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
            f.Text = "WATCHDOG // " + title; f.ClientSize = new Size(w, h);
            f.StartPosition = Visible && WindowState != FormWindowState.Minimized ? FormStartPosition.CenterParent : FormStartPosition.CenterScreen;
            f.FormBorderStyle = FormBorderStyle.FixedSingle; f.MaximizeBox = false; f.MinimizeBox = false; f.ShowInTaskbar = false;
            f.BackColor = cBg; f.ForeColor = cText; f.Font = fCN; f.KeyPreview = true;
            if (formIcon != null) f.Icon = formIcon;
            f.KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) f.Close(); };
            f.HandleCreated += delegate
            {
                Native.Dwm(f.Handle, 20, 1); Native.Dwm(f.Handle, 35, 0x000C090E); Native.Dwm(f.Handle, 36, 0x00575FFF); Native.Dwm(f.Handle, 34, 0x00575FFF);
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
                case "rules": ShowRulesWindow(null); break;
                case "fixes": ShowFixesWindow(); break;
                case "fixlog": ShowFixLogWindow(); break;
                case "ignored": ShowIgnoredWindow(); break;
                case "settings": ShowSettingsWindow(); break;
                case "wizard": ShowRuleWizard(pick ?? "lghub_agent"); break;
                case "ask": Ask("将结束 lghub_agent（1 个进程）并重新打开：\nC:\\Program Files\\LGHUB\\lghub_agent.exe\n\n这个程序里没保存的内容可能会丢失。确定吗？"); break;
                case "prompt": Prompt("监控这个程序", "让看门狗监控 CorsairCpuIdService（现在 52 MB）。\n\n内存超过多少 MB 时提醒你？", "500"); break;
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
            t.BackColor = Color.FromArgb(22, 10, 14); t.ForeColor = cCyan; t.BorderStyle = BorderStyle.FixedSingle; t.Font = fCNb;
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
        // 看门狗日志里的英文核对结果，翻成一句中文
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
        // 某条规则最近一次的动作（来自看门狗日志）
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

        void ShowRulesWindow(string pick)   // pick = 打开时选中的规则 id
        {
            rulesSig = ""; LoadRules();
            const int w = 1100, h = 750;
            BufferedPanel body;
            using (Form f = NewWindow("RULES", "看门狗的规则", w, h, out body))
            {
                List<string[]> log = ReadActionLog();
                Rule cur = null;
                body.Paint += (s, e) =>
                {
                    Txt(e.Graphics, "看门狗每 10 分钟在后台检查一次：程序超过上限时，按下面的规则处理。\"自动处理\"的规则只在你空闲时动手，处理完会核对是不是都恢复了。改动存在你自己的 rules.local.json，不改 rules.json。新加监控：右键程序 → 用看门狗监控。",
                        fCN, cGray, 22, 40, w - 44, 36, tfW);
                    if (cur == null) { Txt(e.Graphics, "在上面选一条规则查看和修改。", fCN, cGray, 22, 396, w - 44, 20); return; }
                    Txt(e.Graphics, cur.Title, fCNbig, cCyan, 22, 392, w - 200, 24);
                    Txt(e.Graphics, cur.Source == "公共" ? "公共规则" : cur.Source == "改过" ? "公共规则 · 你改过" : "你自己的规则", fCN, cAccent, w - 200, 396, 178, 20, tfR);
                    Txt(e.Graphics, cur.Cause, fCN, cText, 22, 420, w - 44, 56, tfW);
                    Txt(e.Graphics, "怎么处理：" + (cur.FixLabel.Length > 0 ? cur.FixLabel : "-") + "        处理后核对：" + VerifyText(cur), fCN, cGray, 22, 480, w - 44, 40, tfW);
                    g_Line(e.Graphics, 22, 528, w - 44);
                    Txt(e.Graphics, "超过多少 MB 时处理", fCN, cGray, 22, 540, 200, 18);
                    Txt(e.Graphics, "空闲多少分钟才动手（0 = 随时）", fCN, cGray, 240, 540, 240, 18);
                    Txt(e.Graphics, "超过上限时", fCN, cGray, 500, 540, 200, 18);
                    Txt(e.Graphics, "这条规则", fCN, cGray, 720, 540, 200, 18);
                };
                BufferedListView lv2 = NewList(body, new Rectangle(14, 82, w - 28, 296),
                    new object[,] { { "状态", 84 }, { "规则", 230 }, { "程序", 170 }, { "什么时候处理", 150 }, { "空闲才动手", 150 }, { "来源", 64 }, { "最近一次", 200 } },
                    (it, col) =>
                    {
                        Rule r = it.Tag as Rule;
                        if (col == 0) return r != null && r.Disabled ? cGray : it.SubItems[0].Text == "自动处理" ? cCyan : cYellow;
                        if (col == 6) return it.SubItems[6].Text.Contains("失败") ? LevelColor(2) : cGray;
                        return col == 1 ? cText : cGray;
                    });
                TextBox tbMB = NewBox(body, 22, 562, 190, ""), tbIdle = NewBox(body, 240, 562, 230, "");
                bool editAuto = false, editOn = true;
                Button bAuto = NewBtn(body, "", 500, 561, 200, null, false), bOn = NewBtn(body, "", 720, 561, 200, null, false);
                Action showToggles = delegate
                {
                    bAuto.Text = editAuto ? "● 自动处理" : "○ 只提醒，不动手"; bAuto.ForeColor = editAuto ? cCyan : cYellow;
                    bOn.Text = editOn ? "● 启用" : "○ 已关闭"; bOn.ForeColor = editOn ? cCyan : cGray;
                };
                bAuto.Click += delegate { editAuto = !editAuto; showToggles(); };
                bOn.Click += delegate { editOn = !editOn; showToggles(); };
                Button bSave = NewBtn(body, "保存修改", 22, h - 52, 150, null, true);
                Button bReset = NewBtn(body, "恢复默认", 180, h - 52, 130, null, false);
                NewBtn(body, "用记事本编辑（高级）", w - 22 - 180 - 108, h - 52, 180, delegate { EditRules(); }, false);
                NewBtn(body, "关闭", w - 22 - 100, h - 52, 100, delegate { f.Close(); }, false);
                Control[] editors = { tbMB, tbIdle, bAuto, bOn, bSave, bReset };

                Action fill = delegate
                {
                    string keep = cur != null ? cur.Id : null;
                    lv2.BeginUpdate(); lv2.Items.Clear();
                    foreach (Rule r in allRules)
                    {
                        ListViewItem it = new ListViewItem(new[] { RuleState(r), r.Title, r.Target ?? r.Process ?? "-", RuleWhen(r), RuleIdle(r), r.Source, LastActionOf(r.Id, log) });
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
                    bAuto.Enabled = cur.FixType != "none";
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
                    Say("已保存。看门狗下次检查（最多 10 分钟）就会按新的设置来。");
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
                body.Paint += (s, e) => Txt(e.Graphics, "看门狗做过的事（最新的在上面）。\"处理完成\"一行写着处理前 → 后的内存和核对结果；句柄现场记录 / 内存分析可以双击打开。",
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
                if (items.Count == 0) l.Items.Add(new ListViewItem(new[] { "（还没有记录）", "", "看门狗只在程序超过上限、而且你空闲 10 分钟以上时才处理", "", "" }));
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
                body.Paint += (s, e) => Txt(e.Graphics, "这些问题现在不会提醒；规则发现的问题，看门狗也不会自动处理。选中一条点\"恢复提醒\"。", fCN, cGray, 22, 40, w - 44, 20);
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
