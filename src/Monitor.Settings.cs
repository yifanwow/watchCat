// 设置窗口：开机自动启动、后台看门猫（计划任务）、被 Windows 关掉后自动重新打开、自动更新规则、界面主题、文件夹
// 开机自动启动写在 HKCU\...\Run（只影响当前用户，不需要管理员），启动参数 --tray：开机后直接缩在托盘里
using System;
using System.Collections.Generic;
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

        void ShowSettingsWindow()
        {
            const int w = 700, h = 668;
            BufferedPanel body;
            using (Form f = NewWindow("SETTINGS", "设置", w, h, out body))
            {
                // 每一行：标题、说明、右边的开关（或状态）
                const int rowH = 74, top = 56, rx = 22, rw = w - 44;
                string[] titles = { "开机自动启动", "后台看门猫", "被关掉后自动重新打开", "自动更新公共规则", "界面主题", "召唤 Fegmi", "Fegmi 的大小" };
                string[] subs = {
                    "登录 Windows 后自动打开 WatchCat，直接缩在托盘里，在后台记录走势",
                    "每 10 分钟检查一次（计划任务），超过上限且你空闲时自动处理；窗口关掉也照常运行",
                    "程序更新、卡住被关掉、重启电脑恢复应用时，Windows 会把 WatchCat 重新打开（一直开着）",
                    "每天从 GitHub 取一次最新的专项修复规则（只下载规则文件，不上传任何东西）", "", "看板娘 Fegmi 站在主窗口右边：会眨眼、挥手，发现问题时在头顶说出来。可以拖动换位置，右键有菜单", "" };
                const int nRow = 7;
                Rectangle[] sw = new Rectangle[nRow];
                for (int i = 0; i < nRow; i++) sw[i] = new Rectangle(w - rx - 18 - 52, top + i * rowH + 22, 52, 26);
                // 主题：右边每个主题一个按钮，用那个主题自己的颜色画（选中 = 实心）
                Rectangle[] themeBtn = new Rectangle[Themes.Length];
                for (int k = 0; k < Themes.Length; k++) themeBtn[k] = new Rectangle(w - rx - 18 - (Themes.Length - k) * 84 + 6, top + 4 * rowH + 22, 78, 26);
                // Fegmi 的大小：0.7–1.7 倍，11 个格子，点哪格就是多大
                const int nStep = 11, stepW = 20, stepGap = 3;
                Rectangle sizeBar = new Rectangle(w - rx - 18 - (nStep * (stepW + stepGap) - stepGap), top + 6 * rowH + 22, nStep * (stepW + stepGap) - stepGap, 26);
                Func<int, Rectangle> stepRect = k => new Rectangle(sizeBar.X + k * (stepW + stepGap), sizeBar.Y, stepW, sizeBar.Height);
                int hover = -1;   // 0–5 开关，10 起 = 主题按钮，20 起 = 大小格子
                string msg = "";
                Func<int, int> state = i => i == 0 ? (AutoStartOn() ? 1 : 0) : i == 1 ? (taskInstalled ? 1 : 0) : i == 3 ? (RulesUpdateOn() ? 1 : 0) : i == 4 ? 1 : i == 5 ? (petOn ? 1 : 0) : i == 6 ? 1 : 2;   // 2 = 一直开着，不能关

                body.Paint += (s, e) =>
                {
                    Graphics g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
                    for (int i = 0; i < nRow; i++)
                    {
                        int y = top + i * rowH, st = state(i);
                        g.FillRectangle(bDeep, rx, y + 4, rw, rowH - 10);
                        g.FillRectangle(st >= 1 ? bLv[0] : bDim, rx, y + 4, 3, rowH - 10);
                        Txt(g, titles[i], fCNbig, cText, rx + 18, y + 12, rw - 140, 24);
                        Txt(g, i == 3 ? "每天从 GitHub 取一次最新规则，只下载不上传 · " + RulesUpdateText() : i == 4 ? "现在：" + Theme.Name + "（" + Theme.Desc + "）· 切换后立即生效"
                            : i == 6 ? "现在 " + petScale.ToString("0.0") + " 倍（1.0 = 主窗口高度的 60%）· 点右边的格子调" : subs[i],
                            fCN, cGray, rx + 18, y + 38, i == 4 ? rw - 18 - Themes.Length * 84 - 24 : i == 6 ? sizeBar.X - rx - 30 : rw - 130, 20, tfL | TextFormatFlags.EndEllipsis);
                        if (i == 4)
                        {
                            for (int k = 0; k < Themes.Length; k++)
                            {
                                Palette tp = Themes[k]; Rectangle r2 = themeBtn[k]; bool on = tp == Theme, hot = hover == 10 + k;
                                PointF[] poly = CutPoly(r2.X, r2.Y, r2.Width, r2.Height, 8);
                                using (SolidBrush b = new SolidBrush(on ? tp.Accent : hot ? tp.Panel : tp.Bg)) g.FillPolygon(b, poly);
                                using (Pen pe = new Pen(tp.Accent, 1)) g.DrawPolygon(pe, poly);
                                using (SolidBrush dot = new SolidBrush(tp.Ok)) g.FillRectangle(dot, r2.X + 8, r2.Y + 10, 6, 6);
                                Txt(g, tp.Name, fCNb, on ? tp.Bg : tp.Accent, r2.X + 14, r2.Y, r2.Width - 16, r2.Height, tfC);
                            }
                            continue;
                        }
                        if (i == 6)
                        {
                            int cur = (int)Math.Round((petScale - 0.7) * 10);
                            for (int k = 0; k < nStep; k++)
                            {
                                Rectangle sr = stepRect(k);
                                int hgt = 8 + k * (sr.Height - 8) / (nStep - 1);   // 格子从矮到高，像音量条
                                Rectangle bar = new Rectangle(sr.X, sr.Bottom - hgt, sr.Width, hgt);
                                g.FillRectangle(k <= cur ? bLv[0] : bDim, bar);
                                if (hover == 20 + k) g.DrawRectangle(pAcc, bar.X - 1, bar.Y - 1, bar.Width + 1, bar.Height + 1);
                            }
                            Txt(g, "×" + petScale.ToString("0.0"), fTagB, cCyan, sizeBar.X - 52, sizeBar.Y, 46, sizeBar.Height, tfR);
                            continue;
                        }
                        Rectangle r = sw[i];
                        if (st == 2) { Txt(g, "已开启", fCNb, cCyan, r.X - 20, r.Y, r.Width + 20, r.Height, tfR); continue; }
                        // 开关：开 = 荧光蓝底、圆点在右；关 = 暗底、圆点在左
                        using (GraphicsPath p = new GraphicsPath())
                        {
                            int d = r.Height;
                            p.AddArc(r.X, r.Y, d, d, 90, 180); p.AddArc(r.Right - d, r.Y, d, d, 270, 180); p.CloseFigure();
                            using (SolidBrush b = new SolidBrush(st == 1 ? Color.FromArgb(hover == i ? 255 : 220, cCyan) : (hover == i ? cRedDim : cOff))) g.FillPath(b, p);
                            if (st != 1) g.DrawPath(pAccDim, p);
                        }
                        int kx = st == 1 ? r.Right - r.Height + 3 : r.X + 3;
                        using (SolidBrush kb = new SolidBrush(st == 1 ? cBg : cGray)) g.FillEllipse(kb, kx, r.Y + 3, r.Height - 6, r.Height - 6);
                        Txt(g, st == 1 ? "开" : "关", fCNb, st == 1 ? cCyan : cGray, r.X - 34, r.Y, 28, r.Height, tfR);
                    }
                    Txt(g, msg, fCN, cYellow, rx, top + nRow * rowH + 2, rw, 20);
                };
                body.MouseMove += (s, e) =>
                {
                    int hv = -1;
                    for (int i = 0; i < nRow; i++) if (i != 2 && i != 4 && i != 6 && Rectangle.Inflate(sw[i], 40, 8).Contains(e.Location)) hv = i;
                    for (int k = 0; k < Themes.Length; k++) if (themeBtn[k].Contains(e.Location) && Themes[k] != Theme) hv = 10 + k;
                    for (int k = 0; k < nStep; k++) if (Rectangle.Inflate(stepRect(k), 1, 6).Contains(e.Location)) hv = 20 + k;
                    if (hv != hover) { hover = hv; body.Cursor = hv >= 0 ? Cursors.Hand : Cursors.Default; body.Invalidate(); }
                };
                body.MouseClick += (s, e) =>
                {
                    if (hover == 0)
                    {
                        bool on = !AutoStartOn();
                        msg = SetAutoStart(on) ? (on ? "已开启：下次登录 Windows 时自动在托盘里打开" : "已关闭开机自动启动") : "没能写入启动项";
                        body.Invalidate();
                    }
                    else if (hover == 1)
                    {
                        ToggleTask();
                        msg = "正在" + (taskInstalled ? "取消" : "安装") + "后台看门猫，可能会弹出管理员确认，十几秒后这里会更新";
                        body.Invalidate();
                    }
                    else if (hover == 5)
                    {
                        bool on = !petOn;
                        if (pet != null) SetPetOn(on); else { petOn = on; WriteSetting("pet", on); }
                        msg = on ? "Fegmi 来了～" : "Fegmi 去休息了（数据照常记录）";
                        body.Invalidate();
                    }
                    else if (hover >= 20)
                    {
                        if (pet != null) SetPetScale(0.7 + (hover - 20) * 0.1); else { petScale = Math.Round(0.7 + (hover - 20) * 0.1, 1); WriteSetting("petScale", petScale); }
                        msg = "Fegmi 的大小：" + petScale.ToString("0.0") + " 倍";
                        body.Invalidate();
                    }
                    else if (hover >= 10)
                    {
                        SwitchTheme(Themes[hover - 10].Id);
                        msg = "已换成「" + Theme.Name + "」主题";
                        hover = -1; body.Cursor = Cursors.Default;
                        body.Invalidate();
                    }
                    else if (hover == 3)
                    {
                        bool on = !RulesUpdateOn();
                        WriteSetting("rulesUpdate", on);
                        if (on) MaybeUpdateRules(true);
                        msg = on ? "已开启：每天自动更新一次公共规则" : "已关闭自动更新（还是用现在的规则）";
                        body.Invalidate();
                    }
                };
                // 后台看门猫装好 / 取消后状态会变：每秒看一下
                Timer t = new Timer(); t.Interval = 1000;
                string last = state(0) + "|" + state(1) + "|" + state(3) + "|" + state(5) + "|" + RulesUpdateText();
                t.Tick += delegate { string now = state(0) + "|" + state(1) + "|" + state(3) + "|" + state(5) + "|" + RulesUpdateText(); if (now != last) { last = now; body.Invalidate(); } };
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
