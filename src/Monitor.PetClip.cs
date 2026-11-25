// 看板娘 Fegmi 的序列帧动画。素材在 assets\mascot\ 下：
//   idle\idle-01.png …（待机，循环）、wave\（挥手，播一次）、talk\（普通说话，说话期间循环）、
//   worried\（报告问题，说话期间循环）、happy\（开心，播一次）；glitch\<动作>-NN.png 是每帧单独的故障效果层，叠在人物上面。
//   每段动画的第一帧和最后一帧和 idle-01 是同一个姿势，所以只在待机播到"标准姿势"的帧时切换，播完回到待机，不会跳。
//
// 省内存：每段动画按显示大小解码后，第 1 帧整张存，后面每帧只存"和第 1 帧不一样的 16×16 小块"；故障层只存有像素的小块。
// 播放：第 1 帧整块复制到一块缓冲区 → 盖上这一帧变了的小块 → 把故障层的小块混合上去 → 整块复制到窗口画面。只是复制像素，不解码、不缩放。
// 载入（解码、缩放、比较）在后台线程做；待机常驻，其他动作用到时才载入，1 分钟没用就释放。
// 程序运行中素材文件变了（比如画师交了新版），会自动重新载入。
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace WatchDog
{
    class ClipTile { public int X, Y, W, H; public int[] Px; }
    class ClipData
    {
        public string Name; public int W, H, PetH; public int[] Base;
        public List<ClipTile[]> Delta = new List<ClipTile[]>();    // 第 2 帧起：和第 1 帧不一样的小块
        public List<ClipTile[]> Glitch = new List<ClipTile[]>();   // 每帧的故障层（有像素的小块，预乘透明度），没有就是空数组
        public HashSet<int> SwitchAt = new HashSet<int> { 0 };      // 可以从这里切到别的动作的帧（和第 1 帧几乎一样的"标准姿势"）
        public long Bytes; public double LastUsed;
        public int Count { get { return Delta.Count + 1; } }
    }

    partial class Monitor
    {
        static readonly string[] ClipNames = { "idle", "wave", "talk", "worried", "happy" };
        const int ClipTileSize = 16;
        const double ClipFps = 12;
        bool petClip;                                   // 用序列帧（有 idle 文件夹）
        Dictionary<string, ClipData> clips = new Dictionary<string, ClipData>();
        HashSet<string> clipLoading = new HashSet<string>();
        string clipStamp = "";                          // 素材文件的修改时间指纹：变了就重新载入
        string clipNow = "idle", clipNext; double clipStart;
        int[] clipBuf;                                  // 拼帧用的缓冲区
        long clipBytes;                                 // 自测报告用

        static string ClipDir(string root, string name) { return Path.Combine(root, name); }
        List<string> ClipFiles(string name)
        {
            string d = ClipDir(PetDir, name);
            return Directory.Exists(d) ? Directory.GetFiles(d, name + "-*.png").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList() : new List<string>();
        }
        string GlitchFile(string name, int i) { return Path.Combine(PetDir, "glitch", name + "-" + (i + 1).ToString("00") + ".png"); }
        string ClipStampNow()
        {
            try
            {
                long sum = 0; int n = 0;
                foreach (string c in ClipNames.Concat(new[] { "glitch" }))
                {
                    string d = ClipDir(PetDir, c);
                    if (!Directory.Exists(d)) continue;
                    foreach (string f in Directory.GetFiles(d, "*.png")) { sum += File.GetLastWriteTimeUtc(f).Ticks / 10000000; n++; }
                }
                return n + ":" + sum;
            }
            catch { return ""; }
        }

        // 有 idle 文件夹且至少 2 帧：用序列帧。petBox = 所有动作所有帧（含故障层）里有像素的范围
        bool LoadPetClip()
        {
            try
            {
                if (ClipFiles("idle").Count < 2) return false;
                Rectangle box = Rectangle.Empty;
                List<string> all = ClipNames.SelectMany(c => ClipFiles(c)).ToList();
                string gd = Path.Combine(PetDir, "glitch");
                if (Directory.Exists(gd)) all.AddRange(Directory.GetFiles(gd, "*.png"));
                foreach (string f in all)
                    using (Bitmap b = new Bitmap(f))
                    {
                        Rectangle r = OpaqueBounds(b);
                        if (r.Width > 0) box = box.Width == 0 ? r : Rectangle.Union(box, r);
                    }
                if (box.Width == 0) return false;
                petBox = box; petClip = true; clipStamp = ClipStampNow();
                petParts = new List<PetPart>(); petAnims.Clear(); petRig = 0;
                return true;
            }
            catch { return false; }
        }
        static int[] ReadPx(Bitmap b, PixelFormat pf)
        {
            BitmapData d = b.LockBits(new Rectangle(0, 0, b.Width, b.Height), ImageLockMode.ReadOnly, pf);
            int[] px = new int[b.Width * b.Height];
            for (int y = 0; y < b.Height; y++) Marshal.Copy(d.Scan0 + y * d.Stride, px, y * b.Width, b.Width);
            b.UnlockBits(d);
            return px;
        }
        static Rectangle OpaqueBounds(Bitmap b)
        {
            int[] px = ReadPx(b, PixelFormat.Format32bppArgb);
            int x0 = b.Width, y0 = b.Height, x1 = -1, y1 = -1;
            for (int y = 0; y < b.Height; y++)
                for (int x = 0, i = y * b.Width; x < b.Width; x++, i++)
                    if (((uint)px[i] >> 24) != 0) { if (x < x0) x0 = x; if (x > x1) x1 = x; if (y < y0) y0 = y; if (y > y1) y1 = y; }
            return x1 < 0 ? Rectangle.Empty : Rectangle.FromLTRB(x0, y0, x1 + 1, y1 + 1);
        }

        // 把一段动作按高度 h 缩好、切成小块（可以在后台线程调用：只用到参数和只读字段）
        ClipData BuildClipData(string name, int h)
        {
            List<string> files = ClipFiles(name);
            if (files.Count == 0) return null;
            Size sz = PetSize(h); float k = PetScale(h); PointF o = PetPt(0, 0, k);
            ClipData c = new ClipData { Name = name, W = sz.Width, H = sz.Height, PetH = h };
            int n = c.W * c.H;
            using (Bitmap frame = new Bitmap(c.W, c.H, PixelFormat.Format32bppPArgb))
            {
                Func<string, int[]> scaled = f =>
                {
                    using (Graphics g = Graphics.FromImage(frame))
                    using (Bitmap src = new Bitmap(f))
                    {
                        g.Clear(Color.Transparent);
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        g.DrawImage(src, new RectangleF(o.X, o.Y, src.Width * k, src.Height * k));
                    }
                    return ReadPx(frame, PixelFormat.Format32bppPArgb);
                };
                int tilesTotal = ((c.W + ClipTileSize - 1) / ClipTileSize) * ((c.H + ClipTileSize - 1) / ClipTileSize);
                for (int fi = 0; fi < files.Count; fi++)
                {
                    int[] cur = scaled(files[fi]);
                    if (c.Base == null) c.Base = cur;
                    else
                    {
                        ClipTile[] t = Tiles(cur, c.Base, c.W, c.H, false);
                        c.Delta.Add(t);
                        if (t.Length <= tilesTotal / 50) c.SwitchAt.Add(fi);   // 和第 1 帧几乎一样：也是标准姿势，可以在这里切换
                    }
                    string gf = GlitchFile(name, fi);
                    c.Glitch.Add(File.Exists(gf) ? Tiles(scaled(gf), null, c.W, c.H, true) : new ClipTile[0]);
                }
            }
            c.Bytes = n * 4L + c.Delta.Sum(a => a.Sum(t => t.Px.Length * 4L)) + c.Glitch.Sum(a => a.Sum(t => t.Px.Length * 4L));
            return c;
        }
        // 切小块：baseline != null 时取和它不一样的块；baseline == null 时取有像素的块（故障层）
        static ClipTile[] Tiles(int[] cur, int[] baseline, int w, int h, bool nonEmpty)
        {
            List<ClipTile> tiles = new List<ClipTile>();
            for (int ty = 0; ty < h; ty += ClipTileSize)
                for (int tx = 0; tx < w; tx += ClipTileSize)
                {
                    int tw = Math.Min(ClipTileSize, w - tx), th = Math.Min(ClipTileSize, h - ty);
                    bool take = false;
                    for (int y = ty; y < ty + th && !take; y++)
                        for (int x = tx, i = y * w + tx; x < tx + tw; x++, i++)
                            if (nonEmpty ? cur[i] != 0 : cur[i] != baseline[i]) { take = true; break; }
                    if (!take) continue;
                    ClipTile t = new ClipTile { X = tx, Y = ty, W = tw, H = th, Px = new int[tw * th] };
                    for (int y = 0; y < th; y++) Array.Copy(cur, (ty + y) * w + tx, t.Px, y * tw, tw);
                    tiles.Add(t);
                }
            return tiles.ToArray();
        }

        // 在后台载入一段动作（已经在载入 / 已经有了就不重复）
        void LoadClipAsync(string name, int h)
        {
            if (clipLoading.Contains(name)) return;
            if (ClipFiles(name).Count == 0) return;
            clipLoading.Add(name);
            ThreadPool.QueueUserWorkItem(delegate
            {
                ClipData c = null;
                try { c = BuildClipData(name, h); } catch { }
                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        clipLoading.Remove(name);
                        if (c == null || c.PetH != petH) return;   // 载入期间大小又变了：作废
                        c.LastUsed = petClock.Elapsed.TotalSeconds;
                        clips[name] = c;
                        clipBytes = clips.Values.Sum(x => x.Bytes);
                    });
                }
                catch { }
            });
        }

        // 按新高度重建：待机同步建好（换大小时要马上有画面），其他动作清掉，用到时再在后台载入
        void BuildClip(int h)
        {
            clips.Clear();
            ClipData idle = BuildClipData("idle", h);
            if (idle != null) { idle.LastUsed = double.MaxValue; clips["idle"] = idle; }
            clipBytes = clips.Values.Sum(x => x.Bytes);
            clipNow = "idle"; clipNext = null; clipStart = petClock.IsRunning ? petClock.Elapsed.TotalSeconds : 0;
            clipBuf = null;
            GC.Collect();
        }

        // 想播某个动作：先在后台载入，等待机播到标准姿势再切过去
        void PlayClip(string name)
        {
            if (!petClip || name == null || ClipFiles(name).Count == 0) return;
            if (!clips.ContainsKey(name)) LoadClipAsync(name, petH);
            clipNext = name;
        }

        // 每一帧：决定现在播哪个动作的第几帧
        int ClipStep(double t)
        {
            ClipData cur; if (!clips.TryGetValue(clipNow, out cur)) { clipNow = "idle"; clipStart = t; if (!clips.TryGetValue("idle", out cur)) return -1; }
            int idx = (int)((t - clipStart) * ClipFps);
            bool loops = clipNow == "idle" || ((clipNow == "talk" || clipNow == "worried") && t < talkUntil);
            if (!loops && idx >= cur.Count) { Switch("idle", t); return 0; }   // 播一次的动作（挥手、开心）、说完话的说话动作：播完回待机
            idx %= cur.Count;
            if (clipNow == "idle" && clipNext != null && cur.SwitchAt.Contains(idx) && clips.ContainsKey(clipNext)) { string nx = clipNext; clipNext = null; Switch(nx, t); return 0; }
            cur.LastUsed = clipNow == "idle" ? double.MaxValue : t;
            return idx;
        }
        void Switch(string name, double t) { clipNow = name; clipStart = t; }

        // 拼出现在这一帧，复制到窗口画面
        void DrawClipNow(Bitmap target, int idx)
        {
            ClipData c = clips[clipNow];
            DrawClipFrame(target, c, idx);
        }
        void DrawClipFrame(Bitmap target, ClipData c, int idx)
        {
            int n = c.W * c.H;
            if (clipBuf == null || clipBuf.Length != n) clipBuf = new int[n];
            Array.Copy(c.Base, clipBuf, n);
            if (idx > 0) foreach (ClipTile t in c.Delta[idx - 1]) for (int y = 0; y < t.H; y++) Array.Copy(t.Px, y * t.W, clipBuf, (t.Y + y) * c.W + t.X, t.W);
            // 故障层：预乘透明度的"源在上"混合（out = 源 + 底 × (1 − 源透明度)），只算有像素的小块
            foreach (ClipTile t in c.Glitch[idx])
                for (int y = 0; y < t.H; y++)
                    for (int x = 0, si = y * t.W, di = (t.Y + y) * c.W + t.X; x < t.W; x++, si++, di++)
                    {
                        uint s = (uint)t.Px[si]; uint sa = s >> 24;
                        if (sa == 0) continue;
                        if (sa == 255) { clipBuf[di] = (int)s; continue; }
                        uint d = (uint)clipBuf[di], inv = 255 - sa;
                        uint a = sa + ((d >> 24) * inv + 127) / 255;
                        uint r = ((s >> 16) & 255) + (((d >> 16) & 255) * inv + 127) / 255;
                        uint g = ((s >> 8) & 255) + (((d >> 8) & 255) * inv + 127) / 255;
                        uint b = (s & 255) + ((d & 255) * inv + 127) / 255;
                        clipBuf[di] = (int)((Math.Min(a, 255u) << 24) | (Math.Min(r, 255u) << 16) | (Math.Min(g, 255u) << 8) | Math.Min(b, 255u));
                    }
            BitmapData bd = target.LockBits(new Rectangle(0, 0, c.W, c.H), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
            try { for (int y = 0; y < c.H; y++) Marshal.Copy(clipBuf, y * c.W, bd.Scan0 + y * bd.Stride, c.W); }
            finally { target.UnlockBits(bd); }
        }

        // 每隔几秒：素材文件变了就整套重新载入；不常用的动作 1 分钟没播就释放
        double clipCheckAt;
        void ClipHousekeeping(double t)
        {
            if (!petClip || t < clipCheckAt) return;
            clipCheckAt = t + 5;
            foreach (string k in clips.Keys.Where(k => k != "idle" && k != clipNow && t - clips[k].LastUsed > 60).ToList()) clips.Remove(k);
            clipBytes = clips.Values.Sum(x => x.Bytes);
            string st = ClipStampNow();
            if (st != clipStamp && st.Length > 0)
            {
                // 新素材：重新量范围、重建（会按新范围重新算大小）
                if (LoadPetClip()) { petH = 0; pet.Hide(); PlacePet(); }
            }
        }
    }
}
