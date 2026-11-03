// 端末コントロール: 画面グリッドの描画とキー・マウス入力。
//
// TS 版で xterm.js(描画・キー入力・選択・スクロールバック・IME)が担っていた部分。
// 描画は GDI の ExtTextOut で行い、各文字をセルの位置にぴったり置く
// (lpDx で送り幅を指定するので、和文フォールバックでも桁がずれない)。

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace CcTerm
{
    internal sealed class TerminalView : Control
    {
        /// <summary>グリッドの周りの余白(96 DPI 換算)。</summary>
        private const int PadBase = 4;
        /// <summary>スクロールバーの幅(96 DPI 換算)。</summary>
        private const int ScrollbarBase = 10;
        /// <summary>ホイール 1 目盛りでスクロールする行数。</summary>
        private const int WheelLines = 3;
        /// <summary>ダブルクリックで単語を選ぶときの区切り文字。</summary>
        private const string WordDelimiters = " \t\"'`()[]{}<>|,;";

        public readonly TerminalBuffer Buffer;
        private readonly VtParser parser;
        private TermFont font;
        private int pad = PadBase;
        private int scrollbarWidth = ScrollbarBase;

        /// <summary>スクロールバックを遡っている行数(0 = 最下部)。</summary>
        private int scrollOffset;

        // 読み取りスレッドから届いた出力を UI スレッドでまとめて処理する。
        private readonly object outLock = new object();
        private readonly StringBuilder pendingOut = new StringBuilder();
        private bool flushPosted;

        // 選択範囲(絶対行番号と桁)。
        private bool selecting;
        private bool hasSelection;
        private long selAnchorLine, selEndLine;
        private int selAnchorCol, selEndCol;

        // スクロールバーのつまみのドラッグ。
        private bool draggingThumb;
        private int thumbGrab;

        private char pendingHigh;
        private Color borderColor = Color.Empty;
        private int borderWidth;

        /// <summary>ユーザの入力(キー・貼り付け)。入力メモの記録対象。</summary>
        public event Action<string> Input;
        /// <summary>端末からの応答(DSR・フォーカス通知など)。入力メモには載せない。</summary>
        public event Action<string> Reply;
        public event Action Bell;
        /// <summary>桁数・行数が変わった(ConPTY にも伝える)。</summary>
        public event Action<int, int> GridResized;
        /// <summary>出力を画面に反映し終えた(承認まち判定のきっかけ)。</summary>
        public event Action OutputProcessed;
        /// <summary>Ctrl+V でクリップボードに文字が無く画像があった。</summary>
        public event Action PasteImageRequested;

        public TerminalView()
        {
            SetStyle(
                ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                ControlStyles.Selectable, true);
            TabStop = true;
            BackColor = Theme.Bg;
            Cursor = Cursors.IBeam;

            Buffer = new TerminalBuffer(80, 24);
            parser = new VtParser(Buffer);
            Buffer.Reply += delegate(string s) { RaiseReply(s); };
            Buffer.Bell += delegate
            {
                var handler = Bell;
                if (handler != null) handler();
            };
            // 遡って読んでいる間に出力が流れても、見ている位置が動かないようにする。
            Buffer.LineScrolledOff += delegate
            {
                if (scrollOffset > 0) scrollOffset = Math.Min(scrollOffset + 1, Buffer.Scrollback.Count);
            };
        }

        public int Cols
        {
            get { return Buffer.Cols; }
        }

        public int Rows
        {
            get { return Buffer.Rows; }
        }

        /// <summary>枠線(承認まち・ドラッグ中の強調)。Color.Empty で消す。</summary>
        public void SetBorder(Color color, int width)
        {
            if (borderColor == color && borderWidth == width) return;
            borderColor = color;
            borderWidth = width;
            Invalidate();
        }

        // ------------------------------------------------------------ 出力

        /// <summary>ConPTY の出力を積む(どのスレッドからでも呼べる)。</summary>
        public void EnqueueOutput(string data)
        {
            lock (outLock)
            {
                pendingOut.Append(data);
                if (flushPosted) return;
                flushPosted = true;
            }
            try
            {
                BeginInvoke(new MethodInvoker(FlushOutput));
            }
            catch (InvalidOperationException)
            {
                // ハンドル破棄後(タブを閉じた直後)に届いた出力は捨てる。
            }
        }

        private void FlushOutput()
        {
            string data;
            lock (outLock)
            {
                data = pendingOut.ToString();
                pendingOut.Length = 0;
                flushPosted = false;
            }
            if (IsDisposed || data.Length == 0) return;

            parser.Feed(data);
            Invalidate();
            var handler = OutputProcessed;
            if (handler != null) handler();
        }

        // ------------------------------------------------------------ サイズ

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            int dpi;
            using (var g = CreateGraphics()) dpi = (int)Math.Round(g.DpiX);
            font = TermFont.Get(dpi);
            pad = PadBase * dpi / 96;
            scrollbarWidth = ScrollbarBase * dpi / 96;
            UpdateGrid();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            UpdateGrid();
        }

        /// <summary>コントロールの大きさから桁数・行数を求め直す。</summary>
        private void UpdateGrid()
        {
            if (font == null) return;
            // 最小化や非表示で 0 になったときは触らない(不正な値になるため)。
            if (ClientSize.Width <= 0 || ClientSize.Height <= 0) return;

            int cols = Math.Max(2, (ClientSize.Width - pad * 2 - scrollbarWidth) / font.CellWidth);
            int rows = Math.Max(1, (ClientSize.Height - pad * 2) / font.CellHeight);
            if (cols == Buffer.Cols && rows == Buffer.Rows) return;

            Buffer.Resize(cols, rows);
            scrollOffset = Math.Min(scrollOffset, Buffer.Scrollback.Count);
            Invalidate();
            var handler = GridResized;
            if (handler != null) handler(cols, rows);
        }

        // ------------------------------------------------------------ 描画

        private int EffectiveOffset
        {
            get { return Buffer.AltActive ? 0 : Math.Min(scrollOffset, Buffer.Scrollback.Count); }
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            // OnPaint で全面を塗るので何もしない(ちらつき防止)。
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (font == null) return;
            IntPtr hdc = e.Graphics.GetHdc();
            try
            {
                Render(hdc);
            }
            finally
            {
                e.Graphics.ReleaseHdc(hdc);
            }
        }

        private void Render(IntPtr hdc)
        {
            int cw = font.CellWidth;
            int ch = font.CellHeight;
            FillRect(hdc, 0, 0, ClientSize.Width, ClientSize.Height, Theme.ToRgb(Theme.Bg));

            IntPtr oldFont = Native.SelectObject(hdc, font.Handle(0, false));
            Native.SetTextAlign(hdc, Native.TA_BASELINE);

            int offset = EffectiveOffset;
            long topAbs = Buffer.TopAbs - offset;
            long cursorAbs = Buffer.TopAbs + Buffer.CursorY;
            bool cursorShown = Buffer.CursorVisible && cursorAbs >= topAbs && cursorAbs < topAbs + Buffer.Rows;
            bool blockCursor = Focused && Buffer.CursorStyle <= 2;

            for (int r = 0; r < Buffer.Rows; r++)
            {
                long abs = topAbs + r;
                int cursorCol = cursorShown && blockCursor && abs == cursorAbs ? Buffer.CursorX : -1;
                DrawRow(hdc, Buffer.GetLine(abs), abs, pad + r * ch, cursorCol);
            }

            if (cursorShown && !blockCursor)
            {
                int x = pad + Buffer.CursorX * cw;
                int y = pad + (int)(cursorAbs - topAbs) * ch;
                int c = Theme.ToRgb(Theme.Cursor);
                if (!Focused) FrameRect(hdc, x, y, cw, ch, c);
                else if (Buffer.CursorStyle <= 4) FillRect(hdc, x, y + ch - 2, cw, 2, c);
                else FillRect(hdc, x, y, 2, ch, c);
            }

            DrawScrollbar(hdc);

            if (borderColor != Color.Empty)
            {
                int c = Theme.ToRgb(borderColor);
                int w = borderWidth;
                FillRect(hdc, 0, 0, ClientSize.Width, w, c);
                FillRect(hdc, 0, ClientSize.Height - w, ClientSize.Width, w, c);
                FillRect(hdc, 0, 0, w, ClientSize.Height, c);
                FillRect(hdc, ClientSize.Width - w, 0, w, ClientSize.Height, c);
            }

            Native.SelectObject(hdc, oldFont);
        }

        /// <summary>1 行を描く。色・フォント・装飾が同じセルはまとめて 1 回の ExtTextOut にする。</summary>
        private void DrawRow(IntPtr hdc, Cell[] line, long abs, int y, int cursorCol)
        {
            int cols = Buffer.Cols;
            int cw = font.CellWidth;

            var text = new StringBuilder();
            var dx = new List<int>();
            int runStart = 0;
            int runFg = 0, runBg = 0, runFont = 0;
            ushort runDeco = 0;
            bool runBold = false;

            int x = 0;
            while (x < cols)
            {
                Cell cell;
                if (line != null && x < line.Length) cell = line[x];
                else
                {
                    cell = new Cell();
                    cell.Ch = ' ';
                    cell.Fg = cell.Bg = ColorCode.Default;
                }

                // 先頭を失った全角の右半分は空白として扱う。
                int width = (cell.Flags & Attr.Wide) != 0 && x + 1 < cols ? 2 : 1;
                string s = (cell.Flags & Attr.WideCont) != 0 ? " " : cell.Text;

                bool isBold = (cell.Flags & Attr.Bold) != 0;
                int fg = Theme.Resolve(cell.Fg, true, isBold);
                int bg = Theme.Resolve(cell.Bg, false, false);
                if ((cell.Flags & Attr.Inverse) != 0)
                {
                    int t = fg;
                    fg = bg;
                    bg = t;
                }
                if ((cell.Flags & Attr.Dim) != 0) fg = Theme.Blend(fg, bg);
                if ((cell.Flags & Attr.Hidden) != 0) s = " ";
                if (IsSelected(abs, x)) bg = Theme.ToRgb(Theme.Selection);
                if (x == cursorCol || (width == 2 && x + 1 == cursorCol))
                {
                    fg = bg;
                    bg = Theme.ToRgb(Theme.Cursor);
                }

                int cp = s.Length > 1 && char.IsSurrogatePair(s, 0) ? char.ConvertToUtf32(s, 0) : s[0];
                int fontIndex = font.FontIndexFor(cp);
                ushort deco = (ushort)(cell.Flags & (Attr.Underline | Attr.Strike));

                bool same = text.Length > 0 && fg == runFg && bg == runBg && fontIndex == runFont &&
                            deco == runDeco && isBold == runBold;
                if (!same)
                {
                    FlushRun(hdc, text, dx, runStart, x, y, runFg, runBg, runFont, runBold, runDeco);
                    runStart = x;
                    runFg = fg;
                    runBg = bg;
                    runFont = fontIndex;
                    runDeco = deco;
                    runBold = isBold;
                }

                text.Append(s);
                dx.Add(cw * width);
                for (int i = 1; i < s.Length; i++) dx.Add(0);
                x += width;
            }
            FlushRun(hdc, text, dx, runStart, x, y, runFg, runBg, runFont, runBold, runDeco);
        }

        private void FlushRun(
            IntPtr hdc, StringBuilder text, List<int> dx, int startCol, int endCol, int y,
            int fg, int bg, int fontIndex, bool isBold, ushort deco)
        {
            if (text.Length == 0) return;
            int cw = font.CellWidth;
            int ch = font.CellHeight;
            int left = pad + startCol * cw;
            int right = pad + endCol * cw;

            Native.SelectObject(hdc, font.Handle(fontIndex, isBold));
            Native.SetTextColor(hdc, Theme.ColorRef(fg));
            Native.SetBkColor(hdc, Theme.ColorRef(bg));
            var rect = new Native.RECT(left, y, right, y + ch);
            Native.ExtTextOut(
                hdc, left, y + font.Ascent, Native.ETO_OPAQUE | Native.ETO_CLIPPED, ref rect,
                text.ToString(), text.Length, dx.ToArray());

            if ((deco & Attr.Underline) != 0) FillRect(hdc, left, y + font.Ascent + 1, right - left, 1, fg);
            if ((deco & Attr.Strike) != 0) FillRect(hdc, left, y + font.Ascent * 2 / 3, right - left, 1, fg);

            text.Length = 0;
            dx.Clear();
        }

        private static void FillRect(IntPtr hdc, int x, int y, int w, int h, int rgb)
        {
            if (w <= 0 || h <= 0) return;
            var rect = new Native.RECT(x, y, x + w, y + h);
            IntPtr brush = Native.CreateSolidBrush(Theme.ColorRef(rgb));
            Native.FillRect(hdc, ref rect, brush);
            Native.DeleteObject(brush);
        }

        private static void FrameRect(IntPtr hdc, int x, int y, int w, int h, int rgb)
        {
            FillRect(hdc, x, y, w, 1, rgb);
            FillRect(hdc, x, y + h - 1, w, 1, rgb);
            FillRect(hdc, x, y, 1, h, rgb);
            FillRect(hdc, x + w - 1, y, 1, h, rgb);
        }

        // ------------------------------------------------------------ スクロールバー

        private bool ScrollbarVisible
        {
            get { return !Buffer.AltActive && Buffer.Scrollback.Count > 0; }
        }

        /// <summary>つまみの位置と高さ。</summary>
        private void ThumbGeometry(out int top, out int height)
        {
            int track = ClientSize.Height;
            int total = Buffer.Scrollback.Count + Buffer.Rows;
            height = Math.Max(scrollbarWidth * 2, track * Buffer.Rows / Math.Max(1, total));
            height = Math.Min(height, track);
            int maxOffset = Math.Max(1, Buffer.Scrollback.Count);
            // offset 0(最下部)でつまみが一番下に来る。
            top = (track - height) * (maxOffset - EffectiveOffset) / maxOffset;
        }

        private void DrawScrollbar(IntPtr hdc)
        {
            if (!ScrollbarVisible) return;
            int x = ClientSize.Width - scrollbarWidth;
            int top, height;
            ThumbGeometry(out top, out height);
            int color = EffectiveOffset > 0 || draggingThumb ? 0x666666 : 0x3a3a3a;
            FillRect(hdc, x + 2, top + 2, scrollbarWidth - 4, height - 4, color);
        }

        private void DragThumbTo(int mouseY)
        {
            int top, height;
            ThumbGeometry(out top, out height);
            int range = ClientSize.Height - height;
            if (range <= 0) return;
            int newTop = Math.Max(0, Math.Min(mouseY - thumbGrab, range));
            int max = Buffer.Scrollback.Count;
            scrollOffset = max - (int)Math.Round((double)newTop * max / range);
            Invalidate();
        }

        /// <summary>表示位置を動かす(正で過去へ遡る)。</summary>
        public void ScrollView(int lines)
        {
            if (Buffer.AltActive) return;
            scrollOffset = Math.Max(0, Math.Min(scrollOffset + lines, Buffer.Scrollback.Count));
            Invalidate();
        }

        public void ScrollToBottom()
        {
            if (scrollOffset == 0) return;
            scrollOffset = 0;
            Invalidate();
        }

        // ------------------------------------------------------------ 選択

        private void CellAt(Point p, out long line, out int col)
        {
            int row = Math.Max(0, Math.Min((p.Y - pad) / font.CellHeight, Buffer.Rows - 1));
            col = Math.Max(0, Math.Min((p.X - pad) / font.CellWidth, Buffer.Cols - 1));
            line = Buffer.TopAbs - EffectiveOffset + row;
        }

        private void NormalizedSelection(out long l1, out int c1, out long l2, out int c2)
        {
            bool forward = selAnchorLine < selEndLine || (selAnchorLine == selEndLine && selAnchorCol <= selEndCol);
            if (forward)
            {
                l1 = selAnchorLine;
                c1 = selAnchorCol;
                l2 = selEndLine;
                c2 = selEndCol;
            }
            else
            {
                l1 = selEndLine;
                c1 = selEndCol;
                l2 = selAnchorLine;
                c2 = selAnchorCol;
            }
        }

        private bool IsSelected(long line, int col)
        {
            if (!hasSelection) return false;
            long l1, l2;
            int c1, c2;
            NormalizedSelection(out l1, out c1, out l2, out c2);
            if (line < l1 || line > l2) return false;
            if (line == l1 && col < c1) return false;
            if (line == l2 && col > c2) return false;
            return true;
        }

        private void ClearSelection()
        {
            if (!hasSelection) return;
            hasSelection = false;
            Invalidate();
        }

        public bool HasSelection
        {
            get { return hasSelection; }
        }

        /// <summary>選択範囲のテキスト(行末の空白は落とし、行は CRLF でつなぐ)。</summary>
        public string SelectedText()
        {
            if (!hasSelection) return "";
            long l1, l2;
            int c1, c2;
            NormalizedSelection(out l1, out c1, out l2, out c2);
            var sb = new StringBuilder();
            for (long l = l1; l <= l2; l++)
            {
                var line = Buffer.GetLine(l);
                if (line == null) continue;
                int from = l == l1 ? c1 : 0;
                int to = l == l2 ? c2 + 1 : line.Length;
                if (l > l1) sb.Append("\r\n");
                sb.Append(TerminalBuffer.LineText(line, from, to).TrimEnd());
            }
            return sb.ToString();
        }

        private void SelectWord(long lineNo, int col)
        {
            var line = Buffer.GetLine(lineNo);
            if (line == null || col >= line.Length) return;
            if (WordDelimiters.IndexOf(line[col].Text[0]) >= 0) return;
            int from = col;
            int to = col;
            while (from > 0 && WordDelimiters.IndexOf(line[from - 1].Text[0]) < 0) from--;
            while (to + 1 < line.Length && WordDelimiters.IndexOf(line[to + 1].Text[0]) < 0) to++;
            selAnchorLine = selEndLine = lineNo;
            selAnchorCol = from;
            selEndCol = to;
            hasSelection = true;
            Invalidate();
        }

        public void CopySelection()
        {
            string text = SelectedText();
            if (text.Length == 0) return;
            try
            {
                Clipboard.SetText(text);
            }
            catch (ExternalException)
            {
                // 他のアプリがクリップボードを掴んでいる。
            }
            ClearSelection();
        }

        // ------------------------------------------------------------ マウス

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            if (font == null) return;

            if (ScrollbarVisible && e.X >= ClientSize.Width - scrollbarWidth)
            {
                int top, height;
                ThumbGeometry(out top, out height);
                // つまみの外をクリックしたら、そこへつまみの中心を持ってくる。
                thumbGrab = e.Y >= top && e.Y < top + height ? e.Y - top : height / 2;
                draggingThumb = true;
                DragThumbTo(e.Y);
                return;
            }

            long line;
            int col;
            CellAt(e.Location, out line, out col);

            if (e.Button == MouseButtons.Left)
            {
                if (e.Clicks >= 2)
                {
                    SelectWord(line, col);
                    return;
                }
                selecting = true;
                hasSelection = false;
                selAnchorLine = selEndLine = line;
                selAnchorCol = selEndCol = col;
                Invalidate();
            }
            else if (e.Button == MouseButtons.Right)
            {
                // Windows のコンソールと同じく、選択があればコピー、なければ貼り付け。
                if (hasSelection) CopySelection();
                else Paste();
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (font == null) return;
            if (draggingThumb)
            {
                DragThumbTo(e.Y);
                return;
            }
            Cursor = ScrollbarVisible && e.X >= ClientSize.Width - scrollbarWidth ? Cursors.Default : Cursors.IBeam;
            if (!selecting || (e.Button & MouseButtons.Left) == 0) return;

            // 上下にはみ出したらスクロールしながら選択を伸ばす。
            if (e.Y < 0) ScrollView(1);
            else if (e.Y > ClientSize.Height) ScrollView(-1);

            long line;
            int col;
            CellAt(e.Location, out line, out col);
            if (line == selEndLine && col == selEndCol) return;
            selEndLine = line;
            selEndCol = col;
            hasSelection = selEndLine != selAnchorLine || selEndCol != selAnchorCol;
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            selecting = false;
            if (draggingThumb)
            {
                draggingThumb = false;
                Invalidate();
            }
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (font == null) return;
            int notches = e.Delta / 120;
            if (notches == 0) notches = Math.Sign(e.Delta);

            if (Buffer.MouseTracking != 0 && Buffer.MouseSgr)
            {
                // アプリがマウスを使っているならホイールをそのまま報告する。
                long line;
                int col;
                CellAt(e.Location, out line, out col);
                int row = (int)(line - Buffer.TopAbs) + 1;
                string button = notches > 0 ? "64" : "65";
                for (int i = 0; i < Math.Abs(notches); i++)
                    RaiseReply("\u001b[<" + button + ";" + (col + 1) + ";" + row + "M");
                return;
            }
            if (Buffer.AltActive)
            {
                // 代替画面(全画面アプリ)にはスクロールバックが無いので、矢印キーとして送る。
                string key = notches > 0 ? CursorKey('A', 1) : CursorKey('B', 1);
                var sb = new StringBuilder();
                for (int i = 0; i < Math.Abs(notches) * WheelLines; i++) sb.Append(key);
                RaiseReply(sb.ToString());
                return;
            }
            ScrollView(notches * WheelLines);
        }

        // ------------------------------------------------------------ キー入力

        protected override bool IsInputKey(Keys keyData)
        {
            // Tab・矢印・Enter なども端末へ送る(フォーカス移動に使わせない)。
            return true;
        }

        protected override bool IsInputChar(char charCode)
        {
            return true;
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.Handled) return;

            bool ctrl = e.Control;
            bool shift = e.Shift;
            bool alt = e.Alt;

            // 選択があれば Ctrl+C はコピー。なければ従来どおり ^C を送る。
            if (ctrl && !alt && e.KeyCode == Keys.C && (hasSelection || shift))
            {
                CopySelection();
                Consume(e);
                return;
            }
            if (ctrl && !shift && !alt && e.KeyCode == Keys.Insert)
            {
                CopySelection();
                Consume(e);
                return;
            }
            if ((ctrl && !alt && e.KeyCode == Keys.V) || (shift && !ctrl && !alt && e.KeyCode == Keys.Insert))
            {
                Paste();
                Consume(e);
                return;
            }
            if (shift && !ctrl && !alt && (e.KeyCode == Keys.PageUp || e.KeyCode == Keys.PageDown))
            {
                ScrollView(e.KeyCode == Keys.PageUp ? Buffer.Rows - 1 : -(Buffer.Rows - 1));
                Consume(e);
                return;
            }

            string seq = KeyToSequence(e.KeyCode, shift, ctrl, alt);
            if (seq == null) return;
            SendInput(seq);
            Consume(e);
        }

        private static void Consume(KeyEventArgs e)
        {
            e.Handled = true;
            // 対応する WM_CHAR を発生させない(KeyPress で二重に送らないため)。
            e.SuppressKeyPress = true;
        }

        /// <summary>文字にならないキーを VT のシーケンスに変換する。文字キーは null(KeyPress で送る)。</summary>
        private string KeyToSequence(Keys key, bool shift, bool ctrl, bool alt)
        {
            int mod = 1 + (shift ? 1 : 0) + (alt ? 2 : 0) + (ctrl ? 4 : 0);
            switch (key)
            {
                case Keys.Enter:
                    // Ctrl+Enter / Shift+Enter は「送信」ではなく「入力欄で改行」。
                    // ESC CR(= Alt+Enter 相当)なら claude は改行として扱う。
                    return ctrl || shift || alt ? "\u001b\r" : "\r";
                case Keys.Back:
                    if (ctrl) return "\b";
                    return alt ? "\u001b\u007f" : "\u007f";
                case Keys.Tab:
                    return shift ? "\u001b[Z" : "\t";
                case Keys.Escape:
                    return "\u001b";
                case Keys.Up: return CursorKey('A', mod);
                case Keys.Down: return CursorKey('B', mod);
                case Keys.Right: return CursorKey('C', mod);
                case Keys.Left: return CursorKey('D', mod);
                case Keys.Home: return CursorKey('H', mod);
                case Keys.End: return CursorKey('F', mod);
                case Keys.Insert: return Tilde(2, mod);
                case Keys.Delete: return Tilde(3, mod);
                case Keys.PageUp: return Tilde(5, mod);
                case Keys.PageDown: return Tilde(6, mod);
                case Keys.F1: return FunctionKey('P', mod);
                case Keys.F2: return FunctionKey('Q', mod);
                case Keys.F3: return FunctionKey('R', mod);
                case Keys.F4: return FunctionKey('S', mod);
                case Keys.F5: return Tilde(15, mod);
                case Keys.F6: return Tilde(17, mod);
                case Keys.F7: return Tilde(18, mod);
                case Keys.F8: return Tilde(19, mod);
                case Keys.F9: return Tilde(20, mod);
                case Keys.F10: return Tilde(21, mod);
                case Keys.F11: return Tilde(23, mod);
                case Keys.F12: return Tilde(24, mod);
                case Keys.Space:
                    return ctrl && !alt ? "\0" : null;
            }
            return null;
        }

        private string CursorKey(char final, int mod)
        {
            if (mod > 1) return "\u001b[1;" + mod + final;
            return (Buffer.AppCursorKeys ? "\u001bO" : "\u001b[") + final;
        }

        private static string Tilde(int code, int mod)
        {
            return mod > 1 ? "\u001b[" + code + ";" + mod + "~" : "\u001b[" + code + "~";
        }

        private static string FunctionKey(char final, int mod)
        {
            return mod > 1 ? "\u001b[1;" + mod + final : "\u001bO" + final;
        }

        protected override void OnKeyPress(KeyPressEventArgs e)
        {
            base.OnKeyPress(e);
            if (e.Handled) return;
            e.Handled = true;

            char c = e.KeyChar;
            // サロゲートペア(絵文字など)は 2 回に分けて届くので揃えてから送る。
            if (char.IsHighSurrogate(c))
            {
                pendingHigh = c;
                return;
            }
            string s;
            if (char.IsLowSurrogate(c))
            {
                if (pendingHigh == '\0') return;
                s = new string(new[] { pendingHigh, c });
                pendingHigh = '\0';
            }
            else
            {
                s = c.ToString();
            }

            // Alt+文字は ESC を前置する(AltGr = Ctrl+Alt は除く)。
            Keys mods = ModifierKeys;
            if ((mods & Keys.Alt) != 0 && (mods & Keys.Control) == 0 && c < 0x80) s = "\u001b" + s;
            SendInput(s);
        }

        /// <summary>クリップボードの文字を貼り付ける。文字が無く画像があれば画像として扱う。</summary>
        public void Paste()
        {
            string text = null;
            try
            {
                if (Clipboard.ContainsText()) text = Clipboard.GetText();
                else if (Clipboard.ContainsImage())
                {
                    var handler = PasteImageRequested;
                    if (handler != null) handler();
                    return;
                }
            }
            catch (ExternalException)
            {
                return;
            }
            if (string.IsNullOrEmpty(text)) return;

            // 改行は端末の流儀(CR)に揃える。
            text = text.Replace("\r\n", "\r").Replace('\n', '\r');
            if (Buffer.BracketedPaste) text = "\u001b[200~" + text + "\u001b[201~";
            SendInput(text);
        }

        /// <summary>ユーザ入力として送る(表示を最下部に戻し、選択を解除する)。</summary>
        private void SendInput(string data)
        {
            ScrollToBottom();
            ClearSelection();
            var handler = Input;
            if (handler != null) handler(data);
        }

        private void RaiseReply(string s)
        {
            var handler = Reply;
            if (handler != null) handler(s);
        }

        // ------------------------------------------------------------ フォーカス・IME

        protected override void OnGotFocus(EventArgs e)
        {
            base.OnGotFocus(e);
            if (Buffer.FocusReporting) RaiseReply("\u001b[I");
            Invalidate();
        }

        protected override void OnLostFocus(EventArgs e)
        {
            base.OnLostFocus(e);
            if (Buffer.FocusReporting) RaiseReply("\u001b[O");
            Invalidate();
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_IME_STARTCOMPOSITION || m.Msg == Native.WM_IME_COMPOSITION)
            {
                PositionIme();
            }
            base.WndProc(ref m);
        }

        /// <summary>IME の変換中の文字をカーソル位置に、端末と同じフォントで出す。</summary>
        private void PositionIme()
        {
            if (font == null) return;
            IntPtr himc = Native.ImmGetContext(Handle);
            if (himc == IntPtr.Zero) return;
            try
            {
                var cf = new Native.COMPOSITIONFORM();
                cf.dwStyle = Native.CFS_POINT;
                cf.ptCurrentPos.X = pad + Buffer.CursorX * font.CellWidth;
                cf.ptCurrentPos.Y = pad + (Buffer.CursorY + EffectiveOffset) * font.CellHeight;
                Native.ImmSetCompositionWindow(himc, ref cf);
                var lf = font.LogFont;
                Native.ImmSetCompositionFont(himc, ref lf);
            }
            finally
            {
                Native.ImmReleaseContext(Handle, himc);
            }
        }
    }
}
