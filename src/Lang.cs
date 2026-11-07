// 界面多语言：中文原文当 key，缺译文自动回退中文
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace FnfLauncher
{
    internal static class Lang
    {
        public static readonly string[] Codes = { "zh", "en" };
        public static readonly string[] Names = { "中文", "English" };

        public static string Current = "zh";

        private static readonly Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.Ordinal);
        private static string[] keyArr = new string[0];
        private static readonly Dictionary<string, string> memo = new Dictionary<string, string>(StringComparer.Ordinal);
        private static Timer sweep;
        private static bool sweeping;

        public static bool Active { get { return map.Count > 0; } }

        public static int Index
        {
            get
            {
                for (int i = 0; i < Codes.Length; i++) if (Codes[i] == Current) return i;
                return 0;
            }
        }

        public static void Set(string code)
        {
            string c = (code ?? "").Trim().ToLowerInvariant();
            bool ok = false;
            foreach (string k in Codes) if (k == c) ok = true;
            Current = ok ? c : "zh";
            map.Clear();
            memo.Clear();
            keyArr = new string[0];
            if (Current == "zh") return;   // 中文直接用源码里的中文字面量，不需要字典
            string[] keys = LangData.Key;
            string[] vals = LangData.En;
            if (keys == null || vals == null) return;
            int n = Math.Min(keys.Length, vals.Length);
            for (int i = 0; i < n; i++)
            {
                if (string.IsNullOrEmpty(keys[i]) || string.IsNullOrEmpty(vals[i])) continue;
                map[keys[i]] = vals[i];
            }
            List<string> ks = new List<string>(map.Keys);
            ks.Sort(delegate(string a, string b) { return b.Length.CompareTo(a.Length); });
            keyArr = ks.ToArray();
            {
            }
        }

        // 核心：查表。没有译文就原样返回中文；查不到再试"数字+单位"的规则。
        public static string T(string s)
        {
            if (string.IsNullOrEmpty(s) || map.Count == 0) return s;
            string r;
            if (map.TryGetValue(s, out r)) return r;
            if (s.IndexOf('\n') >= 0)
            {
                string[] parts = s.Split('\n');
                for (int i = 0; i < parts.Length; i++) parts[i] = T(parts[i].TrimEnd('\r'));
                return string.Join("\r\n", parts);   // 用 CRLF 还原，避免 GDI 把单个 \n 当成不换行
            }
            string pre = Prefix(s);
            if (pre != null) return pre;
            string fr = Frags_(s);
            if (fr != s) return fr;
            return Pattern(s);
        }

        // 动态拼接的文案（"23 首"、"12 张"、"3 分钟前"）用规则翻
        private static readonly string[,] Rules =
        {
            { @"^(\d+) 首$",            " songs" },
            { @"^(\d+) 张$",            " images" },
            { @"^(\d+) 个文件 · $",     " files · " },
            { @"^(\d+) 个文件$",        " files" },
            { @"^(\d+) 个$",            " items" },
            { @"^(\d+) 次$",            " times" },
            { @"^(\d+) 份，最近 $",     " copies, latest " },
            { @"^(\d+) 分钟$",          " min" },
            { @"^(\d+) 分钟前$",        " min ago" },
            { @"^(\d+) 小时$",          " h" },
            { @"^(\d+) 小时前$",        " h ago" },
            { @"^(\d+) 天前$",          " days ago" },
            { @"^(\d+) 秒$",            " s" },
            { @"^(\d+) 秒前$",          " s ago" }
        };

        private static readonly System.Text.RegularExpressions.Regex[] RuleRe = BuildRules();
        private static System.Text.RegularExpressions.Regex[] BuildRules()
        {
            System.Text.RegularExpressions.Regex[] a = new System.Text.RegularExpressions.Regex[Rules.GetLength(0)];
            for (int i = 0; i < a.Length; i++) a[i] = new System.Text.RegularExpressions.Regex(Rules[i, 0]);
            return a;
        }

        // 拼接出来的文案（"启动器缓存（4 个文件 · 0 KB）"）用最长前缀兜底翻译
        private static string Prefix(string s)
        {
            string hit;
            if (memo.TryGetValue(s, out hit)) return hit;
            string result = null;
            int max = Math.Min(keyArr.Length, 600);
            for (int i = 0; i < max; i++)
            {
                string k = keyArr[i];
                if (k == null || k.Length < 2 || s.Length <= k.Length) continue;
                if (string.CompareOrdinal(s, 0, k, 0, k.Length) != 0) continue;
                result = map[k] + Frags_(s.Substring(k.Length));
                break;
            }
            memo[s] = result;
            return result;
        }

        // 拼接残留的碎片替换（"4 个"、"30 个文件，约 "、"　·　"）
        private static readonly string[,] Frags =
        {
            { " 个文件，约 ", " files, ~" },
            { " 个文件", " files" },
            { " 个本地模组", " local mods" },
            { " 个网页版", " web entries" },
            { " 个", "" },
            { "，约 ", ", ~" },
            { "　·　", " · " },
            { "识别目录", "mods folder" },
            { "目录下的 ", " folder: " },
            { " 首 · 原版 ", " songs · vanilla " },
            { "自制 ", "Custom " },
            { " 首", " songs" },
            { "总时长 ", "total " },
            { " 分钟前", " min ago" },
            { " 分钟", " min" },
            { " 小时前", " h ago" },
            { " 天前", " days ago" },
            { "视频 ", "videos " },
            { "截图 ", "shots " },
            { "  ·  ", " · " },
            { " 小时 ", " h " },
            { " 小时前", " h ago" },
            { " 分", " min" },
            { "（原版 ", " (vanilla " },
            { "）· 总音符 ", ") · notes " },
            { "已有 ", "Existing " },
            { " 份，最近 ", " copies, latest " },
            { "存档目录：", "Save folder: " },
            { "版本未标注", "version unknown" },
            { "未标注", "unknown" },
            { " 小时 ", " h " },
            { " 分", " min" },
            { " 份", " copies" },
            { "累计 ", "total " },
            { "最近 ", "latest " },
            { " 次", " times" },
            { "音频 ", "audio " },
            { "脚本 ", "scripts " },
            { "总音符 ", "notes " },
            { "总音符", "notes" },
            { "　　← → 切换　Esc 关闭", "　　← →: switch　Esc: close" },
            { "← → 切换　Esc 关闭", "← →: switch　Esc: close" },
            { "← → 切换", "← →: switch" },
            { "Esc 关闭", "Esc: close" },
            { "浓淡 ", "Strength " },
            { "浓淡", "Strength" },
            { "内核", "core" },
            { "修改 ", "modified " },
            { "启动 ", "launched " },
            { " 次 · 累计 ", " times · total " },
            { "最近 ", "latest " },
            { "文件", "Files" },
            { "目录下的", " folder" },
            { "（压缩包根）", "(archive root)" },
            { "未找到（可能不是游戏目录）", "Not found (maybe not a game folder)" },
            { "（可能不是游戏目录）", " (maybe not a game folder)" }
        };

        private static string Frags_(string s)
        {
            for (int i = 0; i < Frags.GetLength(0); i++) s = s.Replace(Frags[i, 0], Frags[i, 1]);
            return s;
        }
        private static string Pattern(string s)
        {
            if (Current != "en") return s;      // 规则目前只覆盖英文，其它语言先保持中文单位
            for (int i = 0; i < RuleRe.Length; i++)
            {
                System.Text.RegularExpressions.Match m = RuleRe[i].Match(s);
                if (!m.Success) continue;
                string unit = Rules[i, 1];
                if (unit.StartsWith(" ")) return m.Groups[1].Value + unit;
                return m.Groups[1].Value + unit;
            }
            return s;
        }

        // 控件树 / 菜单：把 Text 命中的中文替换成译文（幂等，翻过的不再命中）
        public static void Sweep(Control root)
        {
            if (root == null || map.Count == 0) return;
            if (sweeping) return;
            sweeping = true;
            try { SweepControl(root, 0); }
            finally { sweeping = false; }
        }

        private static void SweepControl(Control c, int depth)
        {
            if (c == null || depth > 24) return;
            try
            {
                string t = c.Text;
                if (!string.IsNullOrEmpty(t))
                {
                    string r = T(t);
                    if (!ReferenceEquals(r, t) && r != t) { c.Text = r; c.Invalidate(); }
                }
            }
            catch { }
            try
            {
                ToolStrip ts = null;
                ContextMenuStrip cms = c.ContextMenuStrip;
                if (cms != null) ts = cms;
                if (ts != null) SweepMenu(ts.Items, 0);
            }
            catch { }
            try
            {
                foreach (Control ch in c.Controls) SweepControl(ch, depth + 1);
            }
            catch { }
        }

        public static void SweepMenu(ToolStripItemCollection items, int depth)
        {
            if (items == null || depth > 6) return;
            foreach (ToolStripItem it in items)
            {
                try
                {
                    string t = it.Text;
                    if (!string.IsNullOrEmpty(t))
                    {
                        string r = T(t);
                        if (r != t) it.Text = r;
                    }
                }
                catch { }
                ToolStripMenuItem mi = it as ToolStripMenuItem;
                if (mi != null) SweepMenu(mi.DropDownItems, depth + 1);
            }
        }

        // 之后新弹出的窗口（各种对话框）也自动翻译：定时扫一遍已打开的窗口
        public static void StartAutoSweep()
        {
            if (sweep != null) { sweep.Start(); return; }
            sweep = new Timer();
            sweep.Interval = 400;
            sweep.Tick += delegate
            {
                if (map.Count == 0) return;
                try
                {
                    FormCollection fs = Application.OpenForms;
                    for (int i = 0; i < fs.Count; i++) Sweep(fs[i]);
                }
                catch { }
            };
            sweep.Start();
        }

        public static void StopAutoSweep()
        {
            if (sweep != null) { sweep.Stop(); sweep.Dispose(); sweep = null; }
        }
    }
}
