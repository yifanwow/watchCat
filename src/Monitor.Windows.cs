// 和主界面同风格的子窗口：提示 / 确认 / 输入框（代替系统弹窗）、规则窗口、自动修复记录、已忽略的问题
// 子窗口用完即释放；字体、画刷是主窗口共用的，不在这里释放
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace WatchDog
{
    partial class Monitor
    {
        // ---------- 通用：窗口外壳 ----------
        Form NewWindow(string tag, string title, int w, int h, out BufferedPanel body)
        {
            Form f = new Form();
            f.Text = "WATCHCAT // " + title; f.ClientSize = new Size(w, h);
            f.StartPosition = Visible && WindowState != FormWindowState.Minimized ? FormStartPosition.CenterParent : FormStartPosition.CenterScreen;
            f.FormBorderStyle = FormBorderStyle.FixedSingle; f.MaximizeBox = false; f.MinimizeBox = false; f.ShowInTaskbar = false;
            f.BackColor = cBg; f.ForeColor = cText; f.Font = fCN; f.KeyPreview = true;
            if (formIcon != null) f.Icon = formIcon;
            f.KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) f.Close(); };
            f.HandleCreated += delegate
            {
                ThemeTitleBar(f.Handle);
                if (capIconH != IntPtr.Zero) Native.SendMessage(f.Handle, 0x80, IntPtr.Zero, capIconH);   // 标题栏小图标和主窗口一样（字母 W）
            };
            if (snapFile != null)   // 自测：在屏幕外显示，截图后自动关掉
            {
                string file = snapFile; snapFile = null;
                f.StartPosition = FormStartPosition.Manual; f.Location = new Point(-6000, -6000);
                f.Shown += delegate { Program.Snapshot(f, file, null); f.Close(); };
            }
            BufferedPanel p = new BufferedPanel(); p.Dock = DockStyle.Fill; p.BackColor = cBg;
            p.Paint += (s, e) => Frame(e.Graphics, p.Width, p.Height, tag, title, false);
            f.Controls.Add(p);
            body = p;
            return f;
        }

        // ---------- 自测：打开某个子窗口并截图 ----------
        string snapFile, snapPick;
        public void SnapWindow(string kind, string file, string pick)
        {
            snapFile = file; snapPick = pick;
            switch (kind)
            {
                case "rules": ShowRulesWindow(pick); break;
                case "fixes": ShowFixesWindow(); break;
                case "fixlog": ShowFixLogWindow(); break;
                case "ignored": ShowIgnoredWindow(); break;
                case "settings": ShowSettingsWindow(); break;
                case "wizard": ShowRuleWizard(pick ?? "lghub_agent"); break;
                case "reportfile": File.WriteAllText(file, WriteReport(pick ?? "lghub_agent")); break;   // 自测：生成问题报告，把报告路径写进 file
                case "ask": Ask("将结束 lghub_agent（1 个进程）并重新打开：\nC:\\Program Files\\LGHUB\\lghub_agent.exe\n\n这个程序里没保存的内容可能会丢失。确定吗？"); break;
                case "eco":   // 自测：对 pick 开节能 → 读优先级 → 关节能 → 再读，结果写进 file
                    {
                        string n = pick ?? "notepad"; int ok, fail;
                        Func<string> pr = () => string.Join(",", ByName(n).Select(x => { try { using (x) return x.PriorityClass.ToString(); } catch { return "?"; } }));
                        string before = pr();
                        ApplyEco(n, true, out ok, out fail); string during = pr() + "（成功 " + ok + "，失败 " + fail + "）";
                        ApplyEco(n, false, out ok, out fail); string after = pr();
                        File.WriteAllText(file, "之前 " + before + " | 节能 " + during + " | 恢复 " + after + " | 允许 " + EcoAllowed(n) + "，explorer 允许 " + EcoAllowed("explorer") + "，svc:Audiosrv 允许 " + EcoAllowed("svc:Audiosrv"), System.Text.Encoding.UTF8);
                        break;
                    }
                case "menu":   // 右键菜单：选中 pick（默认 claude），在屏幕外弹出来截图
                    {
                        selected = pick ?? "claude";
                        menu.Show(this, new Point(10, 10));
                        Application.DoEvents();
                        using (Bitmap b = new Bitmap(menu.Width, menu.Height)) { menu.DrawToBitmap(b, new Rectangle(0, 0, menu.Width, menu.Height)); b.Save(file); }
                        menu.Close();
                        break;
                    }
                case "prompt": Prompt("监控这个程序", "让看门猫监控 CorsairCpuIdService（现在 52 MB）。\n\n内存超过多少 MB 时提醒你？", "500"); break;
            }
            snapFile = null; snapPick = null;
        }

        // ---------- 通用：深色表格（和程序列表一样的画法）----------
        // color(行, 列) 给每格的文字颜色；行的 Tag 是级别（2 红 / 1 黄 / 其他 普通）
        BufferedListView NewList(Control parent, Rectangle bounds, object[,] cols, Func<ListViewItem, int, Color> color)
        {
            BufferedListView l = new BufferedListView();
            l.Bounds = bounds; l.View = View.Details; l.FullRowSelect = true; l.HideSelection = false; l.MultiSelect = false;
            l.OwnerDraw = true; l.BackColor = cRowA; l.ForeColor = cText; l.BorderStyle = BorderStyle.None; l.Font = fCN;
            ImageList rh = new ImageList(); rh.ImageSize = new Size(1, 28); l.SmallImageList = rh;
            l.Disposed += delegate { rh.Dispose(); };
            for (int i = 0; i < cols.GetLength(0); i++) l.Columns.Add((string)cols[i, 0], (int)cols[i, 1]);
            l.DrawColumnHeader += (s, e) =>
            {
                e.Graphics.FillRectangle(bBg, e.Bounds);
                e.Graphics.FillRectangle(bAcc, e.Bounds.X, e.Bounds.Bottom - 2, e.Bounds.Width, 2);
                Txt(e.Graphics, e.Header.Text, fCNb, cAccent, e.Bounds.X + 8, e.Bounds.Y, e.Bounds.Width - 16, e.Bounds.Height);
            };
            l.DrawItem += delegate { };
            l.DrawSubItem += (s, e) =>
            {
                int lvl = e.Item.Tag is int ? (int)e.Item.Tag : 0;
                Brush bg = e.Item.Selected ? bSel : lvl == 2 ? bRow2 : lvl == 1 ? bRow1 : e.Item.Index % 2 == 1 ? bRowB : bRowA;
                e.Graphics.FillRectangle(bg, e.Bounds);
                if (e.ColumnIndex == 0 && e.Item.Selected) e.Graphics.FillRectangle(bLv[0], e.Bounds.X, e.Bounds.Y + 3, 3, e.Bounds.Height - 6);
                Txt(e.Graphics, e.SubItem.Text, e.ColumnIndex == 0 ? fCNb : fCN, color(e.Item, e.ColumnIndex), e.Bounds.X + 8, e.Bounds.Y, e.Bounds.Width - 14, e.Bounds.Height);
            };
            l.ItemSelectionChanged += (s, e) => l.Invalidate(e.Item.Bounds);
            l.HandleCreated += delegate
            {
                Native.SetWindowTheme(l.Handle, "DarkMode_ItemsView", null);   // 不用 Explorer 主题：它会在空白处画列分隔线
                IntPtr hdr = Native.SendMessage(l.Handle, 0x101F, IntPtr.Zero, IntPtr.Zero);
                if (hdr != IntPtr.Zero) Native.SetWindowTheme(hdr, "DarkMode_ItemsView", null);
            };
            // 最后一列填满剩下的宽度
            EventHandler fit = delegate
            {
                int sum = 0; for (int i = 0; i < l.Columns.Count - 1; i++) sum += l.Columns[i].Width;
                int want = Math.Max(80, l.ClientSize.Width - sum - 2);
                if (l.Columns.Count > 0 && l.Columns[l.Columns.Count - 1].Width != want) l.Columns[l.Columns.Count - 1].Width = want;
            };
            l.Resize += fit; l.HandleCreated += fit;
            parent.Controls.Add(l);
            return l;
        }
        TextBox NewBox(Control parent, int x, int y, int w, string text)
        {
            TextBox t = new TextBox();
            t.SetBounds(x, y, w, 26); t.Text = text ?? "";
            t.BackColor = cInput; t.ForeColor = cCyan; t.BorderStyle = BorderStyle.FixedSingle; t.Font = fCNb;
            parent.Controls.Add(t);
            return t;
        }

        // ---------- 提示 / 确认 / 输入（代替系统弹窗）----------
        // 返回 true = 点了确定；input 不为 null 时显示一个输入框，结果写回 input[0]
        bool CyberDialog(string title, string msg, bool twoButtons, string yes, string no, string[] input, bool warn)
        {
            const int w = 500;
            int th;
            using (Graphics g = CreateGraphics()) th = TextRenderer.MeasureText(g, msg, fCN, new Size(w - 48, 4000), tfW).Height;
            th = Math.Min(th, 520);
            int ch = 46 + th + (input != null ? 42 : 0) + 62;
            BufferedPanel body;
            using (Form f = NewWindow(warn ? "CONFIRM" : "NOTICE", title, w, ch, out body))
            {
                body.Paint += (s, e) =>
                {
                    if (warn) e.Graphics.FillRectangle(bHaz, 14, 46, 6, th);
                    Txt(e.Graphics, msg, fCN, cText, warn ? 28 : 22, 44, w - (warn ? 52 : 44), th + 4, tfW);
                };
                TextBox tb = input != null ? NewBox(body, 22, 44 + th + 10, w - 44, input[0]) : null;
                bool ok = false;
                int by = ch - 46;
                Button bYes = NewBtn(body, yes, w - 22 - 118 - (twoButtons ? 126 : 0), by, 118, delegate { ok = true; f.Close(); }, true);
                if (twoButtons) { Button bNo = NewBtn(body, no, w - 22 - 118, by, 118, delegate { f.Close(); }, false); f.CancelButton = bNo; }
                f.AcceptButton = bYes;
                f.Shown += delegate { if (tb != null) { tb.Focus(); tb.SelectAll(); } else bYes.Focus(); };
                f.ShowDialog(this);
                if (ok && tb != null) input[0] = tb.Text;
                return ok;
            }
        }
        // 输入框：取消返回 null
        string Prompt(string title, string msg, string def)
        {
            string[] box = { def ?? "" };
            return CyberDialog(title, msg, true, "确定", "取消", box, false) ? box[0] : null;
        }

        // ---------- 规则窗口：看全部规则，改上限 / 空闲时间 / 自动处理 / 开关 ----------
        static readonly Regex LogLine = new Regex(@"^(\d{4}-\d\d-\d\d \d\d:\d\d:\d\d) (?:\[[^\]]*\] )?(FIX|DONE|NOTICE|CAPTURE|ANALYZE|HIGH|RESTART|REBUILD) (.*)$");
        List<string[]> ReadActionLog()   // [时间, 动作, 其余]，按时间顺序
        {
            List<string[]> o = new List<string[]>();
            try
            {
                if (File.Exists(LogFile))
                    foreach (string l in File.ReadAllLines(LogFile, System.Text.Encoding.UTF8))
                    {
                        Match m = LogLine.Match(l.Trim());
                        if (m.Success) o.Add(new[] { m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value });
                    }
            }
            catch { }
            return o;
        }
        // 看门猫日志里的英文核对结果，翻成一句中文
        static string CheckText(string s)
        {
            if (s == null) return "";
            if (s.StartsWith("checked: all back")) return "核对通过：都恢复了";
            if (s.StartsWith("checked: back to normal after one recovery")) return "补救一次后恢复";
            if (s.StartsWith("checked: running; log not updated")) return "在运行（日志没更新，静态灯效属正常）";
            if (s.StartsWith("no check")) return "没有设核对项";
            if (s.StartsWith("CHECK FAILED")) return "核对失败：" + s.Substring(Math.Min(s.Length, 15)).Replace("not running", "没在运行").Replace("service not running", "服务没在运行").Replace("log not updated", "日志没更新");
            return s;
        }
        // 某条规则最近一次的动作（来自看门猫日志）
        string LastActionOf(string id, List<string[]> log)
        {
            for (int i = log.Count - 1; i >= 0; i--)
            {
                string[] e = log[i];
                if (!e[2].StartsWith(id + ":")) continue;
                string t = e[0].Substring(5, 11);
                if (e[1] == "DONE")
                {
                    Match m = Regex.Match(e[2], @":\s*(\S+) MB -> (\S+) MB, (.*)$");
                    return m.Success ? string.Format("{0} 已处理 {1} → {2} MB · {3}", t, m.Groups[1].Value, m.Groups[2].Value, CheckText(m.Groups[3].Value)) : t + " 已处理";
                }
                if (e[1] == "NOTICE") return t + " 提醒过";
                if (e[1] == "FIX") return t + " 开始处理";
            }
            return "";
        }
        static string RuleState(Rule r) { return r.Disabled ? "已关闭" : r.Auto && r.FixType != "none" ? "自动处理" : "只提醒"; }
        static string RuleWhen(Rule r)
        {
            if (r.MinMB > 0) return string.Format("超过 {0:N0} MB", r.MinMB);
            if (r.MinFileMB > 0) return string.Format("{0} 超过 {1:N0} MB", Path.GetFileName(r.File ?? ""), r.MinFileMB);
            return "-";
        }
        static string RuleIdle(Rule r) { return (r.IdleMin > 0 ? r.IdleMin + " 分钟没操作" : "随时") + (r.SkipIfGame ? " · 不在游戏时" : ""); }
        static string VerifyText(Rule r)
        {
            Dictionary<string, object> v = Json.Obj(r.Raw, "verify");
            List<string> o = new List<string>();
            if (r.FixType == "restartService") o.Add("服务在运行");
            if (v != null)
            {
                object run; if (v.TryGetValue("running", out run) && run is System.Collections.IEnumerable && !(run is string))
                {
                    List<string> names = new List<string>(); foreach (object x in (System.Collections.IEnumerable)run) names.Add(Convert.ToString(x));
                    if (names.Count > 0) o.Add(string.Join("、", names) + " 都在");
                }
                string file = Json.Str(v, "file");
                if (file != null)
                {
                    List<string> texts = new List<string>(); object tx; if (v.TryGetValue("text", out tx) && tx is System.Collections.IEnumerable && !(tx is string)) foreach (object x in (System.Collections.IEnumerable)tx) texts.Add(Convert.ToString(x));
                    o.Add(Path.GetFileName(file.TrimEnd('\\')) + " 重新写入" + (texts.Count > 0 ? "了「" + string.Join("」或「", texts) + "」" : "") + (Json.Bool(v, "soft") ? "（没写也只提示）" : ""));
                }
            }
            return o.Count == 0 ? "（这条规则没有设核对项）" : string.Join("；", o) + "。不对会补救一次（再启动服务 / 按原来的方式再启动程序）";
        }

        // 规则窗口：上面是规则列表（两行一条：规则名 + 程序、状态标签、上限和现在用了多少、最近一次处理），
        // 下面是选中规则的详情卡片：为什么会涨、怎么处理、设置（上限、空闲多久才动手、自动处理还是只提醒、启用还是关闭）
        void ShowRulesWindow(string pick)   // pick = 打开时选中的规则 id
        {
            rulesSig = ""; LoadRules();
            const int w = 1100, h = 780, listTop = 70, listH = 368, rowH = 48;
            int detTop = listTop + listH + 14;
            BufferedPanel body;
            using (Form f = NewWindow("RULES", "看门猫的规则", w, h, out body))
            {
                List<string[]> log = ReadActionLog();
                Rule cur = null;
                bool editAuto = false, editOn = true;
                // ---- 规则列表（两行一条）----
                BufferedListView lv2 = new BufferedListView();
                lv2.Bounds = new Rectangle(14, listTop, w - 28, listH); lv2.View = View.Details; lv2.FullRowSelect = true; lv2.HideSelection = false; lv2.MultiSelect = false;
                lv2.OwnerDraw = true; lv2.BackColor = cRowA; lv2.ForeColor = cText; lv2.BorderStyle = BorderStyle.None; lv2.Font = fCN;
                ImageList rh = new ImageList(); rh.ImageSize = new Size(1, rowH); lv2.SmallImageList = rh; lv2.Disposed += delegate { rh.Dispose(); };
                foreach (object[] c in new[] { new object[] { "规则 / 程序", 360 }, new object[] { "状态", 120 }, new object[] { "上限", 190 }, new object[] { "空闲才动手", 150 }, new object[] { "最近一次", 230 } }) lv2.Columns.Add((string)c[0], (int)c[1]);
                lv2.DrawColumnHeader += (s, e) =>
                {
                    e.Graphics.FillRectangle(bBg, e.Bounds);
                    e.Graphics.FillRectangle(bAcc, e.Bounds.X, e.Bounds.Bottom - 2, e.Bounds.Width, 2);
                    Txt(e.Graphics, e.Header.Text, fCNb, cAccent, e.Bounds.X + 10, e.Bounds.Y, e.Bounds.Width - 16, e.Bounds.Height);
                };
                lv2.DrawItem += delegate { };
                lv2.DrawSubItem += (s, e) =>
                {
                    Graphics g = e.Graphics; Rectangle r = e.Bounds; Rule ru = (Rule)e.Item.Tag;
                    string name = ru.Target ?? ru.Process;
                    ProcRow pr = null; if (name != null) rows.TryGetValue(name, out pr);
                    double now = pr != null ? pr.PrivMB : 0;
                    bool over = ru.MinMB > 0 && now >= ru.MinMB;
                    Brush bg = e.Item.Selected ? bSel : over && !ru.Disabled ? bRow1 : e.Item.Index % 2 == 1 ? bRowB : bRowA;
                    g.FillRectangle(bg, r);
                    if (e.Item.Index > 0) g.FillRectangle(bDimLine, r.X, r.Y, r.Width, 1);
                    int y1 = r.Y + 5, y2 = r.Y + 24;
                    switch (e.ColumnIndex)
                    {
                        case 0:
                            if (e.Item.Selected) g.FillRectangle(bLv[0], r.X, r.Y + 4, 3, r.Height - 8);
                            Bitmap ic = name != null ? GetIcon(name) : null;
                            if (ic != null) { g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic; g.DrawImage(ic, r.X + 12, r.Y + 12, 24, 24); }
                            else g.FillRectangle(bDim, r.X + 15, r.Y + 15, 18, 18);
                            Txt(g, ru.Title, fCNb, ru.Disabled ? cGray : cText, r.X + 46, y1, r.Width - 54, 18);
                            Txt(g, (name ?? "-") + (DescOf(name) != null ? " · " + DescOf(name) : ""), fCN, cGray, r.X + 46, y2, r.Width - 54, 18);
                            break;
                        case 1:
                        {
                            string st = RuleState(ru);
                            Color c = ru.Disabled ? cGray : st == "自动处理" ? cCyan : cYellow;
                            Size ts = TextRenderer.MeasureText(g, st, fCNb, new Size(200, 30), tfL);
                            Rectangle pill = new Rectangle(r.X + 10, r.Y + (r.Height - 22) / 2, ts.Width + 16, 22);
                            PointF[] poly = CutPoly(pill.X, pill.Y, pill.Width, pill.Height, 6);
                            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                            using (SolidBrush b = new SolidBrush(Color.FromArgb(ru.Disabled ? 0 : 40, c))) g.FillPolygon(b, poly);
                            using (Pen pe = new Pen(ru.Disabled ? cRedDim : c)) g.DrawPolygon(pe, poly);
                            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;
                            Txt(g, st, fCNb, c, pill.X, pill.Y, pill.Width, pill.Height, tfC);
                            break;
                        }
                        case 2:
                            Txt(g, RuleWhen(ru), fCN, cText, r.X + 10, y1, r.Width - 16, 18);
                            if (ru.MinMB > 0)
                            {
                                Txt(g, pr != null ? string.Format("现在 {0:N0} MB", now) : "现在没在运行", fCN, over ? cYellow : cGray, r.X + 10, y2, r.Width - 16, 18);
                                // 现在用了上限的多少：一条小横条
                                int bw = r.Width - 28, fw = (int)(bw * Math.Min(1, now / ru.MinMB));
                                using (SolidBrush tr = new SolidBrush(Color.FromArgb(34, cText))) g.FillRectangle(tr, r.X + 10, r.Bottom - 6, bw, 3);
                                if (fw > 0) g.FillRectangle(over ? bLv[1] : bLv[0], r.X + 10, r.Bottom - 6, fw, 3);
                            }
                            break;
                        case 3:
                            Txt(g, RuleIdle(ru), fCN, cGray, r.X + 10, r.Y, r.Width - 16, r.Height);
                            break;
                        case 4:
                        {
                            string la = LastActionOf(ru.Id, log);
                            if (string.IsNullOrEmpty(la)) { Txt(g, "还没处理过", fCN, cGray, r.X + 10, r.Y, r.Width - 16, r.Height); break; }
                            string when = la.Length > 11 ? la.Substring(0, 11) : la, what = la.Length > 12 ? la.Substring(12) : "";
                            Txt(g, when, fCN, cGray, r.X + 10, y1, r.Width - 16, 18);
                            Txt(g, what, fCN, what.Contains("失败") ? LevelColor(2) : cCyan, r.X + 10, y2, r.Width - 16, 18);
                            break;
                        }
                    }
                };
                lv2.ItemSelectionChanged += (s, e) => lv2.Invalidate(e.Item.Bounds);
                lv2.HandleCreated += delegate
                {
                    Native.SetWindowTheme(lv2.Handle, "DarkMode_ItemsView", null);
                    IntPtr hdr = Native.SendMessage(lv2.Handle, 0x101F, IntPtr.Zero, IntPtr.Zero);
                    if (hdr != IntPtr.Zero) Native.SetWindowTheme(hdr, "DarkMode_ItemsView", null);
                };
                EventHandler fit = delegate
                {
                    int sum = 0; for (int i = 0; i < lv2.Columns.Count - 1; i++) sum += lv2.Columns[i].Width;
                    int want = Math.Max(80, lv2.ClientSize.Width - sum - 2);
                    if (lv2.Columns[lv2.Columns.Count - 1].Width != want) lv2.Columns[lv2.Columns.Count - 1].Width = want;
                };
                lv2.Resize += fit; lv2.HandleCreated += fit;
                body.Controls.Add(lv2);

                // ---- 详情卡片 ----
                int fy = detTop + 150;   // 设置那一行的位置
                body.Paint += (s, e) =>
                {
                    Graphics g = e.Graphics;
                    Txt(g, "看门猫每 10 分钟检查一次：程序超过上限时，「自动处理」在你不用电脑时重启它，处理完核对是否恢复；「只提醒」只告诉你。新加监控：在主窗口右键程序。",
                        fCN, cGray, 22, 40, w - 44, 22, tfL | TextFormatFlags.EndEllipsis);
                    g.FillRectangle(bDeep, 14, detTop, w - 28, h - detTop - 70);
                    g.FillRectangle(bAcc, 14, detTop, 3, h - detTop - 70);
                    if (cur == null) { Txt(g, "在上面选一条规则，查看它为什么会涨、怎么处理，并修改设置。", fCN, cGray, 32, detTop + 16, w - 64, 20); return; }
                    Txt(g, cur.Title, fCNbig, cCyan, 32, detTop + 12, w - 260, 26);
                    Txt(g, cur.Source == "公共" ? "公共规则" : cur.Source == "改过" ? "公共规则 · 你改过" : "你自己的规则", fCNb, cAccent, w - 240, detTop + 16, 210, 20, tfR);
                    Txt(g, "为什么会涨", fCNb, cAccent, 32, detTop + 46, 120, 18);
                    Txt(g, cur.Cause, fCN, cText, 150, detTop + 46, w - 190, 40, tfW);
                    Txt(g, "怎么处理", fCNb, cAccent, 32, detTop + 92, 120, 18);
                    Txt(g, (cur.FixLabel.Length > 0 ? cur.FixLabel : "-") + "    ·    处理后核对：" + VerifyText(cur), fCN, cText, 150, detTop + 92, w - 190, 40, tfW);
                    g.FillRectangle(bDimLine, 32, fy - 14, w - 64, 1);
                    Txt(g, "上限（MB）", fCN, cGray, 32, fy, 150, 18);
                    Txt(g, "空闲多少分钟才动手（0 = 随时）", fCN, cGray, 220, fy, 260, 18);
                    Txt(g, "超过上限时", fCN, cGray, 520, fy, 200, 18);
                    Txt(g, "这条规则", fCN, cGray, 800, fy, 200, 18);
                };
                TextBox tbMB = NewBox(body, 32, fy + 22, 160, ""), tbIdle = NewBox(body, 220, fy + 22, 200, "");
                // 两组二选一按钮：选中的那个实心高亮
                Button bAutoY = NewBtn(body, "自动处理", 520, fy + 21, 120, null, false), bAutoN = NewBtn(body, "只提醒", 646, fy + 21, 120, null, false);
                Button bOnY = NewBtn(body, "启用", 800, fy + 21, 110, null, false), bOnN = NewBtn(body, "关闭", 916, fy + 21, 110, null, false);
                Action<Button, bool, Color> seg = (b, on, c) => { b.BackColor = on ? Color.FromArgb(70, c) : cPanel; b.ForeColor = on ? c : cGray; b.FlatAppearance.BorderColor = on ? c : cRedDim; };
                Action showToggles = delegate
                {
                    seg(bAutoY, editAuto, cCyan); seg(bAutoN, !editAuto, cYellow);
                    seg(bOnY, editOn, cCyan); seg(bOnN, !editOn, cGray);
                };
                bAutoY.Click += delegate { editAuto = true; showToggles(); };
                bAutoN.Click += delegate { editAuto = false; showToggles(); };
                bOnY.Click += delegate { editOn = true; showToggles(); };
                bOnN.Click += delegate { editOn = false; showToggles(); };
                Button bSave = NewBtn(body, "保存修改", 22, h - 52, 150, null, true);
                Button bReset = NewBtn(body, "恢复默认", 180, h - 52, 130, null, false);
                NewBtn(body, "用记事本编辑（高级）", w - 22 - 180 - 108, h - 52, 180, delegate { EditRules(); }, false);
                NewBtn(body, "关闭", w - 22 - 100, h - 52, 100, delegate { f.Close(); }, false);
                Control[] editors = { tbMB, tbIdle, bAutoY, bAutoN, bOnY, bOnN, bSave, bReset };

                Action fill = delegate
                {
                    string keep = cur != null ? cur.Id : null;
                    lv2.BeginUpdate(); lv2.Items.Clear();
                    foreach (Rule r in allRules)
                    {
                        ListViewItem it = new ListViewItem(new[] { r.Title, "", "", "", "" });
                        it.Tag = r;
                        lv2.Items.Add(it);
                        if (r.Id == keep) { it.Selected = true; cur = r; }
                    }
                    lv2.EndUpdate();
                };
                Action select = delegate
                {
                    foreach (Control c in editors) c.Visible = cur != null;
                    if (cur == null) { body.Invalidate(); return; }
                    tbMB.Text = (cur.MinMB > 0 ? cur.MinMB : cur.MinFileMB).ToString("0");
                    tbMB.Enabled = cur.MinMB > 0 || cur.MinFileMB > 0;
                    tbIdle.Text = cur.IdleMin.ToString("0");
                    editAuto = cur.Auto && cur.FixType != "none"; editOn = !cur.Disabled;
                    bAutoY.Enabled = cur.FixType != "none";
                    showToggles();
                    bReset.Text = cur.Source == "我的" ? "删除这条规则" : "恢复默认";
                    bReset.Enabled = cur.Source != "公共";
                    body.Invalidate();
                };
                lv2.SelectedIndexChanged += delegate { cur = lv2.SelectedItems.Count > 0 ? lv2.SelectedItems[0].Tag as Rule : null; select(); };
                bSave.Click += delegate
                {
                    if (cur == null) return;
                    double mb, idle;
                    if (!double.TryParse(tbMB.Text.Trim(), out mb) || mb < 0) { Say("上限请填一个数字（MB）。"); return; }
                    if (!double.TryParse(tbIdle.Text.Trim(), out idle) || idle < 0) { Say("空闲时间请填一个数字（分钟），0 表示随时。"); return; }
                    try { SaveRuleEdit(cur, mb, idle, editAuto, !editOn); } catch (Exception ex) { Say("保存失败：" + ex.Message); return; }
                    log = ReadActionLog(); fill(); select();
                    Say("已保存。看门猫下次检查（最多 10 分钟）就会按新的设置来。");
                };
                bReset.Click += delegate
                {
                    if (cur == null || cur.Source == "公共") return;
                    string what = cur.Source == "我的" ? "删除你自己的规则「" + cur.Title + "」" : "把「" + cur.Title + "」恢复成默认设置（去掉你的修改）";
                    if (!Ask("将" + what + "。确定吗？")) return;
                    string id = cur.Id;
                    try { WriteLocalRules(ReadLocalRules().Where(d => Json.Str(d, "id") != id).ToList()); } catch (Exception ex) { Say(ex.Message); return; }
                    if (cur.Source == "我的") cur = null;
                    log = ReadActionLog(); fill(); select();
                };
                fill();
                pick = pick ?? snapPick;
                if (pick != null) foreach (ListViewItem it in lv2.Items) if (((Rule)it.Tag).Id == pick) { it.Selected = true; it.EnsureVisible(); cur = (Rule)it.Tag; }
                select();
                f.ShowDialog(this);
            }
            if (!selfTest) RefreshAll();
        }
        void g_Line(Graphics g, int x, int y, int w) { g.FillRectangle(bDimLine, x, y, w, 1); }

        // 规则窗口的修改：存成 rules.local.json 里同 id 的一条（公共规则就整条复制过来再改），不动 rules.json
        void SaveRuleEdit(Rule r, double limit, double idle, bool auto, bool disabled)
        {
            List<Dictionary<string, object>> local = ReadLocalRules();
            Dictionary<string, object> d = local.FirstOrDefault(x => Json.Str(x, "id") == r.Id);
            if (d == null)
            {
                d = Json.Objects(Json.Parse(Json.Write(r.Raw))).FirstOrDefault() ?? new Dictionary<string, object>();
                local.Add(d);
            }
            Dictionary<string, object> when = Json.Obj(d, "when");
            if (when == null) { when = new Dictionary<string, object>(); d["when"] = when; }
            if (when.ContainsKey("minMB") || (!when.ContainsKey("minFileMB") && r.MinMB > 0)) when["minMB"] = limit;
            else if (when.ContainsKey("minFileMB")) when["minFileMB"] = limit;
            if (idle > 0) d["idleMin"] = idle; else d.Remove("idleMin");
            d["auto"] = auto;
            if (disabled) d["disabled"] = true; else d.Remove("disabled");
            WriteLocalRules(local);
        }

        // ---------- 自动修复记录 ----------
        void ShowFixLogWindow()
        {
            const int w = 1100, h = 600;
            List<string[]> log = ReadActionLog();
            Dictionary<string, string> titles = new Dictionary<string, string>();
            foreach (Rule r in allRules) if (r.Id.Length > 0) titles[r.Id] = r.Title;
            BufferedPanel body;
            using (Form f = NewWindow("FIX LOG", "自动修复记录", w, h, out body))
            {
                body.Paint += (s, e) => Txt(e.Graphics, "看门猫做过的事（最新的在上面）。\"处理完成\"一行写着处理前 → 后的内存和核对结果；句柄现场记录 / 内存分析可以双击打开。",
                    fCN, cGray, 22, 40, w - 44, 20);
                BufferedListView l = NewList(body, new Rectangle(14, 66, w - 28, h - 66 - 62),
                    new object[,] { { "时间", 150 }, { "做了什么", 110 }, { "规则 / 程序", 260 }, { "内存 前 → 后", 150 }, { "结果", 300 } },
                    (it, col) =>
                    {
                        string res = it.SubItems[4].Text;
                        if (col == 4) return res.StartsWith("核对通过") ? cCyan : res.StartsWith("核对失败") ? LevelColor(2) : res.StartsWith("补救") || res.StartsWith("在运行") ? cYellow : cGray;
                        if (col == 1) return it.SubItems[1].Text == "处理完成" ? cCyan : cAccent;
                        return col == 2 ? cText : cGray;
                    });
                List<ListViewItem> items = new List<ListViewItem>();
                foreach (string[] e in log)
                {
                    string act, who = "", mem = "", res = "", file = null;
                    string rest = e[2];
                    int colon = rest.IndexOf(':');
                    string id = colon > 0 ? rest.Substring(0, colon) : "";
                    string title; string name = titles.TryGetValue(id, out title) ? title : id.StartsWith("restart-") ? "手动重启 " + id.Substring(8) : id;
                    switch (e[1])
                    {
                        case "DONE":
                            act = "处理完成"; who = name;
                            Match m = Regex.Match(rest, @":\s*(\S+) MB -> (\S+) MB, (.*)$");
                            if (m.Success) { mem = m.Groups[1].Value + " → " + m.Groups[2].Value + " MB"; res = CheckText(m.Groups[3].Value); }
                            break;
                        case "FIX": act = "开始处理"; who = name; Match mf = Regex.Match(rest, @"-> (.*)$"); res = mf.Success ? mf.Groups[1].Value : ""; break;
                        case "NOTICE": act = "提醒"; who = name; res = rest.Substring(Math.Min(rest.Length, colon + 1)).Trim(); break;
                        case "CAPTURE":
                        case "ANALYZE":
                            act = e[1] == "CAPTURE" ? "记录句柄现场" : "内存分析";
                            Match mc = Regex.Match(rest, @"^(\S+)( #\d+)?.*-> (data\\incidents\\\S+)$");
                            who = mc.Success ? mc.Groups[1].Value : rest; res = "双击打开记录";
                            if (mc.Success) file = Path.Combine(ToolDir, mc.Groups[3].Value);
                            break;
                        default: act = "处理"; who = rest; break;
                    }
                    ListViewItem it = new ListViewItem(new[] { e[0], act, who, mem, res });
                    it.Tag = file;
                    items.Add(it);
                }
                items.Reverse();
                l.Items.AddRange(items.Take(300).ToArray());
                l.DoubleClick += delegate { if (l.SelectedItems.Count > 0) { string fp = l.SelectedItems[0].Tag as string; if (fp != null && File.Exists(fp)) StartShell("notepad.exe", "\"" + fp + "\""); } };
                if (items.Count == 0) l.Items.Add(new ListViewItem(new[] { "（还没有记录）", "", "看门猫只在程序超过上限、而且你空闲 10 分钟以上时才处理", "", "" }));
                NewBtn(body, "打开日志文件", 22, h - 52, 150, delegate { if (File.Exists(LogFile)) StartShell("notepad.exe", "\"" + LogFile + "\""); }, false);
                NewBtn(body, "规则", 180, h - 52, 100, delegate { f.Close(); ShowRulesWindow(null); }, false);
                NewBtn(body, "关闭", w - 22 - 100, h - 52, 100, delegate { f.Close(); }, false);
                f.ShowDialog(this);
            }
        }

        // ---------- 已忽略的问题 ----------
        void ShowIgnoredWindow()
        {
            const int w = 820, h = 480;
            BufferedPanel body;
            using (Form f = NewWindow("IGNORED", "已忽略的问题", w, h, out body))
            {
                body.Paint += (s, e) => Txt(e.Graphics, "这些问题现在不会提醒；规则发现的问题，看门猫也不会自动处理。选中一条点\"恢复提醒\"。", fCN, cGray, 22, 40, w - 44, 20);
                BufferedListView l = NewList(body, new Rectangle(14, 66, w - 28, h - 66 - 62),
                    new object[,] { { "问题", 420 }, { "状态", 200 } },
                    (it, col) => it.Tag == null ? cGray : col == 1 ? (it.SubItems[1].Text.StartsWith("已确认") ? cCyan : cYellow) : cText);
                Action fill = delegate
                {
                    l.Items.Clear();
                    foreach (IgnoreEntry i in ActiveIgnores())
                    {
                        ListViewItem it = new ListViewItem(new[] { i.Label, i.Type == "normal" ? "已确认正常（不再提醒）" : "忽略到 " + UntilOf(i).ToString("MM-dd HH:mm") });
                        it.Tag = i.Key; l.Items.Add(it);
                    }
                    if (l.Items.Count == 0) l.Items.Add(new ListViewItem(new[] { "（现在没有被忽略或确认正常的问题）", "" }));
                };
                fill();
                NewBtn(body, "恢复提醒", 22, h - 52, 130, delegate
                {
                    if (l.SelectedItems.Count == 0 || !(l.SelectedItems[0].Tag is string)) return;
                    ignores.Remove((string)l.SelectedItems[0].Tag); SaveIgnores(); fill(); RefreshAll();
                }, true);
                NewBtn(body, "全部恢复", 160, h - 52, 130, delegate
                {
                    if (ActiveIgnores().Count == 0 || !Ask("全部恢复提醒？")) return;
                    ignores.Clear(); SaveIgnores(); fill(); RefreshAll();
                }, false);
                NewBtn(body, "关闭", w - 22 - 100, h - 52, 100, delegate { f.Close(); }, false);
                f.ShowDialog(this);
            }
        }
    }
}
