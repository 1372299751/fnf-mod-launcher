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

        private static readonly System.Collections.Generic.Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.Ordinal);
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
            string r = TCore(s);
            return map.Count > 0 ? Plural(r) : r;
        }

        // 英文单复数：拼接出来的 "1 files" / "1 songs" / "1 copies" 收一下尾
        private static readonly System.Text.RegularExpressions.Regex OneRe =
            new System.Text.RegularExpressions.Regex(@"(?<![\d.,])1 (files|mods|songs|images|copies|times|entries|scripts|weeks|characters|stages|notes|events|videos|shots)\b");

        private static string Plural(string s)
        {
            if (string.IsNullOrEmpty(s) || s.IndexOf("1 ") < 0) return s;
            return OneRe.Replace(s, delegate(System.Text.RegularExpressions.Match m)
            {
                string w = m.Groups[1].Value;
                if (w == "copies") return "1 copy";
                if (w == "entries") return "1 entry";
                return "1 " + w.Substring(0, w.Length - 1);
            });
        }

        private static string TCore(string s)
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
            string suf = Suffix(s);
            if (suf != null) return suf;
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

        // 只让"带标点/分隔符"的短语参与后缀匹配，避免把 "文件" → "Files" 这种
        // 普通名词从句子尾部啃掉（"找不到文件" 会变成 "找不到Files"）。
        private static bool IsSuffixKey(string k)
        {
            if (k == null || k.Length < 3) return false;
            char c = k[k.Length - 1];
            if (c == '？' || c == '！' || c == '：' || c == '。' || c == '」' || c == '）' || c == '…' || c == '了') return true;
            return k.IndexOf('·') >= 0;
        }

        // 拼接后缀兜底（"再确认一次：删掉 D:\mod 吗？"、"已经是 100% 了"）：
        // 变量部分在前、中文尾巴在后，靠最长后缀命中，前半段交给碎片规则。
        private static string Suffix(string s)
        {
            string hit;
            if (memo.TryGetValue("\u0001" + s, out hit)) return hit;
            string result = null;
            int max = Math.Min(keyArr.Length, 600);
            for (int i = 0; i < max; i++)
            {
                string k = keyArr[i];
                if (k == null || k.Length < 3 || s.Length <= k.Length + 1) continue;
                if (!IsSuffixKey(k)) continue;
                if (string.CompareOrdinal(s, s.Length - k.Length, k, 0, k.Length) != 0) continue;
                result = Frags_(s.Substring(0, s.Length - k.Length)) + map[k];
                break;
            }
            memo["\u0001" + s] = result;
            return result;
        }

        // 拼接残留的碎片替换。顺序即优先级：长片段必须排在短片段前面，
        // 否则 " 个" 会先把 " 个模组" 啃掉、留下中文尾巴。
        private static readonly string[,] Frags =
        {
            // —— 数量 + 单位（长片段优先）
            { " 个键（只统计次数，不记录输入内容）", " keys (counts only, no input recorded)" },
            { " 个文件，约 ", " files, ~" },
            { " 个文件 · ", " files · " },
            { " 个文件。", " files." },
            { " 个文件", " files" },
            { " 个本地模组", " local mods" },
            { " 个网页版", " web entries" },
            { " 个模组的存档", " mods' saves" },
            { " 个模组归到：", " mods to: " },
            { " 个模组到：", " mods to: " },
            { " 个模组，用时 ", " mods in " },
            { " 个模组，本地缺少 ", " mods, missing locally: " },
            { " 个模组本地都有", " mods are all present locally" },
            { " 个模组", " mods" },
            { "网页版 ", "web " },
            { "识别目录 ", "mods folder: " },
            { " 个脚本到 ", " scripts to " },
            { " 个脚本", " scripts" },
            { " 个 lua 文件", " lua files" },
            { " 个键", " keys" },
            { " 个", "" },

            // —— 详情页的内容行（"自制 3 首 · 原版 12 首 · 总时长 …"）
            { " 首 · 原版 ", " songs · vanilla " },
            { " 首", " songs" },
            { " 张", " pics" },
            { " · 周 ", " · weeks " },
            { " · 角色 ", " · characters " },
            { " · 舞台 ", " · stages " },
            { " · 音符 ", " · notes " },
            { " · 事件 ", " · events " },
            { " · 视频 ", " · videos " },
            { " · 截图 ", " · shots " },
            { "总时长 ", "total " },
            { "（原版 ", " (vanilla " },
            { "）· 总音符 ", ") · notes " },
            { " 次 · 累计 ", " times · total " },
            { "）   ·   运行 ", ")   ·   ran " },
            { " 秒   ·   ", " s   ·   " },
            { "  ·  ", " · " },
            { "，约 ", ", ~" },
            { "，缓存命中 ", ", cache hits " },
            { "（约 ", "(about " },
            { " MB）", " MB)" },
            { "（0x", " (0x" },
            { "）：不是正常退出。", "): not a normal exit." },
            { " 吗？", "?" },
            { "」了。", "”." },
            { "（先选一个引擎）", "(pick an engine first)" },
            { "」添加为模组？", "” as a mod?" },
            { "」已经存在，要合并覆盖吗？", "” already exists. Merge and overwrite?" },
            { "　｜　已识别 ", " | detected " },
            { "免安装 · 解压即用，不需要装运行库", "Portable: unzip and run, no runtime required" },
            { "路径含中文/特殊字符，个别引擎会读不到资源", "The path has non-ASCII or special characters; some engines cannot load assets" },
            { "这个目录里没有 lime.ndll / assets / mods，可能不是完整的游戏目录", "No lime.ndll / assets / mods here — this may not be a complete game folder" },
            { "、", ", " },

            // —— 启动日志行（"11-14 12:25:04  [正常]  snsev  ·  …  ·  退出码 0  ·  329.0 秒"）
            { "[启动中]", "[Starting]" },
            { "[正常]", "[OK]" },
            { "[异常]", "[Failed]" },
            { " 退出码 ", " exit code " },

            // —— 时间
            { " 分钟才退出，更像游戏中途出错。", " min before exiting — more likely an error mid-game." },
            { " 秒就退出，通常是启动阶段就失败了（缺文件/缺运行库）。", " s before exiting — it usually fails during startup (missing files / runtime)." },
            { " 秒前", " s ago" },
            { " 分钟前", " min ago" },
            { " 小时前", " h ago" },
            { " 天前", " days ago" },
            { " 分钟", " min" },
            { " 小时 ", " h " },
            { " 秒", " s" },
            { " 分", " min" },

            // —— 备份 / 统计
            { "已有 ", "Existing " },
            { " 份，最近 ", " copies, latest " },
            { " 份", " copies" },
            { "累计 ", "total " },
            { "最近 ", "latest " },
            { " 次", " times" },
            { "视频 ", "videos " },
            { "截图 ", "shots " },
            { "音频 ", "audio " },
            { "脚本 ", "scripts " },
            { "总音符 ", "notes " },
            { "总音符", "notes" },

            // —— 目录 / 版本 / 引擎
            { "　·　", " · " },
            { "识别目录", "mods folder" },
            { "存档目录：", "Save folder: " },
            { "版本未标注", "version unknown" },
            { "未标注", "unknown" },
            { "内核", "core" },
            { "修改于 ", "last modified " },
            { "启动 ", "launched " },

            // —— 键盘提示 / 外观
            { "　　← → 切换　Esc 关闭", "　　← →: switch　Esc: close" },
            { "← → 切换　Esc 关闭", "← →: switch　Esc: close" },
            { "← → 切换", "← →: switch" },
            { "Esc 关闭", "Esc: close" },
            { "浓淡 ", "Strength " },
            { "浓淡", "Strength" },

            // —— 文件对话框滤镜与常见尾巴
            { "未找到（可能不是游戏目录）", "Not found (maybe not a game folder)" },
            { "（可能不是游戏目录）", " (maybe not a game folder)" },
            { "所有文件", "All files" },
            { "文本文件", "Text files" },
            { "（压缩包根）", "(archive root)" },
            { "% 了", "%" },
            { "有些魔改包会在 exe 里去掉版本字符串，识别不到时可以在这里手动填，会覆盖自动结果。",
              "Some modded packs strip the version string from the exe. If detection fails, fill it in here — it overrides the auto result." }
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
