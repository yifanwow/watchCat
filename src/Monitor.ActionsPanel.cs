// 右下角"操作"区：自己画的图标卡片（不用系统按钮），鼠标移上去高亮，点一下执行
// 一行大按钮（一键修复 / 立即检查）+ 一张"专项修复"卡片（所有针对某个程序的修复都在里面）+ 六个小格子
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace WatchDog
{
    class ActTile
    {
        public Rectangle R; public string Icon, Label, Sub, Count; public int Kind;   // Kind：0 小格子，1 一键修复，2 立即检查，3 专项修复
        public Color SubColor, IconColor; public Action Run;
    }

    partial class Monitor
    {
        List<ActTile> actTiles = new List<ActTile>();
        int actHover = -1;
        string actSig = "";
        Font fIcon, fIconBig;
        SolidBrush bPrimary, bPrimaryHot, bTile, bTileHot, bFeat, bFeatHot;
        Pen pTile, pFeat;
        ActTile tFixAll, tCheck, tFixes, tRules, tLog, tReports, tIgnored, tFolder;

        // Windows 11 自带的图标字体（没有就用 Windows 10 的）
        Font IconFont(float size)
        {
            foreach (string n in new[] { "Segoe Fluent Icons", "Segoe MDL2 Assets" })
            {
                Font f = new Font(n, size, GraphicsUnit.Point);
                if (f.Name == n) return Keep(f);
                f.Dispose();
            }
            return Keep(new Font("Segoe UI Symbol", size));
        }

        ActTile Tile(int kind, string icon, string label, Action run)
        {
            ActTile t = new ActTile(); t.Kind = kind; t.Icon = icon; t.Label = label; t.Run = run; t.Sub = ""; t.Count = "";
            t.SubColor = cGray; t.IconColor = cAccent;
            actTiles.Add(t); return t;
        }

        void CreateActionBrushes()
        {
            bPrimary = Keep(new SolidBrush(cPrimary)); bPrimaryHot = Keep(new SolidBrush(cPrimaryHot));
            bTile = Keep(new SolidBrush(cTile)); bTileHot = Keep(new SolidBrush(cSel));
            bFeat = Keep(new SolidBrush(cFeat)); bFeatHot = Keep(new SolidBrush(cFeatHot));
            pTile = Keep(new Pen(cRedDim, 1)); pFeat = Keep(new Pen(Color.FromArgb(150, cCyan), 1));
        }
        void BuildActions()
        {
            fIcon = IconFont(12f); fIconBig = IconFont(17f);
            CreateActionBrushes();

            tFixAll = Tile(1, "", "一键修复全部问题", FixAll);
            tCheck = Tile(2, "", "立即检查", RunCheckNow);
            tFixes = Tile(3, "", "专项修复", delegate { ShowFixesWindow(); });
            tRules = Tile(0, "", "规则设置", delegate { ShowRulesWindow(null); });
            tLog = Tile(0, "", "修复记录", ShowFixLogWindow);
            tReports = Tile(0, "", "现场报告", delegate
            {
                string dir = Path.Combine(DataDir, "incidents");
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                StartShell("explorer.exe", "\"" + dir + "\"");
            });
            tIgnored = Tile(0, "", "已忽略", ShowIgnoredWindow);
            tFolder = Tile(0, "", "设置", delegate { ShowSettingsWindow(); });   // 开机自动启动、后台看门狗、文件夹

            actions.Paint += PaintActions;
            actions.Resize += delegate { LayoutActions(); };
            actions.MouseMove += (s, e) => SetActHover(actTiles.FindIndex(t => t.R.Contains(e.Location)));
            actions.MouseLeave += delegate { SetActHover(-1); };
            actions.MouseClick += (s, e) =>
            {
                if (e.Button != MouseButtons.Left) return;
                ActTile t = actTiles.FirstOrDefault(x => x.R.Contains(e.Location));
                if (t != null && t.Run != null) { SetActHover(-1); t.Run(); UpdateActionInfo(); }
            };
            LayoutActions();
        }
        void SetActHover(int i)
        {
            if (i == actHover) return;
            if (actHover >= 0 && actHover < actTiles.Count) actions.Invalidate(Inflate(actTiles[actHover].R));
            actHover = i;
            if (i >= 0) actions.Invalidate(Inflate(actTiles[i].R));
            actions.Cursor = i >= 0 ? Cursors.Hand : Cursors.Default;
        }
        static Rectangle Inflate(Rectangle r) { r.Inflate(2, 2); return r; }

        // 安装 / 取消后台看门狗（计划任务），在设置窗口里用
        void ToggleTask()
        {
            if (taskInstalled) { if (Ask("将删除看门狗计划任务，之后不再每 10 分钟自动检查和处理。确定吗？")) RunTool("Install-Watchdog.ps1", "-Uninstall"); else return; }
            else RunTool("Install-Watchdog.ps1", "");
            ReloadSoon(15);
        }
        void LayoutActions()
        {
            int x = 14, w = actions.ClientSize.Width - 28, y = 36;
            int wCheck = 104;
            tFixAll.R = new Rectangle(x, y, w - wCheck - 8, 44);
            tCheck.R = new Rectangle(x + w - wCheck, y, wCheck, 44);
            y += 52;
            tFixes.R = new Rectangle(x, y, w, 64);
            y += 72;
            // 第一排 3 个，第二排 2 个（后台看门狗的开关在"设置"里）
            ActTile[] small = { tRules, tLog, tReports, tIgnored, tFolder };
            int cw = (w - 12) / 3, hw = (w - 6) / 2;
            for (int i = 0; i < 3; i++) small[i].R = new Rectangle(x + i * (cw + 6), y, i == 2 ? w - 2 * (cw + 6) : cw, 32);
            tIgnored.R = new Rectangle(x, y + 38, hw, 32);
            tFolder.R = new Rectangle(x + hw + 6, y + 38, w - hw - 6, 32);
        }

        // 小字说明（问题数、上次检查时间、最近一次修复……）：内容变了才重画
        DateTime actLogStamp = DateTime.MinValue; string actLastFix = ""; Color actLastFixColor = Color.Empty;
        void UpdateActionInfo()
        {
            if (tFixAll == null) return;
            int n = FixTodo().Count;
            tFixAll.Sub = n > 0 ? n + " 个问题可以处理" : "现在没有需要处理的问题";
            tFixAll.SubColor = n > 0 ? cYellow : cGray;
            tFixAll.IconColor = n > 0 ? cYellow : cAccent;
            tCheck.Sub = sys.Count > 0 ? "上次 " + sys[sys.Count - 1].T.ToString("HH:mm") : "还没检查过";

            List<Rule> fx = allRules.Where(r => r.FixType != "none").ToList();
            int auto = fx.Count(r => !r.Disabled && r.Auto), over = fx.Count(r => !r.Disabled && OverLimit(r) >= 1);
            // 看门狗日志：文件变了才重新读
            DateTime stamp = File.Exists(LogFile) ? File.GetLastWriteTimeUtc(LogFile) : DateTime.MinValue;
            if (stamp != actLogStamp)
            {
                actLogStamp = stamp; actLastFix = ""; actLastFixColor = cGray;
                List<string[]> log = ReadActionLog();
                for (int i = log.Count - 1; i >= 0; i--)
                {
                    string[] e = log[i]; if (e[1] != "DONE" && e[1] != "FIX") continue;
                    string id = e[2].Split(':')[0];
                    Rule r = allRules.FirstOrDefault(x => x.Id == id);
                    string res = e[1] == "DONE" ? CheckText(System.Text.RegularExpressions.Regex.Match(e[2], @"MB, (.*)$").Groups[1].Value) : "开始处理";
                    actLastFix = "最近：" + ShortName(r, id) + " " + e[0].Substring(5, 11) + " " + (res.StartsWith("核对通过") ? "✓" : res.StartsWith("核对失败") ? "✗" : res.StartsWith("补救") ? "✓（补救过）" : "…");
                    actLastFixColor = res.StartsWith("核对失败") ? cRed : cGray;
                    break;
                }
            }
            tFixes.Sub = string.Format("{0} 个程序 · {1} 个自动处理", fx.Count, auto) + (over > 0 ? " · " + over + " 个超过上限" : "");
            tFixes.SubColor = over > 0 ? cYellow : cCyan;
            tFixes.Count = actLastFix;

            int nIg = ActiveIgnores().Count;
            tIgnored.Count = nIg > 0 ? nIg.ToString() : "";
            tReports.Count = incidents.Count > 0 ? incidents.Count.ToString() : "";
            // 后台看门狗没装：设置按钮标红，提醒去设置里装上
            tFolder.IconColor = taskInstalled ? cAccent : cRed;
            tFolder.Count = taskInstalled ? "" : "!";
            tRules.Count = allRules.Count > 0 ? allRules.Count.ToString() : "";

            string sig = string.Join("|", actTiles.Select(t => t.Label + t.Sub + t.Count + t.SubColor.ToArgb() + t.IconColor.ToArgb())) + actLastFixColor.ToArgb();
            if (sig != actSig) { actSig = sig; actions.Invalidate(); }
        }
        // 规则对应程序的简称（专项修复卡片、操作区用）
        static string ShortName(Rule r, string fallback)
        {
            if (r == null) return fallback;
            string t = r.Title;
            foreach (string cut in new[] { "内存泄漏", "泄漏", "占用过高", "膨胀" }) if (t.EndsWith(cut) && t.Length > cut.Length) { t = t.Substring(0, t.Length - cut.Length); break; }
            return t;
        }

        void PaintActions(object s, PaintEventArgs e)
        {
            Graphics g = e.Graphics; int w = actions.Width, h = actions.Height;
            Frame(g, w, h, "ACTIONS", "操作", false);
            Txt(g, taskInstalled ? "AUTO · 10 MIN" : "AUTO · OFF", fSmall, taskInstalled ? cCyan : cRed, w - 130, 9, 112, 20, tfR);
            for (int i = 0; i < actTiles.Count; i++) DrawTile(g, actTiles[i], i == actHover);
        }

        void DrawTile(Graphics g, ActTile t, bool hot)
        {
            Rectangle r = t.R;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            PointF[] poly = CutPoly(r.X, r.Y, r.Width - 1, r.Height - 1, t.Kind == 0 ? 7 : 10);
            switch (t.Kind)
            {
                case 1:   // 一键修复：深红底 + 左侧黄黑警示条
                    g.FillPolygon(hot ? bPrimaryHot : bPrimary, poly);
                    g.DrawPolygon(pAcc, poly);
                    g.FillRectangle(bHaz, r.X + 1, r.Y + 6, 5, r.Height - 12);
                    Txt(g, t.Icon, fIconBig, t.IconColor, r.X + 12, r.Y, 30, r.Height, tfC);
                    Txt(g, t.Label, fCNb, cText, r.X + 46, r.Y + 5, r.Width - 52, 18);
                    Txt(g, t.Sub, fCN, t.SubColor, r.X + 46, r.Y + 23, r.Width - 52, 17);
                    break;
                case 2:   // 立即检查
                    g.FillPolygon(hot ? bTileHot : bTile, poly);
                    g.DrawPolygon(hot ? pCyanThin : pAcc, poly);
                    Txt(g, t.Icon, fIcon, hot ? cCyan : cAccent, r.X + 8, r.Y + 4, 22, 20, tfC);
                    Txt(g, t.Label, fCNb, hot ? cCyan : cText, r.X + 32, r.Y + 4, r.Width - 36, 20);
                    Txt(g, t.Sub, fCN, cGray, r.X + 10, r.Y + 24, r.Width - 16, 16);
                    break;
                case 3:   // 专项修复：荧光蓝卡片
                    g.FillPolygon(hot ? bFeatHot : bFeat, poly);
                    g.DrawPolygon(hot ? pCyanThin : pFeat, poly);
                    g.FillRectangle(bLv[0], r.X + 1, r.Y + 8, 3, r.Height - 16);
                    Txt(g, t.Icon, fIconBig, cCyan, r.X + 10, r.Y, 32, r.Height, tfC);
                    Txt(g, t.Label, fCNb, cCyan, r.X + 46, r.Y + 5, 80, 20);
                    Txt(g, "每个程序的泄漏和修复", fCN, cGray, r.X + 46 + TxtW(g, t.Label, fCNb) + 8, r.Y + 5, r.Width - 160, 20);
                    Txt(g, t.Sub, fCN, t.SubColor, r.X + 46, r.Y + 25, r.Width - 70, 18);
                    Txt(g, t.Count.Length > 0 ? t.Count : "还没有自动处理过", fCN, actLastFixColor == cRed ? cRed : cGray, r.X + 46, r.Y + 43, r.Width - 70, 17);
                    Txt(g, "", fIcon, hot ? cCyan : cGray, r.Right - 26, r.Y, 20, r.Height, tfC);   // ›
                    break;
                default:  // 小格子
                    g.FillPolygon(hot ? bTileHot : bTile, poly);
                    g.DrawPolygon(hot ? pCyanThin : pTile, poly);
                    Txt(g, t.Icon, fIcon, hot ? cCyan : t.IconColor, r.X + 6, r.Y, 22, r.Height, tfC);
                    int cw = t.Count.Length > 0 ? TxtW(g, t.Count, fSmall) + 10 : 0;
                    Txt(g, t.Label, fCN, hot ? cCyan : cText, r.X + 29, r.Y, r.Width - 31 - cw, r.Height);
                    if (cw > 0)
                    {
                        Rectangle c = new Rectangle(r.Right - cw - 5, r.Y + (r.Height - 16) / 2, cw, 16);
                        g.FillRectangle(bDeep, c);
                        Txt(g, t.Count, fSmall, t == tIgnored ? cYellow : cGray, c.X, c.Y, c.Width, c.Height, tfC);
                    }
                    break;
            }
        }
    }
}
