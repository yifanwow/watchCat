// 主题：整套配色。默认「来生」（红黑 + 荧光蓝），另有「摩羯」（暗红 + 绯红，荧光绿点缀）、「旧梦」（黑紫 + 霓虹粉 + 荧光绿）
// 选择存在 data\settings.json 的 "theme"；在设置里切换，立即生效（重建画刷、给已经打开的窗口换色），不用重启
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

namespace WatchDog
{
    class Palette
    {
        public string Id, Name, Desc, OkName;   // OkName：正常色叫什么（界面上"蓝 正常"这类说明用）
        public Color Bg, Panel, RowA, RowB, Deep,            // 底色、面板、列表两种行底、最深的底（看门狗小面板、输入区）
            Ok, Warn, Bad,                                   // 三个等级：正常 / 留意 / 出问题（也是图表、徽章、开关的颜色）
            Accent, AccentDim, Text, Gray, Dim, Haz,         // 强调色（边框、标题）、暗强调（分隔线）、文字、次要文字、暗块、警示条
            Sel, SelDown, Row1, Row2,                        // 选中 / 按下、"留意"和"出问题"行的底色
            Primary, PrimaryHot, Tile, Feat, FeatHot,        // 主按钮、操作区格子、专项修复大格子
            Input, Off, Disabled, Cause, MenuBg, MenuSel;    // 输入框、关着的开关、不可用的文字、规则说明文字、右键菜单
    }

    partial class Monitor
    {
        internal static readonly Palette[] Themes =
        {
            new Palette
            {
                Id = "afterlife", Name = "来生", Desc = "红黑底 · 珊瑚红边框 · 荧光蓝", OkName = "蓝",
                Bg = Color.FromArgb(14, 9, 12), Panel = Color.FromArgb(32, 13, 17), RowA = Color.FromArgb(19, 10, 13), RowB = Color.FromArgb(26, 12, 16), Deep = Color.FromArgb(9, 6, 8),
                Ok = Color.FromArgb(94, 246, 255), Warn = Color.FromArgb(245, 217, 10), Bad = Color.FromArgb(255, 40, 60),
                Accent = Color.FromArgb(255, 95, 87), AccentDim = Color.FromArgb(92, 30, 33), Text = Color.FromArgb(236, 220, 214), Gray = Color.FromArgb(160, 116, 112),
                Dim = Color.FromArgb(58, 24, 28), Haz = Color.FromArgb(245, 217, 10),
                Sel = Color.FromArgb(12, 46, 54), SelDown = Color.FromArgb(20, 70, 82), Row1 = Color.FromArgb(48, 40, 8), Row2 = Color.FromArgb(78, 14, 22),
                Primary = Color.FromArgb(70, 18, 24), PrimaryHot = Color.FromArgb(104, 26, 34), Tile = Color.FromArgb(22, 10, 13), Feat = Color.FromArgb(14, 34, 40), FeatHot = Color.FromArgb(18, 58, 68),
                Input = Color.FromArgb(22, 10, 14), Off = Color.FromArgb(52, 24, 28), Disabled = Color.FromArgb(100, 72, 72), Cause = Color.FromArgb(200, 180, 172),
                MenuBg = Color.FromArgb(24, 11, 14), MenuSel = Color.FromArgb(70, 20, 26),
            },
            new Palette
            {
                Id = "capricorn", Name = "摩羯", Desc = "暗红底 · 绯红边框 · 荧光绿点缀", OkName = "绿",
                Bg = Color.FromArgb(15, 6, 8), Panel = Color.FromArgb(40, 10, 14), RowA = Color.FromArgb(21, 7, 9), RowB = Color.FromArgb(30, 9, 12), Deep = Color.FromArgb(9, 4, 5),
                Ok = Color.FromArgb(72, 255, 134), Warn = Color.FromArgb(245, 217, 10), Bad = Color.FromArgb(255, 40, 60),
                Accent = Color.FromArgb(255, 38, 58), AccentDim = Color.FromArgb(112, 20, 28), Text = Color.FromArgb(242, 222, 222), Gray = Color.FromArgb(176, 112, 112),
                Dim = Color.FromArgb(68, 18, 22), Haz = Color.FromArgb(255, 38, 58),
                Sel = Color.FromArgb(14, 58, 32), SelDown = Color.FromArgb(20, 86, 48), Row1 = Color.FromArgb(48, 40, 8), Row2 = Color.FromArgb(84, 12, 20),
                Primary = Color.FromArgb(96, 14, 22), PrimaryHot = Color.FromArgb(132, 20, 30), Tile = Color.FromArgb(27, 8, 11), Feat = Color.FromArgb(12, 40, 24), FeatHot = Color.FromArgb(16, 64, 38),
                Input = Color.FromArgb(25, 8, 11), Off = Color.FromArgb(62, 18, 22), Disabled = Color.FromArgb(112, 70, 72), Cause = Color.FromArgb(208, 180, 178),
                MenuBg = Color.FromArgb(27, 8, 11), MenuSel = Color.FromArgb(88, 14, 22),
            },
            new Palette
            {
                Id = "olddream", Name = "旧梦", Desc = "黑紫底 · 霓虹粉边框 · 荧光绿", OkName = "绿",
                Bg = Color.FromArgb(13, 8, 16), Panel = Color.FromArgb(30, 12, 33), RowA = Color.FromArgb(18, 9, 22), RowB = Color.FromArgb(25, 11, 30), Deep = Color.FromArgb(8, 5, 11),
                Ok = Color.FromArgb(96, 255, 166), Warn = Color.FromArgb(245, 217, 10), Bad = Color.FromArgb(255, 48, 64),
                Accent = Color.FromArgb(255, 84, 196), AccentDim = Color.FromArgb(86, 26, 74), Text = Color.FromArgb(242, 226, 240), Gray = Color.FromArgb(168, 124, 162),
                Dim = Color.FromArgb(56, 22, 54), Haz = Color.FromArgb(255, 84, 196),
                Sel = Color.FromArgb(12, 54, 38), SelDown = Color.FromArgb(18, 82, 56), Row1 = Color.FromArgb(48, 40, 8), Row2 = Color.FromArgb(80, 14, 30),
                Primary = Color.FromArgb(74, 16, 62), PrimaryHot = Color.FromArgb(108, 24, 90), Tile = Color.FromArgb(21, 10, 25), Feat = Color.FromArgb(12, 40, 30), FeatHot = Color.FromArgb(16, 64, 46),
                Input = Color.FromArgb(22, 10, 26), Off = Color.FromArgb(50, 22, 50), Disabled = Color.FromArgb(100, 70, 98), Cause = Color.FromArgb(206, 184, 202),
                MenuBg = Color.FromArgb(24, 10, 28), MenuSel = Color.FromArgb(74, 18, 64),
            },
        };
        internal static Palette Theme = Themes[0];

        static Color cBg, cPanel, cRowA, cRowB, cCyan, cYellow, cRed, cAccent, cRedDim, cText, cGray, cDim,
            cDeep, cHaz, cSel, cSelDown, cRow1, cRow2, cPrimary, cPrimaryHot, cTile, cFeat, cFeatHot, cInput, cOff, cDisabled, cCause;

        static void SetPalette(Palette p)
        {
            Theme = p;
            cBg = p.Bg; cPanel = p.Panel; cRowA = p.RowA; cRowB = p.RowB; cDeep = p.Deep;
            cCyan = p.Ok; cYellow = p.Warn; cRed = p.Bad;
            cAccent = p.Accent; cRedDim = p.AccentDim; cText = p.Text; cGray = p.Gray; cDim = p.Dim; cHaz = p.Haz;
            cSel = p.Sel; cSelDown = p.SelDown; cRow1 = p.Row1; cRow2 = p.Row2;
            cPrimary = p.Primary; cPrimaryHot = p.PrimaryHot; cTile = p.Tile; cFeat = p.Feat; cFeatHot = p.FeatHot;
            cInput = p.Input; cOff = p.Off; cDisabled = p.Disabled; cCause = p.Cause;
        }
        static Palette FindTheme(string id) { return Themes.FirstOrDefault(t => t.Id == id) ?? Themes[0]; }
        internal void PreviewTheme(string id) { SwitchTheme(id, false); }   // 自测截图用，不改设置
        void LoadTheme() { SetPalette(FindTheme(Json.Str(ReadSettings(), "theme"))); }

        // 两种颜色按比例混合（t = 0 全是 a，1 全是 b）
        static Color Mix(Color a, Color b, double t)
        {
            return Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
        }
        static int ColorRef(Color c) { return c.R | (c.G << 8) | (c.B << 16); }
        // Windows 11 标题栏：底色、标题文字、窗口边框跟着主题
        static void ThemeTitleBar(IntPtr h)
        {
            Native.Dwm(h, 20, 1);
            Native.Dwm(h, 35, ColorRef(Theme.Bg));
            Native.Dwm(h, 36, ColorRef(Theme.Accent));
            Native.Dwm(h, 34, ColorRef(Theme.Accent));
        }

        // 切换主题：重建画刷，旧颜色一一换成新颜色（按钮、列表、已经打开的窗口、操作区的小字），再整体重画
        void SwitchTheme(string id, bool save = true)
        {
            Palette old = Theme, now = FindTheme(id);
            if (save) WriteSetting("theme", now.Id);
            if (old == now) return;
            Dictionary<int, Color> map = new Dictionary<int, Color>();
            foreach (FieldInfo fi in typeof(Palette).GetFields().Where(f => f.FieldType == typeof(Color)))
            {
                int k = ((Color)fi.GetValue(old)).ToArgb();
                if (!map.ContainsKey(k)) map[k] = (Color)fi.GetValue(now);
            }
            Func<Color, Color> M = c => { Color n; return map.TryGetValue(c.ToArgb(), out n) ? n : c; };

            SetPalette(now);
            foreach (IDisposable o in gdi.Where(o => !(o is Font)).ToList()) { o.Dispose(); gdi.Remove(o); }
            shieldBmp.Clear();
            CreateTheme();
            CreateActionBrushes();
            foreach (ActTile t in actTiles) { t.SubColor = M(t.SubColor); t.IconColor = M(t.IconColor); }
            actLastFixColor = M(actLastFixColor);

            foreach (Form f in Application.OpenForms.Cast<Form>().ToList())
            {
                Recolor(f, M);
                if (f.IsHandleCreated) ThemeTitleBar(f.Handle);
                f.Invalidate(true);
            }
            if (menu != null) RecolorMenu(menu, M);
            if (trayMenu != null) RecolorMenu(trayMenu, M);
        }
        static void Recolor(Control c, Func<Color, Color> M)
        {
            c.BackColor = M(c.BackColor); c.ForeColor = M(c.ForeColor);
            Button b = c as Button;
            if (b != null && b.FlatStyle == FlatStyle.Flat)
            {
                b.FlatAppearance.BorderColor = M(b.FlatAppearance.BorderColor);
                b.FlatAppearance.MouseOverBackColor = M(b.FlatAppearance.MouseOverBackColor);
                b.FlatAppearance.MouseDownBackColor = M(b.FlatAppearance.MouseDownBackColor);
            }
            foreach (Control k in c.Controls) Recolor(k, M);
        }
        static void RecolorMenu(ToolStrip m, Func<Color, Color> M)
        {
            m.ForeColor = M(m.ForeColor);
            foreach (ToolStripItem i in m.Items)
            {
                i.ForeColor = M(i.ForeColor);
                ToolStripMenuItem mi = i as ToolStripMenuItem;
                if (mi != null && mi.HasDropDownItems) RecolorMenu(mi.DropDown, M);
            }
            m.Invalidate();
        }
    }
}
