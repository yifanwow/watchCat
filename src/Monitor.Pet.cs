// 看板娘（桌宠）：贴在主窗口右边的透明小窗口，跟着主窗口移动、缩放、隐藏 / 最小化
// 素材在 assets\mascot\：parts.json + parts\*.png（512×1024 画布对齐，每个部件一张透明 PNG）
// 按 parts.json 把默认状态的部件叠成一张图，用分层窗口（每个像素单独透明）显示；透明的地方鼠标点不到，会穿过去
// 只在第一次显示和大小变化时叠一次图，平时只挪位置，不占 CPU；部件图叠完就释放，只留一张合成图（约 0.5 MB）
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace WatchDog
{
    class PetPart { public string Id, File; public Rectangle R; public int Z; public PointF Pivot; public bool Visible; }

    // 分层窗口：没有边框、不进任务栏和 Alt+Tab、点它不会抢走焦点
    class PetWindow : Form
    {
        public PetWindow()
        {
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; StartPosition = FormStartPosition.Manual;
            Text = "WatchCat 看板娘";
        }
        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x80000 | 0x80 | 0x08000000;   // WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE
                return cp;
            }
        }
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x21) { m.Result = (IntPtr)3; return; }   // WM_MOUSEACTIVATE -> MA_NOACTIVATE
            base.WndProc(ref m);
        }

        // 把一张 32 位带透明度的图放到窗口上（同时设定位置和大小）
        public void SetImage(Bitmap b, Point at)
        {
            if (!IsHandleCreated) CreateHandle();
            IntPtr screen = GetDC(IntPtr.Zero), mem = CreateCompatibleDC(screen), hb = IntPtr.Zero, old = IntPtr.Zero;
            try
            {
                hb = b.GetHbitmap(Color.FromArgb(0));
                old = SelectObject(mem, hb);
                SIZE sz = new SIZE { cx = b.Width, cy = b.Height };
                POINT src = new POINT(), dst = new POINT { x = at.X, y = at.Y };
                BLEND bl = new BLEND { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };
                UpdateLayeredWindow(Handle, screen, ref dst, ref sz, mem, ref src, 0, ref bl, 2);   // ULW_ALPHA
            }
            finally
            {
                if (old != IntPtr.Zero) SelectObject(mem, old);
                if (hb != IntPtr.Zero) DeleteObject(hb);
                DeleteDC(mem); ReleaseDC(IntPtr.Zero, screen);
            }
        }

        [StructLayout(LayoutKind.Sequential)] struct POINT { public int x, y; }
        [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; }
        [StructLayout(LayoutKind.Sequential, Pack = 1)] struct BLEND { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
        [DllImport("user32.dll")] static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLEND pblend, int dwFlags);
        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hDC);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr hObject);
    }

    partial class Monitor
    {
        PetWindow pet;
        Bitmap petImg;
        List<PetPart> petParts;
        Rectangle petBox;   // 所有显示部件合起来的范围（画布坐标），叠图时只取这一块
        string PetDir { get { return Path.Combine(ToolDir, "assets", "mascot"); } }

        bool LoadPetParts()
        {
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
                    };
                    if (pp.File != null && File.Exists(Path.Combine(PetDir, pp.File))) petParts.Add(pp);
                }
                petParts = petParts.OrderBy(x => x.Z).ToList();
                List<PetPart> vis = petParts.Where(x => x.Visible).ToList();
                if (vis.Count == 0) return false;
                petBox = vis[0].R; foreach (PetPart x in vis) petBox = Rectangle.Union(petBox, x.R);
                return true;
            }
            catch { return false; }
        }

        // 按高度 h 把默认状态的部件叠成一张图（只取 petBox 范围，按比例缩小）
        Bitmap ComposePet(int h)
        {
            float k = (float)h / petBox.Height;
            Bitmap b = new Bitmap(Math.Max(1, (int)Math.Round(petBox.Width * k)), h, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(b))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.PixelOffsetMode = PixelOffsetMode.HighQuality; g.SmoothingMode = SmoothingMode.AntiAlias;
                foreach (PetPart p in petParts.Where(x => x.Visible))
                {
                    try
                    {
                        using (Bitmap src = new Bitmap(Path.Combine(PetDir, p.File)))
                        {
                            RectangleF dst = new RectangleF((p.R.X - petBox.X) * k, (p.R.Y - petBox.Y) * k, p.R.Width * k, p.R.Height * k);
                            g.DrawImage(src, dst, p.R, GraphicsUnit.Pixel);   // 只取部件实际有像素的那一块
                        }
                    }
                    catch { }
                }
            }
            return b;
        }

        void BuildPet()
        {
            if (selfTest || !LoadPetParts()) return;
            pet = new PetWindow();
            pet.Owner = this;   // 跟主窗口一起在前 / 在后，主窗口关掉时一起关
            LocationChanged += delegate { PlacePet(); };
            SizeChanged += delegate { PlacePet(); };
            VisibleChanged += delegate { PlacePet(); };
            Shown += delegate { PlacePet(); };
        }

        // 高度跟着主窗口：主窗口高度的 45%，限制在 280–520 像素
        int PetHeight() { return Math.Max(280, Math.Min(520, (int)(Height * 0.45))); }

        // 放在主窗口右边、底部对齐；右边放不下（主窗口太靠右或最大化）就贴着屏幕右边，压在主窗口上
        void PlacePet()
        {
            if (pet == null) return;
            if (!Visible || WindowState == FormWindowState.Minimized) { if (pet.Visible) pet.Hide(); return; }
            int h = PetHeight();
            bool fresh = petImg == null || petImg.Height != h;
            if (fresh)
            {
                Bitmap old = petImg; petImg = ComposePet(h);
                if (old != null) old.Dispose();
            }
            Rectangle wa = Screen.FromControl(this).WorkingArea;
            int x = Right - 4, y = Bottom - h - 8;
            if (x + petImg.Width > wa.Right) x = wa.Right - petImg.Width;
            if (y + h > wa.Bottom) y = wa.Bottom - h;
            if (y < wa.Top) y = wa.Top;
            Point at = new Point(x, y);
            bool show = !pet.Visible;
            if (show) { pet.Bounds = new Rectangle(at, petImg.Size); pet.Show(); }
            if (fresh || show) pet.SetImage(petImg, at);
            else if (pet.Location != at) pet.Location = at;   // 只挪位置：分层窗口的内容会保留，不用重新叠图
        }

        // 自测：把叠好的看板娘存成图片，检查叠图对不对
        internal void SnapPet(string file, int h)
        {
            if (!LoadPetParts()) return;
            using (Bitmap b = ComposePet(h)) b.Save(file, ImageFormat.Png);
        }
    }
}
