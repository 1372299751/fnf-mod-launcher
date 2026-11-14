// FNF 模组启动器 / FNF Mod Launcher  v2
// 左列表 + 右预览 · 可改识别路径 · 过渡动画 · 强调色 · 隐藏/最近/启动参数/拖入添加
//
// 编译：
//   csc /target:winexe /optimize+ /win32icon:"assets\launcher.ico"
//       /out:"dist\FNF启动器.exe" src\*.cs

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace FnfLauncher
{
    internal interface IAnimatable
    {
        bool Animate();
    }

    // ===================================================================== 主题
    
    internal static class Ui
    {
        public static float Scale = 1f;
        public static float Zoom = 1f;          // 界面缩放（+ / - 调整）
        public static int S(float v) { return (int)Math.Round(v * Scale * Zoom); }

        // ---- 色阶系统 ----
        // 全界面只允许出现「一个色相 + N 级明度」的颜色，其它一律由此推导。
        // 好处：换强调色时整个界面（包括深色底、卡片、描边、副文字）会一起偏色，
        // 不会再出现"粉色调色板 + 中性灰底"这种两套色系打架的情况。
        public static Color Accent;
        public static Color AccentDark;
        public static float Hue = 339f, Sat = 0.82f;      // 强调色的 HSL

        public static Color Bg;        // 最底
        public static Color Bar;       // 顶栏 / 底栏
        public static Color Card;      // 卡片
        public static Color CardHi;    // 卡片悬停 / 次级按钮
        public static Color Txt;       // 主文字
        public static Color Sub;       // 副文字
        public static Color Line;      // 描边 / 分割
        public static Color Ramp5, Ramp6, Ramp7, Ramp8;    // 由强调色稀释出的浅阶（标签、徽章、图标底色）
        public static Color Glow;                          // 强调色的极淡底

        // ---- 背景图（外观设置）----
        // WinForms 的子控件都是不透明的，做不了"整窗半透明"。
        // 解法：每个大控件各自画同一张图，按自己在窗口里的位置偏移，拼起来就是一张完整的背景。
        public static Image BackImage;
        public static int BackAlpha = 110;

        // 统一的"外部打开"入口：同一目标 800ms 内只启动一次（按钮 Click 可能触发两次）
        private static string lastShellKey;
        private static double lastShellAt = -10000;

        public static void ShellOpen(string file, string args = null)
        {
            string key = (args ?? "") + "|" + file;
            if (key == lastShellKey && Now - lastShellAt < 1200) return;
            lastShellKey = key; lastShellAt = Now;
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(file);
                if (!string.IsNullOrEmpty(args)) psi.Arguments = args;
                psi.UseShellExecute = true;
                Process.Start(psi);
            }
            catch { }
        }
        public static bool CornerFreeze;

        // 把控件内容裁剪成"窗口圆角形状"，四角先用窗口背后的桌面像素填上 → 视觉上是抗锯齿圆角
        // 圆角统一交给窗口 Region 裁剪，这里只画内容
        public static void PaintWithRoundedCorners(Graphics g, Control c, Action<Graphics> body)
        {
            body(g);
        }
        /// <summary>
        /// 画窗口四角：先用桌面截图铺在角上，再把圆角形状用同一个画刷"填充"回去。
        /// 关键点是 SetClip 用的是方形（无锯齿），圆弧本身由 FillPath 抗锯齿渲染。
        /// </summary>
        public static void PaintCornerAA(Graphics g, Control c, Brush brush, bool top)
        {
            Form f = c.FindForm();
            if (f == null || c.Width < 4 || c.Height < 4) return;
            if (CornerFreeze) return;
            int r = S(10);
            Rectangle formRect = new Rectangle(-c.Left, -c.Top, f.ClientSize.Width, f.ClientSize.Height);
            using (GraphicsPath full = Round(formRect, r))
            {
                Rectangle[] squares;
                if (top)
                    squares = new Rectangle[] { new Rectangle(0, 0, r, r), new Rectangle(c.Width - r, 0, r, r) };
                else
                    squares = new Rectangle[] { new Rectangle(0, c.Height - r, r, r), new Rectangle(c.Width - r, c.Height - r, r, r) };

                foreach (Rectangle sq in squares)
                {
                    Rectangle area = Rectangle.Intersect(sq, c.ClientRectangle);
                    if (area.Width <= 1 || area.Height <= 1) continue;
                    try
                    {
                        Point sp = c.PointToScreen(area.Location);
                        using (Bitmap shot = new Bitmap(area.Width, area.Height))
                        {
                            using (Graphics sg = Graphics.FromImage(shot)) sg.CopyFromScreen(sp.X, sp.Y, 0, 0, area.Size);
                            g.DrawImageUnscaled(shot, area.Location);
                        }
                    }
                    catch { continue; }

                    GraphicsState st = g.Save();
                    g.SetClip(area, CombineMode.Replace);
                    SmoothingMode old = g.SmoothingMode;
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.FillPath(brush, full);
                    g.SmoothingMode = old;
                    g.Restore(st);
                }
            }
        }
        private static Bitmap BackCache;
        private static Size backCacheSize;

        public static void SetBackImage(Image img) { BackImage = img; BackCache = null; }
        public static void SetBackAlpha(int a) { BackAlpha = a; BackCache = null; }

        private static void BuildBackCache(Size sz)
        {
            BackCache = new Bitmap(Math.Max(1, sz.Width), Math.Max(1, sz.Height));
            using (Graphics g = Graphics.FromImage(BackCache))
            {
                g.Clear(Bg);
                if (BackImage == null) return;
                float k = Math.Max(sz.Width / (float)BackImage.Width, sz.Height / (float)BackImage.Height);
                int w = (int)(BackImage.Width * k), h = (int)(BackImage.Height * k);
                using (ImageAttributes ia = new ImageAttributes())
                {
                    ColorMatrix cm = new ColorMatrix();
                    cm.Matrix33 = Math.Max(0, Math.Min(255, BackAlpha)) / 255f;
                    ia.SetColorMatrix(cm);
                    g.DrawImage(BackImage, new Rectangle((sz.Width - w) / 2, (sz.Height - h) / 2, w, h),
                        0, 0, BackImage.Width, BackImage.Height, GraphicsUnit.Pixel, ia);
                }
                using (SolidBrush scrim = new SolidBrush(Color.FromArgb(96, Bg)))
                    g.FillRectangle(scrim, 0, 0, sz.Width, sz.Height);
            }
            backCacheSize = sz;
        }

        public static void PaintBack(Graphics g, Control c)
        {
            g.Clear(Bg);
            if (BackImage == null || c == null) return;
            Form f = c.FindForm();
            if (f == null) return;
            Size sz = f.ClientSize;
            if (BackCache == null || backCacheSize != sz) BuildBackCache(sz);
            if (BackCache == null) return;
            Point p = c.PointToScreen(Point.Empty);
            Point o = f.PointToScreen(Point.Empty);
            g.DrawImageUnscaled(BackCache, o.X - p.X, o.Y - p.Y);
        }
        static Ui()
        {
            Accent = Color.FromArgb(255, 46, 136);
            RebuildTheme();
        }

        /// <summary>由当前 Accent 重新推导整套色阶。改强调色后必须调用。</summary>
        public static void RebuildTheme()
        {
            float h, s, l;
            ToHsl(Accent, out h, out s, out l);
            Hue = h; Sat = s;

            // 深色底：色相不变，饱和度压到 1/4 左右，亮度只取 6%~30%
            Bg = Hsl(h, s * 0.30f, 0.058f);
            Bar = Hsl(h, s * 0.26f, 0.088f);
            Card = Hsl(h, s * 0.24f, 0.125f);
            CardHi = Hsl(h, s * 0.24f, 0.178f);
            Line = Hsl(h, s * 0.20f, 0.255f);
            Txt = Hsl(h, s * 0.10f, 0.945f);
            Sub = Hsl(h, s * 0.13f, 0.630f);

            // 强调色的浅阶（对应 PCL 的 5~8 号浓度）
            Ramp5 = Hsl(h, s * 0.52f, 0.560f);
            Ramp6 = Hsl(h, s * 0.42f, 0.680f);
            Ramp7 = Hsl(h, s * 0.32f, 0.800f);
            Ramp8 = Hsl(h, s * 0.20f, 0.920f);
            Glow = Color.FromArgb(38, Accent);
            AccentDark = Hsl(h, s, 0.440f);
        }

        public static Color Hsl(float h, float s, float l)
        {
            h = ((h % 360f) + 360f) % 360f / 360f;
            if (s < 0f) s = 0f; else if (s > 1f) s = 1f;
            if (l < 0f) l = 0f; else if (l > 1f) l = 1f;
            if (s < 1e-5f) { int v = ToB(l); return Color.FromArgb(255, v, v, v); }
            float q = l < 0.5f ? l * (1f + s) : l + s - l * s;
            float p = 2f * l - q;
            return Color.FromArgb(255, ToB(HueToRgb(p, q, h + 1f / 3f)), ToB(HueToRgb(p, q, h)), ToB(HueToRgb(p, q, h - 1f / 3f)));
        }

        private static float HueToRgb(float p, float q, float t)
        {
            if (t < 0f) t += 1f;
            if (t > 1f) t -= 1f;
            if (t < 1f / 6f) return p + (q - p) * 6f * t;
            if (t < 0.5f) return q;
            if (t < 2f / 3f) return p + (q - p) * (2f / 3f - t) * 6f;
            return p;
        }

        private static int ToB(float v)
        {
            int i = (int)Math.Round(v * 255f);
            return i < 0 ? 0 : (i > 255 ? 255 : i);
        }

        public static void ToHsl(Color c, out float h, out float s, out float l)
        {
            float r = c.R / 255f, g = c.G / 255f, b = c.B / 255f;
            float max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
            l = (max + min) / 2f;
            float d = max - min;
            if (d < 1e-6f) { h = 0f; s = 0f; return; }
            s = l > 0.5f ? d / (2f - max - min) : d / (max + min);
            if (max == r) h = ((g - b) / d + (g < b ? 6f : 0f)) / 6f;
            else if (max == g) h = ((b - r) / d + 2f) / 6f;
            else h = ((r - g) / d + 4f) / 6f;
            h *= 360f;      // 统一成 0~360 度，和 Hsl() 的参数对齐
        }

        public static Font FTitle, FListName, FSub, FDetail, FBig, FBtnBig, FBtn, FHint, FSmall;

        public static void InitFonts()
        {
            const string fam = "Microsoft YaHei UI";
            float z = Zoom;
            FTitle = new Font(fam, 15f * z, FontStyle.Bold);
            FListName = new Font(fam, 10.5f * z, FontStyle.Bold);
            FDetail = new Font(fam, 16f * z, FontStyle.Bold);
            FBig = new Font(fam, 11f * z, FontStyle.Bold);
            FSub = new Font(fam, 8.5f * z);
            FSmall = new Font(fam, 8f * z);
            FHint = new Font(fam, 8.5f * z);
            FBtnBig = new Font(fam, 12f * z, FontStyle.Bold);
            FBtn = new Font(fam, 9.5f * z, FontStyle.Bold);
        }

        public static string[] AccentNames = { "pink", "cyan", "green", "purple", "blue", "orange", "red", "yellow", "teal", "mint" };

        public static void SetAccent(string name)
        {
            string n = (name ?? "").Trim().ToLowerInvariant();
            if (n.StartsWith("hue:"))
            {
                int h;
                if (int.TryParse(n.Substring(4), out h)) Accent = Hsl(h, 0.82f, 0.55f);
                RebuildTheme();
                return;
            }
            switch (n)
            {
                case "cyan": Accent = Color.FromArgb(64, 224, 255); break;
                case "green": Accent = Color.FromArgb(70, 224, 130); break;
                case "purple": Accent = Color.FromArgb(168, 112, 255); break;
                case "blue": Accent = Color.FromArgb(92, 158, 255); break;
                case "orange": Accent = Color.FromArgb(255, 154, 84); break;
                case "red": Accent = Color.FromArgb(255, 92, 92); break;
                case "yellow": Accent = Color.FromArgb(255, 206, 84); break;
                case "teal": Accent = Color.FromArgb(72, 214, 198); break;
                case "mint": Accent = Color.FromArgb(120, 226, 160); break;
                default: Accent = Color.FromArgb(255, 46, 136); break;
            }
            RebuildTheme();      // 强调色一变，整套色阶跟着重算
        }

        public static Color SwatchColor(string name)
        {
            string n = (name ?? "").ToLowerInvariant();
            if (n.StartsWith("hue:"))
            {
                int h;
                if (int.TryParse(n.Substring(4), out h)) return Hsl(h, 0.82f, 0.55f);
            }
            if (n == "cyan") return Color.FromArgb(64, 224, 255);
            if (n == "green") return Color.FromArgb(70, 224, 130);
            if (n == "purple") return Color.FromArgb(168, 112, 255);
            if (n == "blue") return Color.FromArgb(92, 158, 255);
            if (n == "orange") return Color.FromArgb(255, 154, 84);
            if (n == "red") return Color.FromArgb(255, 92, 92);
            if (n == "yellow") return Color.FromArgb(255, 206, 84);
            if (n == "teal") return Color.FromArgb(72, 214, 198);
            if (n == "mint") return Color.FromArgb(120, 226, 160);
            return Color.FromArgb(255, 46, 136);
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        // 对话框淡入（让弹窗不是硬邦邦地出现）
        public static void FadeIn(Form f)
        {
            try
            {
                f.Opacity = 0.05;
                double start = Now;
                Timer t = new Timer();
                t.Interval = 15;
                t.Tick += delegate
                {
                    float p = Clamp01((float)((Now - start) / 150.0));
                    try { f.Opacity = 0.05 + 0.95 * p; } catch { }
                    if (p >= 1f) { try { f.Opacity = 1; } catch { } t.Stop(); t.Dispose(); }
                };
                t.Start();
            }
            catch { try { f.Opacity = 1; } catch { } }
        }
        public static void DarkTitleBar(IntPtr hwnd)
        {
            try
            {
                int v = 1;
                if (DwmSetWindowAttribute(hwnd, 20, ref v, 4) != 0)
                    DwmSetWindowAttribute(hwnd, 19, ref v, 4);
            }
            catch { }
        }

        private static readonly Stopwatch Sw = Stopwatch.StartNew();
        public static double Now { get { return Sw.Elapsed.TotalMilliseconds; } }

        public static float Clamp01(float v) { return v < 0f ? 0f : (v > 1f ? 1f : v); }
        public static float EaseOut(float t) { t = Clamp01(t); float u = 1f - t; return 1f - u * u * u; }

        // 带回弹的缓动：PCL 那套"按下短促下沉、松开慢慢回弹"的骨架
        public static float EaseOutBack(float t) { return EaseOutBack(t, 1.70158f); }
        public static float EaseOutBack(float t, float p)
        {
            t = Clamp01(t) - 1f;
            return 1f + (p + 1f) * t * t * t + p * t * t;
        }

        // 按压缩放：按下 90ms 沉到 0.955，松开 340ms 用 OutBack 回弹（会轻微过冲）
        public static float PressScale(bool down, double pressAt, double releaseAt)
        {
            if (down) return 1f - 0.045f * EaseOut(Progress(pressAt, 90f));
            if (releaseAt <= 0) return 1f;
            float t = Progress(releaseAt, 360f);
            if (t >= 1f) return 1f;
            return Lerp(0.955f, 1f, EaseOutBack(t, 2.4f));
        }
        public static float Lerp(float a, float b, float t) { return a + (b - a) * t; }

        // 距 start 毫秒后的进度（0..1）
        public static float Progress(double start, float durMs)
        {
            if (durMs <= 0f) return 1f;
            return Clamp01((float)((Now - start) / durMs));
        }

        public static Color A(Color c, int alpha)
        {
            if (alpha < 0) alpha = 0;
            if (alpha > 255) alpha = 255;
            return Color.FromArgb(alpha, c);
        }

        public static GraphicsPath Round(Rectangle r, int rad)
        {
            GraphicsPath p = new GraphicsPath();
            if (rad <= 1) { p.AddRectangle(r); return p; }
            int d = Math.Min(rad * 2, Math.Min(r.Width, r.Height));
            if (d <= 0) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static void FillRound(Graphics g, Rectangle r, int rad, Color c)
        {
            if (r.Width <= 0 || r.Height <= 0) return;
            using (GraphicsPath p = Round(r, rad))
            using (SolidBrush b = new SolidBrush(c)) { g.FillPath(b, p); }
        }

        public static void StrokeRound(Graphics g, Rectangle r, int rad, Color c, float w)
        {
            if (r.Width <= 0 || r.Height <= 0) return;
            using (GraphicsPath p = Round(r, rad))
            using (Pen pen = new Pen(c, w)) { g.DrawPath(pen, p); }
        }

        // 小方框里的播放/停止图标：直接画形状，字符 ▶ / ■ 在小字号下会缩成一条竖线
        public static void PlayGlyph(Graphics g, Rectangle r, Color c)
        {
            int h = Math.Max(4, (int)(r.Height * 0.36f));
            int w = Math.Max(4, (int)(h * 0.86f));
            int cx = r.X + r.Width / 2 + w / 5;   // 三角形重心偏左，往右偏一点才像居中
            int cy = r.Y + r.Height / 2;
            using (SolidBrush b = new SolidBrush(c))
                g.FillPolygon(b, new Point[] {
                    new Point(cx - w, cy - h),
                    new Point(cx - w, cy + h),
                    new Point(cx + w / 2, cy) });
        }

        public static void StopGlyph(Graphics g, Rectangle r, Color c)
        {
            int s = Math.Max(4, (int)(r.Height * 0.30f));
            FillRound(g, new Rectangle(r.X + (r.Width - s) / 2, r.Y + (r.Height - s) / 2, s, s),
                Math.Max(1, s / 3), c);
        }

        public static void Text(Graphics g, string s, Font f, Rectangle r, Color c, TextFormatFlags flags)
        {
            s = Lang.T(s);
            TextRenderer.DrawText(g, s, f, r, c, flags | TextFormatFlags.NoPrefix);
        }
    }

    // ===================================================================== 配置
    
    internal class Config
    {
        private readonly string path;
        private readonly Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // 扫描线程用的只读快照（避免后台线程和 UI 线程同时读写同一个字典）
        public Config SnapshotForRead()
        {
            Config c = new Config(null);
            lock (map) { foreach (KeyValuePair<string, string> kv in map) c.map[kv.Key] = kv.Value; }
            return c;
        }

        public Config(string path)
        {
            this.path = path;
            try
            {
                if (!File.Exists(path)) return;
                foreach (string raw in File.ReadAllLines(path, Encoding.UTF8))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";")) continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    map[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                }
            }
            catch { }
        }

        public string Get(string key, string def)
        {
            string v;
            return map.TryGetValue(key, out v) ? v : def;
        }

        public int GetInt(string key, int def)
        {
            int v;
            return int.TryParse(Get(key, ""), out v) ? v : def;
        }

        public double GetDouble(string key, double def)
        {
            double v;
            return double.TryParse(Get(key, ""), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out v) ? v : def;
        }

        public long GetLong(string key, long def)
        {
            long v;
            return long.TryParse(Get(key, ""), out v) ? v : def;
        }

        public bool GetBool(string key, bool def)
        {
            string v = Get(key, "");
            if (v.Length == 0) return def;
            return v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase);
        }

        public void Set(string key, string value)
        {
            if (value == null) map.Remove(key); else map[key] = value;
        }

        public void Set(string key, bool value) { Set(key, value ? "1" : "0"); }

        public void Set(string key, int value) { Set(key, value.ToString()); }

        public void Set(string key, double value)
        {
            Set(key, value.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
        }

        public void Set(string key, long value) { Set(key, value.ToString()); }

        public void Remove(string key) { map.Remove(key); }

        public List<string> KeysStartingWith(string prefix)
        {
            List<string> res = new List<string>();
            foreach (string k in map.Keys)
                if (k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) res.Add(k);
            return res;
        }

        // 原子写入：先写 .tmp，成功后替换，并保留上一版 .bak，避免异常退出把配置写坏
        public void Save()
        {
            try
            {
                List<string> lines = new List<string>();
                lines.Add("# FNF mod launcher config - 可直接编辑，改完重启启动器生效");
                List<string> keys = new List<string>(map.Keys);
                keys.Sort(StringComparer.OrdinalIgnoreCase);
                foreach (string k in keys) lines.Add(k + "=" + map[k]);

                string tmp = path + ".tmp";
                string bak = path + ".bak";
                File.WriteAllLines(tmp, lines.ToArray(), new UTF8Encoding(false));

                if (File.Exists(path))
                {
                    try { File.Copy(path, bak, true); } catch { }
                    try { File.Replace(tmp, path, null); }
                    catch
                    {
                        try { File.Delete(path); } catch { }
                        File.Move(tmp, path);
                    }
                }
                else
                {
                    File.Move(tmp, path);
                }

                // 配置坏了还能从 .bak 抢救：启动时若主文件读不出内容会自动回退
                if (map.Count == 0) { try { if (File.Exists(bak)) File.Copy(bak, path, true); } catch { } }
            }
            catch { }
        }
    }

    // ===================================================================== 模组数据
    
    internal class ModEntry
    {
        public string Name;
        public string Dir;
        public string Exe;
        public string IconPath;
        public bool External;
        public Image Thumb;
        public Image Big;
        public string Args = "";
        public string WorkDir = "";
        public double LastRun;      // ticks，0 = 从未启动

        // ---- 网页版条目
        public bool IsWeb;
        public string Url = "";

        // ---- 引擎信息（后台识别后填充）
        public string EngineName = "";
        public string EngineVersion = "";
        public string EngineNote = "";
        public string EngineTech = "";
        public bool EngineKnown;

        // ---- 统计数据
        public bool Pinned;
        public int LaunchCount;
        public bool Running;
        public long SizeBytes = -1;
        public int SongCount = -1;
        public string PackName = "";
        public string PackDesc = "";

        // ---- 展示与分类
        public string DisplayName = "";      // 重命名（空 = 用文件夹名）
        public string Category = "";
        public string WebIconPath = "";
        public List<SongInfo> Songs = new List<SongInfo>();
        public bool SongsOpen;
        public bool PreviewOpen;
        public List<PreviewImage> Previews;

        public string Title { get { return DisplayName.Length > 0 ? DisplayName : Name; } }

        // ---- 更多信息
        public string Author = "";
        public int FileCount;
        public long DirModifiedTicks;
        public int VideoCount;
        public long PlaySeconds;
        public bool VanillaOpen;
        public bool ExeMissing;
        public int WeekCount;
        public int CharCount;
        public int StageCount;
        public int EventCount;
        public string WeekText = "";
        public string AchvText = "";
        public int ImgCount;
        public int AudioCount;
        public int ScriptCount;
        public int PackColorArgb;
        public int TotalNotes;

        public string EngineDisplay
        {
            get
            {
                if (IsWeb) return "网页版";
                if (EngineName.Length == 0) return "识别中…";
                if (EngineVersion.Length == 0) return EngineName;
                return EngineName + " " + EngineVersion;
            }
        }

        // 列表徽章用的短名：Psych Engine 0.6.3 -> Psych 0.6.3
        public string EngineShort
        {
            get
            {
                if (IsWeb) return "网页";
                if (EngineName.Length == 0) return "识别中";
                string n = EngineName;
                int i = n.IndexOf(" Engine", StringComparison.OrdinalIgnoreCase);
                if (i > 0) n = n.Substring(0, i);
                return EngineVersion.Length > 0 ? n + " " + EngineVersion : n;
            }
        }

        public string LaunchDir
        {
            get
            {
                if (WorkDir != null && WorkDir.Length > 0 && Directory.Exists(WorkDir)) return WorkDir;
                try { return Path.GetDirectoryName(Exe); } catch { return Dir; }
            }
        }

        public Image GetBig(int px)
        {
            if (Big != null && Big.Width == px) return Big;
            if (Big != null) { Big.Dispose(); Big = null; }
            Big = (IsWeb && string.IsNullOrEmpty(WebIconPath)) ? Icons.Globe(px) : Icons.Build(IconPath, Exe, px, Name);
            return Big;
        }

        public void Dispose()
        {
            if (Thumb != null) { Thumb.Dispose(); Thumb = null; }
            if (Big != null) { Big.Dispose(); Big = null; }
        }
    }

    // ===================================================================== 图标
    
    internal static class Icons
    {
        private sealed class Frame
        {
            public int W, H, Off, Len;
            public bool Png;
            public int Diff = -1;
        }

        public static Image Build(string icoPath, string exePath, int px, string label)
        {
            if (px < 8) px = 8;
            Bitmap bmp = new Bitmap(px, px, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.Clear(Color.Transparent);

                Image src = null;
                if (!string.IsNullOrEmpty(icoPath))
                {
                    try { src = FromIco(icoPath, px); }
                    catch { src = null; }
                }
                if (src == null && !string.IsNullOrEmpty(exePath) && File.Exists(exePath))
                {
                    try
                    {
                        using (Icon a = Icon.ExtractAssociatedIcon(exePath))
                        {
                            if (a != null)
                                using (Icon s = new Icon(a, new Size(px, px))) { src = s.ToBitmap(); }
                        }
                    }
                    catch { src = null; }
                }
                if (src != null) { using (src) { g.DrawImage(src, new Rectangle(0, 0, px, px)); } }
                else Fallback(g, px, label);
            }
            return bmp;
        }

        // 网页版条目用的地球图标
        public static Image Globe(int px)
        {
            if (px < 8) px = 8;
            Bitmap bmp = new Bitmap(px, px, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                Rectangle r = new Rectangle(px / 12, px / 12, px - px / 6, px - px / 6);
                using (SolidBrush b = new SolidBrush(Color.FromArgb(38, 44, 62))) g.FillEllipse(b, r);
                using (Pen p = new Pen(Ui.Accent, Math.Max(1.4f, px / 22f)))
                {
                    g.DrawEllipse(p, r);
                    int ry = (int)(r.Height * 0.42f);
                    g.DrawEllipse(p, new Rectangle(r.X, r.Y + (r.Height - ry) / 2, r.Width, ry));
                    g.DrawLine(p, r.X + r.Width / 2, r.Y + 1, r.X + r.Width / 2, r.Bottom - 1);
                    int inset = (int)(r.Width * 0.24f);
                    g.DrawEllipse(p, new Rectangle(r.X + inset, r.Y + 1, r.Width - inset * 2, r.Height - 2));
                }
            }
            return bmp;
        }

        // 从 png/jpg/ico 文件做图标（网页版自定义图标用）
        public static Image FromFile(string path, int px, string label)
        {
            if (px < 8) px = 8;
            Bitmap bmp = new Bitmap(px, px, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.Clear(Color.Transparent);
                try
                {
                    if (path != null && path.ToLowerInvariant().EndsWith(".ico"))
                    {
                        using (Image ico = FromIco(path, px)) g.DrawImage(ico, new Rectangle(0, 0, px, px));
                    }
                    else
                    {
                        using (Image img = Image.FromFile(path))
                        {
                            float s = Math.Min((float)px / img.Width, (float)px / img.Height);
                            int w = Math.Max(1, (int)(img.Width * s)), h = Math.Max(1, (int)(img.Height * s));
                            g.DrawImage(img, new Rectangle((px - w) / 2, (px - h) / 2, w, h));
                        }
                    }
                }
                catch { Fallback(g, px, label); }
            }
            return bmp;
        }

        private static void Fallback(Graphics g, int px, string label)
        {
            Rectangle r = new Rectangle(0, 0, px, px);
            using (GraphicsPath p = Ui.Round(r, px / 6))
            using (LinearGradientBrush b = new LinearGradientBrush(new Rectangle(0, 0, px, px),
                       Ui.Accent, Color.FromArgb(96, 40, 190), LinearGradientMode.ForwardDiagonal))
            { g.FillPath(b, p); }

            string t = string.IsNullOrEmpty(label) ? "?" : label.Substring(0, 1).ToUpperInvariant();
            using (Font f = new Font("Microsoft YaHei UI", Math.Max(8f, px / 3f), FontStyle.Bold, GraphicsUnit.Pixel))
                TextRenderer.DrawText(g, t, f, r, Color.White,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        // 解析 ico：挑“尺寸最接近目标”且“和 32x32 那一帧内容一致”的帧。
        // 有些 ico 里大尺寸帧是坏图（和真实图案完全不同的占位图），这样能自动跳过。
        private static Image FromIco(string path, int target)
        {
            byte[] data = File.ReadAllBytes(path);
            if (data.Length < 22 || data[0] != 0 || data[1] != 0 || data[2] != 1 || data[3] != 0)
                throw new InvalidDataException("not an ico");

            List<Frame> frames = new List<Frame>();
            int count = BitConverter.ToUInt16(data, 4);
            for (int i = 0; i < count; i++)
            {
                int e = 6 + i * 16;
                if (e + 16 > data.Length) break;
                Frame f = new Frame();
                f.W = data[e]; if (f.W == 0) f.W = 256;
                f.H = data[e + 1]; if (f.H == 0) f.H = 256;
                f.Len = BitConverter.ToInt32(data, e + 8);
                f.Off = BitConverter.ToInt32(data, e + 12);
                if (f.Len <= 0 || f.Off <= 0 || f.Off + f.Len > data.Length) continue;
                f.Png = f.Len > 8 && data[f.Off] == 0x89 && data[f.Off + 1] == 0x50;
                frames.Add(f);
            }
            if (frames.Count == 0) throw new InvalidDataException("no frame");

            // 基准帧：最接近系统常用的 32px
            int refIdx = 0;
            int bestD = int.MaxValue;
            for (int i = 0; i < frames.Count; i++)
            {
                int d = Math.Abs(frames[i].W - 32);
                if (d < bestD) { bestD = d; refIdx = i; }
            }

            Bitmap refBmp = null;
            try { refBmp = Decode(data, frames[refIdx]); }
            catch { refBmp = null; }

            Frame pick = null;
            int bestScore = int.MaxValue;
            for (int i = 0; i < frames.Count; i++)
            {
                Frame f = frames[i];
                int score = Math.Abs(f.W - target);
                if (refBmp != null)
                {
                    if (f.Diff < 0) f.Diff = ContentDiff(data, f, refBmp);
                    if (f.Diff > 46) score += 10000;   // 内容对不上 → 基本不用
                }
                if (score < bestScore) { bestScore = score; pick = f; }
            }
            if (refBmp != null) refBmp.Dispose();
            if (pick == null) pick = frames[0];

            Bitmap native = Decode(data, pick);
            if (native == null) throw new InvalidDataException("decode failed");
            if (native.Width == target && native.Height == target) return native;

            Bitmap scaled = new Bitmap(target, target, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(scaled))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.CompositingQuality = CompositingQuality.HighQuality;
                g.SmoothingMode = SmoothingMode.HighQuality;
                g.DrawImage(native, new Rectangle(0, 0, target, target));
            }
            native.Dispose();
            return scaled;
        }

        private static Bitmap Decode(byte[] data, Frame f)
        {
            if (f.Png)
            {
                using (MemoryStream ms = new MemoryStream(data, f.Off, f.Len))
                    return new Bitmap(ms);
            }

            int hdr = BitConverter.ToInt32(data, f.Off);
            int bw = BitConverter.ToInt32(data, f.Off + 4);
            int bhFull = BitConverter.ToInt32(data, f.Off + 8);
            int bpp = BitConverter.ToUInt16(data, f.Off + 14);
            int bh = bhFull / 2;                      // ico 内 DIB 高度是实际高度的两倍
            if (bh <= 0) bh = bhFull;
            if (hdr >= 40 && bpp == 32 && bw > 0 && bh > 0)
            {
                int rowBytes = bw * 4;
                int xorOff = f.Off + hdr;
                if (xorOff + rowBytes * bh <= data.Length)
                {
                    Bitmap bmp = new Bitmap(bw, bh, PixelFormat.Format32bppArgb);
                    BitmapData bd = bmp.LockBits(new Rectangle(0, 0, bw, bh), ImageLockMode.WriteOnly,
                                                 PixelFormat.Format32bppArgb);
                    try
                    {
                        byte[] row = new byte[rowBytes];
                        for (int y = 0; y < bh; y++)
                        {
                            Array.Copy(data, xorOff + (bh - 1 - y) * rowBytes, row, 0, rowBytes);
                            Marshal.Copy(row, 0, (IntPtr)((long)bd.Scan0 + (long)y * bd.Stride), rowBytes);
                        }
                    }
                    finally { bmp.UnlockBits(bd); }
                    return bmp;
                }
            }

            // 其它位深：包成单帧 ico 交给 Icon 解码
            using (MemoryStream ms = new MemoryStream())
            {
                BinaryWriter w = new BinaryWriter(ms);
                w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)1);
                w.Write((byte)(f.W == 256 ? 0 : f.W));
                w.Write((byte)(f.H == 256 ? 0 : f.H));
                w.Write((byte)0); w.Write((byte)0);
                w.Write((ushort)1); w.Write((ushort)32);
                w.Write(f.Len); w.Write(22);
                w.Write(data, f.Off, f.Len);
                w.Flush();
                ms.Position = 0;
                using (Icon ic = new Icon(ms))
                    return ic.ToBitmap();
            }
        }

        // 两张图缩到 16x16 灰度后的平均差异（0..255）
        private static int ContentDiff(byte[] data, Frame f, Bitmap reference)
        {
            Bitmap b = null;
            try { b = Decode(data, f); }
            catch { return 255; }
            if (b == null) return 255;
            try
            {
                using (b)
                using (Bitmap ta = Thumb16(reference))
                using (Bitmap tb = Thumb16(b))
                {
                    long sum = 0;
                    for (int y = 0; y < 16; y++)
                        for (int x = 0; x < 16; x++)
                        {
                            Color ca = ta.GetPixel(x, y);
                            Color cb = tb.GetPixel(x, y);
                            int ga = (ca.R * 30 + ca.G * 59 + ca.B * 11) / 100;
                            int gb = (cb.R * 30 + cb.G * 59 + cb.B * 11) / 100;
                            sum += Math.Abs(ga - gb);
                        }
                    return (int)(sum / 256);
                }
            }
            finally { }
        }

        private static Bitmap Thumb16(Image src)
        {
            Bitmap t = new Bitmap(16, 16, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(t))
            {
                g.Clear(Color.White);
                g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(src, new Rectangle(0, 0, 16, 16));
            }
            return t;
        }
    }

    // ===================================================================== 扫描
    
    internal static class Scanner
    {
        private static readonly string[] BadExe =
        {
            "unins", "uninst", "crash", "updater", "update", "dxwebsetup", "vcredist",
            "oalinst", "setup", "install", "7z", "ffmpeg", "vlc", "python", "java"
        };

        public static List<ModEntry> Scan(string root, List<string> extraDirs, Config cfg)
        {
            List<ModEntry> list = new List<ModEntry>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (Directory.Exists(root))
            {
                string[] dirs;
                try { dirs = Directory.GetDirectories(root); }
                catch { dirs = new string[0]; }
                Array.Sort(dirs, StringComparer.OrdinalIgnoreCase);
                foreach (string d in dirs)
                {
                    ModEntry m = Build(d, cfg);
                    if (m == null) continue;
                    if (!seen.Add(m.Dir)) { m.Dispose(); continue; }
                    list.Add(m);
                }
            }

            foreach (string d in extraDirs)
            {
                if (!Directory.Exists(d)) continue;
                ModEntry m = Build(d, cfg);
                if (m == null) continue;
                if (!seen.Add(m.Dir)) { m.Dispose(); continue; }
                m.External = true;
                list.Add(m);
            }
            return list;
        }

        // 扫描曲目：<mod>\mods\songs、<mod>\assets\songs、<mod>\bin\mods\songs ...
        public static List<SongInfo> ScanSongs(string dir, string exeDir)
        {
            List<SongInfo> list = new List<SongInfo>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<string> roots = new List<string>();
            if (!string.IsNullOrEmpty(exeDir)) roots.Add(exeDir);
            if (string.IsNullOrEmpty(exeDir) || !string.Equals(dir, exeDir, StringComparison.OrdinalIgnoreCase)) roots.Add(dir);

            string[] subs = { "mods\\songs", "assets\\songs", "bin\\mods\\songs", "bin\\assets\\songs" };
            foreach (string root in roots)
            {
                foreach (string sub in subs)
                {
                    string p = Path.Combine(root, sub);
                    if (!Directory.Exists(p)) continue;
                    string[] songDirs;
                    try { songDirs = Directory.GetDirectories(p); } catch { continue; }
                    foreach (string sd in songDirs)
                    {
                        string name = Path.GetFileName(sd);
                        if (name.Length == 0 || !seen.Add(name)) continue;
                        SongInfo si = new SongInfo();
                        si.Name = name;
                        si.Dir = sd;
                        si.Inst = PickAudio(sd, "Inst");
                        si.Voices = PickAudio(sd, "Voices");
                        if (si.Inst == null) { seen.Remove(name); continue; }
                        list.Add(si);
                    }
                }
            }
            list.Sort(delegate(SongInfo a, SongInfo b) { return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase); });
            return list;
        }

        // 统计 mods/... 下的子目录或文件数量（取各处最大值）
        private static int CountSub(string dir, string exeDir, string sub, bool dirs)
        {
            int n = 0;
            string[] roots = { dir, exeDir };
            string[] subs = { "mods", "bin\\mods", "assets", "bin\\assets" };
            foreach (string root in roots)
            {
                if (string.IsNullOrEmpty(root)) continue;
                foreach (string s in subs)
                {
                    try
                    {
                        string p = Path.Combine(root, s, sub);
                        if (!Directory.Exists(p)) continue;
                        int c = dirs ? Directory.GetDirectories(p).Length : Directory.GetFiles(p).Length;
                        if (c > n) n = c;
                    }
                    catch { }
                }
            }
            return n;
        }

        // 从谱面 json 里读 BPM
        private static int ReadBpm(string dir, string exeDir, string song)
        {
            string[] roots = { dir, exeDir };
            string[] subs = { "mods\\data", "assets\\data", "bin\\mods\\data", "bin\\assets\\data" };
            foreach (string root in roots)
            {
                if (string.IsNullOrEmpty(root)) continue;
                foreach (string s in subs)
                {
                    try
                    {
                        string p = Path.Combine(root, s, song + ".json");
                        if (!File.Exists(p)) continue;
                        string txt = File.ReadAllText(p);
                        int i = txt.IndexOf("\"bpm\"", StringComparison.OrdinalIgnoreCase);
                        if (i < 0) continue;
                        int c = txt.IndexOf(':', i);
                        if (c < 0) continue;
                        int j = c + 1;
                        while (j < txt.Length && (txt[j] == ' ' || txt[j] == '\t')) j++;
                        int end = j;
                        while (end < txt.Length && (char.IsDigit(txt[end]) || txt[end] == '.')) end++;
                        double bpm;
                        if (double.TryParse(txt.Substring(j, end - j), out bpm) && bpm > 0) return (int)Math.Round(bpm);
                    }
                    catch { }
                }
            }
            return 0;
        }

        // 富元数据：故事线 / 成就 / 资源构成 / pack 颜色 / 每首 speed+音符数
        private static void FillMeta(ModEntry m, string dir, string exeDir)
        {
            string[] roots = { dir, exeDir };
            foreach (string root in roots)
            {
                if (string.IsNullOrEmpty(root)) continue;
                foreach (string baseName in new string[] { "mods", "bin\\mods" })
                {
                    string b = Path.Combine(root, baseName);
                    if (!Directory.Exists(b)) continue;

                    // 故事线
                    try
                    {
                        string wk = Path.Combine(b, "weeks");
                        if (Directory.Exists(wk) && m.WeekText.Length == 0)
                        {
                            List<string> names = new List<string>();
                            foreach (string f in Directory.GetFiles(wk, "*.json"))
                            {
                                try
                                {
                                    string txt = File.ReadAllText(f);
                                    string s = JsonValue(txt, "storyName");
                                    if (s.Length > 0 && !names.Contains(s)) names.Add(s);
                                }
                                catch { }
                            }
                            if (names.Count > 0) m.WeekText = string.Join("\r\n", names.ToArray());
                        }
                    }
                    catch { }

                    // 成就
                    try
                    {
                        string ach = Path.Combine(b, "achievements");
                        if (Directory.Exists(ach) && m.AchvText.Length == 0)
                        {
                            List<string> names = new List<string>();
                            foreach (string f in Directory.GetFiles(ach, "*.json"))
                            {
                                try
                                {
                                    string txt = File.ReadAllText(f);
                                    string s = JsonValue(txt, "name");
                                    if (s.Length == 0) s = Path.GetFileNameWithoutExtension(f);
                                    names.Add(s);
                                }
                                catch { }
                            }
                            if (names.Count > 0) m.AchvText = string.Join("\r\n", names.ToArray());
                        }
                    }
                    catch { }

                    // 资源构成
                    try
                    {
                        m.ImgCount = CountExt(Path.Combine(b, "images"), ".png", ".jpg", ".jpeg", ".xml");
                        m.AudioCount = CountExt(Path.Combine(b, "songs"), ".ogg", ".mp3") + CountExt(Path.Combine(b, "sounds"), ".ogg", ".mp3");
                        m.ScriptCount = CountExt(Path.Combine(b, "scripts"), ".lua") + CountExt(Path.Combine(b, "custom_events"), ".lua");
                    }
                    catch { }

                    // pack 颜色
                    try
                    {
                        string pack = Path.Combine(b, "pack.json");
                        if (m.PackColorArgb == 0 && File.Exists(pack))
                        {
                            string txt = File.ReadAllText(pack);
                            int i = txt.IndexOf("\"color\"", StringComparison.OrdinalIgnoreCase);
                            if (i >= 0)
                            {
                                int lb = txt.IndexOf('[', i), rb = txt.IndexOf(']', i);
                                if (lb > 0 && rb > lb)
                                {
                                    string[] parts = txt.Substring(lb + 1, rb - lb - 1).Split(',');
                                    if (parts.Length >= 3)
                                    {
                                        int r, g, bl;
                                        if (int.TryParse(parts[0].Trim(), out r) && int.TryParse(parts[1].Trim(), out g) && int.TryParse(parts[2].Trim(), out bl))
                                            m.PackColorArgb = Color.FromArgb(r, g, bl).ToArgb();
                                    }
                                }
                            }
                        }
                    }
                    catch { }
                }
            }

            // 每首 speed / 音符数 + 总音符
            int total = 0;
            foreach (SongInfo s in m.Songs)
            {
                ReadChart(s, roots);
                total += s.Notes;
            }
            m.TotalNotes = total;
        }

        private static int CountExt(string dir, params string[] exts)
        {
            try
            {
                if (!Directory.Exists(dir)) return 0;
                int n = 0;
                HashSet<string> set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string e in exts) set.Add(e);
                foreach (string f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
                    if (set.Contains(Path.GetExtension(f).ToLowerInvariant())) n++;
                    if (n >= 4000) break;      // 够用了，别为了精确数字扫几万个文件
                }
                return n;
            }
            catch { return 0; }
        }

        private static string JsonValue(string txt, string key)
        {
            try
            {
                int i = txt.IndexOf("\"" + key + "\"", StringComparison.OrdinalIgnoreCase);
                if (i < 0) return "";
                int c = txt.IndexOf(':', i);
                if (c < 0) return "";
                int q1 = txt.IndexOf('"', c + 1);
                if (q1 < 0) return "";
                int q2 = txt.IndexOf('"', q1 + 1);
                if (q2 < 0) return "";
                return txt.Substring(q1 + 1, q2 - q1 - 1).Trim();
            }
            catch { return ""; }
        }

        private static void ReadChart(SongInfo s, string[] roots)
        {
            foreach (string root in roots)
            {
                if (string.IsNullOrEmpty(root)) continue;
                foreach (string sub in new string[] { "mods\\data", "assets\\data", "bin\\mods\\data", "bin\\assets\\data" })
                {
                    try
                    {
                        string p = Path.Combine(root, sub, s.Name + ".json");
                        if (!File.Exists(p)) continue;
                        string txt = File.ReadAllText(p);
                        int i = txt.IndexOf("\"speed\"", StringComparison.OrdinalIgnoreCase);
                        if (i >= 0)
                        {
                            int c = txt.IndexOf(':', i);
                            int j = c + 1;
                            while (j < txt.Length && (txt[j] == ' ' || txt[j] == '\t')) j++;
                            int end = j;
                            while (end < txt.Length && (char.IsDigit(txt[end]) || txt[end] == '.' || txt[end] == '-')) end++;
                            double v;
                            if (double.TryParse(txt.Substring(j, end - j), out v) && v > 0) s.Speed = v;
                        }
                        // 音符数：sectionNotes 里每个音符行都是 "...],[" 结尾
                        int notes = 0, from = 0;
                        while (true)
                        {
                            int k = txt.IndexOf("],[", from, StringComparison.Ordinal);
                            if (k < 0) break;
                            notes++;
                            from = k + 3;
                        }
                        s.Notes = notes;
                        return;
                    }
                    catch { }
                }
            }
        }

        private static int CountVideos(string dir)
        {
            int n = 0;
            try { if (dir != null && Directory.Exists(Path.Combine(dir, "mods", "videos"))) n += Directory.GetFiles(Path.Combine(dir, "mods", "videos")).Length; } catch { }
            try { if (dir != null && Directory.Exists(Path.Combine(dir, "assets", "videos"))) n += Directory.GetFiles(Path.Combine(dir, "assets", "videos")).Length; } catch { }
            try { if (dir != null && Directory.Exists(Path.Combine(dir, "videos"))) n += Directory.GetFiles(Path.Combine(dir, "videos")).Length; } catch { }
            return n;
        }

        private static string PickAudio(string dir, string baseName)
        {
            string[] exts = { ".ogg", ".mp3", ".wav" };
            foreach (string e in exts)
            {
                string p = Path.Combine(dir, baseName + e);
                try { if (File.Exists(p)) return p; } catch { }
            }
            return null;
        }

        private static ModEntry Build(string dir, Config cfg)
        {
            try
            {
                DirectoryInfo di = new DirectoryInfo(dir);
                if ((di.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) return null;
                if (di.Name.StartsWith(".") || di.Name.StartsWith("_")) return null;

                string exe = FindExe(dir, di.Name);
                if (exe == null) return null;

                ModEntry m = new ModEntry();
                m.Name = di.Name;
                m.Dir = di.FullName;
                m.Exe = exe;
                m.ExeMissing = !File.Exists(exe);
                m.IconPath = FirstFile(
                    Path.Combine(dir, "icon.ico"),
                    Path.Combine(dir, "Icon.ico"),
                    Path.Combine(Path.GetDirectoryName(exe), "icon.ico"),
                    Path.Combine(Path.GetDirectoryName(exe), "Icon.ico"));
                m.Thumb = Icons.Build(m.IconPath, exe, Ui.S(46), m.Name);

                // 命中缓存：曲目/预览/体积/文件数/引擎全都直接用缓存，不再扫盘
                if (cfg != null && ModCache.Load(cfg, m) && m.FileCount > 0 && m.SizeBytes > 0 &&
                    !string.IsNullOrEmpty(m.EngineName) && !m.EngineName.StartsWith("识别中"))
                {
                    FillMeta(m, dir, Path.GetDirectoryName(exe));
                    return m;
                }

                m.Songs = ScanSongs(dir, Path.GetDirectoryName(exe));
                m.SongCount = m.Songs.Count;
                foreach (SongInfo s in m.Songs)
                {
                    s.IsVanilla = VanillaSongs.IsVanilla(s.Name);
                    s.LengthMs = OggMeta.DurationMs(s.Inst);
                    s.Bpm = ReadBpm(dir, Path.GetDirectoryName(exe), s.Name);
                }
                m.WeekCount = CountSub(dir, Path.GetDirectoryName(exe), "weeks", true);
                m.CharCount = CountSub(dir, Path.GetDirectoryName(exe), "characters", true);
                m.StageCount = CountSub(dir, Path.GetDirectoryName(exe), "stages", true);
                m.EventCount = CountSub(dir, Path.GetDirectoryName(exe), "custom_events", false);
                FillMeta(m, dir, Path.GetDirectoryName(exe));
                m.VideoCount = CountVideos(dir) + (string.Equals(dir, Path.GetDirectoryName(exe), StringComparison.OrdinalIgnoreCase) ? 0 : CountVideos(Path.GetDirectoryName(exe)));
                m.Previews = PreviewScan.Collect(m, 12);
                ReadPackInfo(m);
                return m;
            }
            catch { return null; }
        }

        public static string FirstFile(params string[] paths)
        {
            foreach (string p in paths) { try { if (File.Exists(p)) return p; } catch { } }
            return null;
        }

        // 读 Psych 系 mods/pack.json 里的名称和描述（很多 mod 会写）
        private static void ReadPackInfo(ModEntry m)
        {
            try
            {
                string p = Path.Combine(m.Dir, "mods", "pack.json");
                if (!File.Exists(p)) return;
                string txt = File.ReadAllText(p, Encoding.UTF8);
                if (txt.Length > 8000) txt = txt.Substring(0, 8000);
                m.PackName = JsonString(txt, "name");
                m.PackDesc = JsonString(txt, "description");
                if (m.PackName == "Name" || m.PackName == "name") m.PackName = "";
                if (m.PackDesc == "Description" || m.PackDesc == "description") m.PackDesc = "";
            }
            catch { }
        }

        private static string JsonString(string txt, string key)
        {
            try
            {
                int k = txt.IndexOf("\"" + key + "\"", StringComparison.OrdinalIgnoreCase);
                if (k < 0) return "";
                int colon = txt.IndexOf(':', k + key.Length + 2);
                if (colon < 0) return "";
                int q1 = txt.IndexOf('"', colon + 1);
                if (q1 < 0) return "";
                int q2 = txt.IndexOf('"', q1 + 1);
                if (q2 < 0) return "";
                string v = txt.Substring(q1 + 1, q2 - q1 - 1).Trim();
                if (v.Length > 90) v = v.Substring(0, 90) + "…";
                return v;
            }
            catch { return ""; }
        }

        private static string FindExe(string dir, string modName)
        {
            List<string> cand = new List<string>();
            try { cand.AddRange(Directory.GetFiles(dir, "*.exe")); } catch { }
            try
            {
                foreach (string sub in Directory.GetDirectories(dir))
                {
                    DirectoryInfo si = new DirectoryInfo(sub);
                    if ((si.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                    try { cand.AddRange(Directory.GetFiles(sub, "*.exe")); } catch { }
                }
            }
            catch { }
            if (cand.Count == 0) return null;

            string key = Norm(modName);
            string best = null;
            double bestScore = double.MinValue;
            string rootNorm = dir.TrimEnd('\\');

            foreach (string f in cand)
            {
                string n;
                try { n = Path.GetFileNameWithoutExtension(f); } catch { continue; }
                string nk = Norm(n);
                if (nk.Length == 0 || IsBad(nk)) continue;

                double score = 0;
                if (nk == key) score += 1000;
                else if (key.Length > 0 && nk.Contains(key)) score += 420;
                else if (key.Length > 0 && key.Contains(nk)) score += 320;

                string fd;
                try { fd = Path.GetDirectoryName(f).TrimEnd('\\'); } catch { fd = ""; }
                if (string.Equals(fd, rootNorm, StringComparison.OrdinalIgnoreCase)) score += 200;

                try { score += Math.Min(90.0, new FileInfo(f).Length / 1000000.0 * 3.0); }
                catch { }

                if (score > bestScore) { bestScore = score; best = f; }
            }
            return best;
        }

        private static string Norm(string s)
        {
            StringBuilder sb = new StringBuilder(s.Length);
            foreach (char c in s.ToLowerInvariant())
                if (char.IsLetterOrDigit(c)) sb.Append(c);
            return sb.ToString();
        }

        private static bool IsBad(string nk)
        {
            foreach (string b in BadExe)
                if (nk.StartsWith(b, StringComparison.Ordinal)) return true;
            return false;
        }
    }

    // ===================================================================== 左侧模组列表
    internal delegate void ModHandler(ModEntry m);

    
    internal class ModList : Control, IAnimatable
    {
        public readonly List<ModEntry> Items = new List<ModEntry>();
        public int SelectedIndex = -1;
        public readonly HashSet<ModEntry> Multi = new HashSet<ModEntry>();
        public int MultiCount { get { return Multi.Count; } }
        public List<ModEntry> MultiList()
        {
            List<ModEntry> r = new List<ModEntry>();
            foreach (ModEntry m in Items) if (Multi.Contains(m)) r.Add(m);
            return r;
        }
        public void ClearMulti() { if (Multi.Count > 0) { Multi.Clear(); Invalidate(); } }
        public event EventHandler SelectionChanged;
        public event ModHandler LaunchRequested;
        public event ModHandler ContextRequested;
        public event EventHandler AddRequested;
        public Action AnimRequest;

        private float scroll, scrollTarget;
        private int hoverIndex = -1;
        private double hoverAt, refreshAt = -10000;
        private bool barDrag;
        private int barDragStartY;
        private float barDragStartScroll;
        private DateTime lastClick = DateTime.MinValue;
        private Point lastClickPos;
        private int rowH, padX, barW, addH;

        // 选中光标（滑动的高亮块）
        private double pillAt = -10000;
        private int pillFrom = -1, pillTo = -1;

        private bool addHover, launchHover;
        private Rectangle launchRect, addBtnRect;

        public ModList()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Ui.Bar;
            rowH = Ui.S(76);
            padX = Ui.S(12);
            barW = Ui.S(8);
            addH = Ui.S(108);
        }

        // 缩放变化后重算行高等尺寸
        public void RefreshMetrics()
        {
            rowH = Ui.S(76);
            padX = Ui.S(12);
            barW = Ui.S(8);
            addH = Ui.S(108);
            Invalidate();
        }

        public ModEntry SelectedMod
        {
            get { return (SelectedIndex >= 0 && SelectedIndex < Items.Count) ? Items[SelectedIndex] : null; }
        }

        private int RowsH { get { return Math.Max(Ui.S(60), ClientSize.Height - addH); } }

        private int ContentH { get { return Items.Count * rowH + Ui.S(8); } }

        private bool NeedBar { get { return ContentH > RowsH; } }

        public void SetItems(List<ModEntry> items, string keepName)
        {
            int oldSel = SelectedIndex;
            Items.Clear();
            Items.AddRange(items);
            SelectedIndex = -1;
            scroll = scrollTarget = 0;
            refreshAt = Ui.Now;
            hoverIndex = -1;

            if (keepName != null)
            {
                for (int i = 0; i < Items.Count; i++)
                    if (string.Equals(Items[i].Name, keepName, StringComparison.OrdinalIgnoreCase)) { SelectedIndex = i; break; }
            }
            if (SelectedIndex < 0 && Items.Count > 0) SelectedIndex = 0;

            pillFrom = pillTo = SelectedIndex;
            pillAt = -10000;
            Invalidate();
            RequestAnim();
            if (SelectionChanged != null) SelectionChanged(this, EventArgs.Empty);
        }

        public void SelectIndex(int i, bool scrollIntoView)
        {
            if (i < 0 || i >= Items.Count) return;
            if (i == SelectedIndex) return;
            pillFrom = SelectedIndex < 0 ? i : SelectedIndex;
            pillTo = i;
            pillAt = Ui.Now;
            SelectedIndex = i;
            if (scrollIntoView) ScrollIntoView(i);
            Invalidate();
            RequestAnim();
            if (SelectionChanged != null) SelectionChanged(this, EventArgs.Empty);
        }

        public void MoveSelection(int delta)
        {
            if (Items.Count == 0) return;
            int i = SelectedIndex + delta;
            if (i < 0) i = 0;
            if (i >= Items.Count) i = Items.Count - 1;
            if (i != SelectedIndex) SelectIndex(i, true);
        }

        public void ScrollIntoView(int i)
        {
            float top = i * rowH;
            float bottom = top + rowH;
            if (top < scrollTarget) scrollTarget = top;
            else if (bottom > scrollTarget + RowsH) scrollTarget = bottom - RowsH;
            ClampScroll();
            RequestAnim();
        }

        private void ClampScroll()
        {
            float max = Math.Max(0, ContentH - RowsH);
            if (scrollTarget < 0) scrollTarget = 0;
            if (scrollTarget > max) scrollTarget = max;
        }

        public bool Animate()
        {
            bool busy = false;

            float d = scrollTarget - scroll;
            if (Math.Abs(d) > 0.35f) { scroll += d * 0.34f; Invalidate(); busy = true; }
            else if (scroll != scrollTarget) { scroll = scrollTarget; Invalidate(); }

            if (Ui.Now - refreshAt < Items.Count * 30 + 340) { Invalidate(); busy = true; }
            if (hoverIndex >= 0 && Ui.Now - hoverAt < 200) { InvalidateRow(hoverIndex); busy = true; }
            if (pillTo != pillFrom && Ui.Now - pillAt < 220) { Invalidate(); busy = true; }
            if (addHover && Ui.Now - hoverAt < 160) { Invalidate(); busy = true; }

            return busy;
        }

        private void RequestAnim() { if (AnimRequest != null) AnimRequest(); }

        private void InvalidateRow(int i)
        {
            if (i < 0) return;
            int y = (int)Math.Round(i * rowH - scroll);
            Invalidate(new Rectangle(0, Math.Max(0, y - 2), Width, rowH + 4));
        }

        private float RowAppear(int i)
        {
            double t = Ui.Now - refreshAt - i * 30;
            if (t <= 0) return 0f;
            return Ui.EaseOut((float)Math.Min(1.0, t / 300.0));
        }

        private float RowHover(int i)
        {
            if (i != hoverIndex) return 0f;
            return Ui.EaseOut(Ui.Progress(hoverAt, 160f));
        }

        // 选中高亮块当前所在的行位置（带缓动，切换时滑动过去）
        private float PillRow()
        {
            if (pillTo < 0) return -1;
            if (pillFrom == pillTo || pillAt <= -9000) return pillTo;
            float p = Ui.EaseOut(Ui.Progress(pillAt, 190f));
            return Ui.Lerp(pillFrom, pillTo, p);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            Ui.PaintBack(g, this);

            int rowsH = RowsH;

            if (Items.Count == 0)
            {
                Ui.Text(g, "还没有模组\n点下面的「＋ 添加模组」，或者直接把文件夹拖进窗口", Ui.FHint,
                    new Rectangle(Ui.S(16), rowsH / 2 - Ui.S(30), Width - Ui.S(32), Ui.S(60)),
                    Ui.Sub, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
            }
            else
            {
                // 先画滑动的高亮块（在行背景之下）
                float pr = PillRow();
                if (pr >= 0 && SelectedIndex >= 0)
                {
                    int py = (int)Math.Round(pr * rowH - scroll);
                    if (py + rowH > 0 && py < rowsH)
                    {
                        Rectangle pr2 = new Rectangle(padX, py + Ui.S(3),
                            Width - padX * 2 - (NeedBar ? barW : 0), rowH - Ui.S(6));
                        Ui.FillRound(g, pr2, Ui.S(10), Ui.A(Ui.Accent, 30));
                        Ui.StrokeRound(g, pr2, Ui.S(10), Ui.A(Ui.Accent, 90), 1f);

                        float ap = Ui.EaseOut(Ui.Progress(pillAt, 220f));
                        int full = pr2.Height - Ui.S(16);
                        int barH = Math.Max(Ui.S(6), (int)(full * Math.Max(0.3f, ap)));
                        Rectangle br = new Rectangle(pr2.X, pr2.Y + (pr2.Height - barH) / 2, Ui.S(4), barH);
                        Ui.FillRound(g, br, Ui.S(2), Ui.Accent);
                    }
                }

                for (int i = 0; i < Items.Count; i++)
                {
                    float appear = RowAppear(i);
                    if (appear <= 0f) continue;
                    int y = (int)Math.Round(i * rowH - scroll);
                    if (y > rowsH || y + rowH < 0) continue;
                    y += (int)Math.Round((1f - appear) * Ui.S(18));
                    DrawRow(g, i, y, appear);
                }

                DrawBar(g, rowsH);
            }

            DrawBottom(g, rowsH);
        }

        private void DrawRow(Graphics g, int i, int y, float appear)
        {
            ModEntry m = Items[i];
            if (y > RowsH) return;
            float hov = RowHover(i);

            Rectangle r = new Rectangle(padX, y + Ui.S(3), Width - padX * 2 - (NeedBar ? barW : 0), rowH - Ui.S(6));

            if (Multi.Contains(m))
            {
                Ui.FillRound(g, r, Ui.S(10), Ui.A(Ui.Accent, 26));
                Ui.FillRound(g, new Rectangle(r.X + Ui.S(2), r.Y + Ui.S(6), Ui.S(3), r.Height - Ui.S(12)), 2, Ui.Accent);
            }
            if (hov > 0f && i != SelectedIndex)
                Ui.FillRound(g, r, Ui.S(10), Ui.A(Color.White, (int)(9 * hov)));

            int iconS = Ui.S(46);
            int ix = r.X + Ui.S(14);
            int iy = r.Y + (r.Height - iconS) / 2;
            if (m.Thumb != null)
            {
                Rectangle ir = new Rectangle(ix, iy, iconS, iconS);
                using (GraphicsPath ip = Ui.Round(ir, Ui.S(9)))
                {
                    GraphicsState ist = g.Save();
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.SetClip(ip);
                    g.DrawImage(m.Thumb, ir);
                    g.Restore(ist);
                }
            }

            // 第一行：名称（+ 置顶星标）  +  右侧引擎徽章
            string name = m.Title;
            string eng = m.EngineShort;
            int engMax = (int)(r.Width * 0.40f);
            Size es = TextCache.Measure(g, eng, Ui.FSmall);
            int engW = Math.Min(engMax, es.Width + Ui.S(16));
            Rectangle engRect = new Rectangle(r.Right - engW - Ui.S(10), r.Y + Ui.S(11), engW, Ui.S(20));
            Ui.FillRound(g, engRect, Ui.S(6), Ui.A(m.IsWeb ? Color.FromArgb(64, 224, 255) : Ui.Accent, 26));

            int tx = ix + iconS + Ui.S(14);
            int tw = engRect.X - tx - Ui.S(10);
            if (m.Pinned)
            {
                int starW = Ui.S(16);
                Ui.Text(g, "★", Ui.FSmall, new Rectangle(tx, r.Y + Ui.S(11), starW, Ui.S(20)),
                    Ui.Accent, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
                tx += starW + Ui.S(2);
                tw -= starW + Ui.S(2);
            }
            Color nameColor = m.ExeMissing ? Color.FromArgb(150, 152, 160) : Ui.Txt;
            if (m.ExeMissing) name += "（exe 丢失）";
            Ui.Text(g, name, Ui.FListName, new Rectangle(tx, r.Y + Ui.S(10), tw, Ui.S(24)), nameColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);

            Ui.Text(g, eng, Ui.FSmall, engRect,
                m.IsWeb ? Color.FromArgb(150, 235, 255) : Ui.A(Ui.Accent, 235),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);

            // 第二行：exe（或网址）  +  右侧序号提示
            int l2w = r.Right - tx - Ui.S(10);
            string sub = m.IsWeb ? HostOf(m.Url) : Path.GetFileName(m.Exe);
            if (m.Args != null && m.Args.Length > 0) sub += "  " + m.Args;
            Ui.Text(g, sub, Ui.FSmall, new Rectangle(tx, r.Y + Ui.S(39), l2w - Ui.S(38), Ui.S(20)), Ui.Sub,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);

            if (i < 9 && !m.IsWeb)
            {
                Ui.Text(g, "Ctrl+" + (i + 1), Ui.FSmall,
                    new Rectangle(r.Right - Ui.S(52), r.Y + Ui.S(39), Ui.S(42), Ui.S(20)),
                    Color.FromArgb(96, 104, 126),
                    TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
            }

            if (m.External)
            {
                Ui.Text(g, "引用", Ui.FSmall, new Rectangle(r.Right - Ui.S(46), r.Y + Ui.S(8), Ui.S(36), Ui.S(18)),
                    Ui.A(Ui.Accent, 200), TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
            }
        }

        private static string HostOf(string url)
        {
            try
            {
                Uri u = new Uri(url);
                return u.Host.Length > 0 ? u.Host + (u.AbsolutePath.Length > 1 ? u.AbsolutePath : "") : url;
            }
            catch { return url ?? ""; }
        }

        private void DrawBar(Graphics g, int rowsH)
        {
            int ch = ContentH;
            if (ch <= rowsH) return;
            int trackX = Width - barW - Ui.S(2);
            int trackH = rowsH - Ui.S(8);
            int th = Math.Max(Ui.S(34), (int)((double)trackH * rowsH / ch));
            float max = ch - rowsH;
            int top = (int)(Ui.S(4) + (trackH - th) * (max <= 0 ? 0 : scroll / max));
            Ui.FillRound(g, new Rectangle(trackX, top, barW, th), barW / 2, Ui.A(Color.White, 60));
        }

        private void DrawAddButton(Graphics g, int rowsH)
        {
            DrawBottom(g, rowsH);
        }

        // 左栏底部：主启动键（PCL 的启动键就在左栏底部）+ 次要的"添加模组"
        private void LayoutBottom()
        {
            int top = RowsH;
            int h1 = Ui.S(52);
            launchRect = new Rectangle(padX, top + Ui.S(8), Width - padX * 2, h1);
            addBtnRect = new Rectangle(padX, launchRect.Bottom + Ui.S(8), Width - padX * 2, Ui.S(30));
        }

        private void DrawBottom(Graphics g, int rowsH)
        {
            LayoutBottom();
            bool can = SelectedMod != null;
            Color c = !can ? Ui.CardHi : (launchHover ? Ui.AccentDark : Ui.Accent);
            Ui.FillRound(g, launchRect, Ui.S(10), c);
            Ui.Text(g, can ? "▶   启动游戏" : "▶   先选一个模组", Ui.FBtnBig,
                new Rectangle(launchRect.X, launchRect.Y + Ui.S(2), launchRect.Width, launchRect.Height - Ui.S(14)),
                can ? Color.White : Ui.Sub,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
            if (can)
            {
                string sub = SelectedMod.Title;
                if (sub.Length > 20) sub = sub.Substring(0, 20) + "…";
                Ui.Text(g, sub, Ui.FSmall,
                    new Rectangle(launchRect.X, launchRect.Bottom - Ui.S(17), launchRect.Width, Ui.S(13)),
                    Ui.A(Color.White, 190),
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
            }

            Color bg = addHover ? Ui.CardHi : Ui.Card;
            Ui.FillRound(g, addBtnRect, Ui.S(8), bg);
            Ui.StrokeRound(g, addBtnRect, Ui.S(8), addHover ? Ui.A(Ui.Accent, 170) : Ui.Line, 1f);
            Ui.Text(g, "＋  添加模组", Ui.FSmall, addBtnRect, addHover ? Ui.Txt : Ui.Sub,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        }

        private int HitRow(Point p)
        {
            if (Items.Count == 0) return -1;
            if (p.Y > RowsH) return -1;
            int i = (int)Math.Floor((p.Y + scroll) / (double)rowH);
            if (i < 0 || i >= Items.Count) return -1;
            return i;
        }

        private Rectangle BarThumbRect()
        {
            int rowsH = RowsH;
            int ch = ContentH;
            int trackH = rowsH - Ui.S(8);
            int th = Math.Max(Ui.S(34), (int)((double)trackH * rowsH / Math.Max(1, ch)));
            float max = Math.Max(1, ch - rowsH);
            int top = (int)(Ui.S(4) + (trackH - th) * scroll / max);
            return new Rectangle(Width - barW - Ui.S(2), top, barW, th);
        }

        private bool InAddBar(Point p)
        {
            return p.Y >= RowsH && p.Y <= ClientSize.Height;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);

            if (barDrag)
            {
                int trackH = RowsH - Ui.S(8);
                int th = BarThumbRect().Height;
                float max = Math.Max(0, ContentH - RowsH);
                float travel = Math.Max(1, trackH - th);
                float ratio = max / travel;
                scrollTarget = scroll = Math.Max(0, Math.Min(max, barDragStartScroll + (e.Y - barDragStartY) * ratio));
                Invalidate();
                return;
            }

            bool overAdd = InAddBar(e.Location);
            LayoutBottom();
            bool overLaunch = launchRect.Contains(e.Location);
            overAdd = addBtnRect.Contains(e.Location);
            if (overAdd != addHover || overLaunch != launchHover)
            {
                addHover = overAdd; launchHover = overLaunch;
                hoverAt = Ui.Now; Invalidate(); RequestAnim();
            }

            int i = (overAdd || overLaunch) ? -1 : HitRow(e.Location);
            if (i != hoverIndex)
            {
                hoverIndex = i;
                hoverAt = Ui.Now;
                Cursor = (i >= 0 || overAdd || overLaunch) ? Cursors.Hand : Cursors.Default;
                Invalidate();
                RequestAnim();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            hoverIndex = -1;
            addHover = false;
            launchHover = false;
            Cursor = Cursors.Default;
            Invalidate();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            WheelScroll(e.Delta);
        }

        public void WheelScroll(int delta)
        {
            scrollTarget -= delta / 120f * Ui.S(96);
            ClampScroll();
            Invalidate();
            RequestAnim();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();

            if (e.Button == MouseButtons.Left)
            {
                LayoutBottom();
                if (addBtnRect.Contains(e.Location))
                {
                    if (AddRequested != null) AddRequested(this, EventArgs.Empty);
                    return;
                }
                if (launchRect.Contains(e.Location))
                {
                    if (SelectedMod != null && LaunchRequested != null) LaunchRequested(SelectedMod);
                    return;
                }
            }

            if (e.Button == MouseButtons.Left && ContentH > RowsH && BarThumbRect().Contains(e.Location))
            {
                barDrag = true;
                barDragStartY = e.Y;
                barDragStartScroll = scroll;
                Capture = true;
                return;
            }

            int i = HitRow(e.Location);
            if (i < 0) return;

            if (e.Button == MouseButtons.Right)
            {
                SelectIndex(i, false);
                if (ContextRequested != null) ContextRequested(Items[i]);
                return;
            }

            DateTime now = DateTime.Now;
            bool dbl = (now - lastClick).TotalMilliseconds < 420 &&
                       Math.Abs(e.X - lastClickPos.X) < 6 && Math.Abs(e.Y - lastClickPos.Y) < 6;
            lastClick = now;
            lastClickPos = e.Location;

            if ((Control.ModifierKeys & Keys.Control) == Keys.Control)
            {
                if (Multi.Contains(Items[i])) Multi.Remove(Items[i]); else Multi.Add(Items[i]);
                SelectIndex(i, false);
                Invalidate();
                return;
            }
            if ((Control.ModifierKeys & Keys.Shift) == Keys.Shift && MultiCount > 0)
            {
                List<ModEntry> picked = MultiList();
                int anchor = Items.IndexOf(picked[picked.Count - 1]);
                int a = Math.Min(anchor, i), b = Math.Max(anchor, i);
                for (int k2 = a; k2 <= b; k2++) Multi.Add(Items[k2]);
                SelectIndex(i, false);
                Invalidate();
                return;
            }
            Multi.Clear();
            SelectIndex(i, false);
            if (dbl && LaunchRequested != null) LaunchRequested(Items[i]);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (barDrag) { barDrag = false; Capture = false; }
            base.OnMouseUp(e);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            ClampScroll();
            Invalidate();
        }
    }

    // ===================================================================== 右侧详情
    
    internal class DetailPane : Control, IAnimatable
    {
        private ModEntry mod;
        private double changeAt = Ui.Now;
        private bool hoverLaunch, hoverOpen, pressLaunch, pressOpen;
        private Rectangle launchRect, openRect;
        private Rectangle acc1Rect, acc2Rect, songsRect, previewRect;
        private int scroll;            // 详情区滚动偏移（窗口太小时整块上下滚）
        private int contentHeight;
        private bool layoutForHit;
        private int maxScroll;
        private ScrollHint bar;
        private Rectangle infoCardRect;
        private Rectangle iconRect, titleRect, subRect, cardARect, cardBRect;
        private Point tagPoint;
        private Rectangle infoChipRect;
        private bool infoChipHover;
        private int songHover = -1, previewHover = -1;
        private bool acc1Hover, acc2Hover;
        private bool prevBtnHover, infoBtnHover;
        private float songsScroll, songsScrollTarget;
        private double lastPlayPaint;
        private readonly SongPlayer player = new SongPlayer();
        private int infoRowLimit = 6;
        public static bool FoldVanilla = true;
        public static bool UseWindow = false;      // true=弹窗，false=内嵌（合并）
        public Action<string> StatusText;
        public event EventHandler SongsWindowRequested;
        public event EventHandler PreviewWindowRequested;
        public event EventHandler InfoWindowRequested;

        public event ModHandler LaunchRequested;
        public event ModHandler OpenFolderRequested;
        public event EventHandler SongsToggled;
        public event EventHandler PreviewToggled;
        public event ModHandler PreviewOpenRequested;
        public int PreviewOpenIndex;      // 被点到的是第几张预览图
        public Action AnimRequest;

        public DetailPane()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Ui.Bg;
            bar = new ScrollHint(this);
            bar.Visible = false;
            Controls.Add(bar);
        }

        public ModEntry Mod { get { return mod; } }
        public bool IsPlaying { get { return player.IsPlaying; } }

        public void StopAudio() { }

        public void SetMod(ModEntry m)
        {
            if (m == mod) return;
            if (player.IsPlaying) player.Stop();
            mod = m;
            changeAt = Ui.Now;
            songsScroll = songsScrollTarget = 0;
            songHover = previewHover = -1;
            Invalidate();
            if (AnimRequest != null) AnimRequest();
        }

        public void Touch()
        {
            changeAt = Ui.Now;
            Invalidate();
            if (AnimRequest != null) AnimRequest();
        }

        public bool Animate()
        {
            bool busy = false;
            if (Ui.Now - changeAt < 520) { Invalidate(); busy = true; }

            float d = songsScrollTarget - songsScroll;
            if (Math.Abs(d) > 0.35f) { songsScroll += d * 0.34f; Invalidate(); busy = true; }
            else if (songsScroll != songsScrollTarget) { songsScroll = songsScrollTarget; Invalidate(); }

            if (player.IsPlaying)
            {
                // 播放中的进度条按 ~30fps 刷新，且只重画曲目区，避免整块重绘导致的卡顿
                if (Ui.Now - lastPlayPaint > 33)
                {
                    lastPlayPaint = Ui.Now;
                    if (songsRect.Height > 0 && songsRect.Width > 0) Invalidate(new Rectangle(songsRect.X, songsRect.Y, songsRect.Width, songsRect.Height));
                    else Invalidate();
                }
                busy = true;
            }
            return busy;
        }

        // ---------------------------------------------------------------- 布局
        private void LayoutAll()
        {
            int bh = Ui.S(50);
            int bottom = ClientSize.Height - bh - Ui.S(20);
            int bw = Math.Min(Ui.S(196), (int)(ClientSize.Width * 0.42f));
            launchRect = Rectangle.Empty;      // 启动键已经移到左栏底部
            openRect = new Rectangle(Ui.S(30), bottom, Math.Max(Ui.S(120), bw), bh);

            // 统一坐标源：图标 / 标题 / 副标题 / 标签 / 两张信息卡 都在这里算好，
            // 绘制与命中测试只用这些矩形，整块详情区才能安全滚动
            int step = Math.Max(4, Ui.S(8));
            bool compact = ClientSize.Height < Ui.S(540);   // 矮窗口：小图标 + 信息卡放到折叠头下面
            int accY;

            if (compact)
            {
                iconRect = new Rectangle(Ui.S(30), Ui.S(16), Ui.S(72), Ui.S(72));
                int txc = iconRect.Right + Ui.S(18);
                int twc = Math.Max(Ui.S(120), ClientSize.Width - txc - Ui.S(24));
                titleRect = new Rectangle(txc, Ui.S(20), twc, Ui.S(42));
                subRect = new Rectangle(txc, Ui.S(62), twc, Ui.S(18));
                tagPoint = new Point(txc, Ui.S(82));
                infoChipRect = new Rectangle(ClientSize.Width - Ui.S(24) - Ui.S(112), Ui.S(54), Ui.S(112), Ui.S(22));
                cardARect = Rectangle.Empty;
                cardBRect = Rectangle.Empty;
                infoCardRect = Rectangle.Empty;
                iconSize = iconRect.Width;
                infoRowLimit = 6;
                RefreshInfoRows();
                accY = Ui.S(100);
            }
            else
            {
                int iconS = Math.Min(Ui.S(120), Math.Max(Ui.S(88), ClientSize.Height - Ui.S(430)));
                iconS = Math.Max(step, (iconS / step) * step);
                iconSize = iconS;
                infoRowLimit = 6;
                RefreshInfoRows();

                iconRect = new Rectangle(Ui.S(30), Ui.S(22), iconS, iconS);
                int tx = iconRect.Right + Ui.S(24);
                int tw = Math.Max(Ui.S(100), ClientSize.Width - tx - Ui.S(26));
                titleRect = new Rectangle(tx, Ui.S(22), tw, Ui.S(70));
                subRect = new Rectangle(tx, titleRect.Y + Ui.S(74), tw, Ui.S(20));
                tagPoint = new Point(tx, subRect.Bottom + Ui.S(4));
                infoChipRect = Rectangle.Empty;

                int cardX = Ui.S(24), cardW = ClientSize.Width - Ui.S(48);
                int cardTop = iconRect.Bottom + Ui.S(6);
                cardARect = new Rectangle(cardX, cardTop, cardW, InfoCardH(infoRowsA));
                cardBRect = new Rectangle(cardX, cardARect.Bottom + Ui.S(10), cardW, InfoCardH(infoRowsB));
                infoCardRect = new Rectangle(Ui.S(14), cardARect.Y - Ui.S(4), ClientSize.Width - Ui.S(28),
                    cardBRect.Bottom - cardARect.Y + Ui.S(8));

                accY = cardBRect.Bottom + Ui.S(12);
            }
            acc1Rect = new Rectangle(Ui.S(24), accY, ClientSize.Width - Ui.S(48), Ui.S(40));
            acc2Rect = new Rectangle(Ui.S(24), accY + Ui.S(48), ClientSize.Width - Ui.S(48), Ui.S(40));

            // 文档流：折叠头 → 展开内容 → 底部按钮，总高超过可视区时可上下滚动
            bool pOpen = (mod != null && mod.PreviewOpen && !UseWindow);
            bool sOpen = (mod != null && mod.SongsOpen && !UseWindow);
            int contentTop = acc2Rect.Bottom + Ui.S(8);
            int openH = (sOpen || pOpen) ? Ui.S(300) : 0;
            if (compact)
            {
                int availH = bottom - (acc2Rect.Bottom + Ui.S(8)) - Ui.S(24) - bh;
                if (availH < Ui.S(72)) availH = Ui.S(72);
                openH = Math.Min(openH, availH);
            }
            previewRect = new Rectangle(Ui.S(24), contentTop, ClientSize.Width - Ui.S(48), pOpen ? Math.Min(Ui.S(120), openH) : 0);
            songsRect = new Rectangle(Ui.S(24), contentTop, ClientSize.Width - Ui.S(48), (pOpen || !sOpen) ? 0 : openH);

            int btnY;
            if (compact)
            {
                // 紧凑模式：按钮贴底，折叠头下面的空白用来显示信息卡；展开时信息被顶出可视区
                btnY = bottom;
                if (sOpen || pOpen) { cardARect = Rectangle.Empty; }
                else
                {
                int infoTop = contentTop + openH + Ui.S(8);
                int infoAvail = Math.Max(0, btnY - Ui.S(12) - infoTop);
                int infoH = Math.Min(InfoCardH(4), infoAvail);
                cardARect = infoH > Ui.S(30) ? new Rectangle(Ui.S(24), infoTop, ClientSize.Width - Ui.S(48), infoH) : Rectangle.Empty;
                cardBRect = Rectangle.Empty;
                }
            }
            else
            {
                btnY = contentTop + openH + Ui.S(16);
            }
            openRect = new Rectangle(Ui.S(30), btnY, Math.Max(Ui.S(120), bw), bh);
            contentHeight = btnY + bh + Ui.S(24);
            maxScroll = 0;          // 详情区不滚动（曲目列表内部自己滚）
            scroll = 0;
            if (scroll < 0) scroll = 0;
            if (scroll > maxScroll) scroll = maxScroll;

            // 命中测试：把矩形换算成屏幕坐标（绘制在离屏层里用内容坐标）
            if (layoutForHit && scroll > 0)
            {
                acc1Rect.Y -= scroll; acc2Rect.Y -= scroll; infoCardRect.Y -= scroll;
                songsRect.Y -= scroll; previewRect.Y -= scroll; openRect.Y -= scroll;
            }

        }

        // 供主窗口转发滚轮用（鼠标在详情区但焦点不在它身上时也能滚）
        public double LastWheelAt;
        public void ScrollBy(int delta)
        {
            if (maxScroll <= 0) return;
            scroll -= (int)(delta / 120f * Ui.S(96));
            if (scroll < 0) scroll = 0;
            if (scroll > maxScroll) scroll = maxScroll;
            Invalidate();
        }

        public float ScrollRatio { get { return contentHeight <= 0 ? 1f : Math.Min(1f, ClientSize.Height / (float)contentHeight); } }
        public float ScrollPos
        {
            get
            {
                int max = Math.Max(0, contentHeight - ClientSize.Height);
                return max <= 0 ? 0f : Math.Min(1f, scroll / (float)max);
            }
        }

        private int SongsVisibleRows { get { return Math.Max(0, songsRect.Height / Ui.S(30)); } }

        private int infoRowsA, infoRowsB;
        private int iconSize;

        private void RefreshInfoRows()
        {
            infoRowsA = infoRowLimit >= 6 ? 3 : 2;      // 引擎 + [分类] + 内容
            infoRowsB = 1;                              // 游玩
            if (mod != null && !mod.IsWeb)
            {
                if (infoRowLimit >= 6 && (mod.FileCount > 0 || mod.DirModifiedTicks > 0)) infoRowsB++;
                if (mod.Author.Length > 0 || mod.PackDesc.Length > 0) infoRowsB++;
            }
        }

        private int InfoCardH(int rows) { return Ui.S(34 + rows * 26); }

        private int SongsContentH
        {
            get { return mod == null ? 0 : BuildSongRows().Count * Ui.S(30) + Ui.S(6); }
        }

        // 切换模组时的滑入偏移：start 起 14px，dur 毫秒内缓出到 0
        private int SlideIn(double start, float dur)
        {
            double t = (Ui.Now - start) / dur;
            if (t <= 0) return Ui.S(14);
            if (t >= 1) return 0;
            double e = 1 - Math.Pow(1 - t, 3);
            return (int)((1 - e) * Ui.S(14));
        }

        // ---------------------------------------------------------------- 绘制
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            Ui.PaintBack(g, this);
            LayoutAll();

            // 内容高于可视区时，先画进一张整页位图，再按滚动偏移贴出来（避免坐标错位）
            Bitmap pageBmp = null;
            Graphics pageG = null;
            if (maxScroll > 0)
            {
                pageBmp = new Bitmap(Width, Math.Max(1, contentHeight), System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
                pageG = Graphics.FromImage(pageBmp);
                pageG.SmoothingMode = SmoothingMode.AntiAlias;
                pageG.InterpolationMode = InterpolationMode.HighQualityBicubic;
                Ui.PaintBack(pageG, this);
                g = pageG;
            }


            if (mod == null)
            {
                Ui.Text(g, "从左侧选择一个模组", Ui.FBig,
                    new Rectangle(0, ClientSize.Height / 2 - Ui.S(20), Width, Ui.S(40)),
                    Ui.Sub, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                return;
            }

            float p = Ui.EaseOutBack(Ui.Progress(changeAt, 330f));      // 换模组时图标/标题带一点回弹
            float p2 = Ui.EaseOutBack(Ui.Progress(changeAt + 70, 330f));

            // 信息卡 / 折叠头 / 内容 依次滑入（切换模组时的过渡动画）
            int dCard = SlideIn(changeAt + 60, 300f);
            int dHead = SlideIn(changeAt + 110, 300f);
            int dBody = SlideIn(changeAt + 160, 300f);
            cardARect.Y += dCard; cardBRect.Y += dCard;
            acc1Rect.Y += dHead; acc2Rect.Y += dHead;
            songsRect.Y += dBody; previewRect.Y += dBody;

            if (iconRect.Width <= 0) LayoutAll();
            int animX = (int)((1f - p) * Ui.S(10));
            int animY = (int)((1f - p) * Ui.S(8));
            int iconS = iconRect.Width;

            Image big = mod.GetBig(Ui.S(176));
            int scale = (int)(iconS * (0.92f + 0.08f * p));
            int off = (iconS - scale) / 2;
            if (big != null)
            {
                Rectangle ir = new Rectangle(iconRect.X + off + animX, iconRect.Y + off + animY, scale, scale);
                using (GraphicsPath ip = Ui.Round(ir, Ui.S(18)))
                {
                    GraphicsState ist = g.Save();
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.SetClip(ip);
                    g.DrawImage(big, ir);
                    g.Restore(ist);
                }
            }

            int tx = titleRect.X;
            int tw = titleRect.Width;
            int ty = titleRect.Y + (int)((1f - p2) * Ui.S(10));

            Ui.Text(g, mod.Title, Ui.FDetail, new Rectangle(tx, ty, tw, titleRect.Height), Ui.Txt,
                TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);

            int ny = subRect.Y;

            string sub = mod.PackName.Length > 0 ? mod.PackName
                : (mod.IsWeb ? (mod.Url ?? "") : Path.GetFileName(mod.Exe));
            // 副标题（网页版是网址）不能画到「查看模组信息」按钮底下，否则长网址会被压住
            int subW = tw;
            if (infoChipRect.Width > 0)
                subW = Math.Max(Ui.S(60), infoChipRect.X - Ui.S(8) - tx);
            Ui.Text(g, sub, Ui.FSub, new Rectangle(tx, ny, subW, subRect.Height), Ui.Sub,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);

            int tagX = tagPoint.X, tagY = tagPoint.Y;
            if (mod.IsWeb) { DrawTag(g, ref tagX, tagY, "网页版", Color.FromArgb(64, 224, 255)); }
            if (mod.External) { DrawTag(g, ref tagX, tagY, "外部引用", Ui.Accent); }
            if (mod.Pinned) { DrawTag(g, ref tagX, tagY, "★ 置顶", Color.FromArgb(255, 200, 80)); }

            // 紧凑模式：标题旁直接给一个「查看模组信息」按钮（以窗口形式打开）
            if (infoChipRect.Width > 0)
            {
                DrawChip(g, infoChipRect, "查看模组信息");
                if (infoChipHover)
                    Ui.StrokeRound(g, infoChipRect, Ui.S(6), Ui.A(Ui.Accent, 200), 1f);
            }

            // 信息区：拆成「基本信息」「运行统计」两张卡
            string eng = mod.EngineDisplay;
            if (mod.EngineNote.Length > 0 && !mod.IsWeb) eng += "  ·  " + mod.EngineNote;

            string cat = mod.Category.Length > 0 ? mod.Category : "未分类";

            string content;
            if (mod.IsWeb) content = "浏览器打开 · 无需下载";
            else
            {
                int vanillaCount = 0;
                long totalMs = 0;
                foreach (SongInfo s in mod.Songs) { if (s.IsVanilla) vanillaCount++; if (s.LengthMs > 0) totalMs += s.LengthMs; }
                content = "自制 " + (mod.Songs.Count - vanillaCount) + " 首 · 原版 " + vanillaCount + " 首";
                if (totalMs > 0) content += " · 总时长 " + FormatLong(totalMs);
                if (mod.WeekCount > 0) content += " · 周 " + mod.WeekCount;
                if (mod.CharCount > 0) content += " · 角色 " + mod.CharCount;
                if (mod.StageCount > 0) content += " · 舞台 " + mod.StageCount;
                if (mod.TotalNotes > 0) content += " · 音符 " + mod.TotalNotes.ToString("N0");
                if (mod.EventCount > 0) content += " · 事件 " + mod.EventCount;
                if (mod.VideoCount > 0) content += " · 视频 " + mod.VideoCount;
                content += " · 截图 " + (mod.Previews != null ? mod.Previews.Count : 0);
                if (mod.SizeBytes > 0) content += " · " + FormatSize(mod.SizeBytes);
            }

            List<string[]> cardA = new List<string[]>();
            cardA.Add(new string[] { "引擎", eng });
            if (infoRowLimit >= 6) cardA.Add(new string[] { "分类", cat });
            cardA.Add(new string[] { "内容", content });

            List<string[]> cardB = new List<string[]>();
            if (!mod.IsWeb && infoRowLimit >= 6)
            {
                string files = mod.FileCount > 0 ? mod.FileCount.ToString("N0") + " 个文件" : "";
                if (mod.DirModifiedTicks > 0)
                {
                    if (files.Length > 0) files += " · ";
                    files += "修改于 " + new DateTime(mod.DirModifiedTicks).ToString("yyyy-MM-dd");
                }
                if (files.Length > 0) cardB.Add(new string[] { "文件", files });
            }
            if (!mod.IsWeb)
            {
                string au = mod.Author;
                if (mod.PackDesc.Length > 0) au = (au.Length > 0 ? au + " · " : "") + mod.PackDesc;
                if (au.Length > 0) cardB.Add(new string[] { "作者", au });
            }
            cardB.Add(new string[] { "游玩", "启动 " + mod.LaunchCount + " 次 · 累计 " + FormatLong(mod.PlaySeconds * 1000L) +
                " · 最近 " + FormatRecent(mod.LastRun) });

            if (cardARect.Height > 0)
            {
                DrawInfoCard(g, cardARect, "基本信息", cardA);
                DrawInfoCard(g, cardBRect, "运行统计", cardB);
            }


            if (UseWindow)
            {
                DrawPanelButton(g, acc1Rect, "曲目", mod.Songs.Count + " 首", acc1Hover);
                DrawPanelButton(g, PreviewBtnRect(), "预览", (mod.Previews != null ? mod.Previews.Count : 0) + " 张", prevBtnHover);
                DrawPanelButton(g, InfoBtnRect(), "资料", "故事/成就/资源", infoBtnHover);
            }
            else
            {
            // 折叠头 1：曲目
            DrawAccHeader(g, acc1Rect, "曲目", mod.Songs.Count + " 首", mod.SongsOpen, acc1Hover);

            if (mod.SongsOpen)
            {
                if (mod.Songs.Count == 0)
                {
                    Ui.Text(g, "没有找到曲目文件夹（mods\\songs 或 assets\\songs）", Ui.FSmall,
                        new Rectangle(songsRect.X, songsRect.Y + Ui.S(4), songsRect.Width, Ui.S(24)), Ui.Sub,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
                }
                else DrawSongs(g);
            }

            // 折叠头 2：预览
            DrawAccHeader(g, PreviewBtnRect(), "预览", (mod.Previews != null ? mod.Previews.Count : 0) + " 张", mod.PreviewOpen, prevBtnHover); DrawPanelButton(g, InfoBtnRect(), "资料", "故事/成就/资源", infoBtnHover);

            if (mod.PreviewOpen && previewRect.Height > 0) DrawPreviews(g);
            }

            // 按钮（启动键在左栏底部，这里只留次级操作）
            openRect.Y += dBody;
            DrawButton(g, openRect, mod.IsWeb ? "浏览器打开" : "打开文件夹", Ui.FBtn, false, hoverOpen, pressOpen);
            openRect.Y -= dBody;

            if (pageBmp != null)
            {
                pageG.Dispose();
                e.Graphics.DrawImage(pageBmp,
                    new Rectangle(0, 0, Width, ClientSize.Height),
                    new Rectangle(0, scroll, Math.Max(1, Width), Math.Max(1, ClientSize.Height)),
                    GraphicsUnit.Pixel);
                pageBmp.Dispose();

            }

        }

        // 带小标题的信息卡，返回卡片底部 Y
        // 按矩形绘制：裁剪到框内，只画放得下的行（框高度由 LayoutAll 统一决定）
        private int DrawInfoCard(Graphics g, Rectangle r, string title, List<string[]> rows)
        {
            if (r.Width <= 0 || r.Height <= 0) return r.Bottom;
            int hdr = Ui.S(26);
            int rowH = Ui.S(26);

            GraphicsState st = g.Save();
            g.SetClip(r);
            Ui.FillRound(g, r, Ui.S(10), Color.FromArgb(120, Ui.Card));
            Ui.StrokeRound(g, r, Ui.S(10), Ui.A(Ui.Line, 100), 1f);
            Ui.Text(g, title, Ui.FSmall, new Rectangle(r.X + Ui.S(14), r.Y + Ui.S(5), r.Width - Ui.S(28), Ui.S(18)),
                Ui.A(Ui.Ramp7, 220), TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            using (SolidBrush b = new SolidBrush(Ui.A(Ui.Line, 90)))
                g.FillRectangle(b, r.X + Ui.S(14), r.Y + hdr, r.Width - Ui.S(28), 1);

            int maxRows = Math.Max(0, (r.Height - hdr - Ui.S(8)) / rowH);
            int y = r.Y + hdr + Ui.S(1);
            int drawn = 0;
            foreach (string[] kv in rows)
            {
                if (drawn >= maxRows) break;
                Ui.Text(g, kv[0], Ui.FSmall, new Rectangle(r.X + Ui.S(14), y, Ui.S(64), rowH), Ui.Sub,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
                Ui.Text(g, kv[1] ?? "", Ui.FHint,
                    new Rectangle(r.X + Ui.S(82), y, Math.Max(Ui.S(40), r.Width - Ui.S(82) - Ui.S(14)), rowH), Ui.Txt,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
                y += rowH;
                drawn++;
            }
            g.Restore(st);
            return r.Bottom;
        }

        private void DrawTag(Graphics g, ref int x, int y, string text, Color c)
        {
            int w = Ui.S(16) + TextCache.Measure(g, text, Ui.FSmall).Width;
            Rectangle r = new Rectangle(x, y, Math.Max(Ui.S(60), w), Ui.S(22));
            Ui.FillRound(g, r, Ui.S(6), Ui.A(c, 34));
            Ui.Text(g, text, Ui.FSmall, r, Ui.A(c, 235),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            x = r.Right + Ui.S(8);
        }

        private void DrawInfo(Graphics g, string label, string value, ref int y, int lineH, int maxY)
        {
            if (y + lineH > maxY) return;
            Ui.Text(g, label, Ui.FSmall, new Rectangle(Ui.S(30), y, Ui.S(60), lineH), Ui.Sub,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            Ui.Text(g, value ?? "", Ui.FHint, new Rectangle(Ui.S(84), y, Width - Ui.S(84) - Ui.S(28), lineH), Ui.Txt,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
            y += lineH;
        }

        private Rectangle PreviewBtnRect()
        {
            int w = (acc2Rect.Width - Ui.S(8)) * 58 / 100;
            return new Rectangle(acc2Rect.X, acc2Rect.Y, w, acc2Rect.Height);
        }

        private Rectangle InfoBtnRect()
        {
            Rectangle p = PreviewBtnRect();
            return new Rectangle(p.Right + Ui.S(8), acc2Rect.Y, acc2Rect.Width - p.Width - Ui.S(8), acc2Rect.Height);
        }

        // 卡片画的统一入口：圆角 10、1px 描边、悬停时描边染上强调色
        private void DrawCardFrame(Graphics g, Rectangle r, bool hover, bool open)
        {
            int rad = Ui.S(10);
            Ui.FillRound(g, r, rad, hover ? Ui.CardHi : Ui.Card);
            if (open || hover)
            {
                Region old = g.Clip;
                using (GraphicsPath p = Ui.Round(r, rad)) g.SetClip(p, CombineMode.Replace);
                using (SolidBrush b = new SolidBrush(Ui.A(Color.White, hover ? 13 : 7)))
                    g.FillRectangle(b, r.X, r.Y, r.Width, Math.Min(r.Height, Ui.S(40)));
                g.Clip = old;
            }
            Ui.StrokeRound(g, r, rad, hover ? Ui.A(Ui.Accent, 150) : Ui.Line, 1f);
        }

        private void DrawChevron(Graphics g, int cx, int cy, bool open, Color c)
        {
            using (Pen p = new Pen(c, Math.Max(1.3f, Ui.S(1.3f))))
            {
                p.StartCap = LineCap.Round; p.EndCap = LineCap.Round;
                float s = Ui.S(3.2f);
                if (open)
                {
                    g.DrawLine(p, cx - s, cy - s * 0.5f, cx, cy + s * 0.6f);
                    g.DrawLine(p, cx, cy + s * 0.6f, cx + s, cy - s * 0.5f);
                }
                else
                {
                    g.DrawLine(p, cx - s * 0.5f, cy - s, cx + s * 0.6f, cy);
                    g.DrawLine(p, cx + s * 0.6f, cy, cx - s * 0.5f, cy + s);
                }
            }
        }

        private void DrawChip(Graphics g, Rectangle r, string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            Size t = TextRenderer.MeasureText(g, text, Ui.FSmall);
            int w = t.Width + Ui.S(16);
            Rectangle chip = new Rectangle(r.Right - Ui.S(12) - w, r.Y + (r.Height - Ui.S(20)) / 2, w, Ui.S(20));
            Ui.FillRound(g, chip, Ui.S(10), Ui.A(Ui.Accent, 30));
            Ui.Text(g, text, Ui.FSmall, chip, Ui.A(Ui.Ramp7, 235),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        private void DrawPanelButton(Graphics g, Rectangle r, string title, string badge, bool hover)
        {
            DrawCardFrame(g, r, hover, false);
            Ui.Text(g, title, Ui.FHint, new Rectangle(r.X + Ui.S(14), r.Y, r.Width - Ui.S(120), r.Height),
                Ui.Txt, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
            DrawChip(g, r, badge);
        }

        private void DrawAccHeader(Graphics g, Rectangle r, string title, string badge, bool open, bool hover)
        {
            DrawCardFrame(g, r, hover, open);
            DrawChevron(g, r.X + Ui.S(18), r.Y + r.Height / 2, open, open ? Ui.Accent : Ui.Sub);
            Ui.Text(g, title, Ui.FHint, new Rectangle(r.X + Ui.S(30), r.Y, r.Width - Ui.S(120), r.Height),
                Ui.Txt, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
            DrawChip(g, r, badge);
        }

        private class SongRow
        {
            public SongInfo Song;
            public bool Header;
            public int Count;
        }

        private List<SongRow> BuildSongRows()
        {
            List<SongRow> rows = new List<SongRow>();
            if (mod == null) return rows;
            List<SongInfo> vanilla = new List<SongInfo>();
            foreach (SongInfo s in mod.Songs)
            {
                if (s.IsVanilla) { vanilla.Add(s); continue; }
                SongRow r = new SongRow(); r.Song = s; rows.Add(r);
            }
            if (vanilla.Count > 0)
            {
                if (FoldVanilla)
                {
                    SongRow h = new SongRow(); h.Header = true; h.Count = vanilla.Count; rows.Add(h);
                    if (mod.VanillaOpen)
                        foreach (SongInfo s in vanilla) { SongRow r = new SongRow(); r.Song = s; rows.Add(r); }
                }
                else
                {
                    foreach (SongInfo s in vanilla) { SongRow r = new SongRow(); r.Song = s; rows.Add(r); }
                }
            }
            return rows;
        }

        private void DrawSongs(Graphics g)
        {
            if (songsRect.Height <= 0) return;
            int rowH = Ui.S(30);
            List<SongRow> rows = BuildSongRows();
            int contentH = rows.Count * rowH + Ui.S(6);
            int maxScroll = Math.Max(0, contentH - songsRect.Height);
            if (songsScroll > maxScroll) songsScroll = maxScroll;

            Region old = g.Clip;
            g.SetClip(songsRect);
            for (int i = 0; i < rows.Count; i++)
            {
                int y = songsRect.Y + (int)Math.Round(i * rowH - songsScroll);
                if (y > songsRect.Bottom || y + rowH < songsRect.Y) continue;
                SongRow row = rows[i];

                if (row.Header)
                {
                    bool hovH = (songHover == i);
                    Ui.FillRound(g, new Rectangle(songsRect.X, y + 1, songsRect.Width, rowH - 2), Ui.S(6),
                        Ui.A(Color.White, hovH ? 12 : 6));
                    Ui.Text(g, (mod.VanillaOpen ? "▾  " : "▸  ") + "疑似原版曲目", Ui.FSmall,
                        new Rectangle(songsRect.X + Ui.S(10), y, songsRect.Width - Ui.S(90), rowH), Ui.Sub,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
                    Ui.Text(g, row.Count + " 首" + (mod.VanillaOpen ? "" : "（已折叠）"), Ui.FSmall,
                        new Rectangle(songsRect.Right - Ui.S(160), y, Ui.S(150), rowH), Ui.Sub,
                        TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
                    continue;
                }

                SongInfo s = row.Song;
                bool playing = player.IsPlaying && player.CurrentSong == s.Name;
                bool hov = (songHover == i);
                if (playing || hov)
                    Ui.FillRound(g, new Rectangle(songsRect.X, y + 1, songsRect.Width, rowH - 2), Ui.S(6),
                        playing ? Ui.A(Ui.Accent, 34) : Ui.A(Color.White, 9));

                Rectangle pr = new Rectangle(songsRect.X + Ui.S(4), y + (rowH - Ui.S(20)) / 2, Ui.S(20), Ui.S(20));
                if (playing)
                {
                    Ui.FillRound(g, pr, Ui.S(4), Ui.Accent);
                    Ui.StopGlyph(g, pr, Color.White);
                }
                else
                {
                    Ui.FillRound(g, pr, Ui.S(4), Ui.CardHi);
                    Ui.PlayGlyph(g, pr, hov ? Ui.Txt : Ui.Sub);
                }

                int nameX = songsRect.X + Ui.S(32);
                string tag = "";
                if (s.Bpm > 0) tag = "BPM " + s.Bpm;
                if (s.Speed > 0 && Math.Abs(s.Speed - 1) > 0.01) tag += (tag.Length > 0 ? " · " : "") + s.Speed.ToString("0.0") + "x";
                tag += (tag.Length > 0 ? " · " : "") + (s.HasVoices ? "人声+伴奏" : "合并音轨");
                int tagW = Ui.S(150);
                int lenW = Ui.S(58);
                Color nameColor = playing ? Ui.Accent : (s.IsVanilla ? Color.FromArgb(168, 178, 200) : Ui.Txt);

                Ui.Text(g, s.Name, Ui.FHint,
                    new Rectangle(nameX, y, songsRect.Width - Ui.S(32) - tagW - lenW - Ui.S(10), rowH),
                    nameColor, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);

                Ui.Text(g, tag, Ui.FSmall,
                    new Rectangle(songsRect.Right - tagW - lenW - Ui.S(6), y, tagW, rowH),
                    s.HasVoices ? Ui.Sub : Color.FromArgb(150, 160, 185),
                    TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);

                long len = playing ? player.LengthMs : s.LengthMs;
                string lenText = len > 0 ? FormatMs(len) : "";
                if (playing && len > 0) lenText = FormatMs(Math.Max(0, player.TimeMs)) + " / " + FormatMs(len);
                Ui.Text(g, lenText, Ui.FSmall, new Rectangle(songsRect.Right - lenW - Ui.S(2), y, lenW, rowH), Ui.Sub,
                    TextFormatFlags.Right | TextFormatFlags.VerticalCenter);

                if (playing && len > 0)
                {
                    float frac = Math.Max(0f, Math.Min(1f, (float)player.TimeMs / len));
                    Rectangle bar = new Rectangle(nameX, y + rowH - Ui.S(3), (int)((songsRect.Width - Ui.S(40)) * frac), Ui.S(2));
                    if (bar.Width > 0) Ui.FillRound(g, bar, 1, Ui.A(Ui.Accent, 160));
                }
            }
            g.Clip = old;

            if (contentH > songsRect.Height + 1)
            {
                int trackTop = songsRect.Y + Ui.S(4);
                int trackH = Math.Max(Ui.S(20), songsRect.Height - Ui.S(8));
                int thumbH = Math.Max(Ui.S(24), (int)((double)trackH * songsRect.Height / contentH));
                if (thumbH > trackH) thumbH = trackH;
                int travel = Math.Max(1, trackH - thumbH);
                double frac = maxScroll <= 0 ? 0 : songsScroll / (double)maxScroll;
                if (frac < 0) frac = 0;
                if (frac > 1) frac = 1;
                int thumbTop = trackTop + (int)(travel * frac);
                Ui.FillRound(g, new Rectangle(songsRect.Right - Ui.S(4), trackTop, Ui.S(3), trackH), 2, Ui.A(Color.White, 22));
                Ui.FillRound(g, new Rectangle(songsRect.Right - Ui.S(4), thumbTop, Ui.S(3), thumbH), 2, Ui.A(Color.White, 90));
            }
        }
        private void DrawPreviews(Graphics g)
        {
            List<PreviewImage> imgs = mod.Previews;
            if (imgs == null || imgs.Count == 0)
            {
                Ui.Text(g, "没找到可用的预览图（menubackgrounds / 截图）", Ui.FSmall,
                    new Rectangle(previewRect.X, previewRect.Y, previewRect.Width, previewRect.Height), Ui.Sub,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
                return;
            }

            int gap = Ui.S(8);
            int count = Math.Max(1, Math.Min(imgs.Count, 4));
            int tile = Math.Min(Ui.S(96), (previewRect.Width - gap * (count - 1)) / count);
            tile = Math.Min(tile, Math.Max(0, previewRect.Height - Ui.S(8)));
            if (tile < Ui.S(28)) return;
            for (int i = 0; i < count; i++)
            {
                Rectangle r = new Rectangle(previewRect.X + i * (tile + gap), previewRect.Y + (previewRect.Height - tile) / 2, tile, tile);
                bool hov = (previewHover == i);
                Ui.FillRound(g, r, Ui.S(6), hov ? Ui.CardHi : Ui.Card);
                Image th = imgs[i].Thumb(tile);
                if (th != null) g.DrawImage(th, new Rectangle(r.X, r.Y, r.Width, r.Height));
                if (hov) Ui.StrokeRound(g, r, Ui.S(6), Ui.Accent, 2f);
            }
            if (imgs.Count > count)
                Ui.Text(g, "+" + (imgs.Count - count), Ui.FSmall,
                    new Rectangle(previewRect.Right - Ui.S(46), previewRect.Y, Ui.S(44), previewRect.Height),
                    Ui.Accent, TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
        }

        private void DrawButton(Graphics g, Rectangle r, string text, Font f, bool filled, bool hover, bool press)
        {
            if (r.Width <= 0 || r.Height <= 0) return;
            int rad = Ui.S(10);
            GraphicsState st0 = g.Save();
            if (press)
            {
                // 按住时整体缩一点，和顶栏按钮的手感保持一致
                float cx = r.X + r.Width * 0.5f, cy = r.Y + r.Height * 0.5f;
                g.TranslateTransform(cx, cy);
                g.ScaleTransform(0.972f, 0.972f);
                g.TranslateTransform(-cx, -cy);
            }
            Rectangle rr = press ? new Rectangle(r.X + Ui.S(1), r.Y + Ui.S(2), r.Width - Ui.S(2), r.Height - Ui.S(2)) : r;
            if (filled)
            {
                Color c = (hover || press) ? Ui.AccentDark : Ui.Accent;
                Ui.FillRound(g, rr, rad, c);
                if (hover && !press)
                {
                    using (GraphicsPath path = Ui.Round(rr, rad))
                    using (Pen pen = new Pen(Ui.A(Color.White, 70), 1f)) { g.DrawPath(pen, path); }
                }
                Ui.Text(g, text, f, rr, Color.White,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
            }
            else
            {
                Ui.FillRound(g, rr, rad, hover ? Ui.CardHi : Ui.Card);
                Ui.StrokeRound(g, rr, rad, hover ? Ui.A(Ui.Accent, 200) : Ui.Line, 1f);
                Ui.Text(g, text, f, rr, hover ? Ui.Txt : Ui.Sub,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
            }
            g.Restore(st0);
        }

        private static string FormatMs(long ms)
        {
            int sec = (int)(ms / 1000);
            return (sec / 60) + ":" + (sec % 60).ToString("00");
        }

        private static string FormatLong(long ms)
        {
            if (ms <= 0) return "0 分钟";
            long min = ms / 60000;
            if (min < 60) return min + " 分钟";
            return (min / 60) + " 小时 " + (min % 60) + " 分";
        }

        private static string FormatSize(long bytes)
        {
            if (bytes < 1024L * 1024L) return Math.Max(1, bytes / 1024) + " KB";
            if (bytes < 1024L * 1024L * 1024L) return (bytes / (1024.0 * 1024.0)).ToString("0.#") + " MB";
            return (bytes / (1024.0 * 1024.0 * 1024.0)).ToString("0.##") + " GB";
        }

        private static string FormatRecent(double ticks)
        {
            if (ticks <= 0) return "还没运行过";
            try
            {
                DateTime t = new DateTime((long)ticks);
                TimeSpan d = DateTime.Now - t;
                if (d.TotalMinutes < 1) return "刚刚";
                if (d.TotalHours < 1) return (int)d.TotalMinutes + " 分钟前";
                if (d.TotalDays < 1) return (int)d.TotalHours + " 小时前";
                if (d.TotalDays < 30) return (int)d.TotalDays + " 天前";
                return t.ToString("yyyy-MM-dd");
            }
            catch { return "还没运行过"; }
        }

        // ---------------------------------------------------------------- 交互
        private int HitSong(Point p)
        {
            if (mod == null || !mod.SongsOpen) return -1;
            if (!songsRect.Contains(p)) return -1;
            List<SongRow> rows = BuildSongRows();
            int i = (int)Math.Floor((p.Y - songsRect.Y + songsScroll) / (double)Ui.S(30));
            if (i < 0 || i >= rows.Count) return -1;
            return i;
        }
        private int HitPreview(Point p)
        {
            if (mod == null || !mod.PreviewOpen || mod.Previews == null || mod.Previews.Count == 0) return -1;
            if (!previewRect.Contains(p)) return -1;
            int gap = Ui.S(8);
            int count = Math.Max(1, Math.Min(mod.Previews.Count, 4));
            int tile = Math.Min(Ui.S(96), (previewRect.Width - gap * (count - 1)) / count);
            for (int i = 0; i < count; i++)
            {
                Rectangle r = new Rectangle(previewRect.X + i * (tile + gap), previewRect.Y + (previewRect.Height - tile) / 2, tile, tile);
                if (r.Contains(p)) return i;
            }
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            layoutForHit = true; LayoutAll(); layoutForHit = false;
            bool infoChipHover0 = infoChipHover;
            bool hl = launchRect.Contains(e.Location);
            bool ho2 = openRect.Contains(e.Location);
            bool a1 = acc1Rect.Contains(e.Location);
            bool a2 = acc2Rect.Contains(e.Location);
            bool pv = PreviewBtnRect().Contains(e.Location);
            bool iv = InfoBtnRect().Contains(e.Location);
            infoChipHover = infoChipRect.Width > 0 && infoChipRect.Contains(e.Location);
            int sh = HitSong(e.Location);
            int ph = HitPreview(e.Location);

            if (hl != hoverLaunch || ho2 != hoverOpen || a1 != acc1Hover || a2 != acc2Hover ||
                sh != songHover || ph != previewHover || pv != prevBtnHover || iv != infoBtnHover || infoChipHover != infoChipHover0)
            {
                hoverLaunch = hl; hoverOpen = ho2; acc1Hover = a1; acc2Hover = a2;
                songHover = sh; previewHover = ph; prevBtnHover = pv; infoBtnHover = iv;
                Cursor = (hl || ho2 || a1 || a2 || pv || iv || sh >= 0 || ph >= 0) ? Cursors.Hand : Cursors.Default;
                Invalidate();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            hoverLaunch = hoverOpen = acc1Hover = acc2Hover = prevBtnHover = infoBtnHover = false;
            songHover = previewHover = -1;
            Cursor = Cursors.Default;
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            layoutForHit = true; LayoutAll(); layoutForHit = false;
            pressLaunch = launchRect.Contains(e.Location);
            pressOpen = openRect.Contains(e.Location);
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            layoutForHit = true; LayoutAll(); layoutForHit = false;
            bool wasLaunch = pressLaunch && launchRect.Contains(e.Location);
            bool wasOpen = pressOpen && openRect.Contains(e.Location);
            pressLaunch = pressOpen = false;

            // 紧凑模式标题旁的「查看模组信息」（窗口形式弹出）
            if (infoChipRect.Width > 0 && infoChipRect.Contains(e.Location))
            {
                if (mod != null && InfoWindowRequested != null) InfoWindowRequested(this, EventArgs.Empty);
                return;
            }

            if (wasLaunch)
            {
                if (mod != null && LaunchRequested != null) LaunchRequested(mod);
                return;
            }
            if (wasOpen)
            {
                if (mod != null && OpenFolderRequested != null) OpenFolderRequested(mod);
                return;
            }
            if (acc1Rect.Contains(e.Location))
            {
                if (mod != null)
                {
                    if (UseWindow)
                    {
                        if (SongsWindowRequested != null) SongsWindowRequested(this, EventArgs.Empty);
                        return;
                    }
                    if (mod.SongsOpen) mod.SongsOpen = false;
                    else { mod.SongsOpen = true; mod.PreviewOpen = false; }
                    Invalidate();
                    if (SongsToggled != null) SongsToggled(this, EventArgs.Empty);
                }
                return;
            }
            if (acc2Rect.Contains(e.Location))
            {
                if (mod != null)
                {
                    if (UseWindow)
                    {
                        if (InfoBtnRect().Contains(e.Location))
                        {
                            if (InfoWindowRequested != null) InfoWindowRequested(this, EventArgs.Empty);
                        }
                        else if (PreviewWindowRequested != null) PreviewWindowRequested(this, EventArgs.Empty);
                        return;
                    }
                    if (InfoBtnRect().Contains(e.Location))
                    {
                        if (InfoWindowRequested != null) InfoWindowRequested(this, EventArgs.Empty);
                        return;
                    }
                    if (mod.PreviewOpen) mod.PreviewOpen = false;
                    else { mod.PreviewOpen = true; mod.SongsOpen = false; }
                    Invalidate();
                    if (PreviewToggled != null) PreviewToggled(this, EventArgs.Empty);
                }
                return;
            }

            int si = HitSong(e.Location);
            if (si >= 0)
            {
                List<SongRow> rows = BuildSongRows();
                if (si < rows.Count)
                {
                    if (rows[si].Header) { mod.VanillaOpen = !mod.VanillaOpen; Invalidate(); return; }
                    ShowSongMenu(rows[si].Song);
                }
                return;
            }

            int pi = HitPreview(e.Location);
            if (pi >= 0)
            {
                PreviewOpenIndex = pi;
                if (PreviewOpenRequested != null) PreviewOpenRequested(mod);
            }
        }

        // 点曲目：选 合听 / 伴奏 / 人声，统一交给系统播放器（合听会先预混并缓存）
        private void ShowSongMenu(SongInfo s)
        {
            if (s == null) return;
            ContextMenuStrip cm = new ContextMenuStrip();
            cm.BackColor = Ui.Card;
            cm.ForeColor = Ui.Txt;
            cm.Font = Ui.FHint;
            cm.Renderer = new DarkMenuRenderer();
            cm.ShowImageMargin = false;

            cm.Items.Add("▶  合听（伴奏+人声）", null, delegate { OpenSongTrack(s, "both"); });
            cm.Items.Add("伴奏", null, delegate { OpenSongTrack(s, "inst"); });
            ToolStripMenuItem v = new ToolStripMenuItem("人声");
            v.Enabled = s.HasVoices;
            v.ForeColor = s.HasVoices ? Ui.Txt : Ui.Sub;
            v.Click += delegate { OpenSongTrack(s, "voices"); };
            cm.Items.Add(v);
            Lang.SweepMenu(cm.Items, 0);
            cm.Show(Cursor.Position);
        }

        private void OpenSongTrack(SongInfo s, string mode)
        {
            string path;
            if (mode == "inst")
            {
                path = s.Inst;
            }
            else if (mode == "voices")
            {
                if (!s.HasVoices) return;
                path = s.Voices;
            }
            else
            {
                if (StatusText != null) StatusText("正在创建音乐缓存…（第一次会慢几秒）");
                Cursor = Cursors.WaitCursor;
                try { path = Mixer.MixBoth(s, Path.GetDirectoryName(mod.Exe)); }
                finally { Cursor = Cursors.Default; }
                if (path == null)
                {
                    MessageBox.Show("混音失败：" + Mixer.LastError + "\r\n\r\n可以先试「伴奏」。", "试听",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
            }
            Mixer.OpenWithSystem(path);
            if (StatusText != null) StatusText("已用系统播放器打开");
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            LastWheelAt = Ui.Now;
            layoutForHit = true; LayoutAll(); layoutForHit = false;
            if (mod != null && mod.SongsOpen && mod.Songs.Count > SongsVisibleRows && songsRect.Contains(e.Location))
            {
                songsScrollTarget -= e.Delta / 120f * Ui.S(88);
                float max = Math.Max(0, SongsContentH - songsRect.Height);
                if (songsScrollTarget < 0) songsScrollTarget = 0;
                if (songsScrollTarget > max) songsScrollTarget = max;
                Invalidate();
                if (AnimRequest != null) AnimRequest();
                return;
            }
            if (maxScroll > 0)
            {
                scroll -= (int)(e.Delta / 120f * Ui.S(96));
                if (scroll < 0) scroll = 0;
                if (scroll > maxScroll) scroll = maxScroll;
                Invalidate();
            }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            layoutForHit = true; LayoutAll(); layoutForHit = false;
            Invalidate();
        }
    }

    // ===================================================================== 提示条
    
    internal class Toast : Control, IAnimatable
    {
        private double showAt = -10000;
        private string text = "";
        public Action AnimRequest;

        public Toast()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Ui.Bg;
            Visible = false;
        }

        public void Show(string msg)
        {
            text = msg;
            showAt = Ui.Now;
            Visible = true;
            Invalidate();
            if (AnimRequest != null) AnimRequest();
        }

        public bool Animate()
        {
            double age = Ui.Now - showAt;
            if (age > 2600) { Visible = false; return false; }
            Invalidate();
            return true;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BackColor);

            double age = Ui.Now - showAt;
            float inP = Ui.EaseOut(Ui.Progress(showAt, 200f));
            float outP = Ui.EaseOut(Ui.Progress(showAt + 2200, 300f));
            int alpha = (int)(255 * inP * (1f - outP));
            if (alpha <= 4) return;

            Size sz = TextRenderer.MeasureText(g, text, Ui.FBig);
            int w = sz.Width + Ui.S(40);
            int h = sz.Height + Ui.S(20);
            int x = (Width - w) / 2;
            int y = Height - h - (int)((1f - inP) * Ui.S(14)) + (int)(outP * Ui.S(10));

            Ui.FillRound(g, new Rectangle(x, y, w, h), Ui.S(10), Ui.A(Color.FromArgb(24, 26, 36), alpha));
            Ui.StrokeRound(g, new Rectangle(x, y, w, h), Ui.S(10), Ui.A(Ui.Accent, alpha / 2), 1f);
            Ui.Text(g, text, Ui.FBig, new Rectangle(x, y, w, h), Ui.A(Color.White, alpha),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        }
    }

    // ===================================================================== 圆角按钮
    
    internal class RoundButton : Control, IAnimatable
    {
        public bool Filled = true;
        public bool Glass;      // 玻璃模式：背景由父控件（标题栏渐变）来画，避免出现深色方角
        public Color FillColor = Ui.Accent;
        public Action AnimRequest;
        private bool hover, down;
        private double hoverAt, pressAt, releaseAt;
        private bool clickRaised;

        public RoundButton(string text)
        {
            Text = text;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Font = Ui.FBtn;
            BackColor = Ui.Bar;
            ForeColor = Ui.Txt;
            Cursor = Cursors.Hand;
            Size = new Size(Ui.S(64), Ui.S(30));
        }

        public bool Animate()
        {
            bool busy = false;
            if (Ui.Now - hoverAt < 160) { Invalidate(); busy = true; }
            if (Ui.Now - releaseAt < 380) { Invalidate(); busy = true; }
            return busy;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (Glass && Parent is TitleBar)
            { GraphicsState gst = g.Save(); g.SetClip(ClientRectangle); g.TranslateTransform(-Left, -Top); ((TitleBar)Parent).PaintBackdrop(g); g.Restore(gst); }

            // 按压回弹：整块内容按比例缩放
            float ps = Ui.PressScale(down, pressAt, releaseAt);
            if (Math.Abs(ps - 1f) > 0.0008f)
            {
                g.TranslateTransform(Width * 0.5f, Height * 0.5f);
                g.ScaleTransform(ps, ps);
                g.TranslateTransform(-Width * 0.5f, -Height * 0.5f);
            }

            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            int rad = Ui.S(8);
            float ha = Ui.EaseOut(Ui.Progress(hoverAt, 140f));

            if (Filled)
            {
                Ui.FillRound(g, r, rad, down ? Ui.AccentDark : Ui.Accent);
                if (hover)
                {
                    using (GraphicsPath p = Ui.Round(r, rad))
                    using (Pen pen = new Pen(Ui.A(Color.White, 60), 1f)) { g.DrawPath(pen, p); }
                }
                Ui.Text(g, Text, Font, r, Enabled ? Color.White : Ui.Sub,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
            }
            else
            {
                Color bg = Blend(Ui.Card, Ui.CardHi, ha);
                Ui.FillRound(g, r, rad, bg);
                Ui.StrokeRound(g, r, rad, hover ? Ui.A(Ui.Accent, (int)(120 + 120 * ha)) : Ui.Line, 1f);
                Ui.Text(g, Text, Font, r, hover ? Ui.Txt : Ui.Sub,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
            }

            g.ResetTransform();
        }

        private static Color Blend(Color a, Color b, float t)
        {
            return Color.FromArgb(
                (int)(a.R + (b.R - a.R) * t),
                (int)(a.G + (b.G - a.G) * t),
                (int)(a.B + (b.B - a.B) * t));
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            hover = true; hoverAt = Ui.Now; Invalidate();
            if (AnimRequest != null) AnimRequest();
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            hover = false; down = false; hoverAt = Ui.Now - 140; releaseAt = Ui.Now; Invalidate();
            if (AnimRequest != null) AnimRequest();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            down = true; pressAt = Ui.Now; releaseAt = 0;
            Invalidate();
            if (AnimRequest != null) AnimRequest();
            base.OnMouseDown(e);
        }

        protected override void OnClick(EventArgs e)
        {
            clickRaised = true;
            base.OnClick(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            bool fire = down && e.Button == MouseButtons.Left && ClientRectangle.Contains(e.Location);
            down = false; releaseAt = Ui.Now;
            clickRaised = false;
            base.OnMouseUp(e);
            if (fire && !clickRaised) OnClick(EventArgs.Empty);
            Invalidate();
            if (AnimRequest != null) AnimRequest();
        }

        protected override void OnEnabledChanged(EventArgs e)
        {
            Invalidate();
            base.OnEnabledChanged(e);
        }
    }

    // ===================================================================== 启动中
    // 点启动后盖住内容区的一层"正在启动"页：图标 + 进度条 + 当前步骤 + 随机小提示。
    // 实际进程在点击瞬间就已经拉起了，这一页只是把"等待"这段空窗变得有信息量。
    internal class LaunchOverlay : Control, IAnimatable
    {
        private ModEntry mod;
        private double startAt;
        private Action completed;
        private bool playing;
        private string hint = "";

        private static readonly string[] Hints = {
            "Psych Engine 的 mods 文件夹里，一个子文件夹就是一首曲目；改名会连曲目一起改。",
            "路径里带中文时个别引擎读不到资源，能放英文目录就放英文目录。",
            "启动器每次启动前会自动备份存档，出问题可以去 launcher\\backups 里翻。",
            "曲目窗口里可以直接试听，不用先进游戏。",
            "按 F5 重新扫描模组，把文件夹拖进窗口也能添加。",
            "详情页的「资料」里能看到故事、成就和用到的资源。",
            "Ctrl + 数字 可以快速启动列表里的第 N 个模组。"
        };

        private static readonly string[] StepNames = { "准备启动参数", "检查游戏文件", "组装启动命令", "正在拉起进程" };

        private const float ShowMs = 1550f, FadeInMs = 170f, FadeOutMs = 280f;

        public Action AnimRequest;

        public LaunchOverlay()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Ui.Bg;
            Visible = false;
        }

        public void Play(ModEntry m, Action done)
        {
            mod = m;
            completed = done;
            hint = Hints[(int)((Ui.Now / 7919.0) % Hints.Length)];
            startAt = Ui.Now;
            playing = true;
            Visible = true;
            BringToFront();
            Invalidate();
            if (AnimRequest != null) AnimRequest();
        }

        public bool Animate()
        {
            if (!playing) return false;
            double el = Ui.Now - startAt;
            Invalidate();
            if (el > ShowMs)
            {
                playing = false;
                Visible = false;
                Action cb = completed;
                completed = null;
                if (cb != null) cb();
                return false;
            }
            return true;
        }

        private int FadeAlpha(float el)
        {
            float a = 1f;
            if (el < FadeInMs) a = el / FadeInMs;
            else if (el > ShowMs - FadeOutMs) a = (ShowMs - el) / FadeOutMs;
            a = Ui.Clamp01(a);
            return (int)(255 * a);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;

            float el = (float)(Ui.Now - startAt);
            int alpha = FadeAlpha(el);
            if (alpha <= 0) { g.Clear(BackColor); return; }

            using (SolidBrush b = new SolidBrush(Ui.A(Ui.Bg, alpha))) g.FillRectangle(b, ClientRectangle);

            // 由整页的淡入再叠一点上浮，避免"硬切"
            float rise = (1f - Ui.Clamp01(el / 260f)) * Ui.S(12);
            int cy = Height / 2 + (int)rise;
            int cx = Width / 2;

            if (mod != null)
            {
                Image ic = null;
                try { ic = mod.GetBig(Ui.S(96)); } catch { }
                if (ic != null)
                {
                    int s = Ui.S(96);
                    g.DrawImage(ic, new Rectangle(cx - s / 2, cy - Ui.S(196), s, s));
                }
            }

            Ui.Text(g, "正在启动", Ui.FTitle,
                new Rectangle(0, cy - Ui.S(84), Width, Ui.S(30)), Ui.A(Ui.Accent, alpha),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

            string nm = mod != null ? mod.Title : "";
            Ui.Text(g, nm, Ui.FBig, new Rectangle(0, cy - Ui.S(52), Width, Ui.S(24)),
                Ui.A(Ui.Txt, alpha), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

            // 进度：0~1.25 秒走满，最后停一下再淡出
            float p = Ui.Clamp01(el / 1250f);
            p = Ui.EaseOut(p);
            int bw = Math.Min(Ui.S(360), Math.Max(Ui.S(160), Width - Ui.S(120)));
            Rectangle back = new Rectangle(cx - bw / 2, cy + Ui.S(6), bw, Ui.S(4));
            Ui.FillRound(g, back, Ui.S(2), Ui.A(Ui.Line, alpha));
            int fw = (int)(bw * p);
            if (fw > 1)
            {
                Rectangle fore = new Rectangle(back.X, back.Y, fw, back.Height);
                Ui.FillRound(g, fore, Ui.S(2), Color.FromArgb(alpha, Ui.Accent));
                using (GraphicsPath pth = Ui.Round(fore, Ui.S(2)))
                {
                    GraphicsState st = g.Save();
                    g.SetClip(pth, CombineMode.Replace);
                    using (LinearGradientBrush shine = new LinearGradientBrush(
                        new Rectangle(fore.X, fore.Y, Math.Max(2, fw), Math.Max(2, fore.Height)),
                        Color.FromArgb(0, Color.White), Color.FromArgb((int)(70 * alpha / 255f), Color.White),
                        LinearGradientMode.Horizontal))
                        g.FillRectangle(shine, fore);
                    g.Restore(st);
                }
            }

            int stepIdx = (int)Math.Min(StepNames.Length - 1, p * StepNames.Length);
            string step = StepNames[stepIdx] + "  ·  " + ((int)(p * 100)) + "%";
            Ui.Text(g, step, Ui.FSmall, new Rectangle(0, cy + Ui.S(20), Width, Ui.S(20)),
                Ui.A(Ui.Sub, alpha), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

            // 小提示
            int hw = Math.Min(Ui.S(460), Math.Max(Ui.S(200), Width - Ui.S(160)));
            Rectangle hr = new Rectangle(cx - hw / 2, cy + Ui.S(64), hw, Ui.S(44));
            Ui.Text(g, "你知道吗：", Ui.FSmall, new Rectangle(hr.X, hr.Y, hr.Width, Ui.S(18)),
                Ui.A(Ui.Ramp6, alpha), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            Ui.Text(g, hint, Ui.FSmall, new Rectangle(hr.X, hr.Y + Ui.S(18), hr.Width, hr.Height - Ui.S(18)),
                Ui.A(Ui.Sub, alpha), TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.WordBreak);
        }
    }

    // ===================================================================== 自绘搜索框
    // 原生 TextBox 画不出圆角和描边，所以用一个容器控件画底、里面嵌一个无边框 TextBox。
    internal class SearchBox : Control
    {
        public readonly TextBox Box;
        public event EventHandler TextChanged;
        private bool focus;

        public SearchBox()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Ui.Bar;
            Box = new TextBox();
            Box.BorderStyle = BorderStyle.None;
            Box.BackColor = Ui.Card;
            Box.ForeColor = Ui.Txt;
            Box.Font = Ui.FHint;
            Box.TextChanged += delegate { Invalidate(); if (TextChanged != null) TextChanged(this, EventArgs.Empty); };
            Box.GotFocus += delegate { focus = true; Invalidate(); };
            Box.LostFocus += delegate { focus = false; Invalidate(); };
            Box.HandleCreated += delegate { ApplyCue(); };
            Controls.Add(Box);
            Height = Ui.S(28);
            Cursor = Cursors.IBeam;
            Reflow();
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, string l);

        private const int EM_SETCUEBANNER = 0x1501;

        private void ApplyCue()
        {
            try { SendMessage(Box.Handle, EM_SETCUEBANNER, (IntPtr)1, CueText ?? ""); }
            catch { }
        }

        public string CueText
        {
            get { return cue; }
            set { cue = value; ApplyCue(); Invalidate(); }
        }

        private string cue;

        public override string Text { get { return Box.Text; } set { Box.Text = value; } }

        public void RefreshTheme()
        {
            Box.BackColor = Ui.Card;
            Box.ForeColor = Ui.Txt;
            BackColor = Ui.Bar;
            Invalidate();
        }

        private void Reflow()
        {
            int padL = Ui.S(28), padR = Ui.S(10);
            if (Box == null) return;
            Box.SetBounds(padL, (Height - Box.PreferredHeight) / 2 + 1,
                Math.Max(10, Width - padL - padR), Box.PreferredHeight);
        }

        protected override void OnResize(EventArgs e) { base.OnResize(e); Reflow(); }
        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); Box.Focus(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BackColor);
            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            int rad = Ui.S(8);
            Ui.FillRound(g, r, rad, Ui.Card);
            Ui.StrokeRound(g, r, rad, focus ? Ui.A(Ui.Accent, 200) : Ui.Line, 1f);

            int cx = Ui.S(14), cy = Height / 2;
            using (Pen p = new Pen(focus ? Ui.Accent : Ui.Sub, Math.Max(1.2f, Ui.S(1.2f))))
            {
                p.StartCap = LineCap.Round; p.EndCap = LineCap.Round;
                g.DrawEllipse(p, cx - Ui.S(4), cy - Ui.S(4), Ui.S(7), Ui.S(7));
                g.DrawLine(p, cx + Ui.S(1), cy + Ui.S(2), cx + Ui.S(5), cy + Ui.S(6));
            }
        }
    }

    // ===================================================================== 自绘标题栏
    // 去掉系统边框后，窗口的"脸"由这条 44px 的渐变横条负责：
    // 左边字标 + 版本徽章，右边三个自绘按钮，中间整段可拖动、双击最大化。
    internal class TitleBar : Control, IAnimatable
    {
        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();
        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);

        public Action AnimRequest;
        public event EventHandler AboutRequested;

        private Rectangle closeRect, maxRect, minRect, verRect;
        private int hoverBtn = -1, downBtn = -1;
        private double hoverAt;
        private static Image appIcon;
        private bool dragging;
        private Point dragOrigin;

        public TitleBar()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                     ControlStyles.StandardDoubleClick, true);   // 不开这个 OnDoubleClick 不触发
            BackColor = Ui.Bar;
            Height = Ui.S(44);
            Dock = DockStyle.Top;
            try { appIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath).ToBitmap(); }
            catch { appIcon = null; }
        }

        public bool Animate()
        {
            if (Ui.Now - hoverAt < 160) { Invalidate(); return true; }
            return false;
        }

        private static ColorBlend MakeBlend(Color[] cs, float[] pos)
        {
            ColorBlend cb = new ColorBlend();
            cb.Colors = cs;
            cb.Positions = pos;
            return cb;
        }

        private void Measure()
        {
            int h = Height, bw = Ui.S(46);
            closeRect = new Rectangle(Width - bw, 0, bw, h);
            maxRect = new Rectangle(Width - bw * 2, 0, bw, h);
            minRect = new Rectangle(Width - bw * 3, 0, bw, h);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Measure();
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            Ui.PaintWithRoundedCorners(g, this, PaintBackdrop);
            PaintContent(g);
        }

        // 标题栏底：三段渐变 + 一点高光 + 底部强调线。按钮要"玻璃"效果时就调它重画自己那一块。
        public void PaintBackdrop(Graphics g)
        {
            using (LinearGradientBrush lb = new LinearGradientBrush(
                new Rectangle(0, 0, Math.Max(1, Width), Math.Max(1, Height)),
                Ui.Hsl(Ui.Hue - 14f, Ui.Sat * 0.50f, 0.295f),
                Ui.Hsl(Ui.Hue + 14f, Ui.Sat * 0.50f, 0.295f), LinearGradientMode.Horizontal))
            {
                lb.InterpolationColors = MakeBlend(
                    new Color[] {
                        Ui.Hsl(Ui.Hue - 14f, Ui.Sat * 0.50f, 0.295f),
                        Ui.Hsl(Ui.Hue,        Ui.Sat * 0.62f, 0.400f),
                        Ui.Hsl(Ui.Hue + 14f, Ui.Sat * 0.50f, 0.295f) },
                    new float[] { 0f, 0.5f, 1f });
                g.FillRectangle(lb, 0, 0, Width, Height);
            }
            using (LinearGradientBrush lb = new LinearGradientBrush(
                new Rectangle(0, 0, Math.Max(1, Width), Math.Max(1, Height)),
                Color.FromArgb(26, Color.White), Color.FromArgb(0, Color.White), LinearGradientMode.Vertical))
                g.FillRectangle(lb, 0, 0, Width, Height);
            using (SolidBrush b = new SolidBrush(Ui.A(Ui.Accent, 90)))
                g.FillRectangle(b, 0, Height - 1, Width, 1);

        }

        private void PaintContent(Graphics g)
        {
            // 应用图标
            int ix = Ui.S(15);
            int isz = Ui.S(18);
            if (appIcon != null) g.DrawImage(appIcon, new Rectangle(ix, (Height - isz) / 2, isz, isz));

            // 字标：FNF 用强调色，后面 模组启动器 用白色
            int tx = ix + isz + Ui.S(9);
            float fs = 13f * Ui.Zoom;
            using (Font fb = new Font("Microsoft YaHei UI", fs, FontStyle.Bold))
            {
                Size wm = TextRenderer.MeasureText(g, "FNF", fb);
                Ui.Text(g, "FNF", fb, new Rectangle(tx, 0, wm.Width + Ui.S(4), Height),
                    Color.FromArgb(255, Math.Min(255, Ui.Accent.R + 60), Math.Min(255, Ui.Accent.G + 90), Math.Min(255, Ui.Accent.B + 70)),
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
                int x2 = tx + wm.Width + Ui.S(2);
                Ui.Text(g, Lang.T("模组启动器"), fb, new Rectangle(x2, 0, Ui.S(200), Height),
                    Color.FromArgb(238, 244, 252), TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
                int x3 = x2 + (int)TextRenderer.MeasureText(g, Lang.T("模组启动器"), fb).Width + Ui.S(8);

                verRect = new Rectangle(x3, (Height - Ui.S(20)) / 2, Ui.S(64), Ui.S(20));
                bool vh = verRect.Contains(PointToClient(Cursor.Position));
                Ui.FillRound(g, verRect, Ui.S(6), vh ? Ui.A(Color.White, 46) : Ui.A(Color.White, 24));
                Ui.Text(g, "v" + Version4.AppVersion, Ui.FSmall, verRect, Color.FromArgb(235, Color.White),
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }

            // 窗口按钮
            DrawWinButton(g, minRect, 0, hoverBtn == 0);
            DrawWinButton(g, maxRect, 1, hoverBtn == 1);
            DrawWinButton(g, closeRect, 2, hoverBtn == 2);
        }

        private void DrawWinButton(Graphics g, Rectangle r, int kind, bool hover)
        {
            if (r.Width <= 0) return;
            bool isClose = kind == 2;
            if (hover)
                Ui.FillRound(g, Rectangle.Inflate(r, -Ui.S(3), -Ui.S(3)), Ui.S(4),
                    isClose ? Color.FromArgb(220, 200, 30, 45) : Ui.A(Color.White, 34));

            Color c = Color.FromArgb(hover && isClose ? 255 : 226, Color.White);
            float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f;
            float s = Ui.S(5);
            using (Pen p = new Pen(c, Math.Max(1.2f, Ui.S(1.2f))))
            {
                if (kind == 0) g.DrawLine(p, cx - s, cy + 0.5f, cx + s, cy + 0.5f);
                else if (kind == 1)
                {
                    Form f = FindForm();
                    bool maxed = f != null && f.WindowState == FormWindowState.Maximized;
                    if (maxed)
                    {
                        Rectangle a = new Rectangle((int)(cx - s + 2), (int)(cy - s - 1), (int)(s * 2 - 2), (int)(s * 2 - 2));
                        Rectangle b = new Rectangle((int)(cx - s - 1), (int)(cy - s + 2), (int)(s * 2 - 2), (int)(s * 2 - 2));
                        g.DrawRectangle(p, a); g.DrawRectangle(p, b);
                    }
                    else g.DrawRectangle(p, (int)(cx - s), (int)(cy - s), (int)(s * 2), (int)(s * 2));
                }
                else
                {
                    g.DrawLine(p, cx - s, cy - s, cx + s, cy + s);
                    g.DrawLine(p, cx - s, cy + s, cx + s, cy - s);
                }
            }
        }

        protected override void OnDoubleClick(EventArgs e)
        {
            base.OnDoubleClick(e);
            Point p = PointToClient(Cursor.Position);
            if (Hit(p) >= 0 || verRect.Contains(p)) return;
            Form f = FindForm();
            if (f == null) return;
            f.WindowState = f.WindowState == FormWindowState.Maximized
                ? FormWindowState.Normal : FormWindowState.Maximized;
        }

        private int Hit(Point p)
        {
            if (closeRect.Contains(p)) return 2;
            if (maxRect.Contains(p)) return 1;
            if (minRect.Contains(p)) return 0;
            return -1;
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            if (hoverBtn != -1) { hoverBtn = -1; hoverAt = Ui.Now; Invalidate(); if (AnimRequest != null) AnimRequest(); }
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) { base.OnMouseDown(e); return; }
            downBtn = Hit(e.Location);
            if (downBtn >= 0) { Invalidate(); base.OnMouseDown(e); return; }
            if (verRect.Contains(e.Location))
            {
                if (AboutRequested != null) AboutRequested(this, EventArgs.Empty);
                base.OnMouseDown(e);
                return;
            }
            // 先记下按下位置：拖出去 >3px 才真正开始拖窗口，
            // 这样单击/双击还能正常落到控件上（双击=最大化）。
            dragging = true;
            dragOrigin = Cursor.Position;
            Capture = true;
            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int b = Hit(e.Location);
            if (b != hoverBtn) { hoverBtn = b; hoverAt = Ui.Now; Invalidate(); if (AnimRequest != null) AnimRequest(); }
            Cursor = (b >= 0 || verRect.Contains(e.Location)) ? Cursors.Hand : Cursors.Default;

            if (dragging && (Math.Abs(Cursor.Position.X - dragOrigin.X) > 3 || Math.Abs(Cursor.Position.Y - dragOrigin.Y) > 3))
            {
                dragging = false;
                Capture = false;
                Form f = FindForm();
                if (f != null)
                {
                    ReleaseCapture();
                    SendMessage(f.Handle, 0x00A1 /*WM_NCLBUTTONDOWN*/, (IntPtr)2 /*HTCAPTION*/, IntPtr.Zero);
                }
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            dragging = false;
            Capture = false;
            int b = Hit(e.Location);
            if (downBtn >= 0 && b == downBtn)
            {
                Form f = FindForm();
                if (f != null)
                {
                    if (b == 0) f.WindowState = FormWindowState.Minimized;
                    else if (b == 1) f.WindowState = f.WindowState == FormWindowState.Maximized
                        ? FormWindowState.Normal : FormWindowState.Maximized;
                    else if (b == 2) f.Close();
                }
            }
            downBtn = -1;
            Invalidate();
            base.OnMouseUp(e);
        }
    }

    // ===================================================================== 色相条（自定义主色）
    // 一条 0~360° 的色相渐变，点哪取哪，用来做"自定义强调色"。
    internal class HueBar : Control
    {
        public event EventHandler Picked;
        public int Hue = 339;
        public bool Selected;
        private bool dragging;

        public HueBar()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Ui.Bar;
            Cursor = Cursors.Hand;
            Size = new Size(Ui.S(200), Ui.S(46));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BackColor);

            Rectangle r = new Rectangle(Ui.S(2), Height / 2 - Ui.S(9), Width - Ui.S(4), Ui.S(18));
            using (LinearGradientBrush lb = new LinearGradientBrush(r, Color.Black, Color.Black, LinearGradientMode.Horizontal))
            {
                ColorBlend cb = new ColorBlend();
                cb.Colors = new Color[] {
                    Ui.Hsl(0, .82f, .55f), Ui.Hsl(60, .82f, .55f), Ui.Hsl(120, .82f, .55f),
                    Ui.Hsl(180, .82f, .55f), Ui.Hsl(240, .82f, .55f), Ui.Hsl(300, .82f, .55f), Ui.Hsl(360, .82f, .55f) };
                cb.Positions = new float[] { 0f, 1f / 6, 2f / 6, 3f / 6, 4f / 6, 5f / 6, 1f };
                lb.InterpolationColors = cb;
                using (GraphicsPath p = Ui.Round(r, r.Height / 2)) g.FillPath(lb, p);
            }

            // 当前位置的小圆点
            int x = r.X + (int)Math.Round((r.Width - 1) * (Hue % 360) / 360.0);
            Rectangle dot = new Rectangle(x - Ui.S(6), r.Y + r.Height / 2 - Ui.S(6), Ui.S(12), Ui.S(12));
            using (SolidBrush b = new SolidBrush(Color.White)) g.FillEllipse(b, dot);
            using (Pen pen = new Pen(Ui.Hsl(Hue, .82f, .55f), Math.Max(2f, Ui.S(2f))))
                g.DrawEllipse(pen, dot);
            if (Selected)
                using (Pen pen = new Pen(Color.White, 1f))
                    using (GraphicsPath p = Ui.Round(new Rectangle(0, 0, Width - 1, Height - 1), Ui.S(9))) g.DrawPath(pen, p);
        }

        private void Pick(Point p)
        {
            Rectangle r = new Rectangle(Ui.S(2), Height / 2 - Ui.S(9), Width - Ui.S(4), Ui.S(18));
            double f = (p.X - r.X) / (double)Math.Max(1, r.Width - 1);
            f = f < 0 ? 0 : (f > 1 ? 1 : f);
            Hue = (int)Math.Round(f * 360);
            if (Hue >= 360) Hue = 359;
            Invalidate();
            if (Picked != null) Picked(this, EventArgs.Empty);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            dragging = true;
            Pick(e.Location);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (dragging) Pick(e.Location);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            dragging = false;
        }
    }

    // ===================================================================== 自绘滚动容器
    // 原生 Panel 的滚动条是系统样式（白底箭头），和深色界面不搭，这里自己画一根细的。
    internal class ScrollPanel : Panel
    {
        public readonly Panel Content;
        private float offset;          // 当前实际位置（像素，可为小数）
        private float offsetTarget;    // 滚轮累积的目标位置
        private bool drag;
        private int dragY;
        private float dragOff;
        private Timer anim;

        public ScrollPanel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Ui.Bg;
            Content = new Panel();
            Content.BackColor = Ui.Bg;
            Controls.Add(Content);
        }

        private int MaxScroll { get { return Math.Max(0, Content.Height - ClientSize.Height); } }

        private void EnsureAnim()
        {
            if (anim != null) return;
            anim = new Timer();
            anim.Interval = 15;
            anim.Tick += delegate
            {
                float d = offsetTarget - offset;
                if (Math.Abs(d) <= 0.8f)
                {
                    offset = offsetTarget;
                    anim.Stop();
                }
                else offset += d * 0.2f;
                Apply();
            };
        }

        // 缓动滚动（滚轮每格只改目标，由定时器追上去）
        private void ScrollTo(float v)
        {
            float max = MaxScroll;
            if (v < 0) v = 0;
            if (v > max) v = max;
            offsetTarget = v;
            EnsureAnim();
            if (!anim.Enabled) anim.Start();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (Content != null) Content.Width = ClientSize.Width;
            if (offsetTarget > MaxScroll) offsetTarget = MaxScroll;
            if (offset > MaxScroll) offset = MaxScroll;
            Apply();
        }

        private void Apply()
        {
            if (Content != null) Content.Top = -(int)Math.Round(offset);
            Invalidate();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            ScrollTo(offsetTarget - e.Delta / 120f * Ui.S(96));
        }

        private Rectangle Thumb()
        {
            int trackH = ClientSize.Height - Ui.S(8);
            int h = Content.Height <= ClientSize.Height
                ? trackH
                : (int)Math.Max(Ui.S(34), (double)trackH * ClientSize.Height / Math.Max(1, Content.Height));
            int top = Ui.S(4) + (MaxScroll == 0 ? 0 : (int)((trackH - h) * ((double)offset / MaxScroll)));
            return new Rectangle(ClientSize.Width - Ui.S(10), top, Ui.S(6), h);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (MaxScroll <= 0) return;
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle r = Thumb();
            Ui.FillRound(g, r, r.Width / 2, Ui.A(Color.White, 70));
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (MaxScroll <= 0) return;
            if (Thumb().Contains(e.Location))
            {
                drag = true;
                dragY = e.Y;
                dragOff = offset;
                Capture = true;
                if (anim != null) anim.Stop();
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!drag) return;
            int trackH = ClientSize.Height - Ui.S(8);
            int travel = Math.Max(1, trackH - Thumb().Height);
            offset = dragOff + (float)((e.Y - dragY) * (double)MaxScroll / travel);
            if (offset < 0) offset = 0;
            if (offset > MaxScroll) offset = MaxScroll;
            offsetTarget = offset;
            Apply();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            drag = false;
            Capture = false;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && anim != null) { try { anim.Stop(); anim.Dispose(); } catch { } anim = null; }
            base.Dispose(disposing);
        }
    }

    // ===================================================================== 圆角卡片容器
    // Panel 自己画不出圆角，这里包一层：先画圆角底 + 1px 描边，再把子控件画上去。
    // 详情区的细滚动指示条（窗口太小时出现）
    internal class ScrollHint : Control
    {
        private readonly DetailPane owner;

        public ScrollHint(DetailPane o)
        {
            owner = o;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Ui.Bg;
            Enabled = false;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BackColor);
            float ratio = owner == null ? 1f : owner.ScrollRatio;
            float pos = owner == null ? 0f : owner.ScrollPos;
            int h = Math.Max(Ui.S(26), (int)(Height * ratio));
            int top = (int)((Height - h) * pos);
            Ui.FillRound(g, new Rectangle(0, top, Width, h), Math.Max(1, Width / 2), Ui.A(Color.White, 70));
        }
    }
    internal class CardBox : Panel
    {
        public CardBox()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Ui.Bg;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BackColor);
            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            Ui.FillRound(g, r, Ui.S(10), Ui.Card);
            Ui.StrokeRound(g, r, Ui.S(10), Ui.Line, 1f);
            base.OnPaint(e);
        }
    }

    // ===================================================================== 设置里的色块 / 勾选
    
    internal class Swatch : Control
    {
        public string Key = "pink";
        public bool Selected;
        public event EventHandler Picked;

        public Swatch(string key)
        {
            Key = key;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Ui.Bar;
            Cursor = Cursors.Hand;
            Size = new Size(Ui.S(46), Ui.S(46));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BackColor);
            Color c = Ui.SwatchColor(Key);
            int d = Math.Min(Width, Height) - Ui.S(14);
            Rectangle r = new Rectangle((Width - d) / 2, (Height - d) / 2, d, d);
            using (SolidBrush b = new SolidBrush(c)) g.FillEllipse(b, r);
            if (Selected)
            {
                using (Pen pen = new Pen(Color.White, Math.Max(2f, 2f * Ui.Scale)))
                    g.DrawEllipse(pen, r.X - Ui.S(4), r.Y - Ui.S(4), r.Width + Ui.S(8), r.Height + Ui.S(8));
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (Picked != null) Picked(this, EventArgs.Empty);
        }
    }

    
    internal class FlatCheck : Control
    {
        public bool Checked;
        public event EventHandler Changed;

        public FlatCheck(string text)
        {
            Text = text;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Ui.Bar;
            ForeColor = Ui.Txt;
            Font = Ui.FHint;
            Cursor = Cursors.Hand;
            Height = Ui.S(28);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BackColor);
            int s = Ui.S(17);
            Rectangle box = new Rectangle(0, (Height - s) / 2, s, s);
            if (Checked) Ui.FillRound(g, box, Ui.S(5), Ui.Accent);
            else Ui.StrokeRound(g, box, Ui.S(5), Ui.Line, 1.4f);
            if (Checked)
            {
                using (Pen pen = new Pen(Color.White, Math.Max(1.6f, 1.8f * Ui.Scale)))
                {
                    pen.StartCap = LineCap.Round;
                    pen.EndCap = LineCap.Round;
                    g.DrawLines(pen, new Point[]
                    {
                        new Point(box.X + s / 4, box.Y + s / 2),
                        new Point(box.X + s / 2 - 1, box.Y + s * 3 / 4 - 1),
                        new Point(box.X + s - s / 5, box.Y + s / 3)
                    });
                }
            }
            Ui.Text(g, Text, Font, new Rectangle(s + Ui.S(10), 0, Width - s - Ui.S(10), Height), Ui.Txt,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Checked = !Checked;
            Invalidate();
            if (Changed != null) Changed(this, EventArgs.Empty);
        }
    }

    // 设置里“显示哪些模组”的勾选列表
    
    internal class CheckList : Control
    {
        public readonly List<string> Names = new List<string>();
        public readonly HashSet<string> VisibleSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private float scroll, scrollTarget;
        private int hover = -1;
        private readonly int rowH;

        public CheckList()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Ui.Card;
            rowH = Ui.S(30);
        }

        public void SetNames(List<string> names, HashSet<string> visible)
        {
            Names.Clear();
            Names.AddRange(names);
            VisibleSet.Clear();
            foreach (string v in visible) VisibleSet.Add(v);
            scroll = scrollTarget = 0;
            Invalidate();
        }

        private int ContentH { get { return Names.Count * rowH; } }

        public bool Animate()
        {
            float d = scrollTarget - scroll;
            if (Math.Abs(d) > 0.35f) { scroll += d * 0.2f; Invalidate(); return true; }
            if (scroll != scrollTarget) { scroll = scrollTarget; Invalidate(); }
            return false;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BackColor);
            using (Pen p = new Pen(Ui.Line)) g.DrawRectangle(p, 0, 0, Width - 1, Height - 1);

            for (int i = 0; i < Names.Count; i++)
            {
                int y = (int)(i * rowH - scroll);
                if (y > Height || y + rowH < 0) continue;
                bool on = VisibleSet.Contains(Names[i]);

                if (i == hover)
                {
                    using (SolidBrush b = new SolidBrush(Ui.A(Color.White, 10)))
                        g.FillRectangle(b, 1, y + 1, Width - 2, rowH - 1);
                }

                int s = Ui.S(16);
                Rectangle box = new Rectangle(Ui.S(10), y + (rowH - s) / 2, s, s);
                if (on) Ui.FillRound(g, box, Ui.S(4), Ui.Accent);
                else Ui.StrokeRound(g, box, Ui.S(4), Ui.Line, 1.3f);
                if (on)
                {
                    using (Pen pen = new Pen(Color.White, Math.Max(1.5f, 1.7f * Ui.Scale)))
                    {
                        pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round;
                        g.DrawLines(pen, new Point[]
                        {
                            new Point(box.X + s / 4, box.Y + s / 2),
                            new Point(box.X + s / 2 - 1, box.Y + s * 3 / 4 - 1),
                            new Point(box.X + s - s / 5, box.Y + s / 3)
                        });
                    }
                }
                Ui.Text(g, Names[i], Ui.FHint, new Rectangle(Ui.S(36), y, Width - Ui.S(46), rowH),
                    on ? Ui.Txt : Ui.Sub,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
            }

            // 模组多到装不下时给一条细滚动条，否则用户不知道下面还有
            if (ContentH > Height)
            {
                int trackH = Height - Ui.S(8);
                if (trackH > 4)
                {
                    int maxScroll = ContentH - Height;
                    int thumbH = Math.Max(Ui.S(24), (int)(trackH * (double)Height / ContentH));
                    int thumbY = Ui.S(4) + (int)((trackH - thumbH) * (maxScroll <= 0 ? 0 : scroll / maxScroll));
                    Ui.FillRound(g, new Rectangle(Width - Ui.S(9), Ui.S(4), Ui.S(4), trackH), Ui.S(2), Ui.A(Color.White, 20));
                    Ui.FillRound(g, new Rectangle(Width - Ui.S(9), thumbY, Ui.S(4), thumbH), Ui.S(2), Ui.A(Color.White, 95));
                }
            }
        }

        private void ClampScroll()
        {
            float max = Math.Max(0, ContentH - ClientSize.Height);
            if (scrollTarget < 0) scrollTarget = 0;
            if (scrollTarget > max) scrollTarget = max;
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            scroll -= e.Delta / 120f * Ui.S(60);
            float max = Math.Max(0, ContentH - ClientSize.Height);
            if (scroll < 0) scroll = 0;
            if (scroll > max) scroll = max;
            scrollTarget = scroll;
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int i = (int)Math.Floor((e.Y + scroll) / (double)rowH);
            if (i < 0 || i >= Names.Count) i = -1;
            if (i != hover) { hover = i; Cursor = i >= 0 ? Cursors.Hand : Cursors.Default; Invalidate(); }
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
            int i = (int)Math.Floor((e.Y + scroll) / (double)rowH);
            if (i < 0 || i >= Names.Count) return;
            string n = Names[i];
            if (VisibleSet.Contains(n)) VisibleSet.Remove(n); else VisibleSet.Add(n);
            Invalidate();
        }
    }

    // ===================================================================== 深色右键菜单
    
    internal class DarkMenuColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground { get { return Ui.Card; } }
        public override Color ImageMarginGradientBegin { get { return Ui.Card; } }
        public override Color ImageMarginGradientMiddle { get { return Ui.Card; } }
        public override Color ImageMarginGradientEnd { get { return Ui.Card; } }
        public override Color MenuBorder { get { return Ui.Line; } }
        public override Color MenuItemBorder { get { return Ui.A(Ui.Accent, 120); } }
        public override Color MenuItemSelected { get { return Ui.CardHi; } }
        public override Color MenuItemSelectedGradientBegin { get { return Ui.CardHi; } }
        public override Color MenuItemSelectedGradientEnd { get { return Ui.CardHi; } }
        public override Color SeparatorDark { get { return Ui.Line; } }
        public override Color SeparatorLight { get { return Ui.Line; } }
    }

    
    internal class DarkMenuRenderer : ToolStripProfessionalRenderer
    {
        public DarkMenuRenderer() : base(new DarkMenuColors()) { RoundedEdges = false; }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Selected ? Color.White : Ui.Txt;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            using (SolidBrush b = new SolidBrush(Ui.Card)) e.Graphics.FillRectangle(b, e.AffectedBounds);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            using (Pen p = new Pen(Ui.Line))
                e.Graphics.DrawRectangle(p, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
        }
    }

    // ===================================================================== 设置窗口
    
    internal class SettingsForm : Form
    {
        public string RootPath;
        public string AccentName;
        public bool Minimize;
        public string SortMode;
        public string LangCode = "zh";
        public bool FoldVanilla = true;
        public bool KeyStatsOn = false;
        public bool PermDelete = false;
        public bool ExportDiag = false;
        public bool PanelWindow = false;

        private string closeMode = "ask";
        private readonly List<RoundButton> closeModeBtns = new List<RoundButton>();

        public string CloseMode
        {
            get { return closeMode; }
            set { closeMode = value == null ? "ask" : value; }
        }

        private RoundButton CloseModeBtn(string text, string mode, int x)
        {
            RoundButton b = new RoundButton(text);
            b.Filled = false;
            b.Font = Ui.FSmall;
            b.BackColor = Ui.Bar;
            b.SetBounds(x, ClientSize.Height - Ui.S(48), Ui.S(74), Ui.S(26));
            b.Click += delegate
            {
                closeMode = mode;
                RefreshCloseBtns();
            };
            return b;
        }

        private void RefreshCloseBtns()
        {
            string label = closeMode == "hide" ? "隐藏到托盘" : (closeMode == "exit" ? "直接退出" : "每次询问");
            foreach (RoundButton b in closeModeBtns)
            {
                b.Text = "关闭窗口时：" + label;
                b.Filled = (closeMode != "ask");
                b.Invalidate();
            }
        }

        private readonly TextBox txtRoot;
        private readonly CheckList checkList;
        private readonly FlatCheck chkMin;
        private readonly List<Swatch> swatches = new List<Swatch>();
        private readonly List<RoundButton> sortButtons = new List<RoundButton>();

        public SettingsForm(string root, string accent, bool minimize, string sortMode, bool vanillaFold, bool keyStats,
                            bool permDelete, bool panelWindow, List<string> allMods, HashSet<string> visible)
        {
            RootPath = root;
            AccentName = accent;
            Minimize = minimize;
            SortMode = sortMode ?? "recent";
            FoldVanilla = vanillaFold;
            KeyStatsOn = keyStats;
            PermDelete = permDelete;
            PanelWindow = panelWindow;
            LangCode = Lang.Current;
            {
                RoundButton bLang = new RoundButton("");
                bLang.Filled = false;
                bLang.Font = Ui.FSmall;
                bLang.SetBounds(Ui.S(236), Ui.S(238), Ui.S(300), Ui.S(24));
                Action refreshLang = delegate
                {
                    string name = LangCode == "zh" ? "中文" : "English";
                    string tail = LangCode == Lang.Current ? "" : "（保存后自动重启）";
                    bLang.Text = (Lang.Current == "zh" ? "界面语言：" : "Language: ") + name + tail;
                    bLang.Invalidate();
                };
                refreshLang();
                bLang.MouseUp += delegate(object s, MouseEventArgs ea)
                {
                    LangCode = LangCode == "zh" ? "en" : "zh";
                    refreshLang();
                };
                Controls.Add(bLang);
            }

            Text = "设置";
            BackColor = Ui.Bar;
            ForeColor = Ui.Txt;
            Font = Ui.FHint;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoScaleMode = AutoScaleMode.None;
            ClientSize = new Size(Ui.S(560), Ui.S(646));

            Label l1 = Title("识别路径（放模组的文件夹）");
            l1.SetBounds(Ui.S(24), Ui.S(16), Ui.S(400), Ui.S(22));

            txtRoot = new TextBox();
            txtRoot.BorderStyle = BorderStyle.FixedSingle;
            txtRoot.BackColor = Ui.Card;
            txtRoot.ForeColor = Ui.Txt;
            txtRoot.Font = Ui.FHint;
            txtRoot.Text = root;
            txtRoot.SetBounds(Ui.S(24), Ui.S(42), Ui.S(380), Ui.S(26));

            RoundButton bBrowse = new RoundButton("浏览…");
            bBrowse.Filled = false;
            bBrowse.SetBounds(Ui.S(412), Ui.S(41), Ui.S(56), Ui.S(28));
            bBrowse.Click += delegate
            {
                FolderBrowserDialog fd = new FolderBrowserDialog();
                fd.Description = "选择放模组的文件夹";
                if (Directory.Exists(txtRoot.Text)) fd.SelectedPath = txtRoot.Text;
                if (fd.ShowDialog(this) == DialogResult.OK) txtRoot.Text = fd.SelectedPath;
            };

            RoundButton bDefault = new RoundButton("默认");
            bDefault.Filled = false;
            bDefault.SetBounds(Ui.S(474), Ui.S(41), Ui.S(62), Ui.S(28));
            bDefault.Click += delegate { txtRoot.Text = Path.GetDirectoryName(Application.ExecutablePath); };

            Label l2 = Title("强调色");
            l2.SetBounds(Ui.S(24), Ui.S(86), Ui.S(200), Ui.S(22));

            // 预设色块 + 色相条：按窗口宽度算，10 个色块用 38 的节距，剩下的全给色相条
            int swPitch = Ui.S(38);
            int swSize = Ui.S(34);
            HueBar hueBar = null;   // 先声明：点击色块时要把色相条的选中框收掉
            for (int i = 0; i < Ui.AccentNames.Length; i++)
            {
                Swatch sw = new Swatch(Ui.AccentNames[i]);
                sw.SetBounds(Ui.S(24) + i * swPitch, Ui.S(115), swSize, swSize);
                sw.Selected = string.Equals(Ui.AccentNames[i], accent, StringComparison.OrdinalIgnoreCase);
                sw.Picked += delegate(object s, EventArgs e)
                {
                    AccentName = ((Swatch)s).Key;
                    foreach (Swatch o in swatches) o.Selected = (o == s);
                    foreach (Swatch o in swatches) o.Invalidate();
                    if (hueBar != null) { hueBar.Selected = false; hueBar.Invalidate(); }
                };
                swatches.Add(sw);
                Controls.Add(sw);
            }

            // 自定义主色：一条色相条，点哪取哪
            int hueX = Ui.S(24) + Ui.AccentNames.Length * swPitch + Ui.S(6);
            int hueW = Math.Max(Ui.S(96), ClientSize.Width - Ui.S(24) - hueX);
            Label lc = Title("自定义主色");
            lc.SetBounds(hueX, Ui.S(86), hueW, Ui.S(22));

            int hue0 = 339;
            {
                string a0 = accent ?? "";
                if (a0.StartsWith("hue:")) int.TryParse(a0.Substring(4), out hue0);
            }
            hueBar = new HueBar();
            hueBar.Hue = hue0;
            hueBar.Selected = (accent ?? "").StartsWith("hue:");
            hueBar.SetBounds(hueX, Ui.S(112), hueW, Ui.S(42));
            hueBar.Picked += delegate(object s, EventArgs e)
            {
                HueBar hb = (HueBar)s;
                AccentName = "hue:" + hb.Hue;
                foreach (Swatch o in swatches) { o.Selected = false; o.Invalidate(); }
                hb.Selected = true;
                hb.Invalidate();
            };
            Controls.Add(hueBar);

            Label l3 = Title("排序方式");
            l3.SetBounds(Ui.S(24), Ui.S(172), Ui.S(200), Ui.S(22));

            string[] modes = { "name", "recent", "engine" };
            string[] labels = { "名称", "最近启动", "引擎" };
            for (int i = 0; i < modes.Length; i++)
            {
                RoundButton b = new RoundButton(labels[i]);
                b.Tag = modes[i];
                b.SetBounds(Ui.S(24) + i * Ui.S(86), Ui.S(196), Ui.S(78), Ui.S(32));
                b.Filled = string.Equals(modes[i], SortMode, StringComparison.OrdinalIgnoreCase);
                b.Click += delegate(object s, EventArgs e)
                {
                    RoundButton src = (RoundButton)s;
                    SortMode = (string)src.Tag;
                    foreach (RoundButton o in sortButtons) o.Filled = (o == src);
                    foreach (RoundButton o in sortButtons) o.Invalidate();
                };
                sortButtons.Add(b);
                Controls.Add(b);
            }

            Label lmb = Title("曲目/预览显示");
            lmb.SetBounds(Ui.S(300), Ui.S(172), Ui.S(240), Ui.S(22));

            List<RoundButton> panelBtns = new List<RoundButton>();
            RoundButton bWin = new RoundButton("窗口");
            bWin.Tag = "window";
            bWin.SetBounds(Ui.S(300), Ui.S(196), Ui.S(76), Ui.S(32));
            bWin.Filled = panelWindow;
            bWin.Click += delegate(object s, EventArgs ea)
            {
                PanelWindow = true;
                foreach (RoundButton o in panelBtns) o.Filled = (o == s);
                foreach (RoundButton o in panelBtns) o.Invalidate();
            };
            RoundButton bMerge = new RoundButton("合并");
            bMerge.Tag = "inline";
            bMerge.Filled = false;
            bMerge.Filled = !panelWindow;
            bMerge.SetBounds(Ui.S(384), Ui.S(196), Ui.S(76), Ui.S(32));
            bMerge.Click += delegate(object s, EventArgs ea)
            {
                PanelWindow = false;
                foreach (RoundButton o in panelBtns) o.Filled = (o == s);
                foreach (RoundButton o in panelBtns) o.Invalidate();
            };
            panelBtns.Add(bWin); panelBtns.Add(bMerge);
            bWin.Filled = panelWindow;
            bMerge.Filled = !panelWindow;
            Controls.Add(lmb); Controls.Add(bWin); Controls.Add(bMerge);

            Label l4 = Title("行为");
            l4.SetBounds(Ui.S(24), Ui.S(244), Ui.S(200), Ui.S(22));

            chkMin = new FlatCheck("启动游戏后隐藏到系统托盘（双击图标恢复）");
            chkMin.Checked = minimize;
            chkMin.SetBounds(Ui.S(24), Ui.S(264), Ui.S(320), Ui.S(28));
            chkMin.Changed += delegate { Minimize = chkMin.Checked; };

            FlatCheck chkVanilla = new FlatCheck("把疑似原版曲目折叠起来（关闭则全部平铺显示）");
            chkVanilla.Checked = vanillaFold;
            chkVanilla.SetBounds(Ui.S(24), Ui.S(292), Ui.S(420), Ui.S(28));
            chkVanilla.Changed += delegate { FoldVanilla = chkVanilla.Checked; };

            FlatCheck chkKeys = new FlatCheck("统计键盘按键次数（只记次数，不记录输入内容）");
            chkKeys.Checked = keyStats;
            chkKeys.SetBounds(Ui.S(24), Ui.S(320), Ui.S(420), Ui.S(28));
            chkKeys.Changed += delegate { KeyStatsOn = chkKeys.Checked; };

            RoundButton bClear = new RoundButton("清理 mod 缓存");
            bClear.Filled = false;
            bClear.SetBounds(Ui.S(128), Ui.S(352), Ui.S(98), Ui.S(30));
            bClear.Visible = false;
            bClear.Click += delegate
            {
                Config c3 = new Config(Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "launcher.ini"));
                ModCache.Clear(c3);
                AskBox.Show(this, "缓存已清理。点「保存」回到主界面后会重新识别所有 mod。",
                    "清理缓存", MessageBoxButtons.OK, MessageBoxIcon.Information);
            };

            RoundButton bClean = new RoundButton("清理垃圾…");
            bClean.Filled = false;
            bClean.SetBounds(Ui.S(336), Ui.S(352), Ui.S(98), Ui.S(30));
            bClean.Visible = false;
            bClean.Click += delegate { using (CleanupForm f = new CleanupForm()) f.ShowDialog(this); };

            RoundButton bDiag = new RoundButton("导出诊断信息");
            bDiag.Filled = false;
            bDiag.SetBounds(Ui.S(232), Ui.S(352), Ui.S(98), Ui.S(30));
            bDiag.Click += delegate
            {
                ExportDiag = true;
                bDiag.Text = "保存后导出 ✓";
                bDiag.Invalidate();
            };

            RoundButton bCache = new RoundButton("清理试听缓存");
            bCache.Filled = false;
            bCache.SetBounds(Ui.S(232), Ui.S(352), Ui.S(98), Ui.S(30));
            bCache.Visible = false;
            bCache.Click += delegate
            {
                long before = Mixer.CacheSize();
                Mixer.ClearCache();
                AskBox.Show(this, "试听缓存已清理（释放约 " + (before / (1024 * 1024)) + " MB）",
                    "清理试听缓存", MessageBoxButtons.OK, MessageBoxIcon.Information);
            };

            RoundButton bKeys = new RoundButton("查看键盘统计…");
            bKeys.Filled = false;
            bKeys.SetBounds(Ui.S(24), Ui.S(352), Ui.S(98), Ui.S(30));
            bKeys.Click += delegate
            {
                Config c2 = new Config(Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "launcher.ini"));
                Dictionary<int, int> map = new Dictionary<int, int>();
                long total = 0;
                KeyStats.Load(c2, map);
                KeyStats.Import(map);
                total = KeyStats.Total;
                using (KeyStatsForm kf = new KeyStatsForm(map, total)) kf.ShowDialog(this);
            };

            FlatCheck chkPerm = new FlatCheck("永久删除模组（不进回收站，危险操作）");
            chkPerm.Checked = PermDelete;
            chkPerm.SetBounds(Ui.S(24), Ui.S(384), Ui.S(420), Ui.S(28));
            chkPerm.Changed += delegate { PermDelete = chkPerm.Checked; };

            Label l5 = Title("列表里显示的模组（取消勾选就只在后台保留，随时可勾回来）");
            l5.SetBounds(Ui.S(24), Ui.S(416), Ui.S(512), Ui.S(22));

            checkList = new CheckList();
            checkList.SetBounds(Ui.S(24), Ui.S(442), Ui.S(512), Ui.S(96));
            checkList.SetNames(allMods, visible);

            RoundButton ok = new RoundButton("保存");
            ok.SetBounds(Ui.S(330), Ui.S(562), Ui.S(100), Ui.S(40));
            ok.Click += delegate { RootPath = txtRoot.Text.Trim(); DialogResult = DialogResult.OK; Close(); };

            RoundButton cancel = new RoundButton("取消");
            cancel.Filled = false;
            cancel.SetBounds(Ui.S(438), Ui.S(562), Ui.S(98), Ui.S(40));
            cancel.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };

            Controls.Add(l1); Controls.Add(txtRoot); Controls.Add(bBrowse); Controls.Add(bDefault);
            Controls.Add(l2); Controls.Add(lc); Controls.Add(l3); Controls.Add(l4); Controls.Add(chkMin); Controls.Add(l5);
            Controls.Add(chkVanilla); Controls.Add(chkKeys); Controls.Add(bKeys); Controls.Add(bClear);
            RoundButton bCacheAll = new RoundButton("清理缓存…");
            bCacheAll.SetBounds(Ui.S(128), Ui.S(352), Ui.S(98), Ui.S(30));
            bCacheAll.Click += delegate
            {

                using (CleanupForm f = new CleanupForm()) f.ShowDialog(this);
            };
            Controls.Add(bCacheAll);

            Controls.Add(bDiag); Controls.Add(chkPerm); Controls.Add(bCache); Controls.Add(bClean);
            RoundButton bAbout = new RoundButton("关于");
            bAbout.Filled = false;
            bAbout.Font = Ui.FSmall;
            bAbout.SetBounds(Ui.S(24), ClientSize.Height - Ui.S(44), Ui.S(76), Ui.S(26));
            bAbout.Click += delegate
            {
                using (AboutForm af = new AboutForm(allMods.Count, 0, root)) af.ShowDialog(this);
            };
            RoundButton cmBtn = new RoundButton("关闭窗口时：每次询问");
            cmBtn.Filled = false;
            cmBtn.Font = Ui.FSmall;
            cmBtn.BackColor = Ui.Bar;
            cmBtn.SetBounds(Ui.S(106), ClientSize.Height - Ui.S(44), Ui.S(168), Ui.S(26));
            cmBtn.Click += delegate
            {
                if (closeMode == "ask") closeMode = "hide";
                else if (closeMode == "hide") closeMode = "exit";
                else closeMode = "ask";
                RefreshCloseBtns();
            };
            closeModeBtns.Add(cmBtn);
            Controls.Add(cmBtn);
            System.Windows.Forms.Timer refreshClose = new System.Windows.Forms.Timer();
            refreshClose.Interval = 60;
            refreshClose.Tick += delegate
            {
                refreshClose.Stop();
                refreshClose.Dispose();
                RefreshCloseBtns();
            };
            refreshClose.Start();

            Controls.Add(bAbout);
            Controls.Add(checkList); Controls.Add(ok); Controls.Add(cancel);

            AcceptButton = null;
        }

        private static Label Title(string text)
        {
            Label l = new Label();
            l.Text = text;
            l.AutoSize = false;
            l.BackColor = Ui.Bar;
            l.ForeColor = Ui.Txt;
            l.Font = Ui.FBig;
            l.TextAlign = ContentAlignment.MiddleLeft;
            return l;
        }

        public HashSet<string> GetVisibleMods()
        {
            HashSet<string> set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string n in checkList.VisibleSet) set.Add(n);
            return set;
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Ui.DarkTitleBar(Handle);
            Ui.FadeIn(this);
        }
    }

    // ===================================================================== 启动参数窗口
    
    internal class ArgsForm : Form
    {
        public string Args;
        public string WorkDir;

        private readonly TextBox txtArgs, txtWork;

        public ArgsForm(ModEntry m)
        {
            Args = m.Args ?? "";
            WorkDir = m.WorkDir ?? "";

            Text = "启动参数 · " + m.Name;
            BackColor = Ui.Bar;
            ForeColor = Ui.Txt;
            Font = Ui.FHint;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoScaleMode = AutoScaleMode.None;
            ClientSize = new Size(Ui.S(520), Ui.S(250));

            Label l1 = Label2("启动参数（留空表示不加参数，例如 -debug）", Ui.S(24), Ui.S(18));
            txtArgs = new TextBox();
            txtArgs.BorderStyle = BorderStyle.FixedSingle;
            txtArgs.BackColor = Ui.Card;
            txtArgs.ForeColor = Ui.Txt;
            txtArgs.Font = Ui.FHint;
            txtArgs.Text = Args;
            txtArgs.SetBounds(Ui.S(24), Ui.S(44), Ui.S(468), Ui.S(26));

            Label l2 = Label2("工作目录（留空 = exe 所在目录）", Ui.S(24), Ui.S(82));
            txtWork = new TextBox();
            txtWork.BorderStyle = BorderStyle.FixedSingle;
            txtWork.BackColor = Ui.Card;
            txtWork.ForeColor = Ui.Txt;
            txtWork.Font = Ui.FHint;
            txtWork.Text = WorkDir;
            txtWork.SetBounds(Ui.S(24), Ui.S(108), Ui.S(368), Ui.S(26));

            RoundButton bBrowse = new RoundButton("浏览…");
            bBrowse.Filled = false;
            bBrowse.SetBounds(Ui.S(400), Ui.S(107), Ui.S(92), Ui.S(28));
            bBrowse.Click += delegate
            {
                FolderBrowserDialog fd = new FolderBrowserDialog();
                if (Directory.Exists(txtWork.Text)) fd.SelectedPath = txtWork.Text;
                else if (Directory.Exists(m.Dir)) fd.SelectedPath = m.Dir;
                if (fd.ShowDialog(this) == DialogResult.OK) txtWork.Text = fd.SelectedPath;
            };

            RoundButton ok = new RoundButton("保存");
            ok.SetBounds(Ui.S(292), Ui.S(186), Ui.S(100), Ui.S(38));
            ok.Click += delegate
            {
                Args = txtArgs.Text.Trim();
                WorkDir = txtWork.Text.Trim();
                DialogResult = DialogResult.OK;
                Close();
            };

            RoundButton cancel = new RoundButton("取消");
            cancel.Filled = false;
            cancel.SetBounds(Ui.S(400), Ui.S(186), Ui.S(92), Ui.S(38));
            cancel.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };

            Controls.Add(l1); Controls.Add(txtArgs); Controls.Add(l2); Controls.Add(txtWork);
            Controls.Add(bBrowse); Controls.Add(ok); Controls.Add(cancel);
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            Ui.DarkTitleBar(Handle);
        }

        private static Label Label2(string text, int x, int y)
        {
            Label l = new Label();
            l.Text = text;
            l.AutoSize = false;
            l.BackColor = Ui.Bar;
            l.ForeColor = Ui.Sub;
            l.Font = Ui.FHint;
            l.TextAlign = ContentAlignment.MiddleLeft;
            l.SetBounds(x, y, Ui.S(470), Ui.S(22));
            return l;
        }
    }

    // ===================================================================== 主窗口
    
    internal class MainForm : Form
    {
        private const int EM_SETCUEBANNER = 0x1501;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int cmd);

        private readonly Config cfg;
        private readonly List<ModEntry> all = new List<ModEntry>();
        private readonly HashSet<string> hidden = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Process> running = new Dictionary<string, Process>(StringComparer.OrdinalIgnoreCase);

        private string root;
        private string accentName = "pink";
        private string sortMode = "recent";
        private bool minimizeOnLaunch = true;
        private bool foldVanilla = true;
        private bool keyStatsOn = false;
        private readonly Dictionary<int, int> keyMap = new Dictionary<int, int>();
        private readonly Dictionary<string, DateTime> startedAt = new Dictionary<string, DateTime>();
        private bool firstRunDone;

        private Panel header, footer;
        private TitleBar title;
        private ModList list;
        private DetailPane detail;
        private Toast toast;
        private LaunchOverlay overlay;
        private SearchBox search;
        private RoundButton btnRefresh, btnOpen, btnSettings, btnDl, btnCat, btnLog, btnLook;
        private System.Windows.Forms.Timer animTimer, runTimer;
        private string filter = "";
        private string catFilter = "";
        private Rectangle verRect = Rectangle.Empty;

        // 后台引擎识别
        private readonly Queue<ModEntry> detectQueue = new Queue<ModEntry>();
        private readonly object detectLock = new object();
        private bool detectRunning;

        public MainForm(string argRoot)
        {
            cfg = new Config(Path.Combine(ExeDir(), "launcher.ini"));

            root = cfg.Get("root", "");
            if (root.Length == 0 || !Directory.Exists(root))
                root = (argRoot != null && Directory.Exists(argRoot)) ? argRoot : ExeDir();

            accentName = cfg.Get("accent", "pink");
            try
            {
                int a0 = 110;
                int.TryParse(cfg.Get("bgAlpha", "110"), out a0);
                Ui.SetBackAlpha(a0);
                Ui.SetBackImage(LoadBackImage(cfg.Get("bg", "")));
            }
            catch { }
            Ui.SetAccent(accentName);

            minimizeOnLaunch = cfg.GetBool("minimize", true);
            sortMode = cfg.Get("sort", cfg.GetBool("recentFirst", true) ? "recent" : "name");
            firstRunDone = cfg.GetBool("firstRun", false);
            foldVanilla = cfg.GetBool("vanillaFold", true);
            keyStatsOn = cfg.GetBool("keyStats", false);
            DetailPane.FoldVanilla = foldVanilla;
            DetailPane.UseWindow = !string.Equals(cfg.Get("panelMode", "inline"), "inline", StringComparison.OrdinalIgnoreCase);
            if (keyStatsOn)
            {
                KeyStats.Load(cfg, keyMap);
                KeyStats.Import(keyMap);
                KeyStats.Start();
            }

            foreach (string k in cfg.KeysStartingWith("hidden."))
                if (cfg.GetBool(k, false)) hidden.Add(k.Substring(7));

            SuspendLayout();
            Text = Lang.T("FNF 模组启动器");
            FormBorderStyle = FormBorderStyle.None;      // 标题栏自己画
            BackColor = Ui.Bg;
            Font = Ui.FHint;
            AutoScaleMode = AutoScaleMode.None;
            ClientSize = new Size(Ui.S(1040), Ui.S(650));
            MinimumSize = new Size(Ui.S(900), Ui.S(560));
            StartPosition = FormStartPosition.CenterScreen;
            DoubleBuffered = true;
            KeyPreview = true;
            AllowDrop = true;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            RestoreBoundsFromConfig();

            header = new Panel();
            header.Dock = DockStyle.Top;
            header.Height = Ui.S(64);
            header.BackColor = Ui.Bar;
            header.Paint += HeaderPaint;
            header.Resize += delegate { LayoutHeader(); };
            header.MouseDown += HeaderMouseDown;
            header.Cursor = Cursors.Default;

            title = new TitleBar();
            title.AnimRequest = RequestAnim;
            title.AboutRequested += delegate { ShowAbout(); };

            footer = new Panel();
            footer.Dock = DockStyle.Bottom;
            footer.Height = Ui.S(30);
            footer.BackColor = Ui.Bar;
            footer.Paint += FooterPaint;

            list = new ModList();
            list.Dock = DockStyle.Left;
            list.Width = Ui.S(396);
            list.SelectionChanged += delegate { detail.SetMod(list.SelectedMod); };
            list.LaunchRequested += delegate(ModEntry m) { Launch(m); };
            list.ContextRequested += ShowModMenu;
            list.AddRequested += delegate { OpenAddDialog(); };
            list.AllowDrop = true;
            list.DragEnter += DragEnterHandler;
            list.DragDrop += DragDropHandler;

            detail = new DetailPane();
            detail.Dock = DockStyle.Fill;
            detail.LaunchRequested += delegate(ModEntry m) { Launch(m); };
            detail.OpenFolderRequested += delegate(ModEntry m)
            {
                if (m.IsWeb) OpenUrl(m.Url);
                else OpenFolder(m.LaunchDir);
            };
            detail.SongsToggled += delegate { cfg.Set("songsopen", detail.Mod != null && detail.Mod.SongsOpen); cfg.Save(); };
            detail.PreviewToggled += delegate { cfg.Set("previewopen", detail.Mod != null && detail.Mod.PreviewOpen); cfg.Save(); };
            detail.PreviewOpenRequested += delegate(ModEntry m) { OpenPreview(m); };
            try { string _bk = SaveBackup.Root; } catch { }   // 启动即触发旧备份目录迁移
            CleanupForm.Cfg = cfg;
            InfoForm.Cfg = cfg;
            InfoForm.Changed = Rescan;
            detail.StatusText = ShowToast;

            // 开启动画：一块盖住全窗口的幕布，1.2 秒后向上滑走
            // 旧的全窗幕布开场动画已由 SplashForm + 主窗口淡入取代（如需恢复把这行改回来）
            // IntroCurtain intro = new IntroCurtain();
            // intro.SetBounds(0, 0, ClientSize.Width, ClientSize.Height);
            // intro.Anchor = AnchorStyles.None;
            // Controls.Add(intro);
            // intro.BringToFront();
            detail.SongsWindowRequested += delegate { if (detail.Mod != null) OpenSongsWindow(detail.Mod); };
            detail.PreviewWindowRequested += delegate { if (detail.Mod != null) OpenPreviewWindow(detail.Mod); };
            detail.InfoWindowRequested += delegate { if (detail.Mod != null) { using (InfoForm f = new InfoForm(detail.Mod)) f.ShowDialog(this); } };
            detail.AllowDrop = true;
            detail.DragEnter += DragEnterHandler;
            detail.DragDrop += DragDropHandler;

            toast = new Toast();
            toast.Size = new Size(Ui.S(320), Ui.S(64));
            toast.BackColor = Ui.Bg;
            toast.Visible = false;

            overlay = new LaunchOverlay();
            overlay.AnimRequest = RequestAnim;

            search = new SearchBox();
            search.CueText = "搜索模组 / 引擎…";
            search.Size = new Size(Ui.S(210), Ui.S(28));
            search.TextChanged += delegate
            {
                filter = search.Text.Trim();
                ApplyFilter(true);
            };

            btnRefresh = new RoundButton("刷新");
            btnRefresh.Filled = false;
            btnRefresh.Glass = true;
            btnRefresh.Size = new Size(Ui.S(60), Ui.S(30));
            btnRefresh.Click += delegate { RescanAsync(); };

            btnOpen = new RoundButton("打开目录");
            btnOpen.Filled = false;
            btnOpen.Glass = true;
            btnOpen.Size = new Size(Ui.S(82), Ui.S(30));
            btnOpen.Click += delegate { OpenFolder(root); };

            btnSettings = new RoundButton("设置");
            btnSettings.Filled = false;
            btnSettings.Glass = true;
            btnSettings.Size = new Size(Ui.S(60), Ui.S(30));
            btnSettings.Click += delegate { OpenSettings(); };

            btnDl = new RoundButton("下载模组");
            btnDl.Visible = false;   // 入口已移到「添加模组」窗口里
            btnDl.Filled = false;
            btnDl.Size = new Size(Ui.S(74), Ui.S(30));
            btnDl.Click += delegate { ShowDownload(); };

            btnCat = new RoundButton("分类");
            btnCat.Filled = false;
            btnCat.Glass = true;
            btnCat.Size = new Size(Ui.S(60), Ui.S(30));
            btnCat.Click += delegate { ShowCategoryMenu(); };

            btnLog = new RoundButton("日志");
            btnLog.Filled = false;
            btnLog.Glass = true;
            btnLog.Size = new Size(Ui.S(60), Ui.S(30));
            btnLog.Click += delegate
            {
                using (LaunchLogForm f = new LaunchLogForm()) f.ShowDialog(this);
            };

            btnLook = new RoundButton("外观");
            btnLook.Filled = false;
            btnLook.Glass = true;
            btnLook.Size = new Size(Ui.S(60), Ui.S(30));
            btnLook.Click += delegate { OpenAppearance(); };

            // 按钮宽度跟着文字走（外语更长，避免被裁成 "ppearance"）
            foreach (RoundButton hb in new RoundButton[] { btnRefresh, btnOpen, btnLog, btnLook, btnCat, btnSettings })
            {
                if (hb == null || string.IsNullOrEmpty(hb.Text)) continue;
                int need = TextRenderer.MeasureText(hb.Text, hb.Font).Width + Ui.S(20);
                if (need > hb.Width) hb.Width = need;
            }
            header.Controls.Add(search);
            // 操作按钮直接住在标题栏里（PCL 的导航也在标题栏）
            title.Controls.Add(btnRefresh);
            title.Controls.Add(btnOpen);
            title.Controls.Add(btnSettings);
            title.Controls.Add(btnCat);
            title.Controls.Add(btnLog);
            title.Controls.Add(btnLook);
            title.Controls.Add(btnDl);
            title.Resize += delegate { LayoutHeader(); };

            Controls.Add(detail);
            Controls.Add(list);
            Controls.Add(toast);
            Controls.Add(overlay);
            Controls.Add(footer);
            Controls.Add(header);
            Controls.Add(title);

            animTimer = new System.Windows.Forms.Timer();
            animTimer.Interval = 30;
            animTimer.Tick += delegate { TickAnim(); };

            runTimer = new System.Windows.Forms.Timer();
            runTimer.Interval = 1200;
            runTimer.Tick += delegate { UpdateRunningState(); };

            resizePoll = new System.Windows.Forms.Timer();
            resizePoll.Interval = 20;
            resizePoll.Tick += ResizePollTick;
            resizePoll.Start();

            list.AnimRequest = RequestAnim;
            detail.AnimRequest = RequestAnim;
            toast.AnimRequest = RequestAnim;
            btnRefresh.AnimRequest = RequestAnim;
            btnOpen.AnimRequest = RequestAnim;
            btnSettings.AnimRequest = RequestAnim;
            btnDl.AnimRequest = RequestAnim;
            btnCat.AnimRequest = RequestAnim;

            DragEnter += DragEnterHandler;
            DragDrop += DragDropHandler;

            ResumeLayout(true);
            if (Lang.Active) FitHeaderButtons();

            LayoutHeader();
            PositionToast();

            if (firstRunDone) RescanAsync();
        }

        private static string ExeDir()
        {
            try { return Path.GetDirectoryName(Application.ExecutablePath); }
            catch { return Environment.CurrentDirectory; }
        }

        private void RestoreBoundsFromConfig()
        {
            string b = cfg.Get("window", "");
            if (b.Length == 0) return;
            try
            {
                string[] parts = b.Split(',');
                if (parts.Length != 4) return;
                int x = int.Parse(parts[0]), y = int.Parse(parts[1]);
                int w = int.Parse(parts[2]), h = int.Parse(parts[3]);
                if (w < Ui.S(600) || h < Ui.S(400)) return;
                Rectangle scr = Screen.FromPoint(new Point(x, y)).WorkingArea;
                if (x < scr.Left - Ui.S(40) || y < scr.Top - Ui.S(40)) return;
                int mw = Math.Max(w, MinimumSize.Width), mh = Math.Max(h, MinimumSize.Height);
                ClientSize = new Size(mw, mh);
                StartPosition = FormStartPosition.Manual;
                Location = new Point(x, y);
            }
            catch { }
        }

        // ---------------------------------------------------------------- 动画
        private bool inSizeMove;
        private System.Windows.Forms.Timer resizeSettle;
        private bool hoverEdgeCursor;

        private void RequestAnim()
        {
            if (inSizeMove) return;
            if (!animTimer.Enabled) animTimer.Start();
        }

        protected override void WndProc(ref Message m)
        {
            const int WM_ENTERSIZEMOVE = 0x0231;
            const int WM_EXITSIZEMOVE = 0x0232;
            // 滚轮统一在这里按鼠标位置分发（子控件不一定拿到焦点）
            // 注意：LParam 里是屏幕坐标，64 位下有符号扩展，必须先转 long 再 unchecked 转 int
            if (m.Msg == 0x020A && detail != null && list != null)
            {
                long lpRaw = m.LParam.ToInt64();
                int lp32 = unchecked((int)lpRaw);
                int delta = unchecked((short)(((int)m.WParam.ToInt64() >> 16) & 0xFFFF));
                if (delta != 0)
                {
                    Point scr = new Point(unchecked((short)(lp32 & 0xFFFF)), unchecked((short)((lp32 >> 16) & 0xFFFF)));
                    Point cp = PointToClient(scr);
                    if (detail.Bounds.Contains(cp) && Ui.Now - detail.LastWheelAt > 80) { detail.ScrollBy(delta); m.Result = IntPtr.Zero; return; }
                    if (list.Bounds.Contains(cp)) { list.WheelScroll(delta); m.Result = IntPtr.Zero; return; }
                }
            }
            if (m.Msg == 0x0084 /*WM_NCHITTEST*/)
            {
                base.WndProc(ref m);
                return;
            }
            if (m.Msg == 0x0024 /*WM_GETMINMAXINFO*/)
            {
                base.WndProc(ref m);
                // 无边框窗口直接最大化会盖住任务栏，这里把最大化区域压回工作区
                Rectangle wa = Screen.FromHandle(Handle).WorkingArea;
                FormWindowState st = WindowState;
                MINMAXINFO mmi = (MINMAXINFO)Marshal.PtrToStructure(m.LParam, typeof(MINMAXINFO));
                mmi.ptMaxPosition.x = wa.Left;
                mmi.ptMaxPosition.y = wa.Top;
                mmi.ptMaxSize.x = wa.Width;
                mmi.ptMaxSize.y = wa.Height;
                Marshal.StructureToPtr(mmi, m.LParam, false);
                return;
            }
            if (m.Msg == WM_ENTERSIZEMOVE)
            {
                inSizeMove = true;
                if (animTimer != null) animTimer.Stop();
            }
            else if (m.Msg == WM_EXITSIZEMOVE)
            {
                inSizeMove = false;
                RequestAnim();
            }
            base.WndProc(ref m);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MPoint { public int x, y; }

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr h, out MRECT r);

        [StructLayout(LayoutKind.Sequential)]
        private struct MRECT { public int left, top, right, bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct NCCALCSIZE_PARAMS
        {
            public MRECT rgrc0, rgrc1, rgrc2;
            public IntPtr lppos;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MINMAXINFO
        {
            public MPoint ptReserved, ptMaxSize, ptMaxPosition, ptMinTrackSize, ptMaxTrackSize;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                // 只保留最小化/最大化能力，不要 WS_THICKFRAME——
                // 它会让"窗口矩形"永远比"客户区"大 7px，圆角和布局都会跟着错位。
                // 拖边缩放由下面的 ResizePoll 自己实现。
                cp.Style |= 0x00020000;    // WS_MINIMIZEBOX
                cp.Style |= 0x00010000;    // WS_MAXIMIZEBOX
                return cp;
            }
        }

        // ---------------------------------------------------------------- 自绘窗口的拖边缩放
        private const int EdgeNone = 0, EdgeL = 1, EdgeR = 2, EdgeT = 3, EdgeB = 4,
                          EdgeLT = 5, EdgeRT = 6, EdgeLB = 7, EdgeRB = 8;
        private int resizeEdge;
        private bool resizeTracking;
        private Point resizeStartCursor;
        private Rectangle resizeStartRect;
        private System.Windows.Forms.Timer resizePoll;

        private int EdgeAt(Point p)
        {
            int e = Ui.S(6);
            if (WindowState != FormWindowState.Normal) return EdgeNone;
            bool l = p.X <= e, r = p.X >= ClientSize.Width - e;
            bool t = p.Y <= e, b = p.Y >= ClientSize.Height - e;
            if (!(l || r || t || b)) return EdgeNone;
            if (t && l) return EdgeLT;
            if (t && r) return EdgeRT;
            if (b && l) return EdgeLB;
            if (b && r) return EdgeRB;
            if (l) return EdgeL;
            if (r) return EdgeR;
            if (t) return EdgeT;
            return EdgeB;
        }

        private void ResizePollTick(object sender, EventArgs e)
        {
            if (!Visible || WindowState != FormWindowState.Normal) { resizeTracking = false; return; }
            bool down = (Control.MouseButtons & MouseButtons.Left) != 0;
            Point cur = Cursor.Position;

            if (!resizeTracking)
            {
                if (ActiveForm != this) return;
                int hoverEdge = EdgeAt(PointToClient(cur));
                Cursor wantCur = Cursors.Default;
                if (hoverEdge == EdgeL || hoverEdge == EdgeR) wantCur = Cursors.SizeWE;
                else if (hoverEdge == EdgeT || hoverEdge == EdgeB) wantCur = Cursors.SizeNS;
                else if (hoverEdge == EdgeLT || hoverEdge == EdgeRB) wantCur = Cursors.SizeNWSE;
                else if (hoverEdge == EdgeRT || hoverEdge == EdgeLB) wantCur = Cursors.SizeNESW;
                if (wantCur != Cursors.Default || hoverEdgeCursor)
                {
                    Cursor.Current = wantCur;
                    hoverEdgeCursor = wantCur != Cursors.Default;
                }
                int edge = EdgeAt(PointToClient(cur));
                if (edge != EdgeNone && down)
                {
                    resizeTracking = true;
                    resizeEdge = edge;
                    resizeStartCursor = cur;
                    resizeStartRect = Bounds;
                }
                return;
            }

            if (!down) { resizeTracking = false; return; }

            int dx = cur.X - resizeStartCursor.X;
            int dy = cur.Y - resizeStartCursor.Y;
            Rectangle r = resizeStartRect;
            int minW = MinimumSize.Width, minH = MinimumSize.Height;

            if (resizeEdge == EdgeL || resizeEdge == EdgeLT || resizeEdge == EdgeLB)
            {
                int w = Math.Max(minW, r.Width - dx);
                r.X = r.Right - w; r.Width = w;
            }
            else if (resizeEdge == EdgeR || resizeEdge == EdgeRT || resizeEdge == EdgeRB)
                r.Width = Math.Max(minW, r.Width + dx);

            if (resizeEdge == EdgeT || resizeEdge == EdgeLT || resizeEdge == EdgeRT)
            {
                int h = Math.Max(minH, r.Height - dy);
                r.Y = r.Bottom - h; r.Height = h;
            }
            else if (resizeEdge == EdgeB || resizeEdge == EdgeLB || resizeEdge == EdgeRB)
                r.Height = Math.Max(minH, r.Height + dy);

            if (r != Bounds) Bounds = r;
        }

        private void ApplyFormRegion()
        {
            if (WindowState != FormWindowState.Normal) { Region = null; return; }
            using (GraphicsPath p = Ui.Round(new Rectangle(0, 0, Width, Height), Ui.S(8)))
            {
                Region old = Region;
                Region = new Region(p);
                if (old != null) old.Dispose();
            }
        }

        private void TickAnim()
        {
            bool busy = false;
            if (title.Animate()) busy = true;
            if (overlay.Animate()) busy = true;
            if (list.Animate()) busy = true;
            if (detail.Animate()) busy = true;
            if (toast.Animate()) busy = true;
            if (btnRefresh.Animate()) busy = true;
            if (btnOpen.Animate()) busy = true;
            if (btnSettings.Animate()) busy = true;
            if (btnCat.Animate()) busy = true;
            if (btnLog.Animate()) busy = true;
            if (btnLook.Animate()) busy = true;
            if (!busy) animTimer.Stop();
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            Lang.Set(cfg.Get("lang", "zh"));
            if (Lang.Active)
            {
                Lang.Sweep(this);
                // 翻译后再量一次宽度（英文比中文长，按钮会被裁）
                foreach (RoundButton hb in new RoundButton[] { btnRefresh, btnOpen, btnLog, btnLook, btnCat, btnSettings })
                {
                    if (hb == null || string.IsNullOrEmpty(hb.Text)) continue;
                    hb.Font = Ui.FSmall;
                    int need = TextRenderer.MeasureText(hb.Text, hb.Font).Width + Ui.S(18);
                    if (need > hb.Width) hb.Width = need;
                }
                LayoutHeader();
                if (search != null) search.CueText = Lang.T("搜索模组 / 引擎…");
                Lang.StartAutoSweep();
            }
            Ui.DarkTitleBar(Handle);
            ApplyFormRegion();
            PositionToast();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            // 启动画面先挡着，这里淡入主窗口
            FadeInMain();
            if (!firstRunDone) RunWelcome();
        }

        private void FadeInMain()
        {
            Opacity = 0;
            double at0 = Ui.Now;
            Timer t = new Timer();
            t.Interval = 15;
            t.Tick += delegate
            {
                float k = Ui.Clamp01((float)((Ui.Now - at0) / 260.0));
                Opacity = 0.2 + 0.8 * k;
                if (k >= 1f) { t.Stop(); t.Dispose(); Opacity = 1; if (SplashForm.Current != null) { SplashForm.Current.Close(); SplashForm.Current = null; } }
            };
            t.Start();
        }

        private void RunWelcome()
        {
            using (WelcomeForm f = new WelcomeForm(root))
            {
                if (f.ShowDialog(this) == DialogResult.OK && f.SelectedPath.Length > 0)
                {
                    root = f.SelectedPath;
                    cfg.Set("root", root);
                }
            }
            firstRunDone = true;
            cfg.Set("firstRun", true);
            cfg.Save();
            RescanAsync();
        }

        protected override void OnActivated(EventArgs e)
        {
            base.OnActivated(e);
            if (resizePoll != null) resizePoll.Start();
        }

        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            if (resizePoll != null) resizePoll.Stop();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            LayoutSplit();
            PositionToast();
            // 拖动/缩放时不要每帧重设 Region、重画四个大控件（闪的根源）；停下来 90ms 再做一次
            if (resizeSettle == null)
            {
                resizeSettle = new System.Windows.Forms.Timer();
                resizeSettle.Interval = 90;
                resizeSettle.Tick += delegate
                {
                    resizeSettle.Stop();
                    ApplyFormRegion();
                    if (list != null) list.Invalidate();
                    if (detail != null) detail.Invalidate();
                    if (header != null) header.Invalidate();
                    if (footer != null) footer.Invalidate();
                    if (title != null) title.Invalidate();
                };
            }
            resizeSettle.Stop();
            resizeSettle.Start();
        }
        private void LayoutSplit()
        {
            if (list == null) return;
            int w = (int)(ClientSize.Width * 0.37f);
            w = Math.Max(Ui.S(330), Math.Min(Ui.S(470), w));
            list.Width = w;
        }

        private void PositionToast()
        {
            if (toast == null) return;
            toast.SetBounds(list.Right + Ui.S(20), ClientSize.Height - footer.Height - Ui.S(96),
                ClientSize.Width - list.Right - Ui.S(40), Ui.S(70));
            PositionOverlay();
        }

        private void PositionOverlay()
        {
            if (overlay == null || title == null || header == null || footer == null) return;
            int top = title.Height + header.Height;
            int h = ClientSize.Height - top - footer.Height;
            if (h < 0) h = 0;
            overlay.SetBounds(0, top, ClientSize.Width, h);
        }

        // 外语标签更长：按实测文字宽度重设按钮宽度
        private void FitHeaderButtons()
        {
            RoundButton[] bs = { btnRefresh, btnOpen, btnLog, btnLook, btnCat, btnSettings, btnDl };
            foreach (RoundButton b in bs)
            {
                if (b == null || string.IsNullOrEmpty(b.Text)) continue;
                if (Lang.Active) b.Font = Ui.FSmall;
                int pad = Lang.Active ? Ui.S(12) : Ui.S(22);
                b.Width = TextRenderer.MeasureText(b.Text, b.Font).Width + pad;
            }
        }
        private void LayoutHeader()
        {
            if (header == null || title == null || btnSettings == null) return;
            int pad = Ui.S(24);

            // 标题栏右侧：设置 / 分类 / 打开目录 / 刷新，最后再留出 − □ × 的位置
            int right = title.Width - Ui.S(46 * 3) - Ui.S(10);
            int y = (title.Height - btnSettings.Height) / 2;
            btnSettings.Location = new Point(right - btnSettings.Width, y);
            right -= btnSettings.Width + Ui.S(8);
            btnCat.Location = new Point(right - btnCat.Width, y);
            right -= btnCat.Width + Ui.S(8);
            btnLog.Location = new Point(right - btnLog.Width, y);
            right -= btnLog.Width + Ui.S(8);
            btnLook.Location = new Point(right - btnLook.Width, y);
            right -= btnLook.Width + Ui.S(8);
            btnOpen.Location = new Point(right - btnOpen.Width, y);
            right -= btnOpen.Width + Ui.S(8);
            btnRefresh.Location = new Point(right - btnRefresh.Width, y);
            if (btnDl.Visible)
            {
                right -= btnDl.Width + Ui.S(8);
                btnDl.Location = new Point(right - btnDl.Width, y);
            }

            // header 右侧只剩搜索框
            search.Location = new Point(header.ClientSize.Width - pad - search.Width,
                (header.Height - search.Height) / 2);
        }

        private void HeaderPaint(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Ui.PaintBack(g, header);

            int local = 0, web = 0;
            foreach (ModEntry m in all)
            {
                if (hidden.Contains(m.Name)) continue;
                if (m.IsWeb) web++; else local++;
            }
            string sub = "本地模组 " + local + " 个";
            if (web > 0) sub += "　·　网页版 " + web + " 个";
            sub += "　·　识别目录：" + root;
            if (filter.Length > 0) sub += "　·　搜索「" + filter + "」";
            Ui.Text(g, sub, Ui.FSub,
                new Rectangle(Ui.S(24), Ui.S(22), Math.Max(Ui.S(120), header.Width - search.Width - Ui.S(60)), Ui.S(22)),
                Ui.Sub, TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                        TextFormatFlags.SingleLine | TextFormatFlags.PathEllipsis);

            using (SolidBrush b = new SolidBrush(Ui.A(Ui.Accent, 70)))
                g.FillRectangle(b, 0, header.Height - Math.Max(2, Ui.S(2)), header.Width, Math.Max(2, Ui.S(2)));
        }

        private void HeaderMouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && verRect.Contains(e.Location)) ShowAbout();
        }

        private void FooterPaint(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Ui.PaintBack(g, footer);
            using (SolidBrush b = new SolidBrush(Ui.Line))
                g.FillRectangle(b, 0, 0, footer.Width, Math.Max(1, Ui.S(1)));
            Ui.Text(g, "双击 / 回车 启动　·　↑↓ 选择　·　Ctrl+1~9 快速启动　·　F5 刷新　·　+ / - 缩放界面　·　右键更多",
                Ui.FSmall, new Rectangle(Ui.S(20), 0, footer.Width - Ui.S(40), footer.Height), Ui.Sub,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        }

        // ---------------------------------------------------------------- 扫描
        // 同步扫描：增删改后需要立即拿到结果的操作走这里
        private void Rescan()
        {
            if (scanning) return;
            scanning = true;
            Cursor = Cursors.WaitCursor;
            System.Diagnostics.Stopwatch sw1 = System.Diagnostics.Stopwatch.StartNew();
            List<string> extraDirs = new List<string>();
            foreach (string k in cfg.KeysStartingWith("ref."))
                if (cfg.GetBool(k, false)) extraDirs.Add(k.Substring(4));
            List<ModEntry> found = null;
            string err = null;
            try { found = Scanner.Scan(root, extraDirs, cfg); }
            catch (Exception ex) { err = ex.Message; }
            FinishScan(found, err, sw1);
        }

        private volatile bool scanning;

        // 扫描改为后台线程：UI 不再卡住，扫描期间在页脚显示进度
        private void RescanAsync()
        {
            if (scanning) return;
            scanning = true;
            Cursor = Cursors.WaitCursor;
            ShowToast("正在扫描模组…（" + root + "）");
            Diag.Add("开始扫描：" + root);
            System.Diagnostics.Stopwatch sw0 = System.Diagnostics.Stopwatch.StartNew();

            List<string> extraDirs = new List<string>();
            foreach (string k in cfg.KeysStartingWith("ref."))
                if (cfg.GetBool(k, false)) extraDirs.Add(k.Substring(4));
            string scanRoot = root;
            Config snap = cfg.SnapshotForRead();

            System.Threading.Thread th = new System.Threading.Thread(delegate()
            {
                List<ModEntry> found = null;
                string err = null;
                try { found = Scanner.Scan(scanRoot, extraDirs, snap); }
                catch (Exception ex) { err = ex.Message; }
                try
                {
                    BeginInvoke((MethodInvoker)delegate { FinishScan(found, err, sw0); });
                }
                catch { scanning = false; }
            });
            th.IsBackground = true;
            th.Start();
        }

        private void FinishScan(List<ModEntry> found, string err, System.Diagnostics.Stopwatch sw)
        {
            try
            {
                if (err != null || found == null)
                {
                    ShowToast("扫描失败：" + (err ?? "未知错误"));
                    return;
                }

                // 缃〉鐗堟潯鐩?
                foreach (string k in cfg.KeysStartingWith("web."))
                {
                    string url = cfg.Get(k, "");
                    if (url.Length == 0) continue;
                    ModEntry w = new ModEntry();
                    w.Name = k.Substring(4);
                    w.IsWeb = true;
                    w.Url = url;
                    w.Thumb = Icons.Globe(Ui.S(46));
                    found.Add(w);
                }

                foreach (ModEntry m in all) m.Dispose();
                all.Clear();
                all.AddRange(found);

                foreach (ModEntry m in all)
                {
                    m.Args = cfg.Get("arg." + m.Name, "");
                    m.WorkDir = cfg.Get("work." + m.Name, "");
                    m.LastRun = (double)cfg.GetLong("recent." + m.Name, 0);
                    m.LaunchCount = cfg.GetInt("count." + m.Name, 0);
                    m.PlaySeconds = cfg.GetLong("playsec." + m.Name, 0);
                    m.Pinned = cfg.GetBool("pin." + m.Name, false);
                    m.DisplayName = cfg.Get("name." + m.Name, "");
                    m.Author = cfg.Get("author." + m.Name, "");
                    m.PlaySeconds = cfg.GetLong("play." + m.Name, 0);
                    m.Category = cfg.Get("cat." + m.Name, "");
                    m.SongsOpen = cfg.GetBool("songsopen", false);
                    m.PreviewOpen = cfg.GetBool("previewopen", false);

                    string wic = cfg.Get("wicon." + m.Name, "");
                    if (wic.Length > 0 && File.Exists(wic))
                    {
                        m.WebIconPath = wic;
                        if (m.Thumb != null) m.Thumb.Dispose();
                        m.Thumb = Icons.FromFile(wic, Ui.S(46), m.Name);
                    }

                    if (!m.IsWeb && !m.EngineKnown) ApplyEngineCache(m);
                }

                foreach (ModEntry m in all)
                    if (!m.IsWeb && !m.EngineKnown && m.FileCount > 0 && m.SizeBytes > 0 && !string.IsNullOrEmpty(m.EngineName) && !m.EngineName.StartsWith("识别中")) ModCache.Save(cfg, m);
                cfg.Save();

                ApplyFilter(false);
                LayoutSplit();
                StartDetection();
                sw.Stop();
                Diag.Add("扫描 " + all.Count + " 个模组，用时 " + sw.ElapsedMilliseconds + " ms，缓存命中 " + ModCache.Hits);
                ShowToast("扫描完成：" + all.Count + " 个模组（" + sw.ElapsedMilliseconds + " ms）");
            }
            finally
            {
                scanning = false;
                Cursor = Cursors.Default;
            }
        }

        private void ApplyFilter(bool keepSelection)
        {
            List<ModEntry> shown = new List<ModEntry>();
            string f = filter.ToLowerInvariant();
            foreach (ModEntry m in all)
            {
                if (hidden.Contains(m.Name)) continue;
                if (catFilter.Length > 0)
                {
                    if (catFilter == "?" ) { if (m.Category.Length > 0) continue; }
                    else if (!string.Equals(m.Category, catFilter, StringComparison.OrdinalIgnoreCase)) continue;
                }
                if (f.Length > 0 && !MatchQuery(m, f)) continue;
                shown.Add(m);
            }

            shown.Sort(delegate(ModEntry a, ModEntry b)
            {
                if (a.Pinned != b.Pinned) return a.Pinned ? -1 : 1;
                if (sortMode == "recent")
                {
                    int c = b.LastRun.CompareTo(a.LastRun);
                    if (c != 0) return c;
                }
                else if (sortMode == "engine")
                {
                    int c = string.Compare(a.EngineName, b.EngineName, StringComparison.OrdinalIgnoreCase);
                    if (c != 0) return c;
                }
                return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });

            string keep = null;
            if (keepSelection && list.SelectedMod != null) keep = list.SelectedMod.Name;
            else keep = cfg.Get("last", null);

            list.SetItems(shown, keep);
            header.Invalidate();
        }

        // 搜索：空格分词（AND），支持 分类:xxx 引擎:xxx 作者:xxx 前缀
        private static bool MatchQuery(ModEntry m, string query)
        {
            string[] tokens = query.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            string hayAll = (m.Title + " " + m.Name + " " + (m.IsWeb ? m.Url : Path.GetFileName(m.Exe)) + " " +
                             m.EngineName + " " + m.EngineVersion + " " + m.Category + " " + m.Author).ToLowerInvariant();
            foreach (string tok in tokens)
            {
                string t = tok.ToLowerInvariant();
                if (t.StartsWith("分类:") || t.StartsWith("分类："))
                {
                    string v = t.Substring(3);
                    if (m.Category.ToLowerInvariant().IndexOf(v, StringComparison.Ordinal) < 0) return false;
                    continue;
                }
                if (t.StartsWith("引擎:") || t.StartsWith("引擎："))
                {
                    string v = t.Substring(3);
                    string eng = (m.EngineName + " " + m.EngineVersion).ToLowerInvariant();
                    if (eng.IndexOf(v, StringComparison.Ordinal) < 0) return false;
                    continue;
                }
                if (t.StartsWith("作者:") || t.StartsWith("作者："))
                {
                    string v = t.Substring(3);
                    if (m.Author.ToLowerInvariant().IndexOf(v, StringComparison.Ordinal) < 0) return false;
                    continue;
                }
                if (hayAll.IndexOf(t, StringComparison.Ordinal) < 0 && !MatchesAcronym(m, t)) return false;
            }
            return true;
        }

        // 缩写搜索：Sunday Night Suicide -> sns / snse；Mortimer Night vs Mickey -> mntvm
        private static bool MatchesAcronym(ModEntry m, string token)
        {
            try
            {
                string initials = "";
                string name = m.Name ?? "";
                System.Text.StringBuilder sb = new System.Text.StringBuilder();
                bool start = true;
                foreach (char c in name)
                {
                    if (char.IsLetterOrDigit(c))
                    {
                        if (start) sb.Append(char.ToLowerInvariant(c));
                        start = false;
                    }
                    else start = true;
                }
                initials = sb.ToString();
                if (initials.Length == 0) return false;
                string tk = token.ToLowerInvariant();
                if (initials.Contains(tk)) return true;
                // 每个单词首字母连起来
                string firsts = "";
                bool st = true;
                foreach (char c in name)
                {
                    if (char.IsLetterOrDigit(c)) { if (st) firsts += char.ToLowerInvariant(c); st = false; }
                    else st = true;
                }
                return firsts.Length > 0 && firsts.Contains(tk);
            }
            catch { return false; }
        }
        private void SelectByName(string name)
        {
            for (int i = 0; i < list.Items.Count; i++)
            {
                if (string.Equals(list.Items[i].Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    list.SelectIndex(i, true);
                    return;
                }
            }
        }

        // ---------------------------------------------------------------- 引擎识别（后台）
        private void ApplyEngineCache(ModEntry m)
        {
            string ovr = cfg.Get("ovr." + m.Name, "");
            if (ovr.Length > 0)
            {
                int bar = ovr.IndexOf('|');
                m.EngineName = bar >= 0 ? ovr.Substring(0, bar) : ovr;
                m.EngineVersion = bar >= 0 ? ovr.Substring(bar + 1) : "";
                m.EngineKnown = true;
                return;
            }

            string cached = cfg.Get("eng." + m.Name, "");
            long mtime = 0;
            try { mtime = File.GetLastWriteTimeUtc(m.Exe).Ticks; } catch { }
            long cachedM = cfg.GetLong("engm." + m.Name, 0);
            if (cached.Length > 0 && cachedM == mtime)
            {
                string[] parts = cached.Split('|');
                m.EngineName = parts.Length > 0 ? parts[0] : "";
                m.EngineVersion = parts.Length > 1 ? parts[1] : "";
                m.EngineNote = parts.Length > 2 ? parts[2] : "";
                m.EngineTech = parts.Length > 3 ? parts[3] : "";
                m.EngineKnown = true;
                long sz = cfg.GetLong("sz." + m.Name, -1);
                if (sz > 0) m.SizeBytes = sz;
                return;
            }

            // 没有缓存：先给个占位，后台再识别
            m.EngineName = "识别中…";
            m.EngineVersion = "";
            m.SizeBytes = -1;
            lock (detectLock) { detectQueue.Enqueue(m); }
        }

        private void StartDetection()
        {
            lock (detectLock)
            {
                if (detectRunning || detectQueue.Count == 0) return;
                detectRunning = true;
            }
            System.Threading.Thread t = new System.Threading.Thread(DetectLoop);
            t.IsBackground = true;
            t.Start();
        }

        private void DetectLoop()
        {
            while (true)
            {
                ModEntry m = null;
                lock (detectLock)
                {
                    if (detectQueue.Count == 0) { detectRunning = false; return; }
                    m = detectQueue.Dequeue();
                }
                if (m == null) continue;
                try
                {
                    EngineResult r = null;
                    try { r = EngineDetector.Detect(m.Dir, m.Exe); } catch { r = null; }
                    if (r == null || string.IsNullOrEmpty(r.Name))
                    {
                        // 认不出来也要给个结论，别让界面一直停在「识别中…」
                        m.EngineName = "未知引擎";
                        m.EngineVersion = "";
                    }
                    int files = 0;
                    long newest = 0;
                    long size = EngineDetector.DirStats(m.Dir, out files, out newest, 6000);
                    ModEntry target = m;
                    EngineResult res = r;
                    long sz = size, mt = newest;
                    int fc = files;
                    try
                    {
                        BeginInvoke((Action)delegate { ApplyDetect(target, res, sz, fc, mt); });
                    }
                    catch { }
                }
                catch (Exception ex)
                {
                    // 单个 mod 识别失败只标记它，不影响其它 mod 和扫描线程
                    ModEntry bad = m;
                    string why = ex.GetType().Name;
                    try
                    {
                        BeginInvoke((Action)delegate
                        {
                            if (!all.Contains(bad)) return;
                            bad.EngineName = "识别失败";
                            bad.EngineVersion = "";
                            bad.EngineNote = "点右键→引擎信息…可手动填";
                            bad.EngineKnown = true;
                            list.Invalidate();
                            if (detail.Mod == bad) detail.Touch();
                        });
                    }
                    catch { }
                    Diag.Add("识别失败 " + bad.Title + " -> " + why + " " + ex.Message);
                }
            }
        }

        private void ApplyDetect(ModEntry m, EngineResult r, long size, int files, long mtime)
        {
            if (!all.Contains(m)) return;
            m.EngineName = r.Name;
            m.EngineVersion = r.Version;
            m.EngineNote = r.Note;
            m.EngineTech = r.Tech;
            m.EngineKnown = true;
            m.SizeBytes = size;
            m.FileCount = files;
            m.DirModifiedTicks = mtime;
            ModCache.Save(cfg, m);

            cfg.Set("eng." + m.Name, m.EngineName + "|" + m.EngineVersion + "|" + m.EngineNote + "|" + m.EngineTech);
            cfg.Set("engm." + m.Name, (long)mtime);
            cfg.Set("sz." + m.Name, (long)size);
            cfg.Save();

            list.Invalidate();
            if (detail.Mod == m) detail.Touch();
            header.Invalidate();
        }

        // ---------------------------------------------------------------- 运行状态
        private void UpdateRunningState()
        {
            bool changed = false;
            List<string> dead = new List<string>();
            foreach (KeyValuePair<string, Process> kv in running)
            {
                try
                {
                    if (kv.Value.HasExited) dead.Add(kv.Key);
                }
                catch { dead.Add(kv.Key); }
            }
            foreach (string k in dead)
            {
                running.Remove(k);
                changed = true;
                DateTime st;
                if (startedAt.TryGetValue(k, out st))
                {
                    startedAt.Remove(k);
                    int sec = (int)Math.Max(1, (DateTime.Now - st).TotalSeconds);
                    foreach (ModEntry mm in all)
                    {
                        if (!string.Equals(mm.Name, k, StringComparison.OrdinalIgnoreCase)) continue;
                        mm.PlaySeconds += sec;
                        cfg.Set("play." + mm.Name, mm.PlaySeconds);
                        cfg.Save();
                        break;
                    }
                }
            }
            if (changed)
            {
                foreach (ModEntry m in all) m.Running = running.ContainsKey(m.Name);
                list.Invalidate();
                if (detail.Mod != null) detail.Touch();
            }
            if (running.Count == 0) runTimer.Stop();
        }

        private void MarkRunning(ModEntry m, Process p)
        {
            running[m.Name] = p;
            startedAt[m.Name] = DateTime.Now;
            m.Running = true;
            if (!runTimer.Enabled) runTimer.Start();
        }

        // ---------------------------------------------------------------- 菜单
        private void ShowModMenu(ModEntry m)
        {
            ContextMenuStrip cm = new ContextMenuStrip();
            cm.BackColor = Ui.Card;
            cm.ForeColor = Ui.Txt;
            cm.Font = Ui.FHint;
            cm.Renderer = new DarkMenuRenderer();
            cm.ShowImageMargin = false;

            cm.Items.Add(m.IsWeb ? "▶  打开网页" : "▶  启动游戏", null, delegate { Launch(m); });
            cm.Items.Add(m.IsWeb ? "复制网址" : "打开所在文件夹", null, delegate
            {
                if (m.IsWeb) CopyText(m.Url); else OpenFolder(m.LaunchDir);
            });
            cm.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem more = new ToolStripMenuItem("更多");
            more.ForeColor = Ui.Txt;
            more.DropDown.BackColor = Ui.Card;
            more.DropDown.Renderer = new DarkMenuRenderer();

            more.DropDownItems.Add("重命名…", null, delegate { RenameMod(m); });
            more.DropDownItems.Add("设置分类…", null, delegate { SetCategory(m); });
            more.DropDownItems.Add("设置图标…", null, delegate { SetIcon(m); });
            if (!m.IsWeb)
            {
                more.DropDownItems.Add("启动参数…", null, delegate { EditArgs(m); });
                more.DropDownItems.Add("引擎信息…", null, delegate { EditEngine(m); });
            }
            more.DropDownItems.Add(m.Pinned ? "取消置顶" : "置顶此模组", null, delegate { TogglePin(m); });
            more.DropDownItems.Add("设置作者/简介…", null, delegate { SetAuthor(m); });
            more.DropDownItems.Add("复制路径", null, delegate { CopyText(m.IsWeb ? m.Url : m.Dir); });
            if (!m.IsWeb)
                more.DropDownItems.Add("打开存档目录", null, delegate { OpenSaveFolder(m); });
                more.DropDownItems.Add("检查更新（GameBanana）", null, delegate { CheckUpdate(m); });
                more.DropDownItems.Add("备份存档", null, delegate
                {
                    string r2 = SaveBackup.Backup(m, 10);
                    ShowToast(r2 != null ? ("已备份存档：" + Path.GetFileName(r2)) : "没有可备份的存档文件");
                });
                ToolStripMenuItem restore = new ToolStripMenuItem("恢复存档…");
                string[] snaps = SaveBackup.Snapshots(m);
                restore.Enabled = snaps != null && snaps.Length > 0;
                restore.ForeColor = restore.Enabled ? Ui.Txt : Ui.Sub;
                if (snaps != null)
                {
                    foreach (string sn in snaps)
                    {
                        string snap = sn;
                        restore.DropDownItems.Add(Path.GetFileName(snap), null, delegate
                        {
                            if (AskBox.Show(this, "用这份备份覆盖当前存档？\\r\\n\\r\\n" + Path.GetFileName(snap), "恢复存档",
                                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                            ShowToast(SaveBackup.Restore(m, snap) ? "存档已恢复" : "恢复失败，检查备份是否完整");
                        });
                    }
                }
                more.DropDownItems.Add(restore);

            ToolStripMenuItem lnk = new ToolStripMenuItem("创建桌面快捷方式");
            lnk.Enabled = !m.IsWeb;
            lnk.Click += delegate { CreateShortcut(m, false); };
            more.DropDownItems.Add(lnk);

            ToolStripMenuItem lnk2 = new ToolStripMenuItem("创建开始菜单快捷方式");
            lnk2.Enabled = !m.IsWeb;
            lnk2.Click += delegate { CreateShortcut(m, true); };
            more.DropDownItems.Add(lnk2);

            if (!m.IsWeb)
                more.DropDownItems.Add("在资源管理器中定位 exe", null, delegate
                {
                    try { Ui.ShellOpen("explorer.exe", "/select,\"" + m.Exe + "\""); } catch { }
                });
            if (m.External)
                more.DropDownItems.Add("移除引用", null, delegate
                {
                    cfg.Set("ref." + m.Dir, false);
                    cfg.Save();
                    Rescan();
                    ShowToast("已移除引用");
                });

            more.DropDownItems.Add(new ToolStripSeparator());
            more.DropDownItems.Add("从列表删除", null, delegate { RemoveFromList(m); });

            ToolStripMenuItem del = new ToolStripMenuItem(m.IsWeb ? "删除本地游戏（网页版不可用）" : "删除本地游戏…");
            del.Enabled = !m.IsWeb;
            del.Click += delegate { DeleteLocal(m); };
            more.DropDownItems.Add(del);


            if (list != null && list.MultiCount > 1)
            {
                ToolStripMenuItem batch = new ToolStripMenuItem("批量操作（" + list.MultiCount + " 个）");
                batch.ForeColor = Ui.Txt;
                batch.DropDown.BackColor = Ui.Card;
                batch.DropDown.Renderer = new DarkMenuRenderer();
                batch.DropDownItems.Add("批量隐藏", null, delegate { BatchHide(true); });
                batch.DropDownItems.Add("批量取消隐藏", null, delegate { BatchHide(false); });
                batch.DropDownItems.Add("批量设分类…", null, delegate { BatchCategory(); });
                batch.DropDownItems.Add("批量备份存档", null, delegate { BatchBackup(); });
                cm.Items.Add(batch);
            }
            cm.Items.Add(more);
            cm.Show(list, list.PointToClient(Cursor.Position));
        }

        // ---------------------------------------------------------------- 重命名 / 分类 / 图标 / 快捷方式 / 删除
        private static string SafeName(string s)
        {
            StringBuilder sb = new StringBuilder();
            char[] bad = Path.GetInvalidFileNameChars();
            foreach (char c in (s ?? ""))
            {
                bool ok = true;
                foreach (char b in bad) if (b == c) { ok = false; break; }
                if (ok) sb.Append(c);
            }
            string r = sb.ToString().Trim();
            return r.Length == 0 ? "mod" : r;
        }

        private void RenameMod(ModEntry m)
        {
            using (TextFieldForm f = new TextFieldForm("重命名模组", "显示名称", m.Title,
                       "只改启动器里显示的名字，不会动文件夹名。留空 = 恢复成文件夹名。"))
            {
                if (f.ShowDialog(this) != DialogResult.OK) return;
                string v = f.Value;
                if (string.Equals(v, m.Name, StringComparison.OrdinalIgnoreCase)) v = "";
                m.DisplayName = v;
                cfg.Set("name." + m.Name, v.Length > 0 ? v : null);
                cfg.Save();
                ApplyFilter(true);
                detail.Touch();
                ShowToast(v.Length > 0 ? "已重命名：" + v : "已恢复文件夹名");
            }
        }

        // ---------------------------------------------------------------- 批量操作
        // 更新检测：本地版本 + 用浏览器打开 GameBanana 搜索页
        private void CheckUpdate(ModEntry m)
        {
            string ver = m.EngineVersion.Length > 0 ? m.EngineVersion : "未知";
            string eng = m.EngineDisplay;
            string msg = "模组：" + m.Title + "\r\n引擎：" + eng + "\r\n本地版本：" + ver +
                         "\r\n\r\n是否用浏览器打开 GameBanana 的搜索页？（可直接比对最新版本）";
            if (AskBox.Show(this, msg, "检查更新", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            string q = Uri.EscapeDataString(m.Title);
            try { Ui.ShellOpen("https://gamebanana.com/search?_sSearchString=" + q + "&_idGameRow=8694"); }
            catch { AskBox.Show(this, "打不开浏览器。", "检查更新", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }

        private void BatchHide(bool hide)
        {
            List<ModEntry> sel = list.MultiList();
            foreach (ModEntry m in sel)
            {
                if (hide) hidden.Add(m.Name); else hidden.Remove(m.Name);
                cfg.Set("hidden." + m.Name, hide ? "1" : null);
            }
            cfg.Save();
            list.ClearMulti();
            Rescan();
            ShowToast(hide ? ("已隐藏 " + sel.Count + " 个模组") : ("已取消隐藏 " + sel.Count + " 个"));
        }

        private void BatchCategory()
        {
            using (TextFieldForm f = new TextFieldForm("批量设置分类", "分类名", "",
                       "会应用到当前选中的所有模组。留空 = 设为未分类。"))
            {
                if (f.ShowDialog(this) != DialogResult.OK) return;
                List<ModEntry> sel = list.MultiList();
                foreach (ModEntry m in sel)
                {
                    m.Category = f.Value;
                    cfg.Set("cat." + m.Name, m.Category.Length > 0 ? m.Category : null);
                }
                cfg.Save();
                list.ClearMulti();
                ApplyFilter(true);
                UpdateCatButton();
                ShowToast("已把 " + sel.Count + " 个模组归到：" + (f.Value.Length > 0 ? f.Value : "未分类"));
            }
        }

        private void BatchBackup()
        {
            List<ModEntry> sel = list.MultiList();
            int n = 0;
            Cursor = Cursors.WaitCursor;
            try
            {
                foreach (ModEntry m in sel)
                {
                    if (m.IsWeb) continue;
                    try { if (SaveBackup.Backup(m, 10) != null) n++; } catch { }
                }
            }
            finally { Cursor = Cursors.Default; }
            list.ClearMulti();
            ShowToast("已备份 " + n + " 个模组的存档");
        }

        private void SetCategory(ModEntry m)
        {
            using (TextFieldForm f = new TextFieldForm("设置分类", "分类名", m.Category,
                       "同一个分类的模组可以用顶部「分类」按钮一键筛选。留空 = 未分类。"))
            {
                if (f.ShowDialog(this) != DialogResult.OK) return;
                m.Category = f.Value;
                cfg.Set("cat." + m.Name, m.Category.Length > 0 ? m.Category : null);
                cfg.Save();
                ApplyFilter(true);
                detail.Touch();
                UpdateCatButton();
                ShowToast(m.Category.Length > 0 ? "已归类到：" + m.Category : "已设为未分类");
            }
        }

        private void SetIcon(ModEntry m)
        {
            using (OpenFileDialog fd = new OpenFileDialog())
            {
                fd.Title = "选择图标（png / jpg / ico / bmp）";
                fd.Filter = "图片|*.png;*.jpg;*.jpeg;*.ico;*.bmp|所有文件|*.*";
                if (fd.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    string dir = Path.Combine(ExeDir(), "icons");
                    Directory.CreateDirectory(dir);
                    string dest = Path.Combine(dir, SafeName(m.Name) + Path.GetExtension(fd.FileName).ToLowerInvariant());
                    File.Copy(fd.FileName, dest, true);
                    cfg.Set("wicon." + m.Name, dest);
                    cfg.Save();
                    m.WebIconPath = dest;
                    if (m.Thumb != null) m.Thumb.Dispose();
                    m.Thumb = Icons.FromFile(dest, Ui.S(46), m.Name);
                    if (m.Big != null) { m.Big.Dispose(); m.Big = null; }
                    list.Invalidate();
                    detail.Touch();
                    ShowToast("图标已更新");
                }
                catch (Exception ex)
                {
                    AskBox.Show(this, "设置图标失败：\r\n" + ex.Message, "设置图标",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private void CreateShortcut(ModEntry m, bool startMenu)
        {
            if (m.IsWeb) return;
            try
            {
                string dir = startMenu
                    ? Environment.GetFolderPath(Environment.SpecialFolder.Programs)
                    : Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                string path = Path.Combine(dir, SafeName(m.Title) + ".lnk");
                string icon = m.IconPath != null ? m.IconPath : m.Exe;
                if (ShortcutHelper.Create(path, m.Exe, m.LaunchDir, m.Args, m.Title, icon))
                    ShowToast(startMenu ? "已加到开始菜单" : "已在桌面创建快捷方式");
                else
                    AskBox.Show(this, "创建快捷方式失败（系统限制？）", "快捷方式",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            catch (Exception ex)
            {
                AskBox.Show(this, "创建快捷方式失败：\r\n" + ex.Message, "快捷方式",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void RemoveFromList(ModEntry m)
        {
            if (m.IsWeb)
            {
                cfg.Remove("web." + m.Name);
                cfg.Save();
                Rescan();
                ShowToast("已从列表删除：" + m.Title);
                return;
            }
            if (m.External)
            {
                cfg.Set("ref." + m.Dir, false);
                cfg.Save();
                Rescan();
                ShowToast("已移除引用：" + m.Title);
                return;
            }
            HideMod(m);
        }

        private void DeleteLocal(ModEntry m)
        {
            if (m.IsWeb) return;
            if (m.Running)
            {
                AskBox.Show(this, "这个模组正在运行，先关掉游戏再删。", "删除本地游戏",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            bool perm = cfg.GetBool("permDelete", false);
            string sizeText = m.SizeBytes > 0 ? "（约 " + (m.SizeBytes / (1024.0 * 1024.0)).ToString("0.#") + " MB）" : "";
            DialogResult r = AskBox.Show(this, 
                "删除这个模组？\r\n\r\n" + m.Title + " " + sizeText + "\r\n" + m.Dir +
                (perm ? "\r\n\r\n⚠ 你开启了「永久删除」，不进回收站、不能撤销。"
                      : "\r\n\r\n会移到回收站，之后还能还原。"),
                "删除本地游戏", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (r != DialogResult.Yes) return;

            r = AskBox.Show(this, "再确认一次：删掉 " + m.Dir + " 吗？",
                "删除本地游戏", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (r != DialogResult.Yes) return;

            try
            {
                Cursor = Cursors.WaitCursor;
                if (detail.Mod == m) detail.StopAudio();
                if (perm)
                {
                    Directory.Delete(m.Dir, true);
                }
                else
                {
                    if (!RecycleBin.Delete(m.Dir))
                    {
                        Cursor = Cursors.Default;
                        AskBox.Show(this, "移到回收站失败（可能被占用或路径太长）。", "删除本地游戏",
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return;
                    }
                }
                ClearModConfig(m.Name);
                cfg.Save();
                Cursor = Cursors.Default;
                Rescan();
                ShowToast("已删除：" + m.Title);
            }
            catch (Exception ex)
            {
                Cursor = Cursors.Default;
                AskBox.Show(this, "删除失败（可能被占用）：\r\n" + ex.Message, "删除本地游戏",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void ClearModConfig(string key)
        {
            string[] prefixes = { "name.", "cat.", "wicon.", "hidden.", "pin.", "count.", "recent.",
                                  "arg.", "work.", "eng.", "engm.", "sz.", "ovr.", "web.", "ref." };
            foreach (string p in prefixes) cfg.Remove(p + key);
        }

        private void SetAuthor(ModEntry m)
        {
            using (TextFieldForm f = new TextFieldForm("作者 / 简介", "作者（简介会显示在作者后面）", m.Author,
                       "只影响启动器里显示的信息，不改 mod 文件。留空 = 不显示。"))
            {
                if (f.ShowDialog(this) != DialogResult.OK) return;
                m.Author = f.Value;
                cfg.Set("author." + m.Name, m.Author.Length > 0 ? m.Author : null);
                cfg.Save();
                detail.Touch();
                ShowToast(m.Author.Length > 0 ? "已记录作者：" + m.Author : "已清除作者");
            }
        }

        private void OpenSaveFolder(ModEntry m)
        {
            string p = SavePaths.Find(m);
            if (p == null)
            {
                ShowToast("没找到存档目录，先打开模组目录");
                p = m.Dir;
            }
            OpenFolder(p);
        }

        private void ExportDiagnostics()
        {
            try
            {
                using (SaveFileDialog fd = new SaveFileDialog())
                {
                    fd.Filter = "文本文件|*.txt";
                    fd.FileName = "fnf-launcher-diag-" + DateTime.Now.ToString("yyyyMMdd-HHmm") + ".txt";
                    if (fd.ShowDialog(this) != DialogResult.OK) return;
                    File.WriteAllText(fd.FileName, Diag.Dump(root, all), new UTF8Encoding(false));
                    AskBox.Show(this, "诊断信息已保存到：\r\n" + fd.FileName, "导出诊断",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
            catch (Exception ex)
            {
                AskBox.Show(this, "导出失败：" + ex.Message, "导出诊断", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void ShowCategoryMenu()
        {
            ContextMenuStrip cm = new ContextMenuStrip();
            cm.BackColor = Ui.Card;
            cm.ForeColor = Ui.Txt;
            cm.Font = Ui.FHint;
            cm.Renderer = new DarkMenuRenderer();
            cm.ShowImageMargin = false;


            cm.Items.Add("全部分类", null, delegate { catFilter = ""; ApplyFilter(false); UpdateCatButton(); });
            cm.Items.Add("未分类", null, delegate { catFilter = "?"; ApplyFilter(false); UpdateCatButton(); });

            HashSet<string> cats = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ModEntry m in all)
                if (!hidden.Contains(m.Name) && m.Category.Length > 0) cats.Add(m.Category);

            if (cats.Count > 0) cm.Items.Add(new ToolStripSeparator());
            List<string> sorted = new List<string>(cats);
            sorted.Sort(StringComparer.OrdinalIgnoreCase);
            foreach (string c in sorted)
            {
                string cc = c;
                cm.Items.Add(c, null, delegate { catFilter = cc; ApplyFilter(false); UpdateCatButton(); });
            }
            cm.Items.Add(new ToolStripSeparator());
            cm.Items.Add("导出模组清单…", null, delegate { ExportManifest(); });
            cm.Items.Add("导入模组清单…", null, delegate { ImportManifest(); });
            Lang.SweepMenu(cm.Items, 0);
            cm.Show(btnCat, new Point(0, btnCat.Height));
        }

        // ---------------------------------------------------------------- 模组清单导出 / 导入
        private void ExportManifest()
        {
            using (SaveFileDialog fd = new SaveFileDialog())
            {
                fd.Filter = "文本文件 (*.txt)|*.txt";
                fd.FileName = "模组清单.txt";
                if (fd.ShowDialog(this) != DialogResult.OK) return;
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("# FNF 启动器模组清单 v1");
                sb.AppendLine("# 导出时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm"));
                sb.AppendLine("# 名称\t引擎\t版本\t分类\t来源");
                foreach (ModEntry m in all)
                {
                    string src = m.IsWeb ? (m.Url ?? "") : m.Dir;
                    sb.AppendLine(m.Title + "\t" + m.EngineShort + "\t" + m.EngineVersion + "\t" +
                        (m.Category.Length > 0 ? m.Category : "") + "\t" + src);
                }
                try
                {
                    File.WriteAllText(fd.FileName, sb.ToString(), Encoding.UTF8);
                    ShowToast("已导出 " + all.Count + " 个模组到：" + Path.GetFileName(fd.FileName));
                }
                catch (Exception ex) { AskBox.Show(this, ex.Message, "导出失败", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            }
        }

        private void ImportManifest()
        {
            using (OpenFileDialog fd = new OpenFileDialog())
            {
                fd.Filter = "文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*";
                if (fd.ShowDialog(this) != DialogResult.OK) return;
                string[] lines2;
                try { lines2 = File.ReadAllLines(fd.FileName, Encoding.UTF8); }
                catch (Exception ex) { AskBox.Show(this, ex.Message, "读取失败", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
                List<string> missing = new List<string>();
                List<string[]> missRows = new List<string[]>();
                int total = 0;
                foreach (string raw in lines2)
                {
                    if (raw == null || raw.Trim().Length == 0 || raw.StartsWith("#")) continue;
                    string[] c = raw.Split('\t');
                    if (c.Length < 1) continue;
                    total++;
                    string name = c[0].Trim();
                    bool have = false;
                    foreach (ModEntry m in all)
                        if (string.Equals(m.Title, name, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase)) { have = true; break; }
                    if (have) continue;
                    missing.Add(name);
                    missRows.Add(c);
                }
                if (total == 0) { AskBox.Show(this, "这个文件里没有可识别的模组行。", "导入模组清单", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
                if (missing.Count == 0) { ShowToast("清单里 " + total + " 个模组本地都有"); return; }
                StringBuilder txt = new StringBuilder();
                txt.AppendLine("清单共 " + total + " 个模组，本地缺少 " + missing.Count + " 个：");
                txt.AppendLine("");
                foreach (string[] c in missRows)
                {
                    string eng = c.Length > 1 ? c[1] : "";
                    string ver = c.Length > 2 ? c[2] : "";
                    string src = c.Length > 4 ? c[4] : "";
                    txt.AppendLine("• " + c[0] + (eng.Length > 0 ? "（" + eng + (ver.Length > 0 ? " " + ver : "") + "）" : ""));
                    if (src.Length > 0) txt.AppendLine("    " + src);
                }
                string outp = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "缺失模组.txt");
                try { File.WriteAllText(outp, txt.ToString(), Encoding.UTF8); } catch { }
                txt.AppendLine("");
                txt.AppendLine("完整清单已保存到：缺失模组.txt");
                AskBox.Show(this, txt.ToString(), "导入模组清单", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        private void UpdateCatButton()
        {
            string label = "分类";
            if (catFilter.Length > 0)
            {
                label = catFilter == "?" ? "未分类" : catFilter;
                if (label.Length > 5) label = label.Substring(0, 5) + "…";
            }
            btnCat.Text = label;
            btnCat.Invalidate();
        }

        private void OpenAppearance()
        {
            int a = 110;
            try { int.TryParse(cfg.Get("bgAlpha", "110"), out a); } catch { }
            using (AppearanceForm f = new AppearanceForm(cfg.Get("bg", ""), a, delegate(string path, int alpha)
            {
                try
                {
                    cfg.Set("bg", path ?? "");
                    cfg.Set("bgAlpha", alpha.ToString());
                    cfg.Save();
                }
                catch { }
                Ui.SetBackImage(LoadBackImage(path));
                Ui.SetBackAlpha(alpha);
                ApplyTheme();
                if (!string.Equals(cfg.Get("lang", "zh"), Lang.Current, StringComparison.OrdinalIgnoreCase))
                {
                    // 语言变了：保存完直接重启，不用用户手动关开
                    try { Application.Restart(); return; } catch { }
                }
            }))
            {
                f.ShowDialog(this);
            }
        }

        private static Image LoadBackImage(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    return Image.FromStream(fs);
            }
            catch { return null; }
        }
        private void ShowDownload()
        {
            using (DownloadForm f = new DownloadForm()) f.ShowDialog(this);
        }

        private void OpenSongsWindow(ModEntry m)
        {
            if (m.Songs.Count == 0)
            {
                ShowToast("这个 mod 没有找到曲目");
                return;
            }
            using (SongsForm f = new SongsForm(m)) f.ShowDialog(this);
        }

        private void OpenPreviewWindow(ModEntry m)
        {
            if (m.Previews == null || m.Previews.Count == 0)
            {
                ShowToast("这个 mod 没有可预览的图片");
                return;
            }
            using (PreviewForm f = new PreviewForm(m)) f.ShowDialog(this);
        }

        private void OpenPreview(ModEntry m)
        {
            if (m == null || m.Previews == null || m.Previews.Count == 0)
            {
                ShowToast("这个模组没有可预览的图片");
                return;
            }
            int idx = detail.PreviewOpenIndex;
            if (idx < 0) idx = 0;
            if (idx >= m.Previews.Count) idx = m.Previews.Count - 1;
            using (PreviewViewer v = new PreviewViewer(m.Previews, idx)) v.ShowDialog(this);
        }
        private void TogglePin(ModEntry m)
        {
            m.Pinned = !m.Pinned;
            cfg.Set("pin." + m.Name, m.Pinned);
            cfg.Save();
            ApplyFilter(true);
            detail.Touch();
            ShowToast(m.Pinned ? "已置顶：" + m.Name : "已取消置顶");
        }

        private void HideMod(ModEntry m)
        {
            hidden.Add(m.Name);
            cfg.Set("hidden." + m.Name, true);
            cfg.Save();
            ApplyFilter(false);
            ShowToast("已隐藏：" + m.Name + "（设置里可恢复）");
        }

        private void CopyText(string text)
        {
            try { Clipboard.SetText(text ?? ""); ShowToast("已复制"); }
            catch { }
        }

        private void EditArgs(ModEntry m)
        {
            using (ArgsForm f = new ArgsForm(m))
            {
                if (f.ShowDialog(this) != DialogResult.OK) return;
                m.Args = f.Args;
                m.WorkDir = f.WorkDir;
                if (m.Args.Length > 0) cfg.Set("arg." + m.Name, m.Args); else cfg.Remove("arg." + m.Name);
                if (m.WorkDir.Length > 0) cfg.Set("work." + m.Name, m.WorkDir); else cfg.Remove("work." + m.Name);
                cfg.Save();
                list.Invalidate();
                detail.Touch();
                ShowToast("已保存启动参数");
            }
        }

        private void EditEngine(ModEntry m)
        {
            using (EngineForm f = new EngineForm(m))
            {
                if (f.ShowDialog(this) != DialogResult.OK) return;
                if (f.EngineName.Length == 0)
                {
                    cfg.Remove("ovr." + m.Name);
                    cfg.Remove("eng." + m.Name);
                    cfg.Remove("engm." + m.Name);
                    cfg.Save();
                    Rescan();
                    return;
                }
                cfg.Set("ovr." + m.Name, f.EngineName + "|" + f.EngineVersion);
                cfg.Save();
                m.EngineName = f.EngineName;
                m.EngineVersion = f.EngineVersion;
                m.EngineKnown = true;
                list.Invalidate();
                detail.Touch();
                ShowToast("引擎信息已更新");
            }
        }

        private void ShowAbout()
        {
            int local = 0, web = 0;
            foreach (ModEntry m in all) { if (m.IsWeb) web++; else local++; }
            using (AboutForm f = new AboutForm(local, web, root)) f.ShowDialog(this);
        }

        // ---------------------------------------------------------------- 添加
        private void OpenAddDialog()
        {
            using (AddForm f = new AddForm(root))
            {
                DialogResult dr = f.ShowDialog(this);
                if (dr != DialogResult.OK) return;

                if (f.FolderChosen && f.FolderPath.Length > 0)
                {
                    AddDroppedFolder(f.FolderPath);
                    return;
                }

                if (f.WebChosen && f.WebUrl.Length > 0)
                {
                    string name = f.WebName.Length > 0 ? f.WebName : "网页版 FNF";
                    string uniq = name;
                    int n = 2;
                    while (cfg.Get("web." + uniq, "").Length > 0) { uniq = name + " (" + n + ")"; n++; }
                    cfg.Set("web." + uniq, f.WebUrl);
                    cfg.Save();
                    Rescan();
                    SelectByName(uniq);
                    ShowToast("已添加网页版：" + uniq);
                }
            }
        }

        private void OpenSettings()
        {
            List<string> names = new List<string>();
            HashSet<string> visible = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ModEntry m in all)
            {
                names.Add(m.Name);
                if (!hidden.Contains(m.Name)) visible.Add(m.Name);
            }
            names.Sort(StringComparer.OrdinalIgnoreCase);

            using (SettingsForm f = new SettingsForm(root, accentName, minimizeOnLaunch, sortMode, foldVanilla, keyStatsOn,
                                                    cfg.GetBool("permDelete", false),
                                                    !string.Equals(cfg.Get("panelMode", "inline"), "inline", StringComparison.OrdinalIgnoreCase),
                                                    names, visible))
            {
                f.CloseMode = cfg.Get("closeAction", "ask");
                f.LangCode = cfg.Get("lang", "zh");
                if (f.ShowDialog(this) != DialogResult.OK) return;

                string newRoot = f.RootPath;
                if (newRoot.Length > 0 && !Directory.Exists(newRoot))
                {
                    AskBox.Show(this, "这个文件夹不存在：\r\n" + newRoot, "FNF 模组启动器",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                root = newRoot.Length > 0 ? newRoot : ExeDir();
                accentName = f.AccentName;
                minimizeOnLaunch = f.Minimize;
                sortMode = f.SortMode;
                foldVanilla = f.FoldVanilla;
                keyStatsOn = f.KeyStatsOn;
                cfg.Set("permDelete", f.PermDelete);
                cfg.Set("panelMode", f.PanelWindow ? "window" : "inline");
                cfg.Set("closeAction", f.CloseMode);
                cfg.Set("lang", f.LangCode);
                DetailPane.UseWindow = f.PanelWindow;
                DetailPane.FoldVanilla = foldVanilla;
            DetailPane.UseWindow = !string.Equals(cfg.Get("panelMode", "inline"), "inline", StringComparison.OrdinalIgnoreCase);
                cfg.Set("vanillaFold", foldVanilla);
                cfg.Set("keyStats", keyStatsOn);
                if (keyStatsOn) { if (!KeyStats.Enabled) KeyStats.Start(); }
                else { if (KeyStats.Enabled) KeyStats.Stop(); }

                HashSet<string> vis = f.GetVisibleMods();
                foreach (string k in cfg.KeysStartingWith("hidden.")) cfg.Remove(k);
                hidden.Clear();
                foreach (string n in names)
                    if (!vis.Contains(n)) { hidden.Add(n); cfg.Set("hidden." + n, true); }

                Ui.SetAccent(accentName);
                cfg.Set("root", root);
                cfg.Set("accent", accentName);
                cfg.Set("minimize", minimizeOnLaunch);
                cfg.Set("sort", sortMode);
                cfg.Remove("recentFirst");
                cfg.Save();

                ApplyTheme();
                if (!string.Equals(cfg.Get("lang", "zh"), Lang.Current, StringComparison.OrdinalIgnoreCase))
                {
                    // 语言变了：保存完直接重启，不用用户手动关开
                    try { Application.Restart(); return; } catch { }
                }
                BeginInvoke((MethodInvoker)delegate { Rescan(); detail.SetMod(list.SelectedMod); });   // 设置窗关闭后再扫，避免新目录的曲目/预览为空
                ShowToast("设置已保存");

                if (f.ExportDiag) ExportDiagnostics();
            }
        }

        private void ApplyTheme()
        {
            header.BackColor = Ui.Bar;
            footer.BackColor = Ui.Bar;
            list.BackColor = Ui.Bar;
            detail.BackColor = Ui.Bg;
            toast.BackColor = Ui.Bg;
            title.BackColor = Ui.Bar;
            title.Invalidate();
            if (search != null) search.RefreshTheme();
            btnRefresh.Invalidate();
            btnOpen.Invalidate();
            btnSettings.Invalidate();
            btnDl.Invalidate();
            btnCat.Invalidate();
            btnLog.Invalidate();
            btnLook.Invalidate();
            header.Invalidate();
            footer.Invalidate();
            list.Invalidate();
            detail.Touch();
        }

        // ---------------------------------------------------------------- 启动
        private void Launch(ModEntry m)
        {
            if (m.IsWeb) { LaunchWeb(m); return; }
            try
            {
                if (m.Running)
                {
                    BringToFront(m);
                    return;
                }
                if (!File.Exists(m.Exe))
                {
                    m.ExeMissing = true;
                    AskBox.Show(this, "这个模组的 exe 不在了：\r\n\r\n" + m.Exe +
                        "\r\n\r\n可能被移动或删除。可以右键→引擎信息…附近菜单里重新添加，或删掉这个条目。",
                        "启动失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    list.Invalidate();
                    return;
                }

                // 启动前体检：只提醒，不阻止
                List<string> warns = new List<string>();
                bool ascii = true;
                foreach (char c in m.Exe) if (c > 127) { ascii = false; break; }
                if (!ascii) warns.Add("路径含中文/特殊字符，个别引擎会读不到资源");
                string exeDir = m.LaunchDir;
                if (!File.Exists(Path.Combine(exeDir, "lime.ndll")) &&
                    !Directory.Exists(Path.Combine(exeDir, "assets")) &&
                    !Directory.Exists(Path.Combine(exeDir, "mods")))
                    warns.Add("这个目录里没有 lime.ndll / assets / mods，可能不是完整的游戏目录");
                if (warns.Count > 0)
                {
                    Diag.Add("启动前提醒 " + m.Title + ": " + string.Join("; ", warns.ToArray()));
                    string msg = "启动前发现这些情况：\r\n\r\n· " + string.Join("\r\n· ", warns.ToArray()) +
                                 "\r\n\r\n仍然启动吗？";
                    if (AskBox.Show(this, msg, "启动前检查", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                        return;
                }

                ProcessStartInfo psi = new ProcessStartInfo(m.Exe);
                psi.WorkingDirectory = m.LaunchDir;
                if (m.Args != null && m.Args.Length > 0) psi.Arguments = m.Args;
                psi.UseShellExecute = true;
                Process p = Process.Start(psi);
                try { if (!string.IsNullOrEmpty(m.Exe)) launchedExes.Add(m.Exe); } catch { }   // 无论句柄是否为 null 都记录

                m.LastRun = DateTime.Now.Ticks;
                m.LaunchCount++;
                cfg.Set("recent." + m.Name, (long)m.LastRun);
                cfg.Set("count." + m.Name, m.LaunchCount);
                cfg.Set("last", m.Name);
                cfg.Save();

                if (p != null)
                {
                    MarkRunning(m, p);
                    try { runningGames.Add(p); } catch { }
                    try { if (!m.IsWeb && !string.IsNullOrEmpty(m.Exe)) launchedExes.Add(m.Exe); } catch { }

                    // 启动日志：先记一条"启动中"，进程退出后补退出码和耗时
                    LaunchRecord rec = LaunchLog.Begin(m.Title, m.EngineDisplay, m.Exe, m.Args);
                    WatchExit(m, p, rec);
                }
                ShowToast("已启动：" + m.Name + "（启动 " + m.LaunchCount + " 次）");
                list.Invalidate();
                detail.Touch();

                SaveBackup.Backup(m, 5);        // 启动前自动备份存档
                KeyStats.CurrentMod = m.Name;   // 之后按下的键都算到这个模组名下

                // 先把"正在启动"这一页放出来，等它演完再最小化，免得动画被吞掉
                overlay.Play(m, delegate
                {
                    if (minimizeOnLaunch) HideToTray();
                });
            }
            catch (Exception ex)
            {
                AskBox.Show(this, "启动失败：\r\n\r\n" + ex.Message, "FNF 模组启动器",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // 盯着进程退出：写日志、恢复窗口、异常时弹崩溃分析
        private void WatchExit(ModEntry m, Process p, LaunchRecord rec)
        {
            System.Threading.Thread t = new System.Threading.Thread(delegate()
            {
                long ms = 0;
                int code = -1;
                try
                {
                    Stopwatch sw = Stopwatch.StartNew();
                    p.WaitForExit();
                    ms = sw.ElapsedMilliseconds;
                    code = p.ExitCode;
                }
                catch { }

                try { LaunchLog.End(rec, code, ms, code == 0 ? "" : "异常退出"); }
                catch { }

                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        if (!Visible) RestoreFromTray();
                        if (list != null) list.Invalidate();
                        if (code != 0 && !m.IsWeb && ms < 300000)
                        {
                            try
                            {
                                List<string> hints = LaunchLog.Analyse(rec);
                                using (CrashForm cf = new CrashForm(rec, hints)) cf.ShowDialog(this);
                            }
                            catch { }
                        }
                    });
                }
                catch { }
            });
            t.IsBackground = true;
            t.Start();
        }

        // ---------------- 托盘
        private NotifyIcon tray;

        private void EnsureTray()
        {
            if (tray != null) return;
            tray = new NotifyIcon();
            try { tray.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            tray.Text = Lang.T("FNF 模组启动器");
            tray.Visible = false;
            tray.DoubleClick += delegate { RestoreFromTray(); };
            ContextMenuStrip cm = new ContextMenuStrip();
            cm.BackColor = Ui.Card;
            cm.ForeColor = Ui.Txt;
            cm.Font = Ui.FHint;
            cm.Items.Add("打开启动器", null, delegate { RestoreFromTray(); });
            cm.Items.Add("退出", null, delegate
            {
                if (tray != null) { tray.Visible = false; tray.Dispose(); tray = null; }
                Application.Exit();
            });
            tray.ContextMenuStrip = cm;
            Application.ApplicationExit += delegate
            {
                try { if (tray != null) { tray.Visible = false; tray.Dispose(); tray = null; } }
                catch { }
            };
        }

        private void HideToTray()
        {
            EnsureTray();
            tray.Visible = true;
            try { tray.ShowBalloonTip(1500, "FNF 模组启动器", "已隐藏到托盘，双击图标可恢复", ToolTipIcon.None); }
            catch { }
            Hide();
        }

        private void RestoreFromTray()
        {
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
            if (tray != null) tray.Visible = false;
        }

        private void KillGames()
        {
            List<Process> targets = new List<Process>();
            try
            {
                foreach (Process p in runningGames)
                {
                    try { if (p != null && !p.HasExited) targets.Add(p); }
                    catch { }
                }
            }
            catch { }

            // 兜底：按启动过的 exe 名字找进程（shell 启动时 Process 句柄可能是 null）
            foreach (string exe in launchedExes)
            {
                try
                {
                    string name = Path.GetFileNameWithoutExtension(exe);
                    if (name.Length == 0) continue;
                    foreach (Process p in Process.GetProcessesByName(name))
                    {
                        if (targets.Contains(p)) continue;
                        try { targets.Add(p); } catch { }
                    }
                }
                catch { }
            }

            foreach (Process p in targets)
            {
                try
                {
                    try { p.CloseMainWindow(); } catch { }
                    if (!p.WaitForExit(1200)) { try { p.Kill(); } catch { } }
                }
                catch { }
            }
            runningGames.Clear();
        }

        private void AskCloseThenAct()
        {
            if (askingClose) return;
            askingClose = true;
            lastCloseAskAt = Ui.Now;
            bool hide = false;
            bool killGame = cfg.Get("closeKill", "0") == "1";
            try
            {
                using (CloseChoiceForm f = new CloseChoiceForm())
                {
                    if (f.ShowDialog(this) != DialogResult.OK) { askingClose = false; return; }   // 没选就留在启动器
                    hide = f.HideTray;
                    killGame = f.KillGame;
                    if (f.Remember)
                    {
                        cfg.Set("closeAction", hide ? "hide" : "exit");
                        cfg.Set("closeKill", killGame ? "1" : "0");
                        cfg.Save();
                    }
                }
            }
            catch (Exception ex)
            {
                try
                {
                    File.WriteAllText(Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "launcher", "closelog.txt"), ex.ToString());
                }
                catch { }
                hide = false;
            }
            if (killGame) KillGames();
            askingClose = false;
            if (hide) { HideToTray(); return; }
            closingForReal = true;
            Close();
        }
        private void LaunchWeb(ModEntry m)
        {
            try
            {
                Process.Start(new ProcessStartInfo(m.Url) { UseShellExecute = true });
                m.LastRun = DateTime.Now.Ticks;
                m.LaunchCount++;
                cfg.Set("recent." + m.Name, (long)m.LastRun);
                cfg.Set("count." + m.Name, m.LaunchCount);
                cfg.Set("last", m.Name);
                cfg.Save();
                ShowToast("已用默认浏览器打开：" + m.Name);
                detail.Touch();
            }
            catch (Exception ex)
            {
                AskBox.Show(this, "打开网址失败：\r\n\r\n" + ex.Message, "FNF 模组启动器",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void BringToFront(ModEntry m)
        {
            Process p;
            if (!running.TryGetValue(m.Name, out p)) return;
            try
            {
                p.Refresh();
                IntPtr h = p.MainWindowHandle;
                if (h != IntPtr.Zero)
                {
                    ShowWindow(h, 9);          // SW_RESTORE
                    SetForegroundWindow(h);
                }
            }
            catch { }
        }

        private string lastOpenedUrl;
        private double lastOpenedAt = -10000;

        private void OpenUrl(string url)
        {
            // 同一个地址 800ms 内只开一次（按钮的 Click 可能被触发两次）
            if (string.IsNullOrEmpty(url)) return;
            if (url == lastOpenedUrl && Ui.Now - lastOpenedAt < 800) return;
            lastOpenedUrl = url; lastOpenedAt = Ui.Now;
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch { }
        }

        private void ShowToast(string text) { toast.Show(text); }

        private void OpenFolder(string p)
        {
            try
            {
                if (!string.IsNullOrEmpty(p) && Directory.Exists(p))
                    Ui.ShellOpen("explorer.exe", "\"" + p + "\"");
            }
            catch { }
        }

        // ---------------------------------------------------------------- 拖放
        private void DragEnterHandler(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy;
            else e.Effect = DragDropEffects.None;
        }

        private void DragDropHandler(object sender, DragEventArgs e)
        {
            string[] items = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (items == null || items.Length == 0) return;
            foreach (string path in items)
            {
                if (Directory.Exists(path)) AddDroppedFolder(path);
                else if (File.Exists(path))
                {
                    string ext = Path.GetExtension(path).ToLowerInvariant();
                    if (ext == ".zip" || ext == ".rar" || ext == ".7z")
                    {
                        string target;
                        if (ArchiveInstall.Prompt(this, path, root, out target) && target != null)
                        {
                            Rescan();
                            SelectByName(Path.GetFileName(target));
                            ShowToast("已安装：" + Path.GetFileName(target));
                        }
                        continue;
                    }
                    if (ext == ".tar" || ext == ".gz" || ext == ".iso" || ext == ".xz" || ext == ".bz2")
                    {
                        AskBox.Show(this, "压缩包格式不支持，仅支持 .zip / .rar / .7z。", "不支持的格式",
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        continue;
                    }
                    string dir = Path.GetDirectoryName(path);
                    try
                    {
                        if (dir != null && string.Equals(Path.GetFileNameWithoutExtension(path),
                            new DirectoryInfo(dir).Name, StringComparison.OrdinalIgnoreCase))
                            AddDroppedFolder(dir);
                    }
                    catch { }
                }
            }
        }

        private void AddDroppedFolder(string src)
        {
            string dir = src.TrimEnd('\\');
            DirectoryInfo di;
            try { di = new DirectoryInfo(dir); }
            catch { return; }
            if (!di.Exists) return;

            if (SamePath(dir, root) || IsUnder(dir, root))
            {
                Rescan();
                SelectByName(di.Name);
                ShowToast("已在识别目录里：" + di.Name);
                return;
            }

            DialogResult r = AskBox.Show(this, 
                Lang.T("把「") + di.Name + Lang.T("」添加为模组？") + "\r\n\r\n" +
                Lang.T("是：复制到 ") + root + "\r\n" +
                Lang.T("否：只添加引用（文件保持原位）") + "\r\n" +
                Lang.T("取消：什么都不做"),
                "添加模组", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);

            if (r == DialogResult.Cancel) return;

            if (r == DialogResult.Yes)
            {
                string dest = Path.Combine(root, di.Name);
                if (Directory.Exists(dest))
                {
                    AskBox.Show(this, "目标文件夹已存在：\r\n" + dest, "添加模组",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                try
                {
                    Cursor = Cursors.WaitCursor;
                    CopyDir(dir, dest);
                    Cursor = Cursors.Default;
                    Rescan();
                    SelectByName(di.Name);
                    ShowToast("已复制并添加：" + di.Name);
                }
                catch (Exception ex)
                {
                    Cursor = Cursors.Default;
                    AskBox.Show(this, "复制失败：\r\n" + ex.Message, "添加模组",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
            else
            {
                cfg.Set("ref." + dir, true);
                cfg.Save();
                Rescan();
                SelectByName(di.Name);
                ShowToast("已添加引用：" + di.Name);
            }
        }

        private static void CopyDir(string src, string dst)
        {
            Directory.CreateDirectory(dst);
            foreach (string f in Directory.GetFiles(src))
                File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), false);
            foreach (string d in Directory.GetDirectories(src))
                CopyDir(d, Path.Combine(dst, Path.GetFileName(d)));
        }

        private static bool SamePath(string a, string b)
        {
            try
            {
                return string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private static bool IsUnder(string path, string root)
        {
            try
            {
                string p = Path.GetFullPath(path).TrimEnd('\\') + "\\";
                string r = Path.GetFullPath(root).TrimEnd('\\') + "\\";
                return p.StartsWith(r, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        // ---------------------------------------------------------------- 键盘
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);

            // 界面缩放：+ 放大  - 缩小  Ctrl+0 复位（搜索框里也生效）
            if (e.KeyCode == Keys.Oemplus || e.KeyCode == Keys.Add)
            {
                ZoomStep(0.1f);
                e.Handled = true; e.SuppressKeyPress = true; return;
            }
            if (e.KeyCode == Keys.OemMinus || e.KeyCode == Keys.Subtract)
            {
                ZoomStep(-0.1f);
                e.Handled = true; e.SuppressKeyPress = true; return;
            }
            if (e.Control && (e.KeyCode == Keys.D0 || e.KeyCode == Keys.NumPad0))
            {
                ZoomStep(0f);
                e.Handled = true; e.SuppressKeyPress = true; return;
            }

            if (e.KeyCode == Keys.F5) { RescanAsync(); e.Handled = true; e.SuppressKeyPress = true; return; }
            if (e.Control && e.KeyCode == Keys.F) { search.Box.Focus(); search.Box.SelectAll(); e.Handled = true; return; }

            if (ActiveControl == search || (search.Box != null && search.Box.Focused))
            {
                if (e.KeyCode == Keys.Escape) { ActiveControl = list; e.Handled = true; }
                return;
            }

            if (e.Control && e.KeyCode >= Keys.D1 && e.KeyCode <= Keys.D9)
            {
                int idx = (int)(e.KeyCode - Keys.D1);
                if (idx < list.Items.Count) Launch(list.Items[idx]);
                e.Handled = true;
                e.SuppressKeyPress = true;
                return;
            }

            if (e.KeyCode == Keys.Enter)
            {
                if (list.SelectedMod != null) Launch(list.SelectedMod);
                e.Handled = true; e.SuppressKeyPress = true;
            }
            else if (e.KeyCode == Keys.Down) { list.MoveSelection(1); e.Handled = true; e.SuppressKeyPress = true; }
            else if (e.KeyCode == Keys.Up) { list.MoveSelection(-1); e.Handled = true; e.SuppressKeyPress = true; }
            else if (e.KeyCode == Keys.Home) { list.SelectIndex(0, true); e.Handled = true; }
            else if (e.KeyCode == Keys.End) { list.SelectIndex(list.Items.Count - 1, true); e.Handled = true; }
        }

        // 界面缩放：原地重排尺寸 + 重建字体，不重启、不闪
        private void ZoomStep(float delta)
        {
            float z = delta == 0f ? 1f : Ui.Zoom + delta;
            z = (float)Math.Round(z * 100f) / 100f;
            if (z < 0.7f) z = 0.7f;
            if (z > 1.7f) z = 1.7f;
            if (Math.Abs(z - Ui.Zoom) < 0.001f)
            {
                ShowToast("已经是 " + (int)Math.Round(z * 100) + "% 了");
                return;
            }

            float ratio = z / Ui.Zoom;
            SuspendLayout();
            try
            {
                Ui.Zoom = z;
                Ui.InitFonts();
                TextCache.Clear();

                // 缩略图按新尺寸重建（否则会被拉糊）
                foreach (ModEntry m in all)
                {
                    if (m.Thumb != null) { m.Thumb.Dispose(); m.Thumb = null; }
                    if (m.Big != null) { m.Big.Dispose(); m.Big = null; }
                    if (m.IsWeb)
                        m.Thumb = (m.WebIconPath.Length > 0 && File.Exists(m.WebIconPath))
                            ? Icons.FromFile(m.WebIconPath, Ui.S(46), m.Title)
                            : Icons.Globe(Ui.S(46));
                    else
                        m.Thumb = Icons.Build(m.IconPath, m.Exe, Ui.S(46), m.Title);
                }

            MinimumSize = new Size(Ui.S(900), Ui.S(560));
                header.Height = Ui.S(88);
                footer.Height = Ui.S(30);
                search.Size = new Size(Ui.S(190), Ui.S(26));
                search.Font = Ui.FHint;
                btnRefresh.Size = new Size(Ui.S(60), Ui.S(30));
                btnOpen.Size = new Size(Ui.S(82), Ui.S(30));
                btnDl.Size = new Size(Ui.S(74), Ui.S(30));
                btnCat.Size = new Size(Ui.S(60), Ui.S(30));
                btnSettings.Size = new Size(Ui.S(60), Ui.S(30));
                foreach (RoundButton b in new RoundButton[] { btnRefresh, btnOpen, btnDl, btnCat, btnSettings })
                {
                    b.Font = Ui.FBtn;
                    b.Invalidate();
                }
                toast.Size = new Size(Ui.S(320), Ui.S(64));
                ClientSize = new Size(Math.Max(Ui.S(700), (int)(ClientSize.Width * ratio)),
                                      Math.Max(Ui.S(500), (int)(ClientSize.Height * ratio)));
                list.RefreshMetrics();
                LayoutHeader();
                LayoutSplit();
                PositionToast();
            }
            finally { ResumeLayout(true); }

            list.Invalidate();
            detail.Touch();
            header.Invalidate();
            footer.Invalidate();
            cfg.Set("zoom", (double)z);
            cfg.Save();
            ShowToast("界面 " + (int)Math.Round(z * 100) + "%");
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            if (detail != null && detail.Bounds.Contains(e.X, e.Y)) detail.ScrollBy(e.Delta);
            else if (list != null) list.WheelScroll(e.Delta);
            base.OnMouseWheel(e);
        }

        // ---------------------------------------------------------------- 关闭
        private bool closingForReal;
        private bool askingClose;
        private double lastCloseAskAt = -10000;
        private readonly List<Process> runningGames = new List<Process>();
        private readonly List<string> launchedExes = new List<string>();

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // 关闭确认：先把关闭拦下来再弹窗（在 FormClosing 里直接 ShowDialog 会被连带关掉）
            if (!closingForReal && !askingClose && e.CloseReason == CloseReason.UserClosing)
            {
                string act = cfg.Get("closeAction", "ask");
                if (act == "hide")
                {
                    e.Cancel = true;
                    HideToTray();
                    return;
                }
                if (act != "exit")
                {
                    e.Cancel = true;
                    BeginInvoke((MethodInvoker)delegate { AskCloseThenAct(); });
                    return;
                }
            }
            try
            {
                Rectangle b = (WindowState == FormWindowState.Normal) ? Bounds : RestoreBounds;
                if (WindowState == FormWindowState.Minimized) b = RestoreBounds;
                cfg.Set("window", b.X + "," + b.Y + "," + ClientSize.Width + "," + ClientSize.Height);
                if (keyStatsOn) KeyStats.Save(cfg);
                if (list.SelectedMod != null) cfg.Set("last", list.SelectedMod.Name);
                cfg.Save();
            }
            catch { }
            KeyStats.Stop();
            try { detail.StopAudio(); } catch { }
            foreach (ModEntry m in all) m.Dispose();
            base.OnFormClosing(e);
        }
    }

    // ===================================================================== 入口
    
    internal static class Program
    {
        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindow(string cls, string title);
        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr h);

        private static System.Threading.Mutex singleMutex;

        [STAThread]
        private static void Main(string[] args)
        {
            try
            {
                // 单实例：已经有启动器在跑就把那个窗口唤到前面，自己退出
                bool created;
#if TESTBUILD
                // 仅本地调试用：测试版用独立互斥体，可以和正式版同时开着
                singleMutex = new System.Threading.Mutex(true, "FnfLauncher_SingleInstance_TESTBUILD", out created);
#else
                singleMutex = new System.Threading.Mutex(true, "FnfLauncher_SingleInstance_v1", out created);
#endif
                if (!created)
                {
                    IntPtr exist = FindWindow(null, "FNF 模组启动器");
                if (exist == IntPtr.Zero) exist = FindWindow(null, "FNF Mod Launcher");
                    if (exist != IntPtr.Zero)
                    {
                        ShowWindow(exist, 9);          // SW_RESTORE
                        SetForegroundWindow(exist);
                    }
                    return;
                }
                try { SetProcessDPIAware(); } catch { }
                try
                {
                    using (Graphics g = Graphics.FromHwnd(IntPtr.Zero))
                        Ui.Scale = Math.Max(1f, g.DpiX / 96f);
                }
                catch { Ui.Scale = 1f; }

                try
                {
                    string ini = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "launcher.ini");
                    double z = new Config(ini).GetDouble("zoom", 1.0);
                    if (z < 0.7) z = 0.7;
                    if (z > 1.7) z = 1.7;
                    Ui.Zoom = (float)z;
                }
                catch { Ui.Zoom = 1f; }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Ui.InitFonts();

                string root = (args != null && args.Length > 0) ? args[0] : null;
                SplashForm splash = new SplashForm();
                splash.Show();
                splash.Refresh();
                SplashForm.Current = splash;
                MainForm main = new MainForm(root);
                main.Opacity = 0;
                Application.Run(main);
            }
            catch (Exception ex)
            {
                MessageBox.Show("启动器出错：\r\n\r\n" + ex, "FNF 模组启动器",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}
