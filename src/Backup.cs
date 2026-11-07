// 存档备份：启动前自动备份，资料窗里可手动备份 / 还原
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;

namespace FnfLauncher
{
    internal static class SaveBackup
    {
        // 备份放在 exe 同级的 backups\（以前在 launcher\backups，首次访问会自动搬过来）
        public static string Root
        {
            get
            {
                EnsureMigrated();
                return Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "backups");
            }
        }

        private static bool migrated;

        // 把旧位置 launcher\backups 里的快照搬到新位置（只搬一次，合并同名目录，不覆盖已有文件）
        private static void EnsureMigrated()
        {
            if (migrated) return;
            migrated = true;
            try
            {
                string exeDir = Path.GetDirectoryName(Application.ExecutablePath);
                string oldDir = Path.Combine(exeDir, "launcher", "backups");
                string newDir = Path.Combine(exeDir, "backups");
                if (!Directory.Exists(oldDir)) return;
                if (string.Equals(oldDir, newDir, StringComparison.OrdinalIgnoreCase)) return;

                Directory.CreateDirectory(newDir);
                foreach (string modDir in Directory.GetDirectories(oldDir))
                {
                    string target = Path.Combine(newDir, Path.GetFileName(modDir));
                    foreach (string snap in Directory.GetDirectories(modDir))
                    {
                        string dst = Path.Combine(target, Path.GetFileName(snap));
                        if (Directory.Exists(dst)) continue;
                        try
                        {
                            Directory.CreateDirectory(target);
                            CopyDir(snap, dst);
                        }
                        catch { }
                    }
                }
                try { Directory.Delete(oldDir, true); } catch { }
            }
            catch { }
        }

        public static string DirFor(ModEntry m)
        {
            return Path.Combine(Root, Safe(m.Name));
        }

        private static string Safe(string s)
        {
            if (string.IsNullOrEmpty(s)) return "mod";
            char[] bad = Path.GetInvalidFileNameChars();
            string r = s;
            foreach (char c in bad) r = r.Replace(c, '_');
            r = r.Trim();
            return r.Length > 60 ? r.Substring(0, 60) : r;
        }

        // 快照列表（新→旧）
        public static string[] Snapshots(ModEntry m)
        {
            try
            {
                string d = DirFor(m);
                if (!Directory.Exists(d)) return new string[0];
                string[] all = Directory.GetDirectories(d);
                Array.Sort(all, StringComparer.OrdinalIgnoreCase);
                Array.Reverse(all);
                return all;
            }
            catch { return new string[0]; }
        }

        public static long SnapshotSize(string dir)
        {
            long n = 0;
            try
            {
                foreach (string f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                    n += new FileInfo(f).Length;
            }
            catch { }
            return n;
        }

        public static string Backup(ModEntry m, int keep)
        {
            try
            {
                string src = SavePaths.Find(m);
                if (src == null || !Directory.Exists(src)) return null;
                if (Directory.GetFiles(src, "*", SearchOption.AllDirectories).Length == 0) return null;

                string dst = Path.Combine(DirFor(m), DateTime.Now.ToString("yyyyMMdd-HHmmss"));
                CopyDir(src, dst);

                string[] all = Snapshots(m);
                for (int i = keep; i < all.Length; i++)
                {
                    try { Directory.Delete(all[i], true); }
                    catch { }
                }
                return dst;
            }
            catch { return null; }
        }

        public static bool Restore(ModEntry m, string snapshot)
        {
            try
            {
                string dst = SavePaths.Find(m);
                if (dst == null) dst = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                                                    Path.GetFileNameWithoutExtension(m.Exe));
                if (!Directory.Exists(snapshot)) return false;
                if (Directory.Exists(dst))
                {
                    foreach (string f in Directory.GetFiles(dst, "*", SearchOption.AllDirectories))
                    {
                        try { File.Delete(f); } catch { }
                    }
                }
                CopyDir(snapshot, dst);
                return true;
            }
            catch { return false; }
        }

        private static void CopyDir(string src, string dst)
        {
            Directory.CreateDirectory(dst);
            foreach (string f in Directory.GetFiles(src))
            {
                try { File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), true); }
                catch { }
            }
            foreach (string d in Directory.GetDirectories(src))
            {
                try { CopyDir(d, Path.Combine(dst, Path.GetFileName(d))); }
                catch { }
            }
        }

        public static void OpenDir(ModEntry m)
        {
            try
            {
                string d = DirFor(m);
                Directory.CreateDirectory(d);
                Process.Start(new ProcessStartInfo(d) { UseShellExecute = true });
            }
            catch { }
        }
    }
}
