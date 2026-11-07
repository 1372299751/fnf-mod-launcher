// 一键清理：日志 / 崩溃记录 / 试听缓存 / 旧存档备份
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace FnfLauncher
{
    internal class CleanupForm : Form
    {
        private class Cat
        {
            public string Title;
            public string Note;
            public long Bytes;
            public int Files;
            public Action Clean;
            public CheckBox Box;
            public Label NoteLabel;
        }

        public static Config Cfg;   // 由主窗口注入，用来清模组信息缓存
        private readonly List<Cat> cats = new List<Cat>();
        private readonly Label summary;
        private readonly Label cleanLabel;
        private RoundButton bGo;

        public CleanupForm()
        {
            Text = "清理垃圾";
            BackColor = Ui.Bar;
            ForeColor = Ui.Txt;
            Font = Ui.FHint;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoScaleMode = AutoScaleMode.None;
            ClientSize = new Size(Ui.S(560), Ui.S(448));
            DoubleBuffered = true;

            string exeDir = Path.GetDirectoryName(Application.ExecutablePath);

            cats.Add(new Cat
            {
                Title = "启动器缓存（模组信息）",
                Note = "引擎识别与扫描结果的缓存，清掉后下次启动会重新识别",
                Clean = delegate { try { if (Cfg != null) ModCache.Clear(Cfg); } catch { } }
            });

            cats.Add(new Cat
            {
                Title = "引擎日志与崩溃记录",
                Note = "mods 目录下的 log.txt / crash*.txt / *.log",
                Clean = delegate
                {
                    foreach (string f in FindLogs(exeDir)) { try { File.Delete(f); } catch { } }
                }
            });

            cats.Add(new Cat
            {
                Title = "试听缓存（预混 WAV）",
                Note = "合听时生成的临时音频文件",
                Clean = delegate { Mixer.ClearCache(); }
            });

            cats.Add(new Cat
            {
                Title = "旧存档备份",
                Note = "每个模组只保留最近 2 份备份",
                Clean = delegate
                {
                    try
                    {
                        string root = SaveBackup.Root;
                        if (!Directory.Exists(root)) return;
                        foreach (string modDir in Directory.GetDirectories(root))
                        {
                            string[] snaps = Directory.GetDirectories(modDir);
                            Array.Sort(snaps, StringComparer.OrdinalIgnoreCase);
                            Array.Reverse(snaps);
                            for (int i = 2; i < snaps.Length; i++)
                            {
                                try { Directory.Delete(snaps[i], true); }
                                catch { }
                            }
                        }
                    }
                    catch { }
                }
            });

            int y = Ui.S(56);
            foreach (Cat c in cats)
            {
                Cat cc = c;
                CheckBox box = new CheckBox();
                box.Text = c.Title;
                box.ForeColor = Ui.Txt;
                box.BackColor = Ui.Bar;
                box.Font = Ui.FHint;
                box.Checked = true;
                box.SetBounds(Ui.S(24), y, Ui.S(400), Ui.S(22));
                Controls.Add(box);
                cc.Box = box;

                Label note = new Label();
                note.AutoSize = false;
                note.BackColor = Ui.Bar;
                note.ForeColor = Ui.Sub;
                note.Font = Ui.FSmall;
                note.TextAlign = ContentAlignment.MiddleLeft;
                note.Text = c.Note;
                note.SetBounds(Ui.S(46), y + Ui.S(20), Ui.S(420), Ui.S(18));
                Controls.Add(note);
                cc.NoteLabel = note;
                y += Ui.S(48);
            }

            cleanLabel = new Label();
            cleanLabel.AutoSize = false;
            cleanLabel.BackColor = Ui.Bar;
            cleanLabel.ForeColor = Color.FromArgb(120, 230, 170);
            cleanLabel.Font = new Font("Microsoft YaHei UI", 13f, FontStyle.Bold);
            cleanLabel.TextAlign = ContentAlignment.MiddleCenter;
            cleanLabel.Text = "✓  您的游戏很干净，没有需要清理的东西";
            cleanLabel.SetBounds(Ui.S(24), Ui.S(120), Ui.S(512), Ui.S(80));
            cleanLabel.Visible = false;
            Controls.Add(cleanLabel);

            summary = new Label();
            summary.AutoSize = false;
            summary.BackColor = Ui.Card;
            summary.ForeColor = Ui.Txt;
            summary.Font = Ui.FHint;
            summary.TextAlign = ContentAlignment.MiddleLeft;
            summary.SetBounds(Ui.S(24), y + Ui.S(8), Ui.S(512), Ui.S(44));
            Controls.Add(summary);

            RoundButton bScan = new RoundButton("重新扫描");
            bScan.Filled = false;
            bScan.SetBounds(Ui.S(24), y + Ui.S(64), Ui.S(104), Ui.S(34));
            bScan.Click += delegate { Scan(); };

            bGo = new RoundButton("清理选中");
            bGo.SetBounds(Ui.S(136), y + Ui.S(64), Ui.S(104), Ui.S(34));
            bGo.Click += delegate
            {
                long freed = 0;
                int n = 0;
                foreach (Cat c in cats)
                {
                    if (!c.Box.Checked) continue;
                    freed += c.Bytes;
                    n += c.Files;
                    try { c.Clean(); } catch { }
                }
                if (freed <= 0 && n <= 0)
                {
                    Scan();
                    return;                      // 没东西可清就留在原地
                }
                Close();                         // 清理完成自动关窗
            };

            RoundButton bClose = new RoundButton("关闭");
            bClose.Filled = false;
            bClose.SetBounds(Ui.S(432), y + Ui.S(64), Ui.S(104), Ui.S(34));
            bClose.Click += delegate { Close(); };

            Controls.Add(bScan); Controls.Add(bGo); Controls.Add(bClose);
            Scan();
        }

        private static string MB(long bytes)
        {
            if (bytes >= 1024L * 1024) return (bytes / (1024.0 * 1024)).ToString("0.0") + " MB";
            return (bytes / 1024) + " KB";
        }

        private static List<string> FindLogs(string exeDir)
        {
            List<string> res = new List<string>();
            try
            {
                List<string> roots = new List<string>();
                roots.Add(exeDir);
                foreach (string d in Directory.GetDirectories(exeDir)) roots.Add(d);
                foreach (string r in roots)
                {
                    if (!Directory.Exists(r)) continue;
                    foreach (string f in Directory.GetFiles(r, "*.log", SearchOption.AllDirectories)) res.Add(f);
                    foreach (string f in Directory.GetFiles(r, "crash*.txt", SearchOption.AllDirectories)) res.Add(f);
                }
            }
            catch { }
            return res;
        }

        private void Scan()
        {
            string exeDir = Path.GetDirectoryName(Application.ExecutablePath);
            long total = 0;
            int files = 0;
            foreach (Cat c in cats)
            {
                long bytes = 0;
                int n = 0;
                if (c.Title.StartsWith("启动器缓存"))
                {
                    n = 0;
                    try { if (Cfg != null) foreach (string k in Cfg.KeysStartingWith("modcache.")) n++; } catch { }
                    bytes = 0;
                }
                else if (c.Title.StartsWith("引擎日志"))
                {
                    foreach (string f in FindLogs(exeDir)) { try { bytes += new FileInfo(f).Length; n++; } catch { } }
                }
                else if (c.Title.StartsWith("试听缓存"))
                {
                    bytes = Mixer.CacheSize();
                    try { n = Directory.Exists(Mixer.CacheDir) ? Directory.GetFiles(Mixer.CacheDir, "*.wav").Length : 0; } catch { }
                }
                else
                {
                    try
                    {
                        string root = SaveBackup.Root;
                        if (Directory.Exists(root))
                        {
                            foreach (string modDir in Directory.GetDirectories(root))
                            {
                                string[] snaps = Directory.GetDirectories(modDir);
                                Array.Sort(snaps, StringComparer.OrdinalIgnoreCase);
                                Array.Reverse(snaps);
                                for (int i = 2; i < snaps.Length; i++)
                                {
                                    foreach (string f in Directory.GetFiles(snaps[i], "*", SearchOption.AllDirectories))
                                    {
                                        try { bytes += new FileInfo(f).Length; n++; } catch { }
                                    }
                                }
                            }
                        }
                    }
                    catch { }
                }
                c.Bytes = bytes;
                c.Files = n;
                c.Box.Text = c.Title + "（" + n + " 个文件 · " + MB(bytes) + "）";
                total += bytes;
                files += n;
            }

            bool clean = (files == 0 && total <= 0);
            foreach (Cat c in cats)
            {
                c.Box.Visible = !clean;
                c.NoteLabel.Visible = !clean;
            }
            summary.Visible = !clean;
            cleanLabel.Visible = clean;
            if (bGo != null) bGo.Enabled = !clean;

            if (clean) summary.Text = "";
            else summary.Text = "  可清理：" + files + " 个文件，约 " + MB(total);
            Invalidate();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Ui.DarkTitleBar(Handle);
            Ui.FadeIn(this);
        }
    }
}
