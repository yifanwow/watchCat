// 设置窗口：开机自动启动、后台看门猫（计划任务）、被 Windows 关掉后自动重新打开、自动更新规则、界面主题、文件夹
// 开机自动启动写在 HKCU\...\Run（只影响当前用户，不需要管理员），启动参数 --tray：开机后直接缩在托盘里
using System;
using System.Collections.Generic;
using System.Linq;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Microsoft.Win32;

namespace WatchDog
{
    partial class Monitor
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run", RunName = "WatchDog";

        static bool AutoStartOn()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey))
                {
                    string v = k == null ? null : k.GetValue(RunName) as string;
                    return v != null && v.IndexOf(Application.ExecutablePath, StringComparison.OrdinalIgnoreCase) >= 0;
                }
            }
            catch { return false; }
        }
        static bool SetAutoStart(bool on)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (on) k.SetValue(RunName, "\"" + Application.ExecutablePath + "\" --tray");
                    else if (k.GetValue(RunName) != null) k.DeleteValue(RunName);
                }
                return true;
            }
            catch { return false; }
        }

        // --tray：启动时不显示窗口，只在托盘里（开机自动启动用）。第一次被要求显示时改成"不显示"，之后点托盘正常打开
        public bool StartInTray;
        protected override void SetVisibleCore(bool value)
        {
            if (value && StartInTray)
            {
                StartInTray = false;
                if (!IsHandleCreated) CreateHandle();
                value = false;
            }
            base.SetVisibleCore(value);
        }

        // 被 Windows 结束后自动重新打开（RegisterApplicationRestart）：默认开，设置里可以关
        internal bool AutoRestartOn() { Dictionary<string, object> d = ReadSettings(); return !d.ContainsKey("autoRestart") || Json.Bool(d, "autoRestart"); }
        void SetAutoRestart(bool on)
        {
            WriteSetting("autoRestart", on);
            try { if (on) Program.RegisterApplicationRestart("", 0); else Program.UnregisterApplicationRestart(); } catch { }
        }

        // 关屏时暂停 NVIDIA App 后台（Display-Power.ps1，由计划任务 WatchCatDisplay 在屏幕关闭 / 点亮时运行）。
        // 只在装了 NVIDIA App 时显示；任务没装时打开开关 = 重新安装后台任务（要管理员确认一次）
        static bool NvAppInstalled()
        {
            try { using (RegistryKey k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\NvContainerLocalSystem")) return k != null; }
            catch { return false; }
        }
        bool NvPauseOn() { Dictionary<string, object> d = ReadSettings(); return displayTaskInstalled && (!d.ContainsKey("nvPauseScreenOff") || Json.Bool(d, "nvPauseScreenOff")); }
        string NvPauseText()
        {
            string last = "";
            try
            {
                string p = System.IO.Path.Combine(DataDir, "display-power.log");
                if (System.IO.File.Exists(p)) { string[] ls = System.IO.File.ReadAllLines(p); if (ls.Length > 0) last = " · 最近：" + ls[ls.Length - 1].Substring(5, 11) + " " + ls[ls.Length - 1].Substring(20); }
            }
            catch { }
            return (displayTaskInstalled ? "屏幕关着时 NVIDIA App 会漏内核内存，关屏暂停、亮屏恢复（显示驱动不动）" : "需要重新安装后台任务（管理员确认一次）") + last;
        }

        // 设置窗口：分四组（启动与后台 / 规则 / 外观 / 看板娘 Fegmi），每组一行说明，每一项右边是开关或选择
        class SetRow { public string Kind, Title; public Func<string> Sub; public Func<bool> Get; public Action Toggle; public int Y; }
        void ShowSettingsWindow()
        {
            const int w = 760, rx = 22, rowH = 60, secH = 44, secGap = 10;
            int rw = w - 2 * rx;
            List<KeyValuePair<string[], List<SetRow>>> groups = new List<KeyValuePair<string[], List<SetRow>>>();
            string msg = "";
            Action<string> say = null;
            groups.Add(new KeyValuePair<string[], List<SetRow>>(new[] { "启动与后台", "关掉窗口、重启电脑以后，WatchCat 也能继续帮你看着" }, new List<SetRow> {
                new SetRow { Kind = "toggle", Title = "开机自动启动", Sub = () => "登录 Windows 后自动在托盘里打开，在后台记录内存走势", Get = AutoStartOn,
                    Toggle = () => { bool on = !AutoStartOn(); say(SetAutoStart(on) ? (on ? "已开启：下次登录 Windows 时自动在托盘里打开" : "已关闭开机自动启动") : "没能写入启动项"); } },
                new SetRow { Kind = "toggle", Title = "后台看门猫", Sub = () => "每 10 分钟检查一次；超过上限、你又没在用电脑时自动处理（需要管理员确认一次）", Get = () => taskInstalled,
                    Toggle = () => { ToggleTask(); say("正在" + (taskInstalled ? "取消" : "安装") + "后台看门猫，可能会弹出管理员确认，十几秒后这里会更新"); } },
                new SetRow { Kind = "toggle", Title = "被关掉后自动重新打开", Sub = () => "程序更新、卡住被系统关掉、重启电脑恢复应用时，让 Windows 把 WatchCat 重新打开", Get = AutoRestartOn,
                    Toggle = () => { bool on = !AutoRestartOn(); SetAutoRestart(on); say(on ? "已开启：被关掉后 Windows 会重新打开 WatchCat" : "已关闭：被关掉后不再自动打开"); } },
            }));
            groups.Add(new KeyValuePair<string[], List<SetRow>>(new[] { "规则", "专项修复规则决定看门猫怎么处理已知会漏内存的程序" }, new List<SetRow> {
                new SetRow { Kind = "toggle", Title = "自动更新公共规则", Sub = () => "每天从 GitHub 取一次，只下载不上传 · " + RulesUpdateText(), Get = RulesUpdateOn,
                    Toggle = () => { bool on = !RulesUpdateOn(); WriteSetting("rulesUpdate", on); if (on) MaybeUpdateRules(true); say(on ? "已开启：每天自动更新一次公共规则" : "已关闭自动更新（还是用现在的规则）"); } },
            }));
            if (NvAppInstalled())
                groups[groups.Count - 1].Value.Add(new SetRow { Kind = "toggle", Title = "关屏时暂停 NVIDIA App 后台", Sub = NvPauseText, Get = NvPauseOn,
                    Toggle = () =>
                    {
                        if (!displayTaskInstalled) { WriteSetting("nvPauseScreenOff", true); RunTool("Install-Watchdog.ps1", "-Quiet"); ReloadSoon(15); say("正在安装后台任务，可能会弹出管理员确认，十几秒后这里会更新"); return; }
                        bool on = !NvPauseOn(); WriteSetting("nvPauseScreenOff", on);
                        say(on ? "已开启：下次屏幕关闭时暂停 NVIDIA App 后台，点亮后恢复" : "已关闭：屏幕关闭时不再暂停（已经暂停的，点亮屏幕时照样恢复）");
                    } });
            groups.Add(new KeyValuePair<string[], List<SetRow>>(new[] { "外观", "切换后立即生效" }, new List<SetRow> {
                new SetRow { Kind = "theme", Title = "界面主题", Sub = () => Theme.Name + "：" + Theme.Desc },
            }));
            groups.Add(new KeyValuePair<string[], List<SetRow>>(new[] { "小助手 Fegmi", "站在主窗口右边，发现问题时会说出来；可以拖动她换位置，右键有菜单" }, new List<SetRow> {
                new SetRow { Kind = "toggle", Title = "召唤 Fegmi", Sub = () => "关掉后她会去休息，数据照常记录", Get = () => petOn,
                    Toggle = () => { bool on = !petOn; if (pet != null) SetPetOn(on); else { petOn = on; WriteSetting("pet", on); } say(on ? "Fegmi 来了～" : "Fegmi 去休息了（数据照常记录）"); } },
                new SetRow { Kind = "size", Title = "Fegmi 的大小", Sub = () => "现在 " + petScale.ToString("0.0") + " 倍（1.0 = 主窗口高度的 60%）" },
            }));
            int yy = 52;
            List<int> secY = new List<int>();
            foreach (var gr in groups) { secY.Add(yy); yy += secH; foreach (SetRow r in gr.Value) { r.Y = yy; yy += rowH; } yy += secGap; }
            int h = yy + 64;
            BufferedPanel body;
            using (Form f = NewWindow("SETTINGS", "设置", w, h, out body))
            {
                say = m => { msg = m; body.Invalidate(); };
                List<SetRow> all = groups.SelectMany(gr => gr.Value).ToList();
                Func<SetRow, Rectangle> swRect = r => new Rectangle(w - rx - 16 - 52, r.Y + (rowH - 26) / 2, 52, 26);
                Func<int, SetRow, Rectangle> themeRect = (k, r) => new Rectangle(w - rx - 16 - (Themes.Length - k) * 84 + 6, r.Y + (rowH - 26) / 2, 78, 26);
                const int nStep = 11, stepW = 18, stepGap = 3;
                Func<int, SetRow, Rectangle> stepRect = (k, r) => new Rectangle(w - rx - 16 - (nStep * (stepW + stepGap) - stepGap) + k * (stepW + stepGap), r.Y + (rowH - 26) / 2, stepW, 26);
                SetRow hoverRow = null; int hoverK = -1;

                body.Paint += (sd, e) =>
                {
                    Graphics g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
                    for (int gi = 0; gi < groups.Count; gi++)
                    {
                        int sy = secY[gi];
                        string head = "// " + groups[gi].Key[0];
                        Txt(g, head, fCNb, cAccent, rx, sy + 12, 200, 22);
                        Txt(g, groups[gi].Key[1], fCN, cGray, rx + TxtW(g, head, fCNb) + 14, sy + 12, rw - 240, 22, tfL | TextFormatFlags.EndEllipsis);
                        g.FillRectangle(bDimLine, rx, sy + secH - 6, rw, 1);
                    }
                    foreach (SetRow r in all)
                    {
                        bool on = r.Get != null && r.Get();
                        g.FillRectangle(bDeep, rx, r.Y + 2, rw, rowH - 6);
                        g.FillRectangle(r.Kind != "toggle" || on ? bLv[0] : bDim, rx, r.Y + 2, 3, rowH - 6);
                        int textW = rw - 40 - (r.Kind == "theme" ? Themes.Length * 84 : r.Kind == "size" ? nStep * (stepW + stepGap) + 60 : 110);
                        Txt(g, r.Title, fCNbig, cText, rx + 16, r.Y + 8, textW, 22);
                        Txt(g, r.Sub(), fCN, cGray, rx + 16, r.Y + 31, textW, 20, tfL | TextFormatFlags.EndEllipsis);
                        if (r.Kind == "toggle")
                        {
                            Rectangle sr = swRect(r); bool hot = hoverRow == r;
                            using (GraphicsPath p = new GraphicsPath())
                            {
                                int d = sr.Height;
                                p.AddArc(sr.X, sr.Y, d, d, 90, 180); p.AddArc(sr.Right - d, sr.Y, d, d, 270, 180); p.CloseFigure();
                                using (SolidBrush b = new SolidBrush(on ? Color.FromArgb(hot ? 255 : 220, cCyan) : (hot ? cRedDim : cOff))) g.FillPath(b, p);
                                if (!on) g.DrawPath(pAccDim, p);
                            }
                            int kx = on ? sr.Right - sr.Height + 3 : sr.X + 3;
                            using (SolidBrush kb = new SolidBrush(on ? cBg : cGray)) g.FillEllipse(kb, kx, sr.Y + 3, sr.Height - 6, sr.Height - 6);
                            Txt(g, on ? "开" : "关", fCNb, on ? cCyan : cGray, sr.X - 34, sr.Y, 28, sr.Height, tfR);
                        }
                        else if (r.Kind == "theme")
                        {
                            for (int k = 0; k < Themes.Length; k++)
                            {
                                Palette tp = Themes[k]; Rectangle r2 = themeRect(k, r); bool cur = tp == Theme, hot = hoverRow == r && hoverK == k;
                                PointF[] poly = CutPoly(r2.X, r2.Y, r2.Width, r2.Height, 8);
                                using (SolidBrush b = new SolidBrush(cur ? tp.Accent : hot ? tp.Panel : tp.Bg)) g.FillPolygon(b, poly);
                                using (Pen pe = new Pen(tp.Accent, 1)) g.DrawPolygon(pe, poly);
                                using (SolidBrush dot = new SolidBrush(tp.Ok)) g.FillRectangle(dot, r2.X + 8, r2.Y + 10, 6, 6);
                                Txt(g, tp.Name, fCNb, cur ? tp.Bg : tp.Accent, r2.X + 14, r2.Y, r2.Width - 16, r2.Height, tfC);
                            }
                        }
                        else if (r.Kind == "size")
                        {
                            int curK = (int)Math.Round((petScale - 0.7) * 10);
                            for (int k = 0; k < nStep; k++)
                            {
                                Rectangle sr = stepRect(k, r);
                                int hh = 8 + k * (sr.Height - 8) / (nStep - 1);
                                Rectangle bar = new Rectangle(sr.X, sr.Bottom - hh, sr.Width, hh);
                                g.FillRectangle(k <= curK ? bLv[0] : bDim, bar);
                                if (hoverRow == r && hoverK == k) g.DrawRectangle(pAcc, bar.X - 1, bar.Y - 1, bar.Width + 1, bar.Height + 1);
                            }
                            Rectangle s0 = stepRect(0, r);
                            Txt(g, "×" + petScale.ToString("0.0"), fTagB, cCyan, s0.X - 56, s0.Y, 50, s0.Height, tfR);
                        }
                    }
                    Txt(g, msg, fCN, cYellow, rx, h - 92, rw, 20);
                };
                body.MouseMove += (sd, e) =>
                {
                    SetRow hr = null; int hk = -1;
                    foreach (SetRow r in all)
                    {
                        if (r.Kind == "toggle" && Rectangle.Inflate(swRect(r), 40, 8).Contains(e.Location)) hr = r;
                        if (r.Kind == "theme") for (int k = 0; k < Themes.Length; k++) if (themeRect(k, r).Contains(e.Location) && Themes[k] != Theme) { hr = r; hk = k; }
                        if (r.Kind == "size") for (int k = 0; k < nStep; k++) if (Rectangle.Inflate(stepRect(k, r), 1, 6).Contains(e.Location)) { hr = r; hk = k; }
                    }
                    if (hr != hoverRow || hk != hoverK) { hoverRow = hr; hoverK = hk; body.Cursor = hr != null ? Cursors.Hand : Cursors.Default; body.Invalidate(); }
                };
                body.MouseClick += (sd, e) =>
                {
                    SetRow r = hoverRow; if (r == null) return;
                    if (r.Kind == "toggle") r.Toggle();
                    else if (r.Kind == "theme" && hoverK >= 0) { SwitchTheme(Themes[hoverK].Id); say("已换成「" + Theme.Name + "」主题"); hoverRow = null; body.Cursor = Cursors.Default; }
                    else if (r.Kind == "size" && hoverK >= 0)
                    {
                        if (pet != null) SetPetScale(0.7 + hoverK * 0.1); else { petScale = Math.Round(0.7 + hoverK * 0.1, 1); WriteSetting("petScale", petScale); }
                        say("Fegmi 的大小：" + petScale.ToString("0.0") + " 倍");
                    }
                    body.Invalidate();
                };
                // 后台看门猫装好 / 取消后状态会变：每秒看一下
                Timer t = new Timer(); t.Interval = 1000;
                Func<string> sig = () => string.Join("|", all.Where(x => x.Get != null).Select(x => x.Get() ? "1" : "0")) + RulesUpdateText();
                string last = sig();
                t.Tick += delegate { string now = sig(); if (now != last) { last = now; body.Invalidate(); } };
                t.Start();
                f.FormClosed += delegate { t.Stop(); t.Dispose(); };

                NewBtn(body, "打开工具文件夹", rx, h - 52, 140, delegate { StartShell("explorer.exe", "\"" + ToolDir + "\""); }, false);
                NewBtn(body, "打开数据文件夹", rx + 150, h - 52, 140, delegate
                {
                    if (!System.IO.Directory.Exists(DataDir)) System.IO.Directory.CreateDirectory(DataDir);
                    StartShell("explorer.exe", "\"" + DataDir + "\"");
                }, false);
                NewBtn(body, "立即更新规则", rx + 300, h - 52, 130, delegate { MaybeUpdateRules(true); body.Invalidate(); }, false);
                NewBtn(body, "关闭", w - rx - 100, h - 52, 100, delegate { f.Close(); }, false);
                f.ShowDialog(this);
            }
        }
    }
}
