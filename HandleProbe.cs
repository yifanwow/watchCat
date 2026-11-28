// 句柄现场探测器：Capture-Handles.ps1 用 Add-Type 现场编译（C# 5，.NET Framework 自带的编译器）。
// 给一个进程号，列出它手里的句柄：每类多少个；注册表键 / 事件 / 共享内存这些的具体名字（同名的合并计数）；
// 线程句柄指向哪个进程的线程、线程是否已经结束、线程从哪个模块启动；进程句柄指向谁。
// 需要管理员权限才能看系统服务的句柄。只读：只复制句柄来查询，查完立即关闭，不改动目标进程。
// 文件句柄：管道类的不查名字（查名字可能卡住），其余放在后台线程里查，超时就不再查后面的。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

public static class HandleProbe
{
    [DllImport("ntdll.dll")] static extern int NtQuerySystemInformation(int cls, IntPtr buf, int len, out int ret);
    [DllImport("ntdll.dll")] static extern int NtQueryObject(IntPtr h, int cls, IntPtr buf, int len, out int ret);
    [DllImport("ntdll.dll")] static extern int NtQueryInformationThread(IntPtr h, int cls, out IntPtr val, int len, IntPtr ret);
    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll")] static extern bool DuplicateHandle(IntPtr srcProc, IntPtr src, IntPtr dstProc, out IntPtr dst, uint access, bool inherit, uint options);
    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")] static extern int GetProcessIdOfThread(IntPtr h);
    [DllImport("kernel32.dll")] static extern int GetThreadId(IntPtr h);
    [DllImport("kernel32.dll")] static extern IntPtr OpenThread(uint access, bool inherit, int tid);
    [DllImport("kernel32.dll")] static extern bool GetExitCodeThread(IntPtr h, out uint code);
    [DllImport("kernel32.dll")] static extern int GetProcessId(IntPtr h);
    [DllImport("kernel32.dll")] static extern bool GetExitCodeProcess(IntPtr h, out uint code);
    [DllImport("kernel32.dll")] static extern int GetFileType(IntPtr h);

    // 文件句柄的名字：管道（GetFileType=3）不查；其余在后台线程里查，200 毫秒没返回就算卡住，之后的文件句柄都不再查
    static bool fileStuck, stuckNow;
    static string FileName(IntPtr h)
    {
        int ft = GetFileType(h);
        string kind = ft == 1 ? "文件" : ft == 2 ? "设备" : ft == 3 ? "管道" : "其他";
        if (ft == 3) return "[管道]";
        if (fileStuck) return "[" + kind + "] (没查，前面有句柄查名字卡住了)";
        string n = null; IntPtr b = Marshal.AllocHGlobal(4096);
        System.Threading.Thread t = new System.Threading.Thread(() => { try { n = ObjName(h, b); } catch { } });
        t.IsBackground = true; t.Start();
        if (!t.Join(200)) { fileStuck = stuckNow = true; return "[" + kind + "] (查名字卡住)"; }   // 卡住的线程还拿着缓冲区，不释放
        Marshal.FreeHGlobal(b);
        if (n == null) return "[" + kind + "] (读不到名字)";
        // 同一类设备 / 文件夹下的文件合并：去掉末尾的序号和文件名里的数字
        return "[" + kind + "] " + System.Text.RegularExpressions.Regex.Replace(n, @"\d+", "#");
    }

    const uint PROCESS_DUP_HANDLE = 0x40, DUPLICATE_SAME_ACCESS = 2, STILL_ACTIVE = 259;
    // 查名字的句柄类型（这些查名字不会卡住）
    static readonly HashSet<string> Named = new HashSet<string> { "Key", "Section", "Desktop", "WindowStation", "Event", "Mutant", "Semaphore", "Directory", "SymbolicLink", "Timer" };

    static Dictionary<int, string> TypeNames()
    {
        Dictionary<int, string> types = new Dictionary<int, string>();
        IntPtr tb = Marshal.AllocHGlobal(1 << 20);
        try
        {
            int ret;
            if (NtQueryObject(IntPtr.Zero, 3, tb, 1 << 20, out ret) != 0) return types;
            int n = Marshal.ReadInt32(tb); long p = tb.ToInt64() + 8;
            for (int i = 0; i < n; i++)
            {
                ushort len = (ushort)Marshal.ReadInt16((IntPtr)p), max = (ushort)Marshal.ReadInt16((IntPtr)(p + 2));
                types[Marshal.ReadByte((IntPtr)(p + 0x5A))] = Marshal.PtrToStringUni(Marshal.ReadIntPtr((IntPtr)(p + 8)), len / 2);
                p += 0x68 + ((max + 7) & ~7);
            }
        }
        finally { Marshal.FreeHGlobal(tb); }
        return types;
    }

    // 目标进程的全部句柄：[句柄值, 类型序号]
    static List<long[]> HandlesOf(int pid)
    {
        int size = 16 << 20;
        for (int attempt = 0; attempt < 4; attempt++)
        {
            IntPtr b = Marshal.AllocHGlobal(size);
            try
            {
                int ret, st = NtQuerySystemInformation(64, b, size, out ret);
                if (st == unchecked((int)0xC0000004)) { size = ret + (4 << 20); continue; }
                if (st != 0) return null;
                long count = Marshal.ReadInt64(b);
                List<long[]> o = new List<long[]>();
                for (long i = 0; i < count; i++)
                {
                    long e = b.ToInt64() + 16 + i * 40;
                    if (Marshal.ReadInt64((IntPtr)(e + 8)) != pid) continue;
                    o.Add(new long[] { Marshal.ReadInt64((IntPtr)(e + 16)), (ushort)Marshal.ReadInt16((IntPtr)(e + 30)) });
                }
                return o;
            }
            finally { Marshal.FreeHGlobal(b); }
        }
        return null;
    }

    static string ObjName(IntPtr h, IntPtr buf)
    {
        int ret;
        if (NtQueryObject(h, 1, buf, 4096, out ret) != 0) return null;   // ObjectNameInformation
        ushort len = (ushort)Marshal.ReadInt16(buf);
        if (len == 0) return "";
        return Marshal.PtrToStringUni(Marshal.ReadIntPtr(buf + 8), len / 2);
    }

    static void Inc(Dictionary<string, int> d, string k) { int c; d.TryGetValue(k, out c); d[k] = c + 1; }
    static string Top(Dictionary<string, int> d, int n, string indent)
    {
        return string.Join("\r\n", d.OrderByDescending(x => x.Value).Take(n).Select(x => string.Format("{0}{1,8:N0} × {2}", indent, x.Value, x.Key.Length == 0 ? "(没有名字)" : x.Key)));
    }

    // 进程的模块：[基址, 大小, 名字]，用来判断线程从哪个模块启动
    static List<object[]> Modules(int pid, StringBuilder note)
    {
        List<object[]> o = new List<object[]>();
        try
        {
            using (Process p = Process.GetProcessById(pid))
                foreach (ProcessModule m in p.Modules)
                {
                    o.Add(new object[] { m.BaseAddress.ToInt64(), (long)m.ModuleMemorySize, m.ModuleName, m.FileName, m.FileVersionInfo.FileVersion ?? "" });
                    m.Dispose();
                }
        }
        catch (Exception ex) { note.AppendLine("  （读不到模块列表：" + ex.Message + "）"); }
        return o;
    }
    static string ModuleAt(List<object[]> mods, long addr)
    {
        foreach (object[] m in mods) { long b = (long)m[0]; if (addr >= b && addr < b + (long)m[1]) return (string)m[2]; }
        return addr == 0 ? "(未知)" : "(不在模块里)";
    }

    public static string Probe(int pid, int maxQueries)
    {
        StringBuilder o = new StringBuilder(); fileStuck = stuckNow = false;
        try { Process.EnterDebugMode(); } catch { }
        Dictionary<int, string> types = TypeNames();
        List<long[]> hs = HandlesOf(pid);
        if (hs == null) return "读不到全系统的句柄表。\r\n";
        Dictionary<string, int> byType = new Dictionary<string, int>();
        foreach (long[] h in hs) { string t; if (!types.TryGetValue((int)h[1], out t)) t = "#" + h[1]; Inc(byType, t); }
        o.AppendLine(string.Format("--- 句柄类型（共 {0:N0} 个）---", hs.Count));
        o.AppendLine(Top(byType, 12, "  "));

        IntPtr proc = OpenProcess(PROCESS_DUP_HANDLE, false, pid);
        if (proc == IntPtr.Zero) { o.AppendLine("（打不开这个进程来复制句柄，需要管理员权限；只能给出上面的类型统计）"); return o.ToString(); }
        IntPtr buf = Marshal.AllocHGlobal(4096);
        StringBuilder modNote = new StringBuilder();
        List<object[]> mods = Modules(pid, modNote);
        try
        {
            // 每类句柄最多查 maxQueries 个；更多时均匀抽样，计数按比例放大
            foreach (KeyValuePair<string, int> tc in byType.OrderByDescending(x => x.Value))
            {
                string type = tc.Key;
                bool isThread = type == "Thread", isProc = type == "Process", isFile = type == "File";
                if (!Named.Contains(type) && !isThread && !isProc && !isFile) continue;
                List<long[]> of = hs.Where(h => { string t; return types.TryGetValue((int)h[1], out t) && t == type; }).ToList();
                int step = Math.Max(1, (of.Count + maxQueries - 1) / maxQueries);
                Dictionary<string, int> names = new Dictionary<string, int>(), owner = new Dictionary<string, int>(), start = new Dictionary<string, int>();
                int alive = 0, dead = 0, fail = 0, sampled = 0;
                Dictionary<int, string> pname = new Dictionary<int, string>();
                for (int i = 0; i < of.Count; i += step)
                {
                    sampled++;
                    IntPtr d;
                    if (!DuplicateHandle(proc, (IntPtr)of[i][0], GetCurrentProcess(), out d, 0, false, DUPLICATE_SAME_ACCESS)) { fail++; continue; }
                    try
                    {
                        if (isThread)
                        {
                            int tp = GetProcessIdOfThread(d);
                            uint code; if (GetExitCodeThread(d, out code) && code == STILL_ACTIVE) alive++; else dead++;
                            Inc(owner, tp == 0 ? "查不到（线程已结束，所属进程也已退出）" : ProcName(tp, pname) + (tp == pid ? "（它自己的线程）" : ""));
                            if (tp == pid)
                            {
                                // 线程起始地址（ThreadQuerySetWin32StartAddress）。复制来的句柄权限不够时，按线程号重新打开一次（已结束的线程打不开）
                                IntPtr sa; bool got = NtQueryInformationThread(d, 9, out sa, IntPtr.Size, IntPtr.Zero) == 0;
                                if (!got)
                                {
                                    IntPtr t2 = OpenThread(0x0040, false, GetThreadId(d));
                                    if (t2 != IntPtr.Zero) { got = NtQueryInformationThread(t2, 9, out sa, IntPtr.Size, IntPtr.Zero) == 0; CloseHandle(t2); }
                                }
                                if (got) Inc(start, ModuleAt(mods, sa.ToInt64())); else Inc(start, "(读不到，多半是已结束的线程)");
                            }
                        }
                        else if (isProc)
                        {
                            int tp = GetProcessId(d);
                            uint code; bool ended = GetExitCodeProcess(d, out code) && code != STILL_ACTIVE;
                            Inc(owner, ProcName(tp, pname) + (ended ? "（已退出）" : ""));
                        }
                        else if (isFile) Inc(names, FileName(d));
                        else { string n = ObjName(d, buf); Inc(names, n ?? "(读不到名字)"); }
                    }
                    finally { if (stuckNow) stuckNow = false; else CloseHandle(d); }   // 卡住的那个句柄还在被查询，不关
                }
                string scale = step > 1 ? string.Format("（{0:N0} 个里均匀抽了 {1:N0} 个，下面的数已按比例放大）", of.Count, sampled) : "";
                Func<Dictionary<string, int>, Dictionary<string, int>> up = x => x.ToDictionary(k => k.Key, k => k.Value * step);
                if (isThread)
                {
                    o.AppendLine(string.Format("--- 线程句柄 {0:N0} 个 {1}---", of.Count, scale));
                    o.AppendLine(string.Format("  线程已结束：{0:N0}    仍在运行：{1:N0}    读不到：{2:N0}", dead * step, alive * step, fail * step));
                    o.AppendLine("  属于哪个进程："); o.AppendLine(Top(up(owner), 6, "    "));
                    if (start.Count > 0) { o.AppendLine("  线程从哪个模块启动（只看它自己的线程）："); o.AppendLine(Top(up(start), 8, "    ")); }
                }
                else if (isProc)
                {
                    o.AppendLine(string.Format("--- 进程句柄 {0:N0} 个 {1}---", of.Count, scale));
                    o.AppendLine(Top(up(owner), 10, "  "));
                }
                else
                {
                    o.AppendLine(string.Format("--- {0} 句柄 {1:N0} 个，按名字合并 {2}---", type, of.Count, scale));
                    o.AppendLine(Top(up(names), 15, "  "));
                    if (fail > 0) o.AppendLine(string.Format("  （{0:N0} 个复制失败）", fail * step));
                }
            }
        }
        finally { Marshal.FreeHGlobal(buf); CloseHandle(proc); }

        o.AppendLine("--- 加载的第三方模块（不在 Windows 目录里的）---");
        if (modNote.Length > 0) o.Append(modNote.ToString());
        string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        foreach (object[] m in mods.Where(m => !((string)m[3]).StartsWith(win, StringComparison.OrdinalIgnoreCase)).OrderBy(m => (string)m[2]))
            o.AppendLine(string.Format("  {0,-36} {1,-18} {2}", m[2], m[4], m[3]));
        return o.ToString();
    }
    static string ProcName(int pid, Dictionary<int, string> cache)
    {
        string n;
        if (cache.TryGetValue(pid, out n)) return n;
        try { using (Process p = Process.GetProcessById(pid)) n = p.ProcessName + " #" + pid; } catch { n = "进程 #" + pid + "（已退出）"; }
        cache[pid] = n; return n;
    }

    // 内核内存池最大的几个标签（MB）
    public static string PoolTop(int n)
    {
        int len = 1 << 20; IntPtr b = Marshal.AllocHGlobal(len);
        try
        {
            int ret; if (NtQuerySystemInformation(22, b, len, out ret) != 0) return "  （读不到内存池明细）";
            int cnt = Marshal.ReadInt32(b);
            List<object[]> l = new List<object[]>();
            for (int i = 0; i < cnt; i++)
            {
                IntPtr e = b + 8 + i * 40;
                string tag = Encoding.ASCII.GetString(BitConverter.GetBytes(Marshal.ReadInt32(e)));
                l.Add(new object[] { tag, Marshal.ReadInt64(e + 16) / 1048576.0, Marshal.ReadInt64(e + 32) / 1048576.0 });
            }
            return "  分页池最大：" + string.Join("，", l.OrderByDescending(x => (double)x[1]).Take(n).Select(x => string.Format("{0} {1:N0} MB", x[0], x[1])))
                + "\r\n  非分页池最大：" + string.Join("，", l.OrderByDescending(x => (double)x[2]).Take(n).Select(x => string.Format("{0} {1:N0} MB", x[0], x[2])));
        }
        finally { Marshal.FreeHGlobal(b); }
    }
}
