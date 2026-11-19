// 专项修复：所有"针对某个程序"的泄漏修复放在一个列表里（一个程序一张卡片）
// 每张卡片：这个程序现在多大、上限多少、为什么会涨、怎么修、看门狗最近一次做了什么；可以立即修复、改设置、看分析报告
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace WatchDog
{
    partial class Monitor
    {
        // 规则管的东西现在多大：程序的内存，或者文件（NVIDIA 数据库）的大小。拿不到返回 false
        bool RuleValue(Rule r, out double cur, out double limit)
        {
            cur = -1; limit = r.MinMB > 0 ? r.MinMB : r.MinFileMB;
            if (r.MinFileMB > 0 && r.File != null) { double? f; if (fileMB.TryGetValue(r.File, out f) && f != null) cur = f.Value; }
            else if (r.Process != null) { ProcRow row; if (rows.TryGetValue(r.Process, out row)) cur = row.PrivMB; }
            return cur >= 0;
        }
        // 现在 / 上限；拿不到或没有上限返回 -1
        double OverLimit(Rule r)
        {
            double cur, limit;
            return RuleValue(r, out cur, out limit) && limit > 0 ? cur / limit : -1;
        }

        // 一键修复要做的事：命中规则且有修复方法的问题 + 没有规则但疑似泄漏 / 句柄、图形资源过多的程序（重启它）
        List<KeyValuePair<string, object>> FixTodo()
        {
            List<KeyValuePair<string, object>> todo = new List<KeyValuePair<string, object>>(); HashSet<string> seen = new HashSet<string>();
            foreach (Hit h in hits)
                if (h.Rule.FixType != "none" && !seen.Contains(h.Target ?? "")) { todo.Add(new KeyValuePair<string, object>(h.Rule.FixLabel, h)); seen.Add(h.Target ?? ""); }
            foreach (ProcRow r in rows.Values)
            {
                if (seen.Contains(r.Name) || Protected.Contains(r.Name)) continue;
                Rating rt; if (ratings.TryGetValue(r.Name, out rt) && (rt.Text == "疑似泄漏" || rt.Text == "持续泄漏") && rt.Level >= 2) { todo.Add(new KeyValuePair<string, object>("重启 " + r.Name + "（" + rt.Text + "）", r.Name)); seen.Add(r.Name); continue; }
                if (ResLevel(r.Name) >= 2 && !r.Name.StartsWith("svc:")) { todo.Add(new KeyValuePair<string, object>("重启 " + r.Name + "（句柄 / 图形资源）", r.Name)); seen.Add(r.Name); }
            }
            return todo;
        }

        void ShowFixesWindow()
        {
            rulesSig = ""; LoadRules(); UpdateSlowFiles();
            const int w = 1120, cardH = 182, gap = 12, cols = 2;
            int cardW = (w - 28 - 16 - gap) / cols;   // 16 = 滚动条
            List<Rule> list = allRules.Where(r => r.FixType != "none").ToList();
            int rowsN = (list.Count + cols - 1) / cols;
            int contentH = Math.Max(cardH, rowsN * (cardH + gap) - gap);
            int viewH = Math.Min(contentH, 4 * (cardH + gap) - gap);
            int h = 150 + viewH + 66;
            BufferedPanel body;
            using (Form f = NewWindow("TARGETED FIX", "专项修复", w, h, out body))
            {
                List<string[]> log = ReadActionLog();
                // 顶部四个数字
                body.Paint += (s, e) =>
                {
                    Graphics g = e.Graphics;
                    int auto = list.Count(r => !r.Disabled && r.Auto), over = list.Count(r => !r.Disabled && OverLimit(r) >= 1);
                    string last = ""; int fixes = 0;
                    foreach (string[] x in log) if (x[1] == "DONE") { fixes++; last = x[0].Substring(5, 11); }
                    string[,] stats = { { list.Count.ToString(), "个程序有专项修复" }, { auto.ToString(), "个由看门狗自动处理" }, { over.ToString(), "个现在超过上限" }, { fixes.ToString(), last.Length > 0 ? "次自动处理 · 最近 " + last : "次自动处理" } };
                    int sw = (w - 28 - 3 * 10) / 4;
                    for (int i = 0; i < 4; i++)
                    {
                        int x = 14 + i * (sw + 10), y = 40;
                        Color c = i == 2 && over > 0 ? cYellow : i == 1 ? cCyan : cText;
                        g.FillRectangle(bDeep, x, y, sw, 52);
                        g.FillRectangle(i == 2 && over > 0 ? bLv[1] : i == 1 ? bLv[0] : bAcc, x, y, 3, 52);
                        Txt(g, stats[i, 0], fNum, c, x + 14, y + 4, 70, 44);
                        Txt(g, stats[i, 1], fCN, cGray, x + 14 + TxtW(g, stats[i, 0], fNum) + 6, y + 4, sw - 90, 44);
                    }
                    Txt(g, "这些程序都查过根因，会一直慢慢涨。看门狗每 10 分钟检查一次：超过上限、而且你空闲时，只重启出问题的那一小块，再核对是不是都恢复了（灯效、设备、Steam 本身都不动）。",
                        fCN, cGray, 22, 102, w - 44, 40, tfW);
                };
                Panel scroll = new Panel(); scroll.SetBounds(14, 150, w - 28, viewH); scroll.AutoScroll = true; scroll.BackColor = cBg;
                scroll.HandleCreated += delegate { Native.SetWindowTheme(scroll.Handle, "DarkMode_Explorer", null); };   // 深色滚动条
                body.Controls.Add(scroll);
                BufferedPanel content = new BufferedPanel(); content.SetBounds(0, 0, w - 28 - 18, contentH); content.BackColor = cBg;
                scroll.Controls.Add(content);
                content.Paint += (s, e) =>
                {
                    for (int i = 0; i < list.Count; i++)
                    {
                        Rectangle r = new Rectangle((i % cols) * (cardW + gap), (i / cols) * (cardH + gap), cardW, cardH);
                        if (e.ClipRectangle.IntersectsWith(r)) DrawFixCard(e.Graphics, list[i], r, log);
                    }
                };
                for (int i = 0; i < list.Count; i++)
                {
                    Rule r = list[i];
                    int x = (i % cols) * (cardW + gap), y = (i / cols) * (cardH + gap) + cardH - 42;
                    NewBtn(content, "立即修复", x + cardW - 14 - 108 - 8 - 70, y, 108, delegate
                    {
                        Hit hit = new Hit(); hit.Rule = r; hit.Target = r.Target;
                        if (ApplyFix(hit, false)) Say("已开始：" + r.FixLabel + "。\n\n大约半分钟后完成；看门狗下次检查时会按规则核对。");
                    }, true);
                    NewBtn(content, "设置", x + cardW - 14 - 70, y, 70, delegate
                    {
                        ShowRulesWindow(r.Id);
                        log = ReadActionLog(); content.Invalidate(); body.Invalidate();
                    }, false);
                    string inc; string key = r.Process ?? r.Target;
                    if (key != null && incidents.TryGetValue(key, out inc) && File.Exists(inc))
                    {
                        string file = inc;
                        NewBtn(content, "看分析报告", x + cardW - 14 - 108 - 8 - 70 - 8 - 96, y, 96, delegate { StartShell("notepad.exe", "\"" + file + "\""); }, false);
                    }
                }
                // 数字每 3 秒跟着主窗口的数据更新（变了才重画）
                string sig = "";
                Timer tm = new Timer(); tm.Interval = 3000;
                tm.Tick += delegate
                {
                    string now = string.Join("|", list.Select(r => { double c, l; return RuleValue(r, out c, out l) ? c.ToString("0") : "-"; }));
                    if (now != sig) { sig = now; content.Invalidate(); body.Invalidate(); }
                };
                tm.Start();
                f.FormClosed += delegate { tm.Stop(); tm.Dispose(); };

                NewBtn(body, "规则设置（全部）", 22, h - 52, 160, delegate { ShowRulesWindow(null); log = ReadActionLog(); content.Invalidate(); body.Invalidate(); }, false);
                NewBtn(body, "修复记录", 190, h - 52, 120, delegate { ShowFixLogWindow(); }, false);
                NewBtn(body, "关闭", w - 22 - 100, h - 52, 100, delegate { f.Close(); }, false);
                f.ShowDialog(this);
            }
        }
        // 规则里的文件大小（NVIDIA 数据库）：打开窗口时现读一次，关掉的规则也要显示
        void UpdateSlowFiles()
        {
            foreach (Rule r in allRules)
                if (r.File != null && !fileMB.ContainsKey(r.File))
                    try { fileMB[r.File] = File.Exists(r.File) ? (double?)(new FileInfo(r.File).Length / 1048576.0) : null; } catch { }
        }

        // 多行文字超过 lines 行时截断并加"…"（系统的省略号只对单行有效，多行会露出半行）。结果缓存
        Dictionary<string, string> fitCache = new Dictionary<string, string>();
        string FitLines(Graphics g, string s, Font f, int w, int lines)
        {
            string key = w + "|" + lines + "|" + s, o;
            if (fitCache.TryGetValue(key, out o)) return o;
            int lineH = TextRenderer.MeasureText(g, "测", f, new Size(w, 100), tfW).Height, max = lineH * lines;
            o = s;
            if (TextRenderer.MeasureText(g, s, f, new Size(w, 4000), tfW).Height > max)
            {
                int lo = 0, hi = s.Length;
                while (lo < hi)   // 二分找最长的、加上"…"还放得下的前缀
                {
                    int mid = (lo + hi + 1) / 2;
                    if (TextRenderer.MeasureText(g, s.Substring(0, mid) + "…", f, new Size(w, 4000), tfW).Height <= max) lo = mid; else hi = mid - 1;
                }
                o = s.Substring(0, lo).TrimEnd('，', '。', '；', ' ') + "…";
            }
            if (fitCache.Count > 200) fitCache.Clear();
            fitCache[key] = o;
            return o;
        }

        void DrawFixCard(Graphics g, Rule r, Rectangle b, List<string[]> log)
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            double cur, limit; bool has = RuleValue(r, out cur, out limit);
            double ratio = has && limit > 0 ? cur / limit : -1;
            int lvl = r.Disabled || !has ? -1 : ratio >= 1 ? 2 : ratio >= 0.75 ? 1 : 0;
            Color lc = LevelColor(lvl);
            PointF[] poly = CutPoly(b.X + 1, b.Y + 1, b.Width - 3, b.Height - 3, 14);
            g.FillPolygon(bPanel, poly);
            g.DrawPolygon(lvl == 2 ? pCore[Lx(2)] : pAccDim, poly);
            g.FillRectangle(bLv[Lx(lvl)], b.X + 1, b.Y + 14, 4, b.Height - 28);
            int x = b.X + 18, wIn = b.Width - 36;

            // 标题 + 状态徽章
            string state = RuleState(r);
            // 程序自己的图标（罗技 / 英伟达 / 海盗船 / Steam……）；拿不到（svchost 里的服务、没在运行）就画一个图标字
            Rule sh; string iconOf = Json.Str(r.Raw, "icon") ?? (sharedRules.TryGetValue(r.Id, out sh) ? Json.Str(sh.Raw, "icon") : null);   // 规则可以借别的程序的图标（服务程序自己没有图标时，比如用 iCUE 的）
            Bitmap ico = (iconOf != null ? GetIcon(iconOf) : null) ?? GetIcon(r.Process ?? r.Target ?? "");
            Rectangle ib = new Rectangle(x, b.Y + 12, 40, 40);
            g.FillRectangle(bDeep, ib);
            if (ico != null) { g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic; g.DrawImage(ico, ib.X + 4, ib.Y + 4, 32, 32); }
            else Txt(g, (r.Target ?? "").IndexOf("Audio", StringComparison.OrdinalIgnoreCase) >= 0 ? "\uE767" : "\uE90F", fIconBig, r.Disabled ? cGray : cCyan, ib.X, ib.Y, ib.Width, ib.Height, tfC);
            x += 52; wIn -= 52;
            Txt(g, r.Title, fCNbig, r.Disabled ? cGray : cText, x, b.Y + 12, wIn - 100, 26);
            Badge(g, state, r.Disabled ? 3 : state == "自动处理" ? 0 : 1, b.Right - 18 - 90, b.Y + 14, 90, 22);

            // 程序名 · 什么时候动手
            string who = r.MinFileMB > 0 && r.File != null ? Path.GetFileName(r.File) : (r.Process ?? r.Target ?? "-");
            Txt(g, who + "   ·   " + (r.Disabled ? "已关闭，不检查" : RuleIdle(r) + "才动手"), fCN, cGray, x, b.Y + 38, wIn, 18);
            x -= 52; wIn += 52;   // 下面几行和图标左对齐

            // 现在多大 / 上限：数字 + 条
            string val = has ? (cur >= 1024 ? (cur / 1024).ToString("0.0") : cur.ToString("0")) : "--";
            string unit = has ? (cur >= 1024 ? "GB" : "MB") : (r.Process != null ? "没在运行" : "");
            Txt(g, val, fNumS, lc, x, b.Y + 60, 90, 30);
            int vw = TxtW(g, val, fNumS);
            Txt(g, unit, fCN, cGray, x + vw + 4, b.Y + 68, 70, 18);
            int bx = x + 150, bw = wIn - 150 - 120, by = b.Y + 74;
            g.FillRectangle(bDeep, bx, by, bw, 6);
            if (has && limit > 0)
            {
                double max = Math.Max(limit * 1.25, cur);
                int fw = (int)(bw * Math.Min(1, cur / max)), lx = bx + (int)(bw * limit / max);
                g.FillRectangle(bLv[Lx(lvl)], bx, by, fw, 6);
                g.FillRectangle(bAcc, lx - 1, by - 4, 2, 14);   // 上限位置
            }
            Txt(g, limit > 0 ? "上限 " + FmtMB(limit) : "没有上限", fCN, cGray, b.Right - 18 - 116, b.Y + 66, 116, 20, tfR);

            // 为什么会涨（两行）
            Txt(g, FitLines(g, r.Cause, fCN, wIn, 2), fCN, Color.FromArgb(200, 180, 172), x, b.Y + 96, wIn, 40, tfW);

            // 最近一次
            string last = LastActionOf(r.Id, log);
            Txt(g, last.Length > 0 ? "最近：" + last : "看门狗还没处理过它", fCN, last.Contains("失败") ? cRed : last.Length > 0 ? cCyan : cGray,
                x, b.Bottom - 40, wIn - 300, 26);
        }
    }
}
