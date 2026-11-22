// 让没有专项规则的程序也能被"聪明地"处理，并且越用越好：
//   1. 浏览器：内存大、波动大通常是正常的，判断时放宽，并写明原因（避免电脑小白以为浏览器在漏内存）
//   2. 通用重启：平时记下每个程序是怎么启动的（路径 + 参数 + 谁带起来的）；重启时先试最小的范围
//      （只结束出问题的进程，看它的主程序会不会自己拉起来），没被拉起来再按原来的参数重新打开，最后核对它回来了没有
//   3. 第一次重启某个程序后问一句"现在正常吗"：不正常的以后不自动重启，并引导去建一条专门的规则
//   4. 规则向导：列出它的服务、同一个软件的其他程序 / 服务，选择"只重启它 / 重启服务 / 按顺序重启一组"，先试一次再保存
//   5. 公共规则每天从 GitHub 更新一次（只下载规则文件，不上传任何东西）；用户自己的规则可以复制出来分享
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace WatchDog
{
    partial class Monitor
    {
        // ================= 1. 浏览器 =================
        static readonly HashSet<string> Browsers = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            "chrome", "msedge", "firefox", "vivaldi", "brave", "opera", "iexplore", "waterfox", "librewolf", "floorp", "zen", "arc",
            "thorium", "yandex", "browser", "360se", "360chrome", "360ChromeX", "QQBrowser", "SogouExplorer", "2345Explorer", "UCBrowser",
            "Maxthon", "CentBrowser", "msedgewebview2" };
        static bool IsBrowser(string name) { return name != null && Browsers.Contains(name); }
        const string BrowserWhy = "浏览器的内存跟着打开的网页和标签页涨落，内存大、忽高忽低通常是正常的；关掉不用的标签页就会降下来";

        // 浏览器的评级：涨一点、波动大都不算问题；只有长时间涨很多才提醒（也只是"留意"，而且先建议关标签页）
        static Rating BrowserRating(Rating rt)
        {
            switch (rt.Text)
            {
                case "疑似泄漏":
                case "持续泄漏":
                    return new Rating("涨得多", 1, Math.Max(0, rt.Score - 30), rt.Note + "。" + BrowserWhy + "，关了还降不下来再重启浏览器",
                        "先关掉不用的标签页；降不下来再重启浏览器", rt.Trend);
                case "缓慢上涨":
                case "短时上涨":
                    return new Rating("随网页涨落", -1, 10, rt.Note + "。" + BrowserWhy, "", rt.Trend);
                case "波动大":
                    return new Rating("波动大", -1, 10, BrowserWhy, "", rt.Trend);
            }
            return rt;
        }

        // ================= 2. 记下程序是怎么启动的 =================
        class LaunchInfo { public string Path, Args, Parent, ParentPath; public DateTime T; }
        Dictionary<string, LaunchInfo> launch;
        string LaunchFile { get { return Path.Combine(DataDir, "launch.json"); } }
        bool launchDirty;

        void LoadLaunch()
        {
            if (launch != null) return;
            launch = new Dictionary<string, LaunchInfo>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!File.Exists(LaunchFile)) return;
                foreach (Dictionary<string, object> d in Json.Objects(Json.Parse(Json.ReadFile(LaunchFile))))
                {
                    LaunchInfo li = new LaunchInfo { Path = Json.Str(d, "path"), Args = Json.Str(d, "args") ?? "", Parent = Json.Str(d, "parent"), ParentPath = Json.Str(d, "parentPath") };
                    DateTime t; DateTime.TryParse(Json.Str(d, "t") ?? "", out t); li.T = t;
                    string n = Json.Str(d, "name"); if (n != null && li.Path != null) launch[n] = li;
                }
            }
            catch { }
        }
        void SaveLaunch()
        {
            if (!launchDirty || launch == null || selfTest) return;
            launchDirty = false;
            try
            {
                List<Dictionary<string, object>> o = new List<Dictionary<string, object>>();
                foreach (KeyValuePair<string, LaunchInfo> kv in launch.OrderByDescending(k => k.Value.T).Take(300))
                {
                    Dictionary<string, object> d = new Dictionary<string, object>();
                    d["name"] = kv.Key; d["path"] = kv.Value.Path; d["args"] = kv.Value.Args; d["parent"] = kv.Value.Parent; d["parentPath"] = kv.Value.ParentPath; d["t"] = kv.Value.T.ToString("s");
                    o.Add(d);
                }
                if (!Directory.Exists(DataDir)) Directory.CreateDirectory(DataDir);
                Json.WriteFile(LaunchFile, o);
            }
            catch { }
        }

        // 记下一个程序的启动方式：取同名进程里"最上面"的那个（父进程不是同名的），读它的命令行和父进程
        LaunchInfo CaptureLaunch(string name)
        {
            LoadLaunch();
            if (name == null || name.StartsWith("svc:")) return null;
            Process[] ps = ByName(name);
            try
            {
                if (ps.Length == 0) { LaunchInfo old; return launch.TryGetValue(name, out old) ? old : null; }
                Dictionary<int, int> parent = Native.ParentMap();
                HashSet<int> same = new HashSet<int>(ps.Select(p => p.Id));
                Process root = ps.FirstOrDefault(p => { int pp; return !parent.TryGetValue(p.Id, out pp) || !same.Contains(pp); }) ?? ps[0];
                string path = Native.ImagePath(root.Id);
                if (path == null) return null;
                LaunchInfo li = new LaunchInfo { Path = path, Args = ArgsOf(Native.CommandLine(root.Id), path), T = DateTime.Now };
                int ppid;
                if (parent.TryGetValue(root.Id, out ppid) && ppid > 4)
                {
                    li.ParentPath = Native.ImagePath(ppid);
                    if (li.ParentPath != null) li.Parent = Path.GetFileNameWithoutExtension(li.ParentPath);
                }
                launch[name] = li; launchDirty = true;
                return li;
            }
            finally { DisposeAll(ps); }
        }
        LaunchInfo LaunchOf(string name)
        {
            LoadLaunch();
            LaunchInfo li;
            if (launch.TryGetValue(name, out li) && (DateTime.Now - li.T).TotalHours < 6) return li;
            return CaptureLaunch(name) ?? li;
        }
        // 平时每分钟顺手记 2 个：重点监控的、有问题的、内存最多的程序（很轻：读一下命令行）
        void CaptureSomeLaunches()
        {
            LoadLaunch();
            IEnumerable<string> want = lvG.Items.Cast<ListViewItem>().Select(i => i.Text)
                .Concat(lv.Items.Cast<ListViewItem>().Take(12).Select(i => i.Text));
            int n = 0;
            foreach (string name in want.Distinct())
            {
                if (n >= 2) break;
                if (name.StartsWith("svc:") || Protected.Contains(name)) continue;
                LaunchInfo li;
                if (launch.TryGetValue(name, out li) && (DateTime.Now - li.T).TotalHours < 6) continue;
                CaptureLaunch(name); n++;
            }
            SaveLaunch();
        }
        // 命令行去掉程序路径，剩下的就是启动参数
        static string ArgsOf(string cmd, string path)
        {
            if (string.IsNullOrEmpty(cmd)) return "";
            cmd = cmd.Trim();
            string rest;
            if (cmd.StartsWith("\"")) { int e = cmd.IndexOf('"', 1); rest = e > 0 ? cmd.Substring(e + 1) : ""; }
            else
            {
                int e = cmd.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
                rest = e > 0 ? cmd.Substring(e + 4) : (cmd.IndexOf(' ') > 0 ? cmd.Substring(cmd.IndexOf(' ')) : "");
            }
            return rest.Trim();
        }
        // 软件的安装目录（同一个软件的程序都在这下面）：Program Files\厂商、AppData\Local\厂商……；其他情况用程序所在的文件夹
        static string InstallRoot(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            string[] p = path.Split('\\');
            for (int i = 0; i < p.Length - 2; i++)
            {
                string s = p[i];
                if (s.Equals("Program Files", StringComparison.OrdinalIgnoreCase) || s.Equals("Program Files (x86)", StringComparison.OrdinalIgnoreCase)
                    || s.Equals("ProgramData", StringComparison.OrdinalIgnoreCase) || s.Equals("Local", StringComparison.OrdinalIgnoreCase)
                    || s.Equals("Roaming", StringComparison.OrdinalIgnoreCase) || s.Equals("Programs", StringComparison.OrdinalIgnoreCase))
                {
                    if (s.Equals("Local", StringComparison.OrdinalIgnoreCase) && i + 2 < p.Length - 1 && p[i + 1].Equals("Programs", StringComparison.OrdinalIgnoreCase)) continue;
                    return string.Join("\\", p, 0, i + 2) + "\\";
                }
            }
            return Path.GetDirectoryName(path) + "\\";
        }
        static bool Under(string path, string root) { return path != null && root != null && path.StartsWith(root, StringComparison.OrdinalIgnoreCase); }

        // ================= 3. 通用重启 + 核对 + 问用户 =================
        class PendingFix
        {
            public string Name, Path, Args, How, LogId;
            public bool Child, Launched, AskUser;
            public double Before;
            public DateTime T0, TLaunch;
            public string[] MustRun = new string[0], Services = new string[0];
            public int WaitSec = 10, MaxSec = 45;
            public Action<bool, string> Done;   // 有它就交给它（规则向导"试一次"），没有就记日志 + 问用户
        }
        List<PendingFix> pending = new List<PendingFix>();
        System.Windows.Forms.Timer pendTimer;

        void StartPending(PendingFix p)
        {
            pending.Add(p);
            if (pendTimer == null) { pendTimer = new System.Windows.Forms.Timer(); pendTimer.Interval = 1000; pendTimer.Tick += delegate { PendTick(); }; }
            pendTimer.Start();
        }

        // 没有专项规则的程序：先试最小的范围
        bool SmartRestart(string name, bool noAsk)
        {
            Process[] procs = ByName(name);
            try
            {
                if (procs.Length == 0) { Say(name + " 已经不在运行了。"); return false; }
                int me = Process.GetCurrentProcess().Id;
                if (procs.Any(p => p.Id == me)) { Say("这是监控窗口自己。"); return false; }
                LaunchInfo li = LaunchOf(name);
                string path = li != null ? li.Path : PathOf(name);
                if (path == null) { Say("拿不到 " + name + " 的程序路径（可能是管理员或系统进程），无法自动重新打开。"); return false; }
                string args = li != null ? li.Args ?? "" : "";
                // 它是同一个软件的主程序带起来的（比如 Steam 带起 steamwebhelper）：只结束它，主程序通常会自己把它拉起来
                bool child = li != null && li.ParentPath != null && Under(li.ParentPath, InstallRoot(path)) && !li.Parent.Equals(name, StringComparison.OrdinalIgnoreCase);
                bool browser = IsBrowser(name);
                if (!noAsk)
                {
                    string plan = child
                        ? string.Format("它是 {0} 带起来的：只结束 {1}（{2} 个进程），{0} 通常会自己把它重新拉起来；没被拉起来的话按原来的方式重新打开。", li.Parent, name, procs.Length)
                        : string.Format("将结束 {0}（{1} 个进程），再按原来的方式重新打开：\n{2}{3}", name, procs.Length, path, args.Length > 0 ? "\n启动参数：" + Clip(args, 160) : "");
                    string warn = browser ? "\n\n浏览器重启后标签页一般能恢复，但没保存的表单、正在下载的文件会丢失。内存大通常只是开的网页多，可以先关掉不用的标签页。"
                        : "\n\n这个程序里没保存的内容可能会丢失。";
                    if (!Ask(plan + warn + "\n\n确定吗？")) return false;
                }
                double before = MBOf(procs);
                int failed = 0;
                foreach (Process p in procs) try { p.Kill(); } catch { failed++; }
                if (failed > 0) { Say(name + " 有 " + failed + " 个进程结束失败（可能需要管理员权限）。"); return false; }
                AppendLog(string.Format("FIX restart-{0}: 手动重启 {0} ({1:N0} MB) -> {2}", name, before, child ? "只结束它，等 " + li.Parent + " 把它拉起来" : "结束后按原来的方式重新打开"));
                StartPending(new PendingFix { Name = name, Path = path, Args = args, Child = child, Before = before, T0 = DateTime.Now, MustRun = new[] { name },
                    LogId = "restart-" + name, AskUser = !noAsk && FeedbackOf(name) == null, How = child ? "被 " + li.Parent + " 重新拉起" : "按原来的方式重新打开" });
                ReloadSoon(8);
                return true;
            }
            finally { DisposeAll(procs); }
        }
        static double MBOf(Process[] ps) { double s = 0; foreach (Process p in ps) try { s += p.PrivateMemorySize64 / 1048576.0; } catch { } return s; }
        static string Clip(string s, int n) { return s.Length > n ? s.Substring(0, n) + "…" : s; }

        void PendTick()
        {
            DateTime now = DateTime.Now;
            foreach (PendingFix p in pending.ToList())
            {
                double sec = (now - p.T0).TotalSeconds;
                // 第一步（只对通用重启）：等它自己回来；子进程最多等 8 秒，其他等 2 秒，没回来就按原来的方式打开
                if (p.Path != null && !p.Launched)
                {
                    Process[] again = ByName(p.Name); bool back = again.Length > 0; DisposeAll(again);
                    if (back) { if (!p.Child) p.How = "自己回来了（被它的主程序或服务重新启动）"; p.Launched = true; p.TLaunch = now; }
                    else if (sec >= (p.Child ? 8 : 2))
                    {
                        try
                        {
                            ProcessStartInfo si = new ProcessStartInfo(p.Path, p.Args ?? "") { WorkingDirectory = Path.GetDirectoryName(p.Path), UseShellExecute = true };
                            Process.Start(si).Dispose();
                        }
                        catch { }
                        if (p.Child) p.How = "没被拉起来，按原来的方式重新打开";
                        p.Launched = true; p.TLaunch = now;
                    }
                    continue;
                }
                if (!p.Launched) { p.Launched = true; p.TLaunch = p.T0; }
                // 第二步：核对它（和要一起回来的）都在运行
                double since = (now - p.TLaunch).TotalSeconds;
                if (since < p.WaitSec) continue;
                List<string> miss = p.MustRun.Where(n => { Process[] a = ByName(n); bool ok = a.Length > 0; DisposeAll(a); return !ok; }).ToList();
                miss.AddRange(p.Services.Where(s => Native.ServicePid(s) <= 0).Select(s => "服务 " + s));
                if (miss.Count > 0 && since < p.MaxSec) continue;
                pending.Remove(p);
                if (pending.Count == 0) pendTimer.Stop();
                Process[] cur = ByName(p.Name); double after = MBOf(cur); bool running = cur.Length > 0; DisposeAll(cur);
                bool ok2 = miss.Count == 0;
                string check = ok2 ? "checked: all back to normal" : "CHECK FAILED - not running: " + string.Join(", ", miss);
                string text = ok2 ? string.Format("{0} 回来了（{1}），内存 {2:N0} → {3:N0} MB", p.Name, p.How, p.Before, after)
                    : "没回来：" + string.Join("、", miss) + "。可以在开始菜单里手动打开它";
                if (p.LogId != null) AppendLog(string.Format("DONE {0}: {1:N0} MB -> {2} MB, {3}", p.LogId, p.Before, running ? after.ToString("0") : "?", check));
                ReloadSoon(3);
                if (p.Done != null) { p.Done(ok2, text); continue; }
                if (!ok2) { Say(text + "。"); continue; }
                if (!p.AskUser) continue;
                // 第一次重启这个程序：问用户现在正常不正常，记下来
                bool fine = CyberDialog("重启完成", text + "。\n\n现在一切都正常吗？比如设置、登录状态、灯效、窗口都还在。\n（只问这一次，你的回答会记下来：正常的以后可以放心重启；不正常的以后不会自动重启它）",
                    true, "正常", "不正常", null, false);
                SetFeedback(p.Name, fine ? "ok" : "bad");
                if (!fine && Ask("要为 " + p.Name + " 建一条专门的修复规则吗？\n\n比如重启时连带重启它的服务或同一个软件的其他程序、按原来的参数启动，先试一次再保存。\n（不知道怎么选的话点\"取消\"，之后右键它 → 生成问题报告，发给开发者）"))
                    ShowRuleWizard(p.Name);
            }
        }

        void AppendLog(string line)
        {
            if (selfTest) return;
            try { File.AppendAllText(LogFile, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + line + "\r\n", new UTF8Encoding(false)); } catch { }
        }

        // 重启后用户说的"正常 / 不正常"
        Dictionary<string, string> feedback;
        string FeedbackPath { get { return Path.Combine(DataDir, "restart-feedback.json"); } }
        string FeedbackOf(string name)
        {
            if (feedback == null)
            {
                feedback = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    if (File.Exists(FeedbackPath))
                        foreach (Dictionary<string, object> d in Json.Objects(Json.Parse(Json.ReadFile(FeedbackPath))))
                        { string n = Json.Str(d, "name"), v = Json.Str(d, "result"); if (n != null && v != null) feedback[n] = v; }
                }
                catch { }
            }
            string r; return name != null && feedback.TryGetValue(name, out r) ? r : null;
        }
        void SetFeedback(string name, string v)
        {
            FeedbackOf(name); feedback[name] = v;
            try
            {
                if (!Directory.Exists(DataDir)) Directory.CreateDirectory(DataDir);
                Json.WriteFile(FeedbackPath, feedback.Select(kv => { Dictionary<string, object> d = new Dictionary<string, object>(); d["name"] = kv.Key; d["result"] = kv.Value; return d; }).ToList());
            }
            catch { }
        }

        // ================= 按顺序重启一组（restartGroup 规则、向导"试一次"）=================
        // steps：{ "service": 名字 } 或 { "process": 名字, "args": 启动参数, "path": 程序路径 }；有服务时需要管理员权限
        bool RunGroup(List<Dictionary<string, object>> steps, string what, bool noAsk, Action<bool, string> done)
        {
            if (steps.Count == 0) return false;
            bool svc = steps.Any(s => Json.Str(s, "service") != null);
            if (!noAsk && !Ask(what + "\n\n将按顺序重启：" + StepsText(steps) + (svc ? "\n（有系统服务，需要管理员权限，会弹出确认窗口）" : "") + "\n\n相关程序里没保存的内容可能会丢失。确定吗？")) return false;
            string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(Json.Write(steps)));
            if (svc) { if (!RunElevated("Restart-Group.ps1", "-StepsB64 " + b64)) return false; }
            else RunTool("Restart-Group.ps1", "-StepsB64 " + b64);
            string[] procs = steps.Select(s => Json.Str(s, "process")).Where(n => n != null).ToArray();
            string[] svcs = steps.Select(s => Json.Str(s, "service")).Where(n => n != null).ToArray();
            string first = procs.FirstOrDefault() ?? (svcs.Length > 0 ? "svc:" + svcs[0] : "");
            Process[] ps = ByName(first); double before = MBOf(ps); DisposeAll(ps);
            StartPending(new PendingFix { Name = first, Before = before, T0 = DateTime.Now, MustRun = procs, Services = svcs, WaitSec = 12 + 6 * steps.Count, MaxSec = 60 + 10 * steps.Count,
                How = "按顺序重启了 " + steps.Count + " 项", Done = done ?? ((ok, text) => { if (!ok) Say(text + "。"); }) });
            ReloadSoon(10);
            return true;
        }
        static string StepsText(List<Dictionary<string, object>> steps)
        {
            return string.Join(" → ", steps.Select(s => Json.Str(s, "service") != null ? "服务 " + Json.Str(s, "service") : Json.Str(s, "process")));
        }

        // ================= 4. 规则向导 =================
        class WizItem { public string Label, Sub; public Dictionary<string, object> Step; public bool On, Fixed; }

        void ShowRuleWizard(string name)
        {
            if (name == null) { Say("先在列表里选一个程序。"); return; }
            if (Protected.Contains(name)) { Say(name + " 是系统关键进程，不能为它建重启规则。"); return; }
            ProcRow row; rows.TryGetValue(name, out row);
            double cur = row != null ? row.PrivMB : 0;
            // 它是什么：服务 / 普通程序；路径、启动参数、谁带起来的
            string svc = name.StartsWith("svc:") ? name.Substring(4) : (row != null && row.Count == 1 ? Native.ServiceOfPid(row.Pid) : null);
            LaunchInfo li = name.StartsWith("svc:") ? null : LaunchOf(name);
            string path = li != null ? li.Path : svc != null ? ServiceExe(svc) : PathOf(name);
            string args = li != null ? li.Args ?? "" : "";
            string root = InstallRoot(path);
            string self = name.StartsWith("svc:") ? null : name;

            // 同一个软件的其他程序（正在运行、在同一个安装目录下）和服务（程序在同一个安装目录下、正在运行）
            List<WizItem> items = new List<WizItem>();
            Dictionary<string, object> selfStep = new Dictionary<string, object>();
            if (svc != null) selfStep["service"] = svc; else { selfStep["process"] = name; if (args.Length > 0) selfStep["args"] = args; if (path != null) selfStep["path"] = path; }
            items.Add(new WizItem { Label = svc != null ? "服务 " + svc : name, Sub = "它自己（第一个重启）", Step = selfStep, On = true, Fixed = true });
            // 同一个软件：在同一个安装目录下，或者程序文件里写的公司相同（比如华硕的灯效服务和奥创中心装在不同目录）
            string company = CompanyOf(path);
            Func<string, bool> sameApp = p => p != null && (Under(p, root) || (company != null && company == CompanyOf(p)));
            if (path != null)
            {
                Dictionary<string, int> running = Native.RunningServices();
                HashSet<int> svcPids = new HashSet<int>(running.Values);
                foreach (KeyValuePair<string, int> kv in running.OrderBy(k => k.Key))
                {
                    if (kv.Key.Equals(svc, StringComparison.OrdinalIgnoreCase)) continue;
                    if (running.Values.Count(v => v == kv.Value) > 1) continue;   // svchost 里合住的服务
                    string sp = ServiceExe(kv.Key);
                    if (!sameApp(sp)) continue;
                    Dictionary<string, object> st = new Dictionary<string, object>(); st["service"] = kv.Key;
                    items.Add(new WizItem { Label = "服务 " + kv.Key, Sub = Under(sp, root) ? "同一个软件的服务" : "同一家厂商的服务（" + company + "）", Step = st });
                }
                foreach (string pn in rows.Keys.Where(k => !k.StartsWith("svc:") && !k.Equals(name, StringComparison.OrdinalIgnoreCase) && !Protected.Contains(k)).OrderBy(k => k))
                {
                    ProcRow pr = rows[pn];
                    if (svcPids.Contains(pr.Pid)) continue;   // 服务的进程已经按服务列出来了
                    string pp = PathOf(pn);
                    if (!sameApp(pp)) continue;
                    LaunchInfo pl = CaptureLaunch(pn);
                    Dictionary<string, object> st = new Dictionary<string, object>(); st["process"] = pn; st["path"] = pp;
                    if (pl != null && !string.IsNullOrEmpty(pl.Args)) st["args"] = pl.Args;
                    items.Add(new WizItem { Label = pn, Sub = string.Format("{0} · {1:N0} MB", Under(pp, root) ? "同一个软件的程序" : "同一家厂商的程序", pr.PrivMB), Step = st });
                }
            }
            if (items.Count > 8) items = items.Take(8).ToList();
            SaveLaunch();

            const int w = 780;
            int h = 516 + 30 * Math.Max(1, items.Count - 1);
            BufferedPanel body;
            using (Form f = NewWindow("RULE WIZARD", "为 " + name + " 建修复规则", w, h, out body))
            {
                int method = svc != null ? 1 : 0;   // 0 只重启这个程序 / 1 重启服务 / 2 按顺序重启一组
                bool auto = false;
                string status = FeedbackOf(name) == "bad" ? "上次重启它之后你说不正常：可以试试连带重启它的服务或同一个软件的其他程序" : "选好处理方式后，先点\"试一次\"看看效果，正常再保存";
                Color statusColor = cGray;
                int hover = -1;
                const int x0 = 22;
                int yOpt = 200, yItems = yOpt + 3 * 30 + 26, yLimit = yItems + Math.Max(2, items.Count) * 30 + 16;
                List<Rectangle> hits = new List<Rectangle>();   // 0-2 处理方式，3.. 一组里的项，最后一个：自动处理开关
                for (int i = 0; i < 3; i++) hits.Add(new Rectangle(x0, yOpt + i * 30, w - 44, 28));
                for (int i = 0; i < items.Count; i++) hits.Add(new Rectangle(x0 + 24, yItems + i * 30, w - 68, 28));
                Rectangle autoR = new Rectangle(x0 + 300, yLimit, 300, 30);
                hits.Add(autoR);
                Func<int, bool> optOk = i => i == 0 ? svc == null : i == 1 ? svc != null : items.Count > 1;

                TextBox limitBox = new TextBox();
                limitBox.BorderStyle = BorderStyle.FixedSingle; limitBox.BackColor = Color.FromArgb(24, 12, 15); limitBox.ForeColor = cText; limitBox.Font = fCNbig;
                limitBox.Text = ((int)(Math.Ceiling(Math.Max(cur * 1.5, cur + 300) / 100) * 100)).ToString();
                limitBox.SetBounds(x0 + 92, yLimit + 1, 90, 28);
                body.Controls.Add(limitBox);

                body.Paint += (s, e) =>
                {
                    Graphics g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
                    Bitmap ico = GetIcon(name);
                    if (ico != null) { g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.DrawImage(ico, x0, 42, 32, 32); }
                    Txt(g, name, fCNbig, cCyan, x0 + 42, 40, w - 200, 22);
                    string fb = FeedbackOf(name);
                    Txt(g, string.Format("现在 {0:N0} MB · {1}", cur, fb == "ok" ? "上次重启后正常" : fb == "bad" ? "上次重启后不正常" : "还没重启过"), fCN, cGray, x0 + 42, 62, w - 200, 18);
                    // 查到的信息
                    string[,] facts = {
                        { "程序", path ?? "（拿不到路径）" },
                        { "启动参数", args.Length > 0 ? args : "（没有）" },
                        { "由谁启动", li != null && li.Parent != null ? li.Parent + (Under(li.ParentPath, root) ? "（同一个软件，结束它通常会被重新拉起）" : "") : svc != null ? "Windows 服务管理器" : "（启动它的程序已经退出，比如开机时启动的）" },
                        { "厂商", company ?? "-" },
                        { "服务", svc ?? "（不是服务）" } };
                    for (int i = 0; i < 5; i++)
                    {
                        Txt(g, facts[i, 0], fCN, cGray, x0, 88 + i * 18, 80, 18);
                        Txt(g, facts[i, 1], fCN, cText, x0 + 80, 88 + i * 18, w - x0 * 2 - 80, 18, tfL | TextFormatFlags.PathEllipsis);
                    }
                    Txt(g, "// 怎么处理", fSmall, cAccent, x0, yOpt - 18, 200, 16);
                    string[] opts = { "只重启这个程序（按原来的启动参数重新打开）", "重启服务 " + (svc ?? ""), "按顺序重启一组：先它，再勾选的同一个软件的服务 / 程序（比如重启灯效服务后让控制中心重新下发灯效）" };
                    for (int i = 0; i < 3; i++)
                    {
                        Rectangle r = hits[i]; bool ok = optOk(i);
                        if (hover == i && ok) g.FillRectangle(bDeep, r);
                        int cy = r.Y + r.Height / 2;
                        g.DrawEllipse(ok ? pAcc : pAccDim, r.X + 4, cy - 7, 14, 14);
                        if (method == i) g.FillEllipse(bLv[0], r.X + 8, cy - 3, 6, 6);
                        Txt(g, opts[i], fCN, !ok ? Color.FromArgb(100, 72, 72) : method == i ? cText : cGray, r.X + 26, r.Y, r.Width - 30, r.Height, tfL | TextFormatFlags.EndEllipsis);
                    }
                    Txt(g, method == 2 ? "// 一起重启的（从上到下的顺序）" : "// 同一个软件的服务 / 程序（选\"按顺序重启一组\"时可以勾选）", fSmall, cAccent, x0, yItems - 18, w, 16);
                    for (int i = 0; i < items.Count; i++)
                    {
                        Rectangle r = hits[3 + i]; WizItem it = items[i]; bool en = method == 2 && !it.Fixed;
                        if (hover == 3 + i && en) g.FillRectangle(bDeep, r);
                        int cy = r.Y + r.Height / 2;
                        bool on = it.Fixed || (method == 2 && it.On);
                        g.DrawRectangle(en || it.Fixed ? pAcc : pAccDim, r.X + 4, cy - 7, 14, 14);
                        if (on) g.FillRectangle(it.Fixed ? bAcc : bLv[0], r.X + 7, cy - 4, 9, 9);
                        Txt(g, (on && method == 2 ? (items.Take(i + 1).Count(x => x.Fixed || x.On)) + ". " : "") + it.Label, fCNb, on || en ? cText : cGray, r.X + 26, r.Y, 300, r.Height, tfL | TextFormatFlags.EndEllipsis);
                        Txt(g, it.Sub, fCN, cGray, r.X + 330, r.Y, r.Width - 334, r.Height, tfL | TextFormatFlags.EndEllipsis);
                    }
                    if (items.Count == 1) Txt(g, "（没找到同一个软件 / 同一家厂商正在运行的其他服务或程序）", fCN, cGray, x0 + 50, yItems + 30, 600, 28);
                    Txt(g, "超过", fCN, cText, x0, yLimit, 40, 30);
                    Txt(g, "MB 时处理", fCN, cText, x0 + 188, yLimit, 100, 30);
                    // 自动处理开关
                    Rectangle sw = new Rectangle(autoR.X, autoR.Y + 3, 44, 24);
                    using (GraphicsPath p = new GraphicsPath())
                    {
                        int d = sw.Height; p.AddArc(sw.X, sw.Y, d, d, 90, 180); p.AddArc(sw.Right - d, sw.Y, d, d, 270, 180); p.CloseFigure();
                        using (SolidBrush b = new SolidBrush(auto ? cCyan : Color.FromArgb(52, 24, 28))) g.FillPath(b, p);
                        if (!auto) g.DrawPath(pAccDim, p);
                    }
                    using (SolidBrush kb = new SolidBrush(auto ? cBg : cGray)) g.FillEllipse(kb, auto ? sw.Right - sw.Height + 3 : sw.X + 3, sw.Y + 3, sw.Height - 6, sw.Height - 6);
                    Txt(g, auto ? "自动处理（你空闲 10 分钟以上时）" : "只提醒（试过几次都正常再打开自动处理）", fCN, auto ? cCyan : cGray, sw.Right + 10, autoR.Y, w - sw.Right - 30, 30);
                    Txt(g, status, fCN, statusColor, x0, yLimit + 40, w - 44, 36, tfW);
                };
                body.MouseMove += (s, e) =>
                {
                    int hv = hits.FindIndex(r => r.Contains(e.Location));
                    if (hv >= 0 && hv < 3 && !optOk(hv)) hv = -1;
                    if (hv >= 3 && hv < 3 + items.Count && (method != 2 || items[hv - 3].Fixed)) hv = -1;
                    if (hv != hover) { hover = hv; body.Cursor = hv >= 0 ? Cursors.Hand : Cursors.Default; body.Invalidate(); }
                };
                body.MouseClick += (s, e) =>
                {
                    if (hover < 0) return;
                    if (hover < 3) method = hover;
                    else if (hover < 3 + items.Count) items[hover - 3].On = !items[hover - 3].On;
                    else auto = !auto;
                    body.Invalidate();
                };

                Func<List<Dictionary<string, object>>> plan = () =>
                    method == 2 ? items.Where(i => i.Fixed || i.On).Select(i => i.Step).ToList() : new List<Dictionary<string, object>> { selfStep };
                Func<Dictionary<string, object>> buildRule = () =>
                {
                    int limit;
                    if (!int.TryParse(limitBox.Text.Trim(), out limit) || limit <= 0) { status = "上限请填一个正整数（单位 MB）"; statusColor = cYellow; body.Invalidate(); return null; }
                    List<Dictionary<string, object>> steps = plan();
                    Dictionary<string, object> fix = new Dictionary<string, object>();
                    if (method == 0) { fix["type"] = "restartProcess"; fix["label"] = "重启 " + name; if (args.Length > 0) fix["args"] = args; }
                    else if (method == 1) { fix["type"] = "restartService"; fix["service"] = svc; fix["label"] = "重启服务 " + svc; }
                    else { fix["type"] = "restartGroup"; fix["steps"] = steps; fix["label"] = "按顺序重启：" + StepsText(steps); }
                    Dictionary<string, object> when = new Dictionary<string, object>(); when["process"] = name; when["minMB"] = limit;
                    Dictionary<string, object> rule = new Dictionary<string, object>();
                    rule["id"] = "my-" + name.Replace("svc:", "svc-").ToLowerInvariant();
                    rule["title"] = name + " 内存过多（我的规则）";
                    rule["target"] = name; rule["when"] = when;
                    rule["cause"] = "用规则向导建的：内存超过 " + limit + " MB 时" + Json.Str(fix, "label") + "。";
                    rule["fix"] = fix;
                    string[] run = steps.Select(st => Json.Str(st, "process")).Where(n => n != null).ToArray();
                    if (run.Length > 0) { Dictionary<string, object> v = new Dictionary<string, object>(); v["running"] = run.Cast<object>().ToList(); rule["verify"] = v; }
                    rule["idleMin"] = 10; rule["auto"] = auto; rule["source"] = "wizard";
                    return rule;
                };
                Button bTry = null;
                bTry = NewBtn(body, "试一次", x0, h - 52, 110, delegate
                {
                    List<Dictionary<string, object>> steps = plan();
                    status = "正在重启：" + StepsText(steps) + "……完成后会在这里显示结果"; statusColor = cYellow; body.Invalidate();
                    bTry.Enabled = false;
                    bool started = RunGroup(steps, "试一次 " + name + " 的修复方法", false, (ok, text) =>
                    {
                        if (f.IsDisposed) return;
                        bTry.Enabled = true;
                        status = ok ? text + "。看一下设置、灯效、登录状态是不是都正常，正常就点\"保存规则\"" : text;
                        statusColor = ok ? cCyan : cYellow;
                        body.Invalidate();
                    });
                    if (!started) { bTry.Enabled = true; status = "没有执行"; statusColor = cGray; body.Invalidate(); }
                }, false);
                Action<bool> save = share =>
                {
                    Dictionary<string, object> rule = buildRule();
                    if (rule == null) return;
                    try
                    {
                        string id = Json.Str(rule, "id");
                        List<Dictionary<string, object>> list = ReadLocalRules().Where(d => Json.Str(d, "id") != id && Json.Str(d, "id") != "watch-" + name).ToList();
                        list.Add(rule);
                        WriteLocalRules(list);
                    }
                    catch (Exception ex) { status = "保存失败：" + ex.Message; statusColor = cYellow; body.Invalidate(); return; }
                    if (share) ShareRule(rule);
                    f.Close();
                    Say("规则已保存到 rules.local.json：" + name + " 超过 " + Json.Str(Json.Obj(rule, "when"), "minMB") + " MB 时" + (auto ? "在你空闲时自动处理" : "提醒你") + "。\n可以在\"规则设置\"里修改或关掉。" + (share ? "\n\n规则内容已复制，粘贴到打开的 GitHub 页面里就能分享给其他人。" : ""));
                    RefreshAll();
                };
                NewBtn(body, "保存规则", x0 + 120, h - 52, 120, delegate { save(false); }, true);
                NewBtn(body, "保存并分享", x0 + 250, h - 52, 120, delegate { save(true); }, false);
                NewBtn(body, "生成问题报告", w - x0 - 100 - 140, h - 52, 130, delegate { f.Close(); MakeReport(name); }, false);
                NewBtn(body, "取消", w - x0 - 100, h - 52, 100, delegate { f.Close(); }, false);
                f.ShowDialog(this);
            }
        }

        // 程序文件里写的公司（同一家厂商）；微软的不算（Windows 自带的太多，不能当成"同一个软件"）
        static Dictionary<string, string> companyCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        static string CompanyOf(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            string c;
            if (companyCache.TryGetValue(path, out c)) return c;
            try { c = FileVersionInfo.GetVersionInfo(path).CompanyName; } catch { c = null; }
            c = string.IsNullOrWhiteSpace(c) || c.IndexOf("Microsoft", StringComparison.OrdinalIgnoreCase) >= 0 ? null : c.Trim();
            if (companyCache.Count < 2000) companyCache[path] = c;
            return c;
        }
        // 分享：把规则（去掉个人路径）复制到剪贴板，打开 GitHub 的新 issue 页面让用户粘贴（不往网址里放任何内容）
        void ShareRule(Dictionary<string, object> rule)
        {
            string text = Json.Write(rule);
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (home.Length > 3) text = text.Replace(home.Replace("\\", "\\\\"), "%USERPROFILE%");
            try { Clipboard.SetText("分享一条 WatchDog 规则（在我的电脑上试过有效）：\n\n```json\n" + text + "\n```\n"); } catch { }
            StartShell("https://github.com/yifanwow/watchDog/issues/new", "");
        }

        // ================= 5. 公共规则在线更新 =================
        const string RulesUrl = "https://raw.githubusercontent.com/yifanwow/watchDog/main/rules.json";
        string RemoteRulesPath { get { return Path.Combine(DataDir, "rules.remote.json"); } }
        string SettingsPath { get { return Path.Combine(DataDir, "settings.json"); } }
        bool rulesUpdating; string rulesUpdateMsg = "";

        Dictionary<string, object> ReadSettings()
        {
            try { if (File.Exists(SettingsPath)) { Dictionary<string, object> d = Json.Parse(Json.ReadFile(SettingsPath)) as Dictionary<string, object>; if (d != null) return d; } } catch { }
            return new Dictionary<string, object>();
        }
        void WriteSetting(string key, object v)
        {
            Dictionary<string, object> d = ReadSettings(); d[key] = v;
            try { if (!Directory.Exists(DataDir)) Directory.CreateDirectory(DataDir); Json.WriteFile(SettingsPath, d); } catch { }
        }
        bool RulesUpdateOn() { Dictionary<string, object> d = ReadSettings(); return !d.ContainsKey("rulesUpdate") || Json.Bool(d, "rulesUpdate"); }
        DateTime RulesChecked() { DateTime t; return DateTime.TryParse(Json.Str(ReadSettings(), "rulesChecked") ?? "", out t) ? t : DateTime.MinValue; }

        // 每天一次（开着窗口时每分钟看一下到没到时间）；force = 设置里点了"立即更新"
        void MaybeUpdateRules(bool force)
        {
            if (selfTest || rulesUpdating) return;
            if (!force && (!RulesUpdateOn() || (DateTime.Now - RulesChecked()).TotalHours < 24)) return;
            rulesUpdating = true; rulesUpdateMsg = "正在更新……";
            string target = RemoteRulesPath, bundled = Path.Combine(RulesDir, "rules.json");
            ThreadPool.QueueUserWorkItem(delegate
            {
                string msg;
                try
                {
                    ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                    HttpWebRequest req = (HttpWebRequest)WebRequest.Create(RulesUrl);
                    req.Timeout = 15000; req.ReadWriteTimeout = 15000; req.UserAgent = "WatchDog-rules-updater";
                    string text;
                    using (WebResponse resp = req.GetResponse())
                    using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8)) text = sr.ReadToEnd();
                    List<Dictionary<string, object>> list = Json.Objects(Json.Parse(text));
                    if (list.Count == 0 || list.Any(d => string.IsNullOrEmpty(Json.Str(d, "id")))) throw new Exception("规则文件格式不对");
                    int added = 0;
                    HashSet<string> have = new HashSet<string>();
                    try { foreach (Dictionary<string, object> d in Json.Objects(Json.Parse(Json.ReadFile(bundled)))) have.Add(Json.Str(d, "id") ?? ""); } catch { }
                    added = list.Count(d => !have.Contains(Json.Str(d, "id")));
                    string old = File.Exists(target) ? File.ReadAllText(target, Encoding.UTF8) : "";
                    if (old != text) { if (!Directory.Exists(Path.GetDirectoryName(target))) Directory.CreateDirectory(Path.GetDirectoryName(target)); File.WriteAllText(target, text, new UTF8Encoding(false)); }
                    msg = string.Format("{0:MM-dd HH:mm} 已更新：公共规则 {1} 条{2}", DateTime.Now, list.Count, added > 0 ? "（比自带的多 " + added + " 条）" : "");
                }
                catch (Exception ex) { msg = string.Format("{0:MM-dd HH:mm} 更新失败：{1}", DateTime.Now, ex.Message); }
                try
                {
                    BeginInvoke((Action)delegate
                    {
                        rulesUpdating = false; rulesUpdateMsg = msg;
                        WriteSetting("rulesChecked", DateTime.Now.ToString("s")); WriteSetting("rulesMsg", msg);
                        rulesSig = ""; LoadRules();
                    });
                }
                catch { }
            });
        }
        string RulesUpdateText()
        {
            if (rulesUpdateMsg.Length > 0) return rulesUpdateMsg;
            return Json.Str(ReadSettings(), "rulesMsg") ?? "还没更新过";
        }
    }
}
