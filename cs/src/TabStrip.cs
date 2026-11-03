// タブバー(自前描画)。見出し・承認まちの ●・閉じるボタン・新規タブの ＋ を描く。

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace CcTerm
{
    internal sealed class TabStrip : Control
    {
        public sealed class Item
        {
            public string Title;
            public string Tooltip;
            public bool Active;
            public bool Attention;
        }

        public readonly List<Item> Items = new List<Item>();

        /// <summary>タブがクリックされた(引数は番号)。</summary>
        public event Action<int> TabClicked;
        /// <summary>タブの × がクリックされた。</summary>
        public event Action<int> CloseClicked;
        /// <summary>＋ がクリックされた。</summary>
        public event Action NewTabClicked;

        private readonly float scale;
        private readonly List<Rectangle> tabRects = new List<Rectangle>();
        private readonly List<Rectangle> closeRects = new List<Rectangle>();
        private Rectangle newRect;
        private Point mouse = new Point(-1, -1);
        private readonly ToolTip tip = new ToolTip();
        private string tipText;

        public TabStrip(float scale)
        {
            this.scale = scale;
            SetStyle(
                ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            // クリックで端末からフォーカスを奪わない。
            SetStyle(ControlStyles.Selectable, false);
            BackColor = Theme.Chrome;
            ForeColor = Theme.Fg;
        }

        private int S(int v)
        {
            return (int)Math.Round(v * scale);
        }

        /// <summary>タブの中身が変わったら呼ぶ。</summary>
        public void UpdateItems()
        {
            Height = Font.Height + S(14);
            Invalidate();
        }

        private void ComputeLayout(Graphics g)
        {
            tabRects.Clear();
            closeRects.Clear();
            int x = S(4);
            int top = S(3);
            int h = Height - top;
            bool closable = Items.Count > 1;
            int closeW = S(18);
            int maxW = S(220);

            foreach (var item in Items)
            {
                int textW = TextRenderer.MeasureText(g, Label(item), Font, Size.Empty, TextFormatFlags.NoPadding).Width;
                int w = Math.Min(maxW, textW + S(16) + (closable ? closeW : 0));
                var rect = new Rectangle(x, top, w, h);
                tabRects.Add(rect);
                closeRects.Add(closable
                    ? new Rectangle(rect.Right - closeW - S(4), rect.Top + (h - closeW) / 2, closeW, closeW)
                    : Rectangle.Empty);
                x += w + S(2);
            }
            int nw = S(28);
            newRect = new Rectangle(x, top + (h - S(22)) / 2, nw, S(22));
        }

        private static string Label(Item item)
        {
            return item.Attention ? "● " + item.Title : item.Title;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Theme.Chrome);
            ComputeLayout(g);

            using (var border = new Pen(Theme.Border))
            {
                g.DrawLine(border, 0, Height - 1, Width, Height - 1);

                for (int i = 0; i < Items.Count; i++)
                {
                    var item = Items[i];
                    var rect = tabRects[i];
                    if (item.Active)
                    {
                        using (var b = new SolidBrush(Theme.Bg)) g.FillRectangle(b, rect.X, rect.Y, rect.Width, rect.Height);
                        g.DrawLine(border, rect.Left, rect.Bottom, rect.Left, rect.Top);
                        g.DrawLine(border, rect.Left, rect.Top, rect.Right - 1, rect.Top);
                        g.DrawLine(border, rect.Right - 1, rect.Top, rect.Right - 1, rect.Bottom);
                    }
                    else if (rect.Contains(mouse))
                    {
                        using (var b = new SolidBrush(Theme.ChromeHi)) g.FillRectangle(b, rect);
                    }

                    Color color = item.Attention ? Theme.Accent : item.Active ? Theme.Fg : Theme.Muted;
                    var close = closeRects[i];
                    int textRight = close.IsEmpty ? rect.Right - S(8) : close.Left - S(2);
                    var textRect = new Rectangle(rect.X + S(8), rect.Y, textRight - rect.X - S(8), rect.Height);
                    TextRenderer.DrawText(g, Label(item), Font, textRect, color,
                        TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis |
                        TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);

                    if (!close.IsEmpty)
                    {
                        if (close.Contains(mouse))
                        {
                            using (var b = new SolidBrush(Color.FromArgb(0x55, 0x55, 0x55))) g.FillRectangle(b, close);
                        }
                        TextRenderer.DrawText(g, "×", Font, close, color,
                            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                    }
                }
            }

            if (newRect.Contains(mouse))
            {
                using (var b = new SolidBrush(Theme.ChromeHi)) g.FillRectangle(b, newRect);
            }
            TextRenderer.DrawText(g, "＋", Font, newRect, newRect.Contains(mouse) ? Theme.Fg : Theme.Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            mouse = e.Location;
            Invalidate();

            // ホバー中のタブの cwd をツールチップで出す。
            string text = null;
            for (int i = 0; i < tabRects.Count; i++)
            {
                if (tabRects[i].Contains(mouse)) text = Items[i].Tooltip;
            }
            if (newRect.Contains(mouse)) text = "新しいタブ";
            if (text != tipText)
            {
                tipText = text;
                tip.SetToolTip(this, text);
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            mouse = new Point(-1, -1);
            Invalidate();
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (e.Button != MouseButtons.Left) return;

            if (newRect.Contains(e.Location))
            {
                var handler = NewTabClicked;
                if (handler != null) handler();
                return;
            }
            for (int i = 0; i < tabRects.Count; i++)
            {
                if (!closeRects[i].IsEmpty && closeRects[i].Contains(e.Location))
                {
                    var handler = CloseClicked;
                    if (handler != null) handler(i);
                    return;
                }
                if (tabRects[i].Contains(e.Location))
                {
                    var handler = TabClicked;
                    if (handler != null) handler(i);
                    return;
                }
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) tip.Dispose();
            base.Dispose(disposing);
        }
    }
}
