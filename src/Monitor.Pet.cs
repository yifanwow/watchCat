// 看板娘 Fegmi（桌宠）：贴在主窗口右边的透明小窗口，跟着主窗口移动、缩放、隐藏 / 最小化
// 素材在 assets\mascot\：parts.json + parts\*.png（512×1024 画布对齐，每个部件一张透明 PNG）
//
// 怎么动：按显示大小把每个部件裁好、缩好缓存起来（约 2–3 MB），每帧按姿势叠到一块固定的位图上，用分层窗口（每像素透明）显示。
//   呼吸（头部一起上下 1 像素）、帽子轻轻晃、帽尖星星摆、眨眼、挥手、说话张嘴、故障层偶尔错位一下。
//   每秒最多 10 帧，姿势没变就不画；主窗口藏起来时定时器停掉，不占 CPU。
// 说话：体检结果出现新问题时在头顶冒气泡（另一个分层小窗口），同时换表情；点她会挥手并说说现在的情况。
// 拖动她可以换位置（记住和主窗口的相对位置）；右键有小菜单；设置里可以关掉。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace WatchDog
{
    class PetPart { public string Id, File, Slot; public Rectangle R; public int Z; public PointF Pivot; public bool Visible; public Bitmap Native; }   // Native：补过边、按 R 裁好的原尺寸图
    class PetLayer { public string Id, Slot; public bool Default; public Bitmap B; public Rectangle Dst; public PointF Pivot; public int Z; }
    class PetAnimFrame { public string Slot, Frame; public double Sec; }

    // 一帧的姿势：帽子 / 星星的角度、故障层错位，以及每个"组"显示哪一张（没写的组用默认那张）
    class PetPose
    {
        public int GlitchDx; public float Hat, Star;
        public Dictionary<string, string> Frames = new Dictionary<string, string>();
        public string Key() { return GlitchDx + "|" + Math.Round(Hat * 4) + "|" + Math.Round(Star * 2) + "|" + string.Join(",", Frames.OrderBy(f => f.Key).Select(f => f.Value)); }
    }

    // 分层窗口：没有边框、不进任务栏和 Alt+Tab、点它不会抢走焦点。画面画在一块固定的 DIB 上，每帧不再分配内存
    class LayeredWindow : Form
    {
        IntPtr memDC, dib, oldObj;
        public Bitmap Surface;   // 直接画在 DIB 上（预乘透明度格式）
        public LayeredWindow(string title)
        {
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; StartPosition = FormStartPosition.Manual; Text = title;
        }
        protected override CreateParams CreateParams
        {
            get { CreateParams cp = base.CreateParams; cp.ExStyle |= 0x80000 | 0x80 | 0x08000000; return cp; }   // LAYERED | TOOLWINDOW | NOACTIVATE
        }
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x21) { m.Result = (IntPtr)3; return; }   // WM_MOUSEACTIVATE -> MA_NOACTIVATE
            base.WndProc(ref m);
        }

        public void EnsureSurface(int w, int h)
        {
            if (Surface != null && Surface.Width == w && Surface.Height == h) return;
            FreeSurface();
            BITMAPINFO bi = new BITMAPINFO { biSize = 40, biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
            IntPtr screen = GetDC(IntPtr.Zero), bits;
            memDC = CreateCompatibleDC(screen);
            dib = CreateDIBSection(screen, ref bi, 0, out bits, IntPtr.Zero, 0);
            ReleaseDC(IntPtr.Zero, screen);
            oldObj = SelectObject(memDC, dib);
            Surface = new Bitmap(w, h, w * 4, PixelFormat.Format32bppPArgb, bits);
        }
        // 把 Surface 上的画面放到屏幕上 at 的位置
        public void Push(Point at)
        {
            if (Surface == null) return;
            if (!IsHandleCreated) CreateHandle();
            IntPtr screen = GetDC(IntPtr.Zero);
            SIZE sz = new SIZE { cx = Surface.Width, cy = Surface.Height };
            POINT src = new POINT(), dst = new POINT { x = at.X, y = at.Y };
            BLEND bl = new BLEND { SourceConstantAlpha = 255, AlphaFormat = 1 };
            UpdateLayeredWindow(Handle, screen, ref dst, ref sz, memDC, ref src, 0, ref bl, 2);
            ReleaseDC(IntPtr.Zero, screen);
        }
        void FreeSurface()
        {
            if (Surface != null) { Surface.Dispose(); Surface = null; }
            if (memDC != IntPtr.Zero) { SelectObject(memDC, oldObj); DeleteDC(memDC); memDC = IntPtr.Zero; }
            if (dib != IntPtr.Zero) { DeleteObject(dib); dib = IntPtr.Zero; }
        }
        protected override void Dispose(bool disposing) { FreeSurface(); base.Dispose(disposing); }

        [StructLayout(LayoutKind.Sequential)] struct POINT { public int x, y; }
        [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; }
        [StructLayout(LayoutKind.Sequential, Pack = 1)] struct BLEND { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
        [StructLayout(LayoutKind.Sequential)]
        struct BITMAPINFO { public int biSize, biWidth, biHeight; public short biPlanes, biBitCount; public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant, bmiColors; }
        [DllImport("user32.dll")] static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLEND pblend, int dwFlags);
        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hDC);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr hObject);
        [DllImport("gdi32.dll")] static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO pbmi, uint usage, out IntPtr bits, IntPtr hSection, uint offset);
    }

    partial class Monitor
    {
        LayeredWindow pet, bubble;
        List<PetPart> petParts;
        List<PetLayer> petLayers = new List<PetLayer>();
        Rectangle petBox;          // 所有默认显示部件合起来的范围（画布坐标）
        int petH;                  // 现在缓存的部件是按这个高度缩的
        const int PetPadC = 18;    // 四周留白（画布像素），给帽子晃、手臂转、呼吸留地方
        Timer petTimer;
        Stopwatch petClock = new Stopwatch();
        string petLastKey = "";
        Point petAt;               // 看板娘窗口现在的位置
        Random petRnd = new Random();
        double blinkAt = 2, waveFrom = -9, talkUntil = -1, talkFrom = -9, glitchAt = 3, bubbleUntil = -1, sizeChangedAt = -9;
        int petMood;               // 0 正常 / 1 留意 / 2 出问题：决定平时的表情
        int sayMood;               // 正在说的这句话的表情
        string bubbleText, bubbleTarget; int bubbleLevel;
        bool petGreeted; HashSet<string> petSpoken = new HashSet<string>();
        bool petDown, petDragging; Point petDownScreen, petDownAt;
        ContextMenuStrip petMenu;
        string PetDir { get { return Path.Combine(ToolDir, "assets", "mascot"); } }
        int petRig;   // parts.json 的 version
        double petScale = 1.0;   // 设置里的大小倍数 0.7–1.7（1.0 = 主窗口高度的 60%）
        // 动画：名字 → 帧序列（每帧：哪一组、显示哪一张、多少秒）。parts.json 的 "animations" 可以覆盖
        Dictionary<string, List<PetAnimFrame>> petAnims = new Dictionary<string, List<PetAnimFrame>>();

        // 开关和拖动后的位置存在 data\settings.json；读一次缓存起来（主窗口拖动时会频繁用到，不每次读文件）
        bool petOn = true; Point petOfs;
        void LoadPetSettings()
        {
            Dictionary<string, object> d = ReadSettings();
            petOn = !d.ContainsKey("pet") || Json.Bool(d, "pet");
            petOfs = new Point((int)Json.Num(d, "petDX"), (int)Json.Num(d, "petDY"));
            double sc = Json.Num(d, "petScale"); petScale = sc >= 0.7 && sc <= 1.7 ? sc : 1.0;
        }
        bool PetOn() { return petOn; }
        // 设置里改大小：存起来，按新大小重新缩部件（PetTick 里发现高度不对就会重建）
        void SetPetScale(double v)
        {
            petScale = Math.Max(0.7, Math.Min(1.7, Math.Round(v, 1)));
            WriteSetting("petScale", petScale);
            if (pet != null && pet.Visible) { sizeChangedAt = -9; PetTick(); }
        }
        Point PetOffset() { return petOfs; }
        void SetPetOffset(Point o) { petOfs = o; WriteSetting("petDX", o.X); WriteSetting("petDY", o.Y); }

        bool LoadPetParts()
        {
            if (LoadPetClip()) return true;   // 有 idle 序列帧就用序列帧（Monitor.PetClip.cs）
            try
            {
                string jf = Path.Combine(PetDir, "parts.json");
                if (!File.Exists(jf)) return false;
                Dictionary<string, object> d = Json.Parse(Json.ReadFile(jf)) as Dictionary<string, object>;
                object arr; if (d == null || !d.TryGetValue("parts", out arr)) return false;
                petParts = new List<PetPart>();
                foreach (Dictionary<string, object> p in Json.Objects(arr))
                {
                    PetPart pp = new PetPart
                    {
                        Id = Json.Str(p, "id"), File = Json.Str(p, "file"), Z = (int)Json.Num(p, "z"),
                        R = new Rectangle((int)Json.Num(p, "x"), (int)Json.Num(p, "y"), (int)Json.Num(p, "width"), (int)Json.Num(p, "height")),
                        Pivot = new PointF((float)Json.Num(p, "pivotX"), (float)Json.Num(p, "pivotY")),
                        Visible = !p.ContainsKey("visible") || Json.Bool(p, "visible"),
                        Slot = Json.Str(p, "slot"),
                    };
                    // 没写 slot 的：eyes-* / brows-* / mouth-* 各算一组
                    if (pp.Slot == null && pp.Id != null) foreach (string g in new[] { "eyes", "brows", "mouth" }) if (pp.Id.StartsWith(g + "-")) pp.Slot = g;
                    if (pp.Id != null && pp.File != null && pp.R.Width > 0 && File.Exists(Path.Combine(PetDir, pp.File))) petParts.Add(pp);
                }
                petParts = petParts.OrderBy(x => x.Z).ToList();
                int cw = (int)Json.Num(Json.Obj(d, "canvas"), "width"), ch = (int)Json.Num(Json.Obj(d, "canvas"), "height");
                PreparePetSources(cw > 0 ? cw : 512, ch > 0 ? ch : 1024);
                petParts = petParts.Where(x => x.Native != null).ToList();
                petRig = (int)Json.Num(d, "version");   // 2 = 按新要求重做的素材（眼睛 / 嘴 / 挥手都是换帧）
                LoadPetAnims(d);
                List<PetPart> vis = petParts.Where(x => x.Visible).ToList();
                if (vis.Count == 0) return false;
                petBox = vis[0].R; foreach (PetPart x in vis) petBox = Rectangle.Union(petBox, x.R);
                return true;
            }
            catch { return false; }
        }

        // ---------- 补边：消除部件交界处的缝和白边 ----------
        // 素材是从一张完整立绘上切出来的：相邻两块在交界处都是半透明的边，缩小后叠在一起也不是完全不透明，桌面会从缝里透出来。
        // 办法：每一层被"上面的层"盖住的地方，把边缘改成不透明，并往外多延伸几个像素（颜色取最近的像素）。
        // 多出来的部分都藏在上面那层底下，正常看不出来，但交界处底下永远垫着实色。没被盖住的外轮廓不动。
        internal static bool PetNoFill;   // 自测对比用：--petraw 关掉补边
        const int PetFillPx = 4;          // 往下延伸几个像素（原图尺寸）
        void PreparePetSources(int cw, int ch)
        {
            foreach (PetPart p in petParts) { if (p.Native != null) p.Native.Dispose(); p.Native = null; }
            byte[] above = new byte[cw * ch];   // 已经处理过的、更上面的层合起来的不透明度
            byte[] next = new byte[cw * ch];    // 这一组处理完后要并进 above 的
            int[] px = new int[cw * ch];        // 整张画布的像素，反复用这一块（不为每个部件各开 2 MB）
            foreach (IGrouping<int, PetPart> grp in petParts.GroupBy(x => x.Z).OrderByDescending(g => g.Key))
            {
                Buffer.BlockCopy(above, 0, next, 0, above.Length);
                foreach (PetPart p in grp)
                {
                    if (!ReadArgb(Path.Combine(PetDir, p.File), cw, ch, px)) continue;
                    Rectangle r = Rectangle.Intersect(p.R, new Rectangle(0, 0, cw, ch));
                    if (!PetNoFill) r = FillUnder(px, above, r, cw, ch);
                    p.R = r;
                    p.Native = Crop(px, r, cw);
                    // 同一组的换帧（比如几张眼睛）互相不算"上面"；只有平时显示的那张（或不分组的层）才算盖住下面的层
                    if (p.Slot == null || p.Visible)
                        for (int y = r.Top; y < r.Bottom; y++)
                            for (int x = r.Left, k = y * cw + r.Left; x < r.Right; x++, k++)
                            {
                                byte a = (byte)((uint)px[k] >> 24);
                                if (a > next[k]) next[k] = a;
                            }
                }
                byte[] t = above; above = next; next = t;
            }
            px = null; above = null; next = null;
            GC.Collect();   // 启动时只做一次：把临时数组占的内存还回去
        }
        // px：整张画布的像素（ARGB）；above：上面的层盖住了哪里。返回补边后这一层的范围
        static Rectangle FillUnder(int[] px, byte[] above, Rectangle r, int cw, int ch)
        {
            // 1. 被盖住的半透明边缘 → 不透明
            for (int y = r.Top; y < r.Bottom; y++)
                for (int x = r.Left, i = y * cw + r.Left; x < r.Right; x++, i++)
                {
                    uint c = (uint)px[i]; uint a = c >> 24;
                    if (a > 0 && a < 255 && above[i] >= 24) px[i] = (int)(c | 0xFF000000);
                }
            // 2. 往被盖住的地方延伸几圈，颜色取旁边的不透明像素
            Rectangle grown = r;
            for (int pass = 0; pass < PetFillPx; pass++)
            {
                Rectangle g = Rectangle.Intersect(Rectangle.Inflate(grown, 1, 1), new Rectangle(0, 0, cw, ch));
                List<KeyValuePair<int, int>> add = new List<KeyValuePair<int, int>>();
                for (int y = g.Top; y < g.Bottom; y++)
                    for (int x = g.Left, i = y * cw + g.Left; x < g.Right; x++, i++)
                    {
                        if (((uint)px[i] >> 24) >= 128 || above[i] < 24) continue;
                        int n = -1;
                        if (x > 0 && ((uint)px[i - 1] >> 24) == 255) n = i - 1;
                        else if (x < cw - 1 && ((uint)px[i + 1] >> 24) == 255) n = i + 1;
                        else if (y > 0 && ((uint)px[i - cw] >> 24) == 255) n = i - cw;
                        else if (y < ch - 1 && ((uint)px[i + cw] >> 24) == 255) n = i + cw;
                        if (n >= 0) add.Add(new KeyValuePair<int, int>(i, px[n]));
                    }
                if (add.Count == 0) break;
                foreach (KeyValuePair<int, int> kv in add)
                {
                    px[kv.Key] = (int)((uint)kv.Value | 0xFF000000);
                    int x = kv.Key % cw, y = kv.Key / cw;
                    grown = Rectangle.Union(grown, new Rectangle(x, y, 1, 1));
                }
            }
            return grown;
        }
        static bool ReadArgb(string file, int cw, int ch, int[] px)
        {
            try
            {
                using (Bitmap src = new Bitmap(file))
                using (Bitmap b = new Bitmap(cw, ch, PixelFormat.Format32bppArgb))
                {
                    using (Graphics g = Graphics.FromImage(b)) { g.CompositingMode = CompositingMode.SourceCopy; g.DrawImage(src, new Rectangle(0, 0, cw, ch), 0, 0, src.Width, src.Height, GraphicsUnit.Pixel); }
                    BitmapData bd = b.LockBits(new Rectangle(0, 0, cw, ch), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                    for (int y = 0; y < ch; y++) Marshal.Copy(bd.Scan0 + y * bd.Stride, px, y * cw, cw);
                    b.UnlockBits(bd);
                    return true;
                }
            }
            catch { return false; }
        }
        static Bitmap Crop(int[] px, Rectangle r, int cw)
        {
            Bitmap b = new Bitmap(Math.Max(1, r.Width), Math.Max(1, r.Height), PixelFormat.Format32bppArgb);
            BitmapData bd = b.LockBits(new Rectangle(0, 0, b.Width, b.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            for (int y = 0; y < r.Height; y++) Marshal.Copy(px, (r.Top + y) * cw + r.Left, bd.Scan0 + y * bd.Stride, r.Width);
            b.UnlockBits(bd);
            return b;
        }

        // "animations": { "blink": [ { "frame": "eyes-half", "ms": 50 }, ... ], "wave": [...], "talk": [...] }
        // 帧所属的组按部件的 slot 找；没写的动画按帧名自动组合，以后多画过渡帧（比如 arm-right-raise-1、arm-right-wave-c）不用改代码
        void LoadPetAnims(Dictionary<string, object> d)
        {
            petAnims.Clear();
            Dictionary<string, string> slotOf = new Dictionary<string, string>();
            foreach (PetPart p in petParts) if (p.Slot != null) slotOf[p.Id] = p.Slot;
            Func<string, double, PetAnimFrame> F = (id, sec) => id != null && slotOf.ContainsKey(id) ? new PetAnimFrame { Slot = slotOf[id], Frame = id, Sec = sec } : null;
            Dictionary<string, object> custom = Json.Obj(d, "animations");
            if (custom != null)
                foreach (KeyValuePair<string, object> kv in custom)
                {
                    List<PetAnimFrame> l = new List<PetAnimFrame>();
                    foreach (Dictionary<string, object> f in Json.Objects(kv.Value)) { PetAnimFrame x = F(Json.Str(f, "frame"), Math.Max(0.02, Json.Num(f, "ms") / 1000)); if (x != null) l.Add(x); }
                    if (l.Count > 0) petAnims[kv.Key] = l;
                }
            Func<string[], double, List<PetAnimFrame>> Seq = (ids, sec) => ids.Select(id => F(id, sec)).Where(x => x != null).ToList();
            if (!petAnims.ContainsKey("blink")) petAnims["blink"] = Seq(new[] { "eyes-half", "eyes-closed", "eyes-closed", "eyes-half" }, 0.045);
            if (!petAnims.ContainsKey("talk")) petAnims["talk"] = Seq(new[] { "mouth-talk-a", "mouth-talk-o", "mouth-smile", "mouth-talk-a", "mouth-smile", "mouth-talk-o", "mouth-talk-a", "mouth-smile" }, 0.13);
            if (!petAnims.ContainsKey("wave"))
            {
                // 抬手过渡帧（arm-right-raise-1, -2 …）→ 挥手帧来回 3 遍（arm-right-wave-a, -b …）→ 过渡帧倒着放下
                List<string> raise = slotOf.Keys.Where(k => k.StartsWith("arm-right-raise-")).OrderBy(k => k).ToList();
                List<string> wave = slotOf.Keys.Where(k => k.StartsWith("arm-right-wave-")).OrderBy(k => k).ToList();
                List<PetAnimFrame> l = Seq(raise.ToArray(), 0.06);
                for (int i = 0; i < 3; i++) l.AddRange(Seq(wave.ToArray(), 0.2));
                raise.Reverse(); l.AddRange(Seq(raise.ToArray(), 0.06));
                petAnims["wave"] = l;
            }
        }
        // 动画开始 e 秒后，应该显示哪一帧（loop = 循环播放）；放完了返回 null
        PetAnimFrame AnimAt(string name, double e, bool loop)
        {
            List<PetAnimFrame> l; if (e < 0 || !petAnims.TryGetValue(name, out l) || l.Count == 0) return null;
            double total = l.Sum(f => f.Sec);
            if (loop) e %= total; else if (e >= total) return null;
            foreach (PetAnimFrame f in l) { if (e < f.Sec) return f; e -= f.Sec; }
            return null;
        }
        double AnimLength(string name) { List<PetAnimFrame> l; return petAnims.TryGetValue(name, out l) ? l.Sum(f => f.Sec) : 0; }

        float PetScale(int h) { return (float)h / (petBox.Height + 2 * PetPadC); }
        Size PetSize(int h) { float k = PetScale(h); return new Size((int)Math.Ceiling((petBox.Width + 2 * PetPadC) * k), h); }
        PointF PetPt(float cx, float cy, float k) { return new PointF((cx - petBox.X + PetPadC) * k, (cy - petBox.Y + PetPadC) * k); }

        // 按高度 h 把每个部件裁出来、缩好，缓存成预乘透明度的小图（画的时候不用再缩放，很快）
        void BuildPetLayers(int h)
        {
            if (petClip) { BuildClip(h); petH = h; return; }
            foreach (PetLayer l in petLayers) l.B.Dispose();
            petLayers.Clear();
            float k = PetScale(h);
            foreach (PetPart p in petParts)
            {
                try
                {
                    Bitmap src = p.Native;
                    {
                        PointF o = PetPt(p.R.X, p.R.Y, k);
                        Rectangle dst = new Rectangle((int)Math.Floor(o.X), (int)Math.Floor(o.Y), (int)Math.Ceiling(p.R.Width * k) + 1, (int)Math.Ceiling(p.R.Height * k) + 1);
                        Bitmap b = new Bitmap(dst.Width, dst.Height, PixelFormat.Format32bppPArgb);
                        using (Graphics g = Graphics.FromImage(b))
                        {
                            g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                            // 部件在画布上的位置 → 小图里的位置（保留取整前的小数，部件之间对得齐）
                            g.DrawImage(src, new RectangleF(o.X - dst.X, o.Y - dst.Y, p.R.Width * k, p.R.Height * k), new Rectangle(0, 0, p.R.Width, p.R.Height), GraphicsUnit.Pixel);
                        }
                        petLayers.Add(new PetLayer { Id = p.Id, Slot = p.Slot, Default = p.Visible, B = b, Dst = dst, Pivot = PetPt(p.Pivot.X, p.Pivot.Y, k), Z = p.Z });
                    }
                }
                catch { }
            }
            petH = h;
        }

        // 按姿势把部件叠到 g 上
        void DrawPet(Graphics g, PetPose pose)
        {
            g.InterpolationMode = InterpolationMode.Bilinear; g.PixelOffsetMode = PixelOffsetMode.Half; g.CompositingQuality = CompositingQuality.HighSpeed;
            PetLayer hat = petLayers.FirstOrDefault(l => l.Id == "hat");
            foreach (PetLayer l in petLayers)
            {
                if (l.Slot != null)
                {
                    string f; bool pick = pose.Frames.TryGetValue(l.Slot, out f) && HasLayer(f);
                    if (pick ? f != l.Id : !l.Default) continue;
                }
                else if (!l.Default) continue;
                GraphicsState st = g.Save();
                if ((l.Id == "hat" || l.Id == "hat-star") && hat != null && pose.Hat != 0) Rotate(g, hat.Pivot, pose.Hat);   // 星星先跟着帽子转
                if (l.Id == "hat-star" && pose.Star != 0) Rotate(g, l.Pivot, pose.Star);                                       // 再绕自己的挂点摆
                if (l.Id == "glitch" && pose.GlitchDx != 0) g.TranslateTransform(pose.GlitchDx, 0);
                g.DrawImage(l.B, l.Dst);
                g.Restore(st);
            }
        }
        bool HasLayer(string id) { foreach (PetLayer l in petLayers) if (l.Id == id) return true; return false; }
        static void Rotate(Graphics g, PointF c, float deg) { g.TranslateTransform(c.X, c.Y); g.RotateTransform(deg); g.TranslateTransform(-c.X, -c.Y); }

        // 现在应该是什么姿势（t = 秒）
        // 旧素材（version 1）：只有帽子晃、星星摆、故障闪，说到问题时皱眉；眨眼、说话、挥手的帧画得不对，先不用
        // 新素材（version 2）：再加眨眼（eyes-half → eyes-closed → eyes-half）、说话换嘴型、挥手换手臂帧
        PetPose PoseAt(double t)
        {
            PetPose p = new PetPose();
            p.Hat = (float)(Math.Sin(t * 2 * Math.PI / 5.3) * 1.2);                         // 帽子轻轻晃
            p.Star = (float)(Math.Sin(t * 2 * Math.PI / 2.7) * 7);                          // 帽尖星星摆
            if (t >= glitchAt && t < glitchAt + 0.14) p.GlitchDx = (t - glitchAt < 0.07 ? 1 : -1) * Math.Max(2, petH / 90);   // 故障层错位闪一下
            bool speaking = t < talkUntil;
            bool smilingAfterSpeech = !speaking && t < bubbleUntil;
            bool worried = speaking && sayMood >= 2;
            if (worried) { p.Frames["brows"] = "brows-worried"; p.Frames["mouth"] = "mouth-worried"; }
            if (petRig < 2) return p;
            PetAnimFrame f = AnimAt("blink", t - blinkAt, false);
            if (f != null) p.Frames[f.Slot] = f.Frame;
            else if ((smilingAfterSpeech || (speaking && sayMood == 0)) && HasLayer("eyes-happy")) p.Frames["eyes"] = "eyes-happy";
            if (speaking)
            {
                f = AnimAt("talk", t - talkFrom, true);
                if (f != null && !(worried && f.Frame == "mouth-smile")) p.Frames[f.Slot] = f.Frame;   // 担心时说话的间隙用担心的嘴
            }
            f = AnimAt("wave", t - waveFrom, false);
            if (f != null) p.Frames[f.Slot] = f.Frame;
            return p;
        }

        void BuildPet()
        {
            if (selfTest || !LoadPetParts()) return;
            LoadPetSettings();
            pet = new LayeredWindow("Fegmi · WatchCat 看板娘"); pet.Owner = this; pet.Cursor = Cursors.Hand;
            bubble = new LayeredWindow("Fegmi 说的话"); bubble.Owner = this; bubble.Cursor = Cursors.Hand;
            petTimer = new Timer(); petTimer.Interval = petClip ? 40 : 100;   // 序列帧每秒 12 帧，定时器要比它快才不丢帧
            petTimer.Tick += delegate { PetTick(); };
            petClock.Start();
            LocationChanged += delegate { PlacePet(); };
            SizeChanged += delegate { sizeChangedAt = petClock.Elapsed.TotalSeconds; PlacePet(); };
            VisibleChanged += delegate { PlacePet(); };
            Shown += delegate { PlacePet(); };

            // 点她：挥手 + 说说现在的情况；按住拖动：换位置（记住和主窗口的相对位置）
            pet.MouseDown += (s, e) => { if (e.Button == MouseButtons.Left) { petDown = true; petDragging = false; petDownScreen = Cursor.Position; petDownAt = petAt; } };
            pet.MouseMove += (s, e) =>
            {
                if (!petDown) return;
                Point c = Cursor.Position;
                if (!petDragging && Math.Abs(c.X - petDownScreen.X) + Math.Abs(c.Y - petDownScreen.Y) < 5) return;
                petDragging = true;
                petAt = new Point(petDownAt.X + c.X - petDownScreen.X, petDownAt.Y + c.Y - petDownScreen.Y);
                pet.Location = petAt; PlaceBubble();
            };
            pet.MouseUp += (s, e) =>
            {
                if (e.Button != MouseButtons.Left || !petDown) return;
                petDown = false;
                if (petDragging)
                {
                    petDragging = false;   // 拖完了：不清掉的话，改大小时会以为还在拖，一直不生效
                    Point a = PetAnchor(PetSize(petH));
                    SetPetOffset(new Point(petAt.X - a.X, petAt.Y - a.Y));
                    PlacePet();
                }
                else PetStatus();
            };
            bubble.MouseClick += (s, e) =>
            {
                string t = bubbleTarget;
                HideBubble();
                if (t != null && e.Button == MouseButtons.Left) SelectTarget(t);
            };

            petMenu = new ContextMenuStrip(); petMenu.Renderer = menu.Renderer; petMenu.Font = fCN;
            MenuItem(petMenu, "Fegmi，说说现在的情况", delegate { PetStatus(); });
            MenuItem(petMenu, "回到默认位置（主窗口右边）", delegate { SetPetOffset(Point.Empty); PlacePet(); });
            MenuItem(petMenu, "让 Fegmi 先休息（设置里可以再召唤）", delegate { SetPetOn(false); });
            pet.ContextMenuStrip = petMenu;
        }

        void SetPetOn(bool on)
        {
            petOn = on; WriteSetting("pet", on);
            if (on) { petGreeted = false; PetReact(); }
            PlacePet();
        }

        // 高度：主窗口高度的 60%，至少 360，不超过屏幕可用高度
        int PetWantH()
        {
            Rectangle wa = Screen.FromControl(this).WorkingArea;
            return Math.Min(wa.Height - 20, (int)(Math.Max(360, Height * 0.6) * petScale));
        }
        // 默认位置：主窗口右边、底部对齐
        Point PetAnchor(Size s) { return new Point(Right - 4, Bottom - s.Height); }

        void PlacePet()
        {
            if (pet == null) return;
            bool show = Visible && WindowState != FormWindowState.Minimized && PetOn();
            if (!show)
            {
                if (pet.Visible) pet.Hide();
                HideBubble();
                petTimer.Stop();
                return;
            }
            if (petH == 0) BuildPetLayers(PetWantH());
            Size sz = PetSize(petH);
            Point a = PetAnchor(sz), o = PetOffset();
            Rectangle wa = Screen.FromControl(this).WorkingArea;
            int x = a.X + o.X, y = a.Y + o.Y;
            x = Math.Max(wa.Left - sz.Width / 3, Math.Min(x, wa.Right - sz.Width));   // 最多藏进屏幕左边三分之一，右边不出屏幕
            y = Math.Max(wa.Top, Math.Min(y, wa.Bottom - sz.Height));
            petAt = new Point(x, y);
            if (!pet.Visible)
            {
                pet.EnsureSurface(sz.Width, sz.Height);
                RenderPet(true);
                pet.Bounds = new Rectangle(petAt, sz);
                pet.Show();
                pet.Push(petAt);
                petTimer.Start();
            }
            else if (pet.Location != petAt) pet.Location = petAt;   // 只挪位置：内容还在，不用重画
            PlaceBubble();
        }

        void RenderPet(bool force)
        {
            if (petClip)
            {
                int idx = ClipStep(petClock.Elapsed.TotalSeconds);
                if (idx < 0) return;
                string ck = clipNow + idx;
                if (!force && ck == petLastKey) return;
                petLastKey = ck;
                DrawClipNow(pet.Surface, idx);
                pet.Push(petAt);
                return;
            }
            PetPose p = PoseAt(petClock.Elapsed.TotalSeconds);
            string key = p.Key();
            if (!force && key == petLastKey) return;
            petLastKey = key;
            using (Graphics g = Graphics.FromImage(pet.Surface))
            {
                g.Clear(Color.Transparent);
                DrawPet(g, p);
            }
            pet.Push(petAt);
        }

        void PetTick()
        {
            if (pet == null || !pet.Visible) { if (petTimer != null) petTimer.Stop(); return; }
            double t = petClock.Elapsed.TotalSeconds;
            // 主窗口改大小停下 0.3 秒后，再按新高度重新缩部件（拖动边框时不会一直重建）
            int want = PetWantH();
            if (want != petH && t - sizeChangedAt > 0.3 && !(petDown && petDragging))
            {
                if (petClip) PetLog("大小变了：现在 " + petH + "，要 " + want);
                BuildPetLayers(want);
                Size sz = PetSize(petH);
                pet.EnsureSurface(sz.Width, sz.Height);
                pet.Hide(); PlacePet();   // 重新放（大小变了）
                return;
            }
            if (t > blinkAt + AnimLength("blink") + 0.05) blinkAt = t + 2.5 + petRnd.NextDouble() * 3.5;          // 下次眨眼：2.5–6 秒后
            if (t > glitchAt + 0.3) glitchAt = t + 4 + petRnd.NextDouble() * 5;             // 下次故障闪：4–9 秒后
            if (bubbleUntil > 0 && t > bubbleUntil) HideBubble();
            ClipHousekeeping(t);
            RenderPet(false);
        }

        // ---------- 说话 ----------
        // 体检结果更新后调用：第一次打招呼；出现新的问题就说出来
        void PetReact()
        {
            if (pet == null) return;
            int lv; int.TryParse(verdict[1], out lv);
            petMood = Math.Max(0, lv);
            HashSet<string> now = new HashSet<string>(issues.Where(i => i.Key != null && i.Level >= 1).Select(i => i.Key));
            if (!PetOn() || !Visible || !pet.Visible) { petSpoken = now; return; }   // 她还没出来：先不说，招呼留到出来以后
            if (!petGreeted)
            {
                petGreeted = true; petSpoken = now;
                waveFrom = petClock.Elapsed.TotalSeconds + 0.4;
                PlayClip("wave");   // 序列帧：打招呼时挥手（挥手动画里嘴是张开的）
                Issue top = issues.FirstOrDefault(i => i.Level >= 1);
                if (verdict[0] == "数据收集中" || verdict[0] == "正在检查…") PetSay("我是 Fegmi～ 正在熟悉你的电脑，开着窗口 10 分钟后就能告诉你谁在漏内存。", 0, null, 0);
                else if (top == null) PetSay("我是 Fegmi～ 帮你看过了，内存一切正常。", 0, null, 0);
                else PetSay("我是 Fegmi～ " + verdict[0] + "：" + top.Title + "。", top.Level, top.Target, top.Level);
                return;
            }
            Issue fresh = issues.Where(i => i.Key != null && i.Level >= 1 && !petSpoken.Contains(i.Key)).OrderByDescending(i => i.Level).FirstOrDefault();
            petSpoken = now;
            if (fresh == null) return;
            string head = fresh.Level >= 2 ? "注意！" : "留意一下：";
            PetSay(head + fresh.Title + "。" + Clip(fresh.Sub, 60), fresh.Level, fresh.Target, fresh.Level);
        }
        // 点她 / 右键"说说现在的情况"
        void PetStatus()
        {
            waveFrom = petClock.Elapsed.TotalSeconds;
            List<Issue> bad = issues.Where(i => i.Level >= 1).ToList();
            PlayClip(bad.Count > 0 ? "worried" : "happy");   // 序列帧：点她时，有问题就担心地说，没问题就开心
            if (verdict[0] == "数据收集中") PetSay("还在收集数据～ 开着窗口 10 分钟后给你初步判断。", 0, null, 0);
            else if (bad.Count == 0) PetSay("一切正常～ " + verdict[2] + "。有问题我会第一时间告诉你。", 0, null, 0);
            else PetSay(verdict[0] + "。最要紧的是：" + bad[0].Title + "。" + (bad[0].Target != null ? "点这里看它的详情。" : ""), bad[0].Level, bad[0].Target, bad[0].Level);
        }

        // level：气泡边框颜色（0 正常色 / 1 黄 / 2 红）；target：点气泡时选中这个程序
        void PetSay(string text, int mood, string target, int level)
        {
            if (pet == null || !pet.Visible) return;
            double t = petClock.Elapsed.TotalSeconds;
            bubbleText = text; bubbleTarget = target; bubbleLevel = Math.Max(0, level); sayMood = mood;
            talkFrom = t; talkUntil = t + Math.Min(2.6, 0.5 + text.Length * 0.04);
            if (petClip && clipNext == null && clipNow == "idle") PlayClip(level >= 1 ? "worried" : "talk");   // 序列帧：说话时播说话 / 担心的动画
            bubbleUntil = t + (level >= 1 ? 14 : 8) + text.Length * 0.05;
            RenderBubble();
            PlaceBubble();
            if (!bubble.Visible) bubble.Show();
            bubble.Push(bubble.Location);
            if (!petTimer.Enabled) petTimer.Start();
        }
        void HideBubble() { bubbleUntil = -1; talkUntil = -1; if (bubble != null && bubble.Visible) bubble.Hide(); }

        float BubbleFontPx() { using (Graphics g = CreateGraphics()) return 10f * g.DpiY / 72f; }
        void RenderBubble()
        {
            float fpx = BubbleFontPx();
            using (Font f = new Font("Microsoft YaHei UI", fpx, GraphicsUnit.Pixel))
            using (Font ft = new Font(f.FontFamily, fpx, FontStyle.Bold, GraphicsUnit.Pixel))
            using (Graphics mg = CreateGraphics())
            {
                int w = (int)(fpx * 24), pad = (int)(fpx * 1.1), tail = (int)(fpx * 1.2);
                SizeF ts = mg.MeasureString(bubbleText, f, w - 2 * pad);
                int titleH = (int)(fpx * 1.6), h = pad + titleH + (int)Math.Ceiling(ts.Height) + pad + tail;
                bubble.EnsureSurface(w, h);
                Color edge = bubbleLevel >= 2 ? cRed : bubbleLevel == 1 ? cYellow : cCyan;
                using (Graphics g = Graphics.FromImage(bubble.Surface))
                {
                    g.Clear(Color.Transparent);
                    g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    int bh = h - tail, c = (int)(fpx * 0.9);
                    // 切角面板 + 底部指向看板娘的小尾巴
                    float tx = BubbleTailX(w);
                    PointF[] poly = {
                        new PointF(0.5f, 0.5f), new PointF(w - c, 0.5f), new PointF(w - 0.5f, c), new PointF(w - 0.5f, bh - 0.5f),
                        new PointF(tx + tail * 0.6f, bh - 0.5f), new PointF(tx + tail * 0.2f, h - 1), new PointF(tx - tail * 0.5f, bh - 0.5f),
                        new PointF(c / 2f, bh - 0.5f), new PointF(0.5f, bh - c / 2f) };
                    using (SolidBrush bg = new SolidBrush(Color.FromArgb(242, cPanel))) g.FillPolygon(bg, poly);
                    using (Pen scan = new Pen(Color.FromArgb(14, cAccent))) for (int y = 3; y < bh; y += 3) g.DrawLine(scan, 2, y, w - 3, y);
                    using (Pen pe = new Pen(edge, 1.4f)) g.DrawPolygon(pe, poly);
                    using (SolidBrush b = new SolidBrush(edge)) g.FillRectangle(b, 0, 0, (int)(fpx * 3), 3);
                    using (SolidBrush b = new SolidBrush(cAccent)) g.DrawString("// FEGMI  看门猫值班员", ft, b, pad, pad * 0.6f);
                    using (SolidBrush b = new SolidBrush(cText)) g.DrawString(bubbleText, f, b, new RectangleF(pad, pad * 0.6f + titleH, w - 2 * pad, ts.Height + 4));
                }
            }
        }
        // 尾巴在气泡底边的位置：对着看板娘的头
        float BubbleTailX(int w) { float hx = PetHeadX() - BubbleX(w); return Math.Max(w * 0.15f, Math.Min(w * 0.85f, hx)); }
        float PetHeadX() { return petAt.X + PetPt(245, 0, PetScale(petH)).X; }
        int BubbleX(int w)
        {
            Rectangle wa = Screen.FromControl(this).WorkingArea;
            return Math.Max(wa.Left, Math.Min(wa.Right - w, (int)(PetHeadX() - w * 0.62f)));
        }
        void PlaceBubble()
        {
            if (bubble == null || bubble.Surface == null || bubbleUntil < 0) return;
            int w = bubble.Surface.Width, h = bubble.Surface.Height;
            float k = PetScale(petH);
            int y = petAt.Y + (int)PetPt(0, 96, k).Y - h + (int)(10 * k);   // 底边落在帽子顶上
            Rectangle wa = Screen.FromControl(this).WorkingArea;
            Point at = new Point(BubbleX(w), Math.Max(wa.Top, y));
            if (bubble.Visible && bubble.Location != at) bubble.Location = at;
            else if (!bubble.Visible) bubble.Bounds = new Rectangle(at, new Size(w, h));
        }

        // 主题换了：气泡按新颜色重画
        void PetThemeChanged()
        {
            if (bubble != null && bubble.Visible) { RenderBubble(); bubble.Push(bubble.Location); }
        }

        void DisposePet()
        {
            if (petTimer != null) { petTimer.Stop(); petTimer.Dispose(); }
            foreach (PetLayer l in petLayers) l.B.Dispose();
            petLayers.Clear();
            if (petParts != null) foreach (PetPart p in petParts) if (p.Native != null) p.Native.Dispose();
            if (petMenu != null) petMenu.Dispose();
            if (bubble != null) bubble.Dispose();
            if (pet != null) pet.Dispose();
        }

        // ---------- 自测：几个姿势和一个气泡拼成一张图 ----------
        internal void SnapPet(string file, int h)
        {
            if (!LoadPetParts()) return;
            BuildPetLayers(h);
            Size sz = PetSize(h);
            if (petClip)
            {
                // 序列帧：每个动作取一帧排成一张（待机、挥手中间、说话、担心、开心），再写一份内存占用说明
                List<ClipData> all = new List<ClipData>();
                foreach (string nm in ClipNames) { ClipData cd = nm == "idle" ? clips["idle"] : BuildClipData(nm, h); if (cd != null) all.Add(cd); }
                using (Bitmap one = new Bitmap(sz.Width, sz.Height, PixelFormat.Format32bppPArgb))
                using (Bitmap sheet = new Bitmap(sz.Width * all.Count, sz.Height, PixelFormat.Format32bppPArgb))
                using (Graphics g = Graphics.FromImage(sheet))
                {
                    g.Clear(cBg);
                    for (int i2 = 0; i2 < all.Count; i2++) { DrawClipFrame(one, all[i2], all[i2].Count / 2); g.DrawImage(one, i2 * sz.Width, 0, sz.Width, sz.Height); }
                    sheet.Save(file, ImageFormat.Png);
                }
                System.Text.StringBuilder rep = new System.Text.StringBuilder();
                foreach (ClipData cd in all)
                {
                    long full = (long)cd.Count * sz.Width * sz.Height * 4;
                    rep.AppendFormat("{0}：{1} 帧，只存变化小块 {2:N1} MB，整张存 {3:N1} MB（省 {4:P0}），可切换的帧 {5}" + Environment.NewLine, cd.Name, cd.Count, cd.Bytes / 1048576.0, full / 1048576.0, 1 - cd.Bytes / (double)full, string.Join(",", cd.SwitchAt.OrderBy(x => x).Select(x => x + 1)));
                }
                rep.AppendFormat("显示大小 {0}×{1}", sz.Width, sz.Height);
                File.WriteAllText(Path.ChangeExtension(file, null) + "-clip.txt", rep.ToString(), System.Text.Encoding.UTF8);
                return;
            }
            PetPose[] poses = { new PetPose(), new PetPose(), new PetPose(), new PetPose() };
            poses[1].Frames["eyes"] = "eyes-closed"; poses[1].Star = 7;
            poses[2].Frames["brows"] = "brows-worried"; poses[2].Frames["mouth"] = "mouth-worried"; poses[2].Hat = 1.2f;
            poses[3].Frames["mouth"] = "mouth-talk-a"; poses[3].Frames["arm-right"] = "arm-right-wave-a"; poses[3].Star = -7; poses[3].GlitchDx = 4;
            using (Bitmap sheet = new Bitmap(sz.Width * poses.Length, sz.Height, PixelFormat.Format32bppPArgb))
            using (Graphics g = Graphics.FromImage(sheet))
            {
                g.Clear(cBg);
                for (int i = 0; i < poses.Length; i++)
                {
                    GraphicsState st = g.Save(); g.TranslateTransform(i * sz.Width, 0);
                    DrawPet(g, poses[i]); g.Restore(st);
                }
                sheet.Save(file, ImageFormat.Png);
            }
            // 白色背景（最容易看出缝）：叠出来的默认姿势 vs 全身图整体缩小，数一下"明显比原图亮"的像素
            try
            {
                float k = PetScale(h);
                using (Bitmap a = new Bitmap(sz.Width, sz.Height, PixelFormat.Format32bppArgb))
                using (Bitmap r = new Bitmap(sz.Width, sz.Height, PixelFormat.Format32bppArgb))
                using (Bitmap full = new Bitmap(Path.Combine(PetDir, "mascot-full-1024.png")))
                {
                    using (Graphics g = Graphics.FromImage(a)) { g.Clear(Color.White); DrawPet(g, new PetPose()); }
                    using (Graphics g = Graphics.FromImage(r))
                    {
                        g.Clear(Color.White); g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        PointF o = PetPt(0, 0, k);
                        g.DrawImage(full, new RectangleF(o.X, o.Y, full.Width * k, full.Height * k));
                    }
                    int seams = 0;
                    for (int y = 0; y < sz.Height; y++) for (int x = 0; x < sz.Width; x++)
                        {
                            Color ca = a.GetPixel(x, y), cr = r.GetPixel(x, y);
                            if (ca.R + ca.G + ca.B - cr.R - cr.G - cr.B > 120) seams++;
                        }
                    File.WriteAllText(Path.ChangeExtension(file, null) + "-seams.txt", "白底上比原图明显发亮的像素：" + seams + "\r\n", System.Text.Encoding.UTF8);
                    a.Save(Path.ChangeExtension(file, null) + "-white.png", ImageFormat.Png);
                }
            }
            catch { }
            // 气泡单独存一张
            bubble = new LayeredWindow("snap");
            bubbleText = "注意！vivaldi（Vivaldi 浏览器）疑似内存泄漏。近 2 小时涨了 1.2 GB，关掉不用的标签页试试。"; bubbleLevel = 2; petAt = Point.Empty; petH = h;
            RenderBubble();
            using (Bitmap b = new Bitmap(bubble.Surface.Width, bubble.Surface.Height, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(b)) { g.Clear(cBg); g.DrawImage(bubble.Surface, 0, 0, b.Width, b.Height); }
                b.Save(Path.ChangeExtension(file, null) + "-bubble.png", ImageFormat.Png);
            }
            bubble.Dispose(); bubble = null;
        }
    }
}
