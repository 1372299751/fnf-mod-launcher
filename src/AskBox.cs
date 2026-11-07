// 统一画风的自绘消息框：替换系统 MessageBox，按钮与文案都跟主题/语言一致
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace FnfLauncher
{
    internal class AskBox : Form
    {
        public static DialogResult Show(IWin32Window owner, string text)
        {
            return Show(owner, text, Lang.T("提示"), MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        public static DialogResult Show(IWin32Window owner, string text, string caption)
        {
            return Show(owner, text, caption, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        public static DialogResult Show(IWin32Window owner, string text, string caption, MessageBoxButtons buttons)
        {
            return Show(owner, text, caption, buttons, MessageBoxIcon.Information);
        }

        public static DialogResult Show(IWin32Window owner, string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon)
        {
            using (AskBox f = new AskBox(text, caption, buttons))
            {
                return f.ShowDialog(owner);
            }
        }

        private AskBox(string text, string caption, MessageBoxButtons buttons)
        {
            Text = Lang.T(caption);
            BackColor = Ui.Card;
            ForeColor = Ui.Txt;
            Font = Ui.FHint;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            DoubleBuffered = true;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            try { Ui.DarkTitleBar(Handle); } catch { }

            string body = Lang.T(text);
            int lines = 1;
            foreach (char c in body) if (c == '\n') lines++;
            int h = Math.Max(Ui.S(190), Ui.S(96) + lines * Ui.S(22));
            ClientSize = new Size(Ui.S(440), h);

            Label t = new Label();
            t.AutoSize = false;
            t.BackColor = Ui.Card;
            t.ForeColor = Ui.Txt;
            t.Font = new Font("Microsoft YaHei UI", 11f, FontStyle.Bold);
            t.TextAlign = ContentAlignment.MiddleLeft;
            t.Text = Lang.T(caption);
            t.SetBounds(Ui.S(20), Ui.S(14), ClientSize.Width - Ui.S(76), Ui.S(26));

            Label d = new Label();
            d.AutoSize = false;
            d.BackColor = Ui.Card;
            d.ForeColor = Ui.Sub;
            d.Font = Ui.FHint;
            d.TextAlign = ContentAlignment.TopLeft;
            d.Text = body;
            d.SetBounds(Ui.S(20), Ui.S(48), ClientSize.Width - Ui.S(40), h - Ui.S(112));

            int y = ClientSize.Height - Ui.S(60);
            int bw = Ui.S(96), gap = Ui.S(8);

            if (buttons == MessageBoxButtons.YesNoCancel)
            {
                RoundButton yes = Make(Lang.T("是"), true, ClientSize.Width - Ui.S(26) - bw * 3 - gap * 2, y, bw);
                yes.Click += delegate { DialogResult = DialogResult.Yes; Close(); };
                RoundButton no = Make(Lang.T("否"), false, ClientSize.Width - Ui.S(26) - bw * 2 - gap, y, bw);
                no.Click += delegate { DialogResult = DialogResult.No; Close(); };
                RoundButton cancel = Make(Lang.T("取消"), false, ClientSize.Width - Ui.S(26) - bw, y, bw);
                cancel.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };
                Controls.Add(yes); Controls.Add(no); Controls.Add(cancel);
            }
            else if (buttons == MessageBoxButtons.YesNo)
            {
                RoundButton yes = Make(Lang.T("是"), true, ClientSize.Width - Ui.S(26) - bw * 2 - gap, y, bw);
                yes.Click += delegate { DialogResult = DialogResult.Yes; Close(); };
                RoundButton no = Make(Lang.T("否"), false, ClientSize.Width - Ui.S(26) - bw, y, bw);
                no.Click += delegate { DialogResult = DialogResult.No; Close(); };
                Controls.Add(yes); Controls.Add(no);
            }
            else if (buttons == MessageBoxButtons.OKCancel)
            {
                RoundButton okc = Make(Lang.T("确定"), true, ClientSize.Width - Ui.S(26) - bw * 2 - gap, y, bw);
                okc.Click += delegate { DialogResult = DialogResult.OK; Close(); };
                RoundButton cac = Make(Lang.T("取消"), false, ClientSize.Width - Ui.S(26) - bw, y, bw);
                cac.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };
                Controls.Add(okc); Controls.Add(cac);
            }
            else
            {
                RoundButton ok = Make(Lang.T("关闭"), true, ClientSize.Width - Ui.S(26) - bw, y, bw);
                ok.Click += delegate { DialogResult = DialogResult.OK; Close(); };
                Controls.Add(ok);
            }

            Controls.Add(t); Controls.Add(d);
            Lang.Sweep(this);
        }

        private static RoundButton Make(string text, bool filled, int x, int y, int w)
        {
            RoundButton b = new RoundButton(text);
            b.BackColor = Ui.Card;
            b.Filled = filled;
            b.SetBounds(x, y, w, Ui.S(34));
            return b;
        }

        // 圆角窗口：切 Region + 自绘描边（和主界面同一套画风）
        private void ApplyRoundRegion()
        {
            if (WindowState == FormWindowState.Minimized) return;
            using (GraphicsPath p = Ui.Round(new Rectangle(0, 0, Width, Height), Ui.S(10)))
            {
                Region old = Region;
                Region = new Region(p);
                if (old != null) old.Dispose();
            }
        }

        protected override void OnResize(EventArgs e) { base.OnResize(e); ApplyRoundRegion(); Invalidate(); }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            ApplyRoundRegion();
            Ui.DarkTitleBar(Handle);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (GraphicsPath p = Ui.Round(new Rectangle(0, 0, Width - 1, Height - 1), Ui.S(10)))
            using (Pen pen = new Pen(Ui.Line, 1f))
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                e.Graphics.DrawPath(pen, p);
            }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); }
        }
    }
}
