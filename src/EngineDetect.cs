// 引擎识别 / Engine detection
// 思路（三步，逐层收紧）：
//   1) 文件夹结构指纹：mods/ plugins/ assets/ bin/ manifest/ lime.ndll 的组合
//   2) exe 字符串池签名：Psych Engine / NovaFlare / Codename / Kade / Forever / FNF ...
//   3) 版本号定位：找到引擎的版本变量名（如 psychEngineVersion）在字符串池里的位置，
//      再看哪个“已知引擎版本号”离它最近 —— 比“猜最大的数字”稳得多。

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace FnfLauncher
{
    
    internal class EngineResult
    {
        public string Name = "未知引擎";
        public string Version = "";     // 引擎版本号，可能为空
        public string Note = "";        // 补充说明（例如 Psych 内核版本）
        public string Tech = "";        // 技术栈（HaxeFlixel / Lime）
        public string Family = "";      // psych / novaflare / codename / vanilla / unknown
        public string Source = "";      // 判定依据

        public string Display
        {
            get
            {
                if (Version == null || Version.Length == 0) return Name;
                return Name + " " + Version;
            }
        }
    }

    
    internal static class EngineDetector
    {
        // Psych 系列公开过的版本号，用来在字符串池里“认领”最近的版本字面量
        private static readonly string[] PsychVersions =
        {
            "0.4.2", "0.5.0", "0.5.1", "0.5.2", "0.6.0", "0.6.1", "0.6.2", "0.6.3",
            "0.7.0", "0.7.1", "0.7.2", "0.7.3", "0.7.4",
            "1.0.0", "1.0.1", "1.0.2", "1.0.3", "1.0.4"
        };

        private class Signature
        {
            public string Keys;
            public string Name;
            public string Family;
            public string Anchor;

            public Signature(string keys, string name, string family, string anchor)
            {
                Keys = keys; Name = name; Family = family; Anchor = anchor;
            }
        }

        // 顺序很重要：越具体的越靠前（NovaFlare 里同样含 Psych 的字符串）
        private static readonly Signature[] Sigs =
        {
            new Signature("novaflare|nova flare|novaFlareEngineVersion", "NovaFlare Engine", "novaflare", "novaFlareEngineVersion"),
            new Signature("codename engine|codenameengine|codenameengineversion", "Codename Engine", "codename", "codenameEngineVersion"),
            new Signature("forever engine|foreverengine", "Forever Engine", "forever", "engineVersion"),
            new Signature("andromeda engine|andromedaengine", "Andromeda Engine", "andromeda", "engineVersion"),
            new Signature("kade engine|kadeengine", "Kade Engine", "kade", "engineVersion"),
            new Signature("fps plus|fpsplus", "FPS Plus", "fpsplus", "engineVersion"),
            new Signature("psych engine|psychengine|psychEngineVersion", "Psych Engine", "psych", "psychEngineVersion"),
            new Signature("friday night funkin|funkin'|funkin", "原版 FNF", "vanilla", "engineVersion")
        };

        public static EngineResult Detect(string dir, string exe)
        {
            EngineResult r = new EngineResult();

            // ---------- 1. 结构指纹
            bool hasMods = SafeDir(dir, "mods");
            bool hasPlugins = SafeDir(dir, "plugins");
            bool hasAssets = SafeDir(dir, "assets");
            bool hasBin = SafeDir(dir, "bin");
            bool hasManifest = SafeDir(dir, "manifest");
            bool hasLimeDll = SafeFile(dir, "lime.ndll");

            string exeName = "";
            try { exeName = Path.GetFileNameWithoutExtension(exe) ?? ""; } catch { }

            // ---------- 2. exe 字符串池
            string text = "";
            try
            {
                FileInfo fi = new FileInfo(exe);
                if (fi.Length > 0 && fi.Length <= 400L * 1024 * 1024)
                {
                    byte[] data = File.ReadAllBytes(exe);
                    text = Encoding.GetEncoding(28591).GetString(data);   // Latin-1：一字节一字符，不丢信息
                }
            }
            catch { }

            // ---------- 3. 命名/签名判定
            Signature hit = null;
            foreach (Signature s in Sigs)
            {
                string[] keys = s.Keys.Split('|');
                bool found = false;
                foreach (string k in keys)
                {
                    if (k.Length == 0) continue;
                    if (text.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0) { found = true; break; }
                }
                if (!found && exeName.Length > 0)
                {
                    foreach (string k in keys)
                    {
                        if (k.Length < 4) continue;
                        if (exeName.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0) { found = true; break; }
                    }
                }
                if (found) { hit = s; break; }
            }

            if (hit != null)
            {
                r.Name = hit.Name;
                r.Family = hit.Family;
                r.Source = "exe 字符串命中 " + hit.Name;
            }
            else
            {
                // ---------- 4. 结构兜底
                if (hasMods && hasPlugins) { r.Name = "Psych Engine"; r.Family = "psych"; r.Source = "目录结构（mods + plugins）"; }
                else if (hasMods && hasAssets) { r.Name = "Psych Engine"; r.Family = "psych"; r.Source = "目录结构（mods + assets）"; }
                else if (hasBin && hasManifest && hasLimeDll) { r.Name = "Haxe 引擎"; r.Family = "haxe"; r.Source = "目录结构（bin + manifest + lime）"; }
                else if (hasAssets) { r.Name = "FNF 引擎"; r.Family = "vanilla"; r.Source = "目录结构（assets）"; }
                else { r.Name = "未知引擎"; r.Family = "unknown"; r.Source = "无匹配特征"; }
            }

            // ---------- 5. 技术栈
            bool openfl = text.IndexOf("openfl", StringComparison.OrdinalIgnoreCase) >= 0;
            bool lime = text.IndexOf("lime", StringComparison.OrdinalIgnoreCase) >= 0;
            bool haxe = text.IndexOf("haxe", StringComparison.OrdinalIgnoreCase) >= 0;
            if (openfl && lime) r.Tech = "HaxeFlixel · OpenFL/Lime";
            else if (haxe) r.Tech = "Haxe";
            else if (hasLimeDll) r.Tech = "Lime 运行时";

            // ---------- 6. 版本号
            string anchorKey = hit != null ? hit.Anchor : "engineVersion";
            int anchor = text.Length > 0 ? text.IndexOf(anchorKey, StringComparison.OrdinalIgnoreCase) : -1;
            if (anchor < 0 && text.Length > 0)
                anchor = text.IndexOf("psychEngineVersion", StringComparison.OrdinalIgnoreCase);

            string best = null;
            long bestDist = long.MaxValue;
            for (int i = 0; i < PsychVersions.Length; i++)
            {
                string v = PsychVersions[i];
                int at = text.Length > 0 ? text.IndexOf(v, StringComparison.Ordinal) : -1;
                if (at < 0) continue;
                if (anchor < 0) { if (best == null) best = v; continue; }   // 没锚点就取第一个命中的
                long d = Math.Abs((long)at - anchor);
                if (d < bestDist) { bestDist = d; best = v; }
            }
            if (best != null && (anchor < 0 || bestDist <= 200000))
            {
                r.Version = best;
                if (r.Family == "novaflare") r.Note = "Psych 内核 " + best;
                else if (r.Family != "psych" && r.Family != "unknown") r.Note = "版本按字符串池定位（" + BestSource(r, best) + "）";
            }

            // ---------- 7. 引擎明确的版本变量名（Psych 0.7+ 会把版本写进 psychengine 标识）
            if (r.Family == "psych" && (r.Version == null || r.Version.Length == 0) && hasMods && hasPlugins && hasManifest)
                r.Note = "版本未标注";

            if (r.Version == null) r.Version = "";
            if (r.Note == null) r.Note = "";
            return r;
        }

        private static string BestSource(EngineResult r, string v)
        {
            return r.Name + " " + v;
        }

        private static bool SafeDir(string dir, string name)
        {
            try { return Directory.Exists(Path.Combine(dir, name)); } catch { return false; }
        }

        private static bool SafeFile(string dir, string name)
        {
            try { return File.Exists(Path.Combine(dir, name)); } catch { return false; }
        }

        // 文件夹里的曲目数量（songs 目录下的子文件夹）
        public static int CountSongs(string dir, string exeDir)
        {
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int n = 0;
            AddSongs(seen, Path.Combine(dir, "assets", "songs"), ref n);
            AddSongs(seen, Path.Combine(dir, "mods", "songs"), ref n);
            if (exeDir != null && exeDir.Length > 0)
            {
                AddSongs(seen, Path.Combine(exeDir, "assets", "songs"), ref n);
                AddSongs(seen, Path.Combine(exeDir, "mods", "songs"), ref n);
            }
            return n;
        }

        private static void AddSongs(HashSet<string> seen, string path, ref int n)
        {
            try
            {
                if (!Directory.Exists(path)) return;
                foreach (string d in Directory.GetDirectories(path))
                    if (seen.Add(Path.GetFileName(d))) n++;
            }
            catch { }
        }

        private static int CountSongDirs(string path)
        {
            try
            {
                if (!Directory.Exists(path)) return 0;
                return Directory.GetDirectories(path).Length;
            }
            catch { return 0; }
        }

        // 一次遍历拿到：总大小 / 文件数 / 最近修改时间
        public static long DirStats(string dir, out int files, out long maxWriteTicks, int budget)
        {
            long total = 0;
            int count = 0;
            long newest = 0;
            Stack<string> stack = new Stack<string>();
            stack.Push(dir);
            while (stack.Count > 0 && count < budget)
            {
                string d = stack.Pop();
                try
                {
                    foreach (string f in Directory.GetFiles(d))
                    {
                        try
                        {
                            FileInfo fi = new FileInfo(f);
                            total += fi.Length;
                            long w = fi.LastWriteTimeUtc.Ticks;
                            if (w > newest) newest = w;
                        }
                        catch { }
                        count++;
                        if (count >= budget) break;
                    }
                }
                catch { }
                try { foreach (string sub in Directory.GetDirectories(d)) stack.Push(sub); }
                catch { }
            }
            files = count;
            maxWriteTicks = newest;
            return total;
        }

        // 模组目录总大小（只统计文件大小，遇到没权限的目录跳过）
        public static long DirSize(string dir, int fileBudget)
        {
            long total = 0;
            int count = 0;
            Stack<string> stack = new Stack<string>();
            stack.Push(dir);
            while (stack.Count > 0 && count < fileBudget)
            {
                string d = stack.Pop();
                try
                {
                    foreach (string f in Directory.GetFiles(d))
                    {
                        try { total += new FileInfo(f).Length; } catch { }
                        count++;
                        if (count >= fileBudget) break;
                    }
                }
                catch { }
                try
                {
                    foreach (string sub in Directory.GetDirectories(d)) stack.Push(sub);
                }
                catch { }
            }
            return total;
        }
    }
}
