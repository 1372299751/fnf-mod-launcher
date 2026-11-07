// 各种对话框：首次启动向导 / 添加模组 / 关于 / 引擎信息
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace FnfLauncher
{
    // ===================================================================== 首次启动向导
    
    internal class WelcomeForm : Form
    {
        public string SelectedPath = "";
        private readonly TextBox txtPath;
        private readonly Label lblFound;

        public WelcomeForm(string suggest)
        {
            Text = "欢迎使用 FNF 模组启动器";
            BackColor = Ui.Bar;
            ForeColor = Ui.Txt;
            Font = Ui.FHint;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = true;
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.None;
            ClientSize = new Size(Ui.S(560), Ui.S(348));

            Label t = new Label();
            t.AutoSize = false;
            t.BackColor = Ui.Bar;
            t.ForeColor = Ui.Txt;
            t.Font = new Font("Microsoft YaHei UI", 17f, FontStyle.Bold);
            t.TextAlign = ContentAlignment.MiddleLeft;
            t.Text = "欢迎使用 FNF 模组启动器";
            t.SetBounds(Ui.S(26), Ui.S(20), Ui.S(500), Ui.S(34));

            Label v = new Label();
            v.AutoSize = false;
            v.BackColor = Ui.Bar;
            v.ForeColor = Ui.Accent;
            v.Font = Ui.FHint;
            v.TextAlign = ContentAlignment.MiddleLeft;
            v.Text = "v" + Version4.AppVersion;
            v.SetBounds(Ui.S(26), Ui.S(56), Ui.S(200), Ui.S(20));

            Label d = new Label();
            d.AutoSize = false;
            d.BackColor = Ui.Bar;
            d.ForeColor = Ui.Sub;
            d.Font = Ui.FHint;
            d.TextAlign = ContentAlignment.TopLeft;
            d.Text = "第一次使用，先选一下你的 FNF 模组放在哪个文件夹。\n"
                   + "启动器会把这个文件夹下的每个子文件夹当成一个模组（自动找 exe 和 icon.ico）。\n"
                   + "以后加模组只要把文件夹丢进来，或者用「＋ 添加模组」。";
            d.SetBounds(Ui.S(26), Ui.S(84), Ui.S(510), Ui.S(70));

            Label l1 = new Label();
            l1.AutoSize = false;
            l1.BackColor = Ui.Bar;
            l1.ForeColor = Ui.Txt;
            l1.Font = Ui.FBig;
            l1.TextAlign = ContentAlignment.MiddleLeft;
            l1.Text = "模组文件夹";
            l1.SetBounds(Ui.S(26), Ui.S(158), Ui.S(200), Ui.S(22));

            txtPath = new TextBox();
            txtPath.BorderStyle = BorderStyle.FixedSingle;
            txtPath.BackColor = Ui.Card;
            txtPath.ForeColor = Ui.Txt;
            txtPath.Font = Ui.FHint;
            txtPath.Text = suggest ?? "";
            txtPath.SetBounds(Ui.S(26), Ui.S(184), Ui.S(392), Ui.S(26));
            txtPath.TextChanged += delegate { UpdateFound(); };

            RoundButton browse = new RoundButton("浏览…");
            browse.Filled = false;
            browse.SetBounds(Ui.S(424), Ui.S(183), Ui.S(62), Ui.S(28));
            browse.Click += delegate
            {
                FolderBrowserDialog fd = new FolderBrowserDialog();
                fd.Description = "选择放 FNF 模组的文件夹";
                if (Directory.Exists(txtPath.Text)) fd.SelectedPath = txtPath.Text;
                if (fd.ShowDialog(this) == DialogResult.OK) txtPath.Text = fd.SelectedPath;
            };

            RoundButton auto = new RoundButton("自动查找");
            auto.Filled = false;
            auto.SetBounds(Ui.S(492), Ui.S(183), Ui.S(62), Ui.S(28));
            auto.Click += delegate
            {
                string best = null;
                int bestCount = 0;
                foreach (string c in GuessPaths())
                {
                    int n = QuickCount(c);
                    if (n > bestCount) { best = c; bestCount = n; }
                }
                if (best != null) txtPath.Text = best;
                else MessageBox.Show(this, Lang.T("没在常见位置找到模组文件夹，请手动选择。"), Lang.T("自动查找"),
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            };

            lblFound = new Label();
            lblFound.AutoSize = false;
            lblFound.BackColor = Ui.Bar;
            lblFound.ForeColor = Ui.Sub;
            lblFound.Font = Ui.FSmall;
            lblFound.TextAlign = ContentAlignment.MiddleLeft;
            lblFound.SetBounds(Ui.S(26), Ui.S(218), Ui.S(520), Ui.S(22));

            RoundButton ok = new RoundButton("开始使用");
            ok.Font = Ui.FBtnBig;
            ok.SetBounds(Ui.S(342), Ui.S(276), Ui.S(150), Ui.S(44));
            ok.Click += delegate
            {
                string p = txtPath.Text.Trim();
                if (p.Length == 0 || !Directory.Exists(p))
                {
                    MessageBox.Show(this, Lang.T("这个文件夹不存在，请重新选择。"), Lang.T("FNF 模组启动器"),
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                SelectedPath = p;
                DialogResult = DialogResult.OK;
                Close();
            };

            RoundButton skip = new RoundButton("稍后设置");
            skip.Filled = false;
            skip.SetBounds(Ui.S(222), Ui.S(276), Ui.S(108), Ui.S(44));
            skip.Click += delegate
            {
                DialogResult = DialogResult.Cancel;
                Close();
            };

            Controls.Add(t); Controls.Add(v); Controls.Add(d); Controls.Add(l1);
            Controls.Add(txtPath); Controls.Add(browse); Controls.Add(auto); Controls.Add(lblFound);
            Controls.Add(ok); Controls.Add(skip);

            UpdateFound();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Ui.FadeIn(this);
        }

        private void UpdateFound()
        {
            string p = txtPath.Text.Trim();
            if (!Directory.Exists(p)) { lblFound.Text = "文件夹不存在"; return; }
            int n = QuickCount(p);
            lblFound.Text = n > 0
                ? "✓ 在这个文件夹里找到 " + n + " 个模组"
                : "这个文件夹里还没有模组（可以先用「＋ 添加模组」加进去）";
        }

        // 只看子文件夹里有没有 exe，不做图标/引擎识别，秒出结果
        private static int QuickCount(string dir)
        {
            int n = 0;
            try
            {
                foreach (string d in Directory.GetDirectories(dir))
                {
                    string name = Path.GetFileName(d);
                    if (name.StartsWith(".") || name.StartsWith("_")) continue;
                    bool ok = false;
                    try { ok = Directory.GetFiles(d, "*.exe").Length > 0; } catch { }
                    if (!ok)
                    {
                        try
                        {
                            foreach (string sub in Directory.GetDirectories(d))
                                if (Directory.GetFiles(sub, "*.exe").Length > 0) { ok = true; break; }
                        }
                        catch { }
                    }
                    if (ok) n++;
                }
            }
            catch { }
            return n;
        }

        private static List<string> GuessPaths()
        {
            List<string> list = new List<string>();
            try { list.Add(Path.GetDirectoryName(Application.ExecutablePath)); } catch { }
            string[] drives = { "D", "E", "C", "F" };
            foreach (string d in drives)
            {
                list.Add(d + ":\\fnf");
                list.Add(d + ":\\FNF");
                list.Add(d + ":\\games\\fnf");
                list.Add(d + ":\\Games\\fnf");
            }
            try { list.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "fnf")); } catch { }
            return list;
        }
    }

    // ===================================================================== 添加模组
    
    internal class AddForm : Form
    {
        public bool FolderChosen;
        public string FolderPath = "";
        public bool WebChosen;
        public string WebName = "";
        public string WebUrl = "";
        public string InstalledPath = "";

        private readonly TextBox txtFolder, txtName, txtUrl;
        private readonly string addRoot;

        public AddForm(string root)
        {
            Text = "添加模组";
            BackColor = Ui.Bar;
            ForeColor = Ui.Txt;
            Font = Ui.FHint;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoScaleMode = AutoScaleMode.None;
            addRoot = root;
            ClientSize = new Size(Ui.S(540), Ui.S(316));

            // --- 本地模组
            Label h1 = Head("本地模组（选一个带 exe 的模组文件夹）");
            h1.SetBounds(Ui.S(24), Ui.S(18), Ui.S(460), Ui.S(22));

            txtFolder = new TextBox();
            txtFolder.BorderStyle = BorderStyle.FixedSingle;
            txtFolder.BackColor = Ui.Card;
            txtFolder.ForeColor = Ui.Txt;
            txtFolder.Font = Ui.FHint;
            txtFolder.SetBounds(Ui.S(24), Ui.S(44), Ui.S(376), Ui.S(26));

            RoundButton bFolder = new RoundButton("浏览…");
            bFolder.Filled = false;
            bFolder.SetBounds(Ui.S(408), Ui.S(43), Ui.S(64), Ui.S(28));
            bFolder.Click += delegate
            {
                FolderBrowserDialog fd = new FolderBrowserDialog();
                fd.Description = "选择模组文件夹（里面要有 exe）";
                if (fd.ShowDialog(this) == DialogResult.OK) txtFolder.Text = fd.SelectedPath;
            };

            RoundButton bAddFolder = new RoundButton("添加");
            bAddFolder.SetBounds(Ui.S(480), Ui.S(43), Ui.S(56), Ui.S(28));
            bAddFolder.Click += delegate
            {
                string p = txtFolder.Text.Trim();
                if (p.Length == 0 || !Directory.Exists(p))
                {
                    MessageBox.Show(this, Lang.T("请选择一个存在的文件夹。"), Lang.T("添加模组"),
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                FolderPath = p;
                FolderChosen = true;
                DialogResult = DialogResult.OK;
                Close();
            };

            Label tip = new Label();
            tip.AutoSize = false;
            tip.BackColor = Ui.Bar;
            tip.ForeColor = Ui.Sub;
            tip.Font = Ui.FSmall;
            tip.TextAlign = ContentAlignment.TopLeft;
            tip.Text = "选好后会问你要「复制到识别目录」还是「只添加引用」；也可以把文件夹或 .zip 直接拖进来。";
            tip.SetBounds(Ui.S(24), Ui.S(76), Ui.S(500), Ui.S(36));

            // --- 网页版
            Label h2 = Head("网页版 FNF（填名称和网址，用系统默认浏览器打开）");
            h2.SetBounds(Ui.S(24), Ui.S(126), Ui.S(500), Ui.S(22));

            txtName = new TextBox();
            txtName.BorderStyle = BorderStyle.FixedSingle;
            txtName.BackColor = Ui.Card;
            txtName.ForeColor = Ui.Txt;
            txtName.Font = Ui.FHint;

            Label ln = new Label();
            ln.AutoSize = false;
            ln.BackColor = Ui.Bar;
            ln.ForeColor = Ui.Sub;
            ln.Font = Ui.FSmall;
            ln.TextAlign = ContentAlignment.MiddleLeft;
            ln.Text = "名称：例如 FNF 网页版 / 某个网页移植";
            ln.SetBounds(Ui.S(24), Ui.S(148), Ui.S(220), Ui.S(18));

            txtName.SetBounds(Ui.S(24), Ui.S(168), Ui.S(220), Ui.S(26));

            Label lu = new Label();
            lu.AutoSize = false;
            lu.BackColor = Ui.Bar;
            lu.ForeColor = Ui.Sub;
            lu.Font = Ui.FSmall;
            lu.TextAlign = ContentAlignment.MiddleLeft;
            lu.Text = "网址：例如 https://example.com/fnf/";
            lu.SetBounds(Ui.S(256), Ui.S(148), Ui.S(280), Ui.S(18));

            txtUrl = new TextBox();
            txtUrl.BorderStyle = BorderStyle.FixedSingle;
            txtUrl.BackColor = Ui.Card;
            txtUrl.ForeColor = Ui.Txt;
            txtUrl.Font = Ui.FHint;
            txtUrl.SetBounds(Ui.S(256), Ui.S(168), Ui.S(280), Ui.S(26));

            RoundButton bAddWeb = new RoundButton("添加网页版");
            bAddWeb.SetBounds(Ui.S(368), Ui.S(204), Ui.S(168), Ui.S(36));
            bAddWeb.Click += delegate
            {
                string name = txtName.Text.Trim();
                string url = txtUrl.Text.Trim();
                if (name.Length == 0) name = "网页版 FNF";
                if (url.Length == 0)
                {
                    MessageBox.Show(this, Lang.T("请填写网址。"), Lang.T("添加网页版"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                    !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    url = "https://" + url;
                try { new Uri(url); }
                catch
                {
                    MessageBox.Show(this, Lang.T("这个网址看起来不对。"), Lang.T("添加网页版"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                WebName = name;
                WebUrl = url;
                WebChosen = true;
                DialogResult = DialogResult.OK;
                Close();
            };

            RoundButton bDl = new RoundButton("下载游戏…");
            bDl.Filled = false;
            bDl.SetBounds(Ui.S(24), Ui.S(204), Ui.S(150), Ui.S(36));
            bDl.Click += delegate
            {
                using (DownloadForm dl = new DownloadForm()) dl.ShowDialog(this);
            };

            RoundButton bZip = new RoundButton("从压缩包安装…");
            bZip.Filled = false;
            bZip.SetBounds(Ui.S(182), Ui.S(204), Ui.S(178), Ui.S(36));
            bZip.Click += delegate
            {
                OpenFileDialog od = new OpenFileDialog();
                od.Filter = "压缩包 (*.zip;*.rar;*.7z)|*.zip;*.rar;*.7z|所有文件 (*.*)|*.*";
                if (od.ShowDialog(this) == DialogResult.OK) InstallZip(od.FileName);
            };

            // 支持把 zip 直接拖进本窗口
            AllowDrop = true;
            DragEnter += delegate(object s, DragEventArgs de)
            {
                if (de.Data.GetDataPresent(DataFormats.FileDrop)) de.Effect = DragDropEffects.Copy;
                else de.Effect = DragDropEffects.None;
            };
            DragDrop += delegate(object s, DragEventArgs de)
            {
                string[] files = de.Data.GetData(DataFormats.FileDrop) as string[];
                if (files == null || files.Length == 0) return;
                InstallZip(files[0]);
            };
            RoundButton close = new RoundButton("关闭");
            close.Filled = false;
            close.SetBounds(Ui.S(420), Ui.S(264), Ui.S(96), Ui.S(36));
            close.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };

            Label lroot = new Label();
            lroot.AutoSize = false;
            lroot.BackColor = Ui.Bar;
            lroot.ForeColor = Ui.Sub;
            lroot.Font = Ui.FSmall;
            lroot.TextAlign = ContentAlignment.MiddleLeft;
            lroot.Text = "识别目录：" + root;
            lroot.SetBounds(Ui.S(24), Ui.S(264), Ui.S(380), Ui.S(36));

            Controls.Add(h1); Controls.Add(txtFolder); Controls.Add(bFolder); Controls.Add(bAddFolder);
            Controls.Add(tip); Controls.Add(h2); Controls.Add(ln); Controls.Add(lu);
            Controls.Add(txtName); Controls.Add(txtUrl); Controls.Add(bAddWeb); Controls.Add(bDl); Controls.Add(bZip);
            Controls.Add(lroot); Controls.Add(close);
        }

        // 压缩包安装：按钮和拖拽都走这里（支持 zip / rar / 7z）
        private void InstallZip(string path)
        {
            string target;
            if (ArchiveInstall.Prompt(this, path, addRoot, out target) && target != null)
            {
                InstalledPath = target;
                DialogResult = DialogResult.Cancel;
                Close();
            }
        }
        private static Label Head(string text)
        {
            Label l = new Label();
            l.AutoSize = false;
            l.BackColor = Ui.Bar;
            l.ForeColor = Ui.Txt;
            l.Font = Ui.FBig;
            l.TextAlign = ContentAlignment.MiddleLeft;
            l.Text = text;
            return l;
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Ui.FadeIn(this);
        }
    }

    // ===================================================================== 引擎信息（手填校正）
    
    internal class EngineForm : Form
    {
        public string EngineName = "";
        public string EngineVersion = "";

        private readonly TextBox txtName, txtVer;

        public EngineForm(ModEntry m)
        {
            EngineName = m.EngineName;
            EngineVersion = m.EngineVersion;

            Text = "引擎信息 · " + m.Name;
            BackColor = Ui.Bar;
            ForeColor = Ui.Txt;
            Font = Ui.FHint;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoScaleMode = AutoScaleMode.None;
            ClientSize = new Size(Ui.S(480), Ui.S(258));

            Label d = new Label();
            d.AutoSize = false;
            d.BackColor = Ui.Bar;
            d.ForeColor = Ui.Sub;
            d.Font = Ui.FSmall;
            d.TextAlign = ContentAlignment.TopLeft;
            d.Text = "自动识别结果：" + (m.EngineDisplay.Length > 0 ? m.EngineDisplay : "未知")
                   + (m.EngineTech.Length > 0 ? "（" + m.EngineTech + "）" : "")
                   + "\n有些魔改包会在 exe 里去掉版本字符串，识别不到时可以在这里手动填，会覆盖自动结果。";
            d.SetBounds(Ui.S(24), Ui.S(16), Ui.S(432), Ui.S(56));

            Label l1 = new Label();
            l1.AutoSize = false; l1.BackColor = Ui.Bar; l1.ForeColor = Ui.Txt;
            l1.Font = Ui.FHint; l1.TextAlign = ContentAlignment.MiddleLeft;
            l1.Text = "引擎名";
            l1.SetBounds(Ui.S(24), Ui.S(80), Ui.S(120), Ui.S(22));

            txtName = new TextBox();
            txtName.BorderStyle = BorderStyle.FixedSingle;
            txtName.BackColor = Ui.Card; txtName.ForeColor = Ui.Txt; txtName.Font = Ui.FHint;
            txtName.Text = m.IsWeb ? "网页版" : m.EngineName;
            txtName.SetBounds(Ui.S(24), Ui.S(104), Ui.S(432), Ui.S(26));

            Label l2 = new Label();
            l2.AutoSize = false; l2.BackColor = Ui.Bar; l2.ForeColor = Ui.Txt;
            l2.Font = Ui.FHint; l2.TextAlign = ContentAlignment.MiddleLeft;
            l2.Text = "版本号（可留空）";
            l2.SetBounds(Ui.S(24), Ui.S(140), Ui.S(200), Ui.S(22));

            txtVer = new TextBox();
            txtVer.BorderStyle = BorderStyle.FixedSingle;
            txtVer.BackColor = Ui.Card; txtVer.ForeColor = Ui.Txt; txtVer.Font = Ui.FHint;
            txtVer.Text = Version4.AppVersion;
            txtVer.SetBounds(Ui.S(24), Ui.S(164), Ui.S(200), Ui.S(26));

            RoundButton ok = new RoundButton("保存");
            ok.SetBounds(Ui.S(268), Ui.S(204), Ui.S(92), Ui.S(36));
            ok.Click += delegate
            {
                EngineName = txtName.Text.Trim();
                EngineVersion = txtVer.Text.Trim();
                DialogResult = DialogResult.OK;
                Close();
            };

            RoundButton cancel = new RoundButton("取消");
            cancel.Filled = false;
            cancel.SetBounds(Ui.S(368), Ui.S(204), Ui.S(88), Ui.S(36));
            cancel.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };

            Controls.Add(d); Controls.Add(l1); Controls.Add(txtName);
            Controls.Add(l2); Controls.Add(txtVer); Controls.Add(ok); Controls.Add(cancel);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Ui.FadeIn(this);
        }
    }

    // ===================================================================== 关于
    
    internal class AboutForm : Form
    {
        // 关于正文（整段说明，TextBox 里滚动查看）
        private const string Body =
@"一款面向 Friday Night Funkin' 及其衍生引擎的模组管理器，负责模组的识别、安装、启动、浏览、备份与外观定制。以下为功能说明。

━━ 模组管理 ━━
软件会自动扫描识别目录，每个文件夹即一个模组，图标取该文件夹下的 icon.ico。引擎与版本通过结构特征、exe 字符串签名和版本锚点三重判断。模组支持分类、隐藏、置顶与重命名，排序方式可在名称、最近启动、引擎之间切换；重命名会直接改动磁盘上的目录名，并同步迁移分类、置顶、作者、游玩记录等配置。可为模组创建桌面与开始菜单快捷方式，网页版条目支持自定义图标。把文件夹或压缩包（.zip / .rar / .7z）拖进主窗口，即可添加或安装。

━━ 安装与导入 ━━
压缩包安装会自动识别内部结构，剥掉外层多余的文件夹，只安装真正的那一层。判断依据是最浅一层的 exe；若没有 exe，则看 lime.ndll、mods/pack.json 或 modsList.txt。.zip 由内置解压处理，.rar / .7z 借用本机 Bandizip、7-Zip 或 WinRAR 的命令行；遇到其他格式会直接提示不支持。安装前可给模组起名（同时作为文件夹名），目标已存在时会询问是否合并覆盖。压缩包与文件夹两种安装方式都有进度窗，可中途取消并自动清理残留。

━━ 启动与日志 ━━
双击、回车、点「启动游戏」或按 Ctrl+1~9 均可启动前九个模组。启动前会做体检，路径含中文、目录不完整或 exe 丢失都会提示。启动后可自动隐藏到系统托盘，游戏退出时自动恢复窗口；关闭确认小窗提供「隐藏到托盘 / 直接退出」，可记住选择，也可连带关闭游戏。

每次启动都会记录时间、模组、引擎、退出码与耗时，点标题栏的「日志」可查看、复制、清空。异常退出时自动弹出崩溃分析，把退出码与目录特征翻译成可能原因。软件单实例运行，并支持导出诊断信息。

━━ 试听与内容浏览 ━━
合听会把伴奏与人声预混成一个缓存文件，用系统播放器打开，不会出现不同步；伴奏、人声也可单独播放，只有合并音轨的歌曲会提示无法拆分。

曲目列表显示 BPM、难度倍率、音轨类型（人声+伴奏 / 合并）与时长，疑似原版条目可折叠。预览为大图窗口，支持左右切换、双击全屏。资料页包含故事线/周、成就、资源构成（图片 / 音频 / 脚本比例条）、引擎与文件信息以及主题色。Lua 脚本管理会列出 scripts、custom_events、custom_notetypes，可查看、添加、删除。

━━ 备份与清理 ━━
启动模组前自动备份存档，保留最近 5 份，资料窗内可一键还原。清理功能覆盖启动器缓存（模组信息）、引擎日志、试听缓存与旧存档备份。

━━ 界面与外观 ━━
整套界面采用 PCL 式规则：单一色相 8 级色阶，切换强调色时标题栏、卡片、描边会一起偏色。窗口为自绘：无系统边框与标题栏，8px 圆角，标题栏渐变配字标与自绘的 − □ ×；拖边缩放、拖标题栏移动、双击最大化均为自实现，不依赖系统厚边框。导航按钮收进标题栏，左侧底部是大号「启动游戏」，详情区拆成「基本信息 / 运行统计」两张卡，搜索框为圆角自绘（聚焦变强调色，含放大镜与占位提示）。

外观可自定义主色（色相条）、背景图与三档浓淡（跨控件拼接对齐），窗口背景可压暗；另有 6 种预设强调色、启动动画，并支持 Ctrl +/- 缩放界面。所有外部跳转（网页、资源管理器）做了 1.2 秒去重，不会一次打开两个。

━━ 下载中心 ━━
提供 GameJolt、GameBanana、FridayNightFunkin.net、KBH 等下载站入口。下载中心按分类组织卡片，PE 引擎各版本可展开 / 收起，NF 条目标注国产，选版本提示可永久关闭。

━━ 键盘统计 ━━
统计总计与按模组划分的按键次数，记录每个模组里各键按了多少次，不记录任何输入内容。

━━ 快捷键 ━━
↑ ↓　　　　选择
Enter　　　启动
F5　　　　 刷新
Ctrl+F　　 搜索
Ctrl+1~9　 启动前九个
Ctrl +/-　 缩放界面

修改识别目录后立即重扫，无需手动刷新。点右上角版本号可再次打开本窗口。";

        public AboutForm(int modCount, int webCount, string root)
        {
            Text = "关于 FNF 模组启动器";
            BackColor = Ui.Bar;
            ForeColor = Ui.Txt;
            Font = Ui.FHint;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoScaleMode = AutoScaleMode.None;
            ClientSize = new Size(Ui.S(620), Ui.S(620));
            DoubleBuffered = true;

            Label t = new Label();
            t.AutoSize = false; t.BackColor = Ui.Bar; t.ForeColor = Ui.Txt;
            t.Font = new Font("Microsoft YaHei UI", 15f, FontStyle.Bold);
            t.TextAlign = ContentAlignment.MiddleLeft;
            t.Text = "FNF 模组启动器";
            t.SetBounds(Ui.S(24), Ui.S(16), Ui.S(300), Ui.S(32));

            Label ver = new Label();
            ver.AutoSize = false; ver.BackColor = Ui.Bar; ver.ForeColor = Ui.Accent;
            ver.Font = Ui.FHint; ver.TextAlign = ContentAlignment.MiddleLeft;
            ver.Text = "版本 " + Version4.AppVersion + "　·　免安装 · 解压即用，不需要装运行库";
            ver.SetBounds(Ui.S(24), Ui.S(48), Ui.S(560), Ui.S(20));

            Label stats = new Label();
            stats.AutoSize = false; stats.BackColor = Ui.Card; stats.ForeColor = Ui.Sub;
            stats.Font = Ui.FSmall; stats.TextAlign = ContentAlignment.MiddleLeft;
            stats.Text = "  当前识别目录 " + root + "　｜　已识别 " + modCount + " 个本地模组" + (webCount > 0 ? "、" + webCount + " 个网页版" : "");
            stats.SetBounds(Ui.S(24), Ui.S(74), Ui.S(572), Ui.S(30));

            TextBox list = new TextBox();
            list.Multiline = true;
            list.ReadOnly = true;
            list.ScrollBars = ScrollBars.Vertical;
            list.WordWrap = true;
            list.BorderStyle = BorderStyle.None;
            list.BackColor = Ui.Bar;
            list.ForeColor = Ui.Txt;
            list.Font = new Font("Microsoft YaHei UI", 9f);
            list.SetBounds(Ui.S(24), Ui.S(112), Ui.S(572), Ui.S(366));
            list.Text = Body;
            list.SelectionStart = 0;
            list.SelectionLength = 0;

            Label author = new Label();
            author.AutoSize = false; author.BackColor = Ui.Bar; author.ForeColor = Ui.Txt;
            author.Font = Ui.FHint; author.TextAlign = ContentAlignment.MiddleLeft;
            author.Text = "作者：蘑菇汉化组组长";
            author.SetBounds(Ui.S(24), Ui.S(486), Ui.S(412), Ui.S(24));

            RoundButton bOpen = new RoundButton("打开 B 站主页");
            bOpen.Filled = false;
            bOpen.Font = Ui.FSmall;
            bOpen.SetBounds(Ui.S(24), Ui.S(516), Ui.S(130), Ui.S(32));
            bOpen.Click += delegate
            {
                try
                {
                    Process.Start(new ProcessStartInfo("https://space.bilibili.com/3632319443241866?spm_id_from=333.1007.0.0")
                    { UseShellExecute = true });
                }
                catch { }
            };

            RoundButton ok = new RoundButton("关闭");
            ok.SetBounds(Ui.S(500), Ui.S(516), Ui.S(96), Ui.S(32));
            ok.Click += delegate { Close(); };

            Controls.Add(t); Controls.Add(ver); Controls.Add(stats); Controls.Add(list);
            Controls.Add(author); Controls.Add(bOpen); Controls.Add(ok);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Ui.FadeIn(this);
        }
    }
    internal static class Version4
    {
        public const string AppVersion = "1.9";
    }
}
