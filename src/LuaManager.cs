// 模组 Lua 脚本管理：查看 / 添加 / 删除
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace FnfLauncher
{
    internal class LuaManagerForm : Form
    {
        private readonly ModEntry mod;
        private readonly List<string> files = new List<string>();
        private readonly ListBox list;
        private readonly Label status;
        private string addTarget = "";
        private int lastClick = -1;

        public LuaManagerForm(ModEntry m)
        {
            mod = m;
            Text = "Lua 脚本 · " + m.Name;
            BackColor = Ui.Bar;
            ForeColor = Ui.Txt;
            Font = Ui.FHint;
            StartPosition = FormStartPosition.CenterParent;
            AutoScaleMode = AutoScaleMode.None;
            ClientSize = new Size(Ui.S(720), Ui.S(560));
            MinimumSize = new Size(Ui.S(460), Ui.S(340));
            DoubleBuffered = true;

            Label h = new Label();
            h.AutoSize = false;
            h.BackColor = Ui.Bar;
            h.ForeColor = Ui.Sub;
            h.Font = Ui.FSmall;
            h.TextAlign = ContentAlignment.MiddleLeft;
            h.Text = "scripts / custom_events / custom_notetypes 里的 .lua 文件";
            h.SetBounds(Ui.S(18), Ui.S(10), Ui.S(560), Ui.S(18));

            list = new ListBox();
            list.BackColor = Ui.Card;
            list.ForeColor = Ui.Txt;
            list.BorderStyle = BorderStyle.FixedSingle;
            list.Font = Ui.FSmall;
            list.IntegralHeight = false;
            list.SetBounds(Ui.S(18), Ui.S(36), Ui.S(684), Ui.S(440));
            list.DrawMode = DrawMode.OwnerDrawFixed;
            list.ItemHeight = Ui.S(26);
            list.DrawItem += delegate(object s, DrawItemEventArgs de)
            {
                if (de.Index < 0) return;
                bool sel = (de.Index == list.SelectedIndex);
                // 先铺不透明底色，避免半透明叠加导致"越点越亮"
                Color bg = sel ? Ui.A(Ui.Accent, 255) : (de.Index % 2 == 0 ? Ui.Card : Color.FromArgb(23, 25, 33));
                using (SolidBrush b = new SolidBrush(sel ? Color.FromArgb(70, 60, 90) : bg))
                    de.Graphics.FillRectangle(b, de.Bounds);
                if (sel)
                {
                    using (SolidBrush b2 = new SolidBrush(Ui.Accent))
                        de.Graphics.FillRectangle(b2, new Rectangle(de.Bounds.X, de.Bounds.Y, Ui.S(3), de.Bounds.Height));
                }
                string txt = de.Index < list.Items.Count ? list.Items[de.Index].ToString() : "";
                TextRenderer.DrawText(de.Graphics, txt, Ui.FSmall, de.Bounds,
                    sel ? Ui.Txt : Ui.Sub,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.LeftAndRightPadding);
                if (sel)
                {
                    using (Pen p = new Pen(Ui.Accent)) de.Graphics.DrawRectangle(p, de.Bounds.X, de.Bounds.Y, de.Bounds.Width - 1, de.Bounds.Height - 1);
                }
            };

            status = new Label();
            status.AutoSize = false;
            status.BackColor = Ui.Bar;
            status.ForeColor = Ui.Sub;
            status.Font = Ui.FSmall;
            status.TextAlign = ContentAlignment.MiddleLeft;
            status.SetBounds(Ui.S(18), Ui.S(486), Ui.S(684), Ui.S(20));

            RoundButton bView = new RoundButton("查看");
            bView.SetBounds(Ui.S(18), Ui.S(514), Ui.S(90), Ui.S(34));
            bView.Click += delegate { ViewSelected(); };

            RoundButton bAdd = new RoundButton("添加…");
            bAdd.Filled = false;
            bAdd.SetBounds(Ui.S(116), Ui.S(514), Ui.S(90), Ui.S(34));
            bAdd.Click += delegate { AddLua(); };

            RoundButton bDel = new RoundButton("删除");
            bDel.Filled = false;
            bDel.SetBounds(Ui.S(214), Ui.S(514), Ui.S(90), Ui.S(34));
            bDel.Click += delegate { DeleteSelected(); };

            RoundButton bRef = new RoundButton("刷新");
            bRef.Filled = false;
            bRef.SetBounds(Ui.S(312), Ui.S(514), Ui.S(90), Ui.S(34));
            bRef.Click += delegate { Scan(); };

            RoundButton bClose = new RoundButton("关闭");
            bClose.Filled = false;
            bClose.SetBounds(Ui.S(572), Ui.S(514), Ui.S(90), Ui.S(34));
            bClose.Click += delegate { Close(); };

            // 再次点击已选中的行 → 取消高亮
            list.MouseUp += delegate(object s, MouseEventArgs me)
            {
                int hi = list.IndexFromPoint(me.Location);
                if (hi < 0) { return; }
                if (hi == lastClick)
                {
                    lastClick = -1;
                    list.BeginInvoke((MethodInvoker)delegate
                    {
                        list.ClearSelected();
                        list.SelectedIndex = -1;
                        list.Invalidate();
                    });
                }
                else lastClick = hi;
            };
            list.KeyDown += delegate(object s, KeyEventArgs ke)
            {
                if (ke.KeyCode == Keys.Escape)
                {
                    lastClick = -1;
                    list.ClearSelected();
                    list.SelectedIndex = -1;
                    list.Invalidate();
                }
            };

            list.DoubleClick += delegate { ViewSelected(); };

            Controls.Add(h); Controls.Add(list); Controls.Add(status);
            Controls.Add(bView); Controls.Add(bAdd); Controls.Add(bDel); Controls.Add(bRef); Controls.Add(bClose);

            ToolTip tip = new ToolTip();
            tip.InitialDelay = 300;
            list.MouseMove += delegate
            {
                int hi = list.IndexFromPoint(list.PointToClient(Cursor.Position));
                string t = (hi >= 0 && hi < files.Count) ? files[hi] : "";
                if (t != tip.GetToolTip(list)) tip.SetToolTip(list, t);
            };

            Scan();
        }

        // ---------------- 扫描
        private List<string> Roots()
        {
            List<string> bases = new List<string>();
            try
            {
                string exeDir = Path.GetDirectoryName(mod.Exe);
                if (!string.IsNullOrEmpty(exeDir)) { bases.Add(Path.Combine(exeDir, "mods")); bases.Add(Path.Combine(exeDir, "bin\\mods")); }
            }
            catch { }
            try
            {
                bases.Add(Path.Combine(mod.Dir, "mods"));
                bases.Add(Path.Combine(mod.Dir, "bin\\mods"));
            }
            catch { }
            return bases;
        }

        private void Scan()
        {
            files.Clear();
            list.Items.Clear();
            List<string> seen = new List<string>();

            foreach (string b in Roots())
            {
                if (!Directory.Exists(b)) continue;
                foreach (string sub in new string[] { "scripts", "custom_events", "custom_notetypes" })
                {
                    string dir = Path.Combine(b, sub);
                    if (!Directory.Exists(dir)) continue;
                    try
                    {
                        string[] fs = Directory.GetFiles(dir, "*.lua", SearchOption.AllDirectories);
                        Array.Sort(fs, StringComparer.OrdinalIgnoreCase);
                        foreach (string f in fs)
                        {
                            string full = Path.GetFullPath(f);
                            if (seen.Contains(full)) continue;
                            seen.Add(full);
                            files.Add(full);
                        }
                    }
                    catch { }
                }
                // 添加目标：优先已有的 scripts 目录
                if (addTarget.Length == 0 && Directory.Exists(Path.Combine(b, "scripts")))
                    addTarget = Path.Combine(b, "scripts");
            }

            for (int i = 0; i < files.Count; i++)
            {
                string rel = "";
                try { rel = files[i].Substring(mod.Dir.Length).TrimStart('\\'); } catch { rel = files[i]; }
                long size = 0;
                try { size = new FileInfo(files[i]).Length; } catch { }
                list.Items.Add(Path.GetFileName(files[i]) + "    " + (size / 1024.0).ToString("0.0") + " KB");
            }

            status.Text = files.Count == 0
                ? "这个模组里没有找到 .lua 文件"
                : "共 " + files.Count + " 个 lua 文件" + (addTarget.Length > 0 ? "　·　添加目标：" + addTarget : "");
            if (list.Items.Count > 0) list.SelectedIndex = 0;
        }

        private string SelectedFile()
        {
            int i = list.SelectedIndex;
            if (i < 0 || i >= files.Count) return null;
            return files[i];
        }

        private void ViewSelected()
        {
            string f = SelectedFile();
            if (f == null) return;
            using (LuaViewerForm v = new LuaViewerForm(f)) v.ShowDialog(this);
        }

        private void AddLua()
        {
            OpenFileDialog od = new OpenFileDialog();
            od.Filter = "Lua 脚本 (*.lua)|*.lua|所有文件 (*.*)|*.*";
            od.Multiselect = true;
            if (od.ShowDialog(this) != DialogResult.OK) return;

            string target = addTarget;
            if (target.Length == 0)
            {
                target = Path.Combine(mod.Dir, "mods", "scripts");
            }
            try { Directory.CreateDirectory(target); }
            catch (Exception ex)
            {
                MessageBox.Show(this, "创建目录失败：" + ex.Message, "添加脚本", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            int n = 0;
            foreach (string src in od.FileNames)
            {
                try
                {
                    File.Copy(src, Path.Combine(target, Path.GetFileName(src)), true);
                    n++;
                }
                catch { }
            }
            if (n > 0 && addTarget.Length == 0) addTarget = target;
            Scan();
            status.Text = "已添加 " + n + " 个脚本到 " + target;
        }

        private void DeleteSelected()
        {
            string f = SelectedFile();
            if (f == null) return;
            if (MessageBox.Show(this, "把下面这个脚本移到回收站？\r\n\r\n" + f, "删除脚本",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            if (RecycleBin.Delete(f))
            {
                Scan();
                status.Text = "已删除：" + Path.GetFileName(f);
            }
            else
            {
                try { File.Delete(f); Scan(); status.Text = "已删除：" + Path.GetFileName(f); }
                catch (Exception ex) { status.Text = "删除失败：" + ex.Message; }
            }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Ui.DarkTitleBar(Handle);
            Ui.FadeIn(this);
        }
    }

    internal class LuaViewerForm : Form
    {
        public LuaViewerForm(string path)
        {
            Text = "查看 · " + Path.GetFileName(path);
            BackColor = Ui.Bar;
            ForeColor = Ui.Txt;
            Font = Ui.FHint;
            StartPosition = FormStartPosition.CenterParent;
            AutoScaleMode = AutoScaleMode.None;
            ClientSize = new Size(Ui.S(820), Ui.S(600));
            DoubleBuffered = true;

            TextBox tb = new TextBox();
            tb.Multiline = true;
            tb.ReadOnly = true;
            tb.ScrollBars = ScrollBars.Both;
            tb.WordWrap = false;
            tb.BackColor = Color.FromArgb(14, 15, 20);
            tb.ForeColor = Color.FromArgb(214, 222, 235);
            try { tb.Font = new Font("Consolas", 10f); }
            catch { tb.Font = Ui.FSmall; }
            tb.SetBounds(Ui.S(14), Ui.S(14), Ui.S(792), Ui.S(540));
            try
            {
                tb.Text = File.ReadAllText(path);
                tb.SelectionStart = 0;
                tb.SelectionLength = 0;
            }
            catch (Exception ex) { tb.Text = "读取失败：" + ex.Message; }

            RoundButton close = new RoundButton("关闭");
            close.Filled = false;
            close.SetBounds(Ui.S(704), Ui.S(562), Ui.S(100), Ui.S(30));
            close.Click += delegate { Close(); };

            Controls.Add(tb); Controls.Add(close);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Ui.DarkTitleBar(Handle);
            Ui.FadeIn(this);
        }
    }
}
