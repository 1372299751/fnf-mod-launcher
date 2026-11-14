// 压缩包安装：识别 zip 内部结构，装到识别目录，并给 mod 命名
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Windows.Forms;

namespace FnfLauncher
{
    internal static class ZipInstall
    {
        public static string LastError = "";

        // 找出压缩包里真正的 mod 根目录 + exe 名
        public static bool Detect(string zipPath, out string innerRoot, out string exeName, out int fileCount)
        {
            innerRoot = "";
            exeName = "";
            fileCount = 0;
            try
            {
                using (ZipArchive zip = ZipFile.OpenRead(zipPath))
                {
                    List<string> entries = new List<string>();
                    foreach (ZipArchiveEntry e in zip.Entries)
                    {
                        if (e.FullName.EndsWith("/")) continue;
                        entries.Add(e.FullName.Replace('\\', '/'));
                    }
                    fileCount = entries.Count;
                    if (entries.Count == 0) { LastError = "压缩包是空的"; return false; }

                    // 找最深一层里带 exe 的方案：取路径最短的 exe，其所在目录就是 mod 根
                    string bestExe = null;
                    foreach (string n in entries)
                    {
                        if (!n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                        if (bestExe == null || n.Length < bestExe.Length) bestExe = n;
                    }

                    if (bestExe != null)
                    {
                        int slash = bestExe.LastIndexOf('/');
                        innerRoot = slash > 0 ? bestExe.Substring(0, slash) : "";
                        exeName = Path.GetFileName(bestExe);
                        return true;
                    }

                    // 没有 exe：找带 lime.ndll / mods / assets 的目录
                    foreach (string n in entries)
                    {
                        string low = n.ToLowerInvariant();
                        if (low.EndsWith("lime.ndll") || low.Contains("/mods/pack.json") || low.EndsWith("/modslist.txt"))
                        {
                            int slash = n.LastIndexOf('/');
                            if (slash > 0)
                            {
                                innerRoot = n.Substring(0, slash);
                                if (innerRoot.EndsWith("/mods")) innerRoot = innerRoot.Substring(0, innerRoot.Length - 5);
                                return true;
                            }
                        }
                    }

                    // 最后兜底：如果所有文件都在同一个顶层文件夹里，就取它
                    string first = null;
                    foreach (string n in entries)
                    {
                        int slash = n.IndexOf('/');
                        string top = slash > 0 ? n.Substring(0, slash) : "";
                        if (first == null) first = top;
                        else if (first != top) { first = null; break; }
                    }
                    if (!string.IsNullOrEmpty(first)) { innerRoot = first; return true; }

                    innerRoot = "";
                    return true;
                }
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                return false;
            }
        }

        // 把 innerRoot 下的内容解到 targetDir（已存在则合并覆盖）
        public static bool Install(string zipPath, string innerRoot, string targetDir, out string err, Func<bool> cancel = null)
        {
            err = "";
            try
            {
                Directory.CreateDirectory(targetDir);
                string prefix = string.IsNullOrEmpty(innerRoot) ? "" : innerRoot + "/";
                int n = 0;
                using (ZipArchive zip = ZipFile.OpenRead(zipPath))
                {
                    foreach (ZipArchiveEntry e in zip.Entries)
                    {
                        string name = e.FullName.Replace('\\', '/');
                        if (name.EndsWith("/")) continue;
                        if (prefix.Length > 0)
                        {
                            if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                            name = name.Substring(prefix.Length);
                        }
                        if (name.Length == 0) continue;
                        string dest = Path.Combine(targetDir, name.Replace('/', '\\'));
                        string dir = Path.GetDirectoryName(dest);
                        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                        if (cancel != null && cancel()) { err = "已取消"; return false; }
                        if (cancel != null && cancel()) { err = "已取消"; return false; }
                        try
                        {
                            e.ExtractToFile(dest, true);
                            n++;
                        }
                        catch { }
                    }
                }
                if (n == 0) { err = "没有解出任何文件（压缩包结构和识别结果不匹配）"; return false; }
                return true;
            }
            catch (Exception ex)
            {
                err = ex.Message;
                return false;
            }
        }
    }

    // 安装前确认：显示识别结果 + 输入 mod 名称（= 文件夹名）
    internal class ZipInstallForm : Form
    {
        public string ModName = "";
        public bool Confirmed;

        private readonly TextBox txt;

        public ZipInstallForm(string zipPath, string innerRoot, string exeName, int fileCount, string targetRoot)
        {
            Text = "从压缩包安装";
            BackColor = Ui.Bar;
            ForeColor = Ui.Txt;
            Font = Ui.FHint;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoScaleMode = AutoScaleMode.None;
            ClientSize = new Size(Ui.S(540), Ui.S(336));
            DoubleBuffered = true;

            string defName = "";
            try
            {
                if (!string.IsNullOrEmpty(innerRoot)) defName = Path.GetFileName(innerRoot.Replace('/', '\\'));
                if (string.IsNullOrEmpty(defName)) defName = Path.GetFileNameWithoutExtension(zipPath);
            }
            catch { }
            ModName = defName;

            Label h = new Label();
            h.AutoSize = false; h.BackColor = Ui.Bar; h.ForeColor = Ui.Txt;
            h.Font = Ui.FBig; h.TextAlign = ContentAlignment.MiddleLeft;
            h.Text = "识别到压缩包内容";
            h.SetBounds(Ui.S(22), Ui.S(14), Ui.S(400), Ui.S(26));

            Label info = new Label();
            info.AutoSize = false; info.BackColor = Ui.Card; info.ForeColor = Ui.Sub;
            info.Font = Ui.FSmall; info.TextAlign = ContentAlignment.TopLeft;
            info.Text = "文件：" + Path.GetFileName(zipPath) + "\r\n"
                      + "内部根目录：" + (string.IsNullOrEmpty(innerRoot) ? "（压缩包根）" : innerRoot) + "\r\n"
                      + "游戏 exe：" + (string.IsNullOrEmpty(exeName) ? "未找到（可能不是游戏目录）" : exeName) + "\r\n"
                      + "文件数量：" + fileCount + "\r\n"
                      + "安装到：" + Path.Combine(targetRoot, defName);
            info.SetBounds(Ui.S(22), Ui.S(46), Ui.S(496), Ui.S(96));

            Label ln = new Label();
            ln.AutoSize = false; ln.BackColor = Ui.Bar; ln.ForeColor = Ui.Sub;
            ln.Font = Ui.FSmall; ln.TextAlign = ContentAlignment.MiddleLeft;
            ln.Text = "给这个模组起个名字（同时作为文件夹名）：";
            ln.SetBounds(Ui.S(22), Ui.S(154), Ui.S(400), Ui.S(20));

            txt = new TextBox();
            txt.BorderStyle = BorderStyle.FixedSingle;
            txt.BackColor = Ui.Card;
            txt.ForeColor = Ui.Txt;
            txt.Font = Ui.FHint;
            txt.Text = defName;
            txt.SetBounds(Ui.S(22), Ui.S(178), Ui.S(496), Ui.S(26));

            // 两个小按钮：一键用「文件夹名」或「exe 名」填名字
            RoundButton bFromFolder = new RoundButton("以文件夹名称命名");
            bFromFolder.Filled = false;
            bFromFolder.Font = Ui.FSmall;
            bFromFolder.SetBounds(Ui.S(22), Ui.S(212), Ui.S(150), Ui.S(28));
            bFromFolder.Click += delegate
            {
                string n = "";
                try
                {
                    if (!string.IsNullOrEmpty(innerRoot)) n = Path.GetFileName(innerRoot.Replace('/', '\\'));
                    if (string.IsNullOrEmpty(n)) n = Path.GetFileNameWithoutExtension(zipPath);
                }
                catch { }
                if (n.Length > 0) txt.Text = Clean(n);
            };

            RoundButton bFromExe = new RoundButton("以 exe 文件命名");
            bFromExe.Filled = false;
            bFromExe.Font = Ui.FSmall;
            bFromExe.SetBounds(Ui.S(182), Ui.S(212), Ui.S(140), Ui.S(28));
            bFromExe.Click += delegate
            {
                string n = "";
                try { if (!string.IsNullOrEmpty(exeName)) n = Path.GetFileNameWithoutExtension(exeName); } catch { }
                if (n.Length == 0)
                {
                    MessageBox.Show(this, Lang.T("这个压缩包里没找到 exe，无法用 exe 命名。"), Lang.T("从压缩包安装"),
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                txt.Text = Clean(n);
            };
            RoundButton ok = new RoundButton("开始安装");
            ok.SetBounds(Ui.S(318), Ui.S(276), Ui.S(120), Ui.S(38));
            ok.Click += delegate
            {
                string name = txt.Text.Trim();
                if (name.Length == 0) { MessageBox.Show(this, Lang.T("名字不能为空。"), Lang.T("安装"), MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
                ModName = Clean(name);
                Confirmed = true;
                DialogResult = DialogResult.OK;
                Close();
            };

            RoundButton cancel = new RoundButton("取消");
            cancel.Filled = false;
            cancel.SetBounds(Ui.S(196), Ui.S(276), Ui.S(112), Ui.S(38));
            cancel.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };

            Controls.Add(h); Controls.Add(info); Controls.Add(ln); Controls.Add(txt);
            Controls.Add(bFromFolder); Controls.Add(bFromExe);
            Controls.Add(ok); Controls.Add(cancel);
        }

        public static string Clean(string name)
        {
            char[] bad = Path.GetInvalidFileNameChars();
            string r = name;
            foreach (char c in bad) r = r.Replace(c, '_');
            r = r.Trim().TrimEnd('.');
            return r.Length == 0 ? "新模组" : r;
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Ui.DarkTitleBar(Handle);
            Ui.FadeIn(this);
            txt.Focus();
            txt.SelectAll();
        }
    }

    // 极简输入框（用于重命名文件夹）
    internal class TextPrompt : Form
    {
        public string Value = "";
        private readonly TextBox box;

        public TextPrompt(string title, string label, string def)
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
            ClientSize = new Size(Ui.S(460), Ui.S(160));

            Label l = new Label();
            l.AutoSize = false; l.BackColor = Ui.Bar; l.ForeColor = Ui.Sub;
            l.Font = Ui.FSmall; l.TextAlign = ContentAlignment.MiddleLeft;
            l.Text = label;
            l.SetBounds(Ui.S(20), Ui.S(16), Ui.S(420), Ui.S(20));

            box = new TextBox();
            box.BorderStyle = BorderStyle.FixedSingle;
            box.BackColor = Ui.Card;
            box.ForeColor = Ui.Txt;
            box.Font = Ui.FHint;
            box.Text = def;
            box.SetBounds(Ui.S(20), Ui.S(42), Ui.S(420), Ui.S(26));

            RoundButton ok = new RoundButton("确定");
            ok.SetBounds(Ui.S(252), Ui.S(96), Ui.S(90), Ui.S(34));
            ok.Click += delegate
            {
                string v = box.Text.Trim();
                if (v.Length == 0) return;
                Value = v;
                DialogResult = DialogResult.OK;
                Close();
            };

            RoundButton cancel = new RoundButton("取消");
            cancel.Filled = false;
            cancel.SetBounds(Ui.S(350), Ui.S(96), Ui.S(90), Ui.S(34));
            cancel.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };

            Controls.Add(l); Controls.Add(box); Controls.Add(ok); Controls.Add(cancel);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Ui.DarkTitleBar(Handle);
            box.Focus();
            box.SelectAll();
        }
    }    internal static class ArchiveInstall
    {
        public static bool Prompt(IWin32Window owner, string path, string targetRoot, out string installedPath)
        {
            installedPath = null;
            string ext = "";
            try { ext = Path.GetExtension(path).ToLowerInvariant(); } catch { }
            if (ext != ".zip" && ext != ".rar" && ext != ".7z")
            {
                MessageBox.Show(owner, "压缩包格式不支持，仅支持 .zip / .rar / .7z。\r\n\r\n" +
                                       "其他格式（.tar / .gz / .iso 等）请先解压成文件夹再拖进来。",
                    "不支持的格式", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }

            string temp = Path.Combine(Path.GetTempPath(), "fnf-unpack-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                string innerRoot = "";
                string exeName = "";
                int count = 0;
                string sourceDir = null;   // rar/7z 解包后的目录

                if (ext == ".zip")
                {
                    if (!ZipInstall.Detect(path, out innerRoot, out exeName, out count))
                    {
                        MessageBox.Show(owner, "这个压缩包读不出来：\r\n" + ZipInstall.LastError, "安装",
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return false;
                    }
                }
                else
                {
                    string tool = FindExtractor();
                    if (tool == null)
                    {
                        MessageBox.Show(owner, "解压 " + ext + " 需要系统里装有 7-Zip、WinRAR 或 Bandizip。\r\n\r\n" +
                                               "装好之后重试，或者手动解压成文件夹再拖进来。",
                            "缺少解压工具", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return false;
                    }
                    Directory.CreateDirectory(temp);
                    if (!RunExtract(tool, path, temp))
                    {
                        MessageBox.Show(owner, "解压失败，可能是压缩包损坏或需要密码。", "安装",
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return false;
                    }
                    sourceDir = temp;
                    FindRoot(sourceDir, out innerRoot, out exeName, out count);
                }

                if (string.IsNullOrEmpty(exeName))
                {
                    // 没有 exe：可能是依附引擎的包（只有 mods\ / assets\），先问装到哪个引擎
                    string packPrefix = "";
                    bool packMods = false, packAssets = false, dependent = false;
                    string packDir = null;
                    if (ext == ".zip")
                    {
                        dependent = EnginePack.InspectZip(path, innerRoot, out packPrefix, out packMods, out packAssets);
                    }
                    else
                    {
                        packDir = string.IsNullOrEmpty(innerRoot) ? sourceDir : Path.Combine(sourceDir, innerRoot.Replace('/', (char)92));
                        if (!Directory.Exists(packDir)) packDir = sourceDir;
                        dependent = EnginePack.InspectFolder(packDir, out packPrefix, out packMods, out packAssets);
                    }
                    if (dependent)
                    {
                        string defName = ZipInstallForm.Clean(string.IsNullOrEmpty(innerRoot)
                            ? Path.GetFileNameWithoutExtension(path)
                            : Path.GetFileName(innerRoot.Replace('/', (char)92)));
                        Config cfg2 = null;
                        try { cfg2 = new Config(Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "launcher.ini")); }
                        catch { }
                        string packErr = "";
                        string engineDir = (ext == ".zip")
                            ? EnginePack.RunZip(owner, targetRoot, cfg2, path, innerRoot, packPrefix, packMods, packAssets, defName, out packErr)
                            : EnginePack.RunFolder(owner, targetRoot, cfg2, packDir, packPrefix, packMods, packAssets, defName, out packErr);
                        if (engineDir != null) { installedPath = engineDir; return true; }
                        return false;
                    }
                }

                using (ZipInstallForm zf = new ZipInstallForm(path, innerRoot, exeName, count, targetRoot))
                {
                    if (zf.ShowDialog(owner) != DialogResult.OK) return false;
                    string target = Path.Combine(targetRoot, zf.ModName);
                    if (Directory.Exists(target) && Directory.GetFileSystemEntries(target).Length > 0)
                    {
                        if (MessageBox.Show(owner, "「" + zf.ModName + "」已经存在，要合并覆盖吗？", "安装",
                                MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return false;
                    }

                    int expect = (ext == ".zip") ? count : CountFiles(sourceDir);
                    bool targetWasNew = !Directory.Exists(target) || Directory.GetFileSystemEntries(target).Length == 0;
                    bool ok = InstallWithProgress(owner, "正在安装…", expect, target, targetWasNew, delegate()
                    {
                        if (ext == ".zip")
                        {
                            string e2;
                            return ZipInstall.Install(path, innerRoot, target, out e2, delegate { return ProgressForm.IsCancelled; });
                        }
                        string src2 = string.IsNullOrEmpty(innerRoot) ? sourceDir : Path.Combine(sourceDir, innerRoot.Replace('/', (char)92));
                        if (!Directory.Exists(src2)) src2 = sourceDir;
                        CopyTree(src2, target, delegate { return ProgressForm.IsCancelled; });
                        return true;
                    });
                    if (!ProgressForm.IsCancelled && !ok)
                    {
                        return false;
                    }
                    installedPath = target;
                    MessageBox.Show(owner, "已安装到：\r\n" + target, "安装完成",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return true;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(owner, "安装失败：\r\n" + ex.Message, "安装", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
            finally
            {
                try { if (Directory.Exists(temp)) Directory.Delete(temp, true); } catch { }
            }
        }

        private static string FindExtractor()
        {
            List<string> cands = new List<string>();
            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            foreach (string b in new string[] { pf, pf86 })
            {
                if (string.IsNullOrEmpty(b)) continue;
                cands.Add(Path.Combine(b, "7-Zip", "7z.exe"));
                cands.Add(Path.Combine(b, "7-Zip", "7za.exe"));
                cands.Add(Path.Combine(b, "WinRAR", "UnRAR.exe"));
                cands.Add(Path.Combine(b, "WinRAR", "Rar.exe"));
                cands.Add(Path.Combine(b, "Bandizip", "bz.exe"));
            }
            try
            {
                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (!string.IsNullOrEmpty(local))
                {
                    cands.Add(Path.Combine(local, "Bandizip", "bz.exe"));
                    cands.Add(Path.Combine(local, "Programs", "Bandizip", "bz.exe"));
                }
            }
            catch { }

            string pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (string dir in pathVar.Split(';'))
            {
                if (string.IsNullOrEmpty(dir)) continue;
                foreach (string n in new string[] { "7z.exe", "7za.exe", "7zz.exe", "unrar.exe", "bz.exe" })
                    cands.Add(Path.Combine(dir.Trim(), n));
            }
            foreach (string c in cands)
            {
                try { if (File.Exists(c)) return c; }
                catch { }
            }
            return null;
        }

        private static bool RunExtract(string tool, string archive, string outDir)
        {
            try
            {
                string exeName = Path.GetFileName(tool).ToLowerInvariant();
                string args;
                if (exeName.StartsWith("unrar"))
                    args = "x -y \"" + archive + "\" \"" + outDir + "\\\"";
                else if (exeName.StartsWith("bz"))
                    args = "x -y -o:\"" + outDir + "\" \"" + archive + "\"";      // Bandizip CLI
                else
                    args = "x -y -o\"" + outDir + "\" \"" + archive + "\"";        // 7-Zip
                System.Diagnostics.ProcessStartInfo psi = new System.Diagnostics.ProcessStartInfo(tool, args);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                using (System.Diagnostics.Process p = System.Diagnostics.Process.Start(psi))
                {
                    if (p == null) return false;
                    p.WaitForExit(120000);
                    return p.HasExited && p.ExitCode == 0;
                }
            }
            catch { return false; }
        }

        // 从解压出来的目录里找 mod 根（含 exe 的那一层）
        private static void FindRoot(string dir, out string innerRoot, out string exeName, out int count)
        {
            innerRoot = "";
            exeName = "";
            count = 0;
            try
            {
                string[] exes = Directory.GetFiles(dir, "*.exe", SearchOption.AllDirectories);
                count = Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Length;
                string best = null;
                foreach (string e in exes)
                {
                    if (best == null || e.Length < best.Length) best = e;
                }
                if (best != null)
                {
                    exeName = Path.GetFileName(best);
                    string d = Path.GetDirectoryName(best);
                    if (d != null && !string.Equals(d.TrimEnd('\\'), dir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                        innerRoot = d.Substring(dir.TrimEnd('\\').Length).TrimStart('\\');
                    return;
                }
                string[] dirs = Directory.GetDirectories(dir);
                if (dirs.Length == 1) innerRoot = Path.GetFileName(dirs[0]);
            }
            catch { }
        }

        private static void CopyTree(string src, string dst, Func<bool> cancel = null)
        {
            if (cancel != null && cancel()) return;
            Directory.CreateDirectory(dst);
            foreach (string f in Directory.GetFiles(src))
            {
                if (cancel != null && cancel()) return;
                try { File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), true); }
                catch { }
            }
            foreach (string d in Directory.GetDirectories(src))
            {
                try { CopyTree(d, Path.Combine(dst, Path.GetFileName(d)), cancel); }
                catch { }
            }
        }

        private static int CountFiles(string dir)
        {
            try { return Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Length; }
            catch { return 0; }
        }

        // 后台安装 + 进度窗：进度用"目标目录里已有多少文件"来算；点取消会中断并删掉残余
        private static bool InstallWithProgress(IWin32Window owner, string title, int expect, string targetDir, bool targetWasNew, Func<bool> work)
        {
            using (ProgressForm pf = new ProgressForm(title))
            {
            {
                bool ok = false;
                System.Threading.Thread t = new System.Threading.Thread(delegate()
                {
                    try { ok = work(); } catch { ok = false; }
                    if (!pf.Cancelled) pf.Finish(ok);
                });
                t.IsBackground = true;
                t.Start();

                Timer poll = new Timer();
                poll.Interval = 150;
                poll.Tick += delegate
                {
                    if (pf.Cancelled) return;
                    int have = CountFiles(targetDir);
                    if (expect <= 0) expect = Math.Max(1, have);
                    pf.Report(Math.Min(have, expect), expect, "正在写入文件…");
                };
                poll.Start();
                pf.ShowDialog(owner);
                poll.Stop();
                poll.Dispose();

                if (pf.Cancelled)
                {
                    try { t.Join(3000); } catch { }
                    if (targetWasNew && Directory.Exists(targetDir))
                    {
                        try { Directory.Delete(targetDir, true); } catch { }
                    }
                    return false;
                }
                return ok;
            }
        }
    }
}
}
