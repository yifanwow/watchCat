// 内存泄漏分析器：Analyze-Leak.ps1 用 Add-Type 现场编译（C# 5）。只读，不改动目标进程：
//  1. 哪个线程一直在耗 CPU（观察 10 秒），从哪个模块启动
//  2. 抓最忙线程的调用栈：每 0.1 秒暂停它约 1 毫秒读一次寄存器、沿栈回溯，统计时间花在哪个 DLL 的哪个函数
//  3. 已提交内存按种类分；把可读写的私有内存读一遍，统计重复最多的字符串——漏掉的东西往往是同一份数据存了成千上万遍
// 64 位和 32 位（WOW64）程序都能看；系统服务需要管理员权限。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

public static class LeakProbe
{
    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint a, bool i, int pid);
    [DllImport("kernel32.dll")] static extern IntPtr OpenThread(uint a, bool i, int tid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll")] static extern bool IsWow64Process(IntPtr h, out bool w);
    [DllImport("kernel32.dll")] static extern int SuspendThread(IntPtr h);
    [DllImport("kernel32.dll")] static extern int Wow64SuspendThread(IntPtr h);
    [DllImport("kernel32.dll")] static extern int ResumeThread(IntPtr h);
    [DllImport("kernel32.dll")] static extern bool GetThreadContext(IntPtr h, IntPtr ctx);
    [DllImport("kernel32.dll")] static extern bool Wow64GetThreadContext(IntPtr h, IntPtr ctx);
    [DllImport("kernel32.dll")] static extern bool GetThreadTimes(IntPtr h, out long c, out long e, out long k, out long u);
    [DllImport("kernel32.dll")] static extern bool ReadProcessMemory(IntPtr h, IntPtr addr, byte[] buf, IntPtr size, out IntPtr read);
    [DllImport("kernel32.dll")] static extern IntPtr VirtualQueryEx(IntPtr h, IntPtr addr, out MBI m, IntPtr len);
    [DllImport("ntdll.dll")] static extern int NtQueryInformationThread(IntPtr h, int c, out IntPtr v, int l, IntPtr r);
    [DllImport("psapi.dll")] static extern bool EnumProcessModulesEx(IntPtr h, [Out] IntPtr[] mods, int cb, out int need, uint filter);
    [DllImport("psapi.dll", CharSet = CharSet.Unicode)] static extern int GetModuleFileNameEx(IntPtr h, IntPtr mod, StringBuilder s, int n);
    [DllImport("psapi.dll")] static extern bool GetModuleInformation(IntPtr h, IntPtr mod, out MODINFO mi, int cb);
    [DllImport("dbghelp.dll", SetLastError = true)] static extern bool SymInitializeW(IntPtr h, IntPtr path, bool invade);
    [DllImport("dbghelp.dll")] static extern bool SymCleanup(IntPtr h);
    [DllImport("dbghelp.dll")] static extern uint SymSetOptions(uint o);
    [DllImport("dbghelp.dll", CharSet = CharSet.Unicode)] static extern ulong SymLoadModuleExW(IntPtr h, IntPtr file, string img, string mod, ulong b, uint size, IntPtr data, uint flags);
    [DllImport("dbghelp.dll")] static extern bool StackWalk64(uint machine, IntPtr h, IntPtr t, IntPtr frame, IntPtr ctx, IntPtr read, FTA fta, GMB gmb, IntPtr tr);
    [DllImport("dbghelp.dll")] static extern IntPtr SymFunctionTableAccess64(IntPtr h, ulong a);
    [DllImport("dbghelp.dll")] static extern ulong SymGetModuleBase64(IntPtr h, ulong a);
    [DllImport("dbghelp.dll")] static extern bool SymFromAddr(IntPtr h, ulong a, out ulong d, IntPtr sym);
    delegate IntPtr FTA(IntPtr h, ulong a);
    delegate ulong GMB(IntPtr h, ulong a);

    [StructLayout(LayoutKind.Sequential)] struct MBI { public IntPtr Base, AllocBase; public uint AllocProtect; public IntPtr Size; public uint State, Protect, Type; }
    [StructLayout(LayoutKind.Sequential)] struct MODINFO { public IntPtr Base; public uint Size; public IntPtr Entry; }

    class Mod { public ulong Base, Size; public string Name, Path; }
    static List<Mod> mods;
    static IntPtr hp; static bool wow;
    static readonly FTA fta = (h, a) => SymFunctionTableAccess64(h, a);
    static readonly GMB gmb = (h, a) => SymGetModuleBase64(h, a);
    static StringBuilder o;
    // 自动解读用：最忙线程的 CPU 和栈里出现最多的非系统函数；重复最多的字符串
    static double hotPct; static string hotWhere; static int hotTid;
    static string topStr; static long topCount; static double privMB, ageHours; static long scannedMB; static int scanStep;
    static void L(string s) { o.AppendLine(s); }

    public static string Analyze(int pid, int stackSamples)
    {
        mods = new List<Mod>(); o = new StringBuilder();
        hotPct = 0; hotWhere = null; hotTid = 0; topStr = null; topCount = 0; scannedMB = 0; scanStep = 1;
        try { Process.EnterDebugMode(); } catch { }
        Process p = Process.GetProcessById(pid);
        hp = OpenProcess(0x0410, false, pid);   // QUERY_INFORMATION | VM_READ（只读）
        if (hp == IntPtr.Zero) return "打不开这个进程（系统服务需要管理员权限）。\r\n";
        IsWow64Process(hp, out wow);
        privMB = p.PrivateMemorySize64 / 1048576.0;
        try { ageHours = (DateTime.Now - p.StartTime).TotalHours; } catch { ageHours = 0; }
        L(string.Format("进程  {0} #{1}    {2}    私有内存 {3:N0} MB    线程 {4}    句柄 {5}    启动于 {6:yyyy-MM-dd HH:mm}", p.ProcessName, pid, wow ? "32 位" : "64 位",
            privMB, p.Threads.Count, p.HandleCount, p.StartTime));
        LoadModules();
        SymSetOptions(0x2 | 0x4 | 0x200 | 0x80000);   // UNDNAME | DEFERRED_LOADS | FAIL_CRITICAL_ERRORS | NO_PROMPTS
        SymInitializeW(hp, IntPtr.Zero, false);
        foreach (Mod m in mods) SymLoadModuleExW(hp, IntPtr.Zero, m.Path, null, m.Base, (uint)m.Size, IntPtr.Zero, 0);
        try { HotThreads(p, stackSamples); } catch (Exception ex) { L("抓调用栈失败：" + ex.Message); }
        SymCleanup(hp);
        try { Memory(); } catch (Exception ex) { L("读内存失败：" + ex.Message); }
        L("--- 加载的第三方模块（不在 Windows 目录里的）---");
        string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        foreach (Mod m in mods.Where(m => !m.Path.StartsWith(win, StringComparison.OrdinalIgnoreCase)).OrderBy(m => m.Name))
        {
            string v = ""; try { v = FileVersionInfo.GetVersionInfo(m.Path).FileVersion; } catch { }
            L(string.Format("  {0,-34} {1,-22} {2}", m.Name, v, m.Path));
        }
        CloseHandle(hp);
        return Summary() + "\r\n" + o.ToString();
    }

    // 开头的自动解读：用最直白的话说"它在忙什么、漏的是什么、涨多快"
    static string Summary()
    {
        StringBuilder s = new StringBuilder();
        s.AppendLine("--- 自动解读 ---");
        if (ageHours >= 1) s.AppendLine(string.Format("  · 运行了 {0:N1} 小时，私有内存 {1:N0} MB，平均每小时约 {2:N1} MB（启动时本来就要占一些，所以只是粗略值；准确的上涨速度看 WatchDog 的走势图）", ageHours, privMB, privMB / ageHours));
        if (hotPct >= 3 && hotWhere != null)
            s.AppendLine(string.Format("  · 有一个线程一直在忙（占一个核的 {0:N1}%），时间主要花在：{1}。如果电脑没在用它时也这样，说明它在后台不停地轮询或刷新——一直涨的内存多半就是这个循环每转一圈漏下的", hotPct, hotWhere));
        else s.AppendLine("  · 观察的 10 秒里没有线程明显在忙：它不是靠一个不停转的循环在漏，更可能是偶尔触发的事件（比如设备变化、新进程启动、定时刷新）每次留下一点");
        if (topStr != null && topCount >= 5000)
            s.AppendLine(string.Format("  · 内存里同一段内容重复了 {0:N0} 次{1}：「{2}」。正常程序不会把同一份数据存这么多遍——这就是漏掉的东西：每次生成一份新的，旧的没释放", topCount, scanStep > 1 ? "（只抽查了部分内存，实际更多）" : "", topStr));
        else s.AppendLine("  · 内存里没有成片重复的文字：漏的多半是二进制数据（比如颜色数组、图像缓冲、结构体），从文字上看不出来，要靠上面的调用栈和改设置做对比来判断");
        s.AppendLine("  · 外部程序没法安全地替它释放这些内存（分不清哪些还在用）。根治靠厂商修复，或者关掉 / 换掉触发它的功能；临时办法是让 WatchDog 在它涨过上限时自动重启它");
        return s.ToString();
    }

    static void LoadModules()
    {
        IntPtr[] arr = new IntPtr[2048]; int need;
        if (!EnumProcessModulesEx(hp, arr, arr.Length * IntPtr.Size, out need, 3)) { L("（读不到模块列表）"); return; }
        for (int i = 0; i < need / IntPtr.Size; i++)
        {
            StringBuilder sb = new StringBuilder(520); GetModuleFileNameEx(hp, arr[i], sb, 520);
            MODINFO mi; GetModuleInformation(hp, arr[i], out mi, Marshal.SizeOf(typeof(MODINFO)));
            mods.Add(new Mod { Base = (ulong)mi.Base.ToInt64(), Size = mi.Size, Path = sb.ToString(), Name = Path.GetFileName(sb.ToString()) });
        }
    }
    static string ModOf(ulong addr)
    {
        foreach (Mod m in mods) if (addr >= m.Base && addr < m.Base + m.Size) return m.Name;
        return addr == 0 ? "?" : "0x" + addr.ToString("X");
    }
    static IntPtr symBuf = Marshal.AllocHGlobal(88 + 512);
    static string Sym(ulong addr)
    {
        string mod = ModOf(addr);
        for (int i = 0; i < 88 + 512; i += 4) Marshal.WriteInt32(symBuf, i, 0);
        Marshal.WriteInt32(symBuf, 0, 88); Marshal.WriteInt32(symBuf, 80, 256);   // SizeOfStruct, MaxNameLen
        ulong disp;
        if (SymFromAddr(hp, addr, out disp, symBuf))
        {
            string n = Marshal.PtrToStringAnsi(symBuf + 84);
            if (disp < 0x4000) return mod + "!" + n;   // far from an export = probably an internal function
        }
        return mod;
    }

    static void HotThreads(Process p, int samples)
    {
        Dictionary<int, long> t0 = new Dictionary<int, long>();
        foreach (ProcessThread t in p.Threads) { long c; if (ThreadCpu(t.Id, out c)) t0[t.Id] = c; }
        Thread.Sleep(10000);
        List<KeyValuePair<int, double>> hot = new List<KeyValuePair<int, double>>();
        p.Refresh();
        foreach (ProcessThread t in p.Threads) { long c, b; if (t0.TryGetValue(t.Id, out b) && ThreadCpu(t.Id, out c)) hot.Add(new KeyValuePair<int, double>(t.Id, (c - b) / 1e5 / 10.0)); }   // % of one core
        hot = hot.OrderByDescending(x => x.Value).ToList();
        L(string.Format("--- 10 秒里的 CPU：合计占一个核的 {0:N1}%；最忙的线程 ---", hot.Sum(x => x.Value)));
        foreach (KeyValuePair<int, double> h in hot.Take(6)) L(string.Format("  线程 {0,6}  {1,5:N1}%  从 {2} 启动", h.Key, h.Value, Sym(StartAddr(h.Key))));
        if (hot.Count > 0) { hotPct = hot[0].Value; hotTid = hot[0].Key; }
        foreach (KeyValuePair<int, double> h in hot.Take(3).Where(x => x.Value >= 0.5))
        {
            Dictionary<string, int> incl = new Dictionary<string, int>(), stacks = new Dictionary<string, int>();
            int ok = 0;
            for (int i = 0; i < samples; i++)
            {
                List<ulong> st = Stack(h.Key);
                if (st != null && st.Count > 0)
                {
                    ok++;
                    List<string> names = st.Select(Sym).ToList();
                    foreach (string n in names.Distinct()) { int c; incl.TryGetValue(n, out c); incl[n] = c + 1; }
                    string key = string.Join("  <  ", names.Take(10));
                    int k; stacks.TryGetValue(key, out k); stacks[key] = k + 1;
                }
                Thread.Sleep(100);
            }
            L(string.Format("--- 线程 {0}（{1:N1}%）：抓了 {2} 次调用栈；栈里出现的函数（出现在多少比例的样本里）---", h.Key, h.Value, ok));
            foreach (KeyValuePair<string, int> kv in incl.OrderByDescending(x => x.Value).Take(18)) L(string.Format("  {0,4:N0}%  {1}", 100.0 * kv.Value / Math.Max(1, ok), kv.Key));
            if (h.Key == hotTid)
            {
                // 解读用：去掉系统 DLL 和等待函数，剩下的里出现最多的 3 个
                string[] sysMods = { "ntdll.dll", "KERNEL32.DLL", "KERNELBASE.dll", "USER32.dll", "win32u.dll", "ucrtbase.dll", "OLEAUT32.dll", "combase.dll", "RPCRT4.dll", "msvcrt.dll" };
                hotWhere = string.Join(" → ", incl.Where(kv => !sysMods.Any(m => kv.Key.StartsWith(m, StringComparison.OrdinalIgnoreCase)) && !kv.Key.Contains("QEventLoop") && !kv.Key.Contains("QCoreApplication::exec") && !kv.Key.Contains("QThread"))
                    .OrderByDescending(kv => kv.Value).Take(3).Select(kv => kv.Key));
                if (hotWhere.Length == 0) hotWhere = null;
            }
            L("  最常见的完整调用栈（最里层在前）：");
            foreach (KeyValuePair<string, int> kv in stacks.OrderByDescending(x => x.Value).Take(4)) L(string.Format("  {0,3}x  {1}", kv.Value, kv.Key));
        }
    }
    static bool ThreadCpu(int tid, out long c)
    {
        c = 0; IntPtr t = OpenThread(0x0800, false, tid);   // QUERY_LIMITED
        if (t == IntPtr.Zero) return false;
        long cr, ex, k, u; bool ok = GetThreadTimes(t, out cr, out ex, out k, out u); CloseHandle(t); c = k + u; return ok;
    }
    static ulong StartAddr(int tid)
    {
        IntPtr t = OpenThread(0x0040, false, tid); if (t == IntPtr.Zero) return 0;
        IntPtr sa; bool ok = NtQueryInformationThread(t, 9, out sa, IntPtr.Size, IntPtr.Zero) == 0; CloseHandle(t);
        return ok ? (ulong)sa.ToInt64() : 0;
    }
    static List<ulong> Stack(int tid)
    {
        IntPtr t = OpenThread(0x0002 | 0x0008 | 0x0040, false, tid);   // SUSPEND_RESUME | GET_CONTEXT | QUERY_INFORMATION
        if (t == IntPtr.Zero) return null;
        IntPtr raw = Marshal.AllocHGlobal(1232 + 16), ctx = (IntPtr)((raw.ToInt64() + 15) & ~15L);
        IntPtr frame = Marshal.AllocHGlobal(512);
        List<ulong> r = new List<ulong>();
        try
        {
            for (int i = 0; i < 1232; i += 4) Marshal.WriteInt32(ctx, i, 0);
            for (int i = 0; i < 512; i += 4) Marshal.WriteInt32(frame, i, 0);
            uint machine; ulong ip, sp, bp;
            if (wow ? Wow64SuspendThread(t) < 0 : SuspendThread(t) < 0) return null;
            try
            {
                if (wow)
                {
                    Marshal.WriteInt32(ctx, 0, 0x1000B);   // WOW64_CONTEXT_FULL
                    if (!Wow64GetThreadContext(t, ctx)) return null;
                    machine = 0x014C; ip = (uint)Marshal.ReadInt32(ctx, 0xB8); sp = (uint)Marshal.ReadInt32(ctx, 0xC4); bp = (uint)Marshal.ReadInt32(ctx, 0xB4);
                }
                else
                {
                    Marshal.WriteInt32(ctx, 0x30, 0x10000B);   // CONTEXT_FULL (AMD64)
                    if (!GetThreadContext(t, ctx)) return null;
                    machine = 0x8664; ip = (ulong)Marshal.ReadInt64(ctx, 0xF8); sp = (ulong)Marshal.ReadInt64(ctx, 0x98); bp = (ulong)Marshal.ReadInt64(ctx, 0xA0);
                }
                Marshal.WriteInt64(frame, 0, (long)ip); Marshal.WriteInt32(frame, 12, 3);
                Marshal.WriteInt64(frame, 32, (long)bp); Marshal.WriteInt32(frame, 44, 3);
                Marshal.WriteInt64(frame, 48, (long)sp); Marshal.WriteInt32(frame, 60, 3);
                for (int i = 0; i < 40; i++)
                {
                    if (!StackWalk64(machine, hp, t, frame, ctx, IntPtr.Zero, fta, gmb, IntPtr.Zero)) break;
                    ulong pc = (ulong)Marshal.ReadInt64(frame, 0);
                    if (pc == 0) break;
                    r.Add(pc);
                }
            }
            finally { ResumeThread(t); }
            return r;
        }
        finally { Marshal.FreeHGlobal(raw); Marshal.FreeHGlobal(frame); CloseHandle(t); }
    }

    static void Memory()
    {
        long priv = 0, mapped = 0, image = 0, privRW = 0; int regions = 0;
        Dictionary<long, long> bySize = new Dictionary<long, long>();   // allocation size class -> bytes
        List<KeyValuePair<long, long>> rw = new List<KeyValuePair<long, long>>();
        long addr = 0, max = wow ? 0x100000000L : 0x7FFFFFFF0000L;
        while (addr < max)
        {
            MBI m;
            if (VirtualQueryEx(hp, (IntPtr)addr, out m, (IntPtr)Marshal.SizeOf(typeof(MBI))) == IntPtr.Zero) break;
            long size = m.Size.ToInt64();
            if (m.State == 0x1000)
            {
                regions++;
                if (m.Type == 0x20000) { priv += size; if ((m.Protect & 0x04) != 0 && (m.Protect & 0x100) == 0) { privRW += size; rw.Add(new KeyValuePair<long, long>(m.Base.ToInt64(), size)); } }
                else if (m.Type == 0x40000) mapped += size; else image += size;
            }
            addr = m.Base.ToInt64() + Math.Max(size, 4096);
        }
        L(string.Format("--- 已提交内存：私有 {0:N0} MB（可读写 {1:N0} MB，{2} 块），映射文件 {3:N0} MB，程序和 DLL {4:N0} MB ---",
            priv / 1048576.0, privRW / 1048576.0, rw.Count, mapped / 1048576.0, image / 1048576.0));
        foreach (KeyValuePair<long, long> r in rw.OrderByDescending(x => x.Value).Take(6)) L(string.Format("  最大的块 0x{0:X}  {1:N1} MB", r.Key, r.Value / 1048576.0));

        // strings in private read-write memory; if it is huge, read every n-th MB (the leak dominates anyway)
        long total = rw.Sum(x => x.Value); int step = (int)Math.Max(1, total / (600L << 20));
        Dictionary<string, int> a = new Dictionary<string, int>(), w = new Dictionary<string, int>();
        byte[] buf = new byte[1 << 20]; long chunk = 0, scanned = 0;
        foreach (KeyValuePair<long, long> r in rw)
            for (long off = 0; off < r.Value; off += buf.Length, chunk++)
            {
                if (chunk % step != 0) continue;
                int n = (int)Math.Min(buf.Length, r.Value - off); IntPtr got;
                if (!ReadProcessMemory(hp, (IntPtr)(r.Key + off), buf, (IntPtr)n, out got)) continue;
                n = got.ToInt32(); scanned += n;
                Strings(buf, n, a, w);
                if (a.Count > 3000000) Prune(a); if (w.Count > 3000000) Prune(w);
            }
        scannedMB = (long)(scanned / 1048576.0); scanStep = step;
        KeyValuePair<string, int> ta = a.OrderByDescending(x => x.Value).FirstOrDefault(), tw = w.OrderByDescending(x => x.Value).FirstOrDefault();
        KeyValuePair<string, int> top = ta.Value >= tw.Value ? ta : tw;
        if (top.Key != null) { topStr = Clip(top.Key); topCount = top.Value; }
        L(string.Format("--- 重复最多的字符串（读了 {0:N0} MB{1}）---", scanned / 1048576.0, step > 1 ? "，内存太大，每 " + step + " MB 抽 1 MB，计数没有按比例放大" : ""));
        L("  ASCII（按占的总字节排）：");
        foreach (KeyValuePair<string, int> kv in a.Where(x => x.Value >= 20).OrderByDescending(x => (long)x.Value * x.Key.Length).Take(25)) L(string.Format("  {0,8:N0} ×  {1}", kv.Value, Clip(kv.Key)));
        L("  UTF-16（按占的总字节排）：");
        foreach (KeyValuePair<string, int> kv in w.Where(x => x.Value >= 20).OrderByDescending(x => (long)x.Value * x.Key.Length * 2).Take(25)) L(string.Format("  {0,8:N0} ×  {1}", kv.Value, Clip(kv.Key)));
    }
    static string Clip(string s) { s = s.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t"); return s.Length > 160 ? s.Substring(0, 160) + "..." : s; }
    static void Prune(Dictionary<string, int> d) { foreach (string k in d.Where(x => x.Value < 3).Select(x => x.Key).ToList()) d.Remove(k); }
    static bool Pr(byte b) { return b >= 0x20 && b < 0x7F || b == 9 || b == 10 || b == 13; }
    static void Strings(byte[] b, int n, Dictionary<string, int> a, Dictionary<string, int> w)
    {
        int s = -1;
        for (int i = 0; i <= n; i++)
        {
            bool p = i < n && Pr(b[i]);
            if (p) { if (s < 0) s = i; }
            else if (s >= 0) { int len = i - s; if (len >= 8) Add(a, Encoding.ASCII.GetString(b, s, Math.Min(len, 200))); s = -1; }
        }
        for (int par = 0; par < 2; par++)
        {
            s = -1;
            for (int i = par; i + 1 <= n; i += 2)
            {
                bool p = i + 1 < n && b[i + 1] == 0 && Pr(b[i]);
                if (p) { if (s < 0) s = i; }
                else if (s >= 0) { int len = (i - s) / 2; if (len >= 6) Add(w, Encoding.Unicode.GetString(b, s, Math.Min(len, 200) * 2)); s = -1; }
            }
        }
    }
    static void Add(Dictionary<string, int> d, string k) { int c; d.TryGetValue(k, out c); d[k] = c + 1; }
}
