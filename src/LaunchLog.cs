// 启动日志 + 崩溃分析（第四轮新增，独立文件，不动原有启动逻辑）
// 记录方式：每次启动在 exe 同目录的「启动日志.txt」里追加一行，进程退出时补上退出码和耗时。
// 一行 = 一条记录，用 \t 分隔：开始时间 \t 模组 \t 引擎 \t exe \t 参数 \t 退出码 \t 耗时(ms) \t 备注

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace FnfLauncher
{
    internal class LaunchRecord
    {
        public DateTime Start;
        public string Mod = "";
        public string Engine = "";
        public string Exe = "";
        public string Args = "";
        public int ExitCode = -999;      // -999 = 还没结束
        public long Ms;
        public string Note = "";

        public bool Finished { get { return ExitCode != -999; } }
        public bool Crashed { get { return Finished && ExitCode != 0; } }
    }

    internal static class LaunchLog
    {
        public static string Path
        {
            get
            {
                string dir;
                try { dir = System.IO.Path.GetDirectoryName(Application.ExecutablePath); }
                catch { dir = Environment.CurrentDirectory; }
                return System.IO.Path.Combine(dir, "启动日志.txt");
            }
        }

        public static List<LaunchRecord> Load()
        {
            List<LaunchRecord> list = new List<LaunchRecord>();
            try
            {
                if (!File.Exists(Path)) return list;
                foreach (string line in File.ReadAllLines(Path, Encoding.UTF8))
                {
                    if (line.Trim().Length == 0) continue;
                    string[] f = line.Split('\t');
                    if (f.Length < 7) continue;
                    LaunchRecord r = new LaunchRecord();
                    DateTime t;
                    if (DateTime.TryParse(f[0], out t)) r.Start = t; else r.Start = DateTime.MinValue;
                    r.Mod = f[1];
                    r.Engine = f[2];
                    r.Exe = f[3];
                    r.Args = f[4];
                    int.TryParse(f[5], out r.ExitCode);
                    long.TryParse(f[6], out r.Ms);
                    if (f.Length > 7) r.Note = f[7];
                    list.Add(r);
                }
            }
            catch { }
            return list;
        }

        private static void Append(LaunchRecord r)
        {
            try
            {
                string line = string.Join("\t", new string[] {
                    r.Start.ToString("yyyy-MM-dd HH:mm:ss"),
                    r.Mod, r.Engine, r.Exe, r.Args ?? "",
                    r.ExitCode.ToString(), r.Ms.ToString(), (r.Note ?? "").Replace("\t", " ").Replace("\r", " ").Replace("\n", " ")
                });
                File.AppendAllText(Path, line + "\r\n", Encoding.UTF8);
            }
            catch { }
        }

        /// <summary>开始记录：写入"启动中"，进程退出后由 End 补全。</summary>
        public static LaunchRecord Begin(string mod, string engine, string exe, string args)
        {
            LaunchRecord r = new LaunchRecord();
            r.Start = DateTime.Now;
            r.Mod = mod ?? "";
            r.Engine = engine ?? "";
            r.Exe = exe ?? "";
            r.Args = args ?? "";
            r.ExitCode = -999;
            r.Note = "启动中";
            return r;
        }

        /// <summary>结束记录：把最后一行替换成带退出码和耗时的版本。</summary>
        public static void End(LaunchRecord r, int exitCode, long ms, string note)
        {
            r.ExitCode = exitCode;
            r.Ms = ms;
            r.Note = note ?? "";
            try
            {
                // 先追加完整记录，再把之前那条"启动中"删掉，避免两端不一致
                Append(r);
                RemovePending(r);
            }
            catch { }
        }

        private static void RemovePending(LaunchRecord r)
        {
            try
            {
                if (!File.Exists(Path)) return;
                string[] lines = File.ReadAllLines(Path, Encoding.UTF8);
                List<string> keep = new List<string>();
                string pending = r.Start.ToString("yyyy-MM-dd HH:mm:ss") + "\t" + r.Mod + "\t" + r.Engine + "\t" + r.Exe + "\t" + (r.Args ?? "") + "\t-999\t0\t启动中";
                bool removed = false;
                for (int i = lines.Length - 1; i >= 0; i--)
                {
                    if (!removed && lines[i] == pending) { removed = true; continue; }
                    keep.Insert(0, lines[i]);
                }
                if (removed) File.WriteAllLines(Path, keep.ToArray(), Encoding.UTF8);
            }
            catch { }
        }

        /// <summary>崩溃/异常退出时给点人话：把能看出来的原因列出来。</summary>
        public static List<string> Analyse(LaunchRecord r)
        {
            List<string> hints = new List<string>();
            if (!r.Finished) { hints.Add("游戏还在运行，或者进程信息拿不到。"); return hints; }

            if (r.ExitCode == 0) { hints.Add("进程正常退出（退出码 0）。"); return hints; }

            if (r.ExitCode == -1073741515) hints.Add("退出码 0xC0000135：缺少 DLL（多半是没装 Visual C++ 运行库，或者缺 dll 文件）。");
            else if (r.ExitCode == -1073741819) hints.Add("退出码 0xC0000005：访问冲突（内存越界），常见于 Mod 代码写崩、或显卡驱动/OpenGL 问题。");
            else if (r.ExitCode == -1073741571) hints.Add("退出码 0xC00000FD：栈溢出，常见于 Mod 递归调用出错。");
            else if (r.ExitCode == -1073740791) hints.Add("退出码 0xC0000409：程序主动崩溃（可能是脚本报错后 kill 了自己）。");
            else hints.Add("退出码 " + r.ExitCode + "（0x" + (r.ExitCode & 0xFFFFFFFF).ToString("X8") + "）：不是正常退出。");

            try
            {
                string dir = System.IO.Path.GetDirectoryName(r.Exe);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                {
                    bool ascii = true;
                    foreach (char c in r.Exe) if (c > 127) { ascii = false; break; }
                    if (!ascii) hints.Add("路径里有中文/特殊字符，个别引擎会读不到资源，建议放到纯英文目录。");

                    bool hasLime = File.Exists(System.IO.Path.Combine(dir, "lime.ndll"));
                    bool hasAssets = Directory.Exists(System.IO.Path.Combine(dir, "assets"));
                    bool hasMods = Directory.Exists(System.IO.Path.Combine(dir, "mods"));
                    if (!hasLime && !hasAssets && !hasMods)
                        hints.Add("这个目录里没有 lime.ndll / assets / mods，可能不是完整的游戏目录。");

                    string[] logs = Directory.GetFiles(dir, "*.log");
                    string[] logDir = Directory.Exists(System.IO.Path.Combine(dir, "logs"))
                        ? Directory.GetFiles(System.IO.Path.Combine(dir, "logs"), "*.log") : new string[0];
                    if (logs.Length == 0 && logDir.Length == 0)
                        hints.Add("目录里没有找到 .log，引擎可能在报错前就退出了（也有可能是缺文件）。");
                }
            }
            catch { }

            if (r.Ms >= 0 && r.Ms < 4000) hints.Add("只跑了 " + (r.Ms / 1000.0).ToString("0.0") + " 秒就退出，通常是启动阶段就失败了（缺文件/缺运行库）。");
            else if (r.Ms > 0) hints.Add("跑了约 " + (r.Ms / 1000 / 60) + " 分钟才退出，更像游戏中途出错。");

            hints.Add("可以先用「查看日志」看看最后几行，或者把日志发到群里让人帮忙看。");
            return hints;
        }
    }

    // ===================================================================== 崩溃提示窗
    internal class CrashForm : Form
    {
        public CrashForm(LaunchRecord r, List<string> hints)
        {
            Text = "启动异常：" + r.Mod;
            BackColor = Ui.Bg;
            ForeColor = Ui.Txt;
            Font = Ui.FHint;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(Ui.S(560), Ui.S(430));
            ShowInTaskbar = false;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            Ui.DarkTitleBar(Handle);

            Label title = new Label();
            title.Text = "这次启动没有正常结束";
            title.Font = new Font("Microsoft YaHei UI", 14f, FontStyle.Bold);
            title.ForeColor = Ui.Accent;
            title.SetBounds(Ui.S(22), Ui.S(16), Ui.S(500), Ui.S(28));
            Controls.Add(title);

            Label sub = new Label();
            sub.Text = r.Mod + "   ·   " + (r.Engine.Length > 0 ? r.Engine : "未知引擎");
            sub.ForeColor = Ui.Sub;
            sub.SetBounds(Ui.S(22), Ui.S(46), Ui.S(500), Ui.S(20));
            Controls.Add(sub);

            int y = Ui.S(78);
            foreach (string h in hints)
            {
                Label l = new Label();
                l.Text = "·  " + h;
                l.ForeColor = Ui.Txt;
                l.SetBounds(Ui.S(24), y, Ui.S(500), Ui.S(20));
                Controls.Add(l);
                y += Ui.S(22);
            }

            Label meta = new Label();
            meta.Text = "退出码 " + r.ExitCode + "（0x" + (r.ExitCode & 0xFFFFFFFF).ToString("X8") + "）   ·   运行 " +
                        (r.Ms / 1000.0).ToString("0.0") + " 秒   ·   " + r.Start.ToString("MM-dd HH:mm:ss");
            meta.ForeColor = Ui.Sub;
            meta.SetBounds(Ui.S(24), Ui.S(352), Ui.S(500), Ui.S(20));
            Controls.Add(meta);

            int bx = Ui.S(22), by = Ui.S(384);
            RoundButton openLog = new RoundButton("查看日志");
            openLog.Size = new Size(Ui.S(96), Ui.S(32));
            openLog.Location = new Point(bx, by);
            openLog.Click += delegate { try { Process.Start("notepad.exe", "\"" + LaunchLog.Path + "\""); } catch { } };
            Controls.Add(openLog);

            RoundButton openDir = new RoundButton("打开文件夹");
            openDir.Filled = false;
            openDir.Size = new Size(Ui.S(110), Ui.S(32));
            openDir.Location = new Point(bx + Ui.S(106), by);
            openDir.Click += delegate
            {
                try
                {
                    string dir = System.IO.Path.GetDirectoryName(r.Exe);
                    if (Directory.Exists(dir)) Ui.ShellOpen("explorer.exe", "\"" + dir + "\"");
                }
                catch { }
            };
            Controls.Add(openDir);

            RoundButton copy = new RoundButton("复制信息");
            copy.Filled = false;
            copy.Size = new Size(Ui.S(96), Ui.S(32));
            copy.Location = new Point(bx + Ui.S(226), by);
            copy.Click += delegate
            {
                try { Clipboard.SetText(BuildText(r, hints)); } catch { }
            };
            Controls.Add(copy);

            RoundButton close = new RoundButton("关闭");
            close.Filled = false;
            close.Size = new Size(Ui.S(80), Ui.S(32));
            close.Location = new Point(ClientSize.Width - Ui.S(22) - Ui.S(80), by);
            close.Click += delegate { Close(); };
            Controls.Add(close);
        }

        public static string BuildText(LaunchRecord r, List<string> hints)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("模组：" + r.Mod);
            sb.AppendLine("引擎：" + r.Engine);
            sb.AppendLine("exe：" + r.Exe);
            if (r.Args.Length > 0) sb.AppendLine("参数：" + r.Args);
            sb.AppendLine("开始：" + r.Start.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("退出码：" + r.ExitCode + "（0x" + (r.ExitCode & 0xFFFFFFFF).ToString("X8") + "）");
            sb.AppendLine("耗时：" + (r.Ms / 1000.0).ToString("0.0") + " 秒");
            sb.AppendLine();
            foreach (string h in hints) sb.AppendLine("· " + h);
            return sb.ToString();
        }
    }

    // ===================================================================== 启动日志窗
    internal class LaunchLogForm : Form
    {
        public LaunchLogForm()
        {
            Text = "启动日志";
            BackColor = Ui.Bg;
            ForeColor = Ui.Txt;
            Font = Ui.FHint;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(Ui.S(760), Ui.S(520));
            MinimizeBox = false;
            ShowInTaskbar = false;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            Ui.DarkTitleBar(Handle);

            List<LaunchRecord> recs = LaunchLog.Load();
            recs.Reverse();      // 最近的放最上面

            TextBox box = new TextBox();
            box.Multiline = true;
            box.ReadOnly = true;
            box.ScrollBars = ScrollBars.Vertical;
            box.BackColor = Ui.Card;
            box.ForeColor = Ui.Txt;
            box.BorderStyle = BorderStyle.None;
            box.Font = new Font("Consolas", 9.75f);
            box.SetBounds(Ui.S(16), Ui.S(16), ClientSize.Width - Ui.S(32), ClientSize.Height - Ui.S(74));
            box.Text = Format(recs);
            box.Select(0, 0);
            box.TabStop = false;
            Controls.Add(box);

            int by = ClientSize.Height - Ui.S(48);
            RoundButton copy = new RoundButton("复制全部");
            copy.Filled = false;
            copy.Size = new Size(Ui.S(96), Ui.S(32));
            copy.Location = new Point(Ui.S(16), by);
            copy.Click += delegate { try { Clipboard.SetText(box.Text); } catch { } };
            Controls.Add(copy);

            RoundButton clear = new RoundButton("清空日志");
            clear.Filled = false;
            clear.Size = new Size(Ui.S(96), Ui.S(32));
            clear.Location = new Point(Ui.S(120), by);
            clear.Click += delegate
            {
                if (MessageBox.Show(this, Lang.T("清空启动日志？"), Lang.T("启动日志"), MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                try { if (File.Exists(LaunchLog.Path)) File.Delete(LaunchLog.Path); } catch { }
                box.Text = "";
            };
            Controls.Add(clear);

            RoundButton open = new RoundButton("用记事本打开");
            open.Filled = false;
            open.Size = new Size(Ui.S(120), Ui.S(32));
            open.Location = new Point(Ui.S(224), by);
            open.Click += delegate { try { Process.Start("notepad.exe", "\"" + LaunchLog.Path + "\""); } catch { } };
            Controls.Add(open);

            RoundButton close = new RoundButton("关闭");
            close.Size = new Size(Ui.S(80), Ui.S(32));
            close.Location = new Point(ClientSize.Width - Ui.S(16) - Ui.S(80), by);
            close.Click += delegate { Close(); };
            Controls.Add(close);
        }

        private static string Format(List<LaunchRecord> recs)
        {
            if (recs.Count == 0) return "还没有启动记录。\r\n\r\n每次点「启动游戏」，这里都会记一条：什么时候、哪个模组、退出码、跑了多久。";
            StringBuilder sb = new StringBuilder();
            foreach (LaunchRecord r in recs)
            {
                string flag = !r.Finished ? "启动中" : (r.ExitCode == 0 ? "正常" : "异常");
                sb.Append(r.Start.ToString("MM-dd HH:mm:ss")).Append("  [").Append(flag).Append("]  ");
                sb.Append(r.Mod);
                if (r.Engine.Length > 0) sb.Append("  ·  ").Append(r.Engine);
                if (r.Finished) sb.Append("  ·  退出码 ").Append(r.ExitCode).Append("  ·  ").Append((r.Ms / 1000.0).ToString("0.0")).Append(" 秒");
                sb.AppendLine();
                if (r.Args.Length > 0) sb.AppendLine("        参数：" + r.Args);
            }
            return sb.ToString();
        }
    }

    // ===================================================================== 外观（背景图 / 透明度）
    // 背景浓淡滑杆：0=只剩纯色底，220=几乎看不到背景图
    internal class AlphaSlider : Control
    {
        public event EventHandler ValueChanged;
        private int val = 110;
        public int Value { get { return val; } set { val = Math.Max(0, Math.Min(220, value)); Invalidate(); } }

        public AlphaSlider()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Ui.Bg;
            Cursor = Cursors.Hand;
        }

        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); SetFrom(e.X); }
        protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); if (e.Button == MouseButtons.Left) SetFrom(e.X); }

        private void SetFrom(int x)
        {
            int pad = Ui.S(9);
            int w = Math.Max(1, Width - pad * 2);
            int v = (int)Math.Round((x - pad) / (double)w * 220);
            v = Math.Max(0, Math.Min(220, v));
            if (v == val) return;
            val = v;
            Invalidate();
            if (ValueChanged != null) ValueChanged(this, EventArgs.Empty);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BackColor);
            int pad = Ui.S(9);
            int trackH = Ui.S(8);
            int top = (Height - trackH) / 2;
            int w = Math.Max(1, Width - pad * 2);
            Rectangle track = new Rectangle(pad, top, w, trackH);
            Ui.FillRound(g, track, trackH / 2, Ui.A(Color.White, 24));
            int fw = (int)(w * (val / 220.0));
            if (fw > 1) Ui.FillRound(g, new Rectangle(pad, top, fw, trackH), trackH / 2, Ui.A(Ui.Accent, 210));
            int kx = pad + fw;
            Ui.FillRound(g, new Rectangle(kx - Ui.S(7), top - Ui.S(4), Ui.S(14), trackH + Ui.S(8)), Ui.S(7), Color.White);
            string txt = "浓淡 " + (int)(val / 220.0 * 100) + "%";
            Ui.Text(g, txt, Ui.FSmall, new Rectangle(pad, 0, w, top - Ui.S(2)), Ui.Sub,
                TextFormatFlags.Left | TextFormatFlags.Bottom);
        }
    }

    internal class AppearanceForm : Form
    {
        private readonly Action<string, int> apply;
        private string path;
        private int alpha;
        private readonly Label hint;
        private readonly List<RoundButton> alphaBtns = new List<RoundButton>();

        public AppearanceForm(string bgPath, int bgAlpha, Action<string, int> onApply)
        {
            path = bgPath ?? "";
            alpha = bgAlpha;
            apply = onApply;

            Text = "外观";
            BackColor = Ui.Bg;
            ForeColor = Ui.Txt;
            Font = Ui.FHint;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(Ui.S(520), Ui.S(300));
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            Ui.DarkTitleBar(Handle);

            Label t = new Label();
            t.AutoSize = false;
            t.BackColor = Ui.Bg;
            t.ForeColor = Ui.Txt;
            t.Font = new Font("Microsoft YaHei UI", 12f, FontStyle.Bold);
            t.TextAlign = ContentAlignment.MiddleLeft;
            t.Text = "背景图";
            t.SetBounds(Ui.S(20), Ui.S(14), Ui.S(200), Ui.S(26));
            Controls.Add(t);

            hint = new Label();
            hint.AutoSize = false;
            hint.BackColor = Ui.Bg;
            hint.ForeColor = Ui.Sub;
            hint.Font = Ui.FSmall;
            hint.TextAlign = ContentAlignment.MiddleLeft;
            hint.SetBounds(Ui.S(20), Ui.S(44), Ui.S(480), Ui.S(20));
            Controls.Add(hint);

            RoundButton pick = new RoundButton("选择图片…");
            pick.Filled = false;
            pick.SetBounds(Ui.S(20), Ui.S(74), Ui.S(120), Ui.S(34));
            pick.Click += delegate { Pick(); };
            Controls.Add(pick);

            RoundButton clear = new RoundButton("清除背景");
            clear.Filled = false;
            clear.SetBounds(Ui.S(150), Ui.S(74), Ui.S(110), Ui.S(34));
            clear.Click += delegate
            {
                path = "";
                Ui.BackImage = null;
                Apply();
                RefreshHint();
            };
            Controls.Add(clear);

            Label t2 = new Label();
            t2.AutoSize = false;
            t2.BackColor = Ui.Bg;
            t2.ForeColor = Ui.Txt;
            t2.Font = new Font("Microsoft YaHei UI", 12f, FontStyle.Bold);
            t2.TextAlign = ContentAlignment.MiddleLeft;
            t2.Text = "背景浓淡";
            t2.SetBounds(Ui.S(20), Ui.S(128), Ui.S(200), Ui.S(26));
            Controls.Add(t2);

            AlphaSlider sl = new AlphaSlider();
            sl.Value = Math.Max(0, Math.Min(220, alpha));
            sl.SetBounds(Ui.S(20), Ui.S(158), ClientSize.Width - Ui.S(40), Ui.S(40));
            sl.ValueChanged += delegate
            {
                alpha = sl.Value;
                Ui.BackAlpha = alpha;
                Apply();
                RefreshAlphaBtns();
            };
            Controls.Add(sl);

            RoundButton close = new RoundButton("关闭");
            close.SetBounds(ClientSize.Width - Ui.S(20) - Ui.S(90), ClientSize.Height - Ui.S(48), Ui.S(90), Ui.S(34));
            close.Click += delegate { Close(); };
            Controls.Add(close);

            RefreshHint();
            RefreshAlphaBtns();
        }

        private void Pick()
        {
            using (OpenFileDialog fd = new OpenFileDialog())
            {
                fd.Filter = "图片|*.png;*.jpg;*.jpeg;*.bmp;*.gif|所有文件|*.*";
                fd.Title = "选一张背景图";
                if (fd.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    using (FileStream fs = new FileStream(fd.FileName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        Ui.BackImage = Image.FromStream(fs);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "这张图读不出来：" + ex.Message, "外观", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                path = fd.FileName;
                Apply();
                RefreshHint();
            }
        }

        private void Apply()
        {
            if (apply != null) apply(path, alpha);
        }

        private void RefreshHint()
        {
            hint.Text = path.Length == 0 ? "还没选背景图（默认纯色底）" : path;
        }

        private void RefreshAlphaBtns()
        {
            int[] vals = { 70, 120, 185 };
            for (int i = 0; i < alphaBtns.Count; i++)
            {
                alphaBtns[i].Filled = (vals[i] == alpha);
                alphaBtns[i].Invalidate();
            }
        }
    }

    // ===================================================================== 安装进度窗
    internal class ProgressForm : Form
    {
        private readonly string titleText;
        private string detail = "准备中…";
        private float pct;
        private bool finished;
        private bool ok;
        private readonly Timer pulse;
        public bool Cancelled;
        public static ProgressForm Current;
        public static bool IsCancelled { get { return Current != null && Current.Cancelled; } }

        public ProgressForm(string title)
        {
            titleText = title;
            Text = title;
            BackColor = Ui.Bg;
            ForeColor = Ui.Txt;
            Font = Ui.FHint;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            ClientSize = new Size(Ui.S(440), Ui.S(150));
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            RoundButton cancel = new RoundButton("取消安装");
            cancel.Filled = false;
            cancel.SetBounds(ClientSize.Width - Ui.S(18) - Ui.S(92), ClientSize.Height - Ui.S(44), Ui.S(92), Ui.S(28));
            cancel.Click += delegate { Cancelled = true; pulse.Stop(); Close(); };
            Controls.Add(cancel);

            Current = this;
            pulse = new Timer();
            pulse.Interval = 40;
            pulse.Tick += delegate { Invalidate(); };
            pulse.Start();

            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        public bool Success { get { return ok; } }

        public void Finish(bool success)
        {
            try
            {
                BeginInvoke((MethodInvoker)delegate
                {
                    ok = success;
                    finished = true;
                    pulse.Stop();
                    Close();
                });
            }
            catch { }
        }

        public void Report(int done, int total, string text)
        {
            try
            {
                BeginInvoke((MethodInvoker)delegate
                {
                    if (total > 0) pct = Math.Max(0f, Math.Min(1f, done / (float)total));
                    if (!string.IsNullOrEmpty(text)) detail = text + "  " + done + " / " + total;
                    Invalidate();
                });
            }
            catch { }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Ui.Bg);

            Rectangle r = new Rectangle(1, 1, Width - 3, Height - 3);
            Ui.FillRound(g, r, Ui.S(10), Ui.Card);
            Ui.StrokeRound(g, r, Ui.S(10), Ui.Line, 1f);

            Ui.Text(g, titleText, Ui.FBig, new Rectangle(Ui.S(18), Ui.S(12), Width - Ui.S(36), Ui.S(24)),
                Ui.Txt, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);

            int by = Ui.S(62);
            Rectangle track = new Rectangle(Ui.S(18), by, Width - Ui.S(36), Ui.S(6));
            Ui.FillRound(g, track, Ui.S(3), Ui.A(Ui.Line, 180));
            int w = (int)(track.Width * pct);
            if (w > 2) Ui.FillRound(g, new Rectangle(track.X, track.Y, w, track.Height), Ui.S(3), Ui.Accent);

            string line = finished ? "完成" : detail;
            Ui.Text(g, line, Ui.FSmall, new Rectangle(Ui.S(18), by + Ui.S(12), Width - Ui.S(36), Ui.S(20)),
                Ui.Sub, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            Ui.Text(g, ((int)(pct * 100)) + "%", Ui.FSmall, new Rectangle(Ui.S(18), by + Ui.S(12), Width - Ui.S(36), Ui.S(20)),
                Ui.A(Ui.Accent, 235), TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            pulse.Stop();
            pulse.Dispose();
            base.OnFormClosing(e);
        }
    }

    // ===================================================================== 启动画面（先图标，再出主窗口）
    internal class SplashForm : Form
    {
        public static SplashForm Current;
        private readonly Timer tick;
        private readonly double at0;
        private readonly Bitmap logo;

        public SplashForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            ShowInTaskbar = false;
            BackColor = Ui.Bg;
            ClientSize = new Size(Ui.S(230), Ui.S(150));
            DoubleBuffered = true;
            try { logo = Icon.ExtractAssociatedIcon(Application.ExecutablePath).ToBitmap(); } catch { }

            at0 = Ui.Now;
            tick = new Timer();
            tick.Interval = 20;
            tick.Tick += delegate { Invalidate(); };
            tick.Start();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Ui.Bg);

            float k = Ui.Clamp01((float)((Ui.Now - at0) / 420.0));
            float ease = Ui.EaseOut(k);
            int w = Width, h = Height;

            if (logo != null)
            {
                int s = (int)(Ui.S(56) * (0.88f + 0.12f * ease));
                g.DrawImage(logo, new Rectangle((w - s) / 2, (int)(Ui.S(26) + (1 - ease) * Ui.S(6)), s, s));
            }
            Ui.Text(g, "FNF 模组启动器", Ui.FBig,
                new Rectangle(0, Ui.S(92), w, Ui.S(22)), Ui.A(Ui.Txt, (int)(255 * ease)),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

            // 底部一条流动的细线，代替死板的进度条
            int by = h - Ui.S(16);
            int bw = Ui.S(140), bx = (w - bw) / 2;
            Ui.FillRound(g, new Rectangle(bx, by, bw, Ui.S(3)), 2, Ui.A(Ui.Line, 160));
            double t = (Ui.Now - at0) / 900.0;
            float phase = (float)(t - Math.Floor(t));
            int fw = (int)(bw * 0.4f);
            int fx = bx + (int)((bw - fw) * phase);
            Ui.FillRound(g, new Rectangle(fx, by, fw, Ui.S(3)), 2, Ui.Accent);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            tick.Stop(); tick.Dispose();
            if (logo != null) logo.Dispose();
            base.OnFormClosing(e);
        }
    }
}
