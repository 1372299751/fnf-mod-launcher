// 扩展功能：曲目识别 + OGG 试听（libVLC）+ 图片预览 + 重命名/分类/下载窗 + 快捷方式
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace FnfLauncher
{
    // ===================================================================== 文字测量缓存（每帧 MeasureText 太贵）
    
    internal static class TextCache
{
    private static readonly Dictionary<string, Size> cache = new Dictionary<string, Size>();

    public static Size Measure(Graphics g, string text, Font f)
    {
        if (string.IsNullOrEmpty(text)) return Size.Empty;
        string key = f.Name + "|" + f.SizeInPoints.ToString("0.##") + "|" + (int)f.Style + "|" + text;
        Size s;
        if (cache.TryGetValue(key, out s)) return s;
        s = TextRenderer.MeasureText(g, text, f);
        if (cache.Count > 4000) cache.Clear();
        cache[key] = s;
        return s;
    }

    public static void Clear() { cache.Clear(); }
}

// ===================================================================== 曲目
    
    internal class SongInfo
    {
        public string Name;
        public string Dir;
        public string Inst;      // Inst.ogg
        public string Voices;    // Voices.ogg（可能为空 = 人声已混进 Inst）
        public bool HasVoices { get { return !string.IsNullOrEmpty(Voices); } }
        public long LengthMs = -1;
        public bool IsVanilla;
        public int Bpm;
        public double Speed;
        public int Notes;
    }

    // ===================================================================== 试听（libVLC · 单播放器 + input-slave）
// 关键设计：伴奏和人声只用「一个」media player。人声通过 VLC 的 :input-slave 作为伴奏的
// 从属输入，由 VLC 自身对齐时间轴 —— 所以 seek 只有一路、只有一个音频输出，
// 不存在双路输出打架导致的杂音/不同步/闪退。
internal class SongPlayer : IDisposable
{
    private const string Dll = "libvlc.dll";

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetDllDirectory(string path);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibrary(string path);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr libvlc_new(int argc, IntPtr argv);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr libvlc_media_new_path(IntPtr inst, [MarshalAs(UnmanagedType.LPStr)] string path);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern void libvlc_media_add_option(IntPtr media, [MarshalAs(UnmanagedType.LPStr)] string option);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr libvlc_media_player_new_from_media(IntPtr media);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr libvlc_media_player_new(IntPtr inst);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern void libvlc_media_player_set_media(IntPtr mp, IntPtr media);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int libvlc_media_player_play(IntPtr mp);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern void libvlc_media_player_stop(IntPtr mp);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern void libvlc_media_player_release(IntPtr mp);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern void libvlc_media_release(IntPtr media);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern long libvlc_media_player_get_time(IntPtr mp);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern long libvlc_media_player_get_length(IntPtr mp);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int libvlc_media_player_set_time(IntPtr mp, long ms);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int libvlc_audio_set_volume(IntPtr mp, int volume);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int libvlc_audio_get_volume(IntPtr mp);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int libvlc_audio_set_mute(IntPtr mp, int mute);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int libvlc_audio_get_track_count(IntPtr mp);

    private static IntPtr inst = IntPtr.Zero;
    private static string lastError = "";
    private static bool tried;

    public static string Aout = "";
    public static string LastError { get { return lastError; } }
    public static bool Available { get { return inst != IntPtr.Zero; } }

    private IntPtr media, player;
    private int playing;
    private long lastSeekTick;
    private string songName;

    public string CurrentSong { get { return songName; } }
    public bool IsPlaying { get { return player != IntPtr.Zero; } }

    // ---------------------------------------------------------------- 初始化
    public static bool EnsureInit(List<string> candidateDirs)
    {
        if (inst != IntPtr.Zero) return true;
        if (tried && inst == IntPtr.Zero) return false;
        tried = true;

        foreach (string dir in candidateDirs)
        {
            try
            {
                if (string.IsNullOrEmpty(dir)) continue;
                string dll = System.IO.Path.Combine(dir, "libvlc.dll");
                if (!System.IO.File.Exists(dll)) continue;
                SetDllDirectory(dir);
                if (LoadLibrary(dll) == IntPtr.Zero) { lastError = "加载 libvlc.dll 失败"; continue; }

                string[] outs = { "mmdevice", "directsound", "waveout" };
                foreach (string aout in outs)
                {
                    inst = CreateInstance(aout);
                    if (inst != IntPtr.Zero) { Aout = aout; return true; }
                }
                lastError = "libvlc 初始化失败（音频输出不可用）";
            }
            catch (Exception ex) { lastError = ex.Message; }
        }
        if (lastError.Length == 0) lastError = "没有找到 libvlc（这个 mod 没带 VLC 组件）";
        return false;
    }

    private static IntPtr CreateInstance(string aout)
    {
        IntPtr[] keep = new IntPtr[4];
        IntPtr arr = IntPtr.Zero;
        try
        {
            keep[0] = Marshal.StringToHGlobalAnsi("--aout=" + aout);
            keep[1] = Marshal.StringToHGlobalAnsi("--no-video");
            keep[2] = Marshal.StringToHGlobalAnsi("--no-sub-autodetect-file");
            keep[3] = Marshal.StringToHGlobalAnsi("--quiet");
            arr = Marshal.AllocHGlobal(IntPtr.Size * 4);
            for (int i = 0; i < 4; i++) Marshal.WriteIntPtr(arr, i * IntPtr.Size, keep[i]);
            return libvlc_new(4, arr);
        }
        catch { return IntPtr.Zero; }
        finally
        {
            try { if (arr != IntPtr.Zero) Marshal.FreeHGlobal(arr); } catch { }
            foreach (IntPtr p in keep) { try { if (p != IntPtr.Zero) Marshal.FreeHGlobal(p); } catch { } }
        }
    }

    // ---------------------------------------------------------------- 播放
    public bool Play(SongInfo song, int volume) { return Play(song, volume, true, true); }

    public bool Play(SongInfo song, int volume, bool withInst, bool withVoices)
    {
        return PlayAt(song, volume, withInst, withVoices, 0);
    }

    public bool PlayAt(SongInfo song, int volume, bool withInst, bool withVoices, long startMs)
    {
        if (System.Threading.Interlocked.CompareExchange(ref playing, 1, 0) != 0) return false;
        try
        {
            StopInternal();
            if (inst == IntPtr.Zero || song == null) return false;

            string main = withInst ? song.Inst : song.Voices;
            if (string.IsNullOrEmpty(main) || !System.IO.File.Exists(main)) return false;

            media = libvlc_media_new_path(inst, main);
            if (media == IntPtr.Zero) return false;

            // 人声作为从属输入：VLC 自己与主轨对齐，只有一路音频输出
            if (withInst && withVoices && song.HasVoices)
            {
                try { libvlc_media_add_option(media, ":input-slave=" + ToMRL(song.Voices)); } catch { }
            }
            if (startMs > 0)
            {
                try
                {
                    libvlc_media_add_option(media, ":start-time=" +
                        (startMs / 1000.0).ToString("0.000", System.Globalization.CultureInfo.InvariantCulture));
                }
                catch { }
            }

            // 播放器只创建一次，之后一直复用 —— 避免频繁 new/release 导致的闪退
            if (player == IntPtr.Zero)
            {
                player = libvlc_media_player_new(inst);
                if (player == IntPtr.Zero) return false;
            }
            else
            {
                try { libvlc_media_player_stop(player); } catch { }
                System.Threading.Thread.Sleep(30);
            }

            libvlc_media_player_set_media(player, media);
            try { libvlc_media_release(media); } catch { }   // player 自己持有引用
            media = IntPtr.Zero;

            int hr = libvlc_media_player_play(player);
            try
            {
                libvlc_audio_set_mute(player, 0);
                libvlc_audio_set_volume(player, volume);
            }
            catch { }

            songName = song.Name;
            lastSeekTick = Environment.TickCount;
            return hr == 0;
        }
        catch { return false; }
        finally { System.Threading.Interlocked.Exchange(ref playing, 0); }
    }

    // 本地路径 -> MRL（VLC 的 input-slave 需要 URL 形式）
    private static string ToMRL(string path)
    {
        try
        {
            string p = path.Replace('\\', '/');
            if (p.Length > 1 && p[1] == ':') p = "file:///" + p;
            else if (!p.StartsWith("/")) p = "file:///" + p;
            return p.Replace(" ", "%20");
        }
        catch { return path; }
    }

    public void Stop()
    {
        StopInternal();
    }

    private void StopInternal()
    {
        try
        {
            if (player != IntPtr.Zero) libvlc_media_player_stop(player);   // 不释放，留着复用
            if (media != IntPtr.Zero) { libvlc_media_release(media); media = IntPtr.Zero; }
        }
        catch { }
        songName = null;
    }

    // 只有关窗时才真正释放播放器
    public void Dispose()
    {
        try
        {
            if (player != IntPtr.Zero)
            {
                libvlc_media_player_stop(player);
                System.Threading.Thread.Sleep(60);
                libvlc_media_player_release(player);
                player = IntPtr.Zero;
            }
            if (media != IntPtr.Zero) { libvlc_media_release(media); media = IntPtr.Zero; }
        }
        catch { }
        songName = null;
    }

    // ---------------------------------------------------------------- 控制
    public void ApplyVolume(int volume)
    {
        try
        {
            if (player == IntPtr.Zero) return;
            libvlc_audio_set_mute(player, 0);
            libvlc_audio_set_volume(player, volume);
        }
        catch { }
    }

    public int Volume
    {
        get { try { return player == IntPtr.Zero ? 0 : libvlc_audio_get_volume(player); } catch { return 0; } }
    }

    public int AudioTracks
    {
        get { try { return player == IntPtr.Zero ? 0 : libvlc_audio_get_track_count(player); } catch { return 0; } }
    }

    public long TimeMs
    {
        get { try { return player == IntPtr.Zero ? 0 : libvlc_media_player_get_time(player); } catch { return 0; } }
    }

    public long LengthMs
    {
        get { try { return player == IntPtr.Zero ? 0 : libvlc_media_player_get_length(player); } catch { return 0; } }
    }

    // 单播放器 seek：只有一路，天然同步；带 300ms 防抖避免连点
    public void SeekTo(long ms)
    {
        try
        {
            if (player == IntPtr.Zero) return;
            long now = Environment.TickCount;
            if (Math.Abs(now - lastSeekTick) < 300) return;
            lastSeekTick = now;
            libvlc_media_player_set_time(player, ms);
        }
        catch { }
    }

    public void Seek(long ms) { SeekTo(ms); }
}


// ===================================================================== 预览图
    
    internal class PreviewImage
    {
        public string Path;
        public string Caption;
        private Image thumb;
        private int thumbPx;

        // 只解码/缩放一次，之后复用
        public Image Thumb(int px)
        {
            if (thumb != null && thumbPx == px) return thumb;
            if (thumb != null) { thumb.Dispose(); thumb = null; }
            try
            {
                using (Image src = Image.FromFile(Path))
                {
                    Bitmap b = new Bitmap(px, px, PixelFormat.Format32bppArgb);
                    using (Graphics g = Graphics.FromImage(b))
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        float s = Math.Min((float)px / src.Width, (float)px / src.Height);
                        int w = Math.Max(1, (int)(src.Width * s)), h = Math.Max(1, (int)(src.Height * s));
                        g.DrawImage(src, new Rectangle((px - w) / 2, (px - h) / 2, w, h));
                    }
                    thumb = b;
                    thumbPx = px;
                }
            }
            catch { thumb = null; }
            return thumb;
        }
    }

    
    internal static class PreviewScan
    {
        private static readonly string[] DirHints =
        {
            "mods\\images\\menubackgrounds", "assets\\images\\menubackgrounds",
            "bin\\mods\\images\\menubackgrounds", "bin\\assets\\images\\menubackgrounds",
            "mods\\images", "assets\\images", "images",
            "preview", "screenshots", "screens"
        };

        public static List<PreviewImage> Collect(ModEntry m, int max)
        {
            List<PreviewImage> list = new List<PreviewImage>();
            if (m == null || m.IsWeb) return list;
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                string exeDir = Path.GetDirectoryName(m.Exe);
                List<string> roots = new List<string>();
                roots.Add(m.Dir);
                if (!string.IsNullOrEmpty(exeDir) && !string.Equals(exeDir, m.Dir, StringComparison.OrdinalIgnoreCase))
                    roots.Add(exeDir);

                foreach (string root in roots)
                {
                    foreach (string hint in DirHints)
                    {
                        string p = Path.Combine(root, hint);
                        AddFrom(p, list, seen, max, true);
                        if (list.Count >= max) return list;
                    }
                }

                // 再补 mod 根目录下的大图
                AddFrom(m.Dir, list, seen, max, false);
            }
            catch { }
            return list;
        }

        private static void AddFrom(string dir, List<PreviewImage> list, HashSet<string> seen, int max, bool requireFolder)
        {
            try
            {
                if (!Directory.Exists(dir)) return;
                foreach (string f in Directory.GetFiles(dir))
                {
                    if (list.Count >= max) return;
                    string ext = Path.GetExtension(f).ToLowerInvariant();
                    if (ext != ".png" && ext != ".jpg" && ext != ".jpeg" && ext != ".bmp" && ext != ".gif") continue;
                    string name = Path.GetFileNameWithoutExtension(f);
                    if (name.IndexOf("assets", StringComparison.OrdinalIgnoreCase) >= 0 &&
                        name.IndexOf("NOTE", StringComparison.OrdinalIgnoreCase) < 0) { }
                    long len = 0;
                    try { len = new FileInfo(f).Length; } catch { }
                    if (len < 40000) continue;                 // 太小的多半是贴图碎片
                    if (!requireFolder && !string.Equals(Path.GetDirectoryName(f), dir, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!seen.Add(f)) continue;
                    PreviewImage p = new PreviewImage();
                    p.Path = f;
                    p.Caption = Path.GetFileName(f);
                    list.Add(p);
                }
            }
            catch { }
        }
    }

    // ===================================================================== 预览大图窗口
    
    internal class PreviewViewer : Form
    {
        private readonly List<PreviewImage> images;
        private int index;
        private Image current;

        public PreviewViewer(List<PreviewImage> imgs, int start)
        {
            images = imgs;
            index = Math.Max(0, Math.Min(start, imgs.Count - 1));

            Text = "预览";
            BackColor = Color.FromArgb(10, 10, 14);
            FormBorderStyle = FormBorderStyle.Sizable;
            StartPosition = FormStartPosition.CenterParent;
            AutoScaleMode = AutoScaleMode.None;
            ClientSize = new Size(Ui.S(900), Ui.S(560));
            MinimumSize = new Size(Ui.S(420), Ui.S(300));
            KeyPreview = true;
            DoubleBuffered = true;

            LoadImage();
        }

        private void LoadImage()
        {
            if (current != null) { current.Dispose(); current = null; }
            try { current = Image.FromFile(images[index].Path); }
            catch { current = null; }
            Text = "预览 " + (index + 1) + " / " + images.Count + "　" + images[index].Caption;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(BackColor);
            if (current == null)
            {
                Ui.Text(g, "这张图读不出来", Ui.FBig, new Rectangle(0, ClientSize.Height / 2 - 20, Width, 40),
                    Ui.Sub, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                return;
            }
            float sx = (float)ClientSize.Width / current.Width;
            float sy = (float)(ClientSize.Height - Ui.S(34)) / current.Height;
            float s = Math.Min(sx, sy);
            int w = (int)(current.Width * s), h = (int)(current.Height * s);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(current, new Rectangle((ClientSize.Width - w) / 2, (ClientSize.Height - Ui.S(34) - h) / 2, w, h));

            Ui.Text(g, (index + 1) + " / " + images.Count + "　　← → 切换　Esc 关闭", Ui.FSmall,
                new Rectangle(0, ClientSize.Height - Ui.S(30), ClientSize.Width, Ui.S(28)), Ui.Sub,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            DrawArrows(g);
            Cursor = Cursors.Hand;
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Escape) Close();
            else if (e.KeyCode == Keys.Right || e.KeyCode == Keys.Space) { index = (index + 1) % images.Count; LoadImage(); }
            else if (e.KeyCode == Keys.Left) { index = (index - 1 + images.Count) % images.Count; LoadImage(); }
        }

        private Rectangle LeftRect
        {
            get { return new Rectangle(Ui.S(12), ClientSize.Height / 2 - Ui.S(30), Ui.S(56), Ui.S(60)); }
        }

        private Rectangle RightRect
        {
            get { return new Rectangle(ClientSize.Width - Ui.S(68), ClientSize.Height / 2 - Ui.S(30), Ui.S(56), Ui.S(60)); }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (LeftRect.Contains(e.Location)) { index = (index - 1 + images.Count) % images.Count; LoadImage(); return; }
            if (RightRect.Contains(e.Location)) { index = (index + 1) % images.Count; LoadImage(); return; }
            if (e.Button == MouseButtons.Left) { index = (index + 1) % images.Count; LoadImage(); }
            else if (e.Button == MouseButtons.Right) { index = (index - 1 + images.Count) % images.Count; LoadImage(); }
        }

        private void DrawArrows(Graphics g)
        {
            if (images.Count <= 1) return;
            using (SolidBrush bg = new SolidBrush(Color.FromArgb(90, 0, 0, 0)))
            using (SolidBrush fg = new SolidBrush(Color.FromArgb(230, 255, 255, 255)))
            {
                g.FillEllipse(bg, LeftRect);
                g.FillEllipse(bg, RightRect);
                Point c1 = new Point(LeftRect.X + LeftRect.Width / 2, LeftRect.Y + LeftRect.Height / 2);
                Point c2 = new Point(RightRect.X + RightRect.Width / 2, RightRect.Y + RightRect.Height / 2);
                int a = Ui.S(10);
                g.FillPolygon(fg, new Point[] { new Point(c1.X - a, c1.Y), new Point(c1.X + a, c1.Y - a), new Point(c1.X + a, c1.Y + a) });
                g.FillPolygon(fg, new Point[] { new Point(c2.X + a, c2.Y), new Point(c2.X - a, c2.Y - a), new Point(c2.X - a, c2.Y + a) });
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (current != null) { current.Dispose(); current = null; }
            base.OnFormClosing(e);
        }
    }

    // ===================================================================== 单行文本输入窗（重命名 / 分类）
    
    // ===================================================================== 开启动画（幕布上滑）
internal class IntroCurtain : Control
{
    private readonly Timer tick;
    private readonly DateTime start;
    private Image logo;
    private int phase;            // 0=播放 1=上滑
    private float slide;

    public IntroCurtain()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Color.FromArgb(11, 12, 16);

        try
        {
            Icon ic = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            if (ic != null) logo = ic.ToBitmap();
        }
        catch { }

        start = DateTime.Now;
        tick = new Timer();
        tick.Interval = 16;
        tick.Tick += delegate
        {
            double t = (DateTime.Now - start).TotalMilliseconds;
            if (phase == 0 && t > 1250) { phase = 1; }
            if (phase == 1)
            {
                slide += (float)Math.Max(6, Height * 0.05);
                Top = -(int)slide;
                if (slide >= Height + 8)
                {
                    tick.Stop();
                    tick.Dispose();
                    if (Parent != null) Parent.Controls.Remove(this);
                    Dispose();
                    return;
                }
            }
            Invalidate();
            Update();
        };
        tick.Start();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(BackColor);

        double t = Math.Min(1.0, (DateTime.Now - start).TotalMilliseconds / 900.0);
        int w = Width, h = Height;

        // 背景斜向光带
        using (SolidBrush glow = new SolidBrush(Color.FromArgb(14, Ui.Accent)))
            g.FillEllipse(glow, w / 2 - h / 2, -h / 3, h, h);

        // logo：缩放 + 上浮
        float k = (float)(0.86 + 0.14 * t);
        int ls = (int)(Ui.S(104) * k);
        int ly = (int)(h * 0.34 - Ui.S(60) * (float)t);
        if (logo != null)
            g.DrawImage(logo, new Rectangle(w / 2 - ls / 2, ly, ls, ls));

        // 标题
        int ty = ly + ls + Ui.S(18);
        Color titleColor = Color.FromArgb((int)(255 * t), 240, 240, 246);
        Ui.Text(g, "FNF 模组启动器", new Font("Microsoft YaHei UI", 20f, FontStyle.Bold),
            new Rectangle(0, ty, w, Ui.S(40)), titleColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

        Color subColor = Color.FromArgb((int)(190 * t), 150, 158, 178);
        Ui.Text(g, "v" + Version4.AppVersion + "   ·   正在加载模组…", Ui.FSmall,
            new Rectangle(0, ty + Ui.S(40), w, Ui.S(22)), subColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

        // 扫光进度条
        int barW = Ui.S(240), barH = Ui.S(3);
        int bx = (w - barW) / 2, by = ty + Ui.S(74);
        Ui.FillRound(g, new Rectangle(bx, by, barW, barH), 2, Ui.A(Color.White, 22));
        int fill = (int)(barW * (float)Math.Min(1.0, t * 1.05));
        if (fill > 0) Ui.FillRound(g, new Rectangle(bx, by, fill, barH), 2, Ui.Accent);

        // 底部强调线
        using (SolidBrush b = new SolidBrush(Ui.A(Ui.Accent, 60)))
            g.FillRectangle(b, 0, h - Ui.S(3), w, Ui.S(3));
    }
}
// ===================================================================== 资料窗口（分类展示）
internal class InfoForm : Form
{
    private readonly ModEntry mod;
    private int scroll;
    private int contentHeight;
    private float scrollTarget;
    private Timer scrollAnim;
    public static Config Cfg;
    public static Action Changed;

    public InfoForm(ModEntry m)
    {
        mod = m;
        Text = "资料 · " + m.Title;
        BackColor = Ui.Bar;
        ForeColor = Ui.Txt;
        Font = Ui.FHint;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.None;
        ClientSize = new Size(Ui.S(640), Ui.S(600));
        MinimumSize = new Size(Ui.S(430), Ui.S(320));
        KeyPreview = true;
        DoubleBuffered = true;
        Ui.DarkTitleBar(Handle);

        int by = ClientSize.Height - Ui.S(76);
        RoundButton bBkNow = new RoundButton("立即备份");
        bBkNow.Filled = false;
        bBkNow.Font = Ui.FSmall;
        bBkNow.SetBounds(Ui.S(18), by, Ui.S(96), Ui.S(30));
        bBkNow.Click += delegate
        {
            Cursor = Cursors.WaitCursor;
            string p = SaveBackup.Backup(mod, 5);
            Cursor = Cursors.Default;
            bkStatus = p == null ? "没找到存档目录（这个 mod 还没有存档）" : "已备份：" + Path.GetFileName(p);
            Invalidate();
        };

        RoundButton bBkRestore = new RoundButton("还原最近");
        bBkRestore.Filled = false;
        bBkRestore.Font = Ui.FSmall;
        bBkRestore.SetBounds(Ui.S(122), by, Ui.S(96), Ui.S(30));
        bBkRestore.Click += delegate
        {
            string[] snaps = SaveBackup.Snapshots(mod);
            if (snaps.Length == 0) { bkStatus = "还没有任何备份"; Invalidate(); return; }
            if (MessageBox.Show(this, "用这份备份覆盖当前存档？\r\n\r\n" + Path.GetFileName(snaps[0]),
                    "还原存档", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            bool ok2 = SaveBackup.Restore(mod, snaps[0]);
            bkStatus = ok2 ? "已还原：" + Path.GetFileName(snaps[0]) : "还原失败（存档目录可能被占用）";
            Invalidate();
        };

        RoundButton bBkDir = new RoundButton("打开备份目录");
        bBkDir.Filled = false;
        bBkDir.Font = Ui.FSmall;
        bBkDir.SetBounds(Ui.S(226), by, Ui.S(120), Ui.S(30));
        bBkDir.Click += delegate { SaveBackup.OpenDir(mod); };

        RoundButton bLua = new RoundButton("Lua 脚本…");
        bLua.Filled = false;
        bLua.Font = Ui.FSmall;
        bLua.SetBounds(Ui.S(354), by, Ui.S(104), Ui.S(30));
        bLua.Click += delegate { using (LuaManagerForm lf = new LuaManagerForm(mod)) lf.ShowDialog(this); };

        RoundButton bRename = new RoundButton("重命名文件夹…");
        bRename.Filled = false;
        bRename.Font = Ui.FSmall;
        bRename.SetBounds(Ui.S(466), by, Ui.S(126), Ui.S(30));
        bRename.Click += delegate
        {
            string oldName = Path.GetFileName(mod.Dir);
            using (TextPrompt p = new TextPrompt("重命名文件夹", "新的名字（同时作为文件夹名）", oldName))
            {
                if (p.ShowDialog(this) != DialogResult.OK) return;
                string newName = ZipInstallForm.Clean(p.Value);
                if (string.Equals(newName, oldName, StringComparison.OrdinalIgnoreCase)) return;
                string parent = Path.GetDirectoryName(mod.Dir);
                string target = Path.Combine(parent, newName);
                if (Directory.Exists(target))
                {
                    MessageBox.Show(this, "同一目录下已经有「" + newName + "」了。", "重命名", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                try { Directory.Move(mod.Dir, target); }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "重命名失败：\r\n" + ex.Message, "重命名", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                if (Cfg != null)
                {
                    string[] prefixes = { "name.", "cat.", "wicon.", "hidden.", "pin.", "count.", "recent.", "play.", "arg.", "work.", "author.", "eng.", "engm.", "sz.", "ovr." };
                    foreach (string pre in prefixes)
                    {
                        string v = Cfg.Get(pre + oldName, "\u0001");
                        if (v == "\u0001") continue;
                        Cfg.Set(pre + newName, v);
                        Cfg.Remove(pre + oldName);
                    }
                    Cfg.Save();
                }
                if (Changed != null) Changed();
                Close();
            }
        };

        Controls.Add(bBkNow); Controls.Add(bBkRestore); Controls.Add(bBkDir); Controls.Add(bLua); Controls.Add(bRename);
    }

    private string bkStatus = "";

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(BackColor);

        int y = Ui.S(14) - scroll;
        int x = Ui.S(18);
        int w = ClientSize.Width - Ui.S(36);

        y = Section(g, "故事线 / 周", mod.WeekCount > 0 ? mod.WeekCount + " 个" : "暂无", x, y, w);
        if (mod.WeekText.Length > 0) y = BodyMulti(g, mod.WeekText, x, y, w); else y = Body(g, "暂无", x, y, w);

        y += Ui.S(4);
        y = Section(g, "成就", mod.AchvText.Length > 0 ? "" : "暂无", x, y, w);
        if (mod.AchvText.Length > 0) y = BodyMulti(g, mod.AchvText, x, y, w); else y = Body(g, "暂无", x, y, w);

        y += Ui.S(4);
        y = Section(g, "资源构成", "", x, y, w);
        y = ResourceBar(g, x, y, w);

        y += Ui.S(4);
        y = Section(g, "存档备份", "", x, y, w);
        string[] snaps = SaveBackup.Snapshots(mod);
        string saveDir = SavePaths.Find(mod);
        string bkLine = snaps.Length == 0 ? "暂无备份（启动游戏前后会自动备份，保留最近 5 份）"
                                          : "已有 " + snaps.Length + " 份，最近 " + Path.GetFileName(snaps[0]) +
                                            "（" + (SaveBackup.SnapshotSize(snaps[0]) / 1024) + " KB）";
        y = Body(g, bkLine, x, y, w);
        if (saveDir != null) y = Body(g, "存档目录：" + saveDir, x, y, w);
        if (bkStatus.Length > 0) y = Body(g, bkStatus, x, y, w);

        y += Ui.S(4);
        y = Section(g, "引擎与文件", "", x, y, w);
        y = Body(g, "引擎：" + mod.EngineDisplay + (mod.EngineTech.Length > 0 ? "（" + mod.EngineTech + "）" : ""), x, y, w);
        y = Body(g, "曲目：" + mod.Songs.Count + " 首（原版 " + CountVanilla() + "） · 总音符 " + mod.TotalNotes.ToString("N0"), x, y, w);
        y = Body(g, "文件：" + mod.FileCount.ToString("N0") + " 个 · " + (mod.SizeBytes / (1024 * 1024)) + " MB", x, y, w);
        if (mod.PackColorArgb != 0)
        {
            Rectangle sw = new Rectangle(x + Ui.S(46), y + Ui.S(5), Ui.S(16), Ui.S(16));
            Ui.Text(g, "主题色", Ui.FSmall, new Rectangle(x, y, Ui.S(44), Ui.S(24)), Ui.Sub, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            using (SolidBrush b = new SolidBrush(Color.FromArgb(mod.PackColorArgb))) g.FillEllipse(b, sw);
        }

        contentHeight = y + scroll + Ui.S(96);

        // 滚动条
        int viewH = ClientSize.Height - Ui.S(84);
        if (contentHeight > ClientSize.Height)
        {
            float trackH = viewH - Ui.S(8);
            float th = Math.Max(Ui.S(28), trackH * (ClientSize.Height / (float)contentHeight));
            float max = Math.Max(1f, contentHeight - ClientSize.Height);
            float top = Ui.S(8) + (trackH - th) * (scroll / max);
            using (SolidBrush sb2 = new SolidBrush(Color.FromArgb(70, 255, 255, 255)))
                g.FillRectangle(sb2, new Rectangle(ClientSize.Width - Ui.S(7), (int)top, Ui.S(3), (int)th));
        }
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        int max = Math.Max(0, contentHeight - ClientSize.Height);
        scrollTarget -= (e.Delta / 120f) * Ui.S(84);
        if (scrollTarget < 0) scrollTarget = 0;
        if (scrollTarget > max) scrollTarget = max;

        if (scrollAnim == null)
        {
            scrollAnim = new Timer();
            scrollAnim.Interval = 15;
            scrollAnim.Tick += delegate
            {
                int d = (int)Math.Round(scrollTarget - scroll);
                if (Math.Abs(d) <= 1) { scroll = (int)Math.Round(scrollTarget); scrollAnim.Stop(); }
                else scroll += (int)Math.Round(d * 0.2);
                Invalidate();
            };
        }
        if (!scrollAnim.Enabled) scrollAnim.Start();
    }

    private int CountVanilla()
    {
        int n = 0;
        foreach (SongInfo s in mod.Songs) if (s.IsVanilla) n++;
        return n;
    }

    private int Section(Graphics g, string title, string badge, int x, int y, int w)
    {
        Ui.Text(g, title, Ui.FBig, new Rectangle(x, y, w - Ui.S(120), Ui.S(26)), Ui.Accent,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
        if (badge.Length > 0)
            Ui.Text(g, badge, Ui.FSmall, new Rectangle(x + w - Ui.S(110), y, Ui.S(110), Ui.S(26)), Ui.Sub,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
        using (Pen p = new Pen(Ui.Line)) g.DrawLine(p, x, y + Ui.S(27), x + w, y + Ui.S(27));
        return y + Ui.S(34);
    }

    private int Body(Graphics g, string text, int x, int y, int w)
    {
        Rectangle r = new Rectangle(x, y, w, Ui.S(40));
        Ui.Text(g, text, Ui.FHint, r, Ui.Txt,
            TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);
        return y + Ui.S(24);
    }

    // 多行正文：按实际行数撑高，用于故事线/成就这种一行一条的列表
    private int BodyMulti(Graphics g, string text, int x, int y, int w)
    {
        string t = (text ?? "").Replace("\r\n", "\n").Replace("\r", "\n");
        string[] lines = t.Split('\n');
        int lineH = Ui.S(22);
        int h = Math.Max(lineH, lines.Length * lineH);
        Ui.Text(g, t, Ui.FHint, new Rectangle(x, y, w, h), Ui.Txt,
            TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak);
        return y + h + Ui.S(6);
    }

    private int ResourceBar(Graphics g, int x, int y, int w)
    {
        int img = mod.ImgCount, aud = mod.AudioCount, scr = mod.ScriptCount;
        int total = img + aud + scr;
        string line = "图片 " + img + " · 音频 " + aud + " · 脚本 " + scr;
        if (mod.VideoCount > 0) line += " · 视频 " + mod.VideoCount;
        Ui.Text(g, line, Ui.FHint, new Rectangle(x, y, w, Ui.S(22)), Ui.Txt,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
        y += Ui.S(26);

        if (total > 0)
        {
            int barW = w;
            int wImg = (int)((double)img / total * barW);
            int wAud = (int)((double)aud / total * barW);
            int wScr = barW - wImg - wAud;
            if (wImg > 0) Ui.FillRound(g, new Rectangle(x, y, wImg, Ui.S(10)), Ui.S(3), Ui.Accent);
            if (wAud > 0) Ui.FillRound(g, new Rectangle(x + wImg, y, wAud, Ui.S(10)), Ui.S(3), Color.FromArgb(64, 224, 255));
            if (wScr > 0) Ui.FillRound(g, new Rectangle(x + wImg + wAud, y, wScr, Ui.S(10)), Ui.S(3), Color.FromArgb(120, 200, 140));
            y += Ui.S(16);
        }
        return y;
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Ui.FadeIn(this);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape) Close();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Close();
    }
}
internal class TextFieldForm : Form
    {
        public string Value = "";

        public TextFieldForm(string title, string label, string initial, string hint)
        {
            Text = title;
            BackColor = Ui.Bar;
            ForeColor = Ui.Txt;
            Font = Ui.FHint;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoScaleMode = AutoScaleMode.None;
            ClientSize = new Size(Ui.S(460), Ui.S(196));

            Label l = new Label();
            l.AutoSize = false;
            l.BackColor = Ui.Bar;
            l.ForeColor = Ui.Txt;
            l.Font = Ui.FBig;
            l.TextAlign = ContentAlignment.MiddleLeft;
            l.Text = label;
            l.SetBounds(Ui.S(24), Ui.S(16), Ui.S(400), Ui.S(24));

            TextBox box = new TextBox();
            box.BorderStyle = BorderStyle.FixedSingle;
            box.BackColor = Ui.Card;
            box.ForeColor = Ui.Txt;
            box.Font = Ui.FHint;
            box.Text = initial ?? "";
            box.SetBounds(Ui.S(24), Ui.S(46), Ui.S(412), Ui.S(26));

            Label h = new Label();
            h.AutoSize = false;
            h.BackColor = Ui.Bar;
            h.ForeColor = Ui.Sub;
            h.Font = Ui.FSmall;
            h.TextAlign = ContentAlignment.TopLeft;
            h.Text = hint ?? "";
            h.SetBounds(Ui.S(24), Ui.S(78), Ui.S(412), Ui.S(40));

            RoundButton ok = new RoundButton("确定");
            ok.SetBounds(Ui.S(248), Ui.S(132), Ui.S(90), Ui.S(38));
            ok.Click += delegate { Value = box.Text.Trim(); DialogResult = DialogResult.OK; Close(); };

            RoundButton cancel = new RoundButton("取消");
            cancel.Filled = false;
            cancel.SetBounds(Ui.S(346), Ui.S(132), Ui.S(90), Ui.S(38));
            cancel.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };

            Controls.Add(l); Controls.Add(box); Controls.Add(h); Controls.Add(ok); Controls.Add(cancel);
            AcceptButton = null;
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Ui.DarkTitleBar(Handle);
            Ui.FadeIn(this);
        }
    }

    // ===================================================================== 毛玻璃底（截屏 + 模糊 + 深色蒙版）
internal static class Glass
{
    private static Bitmap cache;
    private static DateTime at = DateTime.MinValue;
    private static Rectangle origin;

    public static Bitmap Screen
    {
        get
        {
            if (cache != null && (DateTime.Now - at).TotalMilliseconds < 1500) return cache;
            try
            {
                Rectangle vs = SystemInformation.VirtualScreen;
                origin = vs;
                Bitmap raw = new Bitmap(vs.Width, vs.Height);
                using (Graphics g = Graphics.FromImage(raw))
                    g.CopyFromScreen(vs.Left, vs.Top, 0, 0, vs.Size, CopyPixelOperation.SourceCopy);
                Bitmap blurred = Blur(raw);
                raw.Dispose();
                if (cache != null) cache.Dispose();
                cache = blurred;
                at = DateTime.Now;
            }
            catch { }
            return cache;
        }
    }

    public static Rectangle Origin { get { return origin; } }

    private static Bitmap Blur(Bitmap src)
    {
        int div = 28;
        int w = Math.Max(1, src.Width / div), h = Math.Max(1, src.Height / div);
        using (Bitmap small = new Bitmap(w, h))
        {
            using (Graphics g = Graphics.FromImage(small))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(src, new Rectangle(0, 0, w, h));
            }
            Bitmap big = new Bitmap(src.Width, src.Height);
            using (Graphics g = Graphics.FromImage(big))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(small, new Rectangle(0, 0, src.Width, src.Height));
            }
            return big;
        }
    }

    // 给窗体画背景：模糊桌面 + 深色蒙版
    public static void PaintBackdrop(Form f, Graphics g)
    {
        Bitmap bmp = Screen;
        if (bmp == null) { g.Clear(f.BackColor); return; }
        try
        {
            Point p = f.PointToScreen(Point.Empty);
            Rectangle src = new Rectangle(p.X - Origin.X, p.Y - Origin.Y, f.ClientSize.Width, f.ClientSize.Height);
            src.Intersect(new Rectangle(0, 0, bmp.Width, bmp.Height));
            if (src.Width > 0 && src.Height > 0)
                g.DrawImage(bmp, new Rectangle(0, 0, f.ClientSize.Width, f.ClientSize.Height), src, GraphicsUnit.Pixel);
            else g.Clear(Color.FromArgb(16, 17, 24));
        }
        catch { g.Clear(Color.FromArgb(16, 17, 24)); }
        using (SolidBrush tint = new SolidBrush(Color.FromArgb(168, 12, 13, 20)))
            g.FillRectangle(tint, f.ClientRectangle);
        using (SolidBrush glow = new SolidBrush(Color.FromArgb(26, Ui.Accent)))
            g.FillRectangle(glow, 0, 0, f.ClientSize.Width, Ui.S(3));
    }
}
// ===================================================================== 可拖动进度条
internal class SeekBar : Control
{
    public long Length { get; set; }
    public long Position { get; set; }
    public event EventHandler SeekRequested;

    private bool drag;
    public bool Dragging { get { return drag; } }

    public SeekBar()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Ui.Bar;
        Height = Ui.S(28);
        Cursor = Cursors.Hand;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(BackColor);

        int leftW = Ui.S(44), rightW = Ui.S(44);
        int barW = Math.Max(1, Width - leftW - rightW);
        int trackY = Height / 2;

        float frac = Length > 0 ? Math.Max(0f, Math.Min(1f, (float)Position / Length)) : 0f;
        Rectangle track = new Rectangle(leftW, trackY - Ui.S(2), barW, Ui.S(4));
        Ui.FillRound(g, track, Ui.S(2), Ui.Line);
        int w = (int)(barW * frac);
        if (w > 0) Ui.FillRound(g, new Rectangle(leftW, trackY - Ui.S(2), w, Ui.S(4)), Ui.S(2), Ui.Accent);
        if (Length > 0)
        {
            using (SolidBrush knob = new SolidBrush(Ui.Accent))
                g.FillEllipse(knob, leftW + w - Ui.S(4), trackY - Ui.S(4), Ui.S(8), Ui.S(8));
        }
        Ui.Text(g, Ms(Position), Ui.FSmall, new Rectangle(0, 0, leftW - Ui.S(6), Height), Ui.Sub,
            TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
        Ui.Text(g, Length > 0 ? Ms(Length) : "--:--", Ui.FSmall,
            new Rectangle(Width - rightW + Ui.S(6), 0, rightW - Ui.S(6), Height), Ui.Sub,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
    }

    private static string Ms(long ms)
    {
        if (ms <= 0) return "0:00";
        int sec = (int)(ms / 1000);
        return (sec / 60) + ":" + (sec % 60).ToString("00");
    }

    private void SetFromX(int x)
    {
        int leftW = Ui.S(44), rightW = Ui.S(44);
        int barW = Math.Max(1, Width - leftW - rightW);
        if (Length <= 0 || barW <= 0) return;
        float f = Math.Max(0f, Math.Min(1f, (float)(x - leftW) / barW));
        Position = (long)(Length * f);
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        drag = true;
        Capture = true;
        SetFromX(e.X);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (drag) SetFromX(e.X);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (drag)
        {
            drag = false;
            Capture = false;
            if (SeekRequested != null) SeekRequested(this, EventArgs.Empty);
        }
    }
}

// ===================================================================== DWM 亚克力背景
internal static class Acrylic
{
    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public int AccentState;
        public int AccentFlags;
        public int GradientColor;
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        public int Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

    // state: 3=blur, 4=acrylic
    public static bool Apply(Form f, int state, int aarrggbb)
    {
        try
        {
            AccentPolicy policy = new AccentPolicy();
            policy.AccentState = state;
            policy.AccentFlags = 2;
            policy.GradientColor = aarrggbb;
            int size = Marshal.SizeOf(policy);
            IntPtr ptr = Marshal.AllocHGlobal(size);
            Marshal.StructureToPtr(policy, ptr, false);
            WindowCompositionAttributeData data = new WindowCompositionAttributeData();
            data.Attribute = 19;                 // WCA_ACCENT_POLICY
            data.SizeOfData = size;
            data.Data = ptr;
            int hr = SetWindowCompositionAttribute(f.Handle, ref data);
            Marshal.FreeHGlobal(ptr);
            return hr != 0;
        }
        catch { return false; }
    }
}
// ===================================================================== 曲目窗口（与主界面同风格）
internal class SongList : Control
{
    private class Row
    {
        public SongInfo Song;
        public bool Header;
        public int Count;
    }

    public readonly List<SongInfo> Songs;
    public string PlayingName = "";
    public int Selected = -1;
    public bool VanillaOpen;

    public event EventHandler PlayRequested;     // 点 ▶ / 双击 / Enter
    public event EventHandler SelectionChanged;

    private readonly int rowH;
    private float scroll, scrollTarget;
    private int hover = -1;
    private DateTime lastClick = DateTime.MinValue;
    private Point lastPos;

    public SongList(List<SongInfo> songs)
    {
        Songs = songs;
        rowH = Ui.S(34);
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                 ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Ui.Bar;
        Cursor = Cursors.Hand;
    }

    private List<Row> BuildRows()
    {
        List<Row> rows = new List<Row>();
        List<SongInfo> vanilla = new List<SongInfo>();
        foreach (SongInfo s in Songs)
        {
            if (s.IsVanilla) { vanilla.Add(s); continue; }
            Row r = new Row(); r.Song = s; rows.Add(r);
        }
        if (vanilla.Count > 0)
        {
            if (DetailPane.FoldVanilla)
            {
                Row h = new Row(); h.Header = true; h.Count = vanilla.Count; rows.Add(h);
                if (VanillaOpen)
                    foreach (SongInfo s in vanilla) { Row r = new Row(); r.Song = s; rows.Add(r); }
            }
            else
            {
                foreach (SongInfo s in vanilla) { Row r = new Row(); r.Song = s; rows.Add(r); }
            }
        }
        return rows;
    }

    private int ContentH { get { return BuildRows().Count * rowH + Ui.S(6); } }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(BackColor);

        List<Row> rows = BuildRows();
        if (rows.Count == 0)
        {
            Ui.Text(g, "没有找到曲目", Ui.FHint, new Rectangle(0, Height / 2 - Ui.S(12), Width, Ui.S(24)),
                Ui.Sub, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            return;
        }

        for (int i = 0; i < rows.Count; i++)
        {
            int y = (int)Math.Round(i * rowH - scroll);
            if (y > Height || y + rowH < 0) continue;
            Row r = rows[i];

            if (r.Header)
            {
                bool hv = (hover == i);
                if (hv) Ui.FillRound(g, new Rectangle(Ui.S(4), y + Ui.S(2), Width - Ui.S(8), rowH - Ui.S(4)), Ui.S(6), Ui.A(Color.White, 10));
                Ui.Text(g, (VanillaOpen ? "▾  " : "▸  ") + "疑似原版曲目", Ui.FSmall,
                    new Rectangle(Ui.S(14), y, Width - Ui.S(120), rowH), Ui.Sub,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
                Ui.Text(g, r.Count + " 首", Ui.FSmall,
                    new Rectangle(Width - Ui.S(90), y, Ui.S(76), rowH), Ui.Sub,
                    TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
                continue;
            }

            SongInfo s = r.Song;
            bool playing = (s.Name == PlayingName);
            bool sel = (i == Selected);
            if (sel || hover == i || playing)
                Ui.FillRound(g, new Rectangle(Ui.S(4), y + Ui.S(2), Width - Ui.S(8), rowH - Ui.S(4)), Ui.S(8),
                    playing ? Ui.A(Ui.Accent, 34) : (sel ? Ui.A(Ui.Accent, 22) : Ui.A(Color.White, 9)));

            Rectangle glyph = new Rectangle(Ui.S(10), y + (rowH - Ui.S(20)) / 2, Ui.S(20), Ui.S(20));
            if (playing)
            {
                Ui.FillRound(g, glyph, Ui.S(5), Ui.Accent);
                Ui.Text(g, "■", Ui.FSmall, glyph, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
            else
            {
                Ui.FillRound(g, glyph, Ui.S(5), Ui.CardHi);
                Ui.Text(g, "▶", Ui.FSmall, glyph, (hover == i || sel) ? Ui.Txt : Ui.Sub,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }

            int nameX = Ui.S(40);
            int tagW = Ui.S(74);
            int lenW = Ui.S(58);
            Color nameColor = playing ? Ui.Accent : (s.IsVanilla ? Color.FromArgb(168, 178, 200) : Ui.Txt);
            Ui.Text(g, s.Name, Ui.FHint, new Rectangle(nameX, y, Width - nameX - tagW - lenW - Ui.S(14), rowH),
                nameColor, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
            Ui.Text(g, s.HasVoices ? "人声+伴奏" : "合并音轨", Ui.FSmall,
                new Rectangle(Width - tagW - lenW - Ui.S(8), y, tagW, rowH),
                s.HasVoices ? Ui.Sub : Color.FromArgb(150, 160, 185),
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
            Ui.Text(g, s.LengthMs > 0 ? Fmt(s.LengthMs) : "", Ui.FSmall,
                new Rectangle(Width - lenW - Ui.S(10), y, lenW, rowH), Ui.Sub,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
        }

        if (ContentH > Height)
        {
            float max = Math.Max(1, ContentH - Height);
            int trackH = Height - Ui.S(8);
            int th = Math.Max(Ui.S(30), (int)((double)trackH * Height / ContentH));
            int top = (int)(Ui.S(4) + (trackH - th) * (scroll / max));
            Ui.FillRound(g, new Rectangle(Width - Ui.S(6), top, Ui.S(3), th), 2, Ui.A(Color.White, 55));
        }
    }

    private static string Fmt(long ms)
    {
        int sec = (int)(ms / 1000);
        return (sec / 60) + ":" + (sec % 60).ToString("00");
    }

    private int Hit(Point p)
    {
        int i = (int)Math.Floor((p.Y + scroll) / (double)rowH);
        List<Row> rows = BuildRows();
        if (i < 0 || i >= rows.Count) return -1;
        return i;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int i = Hit(e.Location);
        if (i != hover) { hover = i; Invalidate(); }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        hover = -1;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        int i = Hit(e.Location);
        if (i < 0) return;

        DateTime now = DateTime.Now;
        bool dbl = (now - lastClick).TotalMilliseconds < 420 &&
                   Math.Abs(e.X - lastPos.X) < 6 && Math.Abs(e.Y - lastPos.Y) < 6;
        lastClick = now;
        lastPos = e.Location;

        List<Row> rows = BuildRows();
        if (rows[i].Header)
        {
            VanillaOpen = !VanillaOpen;
            Invalidate();
            return;
        }

        Selected = i;
        Invalidate();
        if (SelectionChanged != null) SelectionChanged(this, EventArgs.Empty);
        if (dbl && PlayRequested != null) PlayRequested(this, EventArgs.Empty);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        scrollTarget -= e.Delta / 120f * Ui.S(60);
        float max = Math.Max(0, ContentH - Height);
        if (scrollTarget < 0) scrollTarget = 0;
        if (scrollTarget > max) scrollTarget = max;
        // 简单缓动
        TimerStep();
    }

    private void TimerStep()
    {
        float d = scrollTarget - scroll;
        if (Math.Abs(d) > 0.5f) scroll += d * 0.2f; else scroll = scrollTarget;
        Invalidate();
    }

    public SongInfo SelectedSong
    {
        get
        {
            List<Row> rows = BuildRows();
            if (Selected < 0 || Selected >= rows.Count) return null;
            return rows[Selected].Song;
        }
    }

    public void ScrollToSelected()
    {
        if (Selected < 0) return;
        while (Selected * rowH < scroll) scroll -= rowH;
        while (Selected * rowH + rowH > scroll + Height) scroll += rowH;
        if (scroll < 0) scroll = 0;
        scrollTarget = scroll;
        Invalidate();
    }
}

internal class SongsForm : Form
{
    private readonly List<SongInfo> songs;
    private readonly string exeDir;
    private readonly SongList list;
    private readonly Label status;

    public SongsForm(ModEntry m)
    {
        songs = m.Songs;
        try { exeDir = Path.GetDirectoryName(m.Exe); } catch { exeDir = m.Dir; }

        Text = m.Title;
        BackColor = Ui.Bar;
        ForeColor = Ui.Txt;
        Font = Ui.FHint;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.None;
        ClientSize = new Size(Ui.S(600), Ui.S(388));
        MinimumSize = new Size(Ui.S(460), Ui.S(320));
        KeyPreview = true;
        DoubleBuffered = true;
        Ui.DarkTitleBar(Handle);

        list = new SongList(songs);
        list.SetBounds(Ui.S(14), Ui.S(12), Ui.S(572), Ui.S(292));
        list.PlayRequested += delegate { OpenSong(list.SelectedSong, "both"); };

        status = new Label();
        status.AutoSize = false;
        status.BackColor = Ui.Bar;
        status.ForeColor = Ui.Sub;
        status.Font = Ui.FSmall;
        status.TextAlign = ContentAlignment.MiddleLeft;
        status.SetBounds(Ui.S(16), Ui.S(310), Ui.S(566), Ui.S(20));
        status.Text = "";

        RoundButton bBoth = MakeBtn("▶  合听", Ui.S(14), true);
        bBoth.Click += delegate { OpenSong(list.SelectedSong, "both"); };

        RoundButton bInst = MakeBtn("伴奏", Ui.S(114), false);
        bInst.Click += delegate { OpenSong(list.SelectedSong, "inst"); };

        RoundButton bVoices = MakeBtn("人声", Ui.S(214), false);
        bVoices.Click += delegate { OpenSong(list.SelectedSong, "voices"); };

        RoundButton close = new RoundButton("关闭");
        close.Filled = false;
        close.SetBounds(Ui.S(486), Ui.S(338), Ui.S(100), Ui.S(38));
        close.Click += delegate { Close(); };

        Controls.Add(list); Controls.Add(status);
        Controls.Add(bBoth); Controls.Add(bInst); Controls.Add(bVoices); Controls.Add(close);

        if (songs.Count > 0) { list.Selected = 0; list.ScrollToSelected(); }
    }

    private static RoundButton MakeBtn(string text, int x, bool filled)
    {
        RoundButton b = new RoundButton(text);
        b.Font = Ui.FSmall;
        b.Filled = filled;
        b.SetBounds(x, Ui.S(338), Ui.S(90), Ui.S(38));
        return b;
    }

    // 用系统默认播放器打开：伴奏 / 人声 / 合听（合听会先预混成一个 wav 并缓存）
    private void OpenSong(SongInfo s, string mode)
    {
        if (s == null) return;
        string path = null;
        if (mode == "inst")
        {
            path = s.Inst;
        }
        else if (mode == "voices")
        {
            if (!s.HasVoices) { status.Text = "这首是合并音轨，没有单独的人声"; return; }
            path = s.Voices;
        }
        else
        {
            status.Text = "正在把伴奏和人声混成一个文件…（第一次会慢几秒）";
            Application.DoEvents();
            path = Mixer.MixBoth(s, exeDir);
            if (path == null)
            {
                status.Text = "混音失败：" + Mixer.LastError + "（可以先听「伴奏」）";
                return;
            }
        }
        status.Text = "已用系统播放器打开：" + Path.GetFileName(path);
        Mixer.OpenWithSystem(path);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Ui.FadeIn(this);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape) Close();
        else if (e.KeyCode == Keys.Enter) OpenSong(list.SelectedSong, "both");
        else if (e.KeyCode == Keys.Up) { if (list.Selected > 0) { list.Selected--; list.ScrollToSelected(); list.Invalidate(); } e.Handled = true; }
        else if (e.KeyCode == Keys.Down) { list.Selected++; list.ScrollToSelected(); list.Invalidate(); e.Handled = true; }
    }
}
// ===================================================================== 预览窗口（弹窗版）
internal class PreviewForm : Form
{
    private readonly List<PreviewImage> images;
    private readonly PictureBox pic;
    private readonly Label caption;
    private int index;

    public PreviewForm(ModEntry m)
    {
        images = m.Previews ?? new List<PreviewImage>();

        Text = "预览 · " + m.Title;
        BackColor = Ui.Bar;
        ForeColor = Ui.Txt;
        Font = Ui.FHint;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.None;
        ClientSize = new Size(Ui.S(860), Ui.S(586));
        MinimumSize = new Size(Ui.S(520), Ui.S(380));
        KeyPreview = true;
        DoubleBuffered = true;
        Ui.DarkTitleBar(Handle);

        pic = new PictureBox();
        pic.BackColor = Color.FromArgb(10, 10, 14);
        pic.SizeMode = PictureBoxSizeMode.Zoom;
        pic.SetBounds(Ui.S(14), Ui.S(12), Ui.S(832), Ui.S(500));
        pic.DoubleClick += delegate { FullScreen(); };

        RoundButton prev = new RoundButton("◀");
        prev.Filled = false;
        prev.SetBounds(Ui.S(14), Ui.S(522), Ui.S(52), Ui.S(40));
        prev.Click += delegate { Move(-1); };

        RoundButton next = new RoundButton("▶");
        next.Filled = false;
        next.SetBounds(Ui.S(74), Ui.S(522), Ui.S(52), Ui.S(40));
        next.Click += delegate { Move(1); };

        caption = new Label();
        caption.AutoSize = false;
        caption.BackColor = Ui.Bar;
        caption.ForeColor = Ui.Sub;
        caption.Font = Ui.FSmall;
        caption.TextAlign = ContentAlignment.MiddleLeft;
        caption.SetBounds(Ui.S(140), Ui.S(522), Ui.S(590), Ui.S(40));

        RoundButton close = new RoundButton("关闭");
        close.Filled = false;
        close.SetBounds(Ui.S(744), Ui.S(522), Ui.S(100), Ui.S(40));
        close.Click += delegate { Close(); };

        Controls.Add(pic); Controls.Add(prev); Controls.Add(next); Controls.Add(caption); Controls.Add(close);

        if (images.Count > 0) ShowIndex(0);
        else caption.Text = "这个 mod 里没有找到可预览的图片";
    }

    private void ShowIndex(int i)
    {
        if (images.Count == 0) return;
        if (i < 0) i = images.Count - 1;
        if (i >= images.Count) i = 0;
        index = i;
        try
        {
            using (FileStream fs = new FileStream(images[i].Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                Image img = Image.FromStream(fs);
                if (pic.Image != null) pic.Image.Dispose();
                pic.Image = img;
            }
            caption.Text = (i + 1) + " / " + images.Count + "　·　" + images[i].Caption;
        }
        catch { caption.Text = "这张图读不出来：" + images[i].Caption; }
    }

    private void Move(int delta)
    {
        if (images.Count == 0) return;
        ShowIndex(index + delta);
    }

    private void FullScreen()
    {
        if (images.Count == 0) return;
        using (PreviewViewer v = new PreviewViewer(images, index)) v.ShowDialog(this);
        ShowIndex(index);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Ui.FadeIn(this);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape) Close();
        else if (e.KeyCode == Keys.Right || e.KeyCode == Keys.Space) Move(1);
        else if (e.KeyCode == Keys.Left) Move(-1);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        try { if (pic.Image != null) { pic.Image.Dispose(); pic.Image = null; } } catch { }
        base.OnFormClosing(e);
    }
}
internal class DownloadForm : Form
{
    private class DItem
    {
        public string Name, Note, Url, Cat;
        public List<DItem> Children;
        public bool Open;
        public DItem(string n, string note, string url, string cat)
        {
            Name = n; Note = note; Url = url; Cat = cat;
        }
    }

    // 每张卡片的运行时状态：不销毁重建，只改位置 → 不会闪
    private class CardRef
    {
        public DItem Item;
        public CardBox Box;
        public bool IsChild;
        public CardRef Group;      // 子卡片属于哪个组
        public float OpenP;        // 组：0=收起 1=展开
        public List<CardRef> Kids;
        public bool Target;
        public int BaseX = -1;
    }

    private static readonly List<DItem> items = new List<DItem>
    {
        new DItem("GameJolt", "模组最多，新 mod 大多首发在这", "https://gamejolt.com/games/tag-fridaynightfunkin", "模组站"),
        new DItem("GameBanana", "模组 / 皮肤 / 引擎都有，页面里能直接下 zip", "https://gamebanana.com/search?_sOrder=best_match&_sSearchString=&_idGameRow=8694", "模组站"),
        new DItem("FridayNightFunkin.net", "官方风格的网页版 FNF，浏览器里直接玩", "https://www.fridaynightfunkin.net/", "模组站"),
        new DItem("KBH Games", "在线直接玩，也能找到不少移植版", "https://kbhgames.com/tag/friday-night-funkin", "模组站"),

        Group("Psych Engine (PE)", "最常用的 FNF 引擎。点「展开」看各版本区别后再挑", "下载引擎", new DItem[] {
            new DItem("Psych Engine 1.0.4", "官方最终版。功能最全、代码最规范。适合自己做模组或体验纯净原版；但部分老模组尚未适配，存在兼容风险。",
                "https://github.com/ShadowMario/FNF-PsychEngine/releases/tag/1.0.4", ""),
            new DItem("Psych Engine 0.7.3", "兼容性最广（强烈推荐）。目前社区的「事实主流」。绝大多数新老模组的首选适配版本，玩模组下这个准没错。",
                "https://github.com/ShadowMario/FNF-PsychEngine/releases/tag/0.7.3", ""),
            new DItem("Psych Engine 0.6.3", "经典老版本。早期神作模组的「温床」。很经典，但较新的模组基本已不再支持此版本。",
                "https://github.com/ShadowMario/FNF-PsychEngine/releases/tag/0.6.3", ""),
            new DItem("Psych Engine 0.5.2", "远古怀旧版。仅适用于极少数拒绝更新的骨灰级老模组。",
                "https://gamejolt.com/games/Pysch-Engine-Optimizado-por-mi-Espero-Te-Sirva/784131", "")
        }),
        new DItem("NovaFlare (NF) 引擎", "国产改版，基于 Psych 内核，NF 系 mod 用它跑", "https://github.com/NovaFlare-Engine-Concentration/FNF-NovaFlare-Engine/releases", "下载引擎"),
        new DItem("Codename Engine", "新一代引擎，脚本能力和特效更强", "https://github.com/FNF-CNE-Devs/CodenameEngine", "下载引擎"),
        new DItem("Kade Engine", "老牌引擎，很多经典 mod 基于它", "https://github.com/KadeDev/Kade-Engine", "下载引擎"),
        new DItem("GameBanana 引擎分类", "按下载量/BPM 排序找引擎", "https://gamebanana.com/mods/cats/8694", "下载引擎"),

        new DItem("7-Zip", "解压整合包 / zip 必备（本启动器不代下）", "https://www.7-zip.org/", "工具"),
        new DItem("Psych Engine Wiki", "引擎参数、Lua API、目录结构说明", "https://github.com/ShadowMario/FNF-PsychEngine/wiki", "工具"),
        new DItem("FNF 汉化教程", "B 站搜「FNF 汉化」，社区教程很多", "https://search.bilibili.com/all?keyword=FNF%20汉化", "工具")
    };

    private static DItem Group(string name, string note, string cat, DItem[] kids)
    {
        DItem g = new DItem(name, note, "", cat);
        g.Children = new List<DItem>(kids);
        return g;
    }

    private readonly ScrollPanel listPanel;
    private readonly List<RoundButton> cats = new List<RoundButton>();
    private readonly Label hint;
    private readonly List<CardRef> cards = new List<CardRef>();
    private string current = "模组站";
    private double switchAt = -10000;
    private float switchP = 1f;
    private CatMark catMark;
    private float markY;
    private int markTargetY;
    private bool showHint = true;
    private Timer anim;
    private double lastToggleAt;

    private static readonly string[] catNames = { "模组站", "下载引擎", "工具" };

    public DownloadForm()
    {
        Text = "下载中心";
        BackColor = Ui.Bg;
        ForeColor = Ui.Txt;
        Font = Ui.FHint;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.None;
        ClientSize = new Size(Ui.S(800), Ui.S(600));
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

        Label t = new Label();
        t.AutoSize = false;
        t.BackColor = Ui.Bg;
        t.ForeColor = Ui.Txt;
        t.Font = new Font("Microsoft YaHei UI", 13f, FontStyle.Bold);
        t.TextAlign = ContentAlignment.MiddleLeft;
        t.Text = "下载中心";
        t.SetBounds(Ui.S(20), Ui.S(12), Ui.S(240), Ui.S(30));
        Controls.Add(t);

        Label sub = new Label();
        sub.AutoSize = false;
        sub.BackColor = Ui.Bg;
        sub.ForeColor = Ui.Sub;
        sub.Font = Ui.FSmall;
        sub.TextAlign = ContentAlignment.MiddleLeft;
        sub.Text = "卡片点「打开」用浏览器去下；启动器不代为下载，避免违反站点条款";
        sub.SetBounds(Ui.S(20), Ui.S(42), Ui.S(700), Ui.S(20));
        Controls.Add(sub);

        Config cfg = null;
        try { cfg = new Config(Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "launcher.ini")); }
        catch { }
        bool hideHint = false;
        try { hideHint = cfg != null && cfg.GetBool("hideVerHint", false); } catch { }
        showHint = !hideHint;

        hint = new Label();
        hint.AutoSize = false;
        hint.BackColor = Color.FromArgb(255, 46, 30, 40);
        hint.ForeColor = Color.FromArgb(255, 255, 196, 120);
        hint.Font = Ui.FSmall;
        hint.TextAlign = ContentAlignment.MiddleLeft;
        hint.Text = "  ⚠  如果不知道选择哪个版本，请询问模组资源发布者";
        hint.SetBounds(Ui.S(16), Ui.S(66), ClientSize.Width - Ui.S(32) - Ui.S(96), Ui.S(30));

        RoundButton bHide = new RoundButton("不再提示");
        bHide.Filled = false;
        bHide.Font = Ui.FSmall;
        bHide.BackColor = Ui.Bg;
        bHide.SetBounds(ClientSize.Width - Ui.S(16) - Ui.S(92), Ui.S(68), Ui.S(92), Ui.S(26));
        bHide.Visible = !hideHint;
        bHide.Click += delegate
        {
            try { if (cfg != null) { cfg.Set("hideVerHint", "1"); cfg.Save(); } } catch { }
            showHint = false;
            bHide.Visible = false;
            hint.Visible = false;
            LayoutList();
        };
        hint.Visible = showHint;
        Controls.Add(hint);
        Controls.Add(bHide);

        for (int i = 0; i < catNames.Length; i++)
        {
            RoundButton b = new RoundButton(catNames[i]);
            b.Filled = false;
            b.SetBounds(Ui.S(16), Ui.S(108) + i * Ui.S(44), Ui.S(116), Ui.S(34));
            string c = catNames[i];
            b.Click += delegate { if (c != current) { current = c; switchAt = Ui.Now; switchP = 0f; } RefreshCats(); BuildList(); };
            cats.Add(b);
            Controls.Add(b);
        }

        // 分类指示条（切换时滑动）
        catMark = new CatMark();
        markY = Ui.S(108);
        markTargetY = (int)markY;
        catMark.SetBounds(Ui.S(6), (int)markY, Ui.S(4), Ui.S(34));
        Controls.Add(catMark);

        listPanel = new ScrollPanel();
        listPanel.BackColor = Ui.Bg;
        Controls.Add(listPanel);
        LayoutList();

        RoundButton close = new RoundButton("关闭");
        close.Filled = false;
        close.SetBounds(ClientSize.Width - Ui.S(16) - Ui.S(96), ClientSize.Height - Ui.S(50), Ui.S(96), Ui.S(34));
        close.Click += delegate { Close(); };
        Controls.Add(close);

        RefreshCats();
        BuildList();
    }

    private void LayoutList()
    {
        int top = showHint ? Ui.S(110) : Ui.S(72);
        listPanel.SetBounds(Ui.S(142), top, ClientSize.Width - Ui.S(158), ClientSize.Height - top - Ui.S(60));
    }

    private void RefreshCats()
    {
        int idx = 0;
        for (int i = 0; i < cats.Count; i++) if (cats[i].Text == current) idx = i;
        markTargetY = Ui.S(108) + idx * Ui.S(44);
        foreach (RoundButton b in cats)
        {
            b.Filled = (b.Text == current);
            b.BackColor = Ui.Bg;
            b.Invalidate();
        }
    }

    // 只建一次卡片；展开/收起只改 Top，不销毁重建
    private void BuildList()
    {
        listPanel.Content.SuspendLayout();
        while (listPanel.Content.Controls.Count > 0)
        {
            Control c = listPanel.Content.Controls[0];
            listPanel.Content.Controls.RemoveAt(0);
            c.Dispose();
        }
        cards.Clear();

        int w = Math.Max(Ui.S(320), listPanel.ClientSize.Width - Ui.S(24));
        foreach (DItem it in items)
        {
            if (it.Cat != current) continue;
            CardRef g = new CardRef();
            g.Item = it;
            g.Box = MakeCard(it, Ui.S(2), w, false, it.Children != null && it.Children.Count > 0 ? g : null);
            listPanel.Content.Controls.Add(g.Box);
            cards.Add(g);

            if (it.Children != null)
            {
                g.Kids = new List<CardRef>();
                for (int i = 0; i < it.Children.Count; i++)
                {
                    CardRef ch = new CardRef();
                    ch.Item = it.Children[i];
                    ch.IsChild = true;
                    ch.Group = g;
                    ch.Box = MakeCard(ch.Item, Ui.S(26), w - Ui.S(24), true, g);
                    ch.Box.Visible = it.Open;
                    listPanel.Content.Controls.Add(ch.Box);
                    cards.Add(ch);
                    g.Kids.Add(ch);
                }
            }
        }
        listPanel.Content.ResumeLayout();
        RefreshLayout();
        StartAnim();
    }

    // 站点图标：<exe>\launcher\siteicons\{gamejolt,gamebanana,kbh,fnfnet}.png
    // fnfnet 没有图时退回启动器自己的图标；其余站点没有图就不画
    // 启动器自带图标（exe 里那个）
    private static Image LauncherIcon()
    {
        try
        {
            Icon ic = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            return ic == null ? null : ic.ToBitmap();
        }
        catch { return null; }
    }

    private static Image CardIcon(string name)
    {
        try
        {
            string slug = "";
            if (name.IndexOf("NovaFlare", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("(NF)", StringComparison.OrdinalIgnoreCase) >= 0) slug = "nf";
            if (name.StartsWith("GameJolt", StringComparison.OrdinalIgnoreCase)) slug = "gamejolt";
            else if (name.StartsWith("GameBanana", StringComparison.OrdinalIgnoreCase)) slug = "gamebanana";
            else if (name.StartsWith("FridayNightFunkin", StringComparison.OrdinalIgnoreCase)) slug = "fnfnet";
            else if (name.StartsWith("KBH", StringComparison.OrdinalIgnoreCase)) slug = "kbh";
            if (slug.Length == 0) return LauncherIcon();

            string dir = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "launcher", "siteicons");
            string p = Path.Combine(dir, slug + ".png");
            if (File.Exists(p))
            {
                using (FileStream fs = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    return Image.FromStream(fs);
            }
            if (slug.Length > 0)
            {
                Image builtin = SiteIcons.Get(slug);
                if (builtin != null) return builtin;
            }
            // 其余卡片（以及图标缺失时）统一用启动器自带图标
            Icon ic = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            return ic == null ? null : ic.ToBitmap();
        }
        catch { return null; }
    }

    private CardBox MakeCard(DItem it, int x, int w, bool isChild, CardRef group)
    {
        string url = it.Url;
        bool isGroup = it.Children != null && it.Children.Count > 0;

        CardBox card = new CardBox();
        card.SetBounds(x, 0, w, Ui.S(86));
        card.BackColor = Ui.Bg;

        // 站点图标（可选）：<exe>\launcher\siteicons\{gamejolt,gamebanana,kbh,fnfnet}.png
        Image siteIcon = CardIcon(it.Name);
        int iconShift = 0;
        if (siteIcon != null)
        {
            PictureBox pb = new PictureBox();
            pb.SizeMode = PictureBoxSizeMode.Zoom;
            pb.BackColor = Ui.Card;
            pb.Image = siteIcon;
            pb.Enabled = false;
            pb.SetBounds(Ui.S(16) + (isChild ? Ui.S(6) : 0), Ui.S(23), Ui.S(40), Ui.S(40));
            card.Controls.Add(pb);
            iconShift = Ui.S(52);
        }

        Label n = new Label();
        n.AutoSize = false;
        n.BackColor = Ui.Card;
        n.ForeColor = isChild ? Ui.Txt : (isGroup ? Ui.A(Ui.Ramp7, 235) : Ui.Txt);
        n.Font = Ui.FBig;
        n.TextAlign = ContentAlignment.MiddleLeft;
        n.Text = it.Name;
        n.SetBounds(Ui.S(16) + (isChild ? Ui.S(6) : 0) + iconShift, Ui.S(12), w - Ui.S(200) - iconShift, Ui.S(24));

        Label d = new Label();
        d.AutoSize = false;
        d.BackColor = Ui.Card;
        d.ForeColor = Ui.Sub;
        d.Font = Ui.FSmall;
        d.TextAlign = ContentAlignment.MiddleLeft;
        d.Text = it.Note;
        d.SetBounds(Ui.S(16) + (isChild ? Ui.S(6) : 0) + iconShift, Ui.S(40), w - Ui.S(200) - iconShift, Ui.S(34));

        card.Controls.Add(n);
        card.Controls.Add(d);

        if (isGroup)
        {
            RoundButton bToggle = new RoundButton("展开");
            bToggle.BackColor = Ui.Card;
            bToggle.SetBounds(w - Ui.S(106), Ui.S(26), Ui.S(86), Ui.S(32));
            CardRef g = group;
            bToggle.Click += delegate { Toggle(g); };
            n.Click += delegate { Toggle(g); };
            d.Click += delegate { Toggle(g); };
            card.Controls.Add(bToggle);
            card.Tag = bToggle;
        }
        else
        {
            RoundButton bOpen = new RoundButton("打开");
            bOpen.Filled = false;
            bOpen.BackColor = Ui.Card;
            bOpen.SetBounds(w - Ui.S(160), Ui.S(26), Ui.S(66), Ui.S(32));
            bOpen.Click += delegate { Open(url); };

            RoundButton bCopy = new RoundButton("复制");
            bCopy.Filled = false;
            bCopy.BackColor = Ui.Card;
            bCopy.Font = Ui.FSmall;
            bCopy.SetBounds(w - Ui.S(86), Ui.S(26), Ui.S(66), Ui.S(32));
            bCopy.Click += delegate(object src, EventArgs ea)
            {
                try { Clipboard.SetText(url); } catch { }
                RoundButton rb = (RoundButton)src;
                rb.Text = "已复制";
                rb.Invalidate();
            };

            card.Controls.Add(bOpen);
            card.Controls.Add(bCopy);
        }
        return card;
    }

    private void Toggle(CardRef g)
    {
        if (g == null) return;
        if (Ui.Now - lastToggleAt < 260) return;
        lastToggleAt = Ui.Now;
        g.Item.Open = !g.Item.Open;
        g.Target = g.Item.Open;

        RoundButton b = g.Box.Tag as RoundButton;
        if (b != null)
        {
            b.Text = g.Item.Open ? "收起" : "展开";
            b.Filled = g.Item.Open;
            b.Invalidate();
        }

        StartAnim();
    }

    // 错峰进度：卡片按顺序依次入场（0=还在外面，1=到位）
    private float Stagger(int i)
    {
        float p = (switchP * 1.45f) - i * 0.06f;
        if (p < 0f) p = 0f;
        if (p > 1f) p = 1f;
        return p * p * (3f - 2f * p);
    }

    private void StartAnim()
    {
        if (anim == null)
        {
            anim = new Timer();
            anim.Interval = 15;
            anim.Tick += delegate
            {
                bool busy = false;

                // 展开/收起
                foreach (CardRef c in cards)
                {
                    if (c.Group == null && c.Item.Children == null) continue;
                    float target = (c.Item.Children != null) ? (c.Item.Open ? 1f : 0f) : (c.Group.Target ? 1f : 0f);
                    if (Math.Abs(c.OpenP - target) > 0.001f)
                    {
                        c.OpenP += (target - c.OpenP) * 0.16f;
                        if (Math.Abs(c.OpenP - target) < 0.002f) c.OpenP = target;
                        busy = true;
                    }
                }

                // 分类切换：卡片错峰入场
                float sp = (float)Math.Min(1.0, (Ui.Now - switchAt) / 380.0);
                sp = sp * sp * (3f - 2f * sp);
                if (Math.Abs(sp - switchP) > 0.001f) { switchP = sp; busy = true; }

                // 分类指示条滑动
                if (catMark != null && Math.Abs(markY - markTargetY) > 0.6f)
                {
                    markY += (markTargetY - markY) * 0.24f;
                    if (Math.Abs(markY - markTargetY) < 0.6f) markY = markTargetY;
                    catMark.Top = (int)markY;
                    catMark.Invalidate();
                    busy = true;
                }

                RefreshLayout();
                if (!busy) anim.Stop();
            };
        }
        anim.Start();
    }

    private void RefreshLayout()
    {
        int y = Ui.S(2);
        int gap = Ui.S(8);
        int h = Ui.S(86);
        int idx = 0;
        foreach (CardRef c in cards)
        {
            if (c.IsChild) continue;
            if (c.BaseX < 0) c.BaseX = c.Box.Left;
            float p = Stagger(idx++);
            c.Box.Left = c.BaseX + (int)((1f - p) * Ui.S(22));
            c.Box.Top = y + (int)((1f - p) * Ui.S(36));
            y += h + gap;
            if (c.Kids == null || c.Kids.Count == 0) continue;
            float op = c.OpenP;
            for (int i = 0; i < c.Kids.Count; i++)
            {
                CardRef ch = c.Kids[i];
                if (ch.BaseX < 0) ch.BaseX = ch.Box.Left;
                ch.Box.Left = ch.BaseX + (int)((1f - Stagger(idx++)) * Ui.S(18));
                ch.Box.Visible = op > 0.02f;
                ch.Box.Top = y - (int)((h + gap) * (1f - op)) + (int)((h + gap) * i * op);
                ch.OpenP = op;
            }
            y += (int)((h + gap) * c.Kids.Count * op);
        }
        listPanel.Content.Height = y + Ui.S(6);
    }
    // RoundButton 的 Click 在部分路径会触发两次，这里按 URL 去重（800ms 内同一地址只开一次）
    private static string lastUrl;
    private static double lastUrlAt = -10000;
    private static void Open(string url)
    {
        if (string.IsNullOrEmpty(url)) return;
        if (url == lastUrl && Ui.Now - lastUrlAt < 800) return;
        lastUrl = url; lastUrlAt = Ui.Now;
        Ui.ShellOpen(url);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Ui.DarkTitleBar(Handle);
        Ui.FadeIn(this);
    }
}
// ===================================================================== 关闭确认（无边框小窗）
internal class CloseChoiceForm : Form
{
    public bool HideTray;
    public bool Remember;
    public bool KillGame;
    private DateTime shownAt = DateTime.MinValue;
    private bool TooEarly { get { return (DateTime.Now - shownAt).TotalMilliseconds < 120; } }

    private Point dragFrom;
    private bool dragging;

    // 统一出口：拖动过弹窗也只需点一次
    private void Choose(bool hide)
    {
        if (TooEarly) return;
        HideTray = hide;
        Capture();
        DialogResult = DialogResult.OK;
        Close();
    }

    public CloseChoiceForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        BackColor = Ui.Card;
        ForeColor = Ui.Txt;
        Font = Ui.FHint;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.None;
        ClientSize = new Size(Ui.S(348), Ui.S(206));
        ShowInTaskbar = false;
        DoubleBuffered = true;

        Label t = new Label();
        t.AutoSize = false;
        t.BackColor = Ui.Card;
        t.ForeColor = Ui.Txt;
        t.Font = new Font("Microsoft YaHei UI", 11f, FontStyle.Bold);
        t.TextAlign = ContentAlignment.MiddleLeft;
        t.Text = "关闭启动器？";
        t.SetBounds(Ui.S(20), Ui.S(14), Ui.S(240), Ui.S(26));

        Label d = new Label();
        d.AutoSize = false;
        d.BackColor = Ui.Card;
        d.ForeColor = Ui.Sub;
        d.Font = Ui.FSmall;
        d.TextAlign = ContentAlignment.TopLeft;
        d.Text = "选「隐藏到托盘」启动器会留在系统托盘；否则直接结束进程。";
        d.SetBounds(Ui.S(20), Ui.S(42), Ui.S(308), Ui.S(32));

        RoundButton bHide = new RoundButton("隐藏到托盘");
        bHide.Filled = false;
        bHide.BackColor = Ui.Card;
        bHide.Font = Ui.FSmall;
        bHide.SetBounds(Ui.S(20), Ui.S(80), Ui.S(112), Ui.S(32));
        bool pressHide = false;
        bHide.MouseDown += delegate { pressHide = true; };
        bHide.MouseUp += delegate { if (pressHide) { pressHide = false; Choose(true); } };

        RoundButton bExit = new RoundButton("直接退出");
        bExit.Filled = false;
        bExit.BackColor = Ui.Card;
        bExit.Font = Ui.FSmall;
        bExit.SetBounds(Ui.S(142), Ui.S(80), Ui.S(96), Ui.S(32));
        bool pressExit = false;
        bExit.MouseDown += delegate { pressExit = true; };
        bExit.MouseUp += delegate { if (pressExit) { pressExit = false; Choose(false); } };

        FlatCheck chkRemember = new FlatCheck("记住我的选择");
        chkRemember.Font = Ui.FSmall;
        chkRemember.BackColor = Ui.Card;
        chkRemember.ForeColor = Ui.Sub;
        chkRemember.SetBounds(Ui.S(20), Ui.S(126), Ui.S(140), Ui.S(20));

        FlatCheck chkKill = new FlatCheck("关闭启动器同时关闭游戏");
        chkKill.Font = Ui.FSmall;
        chkKill.BackColor = Ui.Card;
        chkKill.ForeColor = Ui.Sub;
        chkKill.SetBounds(Ui.S(20), Ui.S(152), Ui.S(230), Ui.S(20));

        _remember = chkRemember;
        _kill = chkKill;

        // 右上角关闭（取消）
        Label btnX = new Label();
        btnX.AutoSize = false;
        btnX.BackColor = Ui.Card;
        btnX.ForeColor = Ui.Sub;
        btnX.Font = new Font("Segoe UI", 10f);
        btnX.TextAlign = ContentAlignment.MiddleCenter;
        btnX.Text = "✕";
        btnX.Cursor = Cursors.Hand;
        btnX.SetBounds(ClientSize.Width - Ui.S(34), Ui.S(8), Ui.S(24), Ui.S(24));
        btnX.MouseEnter += delegate { btnX.ForeColor = Ui.Txt; };
        btnX.MouseLeave += delegate { btnX.ForeColor = Ui.Sub; };
        btnX.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };

        Controls.Add(t); Controls.Add(d); Controls.Add(bHide); Controls.Add(bExit); Controls.Add(btnX);
        Controls.Add(chkRemember); Controls.Add(chkKill);

        MouseEventHandler down = delegate(object s, MouseEventArgs me)
        {
            if (me.Button == MouseButtons.Left) { dragging = true; dragFrom = me.Location; }
        };
        MouseEventHandler move = delegate(object s, MouseEventArgs me)
        {
            if (!dragging) return;
            Point scr = ((Control)s).PointToScreen(me.Location);
            Location = new Point(scr.X - dragFrom.X, scr.Y - dragFrom.Y);
        };
        MouseEventHandler up = delegate(object s, MouseEventArgs me) { dragging = false; };
        this.MouseDown += down; this.MouseMove += move; this.MouseUp += up;
        foreach (Control c in Controls) { c.MouseUp += up; }
        foreach (Control c in Controls) { c.MouseDown += down; c.MouseMove += move; c.MouseUp += up; }
    }

    private FlatCheck _remember, _kill;

    private void Capture()
    {
        try
        {
            Remember = _remember != null && _remember.Checked;
            KillGame = _kill != null && _kill.Checked;
        }
        catch { }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using (Pen p = new Pen(Ui.A(Ui.Accent, 130), 1f))
            e.Graphics.DrawRectangle(p, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        shownAt = DateTime.Now;
        try { Activate(); } catch { }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); }
    }
}
internal static class ShortcutHelper
    {
        public static bool Create(string lnkPath, string target, string workDir, string args, string desc, string iconPath)
        {
            try
            {
                Type t = Type.GetTypeFromProgID("WScript.Shell");
                if (t == null) return false;
                object shell = Activator.CreateInstance(t);
                object lnk = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnkPath });
                if (lnk == null) return false;
                Type lt = lnk.GetType();
                lt.InvokeMember("TargetPath", BindingFlags.SetProperty, null, lnk, new object[] { target });
                if (!string.IsNullOrEmpty(workDir))
                    lt.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, lnk, new object[] { workDir });
                if (!string.IsNullOrEmpty(args))
                    lt.InvokeMember("Arguments", BindingFlags.SetProperty, null, lnk, new object[] { args });
                if (!string.IsNullOrEmpty(desc))
                    lt.InvokeMember("Description", BindingFlags.SetProperty, null, lnk, new object[] { desc });
                if (!string.IsNullOrEmpty(iconPath) && File.Exists(iconPath))
                    lt.InvokeMember("IconLocation", BindingFlags.SetProperty, null, lnk, new object[] { iconPath + ",0" });
                lt.InvokeMember("Save", BindingFlags.InvokeMethod, null, lnk, null);
                return true;
            }
            catch { return false; }
        }
    }

// ===================================================================== Ogg 时长（读最后一个 OggS 页的 granule + 采样率，不解码整首歌）
    
    internal static class OggMeta
{
    public static long DurationMs(string path)
    {
        try
        {
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                if (fs.Length < 200) return -1;
                int headLen = (int)Math.Min(8192, fs.Length);
                byte[] head = new byte[headLen];
                fs.Read(head, 0, headLen);
                int sr = 0;
                for (int i = 0; i + 16 < headLen; i++)
                {
                    if (head[i] == 1 && head[i + 1] == (byte)'v' && head[i + 2] == (byte)'o' &&
                        head[i + 3] == (byte)'r' && head[i + 4] == (byte)'b' && head[i + 5] == (byte)'i' &&
                        head[i + 6] == (byte)'s')
                    {
                        sr = BitConverter.ToInt32(head, i + 12);
                        break;
                    }
                }
                if (sr <= 0) return -1;

                int tailLen = (int)Math.Min(65536, fs.Length);
                byte[] tail = new byte[tailLen];
                fs.Seek(-tailLen, SeekOrigin.End);
                fs.Read(tail, 0, tailLen);
                for (int i = tailLen - 14; i >= 0; i--)
                {
                    if (tail[i] == (byte)'O' && tail[i + 1] == (byte)'g' && tail[i + 2] == (byte)'g' &&
                        tail[i + 3] == (byte)'S')
                    {
                        long granule = BitConverter.ToInt64(tail, i + 6);
                        if (granule > 0) return (long)(granule * 1000.0 / sr);
                    }
                }
            }
        }
        catch { }
        return -1;
    }
}

// ===================================================================== mod 信息缓存（第二次启动秒开）
    
    internal static class ModCache
{
    private const char OuterSep = '\t';
    private const char EntSep = ';';
    private const char FldSep = '|';

    private static readonly string[] SongSubs = { "mods\\songs", "assets\\songs", "bin\\mods\\songs", "bin\\assets\\songs" };

    // 签名：exe(时间+大小) ^ mod根目录时间 ^ 各 songs 目录(时间+子目录数) ^ 抽样曲目里的 Inst/Voices 时间
    // 这样“手动替换文件但时间没变”也能被大小/子目录数变化捕捉到
    public static long Signature(ModEntry m)
    {
        long sig = 0;
        try
        {
            FileInfo fi = new FileInfo(m.Exe);
            sig ^= fi.LastWriteTimeUtc.Ticks;
            sig ^= unchecked(fi.Length * 31L);
        }
        catch { }
        try { sig ^= Directory.GetLastWriteTimeUtc(m.Dir).Ticks; } catch { }

        List<string> roots = new List<string>();
        roots.Add(m.Dir);
        try
        {
            string exeDir = Path.GetDirectoryName(m.Exe);
            if (!string.IsNullOrEmpty(exeDir) && !string.Equals(exeDir, m.Dir, StringComparison.OrdinalIgnoreCase)) roots.Add(exeDir);
        }
        catch { }

        foreach (string root in roots)
        {
            foreach (string sub in SongSubs)
            {
                try
                {
                    string p = Path.Combine(root, sub);
                    if (!Directory.Exists(p)) continue;
                    sig ^= Directory.GetLastWriteTimeUtc(p).Ticks;
                    string[] songDirs = Directory.GetDirectories(p);
                    sig ^= unchecked(songDirs.Length * 17L);
                    int sampled = 0;
                    foreach (string sd in songDirs)
                    {
                        if (sampled >= 8) break;
                        sampled++;
                        try
                        {
                            string inst = Path.Combine(sd, "Inst.ogg");
                            if (File.Exists(inst))
                            {
                                FileInfo ii = new FileInfo(inst);
                                sig ^= unchecked(ii.Length * 7L + ii.LastWriteTimeUtc.Ticks);
                            }
                            string voices = Path.Combine(sd, "Voices.ogg");
                            if (File.Exists(voices))
                            {
                                FileInfo vi = new FileInfo(voices);
                                sig ^= unchecked(vi.Length * 3L + vi.LastWriteTimeUtc.Ticks);
                            }
                        }
                        catch { }
                    }
                }
                catch { }
            }
        }
        return sig;
    }
    public static int Hits { get; private set; }
    public static void ResetStats() { Hits = 0; }

    public static bool Load(Config cfg, ModEntry m)
    {
        if (m == null || m.IsWeb) return false;
        string raw = cfg.Get("modcache." + m.Name, "");
        if (raw.Length == 0) return false;
        string[] p = raw.Split(OuterSep);
        if (p.Length < 10) return false;

        long sig;
        if (!long.TryParse(p[0], out sig)) return false;
        if (sig != Signature(m)) return false;

        int files, videos;
        long size, dirmod;
        int.TryParse(p[1], out files);
        long.TryParse(p[2], out size);
        long.TryParse(p[3], out dirmod);
        int.TryParse(p[4], out videos);

        m.FileCount = files;
        m.SizeBytes = size;
        m.DirModifiedTicks = dirmod;
        m.VideoCount = videos;
        m.EngineName = p[5];
        m.EngineVersion = p[6];
        m.EngineNote = p[7];
        m.EngineTech = p[8];
        m.EngineKnown = m.EngineName.Length > 0;

        m.Songs.Clear();
        if (p[9].Length > 0)
        {
            foreach (string e in p[9].Split(EntSep))
            {
                if (e.Length == 0) continue;
                string[] f = e.Split(FldSep);
                if (f.Length < 6) continue;
                SongInfo s = new SongInfo();
                s.Name = f[0];
                s.Dir = f[1];
                s.Inst = f[2];
                s.Voices = f[3];
                long ms;
                long.TryParse(f[4], out ms);
                s.LengthMs = ms;
                s.IsVanilla = f[5] == "1";
                m.Songs.Add(s);
            }
        }

        m.Previews = new List<PreviewImage>();
        if (p.Length > 10 && p[10].Length > 0)
        {
            foreach (string path in p[10].Split(EntSep))
            {
                if (path.Length == 0) continue;
                PreviewImage pi = new PreviewImage();
                pi.Path = path;
                pi.Caption = Path.GetFileName(path);
                m.Previews.Add(pi);
            }
        }
        Hits++;
        return true;
    }

    public static void Save(Config cfg, ModEntry m)
    {
        if (m == null || m.IsWeb) return;
        StringBuilder sb = new StringBuilder();
        sb.Append(Signature(m)).Append(OuterSep);
        sb.Append(m.FileCount).Append(OuterSep);
        sb.Append(m.SizeBytes).Append(OuterSep);
        sb.Append(m.DirModifiedTicks).Append(OuterSep);
        sb.Append(m.VideoCount).Append(OuterSep);
        sb.Append(m.EngineName).Append(OuterSep);
        sb.Append(m.EngineVersion).Append(OuterSep);
        sb.Append(m.EngineNote).Append(OuterSep);
        sb.Append(m.EngineTech).Append(OuterSep);

        StringBuilder songs = new StringBuilder();
        foreach (SongInfo s in m.Songs)
        {
            if (songs.Length > 0) songs.Append(EntSep);
            songs.Append(s.Name).Append(FldSep).Append(s.Dir).Append(FldSep).Append(s.Inst).Append(FldSep)
                 .Append(s.Voices).Append(FldSep).Append(s.LengthMs).Append(FldSep).Append(s.IsVanilla ? "1" : "0");
        }
        sb.Append(songs.ToString()).Append(OuterSep);

        StringBuilder pv = new StringBuilder();
        if (m.Previews != null)
            foreach (PreviewImage pi in m.Previews)
            {
                if (pv.Length > 0) pv.Append(EntSep);
                pv.Append(pi.Path);
            }
        sb.Append(pv.ToString());

        cfg.Set("modcache." + m.Name, sb.ToString());
    }

    public static void Clear(Config cfg)
    {
        foreach (string k in cfg.KeysStartingWith("modcache.")) cfg.Remove(k);
        foreach (string k in cfg.KeysStartingWith("eng.")) cfg.Remove(k);
        foreach (string k in cfg.KeysStartingWith("engm.")) cfg.Remove(k);
        foreach (string k in cfg.KeysStartingWith("sz.")) cfg.Remove(k);
        cfg.Save();
    }
}

// ===================================================================== 原版曲目名单
    
    internal static class VanillaSongs
{
    private static readonly HashSet<string> set = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "blammed", "bopeebo", "cocoa", "dad-battle", "eggnog", "fresh", "guns", "high", "milf",
        "monster", "philly-nice", "pico", "roses", "satin-panties", "senpai", "south", "spookeez",
        "stress", "test", "thorns", "tutorial", "ugh", "winter-horrorland", "winter-horrorla"
    };

    public static bool IsVanilla(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        string n = name.Trim().ToLowerInvariant().Replace("_", "-").Replace(" ", "-");
        return set.Contains(n);
    }

    public static int Count { get { return set.Count; } }
}

// ===================================================================== 键盘统计（只数次数，不记录内容）
    
    internal static class KeyStats
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint threadId);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string name);
    [DllImport("user32.dll")]
    private static extern int GetKeyNameText(int lParam, System.Text.StringBuilder sb, int size);
    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint code, uint mapType);

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;

    private static IntPtr hook = IntPtr.Zero;
    private static HookProc proc;

    private static readonly Dictionary<int, int> counts = new Dictionary<int, int>();          // 总计
    private static readonly Dictionary<string, Dictionary<int, int>> perMod =
        new Dictionary<string, Dictionary<int, int>>(StringComparer.OrdinalIgnoreCase);         // 按模组

    public const string Unassigned = "（未归属）";

    // 启动某个模组时设成它的名字；没有在跑就置空
    public static string CurrentMod = "";

    public static bool Enabled { get { return hook != IntPtr.Zero; } }
    public static long Total { get; private set; }

    public static void Start()
    {
        if (hook != IntPtr.Zero) return;
        proc = HookCallback;
        hook = SetWindowsHookEx(WH_KEYBOARD_LL, proc, GetModuleHandle(null), 0);
    }

    public static void Stop()
    {
        if (hook == IntPtr.Zero) return;
        UnhookWindowsHookEx(hook);
        hook = IntPtr.Zero;
        proc = null;
    }

    private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode >= 0 && (wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN))
            {
                int vk = Marshal.ReadInt32(lParam);
                if (vk > 0)
                {
                    int c;
                    counts.TryGetValue(vk, out c);
                    counts[vk] = c + 1;
                    Total++;

                    string mod = (CurrentMod == null || CurrentMod.Length == 0) ? Unassigned : CurrentMod;
                    if (!perMod.ContainsKey(mod)) perMod[mod] = new Dictionary<int, int>();
                    Dictionary<int, int> d = perMod[mod];
                    d.TryGetValue(vk, out c);
                    d[vk] = c + 1;
                }
            }
        }
        catch { }
        return CallNextHookEx(hook, nCode, wParam, lParam);
    }

    public static void Clear() { counts.Clear(); Total = 0; perMod.Clear(); }

    public static List<KeyValuePair<int, int>> Top(int max)
    {
        List<KeyValuePair<int, int>> list = new List<KeyValuePair<int, int>>(counts);
        list.Sort(delegate(KeyValuePair<int, int> a, KeyValuePair<int, int> b) { return b.Value.CompareTo(a.Value); });
        if (list.Count > max) list.RemoveRange(max, list.Count - max);
        return list;
    }

    public static int AllCount { get { return counts.Count; } }

    // ---------------- 按模组
    public static List<string> Mods()
    {
        List<string> list = new List<string>(perMod.Keys);
        list.Sort(delegate(string a, string b) { return ModTotal(b).CompareTo(ModTotal(a)); });
        return list;
    }

    public static long ModTotal(string mod)
    {
        long sum = 0;
        Dictionary<int, int> d;
        if (perMod.TryGetValue(mod, out d)) foreach (KeyValuePair<int, int> kv in d) sum += kv.Value;
        return sum;
    }

    public static List<KeyValuePair<int, int>> ModTop(string mod, int max)
    {
        Dictionary<int, int> d;
        if (!perMod.TryGetValue(mod, out d)) return new List<KeyValuePair<int, int>>();
        List<KeyValuePair<int, int>> list = new List<KeyValuePair<int, int>>(d);
        list.Sort(delegate(KeyValuePair<int, int> a, KeyValuePair<int, int> b) { return b.Value.CompareTo(a.Value); });
        if (list.Count > max) list.RemoveRange(max, list.Count - max);
        return list;
    }

    public static void ClearMod(string mod)
    {
        if (mod == null) return;
        if (perMod.ContainsKey(mod)) perMod.Remove(mod);
    }

    public static string KeyName(int vk)
    {
        try
        {
            StringBuilder sb = new StringBuilder(64);
            int l = (int)(MapVirtualKey((uint)vk, 0) << 16);
            if (GetKeyNameText(l, sb, 64) > 0 && sb.Length > 0) return sb.ToString();
        }
        catch { }
        return "VK" + vk;
    }

    // launcher.ini: keys.<vk>=n  （总计） / km.<模组>.<vk>=n （按模组）
    public static void Save(Config cfg)
    {
        cfg.Set("keytotal", (long)Total);
        foreach (KeyValuePair<int, int> kv in counts) cfg.Set("keys." + kv.Key, kv.Value);
        foreach (string k in cfg.KeysStartingWith("km.")) cfg.Remove(k);
        foreach (KeyValuePair<string, Dictionary<int, int>> m in perMod)
            foreach (KeyValuePair<int, int> kv in m.Value)
                cfg.Set("km." + m.Key + "." + kv.Key, kv.Value);
    }

    public static void Load(Config cfg, Dictionary<int, int> into)
    {
        into.Clear();
        Total = 0;
        foreach (string k in cfg.KeysStartingWith("keys."))
        {
            int vk, n;
            if (!int.TryParse(k.Substring(5), out vk)) continue;
            n = cfg.GetInt(k, 0);
            if (n <= 0) continue;
            into[vk] = n;
            Total += n;
        }
        // 按模组
        perMod.Clear();
        foreach (string k in cfg.KeysStartingWith("km."))
        {
            string rest = k.Substring(3);
            int dot = rest.LastIndexOf('.');
            if (dot <= 0) continue;
            string mod = rest.Substring(0, dot);
            int vk, n;
            if (!int.TryParse(rest.Substring(dot + 1), out vk)) continue;
            n = cfg.GetInt(k, 0);
            if (n <= 0) continue;
            if (!perMod.ContainsKey(mod)) perMod[mod] = new Dictionary<int, int>();
            perMod[mod][vk] = n;
        }
    }

    public static void Import(Dictionary<int, int> map)
    {
        counts.Clear();
        Total = 0;
        if (map == null) return;
        foreach (KeyValuePair<int, int> kv in map) { counts[kv.Key] = kv.Value; Total += kv.Value; }
    }

    public static void ResetAll(Config cfg)
    {
        counts.Clear();
        Total = 0;
        perMod.Clear();
        foreach (string k in cfg.KeysStartingWith("keys.")) cfg.Remove(k);
        foreach (string k in cfg.KeysStartingWith("km.")) cfg.Remove(k);
        cfg.Set("keytotal", 0L);
        cfg.Save();
    }
}
// ===================================================================== 回收站 / 诊断
internal static class RecycleBin
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
        [MarshalAs(UnmanagedType.LPWStr)] public string pTo;
        public ushort fFlags;
        public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT op);

    public static bool Delete(string path)
    {
        try
        {
            SHFILEOPSTRUCT op = new SHFILEOPSTRUCT();
            op.wFunc = 3;                              // FO_DELETE
            op.pFrom = path + "\0\0";                  // 双 null 结尾
            op.fFlags = 0x0040 | 0x0010 | 0x0400;      // ALLOWUNDO | NOCONFIRMATION | NOERRORUI
            return SHFileOperation(ref op) == 0;
        }
        catch { return false; }
    }
}

internal static class Diag
{
    private static readonly List<string> lines = new List<string>();

    public static void Add(string s)
    {
        lock (lines)
        {
            if (lines.Count < 500) lines.Add(DateTime.Now.ToString("HH:mm:ss") + "  " + s);
        }
    }

    public static string Dump(string root, List<ModEntry> mods)
    {
        StringBuilder sb = new StringBuilder();
        sb.AppendLine("FNF 模组启动器 诊断信息");
        sb.AppendLine("版本: v" + Version4.AppVersion);
        sb.AppendLine("时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        sb.AppendLine("系统: " + Environment.OSVersion.VersionString + "   .NET " + Environment.Version);
        sb.AppendLine("识别目录: " + root);
        sb.AppendLine("界面缩放: " + Ui.Zoom.ToString("0.##") + "    DPI 缩放: " + Ui.Scale.ToString("0.##"));
        sb.AppendLine();
        sb.AppendLine("== 模组 ==");
        foreach (ModEntry m in mods)
        {
            sb.AppendLine("- " + m.Title + " | " + (m.IsWeb ? m.Url : m.Exe) + " | " + m.EngineDisplay +
                          " | 文件 " + m.FileCount + " | 曲目 " + m.Songs.Count + " | 体积 " + m.SizeBytes +
                          (m.ExeMissing ? " | exe 丢失" : ""));
        }
        sb.AppendLine();
        sb.AppendLine("== 记录 ==");
        lock (lines) { foreach (string l in lines) sb.AppendLine(l); }
        return sb.ToString();
    }
}
internal static class SavePaths
{
    public static List<string> Candidates(ModEntry m)
    {
        List<string> list = new List<string>();
        try
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string exeName = Path.GetFileNameWithoutExtension(m.Exe);
            list.Add(Path.Combine(appData, exeName));
            list.Add(Path.Combine(appData, "ShadowMario", "PsychEngine"));
            list.Add(Path.Combine(appData, "Psych Engine"));
            list.Add(Path.Combine(appData, "FNF"));
            list.Add(Path.Combine(appData, "Macromedia", "Flash Player", "#SharedObjects"));
            list.Add(Path.Combine(appData, "Macromedia", "Flash Player"));
            list.Add(Path.Combine(m.Dir, "saves"));
            list.Add(Path.Combine(m.Dir, "mods", "saves"));
            list.Add(Path.Combine(m.Dir, "bin", "saves"));
        }
        catch { }
        return list;
    }

    public static string Find(ModEntry m)
    {
        foreach (string p in Candidates(m))
        {
            try { if (Directory.Exists(p)) return p; }
            catch { }
        }
        return null;
    }
}

// ===================================================================== 键盘统计窗口
    
    internal class KeyStatsForm : Form
{
    private readonly List<KeyValuePair<int, int>> top;
    private readonly long total;
    private readonly int allKeys;

    public KeyStatsForm(Dictionary<int, int> map, long totalCount)
    {
        total = totalCount;
        allKeys = map.Count;
        top = new List<KeyValuePair<int, int>>(map);
        top.Sort(delegate(KeyValuePair<int, int> a, KeyValuePair<int, int> b) { return b.Value.CompareTo(a.Value); });
        if (top.Count > 10) top.RemoveRange(10, top.Count - 10);

        Text = "键盘统计";
        BackColor = Ui.Bar;
        ForeColor = Ui.Txt;
        Font = Ui.FHint;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.None;
        ClientSize = new Size(Ui.S(520), Ui.S(600));
        DoubleBuffered = true;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(BackColor);

        Ui.Text(g, "键盘统计", new Font("Microsoft YaHei UI", 13f, FontStyle.Bold),
            new Rectangle(Ui.S(24), Ui.S(14), Ui.S(300), Ui.S(30)), Ui.Txt, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
        Ui.Text(g, "总计 " + total.ToString("N0") + " 次 · " + allKeys + " 个键（只统计次数，不记录输入内容）",
            Ui.FSmall, new Rectangle(Ui.S(24), Ui.S(44), Ui.S(470), Ui.S(20)), Ui.Sub,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter);

        // ---------- 总计
        int y = Ui.S(72);
        long max = top.Count > 0 ? Math.Max(1, top[0].Value) : 1;
        foreach (KeyValuePair<int, int> kv in top)
        {
            Ui.Text(g, KeyStats.KeyName(kv.Key), Ui.FHint, new Rectangle(Ui.S(24), y, Ui.S(90), Ui.S(20)), Ui.Txt,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            int barW = (int)((Ui.S(250)) * ((double)kv.Value / max));
            Ui.FillRound(g, new Rectangle(Ui.S(120), y + Ui.S(5), Math.Max(2, barW), Ui.S(10)), Ui.S(5), Ui.A(Ui.Accent, 200));
            Ui.Text(g, kv.Value.ToString("N0"), Ui.FSmall, new Rectangle(Ui.S(382), y, Ui.S(80), Ui.S(20)), Ui.Sub,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
            y += Ui.S(21);
        }
        if (top.Count == 0)
            Ui.Text(g, "还没有数据（在设置里勾选「统计键盘按键次数」后开始记录）", Ui.FSmall,
                new Rectangle(Ui.S(24), y, Ui.S(470), Ui.S(30)), Ui.Sub,
                TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak);

        // ---------- 按模组
        y += Ui.S(10);
        using (Pen p = new Pen(Ui.Line)) g.DrawLine(p, Ui.S(24), y, Ui.S(470), y);
        y += Ui.S(8);
        Ui.Text(g, "按模组", Ui.FHint, new Rectangle(Ui.S(24), y, Ui.S(200), Ui.S(22)), Ui.Accent,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
        y += Ui.S(24);

        List<string> mods = KeyStats.Mods();
        if (mods.Count == 0)
        {
            Ui.Text(g, "还没有模组的统计数据（启动一次游戏后再按键才会记录）", Ui.FSmall,
                new Rectangle(Ui.S(24), y, Ui.S(470), Ui.S(24)), Ui.Sub,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            return;
        }

        foreach (string mod in mods)
        {
            if (y > ClientSize.Height - Ui.S(40)) break;
            long mt = KeyStats.ModTotal(mod);
            string line = mod;
            List<KeyValuePair<int, int>> mk = KeyStats.ModTop(mod, 3);
            string detail = "";
            foreach (KeyValuePair<int, int> kv in mk)
                detail += (detail.Length > 0 ? " · " : "") + KeyStats.KeyName(kv.Key) + " " + kv.Value.ToString("N0");

            Ui.Text(g, line, Ui.FHint, new Rectangle(Ui.S(24), y, Ui.S(300), Ui.S(20)), Ui.Txt,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            Ui.Text(g, mt.ToString("N0") + " 次", Ui.FSmall, new Rectangle(Ui.S(380), y, Ui.S(90), Ui.S(20)), Ui.Accent,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
            Ui.Text(g, detail, Ui.FSmall, new Rectangle(Ui.S(24), y + Ui.S(18), Ui.S(446), Ui.S(18)), Ui.Sub,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            y += Ui.S(40);
        }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Ui.DarkTitleBar(Handle);
        Ui.FadeIn(this);
    }
}
    // ===================================================================== 下载中心：分类指示条
    internal class CatMark : Control
    {
        public CatMark()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Ui.Bg;
            Enabled = false;          // 不拦鼠标，点穿到按钮
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BackColor);
            Ui.FillRound(g, new Rectangle(0, 0, Width, Height), Ui.S(2), Ui.Accent);
        }
    }
}
