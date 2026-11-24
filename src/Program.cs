// WatchDog 监控窗口：内存 / CPU / 磁盘 监控面板
// 和任务管理器的区别：不只看"现在谁占得多"，而是看"谁在变坏、为什么、怎么修"——
//   1. 稳定性评级：窗口打开 10 分钟后用实时数据给出初步判断；看门猫记录满 1 小时后给出长期判断（含疑似泄漏）
//   2. 问题规则库：rules.json（公共）+ rules.local.json（自己的）写明"症状 → 原因 → 修复方法"，命中就给出修复按钮
// 每 5 秒刷新一次；点关闭缩到托盘，在后台继续记录。
// 实时数据存在固定大小的环形缓存里（每条最多 1 小时 / 720 个点，最多 150 条），不会越用越大。
//
// 自测：WatchDog.exe --selftest --loops N [--report 文件] [--snapshot x.png] [--select 程序名]
//        [--chart mem|cpu|disk|kernel|dog|proc] [--metric mem|cpu|io|hnd|gui|pct|pool|total] [--range 30m|1h|3h|12h|24h]
//        [--sim 分钟] [--rising 程序名（内存、句柄、GDI 一起涨）] [--kleak（模拟内核非分页池上涨）] [--data 目录]
//   在屏幕外显示窗口，连续刷新并完整重绘 N 次，记录自身内存 / GDI / 句柄，看有没有泄漏
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Forms;

// 文件属性 / 任务管理器 / 开始菜单搜索里显示的名字（exe 文件名为了兼容暂时还叫 WatchDog.exe）
[assembly: System.Reflection.AssemblyTitle("WatchCat")]
[assembly: System.Reflection.AssemblyProduct("WatchCat")]
[assembly: System.Reflection.AssemblyDescription("WatchCat 看门猫 · 内存泄漏体检和修复")]
[assembly: System.Reflection.AssemblyCompany("yifanovo")]
[assembly: System.Reflection.AssemblyCopyright("yifanovo.com")]
[assembly: System.Reflection.AssemblyVersion("1.0.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.0.0.0")]

namespace WatchDog
{
    static class Program
    {
        [STAThread]
        static int Main(string[] argv)
        {
            Dictionary<string, string> a = ParseArgs(argv);
            string toolDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
            string dataDir = Get(a, "data", Path.Combine(toolDir, "data"));
            bool selfTest = a.ContainsKey("selftest");
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            if (selfTest) return SelfTest(a, toolDir, dataDir);
            // --exit：让正在运行的 WatchDog 存好数据后退出（重新编译前用）
            if (a.ContainsKey("exit"))
            {
                try { using (EventWaitHandle ev = EventWaitHandle.OpenExisting("Local\\yifanwow.WatchDog.Exit")) ev.Set(); } catch { return 0; }
                for (int i = 0; i < 50; i++)
                {
                    Thread.Sleep(200);
                    try { using (Mutex m = Mutex.OpenExisting("Local\\yifanwow.WatchDog")) { } } catch { return 0; }   // 已经退出
                }
                return 1;
            }

            // 被商店应用（比如 Claude 桌面版）带起来时，会被关在它的"作业"里：那个应用一更新，Windows 会把作业里的进程连带全部结束
            //（WatchDog 曾因此两次被关掉，事件日志里记成"停止响应"）。这时通过资源管理器重新启动自己，脱离那个作业，自己退出
            //（已经有一个在运行时不用：下面会直接把那个叫出来）
            bool another = false;
            try { using (Mutex.OpenExisting("Local\\yifanwow.WatchDog")) another = true; } catch { }
            if (!another && argv.Length == 0 && InsidePackagedAppJob() && RelaunchViaExplorer()) return 0;

            // 只开一个：已经在运行（可能在托盘里）时，把它叫出来，自己退出
            bool own;
            using (Mutex mutex = new Mutex(false, "Local\\yifanwow.WatchDog"))
            {
                try { own = mutex.WaitOne(0); } catch (AbandonedMutexException) { own = true; }   // 上次被强制结束
                if (!own)
                {
                    try
                    {
                        using (EventWaitHandle ev = EventWaitHandle.OpenExisting("Local\\yifanwow.WatchDog.Show"))
                        { Native.AllowSetForegroundWindow(-1); ev.Set(); }   // 让已经开着的那个可以把窗口提到最前
                    }
                    catch { }
                    return 0;
                }
                using (EventWaitHandle show = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\yifanwow.WatchDog.Show"))
                using (EventWaitHandle exit = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\yifanwow.WatchDog.Exit"))
                using (Monitor form = new Monitor(toolDir, dataDir, false, null, Get(a, "chart", "mem"), Get(a, "range", "30m")))
                {
                    form.StartInTray = a.ContainsKey("tray");   // 开机自动启动：只在托盘里
                    form.Prepare(true);
                    form.StartTimer();
                    // 万一还是被 Windows 结束了（程序更新、卡住被关、重启后恢复应用），让 Windows 把它重新启动
                    try { RegisterApplicationRestart("", 0); } catch { }
                    form.ListenForShow(show, exit);
                    Application.Run(form);
                }
                mutex.ReleaseMutex();
            }
            return 0;
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        static extern int RegisterApplicationRestart(string cmdLine, int flags);
        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        static extern bool IsProcessInJob(IntPtr process, IntPtr job, out bool result);

        // 在作业里，而且往上的父进程里有商店应用（C:\Program Files\WindowsApps\…）
        static bool InsidePackagedAppJob()
        {
            try
            {
                bool inJob;
                using (Process me = Process.GetCurrentProcess())
                {
                    if (!IsProcessInJob(me.Handle, IntPtr.Zero, out inJob) || !inJob) return false;
                    Dictionary<int, int> parent = Native.ParentMap();
                    int pid = me.Id;
                    for (int i = 0; i < 12 && parent.TryGetValue(pid, out pid) && pid > 4; i++)
                    {
                        string p = Native.ImagePath(pid);
                        if (p != null && p.IndexOf("\\WindowsApps\\", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                    }
                }
            }
            catch { }
            return false;
        }
        static bool RelaunchViaExplorer()
        {
            // 防止万一脱离不了时反复重启：1 分钟内只试一次
            string mark = Path.Combine(Path.GetTempPath(), "WatchDog.relaunch");
            try { if (File.Exists(mark) && (DateTime.Now - File.GetLastWriteTime(mark)).TotalSeconds < 60) return false; File.WriteAllText(mark, DateTime.Now.ToString("s")); } catch { }
            try
            {
                ProcessStartInfo si = new ProcessStartInfo("explorer.exe", "\"" + Application.ExecutablePath + "\"");
                si.UseShellExecute = false;
                using (Process.Start(si)) { }
                return true;
            }
            catch { return false; }
        }

        // 截图：用 PrintWindow 抓窗口真实的样子（含 Windows 11 的深色标题栏和珊瑚红边框），
        // 再裁掉窗口四周看不见的拖拽边；失败时退回 DrawToBitmap 的结果（标题栏会是老式的浅色）
        internal static void Snapshot(Form form, string file, Bitmap fallback)
        {
            Application.DoEvents(); Thread.Sleep(300); Application.DoEvents();
            Native.RECT wr, fr;
            Native.GetWindowRect(form.Handle, out wr);
            if (Native.DwmGetWindowAttribute(form.Handle, 9, out fr, 16) != 0) fr = wr;   // DWMWA_EXTENDED_FRAME_BOUNDS
            using (Bitmap full = new Bitmap(wr.R - wr.L, wr.B - wr.T))
            {
                bool ok;
                using (Graphics g = Graphics.FromImage(full))
                {
                    IntPtr hdc = g.GetHdc();
                    ok = Native.PrintWindow(form.Handle, hdc, 2);   // PW_RENDERFULLCONTENT
                    g.ReleaseHdc(hdc);
                }
                if (!ok) { if (fallback != null) fallback.Save(file, System.Drawing.Imaging.ImageFormat.Png); return; }
                Rectangle crop = new Rectangle(fr.L - wr.L, fr.T - wr.T, fr.R - fr.L, fr.B - fr.T);
                using (Bitmap c = full.Clone(crop, full.PixelFormat)) c.Save(file, System.Drawing.Imaging.ImageFormat.Png);
            }
        }

        static Dictionary<string, string> ParseArgs(string[] argv)
        {
            Dictionary<string, string> d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < argv.Length; i++)
            {
                if (!argv[i].StartsWith("--")) continue;
                string k = argv[i].Substring(2);
                if (i + 1 < argv.Length && !argv[i + 1].StartsWith("--")) { d[k] = argv[i + 1]; i++; } else d[k] = "";
            }
            return d;
        }
        static string Get(Dictionary<string, string> a, string k, string def) { string v; return a.TryGetValue(k, out v) && v.Length > 0 ? v : def; }

        static int SelfTest(Dictionary<string, string> a, string toolDir, string dataDir)
        {
            int loops = int.Parse(Get(a, "loops", "1"));
            int sim = int.Parse(Get(a, "sim", "0"));
            string report = Get(a, "report", Path.Combine(toolDir, "selftest.txt"));
            string snapshot = Get(a, "snapshot", null);
            StringBuilder outp = new StringBuilder();
            using (Monitor form = new Monitor(toolDir, dataDir, true, Get(a, "select", null), Get(a, "chart", "mem"), Get(a, "range", "30m")))
            {
                form.Prepare(sim == 0); form.SetMetric(Get(a, "metric", "mem"));   // 用模拟数据时不读真实记录，也不存，免得混在一起
                Thread.Sleep(800);
                // 在屏幕外真实显示窗口，让所有控件都按正常流程创建和绘制
                form.StartPosition = FormStartPosition.Manual; form.Location = new Point(-5000, -5000); form.ShowInTaskbar = false;
                form.Show(); Application.DoEvents();
                if (a.ContainsKey("ecoshow")) form.EcoMarkForSnapshot(Get(a, "ecoshow", ""));   // --ecoshow 名字：截图时显示节能叶子
                if (a.ContainsKey("theme")) { form.PreviewTheme(Get(a, "theme", "")); Application.DoEvents(); }   // --theme capricorn：看另一个主题
                if (a.ContainsKey("height")) { form.Height = int.Parse(Get(a, "height", "0")); Application.DoEvents(); }   // --height N：看不同窗口高度下的排版
                if (sim > 0) form.Simulate(sim, Get(a, "rising", ""), a.ContainsKey("kleak"));
                using (Bitmap bmp = new Bitmap(form.Width, form.Height))
                {
                    Rectangle rect = new Rectangle(0, 0, form.Width, form.Height);
                    List<string> mem = new List<string>();
                    for (int i = 1; i <= loops; i++)
                    {
                        form.RefreshAll();
                        Application.DoEvents();        // 处理重绘消息
                        form.DrawToBitmap(bmp, rect);  // 再完整画一遍，检查绘制有没有泄漏
                        if (i == Math.Min(20, loops) || i == loops || i % 150 == 0)
                        {
                            GC.Collect(); GC.WaitForPendingFinalizers();
                            using (Process me = Process.GetCurrentProcess())
                                mem.Add(string.Format("第 {0} 次刷新+重绘后：本程序 {1:N1} MB，托管堆 {2:N1} MB，句柄 {3}，GDI 对象 {4}，USER 对象 {5}，图标缓存 {6}，实时数据 {7} 条",
                                    i, me.PrivateMemorySize64 / 1048576.0, GC.GetTotalMemory(true) / 1048576.0, me.HandleCount,
                                    Native.GetGuiResources(me.Handle, 0), Native.GetGuiResources(me.Handle, 1), form.IconCount, form.SeriesCount));
                        }
                    }
                    if (snapshot != null) Snapshot(form, snapshot, bmp);
                    Monitor.PetNoFill = a.ContainsKey("petraw");   // --petraw：不补边（对比用）
                    if (a.ContainsKey("petsnap")) form.SnapPet(Get(a, "petsnap", "pet.png"), int.Parse(Get(a, "petheight", "480")));   // --petsnap x.png：叠好的看板娘
                    // --window rules|fixes|fixlog|ignored|settings|ask|prompt --winsnap x.png [--pick 规则id]：打开子窗口截图
                    if (a.ContainsKey("window")) form.SnapWindow(Get(a, "window", ""), Get(a, "winsnap", "window.png"), Get(a, "pick", null));
                    form.Report(outp);
                    outp.AppendLine("--- 自身资源 ---");
                    foreach (string s in mem) outp.AppendLine(s);
                }
                if (sim == 0) form.SaveLiveNow();
                form.ForceClose();
            }
            File.WriteAllText(report, outp.ToString(), new UTF8Encoding(true));
            return 0;
        }
    }

    partial class Monitor
    {
        public int IconCount { get { return icons.Count; } }
        public int SeriesCount { get { return series.Count; } }
        public void SaveLiveNow() { SaveLive(); }
        public void SetMetric(string m) { if (m == "pct") memPct = true; else if (m == "pool" || m == "total") kernelMetric = m; else procMetric = m; }
        public void ForceClose() { reallyExit = true; Close(); }

        // 仅自测：预先填入最近 N 分钟的模拟实时数据
        public void Simulate(int minutes, string rising, bool kleak)
        {
            List<ProcRow> rows0 = SampleProcesses().Where(r => r.Name != "Idle").OrderByDescending(r => r.PrivMB).Take(40).ToList();
            int n = Math.Min(RingSize, minutes * 12); DateTime t0 = DateTime.UtcNow.AddMinutes(-minutes);
            Random rnd = new Random(7);
            for (int i = 0; i < n; i++)
            {
                DateTime t = t0.AddSeconds(i * 5); double f = i / (double)Math.Max(1, n - 1);
                foreach (ProcRow r in rows0) { Record(r.Name, r.Name == rising ? r.PrivMB - 250 * (1 - f) : r.PrivMB * (1 + (rnd.NextDouble() - 0.5) * 0.02), t); Record("cpu:" + r.Name, rnd.NextDouble() * 8, t); Record("io:" + r.Name, Math.Max(0, rnd.NextDouble() * 3 - 2), t); }
                Record("sys:mem", 19 + f + rnd.NextDouble() * 0.3, t); Record("sys:cpu", 20 + 15 * Math.Sin(i / 20.0) + rnd.NextDouble() * 8, t);
                Record("sys:dr", Math.Max(0, rnd.NextDouble() * 3 - 1), t); Record("sys:dw", Math.Max(0, rnd.NextDouble() * 6 - 2 + (i % 90 < 5 ? 40 : 0)), t);
                Record("sys:self", 40 + rnd.NextDouble() * 4, t);
                Record("sys:kp", 1500 + rnd.NextDouble() * 20, t); Record("sys:kn", (kleak ? 700 + 400 * f : 900) + rnd.NextDouble() * 10, t); Record("sys:hnd", 200000 + rnd.NextDouble() * 3000, t);
                foreach (ProcRow r in rows0.Where((r, j) => j < 15 || r.Name == rising))
                {
                    bool up = r.Name == rising;
                    double k = up ? 0.3 + 0.7 * f : 1;   // 上涨的程序：从现在值的 30% 一路涨到现在值
                    Record("hnd:" + r.Name, r.Handles * k + rnd.Next(30), t); Record("gdi:" + r.Name, r.Gdi * k, t); Record("usr:" + r.Name, r.User * k, t);
                }
            }
        }

        // 自测报告：卡片、结论、问题、评级
        public void Report(StringBuilder o)
        {
            foreach (BufferedPanel c in cardList) { CardData d = (CardData)c.Tag; o.AppendFormat("{0}: {1} [{2}] {3}\r\n", d.Tag, d.Value, d.Badge, d.Sub); }
            o.AppendFormat("WATCHDOG: {0} [{1}] {2}\r\n", dog.Value, dog.Badge, dog.Sub);
            o.AppendFormat("内核: 分页 {0:N0} MB · 非分页 {1:N0} MB · 句柄 {2:N0} [{3}] {4}\r\n", kPaged, kNonpaged, kHandles, kBadge, kWhy);
            o.AppendFormat("内存池最大: {0}\r\n", string.Join("，", poolBig.Select(t => TagLabel(t) + " " + (int)t.MB + " MB")));
            o.AppendFormat("GDI 最多: {0}\r\n", string.Join("，", rows.Values.Where(r => !r.Name.StartsWith("svc:")).OrderByDescending(r => r.GuiMax).Take(3).Select(r => r.Name + " " + r.Gdi + "/" + r.User)));
            o.AppendFormat("句柄最多: {0}\r\n", string.Join("，", rows.Values.Where(r => !r.Name.StartsWith("svc:")).OrderByDescending(r => r.Handles).Take(3).Select(r => r.Name + " " + r.Handles)));
            o.AppendFormat("内存 TOP: {0}\r\n", string.Join("，", ((CardData)cardMem.Tag).Top.Select(t => t.Key + " " + (int)t.Value)));
            o.AppendFormat("结论: {0} | {1}\r\n", verdict[0], verdict[2]);
            foreach (Issue i in issues) o.AppendFormat("  - [{0}] {1} | {2} | -> {3}\r\n", i.Level, i.Title, i.Sub, i.Target);
            o.AppendLine("评级:");
            foreach (KeyValuePair<string, Rating> kv in ratings)
                if (kv.Value.Text != "数据不足") o.AppendFormat("  {0}: {1}(级别 {2}) | {3} | {4} | {5}\r\n", kv.Key, kv.Value.Text, kv.Value.Level, kv.Value.Trend, kv.Value.Note, kv.Value.Forecast);
            o.AppendLine("主按钮: " + btnPrimary.Text);
            o.AppendFormat("规则 {0} 条，监控 {1} 个，忽略 {2} 项，看门猫任务 {3}，历史记录 {4} 个程序 / 整机 {5} 条\r\n",
                rules.Count, watched.Count, ActiveIgnores().Count, taskInstalled ? "已安装" : "未安装", hist.Count, sys.Count);
        }
    }
}
