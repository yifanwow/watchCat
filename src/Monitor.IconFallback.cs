// 兜底图标：程序自己没有图标（exe 里没放图标、拿不到路径、svchost 里的服务……）时，不再画一个灰方块，而是：
// 1. 商店应用（WindowsApps / SystemApps 里的，exe 本身没图标）：从应用包的 AppxManifest.xml 找它的小方块标志；
// 2. 其余按名字 / 说明 / 路径猜类别，画一个类别图标字（安全盾牌、喇叭、显示器、键盘、灯效、网络、下载、诊断、游戏、命令行、系统、齿轮）；
// 3. 什么都猜不出来：画程序名的首字母。
// 兜底图标跟着主题配色：切换主题时丢掉重画。
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace WatchDog
{
    partial class Monitor
    {
        HashSet<string> fallbackIcons = new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // icons 里哪些是兜底画出来的（切换主题时重画）

        // 类别：关键字（名字 / 说明 / 路径里出现任意一个，不分大小写）→ 图标字。从上往下，先匹配先用
        static readonly KeyValuePair<string[], string>[] IconKinds = {
            Kind("", "lsass", "lsaiso", "msmpeng", "nissrv", "defender", "securityhealth", "smartscreen", "antimalware", "cert", "安全", "杀毒"),
            Kind("", "audio", "audiodg", "apo", "sound", "midi", "rtkaud", "dts", "音频", "声音"),
            Kind("", "dwm", "nvdisplay", "nvcontainer", "nvsp", "nvidia", "amdrs", "radeon", "display", "gpu", "显示", "显卡"),
            Kind("", "ctfmon", "chsime", "imebroker", "tabtip", "textinput", "haptic", "hid", "keyboard", "输入法", "键盘"),
            Kind("", "lighting", "rgb", "aura", "icue", "corsair", "hue", "lamparray", "razer", "lghub", "logi_", "armoury", "灯效", "外设"),
            Kind("", "game", "steam", "xbox", "gaming", "epic", "battle.net", "游戏"),
            Kind("", "wlan", "wifi", "network", "snmp", "ping", "dashost", "socket", "网络"),
            Kind("", "update", "updater", "download", "install", "migration", "packagemanager", "idm", "更新", "下载"),
            Kind("", "crash", "diagnostic", "debug", "werfault", "report", "诊断", "崩溃"),
        };
        static KeyValuePair<string[], string> Kind(string glyph, params string[] keys) { return new KeyValuePair<string[], string>(keys, glyph); }
        // Windows 自己的核心进程
        static readonly HashSet<string> CoreNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            "System", "Idle", "Registry", "smss", "csrss", "wininit", "winlogon", "services", "Memory Compression", "MemCompression", "Secure System",
            "fontdrvhost", "sihost", "RuntimeBroker", "dllhost", "taskhostw", "svchost", "explorer", "ShellHost", "ShellExperienceHost",
            "StartMenuExperienceHost", "SearchHost", "SearchIndexer", "ApplicationFrameHost", "backgroundTaskHost", "rundll32", "spoolsv", "unsecapp", "WmiPrvSE", "WUDFHost" };

        // 这个 exe 自己带图标吗（没带的话 ExtractAssociatedIcon 只会给一个千篇一律的"程序"图标）
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern uint ExtractIconEx(string file, int index, IntPtr[] large, IntPtr[] small, uint n);
        static bool HasOwnIcon(string path) { try { return ExtractIconEx(path, -1, null, null, 0) > 0; } catch { return true; } }

        // 命令行程序：PE 头里的子系统 = 3（WINDOWS_CUI）
        static bool IsConsoleExe(string path)
        {
            if (path == null) return false;
            try
            {
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (BinaryReader br = new BinaryReader(fs))
                {
                    if (fs.Length < 0x100) return false;
                    fs.Position = 0x3C; int pe = br.ReadInt32();
                    if (pe <= 0 || pe + 24 + 70 > fs.Length) return false;
                    fs.Position = pe; if (br.ReadUInt32() != 0x4550) return false;   // "PE\0\0"
                    fs.Position = pe + 24 + 68; return br.ReadUInt16() == 3;          // 可选头里的 Subsystem（32 / 64 位同一位置）
                }
            }
            catch { return false; }
        }

        // 商店应用的标志：exe 往上找 AppxManifest.xml → Square44x44Logo（或 Logo）→ 挑最接近 32 像素的那张图
        static Bitmap AppxLogo(string exe)
        {
            try
            {
                string dir = Path.GetDirectoryName(exe);
                for (int up = 0; up < 3 && dir != null; up++, dir = Path.GetDirectoryName(dir))
                {
                    string man = Path.Combine(dir, "AppxManifest.xml");
                    if (!File.Exists(man)) continue;
                    string xml = File.ReadAllText(man);
                    Match m = Regex.Match(xml, "Square44x44Logo=\"([^\"]+)\"");
                    if (!m.Success) m = Regex.Match(xml, "<Logo>([^<]+)</Logo>");
                    if (!m.Success) return null;
                    string rel = m.Groups[1].Value.Replace('/', '\\');
                    string folder = Path.Combine(dir, Path.GetDirectoryName(rel) ?? "");
                    string stem = Path.GetFileNameWithoutExtension(rel);
                    if (!Directory.Exists(folder)) return null;
                    string[] files = Directory.GetFiles(folder, stem + "*.png");
                    if (files.Length == 0) return null;
                    // 优先：不带底板的 32 / 48 像素版，再是任意 targetsize，再是 scale-100 / 200，最后随便一张；不要高对比度版
                    string pick = files.Where(f => f.IndexOf("contrast-", StringComparison.OrdinalIgnoreCase) < 0)
                        .OrderBy(f => LogoRank(Path.GetFileName(f).ToLowerInvariant())).FirstOrDefault();
                    if (pick == null) return null;
                    using (Bitmap src = new Bitmap(new MemoryStream(File.ReadAllBytes(pick))))   // 先读进内存，不锁文件
                    {
                        Bitmap b = new Bitmap(32, 32);
                        using (Graphics g = Graphics.FromImage(b)) { g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.PixelOffsetMode = PixelOffsetMode.HighQuality; g.DrawImage(src, 0, 0, 32, 32); }
                        return b;
                    }
                }
            }
            catch { }
            return null;
        }
        static int LogoRank(string f)
        {
            bool unplated = f.Contains("unplated");
            if (f.Contains("targetsize-32")) return unplated ? 0 : 2;
            if (f.Contains("targetsize-48")) return unplated ? 1 : 3;
            if (f.Contains("targetsize-")) return unplated ? 4 : 5;
            if (f.Contains("scale-200")) return 6;
            if (f.Contains("scale-100")) return 7;
            return 8;
        }

        // 画一个 32×32 的兜底图标：主题色的圆角底板 + 类别图标字（或首字母）
        Bitmap FallbackIcon(string name, string path)
        {
            string bare = name.StartsWith("svc:") ? name.Substring(4) : name;
            string hay = (bare + " " + (DescOf(name) ?? "") + " " + (path == null ? "" : Path.GetFileName(path))).ToLowerInvariant();
            string glyph = null;
            foreach (KeyValuePair<string[], string> k in IconKinds) if (k.Key.Any(w => hay.Contains(w))) { glyph = k.Value; break; }
            if (glyph == null && IsConsoleExe(path)) glyph = "";   // 命令行
            if (glyph == null && (CoreNames.Contains(bare) || Protected.Contains(bare) || (path != null && path.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows), StringComparison.OrdinalIgnoreCase))))
                glyph = "";   // Windows 系统
            if (glyph == null && (name.StartsWith("svc:") || Regex.IsMatch(hay, "service|svc|srv|daemon|agent|helper|server|broker|host|服务")))
                glyph = "";   // 后台服务：齿轮

            Bitmap b = new Bitmap(32, 32);
            using (Graphics g = Graphics.FromImage(b))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                using (GraphicsPath gp = Round(new RectangleF(1.5f, 1.5f, 29, 29), 6))
                using (SolidBrush bg = new SolidBrush(Color.FromArgb(70, cCyan)))
                using (Pen edge = new Pen(Color.FromArgb(190, cCyan), 1))
                { g.FillPath(bg, gp); g.DrawPath(edge, gp); }
                using (StringFormat sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                using (SolidBrush fg = new SolidBrush(cCyan))
                {
                    if (glyph != null)
                        using (Font f = new Font(fIconBig.FontFamily, 17f, GraphicsUnit.Pixel)) g.DrawString(glyph, f, fg, new RectangleF(0, 1, 32, 32), sf);
                    else
                    {
                        char lc = bare.FirstOrDefault(c => char.IsLetterOrDigit(c));
                        string letter = lc == default(char) ? "?" : char.ToUpperInvariant(lc).ToString();
                        using (Font f = new Font("Bahnschrift SemiBold", 18f, GraphicsUnit.Pixel)) g.DrawString(letter, f, fg, new RectangleF(0, 1, 32, 32), sf);
                    }
                }
            }
            return b;
        }
        static GraphicsPath Round(RectangleF r, float rad)
        {
            GraphicsPath p = new GraphicsPath(); float d = rad * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure(); return p;
        }

        // 切换主题：兜底图标按新配色重画（下次取的时候）
        void DropFallbackIcons()
        {
            foreach (string n in fallbackIcons) { Bitmap b; if (icons.TryGetValue(n, out b) && b != null) b.Dispose(); icons.Remove(n); }
            fallbackIcons.Clear();
        }
    }
}
