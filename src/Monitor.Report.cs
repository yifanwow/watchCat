// 问题报告：用户发现某个程序疑似泄漏、又不知道怎么处理时，生成一份报告发给开发者，开发者据此写公共规则。
// 报告是本地的文本文件（data\reports\），由用户自己决定发不发：点"去 GitHub 提交"会打开新问题页面，把文件拖进去即可。
// 不联网、不自动上传；用户名、电脑名、个人目录都换成占位符。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace WatchDog
{
    partial class Monitor
    {
        void MakeReport(string name)
        {
            if (name == null) { Say("先在列表里选一个程序。"); return; }
            string file;
            try { file = WriteReport(name); }
            catch (Exception ex) { Say("生成报告失败：" + ex.Message); return; }
            StartShell("explorer.exe", "/select,\"" + file + "\"");
            bool go = CyberDialog("问题报告已生成",
                "已经把 " + name + " 的情况整理成一份报告：\n" + Path.GetFileName(file) + "\n（在 data\\reports 文件夹里，刚才已经打开）\n\n" +
                "里面有：程序信息、启动方式、同一家厂商的服务 / 程序、最近 24 小时和 7 天的内存走势、稳定性判断、重启过的结果" +
                (incidents.ContainsKey(name) ? "，以及之前做过的内存分析报告" : "") + "。用户名、电脑名、个人目录已经换成占位符。\n\n" +
                "发给开发者：点\"去 GitHub 提交\"，会打开新问题页面，把这个文件拖进去、写一句你遇到的情况即可（需要 GitHub 账号）。开发者会据此为这个程序写一条公共规则，之后所有人都会自动用上。\n\n" +
                "发送前可以先打开看一下内容。",
                true, "去 GitHub 提交", "先打开看看", null, false);
            if (go) StartShell("https://github.com/yifanwow/watchDog/issues/new", "");
            else StartShell("notepad.exe", "\"" + file + "\"");
        }

        string WriteReport(string name)
        {
            ProcRow row; rows.TryGetValue(name, out row);
            string svc = name.StartsWith("svc:") ? name.Substring(4) : (row != null && row.Count == 1 ? Native.ServiceOfPid(row.Pid) : null);
            LaunchInfo li = name.StartsWith("svc:") ? null : LaunchOf(name);
            string path = li != null ? li.Path : svc != null ? ServiceExe(svc) : PathOf(name);
            StringBuilder o = new StringBuilder();
            Action<string> L = s => o.Append(s).Append("\r\n");

            L("# WatchDog 问题报告：" + name);
            L("");
            L("生成时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm"));
            L("WatchDog：" + File.GetLastWriteTime(Application_Path()).ToString("yyyy-MM-dd") + " 编译");
            L("系统：" + osName + " · 内存 " + string.Format("{0:N0} GB", Native.Mem().ullTotalPhys / 1073741824.0) + " · CPU " + cpuName + " · 显卡 " + gpuName);
            L("");
            L("## 程序");
            L("- 名字：" + name + (DescOf(name) != null ? "（" + DescOf(name) + "）" : ""));
            L("- 路径：" + (path ?? "（拿不到）"));
            if (path != null && File.Exists(path))
            {
                try
                {
                    FileVersionInfo vi = FileVersionInfo.GetVersionInfo(path);
                    L("- 产品：" + (vi.ProductName ?? "-") + " " + (vi.ProductVersion ?? "") + " · 文件版本 " + (vi.FileVersion ?? "-"));
                    L("- 厂商：" + (vi.CompanyName ?? "-"));
                }
                catch { }
            }
            L("- 启动参数：" + (li != null && !string.IsNullOrEmpty(li.Args) ? li.Args : "（没有）"));
            L("- 由谁启动：" + (li != null && li.Parent != null ? li.Parent + "（" + li.ParentPath + "）" : svc != null ? "Windows 服务" : "-"));
            L("- 服务：" + (svc ?? "（不是服务）"));
            if (row != null)
                L(string.Format("- 现在：内存 {0:N0} MB · {1} 个进程 · CPU {2:N1}% · 读写 {3:N1} MB/s · 句柄 {4:N0} · GDI/USER {5}/{6}", row.PrivMB, row.Count, row.CpuPct, row.IoMBs, row.Handles, row.Gdi, row.User));
            else L("- 现在：没在运行");
            L("");

            L("## WatchDog 的判断");
            Rating rt = GetRating(name, row != null ? row.PrivMB : 0);
            L("- 稳定性：" + rt.Text + " · " + rt.Note);
            if (rt.Trend.Length > 0) L("- 变化：" + rt.Trend);
            if (rt.Forecast.Length > 0) L("- 预测：" + rt.Forecast);
            Issue ri; if (resIssues.TryGetValue(name, out ri)) L("- 句柄 / 图形资源：" + ri.Title + " · " + ri.Sub);
            Hit hit; if (hitByTarget.TryGetValue(name, out hit)) L("- 命中规则：" + hit.Rule.Id + " · " + hit.Why);
            foreach (Rule r in allRules.Where(r => name.Equals(r.Process, StringComparison.OrdinalIgnoreCase) || name.Equals(r.Target, StringComparison.OrdinalIgnoreCase)))
                L("- 规则：" + r.Id + "（" + r.Source + (r.Disabled ? "，已关闭" : "") + "）" + r.Title + " · 修复：" + r.FixType + " " + r.FixLabel);
            string fb = FeedbackOf(name);
            L("- 重启后用户的反馈：" + (fb == "ok" ? "正常" : fb == "bad" ? "不正常（设置 / 状态没恢复）" : "还没重启过"));
            L("");

            L("## 同一个软件 / 同一家厂商正在运行的");
            List<string> rel = RelatedText(name, path, svc);
            if (rel.Count == 0) L("（没有）"); else foreach (string s in rel) L("- " + s);
            L("");

            L("## 内存走势");
            AppendSeries(o, "最近 24 小时（看门狗每 10 分钟记录一次，这里每小时取一个点）", hist, name, 1);
            AppendSeries(o, "最近 7 天（每小时的最低值，这里每 6 小时取一个点）", histLong, name, 6);
            AppendSeries(o, "句柄数，最近 24 小时（每小时一个点）", histH, name, 1);
            L("");

            // 修复记录里和它有关的
            List<string> logLines = new List<string>();
            try
            {
                if (File.Exists(LogFile))
                    foreach (string ln in File.ReadAllLines(LogFile, Encoding.UTF8).Reverse())
                    {
                        if (ln.IndexOf(name.Replace("svc:", ""), StringComparison.OrdinalIgnoreCase) < 0) continue;
                        logLines.Add(ln); if (logLines.Count >= 20) break;
                    }
            }
            catch { }
            if (logLines.Count > 0)
            {
                L("## 看门狗的记录（最近 " + logLines.Count + " 条）");
                foreach (string ln in Enumerable.Reverse(logLines)) L("    " + ln);
                L("");
            }

            string inc;
            if (incidents.TryGetValue(name, out inc) && File.Exists(inc))
            {
                L("## 之前做过的分析 / 现场记录（" + Path.GetFileName(inc) + "）");
                L("（里面有程序内存里重复最多的字符串，发送前请看一下有没有不想公开的内容）");
                L("");
                try
                {
                    string[] lines = File.ReadAllLines(inc, Encoding.UTF8);
                    foreach (string ln in lines.Take(400)) L("    " + ln);
                    if (lines.Length > 400) L("    ……（后面还有 " + (lines.Length - 400) + " 行，略）");
                }
                catch { }
            }

            string text = Scrub(o.ToString());
            string dir = Path.Combine(DataDir, "reports");
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            string safe = new string(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c == ':' ? '-' : c).ToArray());
            string file = Path.Combine(dir, DateTime.Now.ToString("yyyyMMdd-HHmm") + "-" + safe + ".md");
            File.WriteAllText(file, text, new UTF8Encoding(false));
            return file;
        }
        static string Application_Path() { return System.Windows.Forms.Application.ExecutablePath; }

        // 去掉能认出人的东西：个人目录、用户名、电脑名
        static string Scrub(string s)
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (home.Length > 3) s = ReplaceI(s, home, "%USERPROFILE%");
            string user = Environment.UserName, pc = Environment.MachineName;
            if (user.Length >= 3) s = ReplaceI(s, user, "<用户名>");
            if (pc.Length >= 3) s = ReplaceI(s, pc, "<电脑名>");
            return s;
        }
        static string ReplaceI(string s, string a, string b)
        {
            StringBuilder o = new StringBuilder(); int i = 0, j;
            while ((j = s.IndexOf(a, i, StringComparison.OrdinalIgnoreCase)) >= 0) { o.Append(s, i, j - i).Append(b); i = j + a.Length; }
            return o.Append(s, i, s.Length - i).ToString();
        }

        static void AppendSeries(StringBuilder o, string title, Dictionary<string, List<HistPt>> src, string name, int everyHours)
        {
            List<HistPt> l;
            if (!src.TryGetValue(name, out l) || l.Count < 2) return;
            o.Append("\r\n").Append(title).Append("：\r\n");
            DateTime next = DateTime.MinValue;
            foreach (HistPt p in l)
            {
                if (p.T < next) continue;
                next = p.T.AddHours(everyHours);
                o.AppendFormat("    {0:MM-dd HH:mm}  {1,8:N0}\r\n", p.T, p.MB);
            }
            HistPt last = l[l.Count - 1];
            if (last.T >= next.AddHours(-everyHours) && last.T > l[0].T) o.AppendFormat("    {0:MM-dd HH:mm}  {1,8:N0}（最新）\r\n", last.T, last.MB);
        }

        // 同一个软件（同一个安装目录）或同一家厂商正在运行的服务和程序，写进报告
        List<string> RelatedText(string name, string path, string svc)
        {
            List<string> o = new List<string>();
            if (path == null) return o;
            string root = InstallRoot(path), company = CompanyOf(path);
            Func<string, bool> sameApp = p => p != null && (Under(p, root) || (company != null && company == CompanyOf(p)));
            Dictionary<string, int> running = Native.RunningServices();
            HashSet<int> svcPids = new HashSet<int>(running.Values);
            foreach (KeyValuePair<string, int> kv in running.OrderBy(k => k.Key))
            {
                if (kv.Key.Equals(svc, StringComparison.OrdinalIgnoreCase) || running.Values.Count(v => v == kv.Value) > 1) continue;
                string sp = ServiceExe(kv.Key);
                if (sameApp(sp)) o.Add("服务 " + kv.Key + "（" + sp + "）");
            }
            foreach (string pn in rows.Keys.Where(k => !k.StartsWith("svc:") && !k.Equals(name, StringComparison.OrdinalIgnoreCase)).OrderBy(k => k))
            {
                ProcRow pr = rows[pn];
                if (svcPids.Contains(pr.Pid)) continue;
                string pp = PathOf(pn);
                if (!sameApp(pp)) continue;
                LaunchInfo pl = CaptureLaunch(pn);
                o.Add(string.Format("{0} · {1:N0} MB · {2}{3}", pn, pr.PrivMB, pp, pl != null && !string.IsNullOrEmpty(pl.Args) ? " · 参数 " + Clip(pl.Args, 120) : ""));
            }
            return o;
        }
    }
}
