// 右键菜单：顶部是程序名，下面按"处理 / 诊断 / 监控 / 提醒"分组，每项左边一个图标（Segoe Fluent Icons），右边灰色小字是补充说明
// 节能模式 = Windows 11 任务管理器里的"效率模式"：最低优先级 + EcoQoS（让 CPU 用省电的方式跑它）。不冻结、不结束，程序照常运行，
// 游戏等前台程序要 CPU 时它会让路。只对普通应用开放，系统进程、外壳、输入法、音频、Claude 等一律不碰。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace WatchDog
{
    class MenuMeta { public string Glyph; public Func<Color> Color; public bool Header; }

    partial class Monitor
    {
        ToolStripMenuItem menuEco, menuEnd, menuOpen;
        ToolStripLabel menuTitle, menuSub;
        Font fMenuIcon;
        Dictionary<ToolStripItem, ToolStripItem[]> menuGroups = new Dictionary<ToolStripItem, ToolStripItem[]>();   // 分组标题 → 组里的项

        void BuildMenu()
        {
            fMenuIcon = IconFont(11f);
            menu = new ContextMenuStrip();
            menu.Renderer = new CyberMenuRenderer(this);
            menu.Font = fCN; menu.ForeColor = cText; menu.ShowImageMargin = true; menu.ImageScalingSize = new Size(22, 22);

            menuTitle = new ToolStripLabel(""); menuTitle.Font = fCNbig; menuTitle.Margin = new Padding(8, 6, 8, 0);
            menuSub = new ToolStripLabel(""); menuSub.Font = fCN; menuSub.ForeColor = cGray; menuSub.Margin = new Padding(8, 0, 8, 4);
            menu.Items.Add(menuTitle); menu.Items.Add(menuSub);

            ToolStripItem hFix = Header("处理");
            menuFix = Item("", () => cCyan, "修复 / 重启", null, delegate { FixSelected(); });
            menuEco = Item("", () => cCyan, "节能模式", "低优先级 · 不影响使用", delegate { ToggleEco(selected); });
            menuEnd = Item("", () => cRed, "结束程序", null, delegate { KillTarget(selected); });
            menuOpen = Item("", () => cGray, "打开文件位置", null, delegate { OpenLocation(selected); });
            menuNote = Item("", () => cGray, "写备注…", null, delegate { EditNote(selected); });
            Group(hFix, menuFix, menuEco, menuEnd, menuOpen, menuNote);

            ToolStripItem hDiag = Header("诊断");
            menuAnalyze = Item("", () => cYellow, "分析内存泄漏", "管理员 · 约 1 分钟", delegate { AnalyzeSelected(); });
            menuCapture = Item("", () => cYellow, "记录句柄现场", "管理员", delegate { CaptureSelected(); });
            menuIncident = Item("", () => cYellow, "打开分析报告", null, delegate { OpenIncident(selected); });
            menuReport = Item("", () => cGray, "生成问题报告…", "发给开发者", delegate { MakeReport(selected); });
            Group(hDiag, menuAnalyze, menuCapture, menuIncident, menuReport);

            ToolStripItem hWatch = Header("监控");
            menuWatch = Item("", () => cCyan, "超过上限时提醒", null, delegate { AddWatch(selected, false); });
            menuWatchA = Item("", () => cCyan, "超过上限时自动重启", null, delegate { AddWatch(selected, true); });
            menuUnwatch = Item("", () => cGray, "取消监控", null, delegate { RemoveWatch(selected); });
            menuWizard = Item("", () => cAccent, "建修复规则…", "选重启方式，先试一次", delegate { ShowRuleWizard(selected); });
            Group(hWatch, menuWatch, menuWatchA, menuUnwatch, menuWizard);

            ToolStripItem hIssue = Header("提醒");
            menuSnooze = Item("", () => cGray, "忽略 24 小时", null, delegate { string[] i = IssueOf(selected); if (i != null) IgnoreIssue(i[0], i[1], false); });
            menuNormal = Item("", () => cGray, "确认正常，不再提醒", null, delegate
            {
                string[] i = IssueOf(selected);
                if (i != null && Ask(string.Format("确认「{0}」是正常的？以后不再提醒；如果是规则发现的，看门猫也不会再自动处理它。", i[1]))) IgnoreIssue(i[0], i[1], true);
            });
            menuRestore = Item("", () => cGray, "恢复提醒", null, delegate { UnignoreName(selected); });
            Group(hIssue, menuSnooze, menuNormal, menuRestore);

            menu.Opening += delegate(object s, System.ComponentModel.CancelEventArgs e)
            {
                string n = selected;
                if (n == null) { e.Cancel = true; return; }
                ProcRow r; rows.TryGetValue(n, out r);
                Rating rt = GetRating(n, r != null ? r.PrivMB : 0);
                menuTitle.Text = n; menuTitle.ForeColor = rt.Level >= 1 ? LevelColor(rt.Level) : cText;
                menuSub.Text = (DescOf(n) ?? "") + (r != null && r.Count > 1 ? " · " + r.Count + " 个进程" : "") + " · " + rt.Text + (ecoNames.Contains(n) ? " · 节能中" : "");
                bool prot = Protected.Contains(n), svc = n.StartsWith("svc:");
                menuFix.Text = btnPrimary.Text;
                menuEco.Visible = EcoAllowed(n);
                bool eco = ecoNames.Contains(n);
                menuEco.Text = eco ? "退出节能模式" : "节能模式";
                menuEco.ShortcutKeyDisplayString = eco ? "恢复正常优先级" : "低优先级 · 不影响使用";
                menuEnd.Visible = !prot;
                menuNote.Text = HasNote(n) ? "改备注…" : "写备注…";
                menuIncident.Visible = incidents.ContainsKey(n);
                bool hasIssue = IssueOf(n) != null;
                menuSnooze.Visible = hasIssue; menuNormal.Visible = hasIssue;
                menuRestore.Visible = HasIgnore(n);
                bool isW = watched.ContainsKey(n);
                menuUnwatch.Visible = isW;
                menuWatch.Text = isW ? "改成：超过上限时提醒" : "超过上限时提醒";
                menuWatchA.Text = isW ? "改成：超过上限时自动重启" : "超过上限时自动重启";
                menuWatchA.Visible = !prot;
                menuWizard.Visible = !prot && !svc;
                // 组里一项都不显示时，组标题也藏起来
                foreach (KeyValuePair<ToolStripItem, ToolStripItem[]> g in menuGroups) g.Key.Available = g.Value.Any(x => x.Available);   // 菜单弹出前 Visible 读出来都是 false，要用 Available
            };
            lv.ContextMenuStrip = menu; lvG.ContextMenuStrip = menu;
        }

        ToolStripMenuItem Item(string glyph, Func<Color> color, string text, string hint, EventHandler onClick)
        {
            ToolStripMenuItem i = new ToolStripMenuItem(text);
            i.ForeColor = cText; i.Click += onClick; i.Padding = new Padding(0, 3, 0, 3);
            i.Tag = new MenuMeta { Glyph = glyph, Color = color };
            if (hint != null) { i.ShortcutKeyDisplayString = hint; i.ShowShortcutKeys = true; }
            menu.Items.Add(i);
            return i;
        }
        ToolStripItem Header(string text)
        {
            ToolStripLabel l = new ToolStripLabel("// " + text);
            l.Font = fTagB; l.ForeColor = cAccent; l.Margin = new Padding(6, 8, 6, 2);
            l.Tag = new MenuMeta { Header = true };
            menu.Items.Add(l);
            return l;
        }
        void Group(ToolStripItem header, params ToolStripItem[] items) { menuGroups[header] = items; }

        // 菜单的画法：选中项 = 底色 + 左边一条强调色竖条；图标画在左边留白里；右边说明用灰色小字
        class CyberMenuRenderer : ToolStripProfessionalRenderer
        {
            readonly Monitor m;
            public CyberMenuRenderer(Monitor m) : base(new CyberColors()) { this.m = m; RoundedEdges = false; }
            protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
            {
                if (!e.Item.Selected || !e.Item.Enabled) return;
                Rectangle r = new Rectangle(2, 0, e.Item.Width - 4, e.Item.Height);
                using (SolidBrush b = new SolidBrush(Theme.MenuSel)) e.Graphics.FillRectangle(b, r);
                using (SolidBrush b = new SolidBrush(Theme.Accent)) e.Graphics.FillRectangle(b, 2, 2, 3, e.Item.Height - 4);
            }
            protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
            {
                MenuMeta meta = e.Item.Tag as MenuMeta;
                ToolStripMenuItem mi = e.Item as ToolStripMenuItem;
                if (mi != null && e.Text == mi.ShortcutKeyDisplayString && !string.IsNullOrEmpty(e.Text))
                {
                    e.TextColor = Theme.Gray;   // 右边的补充说明
                }
                else if (meta != null && !meta.Header && m.fMenuIcon != null)
                {
                    // 左边图标：画在图片留白的中间
                    Rectangle ir = new Rectangle(4, 0, 26, e.Item.Height);
                    TextRenderer.DrawText(e.Graphics, meta.Glyph, m.fMenuIcon, ir, meta.Color(), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                    e.TextColor = e.Item.Enabled ? Theme.Text : Theme.Disabled;
                }
                base.OnRenderItemText(e);
            }
            protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
            {
                using (Pen p = new Pen(Theme.AccentDim)) e.Graphics.DrawLine(p, 8, e.Item.Height / 2, e.Item.Width - 8, e.Item.Height / 2);
            }
            protected override void OnRenderImageMargin(ToolStripRenderEventArgs e) { }   // 左边留白不单独涂色
        }

        // ---------- 节能模式（效率模式）----------
        HashSet<string> ecoNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // 用户开了节能模式的程序（只在这次运行里有效）
        Dictionary<int, uint> ecoOrig = new Dictionary<int, uint>();                          // 进程 id → 原来的优先级，退出节能时还原
        // 除了系统关键进程以外，这些也不碰：外壳、输入法、音频、开始菜单 / 搜索、Claude 自己和本程序
        static readonly HashSet<string> EcoNever = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            "explorer", "ShellExperienceHost", "StartMenuExperienceHost", "SearchHost", "SearchApp", "TextInputHost", "ctfmon", "conhost",
            "RuntimeBroker", "ApplicationFrameHost", "dllhost", "taskhostw", "audiodg", "dwm", "claude", "Claude", "WatchDog", "WatchCat" };
        bool EcoAllowed(string n) { return n != null && !n.StartsWith("svc:") && !Protected.Contains(n) && !EcoNever.Contains(n); }

        void ToggleEco(string n)
        {
            if (!EcoAllowed(n)) return;
            bool on = !ecoNames.Contains(n);
            int ok, fail; ApplyEco(n, on, out ok, out fail);
            if (on) ecoNames.Add(n); else ecoNames.Remove(n);
            if (ok == 0 && fail > 0) { if (on) ecoNames.Remove(n); Say(n + " 是以管理员身份运行的，普通权限改不了它的优先级。"); }
            detail.Invalidate();
        }
        // 对这个名字的所有进程开 / 关节能模式
        void ApplyEco(string n, bool on, out int ok, out int fail)
        {
            ok = 0; fail = 0;
            foreach (Process p in ByName(n))
            {
                using (p)
                {
                    IntPtr h = OpenProcess(0x0200 | 0x1000, false, p.Id);   // PROCESS_SET_INFORMATION | PROCESS_QUERY_LIMITED_INFORMATION
                    if (h == IntPtr.Zero) { fail++; continue; }
                    try
                    {
                        bool done;
                        if (on)
                        {
                            if (!ecoOrig.ContainsKey(p.Id)) ecoOrig[p.Id] = GetPriorityClass(h);
                            done = SetPriorityClass(h, 0x40);   // IDLE_PRIORITY_CLASS
                        }
                        else
                        {
                            uint orig; done = SetPriorityClass(h, ecoOrig.TryGetValue(p.Id, out orig) && orig != 0 ? orig : 0x20u);   // 还原；不知道就用 NORMAL
                            ecoOrig.Remove(p.Id);
                        }
                        PowerThrottle t = new PowerThrottle { Version = 1, ControlMask = 1, StateMask = on ? 1u : 0u };   // EXECUTION_SPEED
                        SetProcessInformation(h, 4, ref t, Marshal.SizeOf(typeof(PowerThrottle)));                 // ProcessPowerThrottling（Win10 1709 以前没有，失败也没关系）
                        if (done) ok++; else fail++;
                    }
                    finally { CloseHandle(h); }
                }
            }
        }
        // 每分钟一次：开了节能的程序如果又开了新进程（浏览器开新标签页），也给它们开上
        void EcoKeep()
        {
            foreach (string n in ecoNames.ToList())
            {
                Process[] ps = ByName(n);
                bool fresh = ps.Any(p => !ecoOrig.ContainsKey(p.Id));
                HashSet<int> alive = new HashSet<int>(ps.Select(p => p.Id));
                foreach (Process p in ps) p.Dispose();
                foreach (int id in ecoOrig.Keys.Where(id => !alive.Contains(id)).ToList()) ecoOrig.Remove(id);   // 已经退出的进程
                if (fresh) { int ok, fail; ApplyEco(n, true, out ok, out fail); }
            }
        }
        // 真正退出时：把开了节能的程序都还原
        void EcoRestoreAll()
        {
            foreach (string n in ecoNames.ToList()) { int ok, fail; ApplyEco(n, false, out ok, out fail); }
            ecoNames.Clear();
        }

        [StructLayout(LayoutKind.Sequential)] struct PowerThrottle { public uint Version, ControlMask, StateMask; }
        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(int access, bool inherit, int pid);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll")] static extern bool SetPriorityClass(IntPtr h, uint cls);
        [DllImport("kernel32.dll")] static extern uint GetPriorityClass(IntPtr h);
        [DllImport("kernel32.dll")] static extern bool SetProcessInformation(IntPtr h, int cls, ref PowerThrottle info, int size);
    }
}
