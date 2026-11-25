// 看板娘 Fegmi 的序列帧动画（比如 assets\mascot\idle\idle-01.png … idle-24.png，512×1024 透明 PNG，整张画好的每一帧）
// 有 idle 文件夹时用序列帧，否则用分层零件（Monitor.Pet.cs）。
//
// 省内存的存法：每帧都按显示大小解码好才能流畅播放，24 帧整张存要 20 多 MB。
//   相邻帧之间大部分像素是一样的（腿、靴子……），所以第 1 帧完整存一份，后面每帧只存"和第 1 帧不一样的 16×16 小块"。
//   播放：把第 1 帧整块复制到窗口画面上，再把这一帧变了的小块盖上去 —— 只是复制像素，不解码、不缩放，比画一整张还少复制一些。
//   比较哪些小块变了只在载入时做一次。
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace WatchDog
{
    class ClipTile { public int X, Y, W, H; public int[] Px; }

    partial class Monitor
    {
        bool petClip;                       // 用序列帧
        List<string> clipFiles;             // idle 的每一帧（按文件名排序）
        int[] clipBase;                     // 第 1 帧（显示大小，预乘透明度）
        List<ClipTile[]> clipDelta = new List<ClipTile[]>();   // 第 2 帧起：和第 1 帧不一样的小块
        int clipW, clipH;
        const int ClipTileSize = 16;
        const double ClipFps = 12;          // 每秒 12 帧：24 帧的待机 2 秒一圈
        long clipBytes;                     // 缓存占了多少内存（自测报告用）

        // 有 idle 文件夹且至少 2 帧：用序列帧。petBox = 所有帧里有像素的范围（第一次载入时量一次）
        bool LoadPetClip()
        {
            try
            {
                string dir = Path.Combine(PetDir, "idle");
                if (!Directory.Exists(dir)) return false;
                List<string> files = Directory.GetFiles(dir, "*.png").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
                if (files.Count < 2) return false;
                Rectangle box = Rectangle.Empty;
                foreach (string f in files)
                    using (Bitmap b = new Bitmap(f))
                    {
                        Rectangle r = OpaqueBounds(b);
                        if (r.Width > 0) box = box.Width == 0 ? r : Rectangle.Union(box, r);
                    }
                if (box.Width == 0) return false;
                clipFiles = files; petBox = box; petClip = true;
                petParts = new List<PetPart>(); petAnims.Clear(); petRig = 0;
                return true;
            }
            catch { return false; }
        }
        static Rectangle OpaqueBounds(Bitmap b)
        {
            BitmapData d = b.LockBits(new Rectangle(0, 0, b.Width, b.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            int[] px = new int[b.Width * b.Height]; Marshal.Copy(d.Scan0, px, 0, px.Length); b.UnlockBits(d);
            int x0 = b.Width, y0 = b.Height, x1 = -1, y1 = -1;
            for (int y = 0; y < b.Height; y++)
                for (int x = 0, i = y * b.Width; x < b.Width; x++, i++)
                    if (((uint)px[i] >> 24) != 0) { if (x < x0) x0 = x; if (x > x1) x1 = x; if (y < y0) y0 = y; if (y > y1) y1 = y; }
            return x1 < 0 ? Rectangle.Empty : Rectangle.FromLTRB(x0, y0, x1 + 1, y1 + 1);
        }

        // 按高度 h 把每一帧缩好；第 1 帧整张留着，后面的帧只留变了的小块
        void BuildClip(int h)
        {
            clipBase = null; clipDelta.Clear(); clipBytes = 0;
            Size sz = PetSize(h); clipW = sz.Width; clipH = sz.Height;
            float k = PetScale(h); PointF o = PetPt(0, 0, k);
            int n = clipW * clipH;
            int[] cur = new int[n];
            using (Bitmap frame = new Bitmap(clipW, clipH, PixelFormat.Format32bppPArgb))
            {
                foreach (string f in clipFiles)
                {
                    using (Graphics g = Graphics.FromImage(frame))
                    using (Bitmap src = new Bitmap(f))
                    {
                        g.Clear(Color.Transparent);
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        g.DrawImage(src, new RectangleF(o.X, o.Y, src.Width * k, src.Height * k));
                    }
                    BitmapData d = frame.LockBits(new Rectangle(0, 0, clipW, clipH), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
                    for (int y = 0; y < clipH; y++) Marshal.Copy(d.Scan0 + y * d.Stride, cur, y * clipW, clipW);
                    frame.UnlockBits(d);
                    if (clipBase == null) { clipBase = (int[])cur.Clone(); clipBytes += n * 4L; continue; }
                    List<ClipTile> tiles = new List<ClipTile>();
                    for (int ty = 0; ty < clipH; ty += ClipTileSize)
                        for (int tx = 0; tx < clipW; tx += ClipTileSize)
                        {
                            int tw = Math.Min(ClipTileSize, clipW - tx), th = Math.Min(ClipTileSize, clipH - ty);
                            bool diff = false;
                            for (int y = ty; y < ty + th && !diff; y++)
                                for (int x = tx, i = y * clipW + tx; x < tx + tw; x++, i++) if (cur[i] != clipBase[i]) { diff = true; break; }
                            if (!diff) continue;
                            ClipTile t = new ClipTile { X = tx, Y = ty, W = tw, H = th, Px = new int[tw * th] };
                            for (int y = 0; y < th; y++) Array.Copy(cur, (ty + y) * clipW + tx, t.Px, y * tw, tw);
                            tiles.Add(t); clipBytes += tw * th * 4L;
                        }
                    clipDelta.Add(tiles.ToArray());
                }
            }
            cur = null;
            GC.Collect();   // 载入时的临时数组还回去（只在改大小时发生）
        }

        int ClipFrameAt(double t) { int count = clipDelta.Count + 1; return (int)(t * ClipFps) % count; }

        // 把第 idx 帧画到一块 w×h 的预乘透明度位图上（窗口画面或自测用的位图）
        void DrawClipFrame(Bitmap target, int idx)
        {
            BitmapData d = target.LockBits(new Rectangle(0, 0, clipW, clipH), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
            try
            {
                for (int y = 0; y < clipH; y++) Marshal.Copy(clipBase, y * clipW, d.Scan0 + y * d.Stride, clipW);
                if (idx > 0)
                    foreach (ClipTile t in clipDelta[idx - 1])
                        for (int y = 0; y < t.H; y++) Marshal.Copy(t.Px, y * t.W, d.Scan0 + (t.Y + y) * d.Stride + t.X * 4, t.W);
            }
            finally { target.UnlockBits(d); }
        }
    }
}
