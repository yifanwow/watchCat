// 启动、每 5 秒刷新、各个操作（修复 / 重启 / 结束 / 监控 / 忽略）
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace WatchDog
{
    partial class Monitor
    {
        readonly bool selfTest;

        public Monitor(string toolDir, string dataDir, bool selfTest, string select, string startChart, string startRange)
        {
            ToolDir = toolDir; DataDir = dataDir; RulesDir = toolDir;
            LogFile = Path.Combine(toolDir, "MemWatchdog.log");
            LiveFile = Path.Combine(dataDir, "live.bin");
            this.selfTest = selfTest;
            selected = string.IsNullOrEmpty(select) ? null : select;
            chartMode = startChart; chartRange = startRange;
            LoadTheme();
            CreateTheme();
            LoadNotes();
            LoadProfile();
            BuildUI();
            if (!selfTest) BuildTray();
            BuildPet();   // 看板娘（自测时不建）
        }

        // 窗口出现前：读回实时数据，取一次基准（CPU / 磁盘 / 读写速度要两次之间的差值）
        public void Prepare(bool loadLive)
        {
            if (loadLive) LoadLive();
            DiskPerf.Open();
            Native.Cpu(); SampleProcesses();
        }

        public void StartTimer()
        {
            timer = new Timer(); timer.Interval = 5000;
            timer.Tick += delegate { RefreshAll(); };
            Shown += delegate { RefreshAll(); timer.Start(); };
            FormClosed += delegate { timer.Stop(); SaveLive(); DiskPerf.Close(); };
        }

        // 再次双击 WatchDog.exe 时，新开的那个会发信号过来：把窗口从托盘里叫出来
        // WatchDog.exe --exit 发来的信号：存好数据后退出（重新编译前用）
        public void ListenForShow(System.Threading.EventWaitHandle ev, System.Threading.EventWaitHandle exit)
        {
            showTimer = new Timer(); showTimer.Interval = 400;
            showTimer.Tick += delegate
            {
                if (exit.WaitOne(0)) { reallyExit = true; Close(); return; }
                if (ev.WaitOne(0)) ShowFromTray();
            };
            showTimer.Start();
        }

        // ---------- 刷新 ----------
        public void RefreshAll()
        {
            // 缩到托盘（窗口隐藏）时照常记录；只有"最小化但没进托盘"的一瞬间跳过
            if (Visible && WindowState == FormWindowState.Minimized) return;
            tick++;
            if (tick % 60 == 0 && !selfTest) SaveLive();   // 每 5 分钟存一次盘，防止意外关机丢数据
            DateTime now = DateTime.Now, nowU = DateTime.UtcNow;
            if (now >= forceSlowAt) { forceSlowAt = DateTime.MaxValue; lastSlow = DateTime.MinValue; histSig = ""; rulesSig = ""; ignoreSig = -1; }
            if ((now - lastSlow).TotalSeconds >= 60) { lastSlow = now; UpdateSlow(); }

            // 内存
            Native.MEMSTAT m = Native.Mem();
            double totalGB = m.ullTotalPhys / 1073741824.0, usedGB = (m.ullTotalPhys - m.ullAvailPhys) / 1073741824.0;
            double pct = 100 * usedGB / totalGB;
            double commitGB = (m.ullTotalPageFile - m.ullAvailPageFile) / 1073741824.0, limitGB = m.ullTotalPageFile / 1073741824.0;
            double worst = Math.Max(pct, 100 * commitGB / limitGB);
            int lvl = worst < 70 ? 0 : worst < 85 ? 1 : 2;
            SetCard(cardMem, string.Format("{0:N1} / {1:N0} GB", usedGB, totalGB), string.Format("{0}  {1:N0}%", new[] { "正常", "偏高", "紧张" }[lvl], pct), lvl,
                string.Format("已提交 {0:N1} / {1:N0} GB（含虚拟内存）", commitGB, limitGB), pct);
            Record("sys:mem", usedGB, nowU);
            string memExtra = string.Format("可用 {0:N1} GB", m.ullAvailPhys / 1073741824.0);
            if (tray != null) tray.Text = string.Format("WatchCat · 内存 {0:N1} / {1:N0} GB ({2:N0}%)", usedGB, totalGB, pct);

            // 进程采样 + 规则
            double cpuNow = Native.Cpu();
            cpuHist.Enqueue(cpuNow);
            while (cpuHist.Count > 6) cpuHist.Dequeue();
            double cpuAvg = cpuHist.Average();
            Record("sys:cpu", cpuNow, nowU);
            List<ProcRow> all = SampleProcesses().Where(r => r.Name != "Idle" && r.Name != "Memory Compression").ToList();
            Dictionary<string, ProcRow> byName = new Dictionary<string, ProcRow>();
            foreach (ProcRow r in all) byName[r.Name] = r;
            rows = byName;
            List<ProcRow> real = all.Where(r => !r.Name.StartsWith("svc:")).ToList();
            // 实时数据：内存前 40 + 规则 / 监控涉及的 + 选中的
            Dictionary<string, ProcRow> rec = new Dictionary<string, ProcRow>();
            foreach (ProcRow r in real.OrderByDescending(r => r.PrivMB).Take(40)) rec[r.Name] = r;
            foreach (Rule r in rules) if (r.Process != null && byName.ContainsKey(r.Process)) rec[r.Process] = byName[r.Process];
            if (selected != null && byName.ContainsKey(selected)) rec[selected] = byName[selected];
            // 每个程序记三条：内存（键就是程序名）、CPU（cpu:程序名）、读写（io:程序名）
            foreach (ProcRow r in rec.Values) { Record(r.Name, r.PrivMB, nowU); Record("cpu:" + r.Name, r.CpuPct, nowU); Record("io:" + r.Name, r.IoMBs, nowU); }
            RecordResources(real, nowU);
            EvalRules();
            SetMemExtra(memExtra, real.OrderByDescending(r => r.PrivMB).Take(3).Select(r => new KeyValuePair<string, double>(r.Name, r.PrivMB)).ToList());
            Dictionary<string, Rating> rts = new Dictionary<string, Rating>();
            foreach (ProcRow r in all) rts[r.Name] = GetRating(r.Name, r.PrivMB);
            ratings = rts;

            // CPU（最近 30 秒平均）
            lvl = cpuAvg < 50 ? 0 : cpuAvg < 85 ? 1 : 2;
            SetCard(cardCpu, string.Format("{0:N0} %", cpuAvg), new[] { "正常", "较忙", "很忙" }[lvl], lvl,
                "最忙：" + string.Join("，", real.OrderByDescending(r => r.CpuPct).Take(2).Select(r => string.Format("{0} {1:N1}%", r.Name, r.CpuPct))), cpuAvg);

            // 磁盘
            disk = DiskPerf.Read();
            double rd = disk[0] / 1048576, wr = disk[1] / 1048576, busyPct = disk[2];
            Record("sys:dr", rd, nowU); Record("sys:dw", wr, nowU);
            ProcRow topIo = real.OrderByDescending(r => r.IoMBs).FirstOrDefault();
            lvl = busyPct < 60 ? 0 : busyPct < 90 ? 1 : 2;
            SetCard(cardDisk, string.Format("R {0:N1}  W {1:N1} MB/s", rd, wr), string.Format("{0}  {1:N0}%", new[] { "正常", "较忙", "很忙" }[lvl], busyPct), lvl,
                topIo != null && topIo.IoMBs >= 0.1 ? string.Format("读写最多：{0} {1:N1} MB/s", topIo.Name, topIo.IoMBs) : "没有程序在大量读写", busyPct);

            // 内核内存池 + 句柄 / 图形资源
            SampleKernel(nowU);
            EvalKernel();
            EvalResources(real);
            lvl = kLevel;
            SetCard(cardKernel, string.Format("{0:N1} GB", (kPaged + kNonpaged) / 1024), kBadge, lvl,
                string.Format("分页 {0:N1} · 非分页 {1:N1} GB", kPaged / 1024, kNonpaged / 1024), -1);
            SetCardExtra(cardKernel, string.Format("句柄 {0:N1} 万", kHandles / 10000));

            // 本窗口自己的占用
            using (Process me = Process.GetCurrentProcess())
            {
                selfMB = me.PrivateMemorySize64 / 1048576.0;
                selfGdi = Native.GetGuiResources(me.Handle, 0);
                double c = me.TotalProcessorTime.TotalMilliseconds;
                if (selfPrevCpuMs >= 0) selfCpu = 100 * (c - selfPrevCpuMs) / Math.Max(1, (nowU - selfPrevT).TotalMilliseconds) / Environment.ProcessorCount;
                selfPrevCpuMs = c; selfPrevT = nowU;
            }
            Record("sys:self", selfMB, nowU);

            // 看门猫 + 自身占用
            SysRow last = sys.Count > 0 ? sys[sys.Count - 1] : null;
            if (!taskInstalled) SetDog("未安装", "离线", 1, "看门猫还没安装：点右下角\"设置\"里的后台看门猫开关", -1);
            else if (last == null) SetDog("等待首次检查", "待命", 1, "安装好了，等第一次检查（最多 10 分钟）", -1);
            else
            {
                double mins = (now - last.T).TotalMinutes; bool ok = mins <= 25;
                string sub = last.HasSelf ? string.Format("每次跑 {0:N1} 秒 · CPU {1:N1} 秒 · 峰值 {2:N0} MB", last.RunMs / 1000, last.SelfCpuSec, last.SelfPeakMB)
                    : string.Format("每次跑 {0:N1} 秒", last.RunMs / 1000);
                SetDog("上次 " + Ago(last.T), ok ? "运行中" : "可能停了", ok ? 0 : 2, sub, Math.Min(100, mins * 10));
            }

            BuildIssues(all, real);

            // 程序列表：内存前 25 + 有问题 / 需要留意 / 大量读写 / 被监控的；按当前排序方式排
            List<ProcRow> list = real.OrderByDescending(r => r.PrivMB).Take(25).ToList();
            HashSet<string> inList = new HashSet<string>(list.Select(r => r.Name));
            foreach (ProcRow r in all)
                if (!inList.Contains(r.Name) && (ratings[r.Name].Level >= 1 || ResLevel(r.Name) >= 1 || r.IoMBs >= HeavyIoMBs || watched.ContainsKey(r.Name))) { list.Add(r); inList.Add(r.Name); }
            // 看门猫专项修复在管的程序放到上面的"重点监控"小列表；同一个进程的服务名（svc:）和进程名只留进程名
            List<ProcRow> top = all.Where(r => guarded.ContainsKey(r.Name)).ToList();
            top = top.Where(r => !(r.Name.StartsWith("svc:") && top.Any(o => o != r && o.Pid == r.Pid && !o.Name.StartsWith("svc:")))).ToList();
            list = SortRows(list.Where(r => !guarded.ContainsKey(r.Name)).ToList());
            top = SortRows(top);
            // 原地更新列表（不清空重建）：只改变了的格子，滚动位置和选中都保留，不闪
            updating = true;
            try { FillList(lvG, top); FillList(lv, list); }
            finally { updating = false; }
            LayoutWatchBox(top.Count);
            FitColumns();
            UpdateDetailButtons();
            UpdateActionInfo();
            if (tick % 12 == 3) CaptureSomeLaunches();   // 每分钟顺手记 2 个程序的启动方式（重启时按原来的参数打开）

            UpdateDiskInfo();
            header.Invalidate(); status.Invalidate(); profile.Invalidate(); chart.Invalidate(); detail.Invalidate();
        }

        List<ProcRow> SortRows(List<ProcRow> list)
        {
            switch (sortMode)
            {
                case "mem": return list.OrderByDescending(r => r.PrivMB).ToList();
                case "cpu": return list.OrderByDescending(r => r.CpuPct).ToList();
                case "io": return list.OrderByDescending(r => r.IoMBs).ToList();
                case "hnd": return list.OrderByDescending(r => r.Handles).ToList();
                case "gui": return list.OrderByDescending(r => r.Gdi + r.User).ToList();
                default: return list.OrderByDescending(r => ratings[r.Name].Score + 45 * ResLevel(r.Name) + (r.IoMBs >= HeavyIoMBs ? 40 : 0)).ThenByDescending(r => r.PrivMB).ToList();
            }
        }
        // 原地更新列表（不清空重建）：只改变了的格子，滚动位置和选中都保留，不闪
        void FillList(ListView l, List<ProcRow> list)
        {
            if (list.Count > 0) listMaxMB = Math.Max(1, Math.Max(listMaxMB * 0.98, list.Max(x => x.PrivMB)));   // 内存小横条的满格（慢慢回落，不会每次都跳）
            while (l.Items.Count > list.Count) l.Items.RemoveAt(l.Items.Count - 1);
            for (int i = 0; i < list.Count; i++)
            {
                ProcRow r = list[i]; Rating rt = ratings[r.Name];
                ListViewItem it;
                if (i < l.Items.Count) it = l.Items[i];
                else { it = new ListViewItem(""); for (int k = 1; k < ColCount; k++) it.SubItems.Add(""); l.Items.Add(it); }
                string desc = DescOf(r.Name);
                Issue ri; resIssues.TryGetValue(r.Name, out ri);
                // 稳定性看内存；句柄 / 图形资源有问题时，"判断"一栏显示那个问题
                int lvRow = Math.Max(rt.Level, ri != null ? ri.Level : -1);
                string judge = ri != null && ri.Level >= rt.Level ? ShortTitle(ri, r.Name) : rt.Forecast.Length > 0 ? rt.Forecast : rt.Note;
                // 每一格的文字只用来判断"变没变"（变了才重画）；真正怎么画见 DrawSubItem
                List<HistPt> hp; string spark = hist.TryGetValue(r.Name, out hp) && hp.Count > 0 ? hp.Count + "@" + hp[hp.Count - 1].T.Ticks : "";
                string[] vals = { r.Name, string.Format("{0:N0}", r.PrivMB) + (desc ?? "") + r.Count + ecoNames.Contains(r.Name) + ResLevel(r.Name),
                    string.Format("{0:N1}", r.CpuPct), string.Format("{0:N1}", r.IoMBs), spark, rt.Text + rt.Level, judge + "\n" + rt.Trend };
                bool changed = false;
                for (int k = 0; k < ColCount; k++) if (it.SubItems[k].Text != vals[k]) { it.SubItems[k].Text = vals[k]; changed = true; }
                if (changed) l.Invalidate(it.Bounds);   // 第一格的第二行（说明、进程数、节能）跟着别的格子变，整行重画
                if (!(it.Tag is int) || (int)it.Tag != lvRow) { it.Tag = lvRow; l.Invalidate(it.Bounds); }
                bool sel = r.Name == selected;
                if (it.Selected != sel) it.Selected = sel;
            }
        }

        // 体检结论：规则命中 + 稳定性问题 + 大量读写
        void BuildIssues(List<ProcRow> all, List<ProcRow> real)
        {
            List<Issue> list = new List<Issue>(); HashSet<string> seen = new HashSet<string>();
            foreach (Hit h in hits)
            {
                list.Add(NewIssue(h.Rule.Title, h.Why, h.Rule.FixType == "none" ? 1 : 2, h.Target, "rule:" + h.Rule.Id));
                if (h.Target != null) seen.Add(h.Target);
            }
            HashSet<int> seenPid = new HashSet<int>();   // 服务（svc:名字）和它所在的进程是同一个时只报一次
            foreach (ProcRow r in all.OrderBy(x => x.Name.StartsWith("svc:") ? 0 : 1))
            {
                if (seen.Contains(r.Name)) continue;
                Rating rt = ratings[r.Name];
                if (rt.Level >= 1 && r.Count == 1 && !seenPid.Add(r.Pid)) continue;
                if (rt.Level < 1) continue;   // 正常的、或已被忽略 / 确认正常的
                string label = Label(r.Name);
                if (rt.Text == "疑似泄漏") list.Add(NewIssue(label + " 疑似内存泄漏", rt.Note, 2, r.Name, "prog:" + r.Name));
                else if (rt.Text == "持续泄漏") list.Add(NewIssue(label + " 一直在慢慢泄漏", rt.Note, 2, r.Name, "prog:" + r.Name));
                else if (rt.Text == "缓慢上涨") list.Add(NewIssue(label + " 在缓慢上涨", rt.Note, 1, r.Name, "prog:" + r.Name));
                else if (rt.Text == "涨得多") list.Add(NewIssue(label + " 内存涨得多（浏览器，先关掉不用的标签页）", rt.Note, 1, r.Name, "prog:" + r.Name));
                else if (rt.Text == "短时上涨")
                {
                    Ring ring; double[] st = series.TryGetValue(r.Name, out ring) ? ring.Stats(GapSec) : null;
                    if (st != null && st[2] >= 100) list.Add(NewIssue(label + " 在上涨（观察中）", rt.Note, 1, r.Name, "prog:" + r.Name));
                }
            }
            if (kernelIssue != null) list.Add(kernelIssue);
            foreach (Issue ri in resIssues.Values.OrderByDescending(i => i.Level).Take(4))
            {
                // 这个程序已经报了内存问题：把句柄 / 图形资源的情况并进去，不另起一条
                Issue same = list.FirstOrDefault(i => i.Target == ri.Target);
                if (same != null) { same.Sub = ri.Title.Replace(ri.Target, "").Trim().TrimStart('的') + "：" + ri.Sub.Split('·')[0].Trim() + " · " + same.Sub; same.Level = Math.Max(same.Level, ri.Level); }
                else if (!seen.Contains(ri.Target)) { list.Add(ri); seen.Add(ri.Target); }
            }
            foreach (ProcRow r in real.Where(x => x.IoMBs >= HeavyIoMBs).OrderByDescending(x => x.IoMBs).Take(2))
            {
                if (IsIgnored("io:" + r.Name) != null || seen.Contains(r.Name)) continue;
                list.Add(NewIssue(Label(r.Name) + " 正在大量读写", string.Format("{0:N1} MB/s，长时间这样会拖慢硬盘；如果它不该这么忙，可以重启它", r.IoMBs), 1, r.Name, "io:" + r.Name));
            }
            // 已经做过内存泄漏分析的（右键 → 分析内存泄漏），说明里提示一下
            foreach (Issue i in list) if (i.Key != null && i.Key.StartsWith("prog:") && i.Target != null && incidents.ContainsKey(i.Target)) i.Sub = "已有分析报告（右键查看）· " + i.Sub;
            // 同样严重时：内核问题最先，其次是突发的泄漏（"疑似"），最后是慢慢涨的
            list = list.OrderByDescending(i => i.Level).ThenBy(i => i.Key == "kernel" ? 0 : i.Title.Contains("疑似") ? 1 : 2).ToList();
            int nBad = list.Count(i => i.Level == 2), nWarn = list.Count(i => i.Level == 1);
            int nLong = analysis.Count, nShort = ratings.Values.Count(x => x.Trend.EndsWith("分钟"));
            string basis = string.Format("已评级 {0} 个程序（长期 {1} · 实时 {2}）· 规则 {3} 条", nLong + nShort, nLong, nShort, rules.Count);
            verdict = nBad > 0 ? new[] { "发现 " + nBad + " 个问题", "2", basis }
                : nWarn > 0 ? new[] { "有 " + nWarn + " 项需要留意", "1", basis }
                : nLong + nShort == 0 ? new[] { "数据收集中", "1", "窗口打开满 10 分钟后给出初步判断" }
                : new[] { "电脑健康", "0", basis };
            if (lastAction.Length > 0 && list.Count < 3) list.Add(NewIssue("看门猫最近一次动作", lastAction, -1, null, null));
            issues = list;
            LayoutSide();
            diag.Invalidate();
            PetReact();   // 看板娘：第一次打招呼，出现新问题就说出来
        }
        static Issue NewIssue(string title, string sub, int level, string target, string key)
        {
            Issue i = new Issue(); i.Title = title; i.Sub = sub; i.Level = level; i.Target = target; i.Key = key; return i;
        }
        string Label(string name) { string d = DescOf(name); return d != null ? name + "（" + d + "）" : name; }

        // ---------- 操作 ----------
        // 确认 / 提示：和主界面同风格的小窗口（Monitor.Windows.cs），不用系统弹窗
        bool Ask(string msg) { return CyberDialog("请确认", msg, true, "确定", "取消", null, true); }
        void Say(string msg) { CyberDialog("提示", msg, false, "知道了", null, null, false); }
        static void StartShell(string file, string args)
        {
            try { Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = true }).Dispose(); } catch { }
        }
        // 看门猫、修复脚本仍是 PowerShell 脚本（只在需要时运行几秒）
        void RunTool(string file, string args)
        {
            StartShell("powershell.exe", string.Format("-NoProfile -ExecutionPolicy Bypass -File \"{0}\" {1}", Path.Combine(ToolDir, file), args));
        }
        bool RunElevated(string file, string args)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("powershell.exe", string.Format("-NoProfile -ExecutionPolicy Bypass -File \"{0}\" {1}", Path.Combine(ToolDir, file), args));
                psi.UseShellExecute = true; psi.Verb = "runas"; psi.WindowStyle = ProcessWindowStyle.Hidden;
                Process.Start(psi).Dispose(); return true;
            }
            catch { return false; }   // 用户在管理员确认窗口点了"否"
        }
        void ReloadSoon(int sec) { forceSlowAt = DateTime.Now.AddSeconds(sec); }
        string PathOf(string name) { ProcRow r; return name != null && rows.TryGetValue(name, out r) ? Native.ImagePath(r.Pid) : null; }

        void SelectTarget(string name)
        {
            selected = name;
            updating = true;
            try { foreach (ListView l in new ListView[] { lvG, lv }) foreach (ListViewItem it in l.Items) { bool sel = it.Text == name; if (it.Selected != sel) it.Selected = sel; if (sel) it.EnsureVisible(); } }
            finally { updating = false; }
            UpdateDetailButtons(); SetChartMode("proc"); detail.Invalidate();
        }

        static Process[] ByName(string name) { try { return Process.GetProcessesByName(name); } catch { return new Process[0]; } }
        static void DisposeAll(Process[] ps) { foreach (Process p in ps) p.Dispose(); }

        // 重启一个程序（或服务）。noAsk 用于"一键修复"时已统一确认过。
        bool RestartTarget(string name, bool noAsk)
        {
            if (name == null) { Say("先在列表里选一个程序。"); return false; }
            if (name.StartsWith("svc:"))
            {
                string svc = name.Substring(4);
                if (!noAsk && !Ask("将重启系统服务 " + svc + "（需要管理员权限，会弹出确认窗口）。确定吗？")) return false;
                bool ok = RunElevated("Restart-ServiceHard.ps1", "-Name " + svc); ReloadSoon(8); return ok;
            }
            if (Protected.Contains(name)) { Say(name + " 是系统关键进程，不能在这里重启。"); return false; }
            // 这个程序其实是一个服务（比如 CorsairCpuIdService、ROG Live Service）：按服务重启，直接结束进程会因权限失败，结束了也不会按服务重新启动
            ProcRow pr; string svcName = rows.TryGetValue(name, out pr) && pr.Count == 1 ? Native.ServiceOfPid(pr.Pid) : null;
            if (svcName != null)
            {
                if (!noAsk && !Ask(string.Format("{0} 是系统服务「{1}」，将重启这个服务（需要管理员权限，会弹出确认窗口）。确定吗？", name, svcName))) return false;
                bool ok = RunElevated("Restart-ServiceHard.ps1", "-Name \"" + svcName + "\""); ReloadSoon(8); return ok;
            }
            // 没有专项规则的程序：先试最小的范围，按原来的方式重新打开，核对它回来了（Monitor.SmartFix.cs）
            return SmartRestart(name, noAsk);
        }
        // 按规则修复
        bool ApplyFix(Hit hit, bool noAsk)
        {
            Rule r = hit.Rule;
            switch (r.FixType)
            {
                case "restartProcess": return RestartTarget(r.Process, noAsk);
                case "restartService":
                    if (!noAsk && !Ask(string.Format("{0}\n\n{1}\n\n将执行：{2}（需要管理员权限）。确定吗？", r.Title, r.Cause, r.FixLabel))) return false;
                    { bool ok = RunElevated("Restart-ServiceHard.ps1", "-Name " + r.FixService); ReloadSoon(8); return ok; }
                case "restartGroup":
                    return RunGroup(Json.Objects(Json.Obj(r.Raw, "fix").ContainsKey("steps") ? Json.Obj(r.Raw, "fix")["steps"] : null), r.Title + "\n\n" + r.Cause, noAsk, null);
                case "script":
                    if (!noAsk && !Ask(string.Format("{0}\n\n{1}\n\n将执行：{2}（需要管理员权限）。确定吗？", r.Title, r.Cause, r.FixLabel))) return false;
                    RunTool(r.FixScript, "-Quiet"); ReloadSoon(8); return true;
                default:
                    Say(string.Format("{0}\n\n{1}\n\n建议：{2}", r.Title, r.Cause, r.FixLabel)); return false;
            }
        }
        void FixSelected()
        {
            Hit hit = null; if (selected != null) hitByTarget.TryGetValue(selected, out hit);
            if (hit != null) ApplyFix(hit, false); else RestartTarget(selected, false);
        }
        void KillTarget(string name)
        {
            if (name == null) { Say("先在列表里选一个程序。"); return; }
            if (name.StartsWith("svc:") || Protected.Contains(name)) { Say(name + " 是系统服务或关键进程，不能在这里结束。"); return; }
            Process[] procs = ByName(name);
            try
            {
                if (procs.Length == 0) return;
                int me = Process.GetCurrentProcess().Id;
                if (procs.Any(p => p.Id == me)) { Say("这是监控窗口自己。要退出请右键托盘图标 → 退出。"); return; }
                if (!Ask(string.Format("将结束 {0}（{1} 个进程），不会自动重新打开。没保存的内容可能会丢失。确定吗？", name, procs.Length))) return;
                int failed = 0;
                foreach (Process p in procs) try { p.Kill(); } catch { failed++; }
                if (failed > 0) Say("有 " + failed + " 个进程结束失败（可能需要管理员权限）。");
            }
            finally { DisposeAll(procs); }
        }
        void OpenLocation(string name)
        {
            string path = PathOf(name);
            if (path != null) StartShell("explorer.exe", "/select,\"" + path + "\""); else Say("拿不到这个程序的路径。");
        }
        // 一键修复：所有命中规则且有修复方法的问题 + 没有规则但疑似泄漏的程序（重启它）
        void FixAll()
        {
            List<KeyValuePair<string, object>> todo = FixTodo();
            if (todo.Count == 0) { Say("现在没有需要修复的问题。"); return; }
            if (!Ask("将依次执行：\n\n  " + string.Join("\n  ", todo.Select(t => t.Key)) + "\n\n相关程序里没保存的内容可能会丢失；部分操作需要管理员权限。确定吗？")) return;
            int ok = 0;
            foreach (KeyValuePair<string, object> t in todo)
                if (t.Value is string ? RestartTarget((string)t.Value, true) : ApplyFix((Hit)t.Value, true)) ok++;
            Say("已执行 " + ok + " / " + todo.Count + " 项。");
        }
        void RunCheckNow()
        {
            if (taskInstalled && RunTask("MemWatchdog")) Say("已开始检查，大约 5 秒后完成，结果会自动刷新到界面上。");
            else RunElevated("MemWatchdog.ps1", "");
            ReloadSoon(8);
        }
        static bool RunTask(string name)
        {
            object svc = null;
            try
            {
                Type t = Type.GetTypeFromProgID("Schedule.Service"); svc = Activator.CreateInstance(t);
                t.InvokeMember("Connect", System.Reflection.BindingFlags.InvokeMethod, null, svc, new object[0]);
                object folder = t.InvokeMember("GetFolder", System.Reflection.BindingFlags.InvokeMethod, null, svc, new object[] { "\\" });
                object task = folder.GetType().InvokeMember("GetTask", System.Reflection.BindingFlags.InvokeMethod, null, folder, new object[] { name });
                object run = task.GetType().InvokeMember("Run", System.Reflection.BindingFlags.InvokeMethod, null, task, new object[] { null });
                if (run != null) System.Runtime.InteropServices.Marshal.ReleaseComObject(run);
                System.Runtime.InteropServices.Marshal.ReleaseComObject(task);
                System.Runtime.InteropServices.Marshal.ReleaseComObject(folder);
                return true;
            }
            catch { return false; }
            finally { if (svc != null) System.Runtime.InteropServices.Marshal.ReleaseComObject(svc); }
        }

        // ---- 个人规则文件（rules.local.json）读写：右键"用看门猫监控"就是往这里加一条规则 ----
        string LocalRulesPath { get { return Path.Combine(RulesDir, "rules.local.json"); } }
        List<Dictionary<string, object>> ReadLocalRules()
        {
            if (!File.Exists(LocalRulesPath)) return new List<Dictionary<string, object>>();
            try { return Json.Objects(Json.Parse(Json.ReadFile(LocalRulesPath))); }
            catch (Exception ex) { throw new Exception("rules.local.json 格式有误：" + ex.Message); }
        }
        void WriteLocalRules(List<Dictionary<string, object>> list)
        {
            Json.WriteFile(LocalRulesPath, list);
            rulesSig = ""; LoadRules();
        }
        void AddWatch(string name, bool auto)
        {
            if (name == null) return;
            ProcRow r; double cur = rows.TryGetValue(name, out r) ? r.PrivMB : 0;
            int def = (int)(Math.Ceiling(Math.Max(cur * 1.5, cur + 300) / 100) * 100);
            string what = auto ? "自动重启它" : "提醒你";
            string ans = Prompt("监控这个程序",
                string.Format("让看门猫监控 {0}（现在 {1:N0} MB）。\n\n内存超过多少 MB 时{2}？\n\n看门猫每 10 分钟检查一次，也会专门记录它的内存走势。", name, cur, what), def.ToString());
            if (string.IsNullOrEmpty(ans)) return;
            int limit;
            if (!int.TryParse(ans.Trim(), out limit) || limit <= 0) { Say("请输入一个正整数（单位 MB）。"); return; }
            List<Dictionary<string, object>> list;
            try { list = ReadLocalRules().Where(d => Json.Str(d, "id") != "watch-" + name).ToList(); } catch (Exception ex) { Say(ex.Message); return; }
            Dictionary<string, object> fix = new Dictionary<string, object>();
            if (name.StartsWith("svc:")) { fix["type"] = auto ? "restartService" : "none"; fix["service"] = name.Substring(4); fix["label"] = "重启服务 " + name.Substring(4); }
            else { fix["type"] = auto ? "restartProcess" : "none"; fix["label"] = auto ? "重启 " + name : "方便时手动重启 " + name; }
            Dictionary<string, object> when = new Dictionary<string, object>(); when["process"] = name; when["minMB"] = limit;
            Dictionary<string, object> rule = new Dictionary<string, object>();
            rule["id"] = "watch-" + name; rule["title"] = name + " 超过监控上限"; rule["target"] = name; rule["when"] = when;
            rule["cause"] = "你在监控窗口里设置的：内存超过 " + limit + " MB 时" + what + "。"; rule["fix"] = fix; rule["auto"] = auto;
            list.Add(rule);
            try { WriteLocalRules(list); } catch (Exception ex) { Say("保存 rules.local.json 失败：" + ex.Message); return; }
            Say(string.Format("已加入看门猫监控：{0} 超过 {1} MB 时{2}。\n规则保存在 rules.local.json，可以随时在右键菜单里取消。", name, limit, what));
            RefreshAll();
        }
        void RemoveWatch(string name)
        {
            List<Dictionary<string, object>> list;
            try { list = ReadLocalRules().Where(d => Json.Str(d, "id") != "watch-" + name).ToList(); } catch (Exception ex) { Say(ex.Message); return; }
            try { WriteLocalRules(list); } catch (Exception ex) { Say("保存 rules.local.json 失败：" + ex.Message); return; }
            RefreshAll();
        }
        void EditRules()
        {
            if (!File.Exists(LocalRulesPath))
            {
                File.WriteAllText(LocalRulesPath, "[]", new System.Text.UTF8Encoding(false));
                StartShell("notepad.exe", "\"" + Path.Combine(RulesDir, "rules.local.example.json") + "\"");
            }
            StartShell("notepad.exe", "\"" + LocalRulesPath + "\"");
            ReloadSoon(20);
        }

        // 选中程序当前的问题：[key, 标题]；没有问题返回 null
        string[] IssueOf(string name)
        {
            if (name == null) return null;
            Hit hit; if (hitByTarget.TryGetValue(name, out hit)) return new[] { "rule:" + hit.Rule.Id, hit.Rule.Title };
            Rating rt; if (ratings.TryGetValue(name, out rt) && rt.Level >= 1) return new[] { "prog:" + name, name + " " + rt.Text };
            Issue ri; if (resIssues.TryGetValue(name, out ri)) return new[] { ri.Key, ri.Title };
            ProcRow r; if (rows.TryGetValue(name, out r) && r.IoMBs >= HeavyIoMBs && IsIgnored("io:" + name) == null) return new[] { "io:" + name, name + " 正在大量读写" };
            return null;
        }
    }
}
