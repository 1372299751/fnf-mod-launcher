// 依附引擎的模组包：没有 exe、只有 mods\ 或 assets\ 的包，装到用户选中的引擎里
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Windows.Forms;

namespace FnfLauncher
{
    // 可选的引擎（识别目录里带 exe 的文件夹）
    internal class EngineCandidate
    {
        public string Dir = "";
        public string Name = "";
        public string EngineText = "";
        public string Family = "";
        public string Exe = "";
        public Image Icon;

        public string DirLeaf
        {
            get { try { return Path.GetFileName(Dir.TrimEnd('\\')); } catch { return Dir; } }
        }
    }

    internal static class EnginePack
    {
        // ---------------------------------------------------------------- 识别

        // 文件夹是不是"依附引擎"的包：没有 exe，但有 mods\ / assets\（或 Psych 的 pack.json）
        public static bool InspectFolder(string dir, out string layerPrefix, out bool hasMods, out bool hasAssets)
        {
            layerPrefix = "";
            hasMods = false;
            hasAssets = false;
            try
            {
                string leaf = Path.GetFileName(dir.TrimEnd('\\'));
                if (string.Equals(leaf, "mods", StringComparison.OrdinalIgnoreCase)) { hasMods = true; layerPrefix = "mods/"; }
                else if (string.Equals(leaf, "assets", StringComparison.OrdinalIgnoreCase)) { hasAssets = true; layerPrefix = "assets/"; }

                if (Directory.Exists(Path.Combine(dir, "mods"))) hasMods = true;
                if (Directory.Exists(Path.Combine(dir, "assets"))) hasAssets = true;
                if (!hasMods && !hasAssets && !File.Exists(Path.Combine(dir, "pack.json"))) return false;

                // 根目录有 exe：那是完整游戏（或自带引擎的整合包），走原来的添加流程
                if (Directory.GetFiles(dir, "*.exe", SearchOption.TopDirectoryOnly).Length > 0) return false;
                return true;
            }
            catch { return false; }
        }

        // 压缩包是不是"依附引擎"的包（判定依据同上，只是数据来自条目表）
        public static bool InspectZip(string zipPath, string innerRoot, out string layerPrefix, out bool hasMods, out bool hasAssets)
        {
            layerPrefix = "";
            hasMods = false;
            hasAssets = false;
            bool hasPackJson = false;
            try
            {
                string leaf = innerRoot.Length > 0 ? innerRoot.Substring(innerRoot.LastIndexOf('/') + 1) : "";
                if (string.Equals(leaf, "mods", StringComparison.OrdinalIgnoreCase)) layerPrefix = "mods/";
                else if (string.Equals(leaf, "assets", StringComparison.OrdinalIgnoreCase)) layerPrefix = "assets/";
                string pre = innerRoot.Length == 0 ? "" : innerRoot + "/";

                using (ZipArchive zip = ZipFile.OpenRead(zipPath))
                {
                    foreach (ZipArchiveEntry e in zip.Entries)
                    {
                        string n = e.FullName.Replace('\\', '/');
                        if (n.EndsWith("/")) continue;
                        if (pre.Length > 0)
                        {
                            if (!n.StartsWith(pre, StringComparison.OrdinalIgnoreCase)) continue;
                            n = n.Substring(pre.Length);
                        }
                        n = layerPrefix + n;
                        if (n.StartsWith("mods/", StringComparison.OrdinalIgnoreCase)) hasMods = true;
                        else if (n.StartsWith("assets/", StringComparison.OrdinalIgnoreCase)) hasAssets = true;
                        if (n.EndsWith("/pack.json", StringComparison.OrdinalIgnoreCase) || n.Equals("pack.json", StringComparison.OrdinalIgnoreCase))
                            hasPackJson = true;
                    }
                }
            }
            catch { return false; }
            return hasMods || hasAssets || hasPackJson;
        }

        // ---------------------------------------------------------------- 引擎列表

        public static List<EngineCandidate> FindEngines(string root, Config cfg)
        {
            List<EngineCandidate> list = new List<EngineCandidate>();
            if (string.IsNullOrEmpty(root)) return list;
            try
            {
                foreach (string d in Directory.GetDirectories(root))
                {
                    string exe = PreferExe(d);
                    if (exe == null) continue;
                    EngineCandidate c = new EngineCandidate();
                    c.Dir = d;
                    c.Name = Path.GetFileName(d.TrimEnd('\\'));
                    c.Exe = exe;
                    string cached = "";
                    try { if (cfg != null) cached = cfg.Get("eng." + c.Name, ""); } catch { }
                    string[] parts = cached.Split('|');
                    c.EngineText = parts.Length > 1 && parts[1].Length > 0 ? parts[0] + " " + parts[1] : (parts.Length > 0 ? parts[0] : "");
                    if (c.EngineText.Length == 0) c.EngineText = "未知引擎";
                    string low = c.EngineText.ToLowerInvariant();
                    if (low.Contains("novaflare")) c.Family = "novaflare";
                    else if (low.Contains("psych")) c.Family = "psych";
                    else if (low.Contains("codename")) c.Family = "codename";
                    else if (low.Contains("kade")) c.Family = "kade";
                    else if (low.Contains("原版") || low.Contains("funkin") || low.Contains("friday")) c.Family = "vanilla";
                    list.Add(c);
                }
            }
            catch { }
            list.Sort(delegate(EngineCandidate a, EngineCandidate b)
            {
                return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });
            return list;
        }

        private static string PreferExe(string dir)
        {
            try
            {
                string[] exes = Directory.GetFiles(dir, "*.exe", SearchOption.TopDirectoryOnly);
                if (exes.Length == 0)
                {
                    // 有的引擎把 exe 藏在 bin\ 里（NovaFlare 之类），往下找两层
                    List<string> deep = new List<string>();
                    foreach (string sub in Directory.GetDirectories(dir))
                    {
                        deep.AddRange(Directory.GetFiles(sub, "*.exe", SearchOption.TopDirectoryOnly));
                        foreach (string sub2 in Directory.GetDirectories(sub))
                            deep.AddRange(Directory.GetFiles(sub2, "*.exe", SearchOption.TopDirectoryOnly));
                    }
                    if (deep.Count == 0) return null;
                    string shallow = null;
                    foreach (string e in deep) if (shallow == null || e.Length < shallow.Length) shallow = e;
                    exes = new string[] { shallow };
                }
                string leaf = Path.GetFileName(dir.TrimEnd('\\'));
                foreach (string e in exes)
                {
                    string n = Path.GetFileNameWithoutExtension(e);
                    if (string.Equals(n, leaf, StringComparison.OrdinalIgnoreCase)) return e;
                }
                foreach (string e in exes)
                {
                    string n = Path.GetFileName(e).ToLowerInvariant();
                    if (n.Contains("unins") || n.Contains("crash") || n.Contains("setup") || n.Contains("updater")) continue;
                    return e;
                }
                return exes[0];
            }
            catch { return null; }
        }

        // 按包内容猜一个默认引擎（Psych 类包优先 Psych 系）
        public static int GuessIndex(List<EngineCandidate> engines, bool hasMods, bool hasAssets)
        {
            if (engines == null || engines.Count == 0) return -1;
            string want = hasMods ? "psych" : (hasAssets ? "vanilla" : "");
            if (want.Length > 0)
            {
                for (int i = 0; i < engines.Count; i++) if (engines[i].Family == want) return i;
            }
            if (hasMods)
            {
                for (int i = 0; i < engines.Count; i++) if (engines[i].Family == "novaflare") return i;
            }
            return 0;
        }

        // ---------------------------------------------------------------- 安装

        private static string MapTarget(string rel, bool hasMods, bool hasAssets, string fallbackName)
        {
            rel = rel.Replace('\\', '/').TrimStart('/');
            if (hasMods && rel.StartsWith("mods/", StringComparison.OrdinalIgnoreCase)) return rel;
            if (hasAssets && rel.StartsWith("assets/", StringComparison.OrdinalIgnoreCase)) return rel;
            if (hasMods || hasAssets) return null;   // 层外面的散文件（readme 之类）不装
            return "mods/" + fallbackName + "/" + rel;
        }

        // 单个文件写入：modsList.txt 已存在就不覆盖（那是引擎自己的启用列表）
        private static bool WriteFile(string src, string dest)
        {
            try
            {
                string name = Path.GetFileName(dest);
                if (string.Equals(name, "modsList.txt", StringComparison.OrdinalIgnoreCase) && File.Exists(dest)) return false;
                string dir = Path.GetDirectoryName(dest);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.Copy(src, dest, true);
                return true;
            }
            catch { return false; }
        }

        public static bool CopyFolder(ProgressForm pf, string engineDir, string packDir, string layerPrefix,
            bool hasMods, bool hasAssets, string fallbackName, out int copied, out string err)
        {
            copied = 0;
            err = "";
            List<string> files = new List<string>();
            try
            {
                string baseDir = packDir.TrimEnd('\\');
                foreach (string f in Directory.GetFiles(baseDir, "*", SearchOption.AllDirectories)) files.Add(f);
                foreach (string f in files)
                {
                    if (pf != null && pf.Cancelled) { err = "已取消"; return false; }
                    string rel = layerPrefix + f.Substring(baseDir.Length).TrimStart('\\');
                    string dstRel = MapTarget(rel, hasMods, hasAssets, fallbackName);
                    if (dstRel == null) continue;
                    string dest = Path.Combine(engineDir, dstRel.Replace('/', '\\'));
                    if (WriteFile(f, dest)) copied++;
                    if (pf != null && (copied % 30) == 0) pf.Report(copied, Math.Max(1, files.Count), "正在写入文件…");
                }
            }
            catch (Exception ex) { err = ex.Message; return false; }
            if (pf != null) pf.Report(copied, Math.Max(1, files.Count), "正在写入文件…");
            return true;
        }

        public static bool CopyZip(ProgressForm pf, string engineDir, string zipPath, string innerRoot, string layerPrefix,
            bool hasMods, bool hasAssets, string fallbackName, out int copied, out string err)
        {
            copied = 0;
            err = "";
            int total = 1;
            try
            {
                using (ZipArchive zip = ZipFile.OpenRead(zipPath))
                {
                    total = Math.Max(1, zip.Entries.Count);
                    string pre = innerRoot.Length == 0 ? "" : innerRoot + "/";
                    foreach (ZipArchiveEntry e in zip.Entries)
                    {
                        if (pf != null && pf.Cancelled) { err = "已取消"; return false; }
                        string n = e.FullName.Replace('\\', '/');
                        if (n.EndsWith("/")) continue;
                        if (pre.Length > 0)
                        {
                            if (!n.StartsWith(pre, StringComparison.OrdinalIgnoreCase)) continue;
                            n = n.Substring(pre.Length);
                        }
                        string dstRel = MapTarget(layerPrefix + n, hasMods, hasAssets, fallbackName);
                        if (dstRel == null) continue;
                        string dest = Path.Combine(engineDir, dstRel.Replace('/', '\\'));
                        string name = Path.GetFileName(dest);
                        if (string.Equals(name, "modsList.txt", StringComparison.OrdinalIgnoreCase) && File.Exists(dest)) continue;
                        string dir = Path.GetDirectoryName(dest);
                        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                        try { e.ExtractToFile(dest, true); copied++; }
                        catch { }
                        if (pf != null && (copied % 30) == 0) pf.Report(copied, total, "正在写入文件…");
                    }
                }
            }
            catch (Exception ex) { err = ex.Message; return false; }
            if (pf != null) pf.Report(copied, total, "正在写入文件…");
            return true;
        }

        // 后台安装 + 进度窗
        public static bool WithProgress(IWin32Window owner, string title, Func<ProgressForm, bool> work)
        {
            bool ok = false;
            using (ProgressForm pf = new ProgressForm(title))
            {
                System.Threading.Thread t = new System.Threading.Thread(delegate()
                {
                    try { ok = work(pf); } catch { ok = false; }
                    if (!pf.Cancelled) pf.Finish(ok);
                });
                t.IsBackground = true;
                t.Start();
                pf.ShowDialog(owner);
                if (pf.Cancelled) { try { t.Join(2000); } catch { } return false; }
                return ok;
            }
        }

        // 弹选择框 + 装一个文件夹包；返回装进了哪个引擎（null = 取消或失败）
        public static string RunFolder(IWin32Window owner, string root, Config cfg, string packDir,
            string layerPrefix, bool hasMods, bool hasAssets, string defName, out string err)
        {
            err = "";
            List<EngineCandidate> engines = FindEngines(root, cfg);
            using (EnginePickForm f = new EnginePickForm(root, cfg, engines, hasMods, hasAssets, defName))
            {
                if (f.ShowDialog(owner) != DialogResult.OK || f.Chosen == null) return null;
                string engineDir = f.Chosen.Dir;
                string engineName = f.Chosen.Name;
                string modName = f.ModName;
                int copied = 0;
                string copyErr = "";
                bool ok = WithProgress(owner, "正在装到 " + engineName + "…", delegate(ProgressForm pf)
                {
                    return CopyFolder(pf, engineDir, packDir, layerPrefix, hasMods, hasAssets, modName, out copied, out copyErr);
                });
                if (!ok)
                {
                    if (!ProgressForm.IsCancelled && copyErr.Length > 0)
                        MessageBox.Show(owner, "安装失败：\r\n" + copyErr, "安装", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    err = copyErr;
                    return null;
                }
                MessageBox.Show(owner, "已装到 " + engineName + "\r\n" + engineDir +
                    (copied > 0 ? "\r\n\r\n写入 " + copied + " 个文件。" : ""), "安装完成",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return engineDir;
            }
        }

        // 弹选择框 + 装一个压缩包里的包；返回装进了哪个引擎（null = 取消或失败）
        public static string RunZip(IWin32Window owner, string root, Config cfg, string zipPath, string innerRoot,
            string layerPrefix, bool hasMods, bool hasAssets, string defName, out string err)
        {
            err = "";
            List<EngineCandidate> engines = FindEngines(root, cfg);
            using (EnginePickForm f = new EnginePickForm(root, cfg, engines, hasMods, hasAssets, defName))
            {
                if (f.ShowDialog(owner) != DialogResult.OK || f.Chosen == null) return null;
                string engineDir = f.Chosen.Dir;
                string engineName = f.Chosen.Name;
                string modName = f.ModName;
                int copied = 0;
                string copyErr = "";
                bool ok = WithProgress(owner, "正在装到 " + engineName + "…", delegate(ProgressForm pf)
                {
                    return CopyZip(pf, engineDir, zipPath, innerRoot, layerPrefix, hasMods, hasAssets, modName, out copied, out copyErr);
                });
                if (!ok)
                {
                    if (!ProgressForm.IsCancelled && copyErr.Length > 0)
                        MessageBox.Show(owner, "安装失败：\r\n" + copyErr, "安装", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    err = copyErr;
                    return null;
                }
                MessageBox.Show(owner, "已装到 " + engineName + "\r\n" + engineDir +
                    (copied > 0 ? "\r\n\r\n写入 " + copied + " 个文件。" : ""), "安装完成",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return engineDir;
            }
        }
    }

    // ===================================================================== 选择引擎
    internal class EnginePickForm : Form
    {
        public EngineCandidate Chosen;
        public string ModName = "";

        private readonly List<EngineCandidate> engines;
        private readonly bool hasMods, hasAssets;
        private readonly string root;
        private readonly Config cfg;
        private readonly TextBox txtName;
        private int sel = -1, hover = -1, scroll;
        private Rectangle listRect;
        private readonly int rowH = Ui.S(46);
        private readonly bool needName;

        public EnginePickForm(string root, Config cfg, List<EngineCandidate> engines, bool hasMods, bool hasAssets, string defName)
        {
            this.root = root;
            this.cfg = cfg;
            this.engines = engines;
            this.hasMods = hasMods;
            this.hasAssets = hasAssets;
            needName = !hasMods && !hasAssets;

            Text = "选择引擎";
            BackColor = Ui.Bar;
            ForeColor = Ui.Txt;
            Font = Ui.FHint;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoScaleMode = AutoScaleMode.None;
            ClientSize = new Size(Ui.S(560), Ui.S(432));
            DoubleBuffered = true;

            sel = EnginePack.GuessIndex(engines, hasMods, hasAssets);
            if (sel >= 0) scroll = Math.Max(0, sel * rowH - rowH);
            listRect = new Rectangle(Ui.S(24), Ui.S(104), Ui.S(512), Ui.S(190));

            txtName = new TextBox();
            txtName.BorderStyle = BorderStyle.FixedSingle;
            txtName.BackColor = Ui.Card;
            txtName.ForeColor = Ui.Txt;
            txtName.Font = Ui.FHint;
            txtName.Text = defName ?? "";
            txtName.SetBounds(Ui.S(24), Ui.S(344), Ui.S(396), Ui.S(26));
            txtName.Visible = needName;
            ModName = defName ?? "";
            Controls.Add(txtName);

            RoundButton bDl = new RoundButton("下载引擎…");
            bDl.Filled = false;
            bDl.Font = Ui.FSmall;
            bDl.SetBounds(Ui.S(24), Ui.S(382), Ui.S(120), Ui.S(34));
            bDl.Click += delegate
            {
                using (DownloadForm dl = new DownloadForm("下载引擎")) dl.ShowDialog(this);
                Reload();
            };
            Controls.Add(bDl);

            RoundButton ok = new RoundButton("安装到选中的引擎");
            ok.SetBounds(Ui.S(292), Ui.S(380), Ui.S(174), Ui.S(38));
            ok.Visible = engines.Count > 0;   // 一个引擎都没有时只留「下载引擎」和「取消」
            ok.Click += delegate { Confirm(); };
            Controls.Add(ok);

            RoundButton cancel = new RoundButton("取消");
            cancel.Filled = false;
            cancel.SetBounds(Ui.S(474), Ui.S(381), Ui.S(62), Ui.S(36));
            cancel.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };
            Controls.Add(cancel);

            AllowDrop = false;
        }

        private void Confirm()
        {
            if (sel < 0 || sel >= engines.Count) return;
            if (needName)
            {
                string n = ZipInstallForm.Clean(txtName.Text.Trim());
                if (n.Length == 0)
                {
                    MessageBox.Show(this, Lang.T("名字不能为空。"), Lang.T("选择引擎"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                ModName = n;
            }
            Chosen = engines[sel];
            DialogResult = DialogResult.OK;
            Close();
        }

        // 下载中心回来后刷新一下（用户可能刚解压了一个引擎进来）
        private void Reload()
        {
            List<EngineCandidate> fresh = EnginePack.FindEngines(root, cfg);
            engines.Clear();
            engines.AddRange(fresh);
            foreach (EngineCandidate c in fresh) c.Icon = Icons.Build(null, c.Exe, Ui.S(34), c.Name);
            if (sel >= engines.Count) sel = engines.Count - 1;
            if (sel < 0 && engines.Count > 0) sel = 0;
            Invalidate();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Ui.DarkTitleBar(Handle);
            Ui.FadeIn(this);
            foreach (EngineCandidate c in engines)
            {
                if (c.Icon == null) c.Icon = Icons.Build(null, c.Exe, Ui.S(34), c.Name);
            }
            if (needName) { txtName.Focus(); txtName.SelectAll(); }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int h = RowAt(e.Y);
            if (h != hover) { hover = h; Cursor = h >= 0 ? Cursors.Hand : Cursors.Default; Invalidate(); }
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
            int i = RowAt(e.Y);
            if (i >= 0) { sel = i; Invalidate(); }
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (RowAt(e.Y) >= 0) Confirm();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            int max = Math.Max(0, engines.Count * rowH - listRect.Height);
            scroll -= e.Delta / 120 * Ui.S(60);
            if (scroll < 0) scroll = 0;
            if (scroll > max) scroll = max;
            Invalidate();
        }

        private int RowAt(int y)
        {
            if (!listRect.Contains(Ui.S(24), y)) return -1;
            int i = (y - listRect.Y + scroll) / rowH;
            if (i < 0 || i >= engines.Count) return -1;
            return i;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Ui.Bar);

            Ui.Text(g, "这个模组需要依附引擎", Ui.FBig, new Rectangle(Ui.S(24), Ui.S(14), Ui.S(500), Ui.S(28)),
                Ui.Txt, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);

            string why;
            if (hasMods && hasAssets)
                why = "包里没有 exe，是 mods 与 assets 结构，会分别合并进所选引擎的 mods 与 assets 文件夹。同名文件会被覆盖，建议先备份。";
            else if (hasMods)
                why = "包里没有 exe，是 mods 结构，会合并进所选引擎的 mods 文件夹。同名文件会被覆盖，建议先备份。";
            else if (hasAssets)
                why = "包里没有 exe，是 assets 结构，会合并进所选引擎的 assets 文件夹。同名文件会被覆盖，建议先备份。";
            else
                why = "包里没有 exe，像是某个引擎的 mod 文件夹，会装进所选引擎的 mods 里。同名文件会被覆盖，建议先备份。";
            Ui.Text(g, why, Ui.FSmall,
                new Rectangle(Ui.S(24), Ui.S(44), Ui.S(512), Ui.S(52)), Ui.Sub,
                TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak);

            // 列表
            Ui.FillRound(g, listRect, Ui.S(8), Ui.Card);
            Ui.StrokeRound(g, listRect, Ui.S(8), Ui.Line, 1f);

            if (engines.Count == 0)
            {
                Ui.Text(g, "识别目录里还没有可用的引擎。\r\n先点「下载引擎」拿一个，解压到识别目录，再回来。", Ui.FSmall,
                    new Rectangle(listRect.X + Ui.S(16), listRect.Y + Ui.S(20), listRect.Width - Ui.S(32), listRect.Height - Ui.S(40)),
                    Ui.Sub, TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak);
            }

            for (int i = 0; i < engines.Count; i++)
            {
                int y = listRect.Y + i * rowH - scroll;
                if (y + rowH < listRect.Y || y > listRect.Bottom) continue;
                Rectangle row = new Rectangle(listRect.X + Ui.S(2), y, listRect.Width - Ui.S(4), rowH);
                if (row.Bottom > listRect.Bottom) row.Height = listRect.Bottom - row.Y;
                bool on = (i == sel);
                if (on || i == hover)
                    Ui.FillRound(g, new Rectangle(row.X + 2, row.Y + 2, row.Width - 4, row.Height - 4), Ui.S(6),
                        on ? Ui.A(Ui.Accent, 26) : Ui.A(Color.White, 8));
                if (on)
                    Ui.FillRound(g, new Rectangle(row.X + Ui.S(4), row.Y + Ui.S(10), Ui.S(3), rowH - Ui.S(20)), Ui.S(2), Ui.Accent);

                EngineCandidate c = engines[i];
                Rectangle ico = new Rectangle(row.X + Ui.S(14), row.Y + (rowH - Ui.S(34)) / 2, Ui.S(34), Ui.S(34));
                if (c.Icon != null)
                {
                    using (System.Drawing.Drawing2D.GraphicsPath p = Ui.Round(ico, Ui.S(7)))
                    {
                        System.Drawing.Drawing2D.GraphicsState st = g.Save();
                        g.SetClip(p);
                        g.DrawImage(c.Icon, ico);
                        g.Restore(st);
                    }
                }
                int tx = ico.Right + Ui.S(12);
                Ui.Text(g, c.Name, Ui.FHint, new Rectangle(tx, row.Y + Ui.S(5), row.Width - (tx - row.X) - Ui.S(12), Ui.S(20)),
                    on ? Ui.Accent : Ui.Txt, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
                Ui.Text(g, c.EngineText + "  ·  " + c.Dir, Ui.FSmall, new Rectangle(tx, row.Y + Ui.S(23), row.Width - (tx - row.X) - Ui.S(12), Ui.S(18)),
                    Ui.Sub, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
            }

            if (engines.Count * rowH > listRect.Height)
            {
                int trackH = listRect.Height - Ui.S(10);
                int max = Math.Max(1, engines.Count * rowH - listRect.Height);
                int th = Math.Max(Ui.S(24), (int)((double)trackH * listRect.Height / (engines.Count * rowH)));
                int top = listRect.Y + Ui.S(5) + (int)((trackH - th) * (scroll / (double)max));
                Ui.FillRound(g, new Rectangle(listRect.Right - Ui.S(8), top, Ui.S(3), th), 2, Ui.A(Color.White, 70));
            }

            if (needName)
            {
                Ui.Text(g, "装到引擎里的名字（作为 mods 下的子文件夹名）：", Ui.FSmall,
                    new Rectangle(Ui.S(24), Ui.S(320), Ui.S(480), Ui.S(20)), Ui.Sub,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            }
            else
            {
                Ui.Text(g, "安装位置：" + (sel >= 0 && sel < engines.Count ? engines[sel].Dir : "（先选一个引擎）"), Ui.FSmall,
                    new Rectangle(Ui.S(24), Ui.S(322), Ui.S(512), Ui.S(20)), Ui.Sub,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
            }
        }
    }
}
