// 画面グリッドの状態(セル・カーソル・スクロール領域・代替画面・スクロールバック)。
//
// TS 版では xterm.js が一手に引き受けていた部分。VtParser が解釈した結果を
// ここで画面に反映し、TerminalView がそれを描画する。
//
// ConPTY は自前の画面を VT シーケンスとして送ってくるだけなので、
// 必要なのは xterm 互換の基本的な部分集合で足りる。

using System;
using System.Collections.Generic;
using System.Text;

namespace CcTerm
{
    /// <summary>1 セル分の文字と属性。</summary>
    internal struct Cell
    {
        /// <summary>BMP の 1 文字('\0' は空白扱い)。</summary>
        public char Ch;
        /// <summary>サロゲートペアや結合文字を含む場合の文字列全体(なければ null)。</summary>
        public string Ext;
        public int Fg;
        public int Bg;
        public ushort Flags;

        public string Text
        {
            get
            {
                if (Ext != null) return Ext;
                return Ch == '\0' ? " " : Ch.ToString();
            }
        }
    }

    /// <summary>セル属性のビット。</summary>
    internal static class Attr
    {
        public const ushort Bold = 1;
        public const ushort Dim = 2;
        public const ushort Italic = 4;
        public const ushort Underline = 8;
        public const ushort Blink = 16;
        public const ushort Inverse = 32;
        public const ushort Hidden = 64;
        public const ushort Strike = 128;
        /// <summary>全角文字の左半分(文字本体を持つ)。</summary>
        public const ushort Wide = 256;
        /// <summary>全角文字の右半分(描画しない)。</summary>
        public const ushort WideCont = 512;

        public const ushort Style = Bold | Dim | Italic | Underline | Blink | Inverse | Hidden | Strike;
    }

    /// <summary>
    /// 色の表現。-1 は既定色、0〜255 はパレット番号、RgbFlag を立てた値は 24bit 色。
    /// </summary>
    internal static class ColorCode
    {
        public const int Default = -1;
        public const int RgbFlag = 0x1000000;

        public static int Rgb(int r, int g, int b)
        {
            return RgbFlag | ((r & 0xFF) << 16) | ((g & 0xFF) << 8) | (b & 0xFF);
        }
    }

    /// <summary>固定長のリングバッファ(スクロールバック用)。</summary>
    internal sealed class RingBuffer<T>
    {
        private readonly T[] items;
        private int start;
        private int count;

        public RingBuffer(int capacity)
        {
            items = new T[capacity];
        }

        public int Count
        {
            get { return count; }
        }

        public T this[int index]
        {
            get { return items[(start + index) % items.Length]; }
        }

        /// <summary>末尾に追加する。満杯で先頭を捨てたら true。</summary>
        public bool Add(T item)
        {
            if (count < items.Length)
            {
                items[(start + count) % items.Length] = item;
                count++;
                return false;
            }
            items[start] = item;
            start = (start + 1) % items.Length;
            return true;
        }

        public void Clear()
        {
            Array.Clear(items, 0, items.Length);
            start = 0;
            count = 0;
        }
    }

    internal sealed class TerminalBuffer
    {
        /// <summary>スクロールバック行数。</summary>
        public const int ScrollbackLines = 10000;

        private sealed class SavedCursor
        {
            public int X, Y, Fg, Bg;
            public ushort Flags;
            public bool OriginMode, PendingWrap, G0Graphics, G1Graphics, ShiftOut;
        }

        public int Cols { get; private set; }
        public int Rows { get; private set; }

        private Cell[][] main;
        private Cell[][] alt;
        private bool altActive;

        public readonly RingBuffer<Cell[]> Scrollback = new RingBuffer<Cell[]>(ScrollbackLines);
        /// <summary>スクロールバックから溢れて捨てた行数(行の絶対番号の基準)。</summary>
        public long Dropped { get; private set; }

        public int CursorX { get; private set; }
        public int CursorY { get; private set; }
        private bool pendingWrap;

        private int fg = ColorCode.Default;
        private int bg = ColorCode.Default;
        private ushort flags;

        private int scrollTop;
        private int scrollBottom;
        private SavedCursor savedMain;
        private SavedCursor savedAlt;
        private bool[] tabStops;

        private bool g0Graphics;
        private bool g1Graphics;
        private bool shiftOut;
        private int lastPrinted = ' ';

        // ---------------------------------------------------------- モード

        /// <summary>DECCKM: カーソルキーを SS3 で送る。</summary>
        public bool AppCursorKeys { get; private set; }
        public bool AutoWrap { get; private set; }
        public bool OriginMode { get; private set; }
        public bool CursorVisible { get; private set; }
        public bool BracketedPaste { get; private set; }
        public bool InsertMode { get; private set; }
        /// <summary>マウス報告(0 なら無効、1000/1002/1003)。</summary>
        public int MouseTracking { get; private set; }
        public bool MouseSgr { get; private set; }
        public bool FocusReporting { get; private set; }
        /// <summary>DECSCUSR のカーソル形状(0〜6)。</summary>
        public int CursorStyle { get; private set; }

        /// <summary>端末から相手(シェル)への応答(DSR・DA など)。</summary>
        public event Action<string> Reply;
        /// <summary>BEL を受けた。</summary>
        public event Action Bell;
        /// <summary>行がスクロールバックへ送られた(表示位置の固定に使う)。</summary>
        public event Action LineScrolledOff;

        public TerminalBuffer(int cols, int rows)
        {
            Cols = Math.Max(1, cols);
            Rows = Math.Max(1, rows);
            main = NewScreen(Cols, Rows);
            alt = NewScreen(Cols, Rows);
            ResetModes();
        }

        private void ResetModes()
        {
            AppCursorKeys = false;
            AutoWrap = true;
            OriginMode = false;
            CursorVisible = true;
            BracketedPaste = false;
            InsertMode = false;
            MouseTracking = 0;
            MouseSgr = false;
            FocusReporting = false;
            CursorStyle = 0;
            fg = ColorCode.Default;
            bg = ColorCode.Default;
            flags = 0;
            scrollTop = 0;
            scrollBottom = Rows - 1;
            g0Graphics = g1Graphics = shiftOut = false;
            savedMain = savedAlt = null;
            ResetTabs();
        }

        /// <summary>いま表示している画面(通常 or 代替)。</summary>
        public Cell[][] Screen
        {
            get { return altActive ? alt : main; }
        }

        public bool AltActive
        {
            get { return altActive; }
        }

        /// <summary>画面の 1 行目の絶対行番号。</summary>
        public long TopAbs
        {
            get { return Dropped + Scrollback.Count; }
        }

        /// <summary>絶対行番号の行(スクロールバック or 画面)。範囲外なら null。</summary>
        public Cell[] GetLine(long abs)
        {
            long i = abs - Dropped;
            if (i < 0) return null;
            if (i < Scrollback.Count) return Scrollback[(int)i];
            i -= Scrollback.Count;
            return i < Rows ? Screen[(int)i] : null;
        }

        /// <summary>可視画面のテキスト(承認まち検知に使う)。</summary>
        public string ScreenText()
        {
            var sb = new StringBuilder();
            var screen = Screen;
            for (int y = 0; y < Rows; y++)
            {
                if (y > 0) sb.Append('\n');
                sb.Append(LineText(screen[y], 0, Cols).TrimEnd());
            }
            return sb.ToString();
        }

        /// <summary>行の [from, to) 桁のテキスト。</summary>
        public static string LineText(Cell[] line, int from, int to)
        {
            var sb = new StringBuilder();
            to = Math.Min(to, line.Length);
            for (int x = Math.Max(0, from); x < to; x++)
            {
                if ((line[x].Flags & Attr.WideCont) != 0) continue;
                sb.Append(line[x].Text);
            }
            return sb.ToString();
        }

        // ---------------------------------------------------------- 画面の生成・リサイズ

        private Cell[][] NewScreen(int cols, int rows)
        {
            var s = new Cell[rows][];
            for (int y = 0; y < rows; y++) s[y] = BlankRow(cols, ColorCode.Default);
            return s;
        }

        private static Cell[] BlankRow(int cols, int bgColor)
        {
            var row = new Cell[cols];
            for (int x = 0; x < cols; x++) row[x] = Blank(bgColor);
            return row;
        }

        private static Cell Blank(int bgColor)
        {
            var c = new Cell();
            c.Ch = ' ';
            c.Fg = ColorCode.Default;
            c.Bg = bgColor;
            return c;
        }

        /// <summary>
        /// 端末サイズの変更。行が減るときはカーソルより下の空行から削り、
        /// 足りなければ上の行をスクロールバックへ送る。ConPTY がこの後に画面を描き直す。
        /// </summary>
        public void Resize(int cols, int rows)
        {
            cols = Math.Max(1, cols);
            rows = Math.Max(1, rows);
            if (cols == Cols && rows == Rows) return;

            // カーソルを考慮して詰めるのは表示中の画面だけ。
            int cursorY = CursorY;
            int unused = 0;
            if (altActive)
            {
                main = ResizeScreen(main, cols, rows, false, true, ref unused);
                alt = ResizeScreen(alt, cols, rows, true, false, ref cursorY);
            }
            else
            {
                main = ResizeScreen(main, cols, rows, true, true, ref cursorY);
                alt = ResizeScreen(alt, cols, rows, false, false, ref unused);
            }

            Cols = cols;
            Rows = rows;
            CursorX = Math.Min(CursorX, cols - 1);
            CursorY = Math.Max(0, Math.Min(cursorY, rows - 1));
            pendingWrap = false;
            scrollTop = 0;
            scrollBottom = rows - 1;
            ResetTabs();
        }

        private Cell[][] ResizeScreen(Cell[][] screen, int cols, int rows, bool active, bool isMain, ref int cursorY)
        {
            var list = new List<Cell[]>(screen);

            while (list.Count > rows)
            {
                int last = list.Count - 1;
                if (active && last > cursorY && IsBlank(list[last]))
                {
                    list.RemoveAt(last);
                    continue;
                }
                if (!isMain && !active)
                {
                    list.RemoveAt(last);
                    continue;
                }
                if (isMain) PushScrollback(list[0]);
                list.RemoveAt(0);
                if (active) cursorY--;
            }
            while (list.Count < rows) list.Add(BlankRow(cols, ColorCode.Default));

            for (int y = 0; y < list.Count; y++)
            {
                var row = list[y];
                if (row.Length == cols) continue;
                var resized = new Cell[cols];
                Array.Copy(row, resized, Math.Min(cols, row.Length));
                for (int x = row.Length; x < cols; x++) resized[x] = Blank(ColorCode.Default);
                // 右端で全角文字が半分に切れたら消す。
                if ((resized[cols - 1].Flags & Attr.Wide) != 0) resized[cols - 1] = Blank(ColorCode.Default);
                list[y] = resized;
            }
            return list.ToArray();
        }

        private static bool IsBlank(Cell[] row)
        {
            foreach (var c in row)
            {
                if (c.Ext != null || (c.Ch != ' ' && c.Ch != '\0') || c.Bg != ColorCode.Default) return false;
            }
            return true;
        }

        private void PushScrollback(Cell[] row)
        {
            if (Scrollback.Add(row)) Dropped++;
            var handler = LineScrolledOff;
            if (handler != null) handler();
        }

        private void ResetTabs()
        {
            tabStops = new bool[Cols];
            for (int x = 8; x < Cols; x += 8) tabStops[x] = true;
        }

        // ---------------------------------------------------------- 文字の出力

        /// <summary>DEC 特殊図形文字(ESC ( 0)の 0x5F〜0x7E の対応表。</summary>
        private const string DecGraphics =
            " ◆▒␉␌␍␊°±␤␋┘┐┌└┼" +
            "⎺⎻─⎼⎽├┤┴┬│≤≥π≠£·";

        /// <summary>印字可能文字を 1 つ置く。</summary>
        public void Print(int cp)
        {
            bool graphics = shiftOut ? g1Graphics : g0Graphics;
            if (graphics && cp >= 0x5F && cp <= 0x7E) cp = DecGraphics[cp - 0x5F];

            int w = CharWidth.Of(cp);
            if (w == 0)
            {
                AttachCombining(cp);
                return;
            }

            if (pendingWrap)
            {
                pendingWrap = false;
                CursorX = 0;
                LineFeed();
            }

            if (w == 2 && CursorX == Cols - 1)
            {
                // 全角文字が右端に入りきらないので次の行へ送る。
                if (AutoWrap)
                {
                    var r = Screen[CursorY];
                    ClearWideAt(r, CursorX);
                    r[CursorX] = Blank(bg);
                    CursorX = 0;
                    LineFeed();
                }
                else if (Cols >= 2)
                {
                    CursorX = Cols - 2;
                }
            }
            if (Cols < w) return;

            var row = Screen[CursorY];
            if (InsertMode) InsertCells(w);

            ClearWideAt(row, CursorX);
            if (w == 2) ClearWideAt(row, CursorX + 1);

            var cell = new Cell();
            if (cp <= 0xFFFF)
            {
                cell.Ch = (char)cp;
            }
            else
            {
                cell.Ch = ' ';
                cell.Ext = char.ConvertFromUtf32(cp);
            }
            cell.Fg = fg;
            cell.Bg = bg;
            cell.Flags = (ushort)(flags | (w == 2 ? Attr.Wide : 0));
            row[CursorX] = cell;

            if (w == 2)
            {
                var cont = new Cell();
                cont.Ch = '\0';
                cont.Fg = fg;
                cont.Bg = bg;
                cont.Flags = (ushort)(flags | Attr.WideCont);
                row[CursorX + 1] = cont;
            }

            lastPrinted = cp;
            CursorX += w;
            if (CursorX >= Cols)
            {
                CursorX = Cols - 1;
                if (AutoWrap) pendingWrap = true;
            }
        }

        /// <summary>結合文字・異体字セレクタは直前のセルに重ねる。</summary>
        private void AttachCombining(int cp)
        {
            var row = Screen[CursorY];
            int x = pendingWrap ? CursorX : CursorX - 1;
            if (x < 0) return;
            if ((row[x].Flags & Attr.WideCont) != 0 && x > 0) x--;
            row[x].Ext = row[x].Text + char.ConvertFromUtf32(cp);
        }

        /// <summary>全角文字の片割れを上書きする前に、もう片方を空白に戻す。</summary>
        private void ClearWideAt(Cell[] row, int x)
        {
            if (x < 0 || x >= row.Length) return;
            if ((row[x].Flags & Attr.WideCont) != 0 && x > 0) row[x - 1] = Blank(row[x - 1].Bg);
            if ((row[x].Flags & Attr.Wide) != 0 && x + 1 < row.Length) row[x + 1] = Blank(row[x + 1].Bg);
        }

        // ---------------------------------------------------------- C0 制御文字

        public void Execute(char c)
        {
            switch (c)
            {
                case '\x07':
                    var bell = Bell;
                    if (bell != null) bell();
                    break;
                case '\b':
                    pendingWrap = false;
                    if (CursorX > 0) CursorX--;
                    break;
                case '\t':
                    TabForward(1);
                    break;
                case '\n':
                case '\x0b':
                case '\x0c':
                    LineFeed();
                    break;
                case '\r':
                    CursorX = 0;
                    pendingWrap = false;
                    break;
                case '\x0e':
                    shiftOut = true;
                    break;
                case '\x0f':
                    shiftOut = false;
                    break;
            }
        }

        private void LineFeed()
        {
            pendingWrap = false;
            if (CursorY == scrollBottom) ScrollUp(1);
            else if (CursorY < Rows - 1) CursorY++;
        }

        private void ReverseIndex()
        {
            pendingWrap = false;
            if (CursorY == scrollTop) ScrollDown(1);
            else if (CursorY > 0) CursorY--;
        }

        private void TabForward(int n)
        {
            pendingWrap = false;
            for (int i = 0; i < n; i++)
            {
                int x = CursorX + 1;
                while (x < Cols - 1 && !tabStops[x]) x++;
                CursorX = Math.Min(x, Cols - 1);
            }
        }

        private void TabBackward(int n)
        {
            pendingWrap = false;
            for (int i = 0; i < n; i++)
            {
                int x = CursorX - 1;
                while (x > 0 && !tabStops[x]) x--;
                CursorX = Math.Max(x, 0);
            }
        }

        // ---------------------------------------------------------- スクロール

        /// <summary>スクロール領域を n 行上へ。画面全体なら消える行はスクロールバックへ。</summary>
        private void ScrollUp(int n)
        {
            var s = Screen;
            int height = scrollBottom - scrollTop + 1;
            n = Math.Min(n, height);
            for (int i = 0; i < n; i++)
            {
                var top = s[scrollTop];
                if (!altActive && scrollTop == 0 && scrollBottom == Rows - 1) PushScrollback(top);
                Array.Copy(s, scrollTop + 1, s, scrollTop, height - 1);
                s[scrollBottom] = BlankRow(Cols, bg);
            }
        }

        private void ScrollDown(int n)
        {
            var s = Screen;
            int height = scrollBottom - scrollTop + 1;
            n = Math.Min(n, height);
            for (int i = 0; i < n; i++)
            {
                Array.Copy(s, scrollTop, s, scrollTop + 1, height - 1);
                s[scrollTop] = BlankRow(Cols, bg);
            }
        }

        // ---------------------------------------------------------- 消去・挿入・削除

        private void EraseCells(int y, int from, int to)
        {
            var row = Screen[y];
            from = Math.Max(0, from);
            to = Math.Min(Cols, to);
            if (from >= to) return;
            ClearWideAt(row, from);
            ClearWideAt(row, to - 1);
            for (int x = from; x < to; x++) row[x] = Blank(bg);
        }

        private void EraseInDisplay(int mode)
        {
            switch (mode)
            {
                case 0:
                    EraseCells(CursorY, CursorX, Cols);
                    for (int y = CursorY + 1; y < Rows; y++) EraseCells(y, 0, Cols);
                    break;
                case 1:
                    for (int y = 0; y < CursorY; y++) EraseCells(y, 0, Cols);
                    EraseCells(CursorY, 0, CursorX + 1);
                    break;
                case 2:
                    for (int y = 0; y < Rows; y++) EraseCells(y, 0, Cols);
                    break;
                case 3:
                    Dropped += Scrollback.Count;
                    Scrollback.Clear();
                    break;
            }
        }

        private void EraseInLine(int mode)
        {
            switch (mode)
            {
                case 0: EraseCells(CursorY, CursorX, Cols); break;
                case 1: EraseCells(CursorY, 0, CursorX + 1); break;
                case 2: EraseCells(CursorY, 0, Cols); break;
            }
        }

        private void InsertCells(int n)
        {
            var row = Screen[CursorY];
            n = Math.Min(n, Cols - CursorX);
            ClearWideAt(row, CursorX);
            Array.Copy(row, CursorX, row, CursorX + n, Cols - CursorX - n);
            for (int x = CursorX; x < CursorX + n; x++) row[x] = Blank(bg);
            if ((row[Cols - 1].Flags & Attr.Wide) != 0) row[Cols - 1] = Blank(bg);
        }

        private void DeleteCells(int n)
        {
            var row = Screen[CursorY];
            n = Math.Min(n, Cols - CursorX);
            ClearWideAt(row, CursorX);
            ClearWideAt(row, CursorX + n - 1);
            Array.Copy(row, CursorX + n, row, CursorX, Cols - CursorX - n);
            for (int x = Cols - n; x < Cols; x++) row[x] = Blank(bg);
        }

        private void InsertLines(int n)
        {
            if (CursorY < scrollTop || CursorY > scrollBottom) return;
            int saveTop = scrollTop;
            scrollTop = CursorY;
            ScrollDown(n);
            scrollTop = saveTop;
            CursorX = 0;
            pendingWrap = false;
        }

        private void DeleteLines(int n)
        {
            if (CursorY < scrollTop || CursorY > scrollBottom) return;
            int saveTop = scrollTop;
            scrollTop = CursorY;
            // 領域の途中から消すので、スクロールバックには送らない。
            var s = Screen;
            int height = scrollBottom - scrollTop + 1;
            n = Math.Min(n, height);
            for (int i = 0; i < n; i++)
            {
                Array.Copy(s, scrollTop + 1, s, scrollTop, height - 1);
                s[scrollBottom] = BlankRow(Cols, bg);
            }
            scrollTop = saveTop;
            CursorX = 0;
            pendingWrap = false;
        }

        // ---------------------------------------------------------- カーソル

        private void SetCursor(int x, int y)
        {
            int top = OriginMode ? scrollTop : 0;
            int bottom = OriginMode ? scrollBottom : Rows - 1;
            CursorX = Math.Max(0, Math.Min(x, Cols - 1));
            CursorY = Math.Max(top, Math.Min(y + top, bottom));
            pendingWrap = false;
        }

        private void MoveCursor(int dx, int dy)
        {
            // 相対移動はスクロール領域の中にいればその中に留める。
            int top = CursorY >= scrollTop ? scrollTop : 0;
            int bottom = CursorY <= scrollBottom ? scrollBottom : Rows - 1;
            CursorX = Math.Max(0, Math.Min(CursorX + dx, Cols - 1));
            CursorY = Math.Max(top, Math.Min(CursorY + dy, bottom));
            pendingWrap = false;
        }

        private void SaveCursor()
        {
            var s = new SavedCursor();
            s.X = CursorX;
            s.Y = CursorY;
            s.Fg = fg;
            s.Bg = bg;
            s.Flags = flags;
            s.OriginMode = OriginMode;
            s.PendingWrap = pendingWrap;
            s.G0Graphics = g0Graphics;
            s.G1Graphics = g1Graphics;
            s.ShiftOut = shiftOut;
            if (altActive) savedAlt = s;
            else savedMain = s;
        }

        private void RestoreCursor()
        {
            var s = altActive ? savedAlt : savedMain;
            if (s == null)
            {
                CursorX = 0;
                CursorY = 0;
                fg = bg = ColorCode.Default;
                flags = 0;
                OriginMode = false;
                pendingWrap = false;
                return;
            }
            CursorX = Math.Min(s.X, Cols - 1);
            CursorY = Math.Min(s.Y, Rows - 1);
            fg = s.Fg;
            bg = s.Bg;
            flags = s.Flags;
            OriginMode = s.OriginMode;
            pendingWrap = s.PendingWrap && CursorX == Cols - 1;
            g0Graphics = s.G0Graphics;
            g1Graphics = s.G1Graphics;
            shiftOut = s.ShiftOut;
        }

        // ---------------------------------------------------------- 代替画面

        private void EnterAlt(bool saveCursor)
        {
            if (altActive) return;
            if (saveCursor) SaveCursor();
            alt = NewScreen(Cols, Rows);
            altActive = true;
            scrollTop = 0;
            scrollBottom = Rows - 1;
        }

        private void LeaveAlt(bool restoreCursor)
        {
            if (!altActive) return;
            altActive = false;
            scrollTop = 0;
            scrollBottom = Rows - 1;
            if (restoreCursor) RestoreCursor();
        }

        // ---------------------------------------------------------- ESC シーケンス

        public void EscDispatch(char intermediate, char final)
        {
            if (intermediate == '(' || intermediate == ')')
            {
                bool graphics = final == '0';
                if (intermediate == '(') g0Graphics = graphics;
                else g1Graphics = graphics;
                return;
            }
            if (intermediate != '\0') return;

            switch (final)
            {
                case '7': SaveCursor(); break;
                case '8': RestoreCursor(); break;
                case 'D': LineFeed(); break;
                case 'E':
                    CursorX = 0;
                    LineFeed();
                    break;
                case 'H':
                    if (CursorX < Cols) tabStops[CursorX] = true;
                    break;
                case 'M': ReverseIndex(); break;
                case 'c': FullReset(); break;
            }
        }

        private void FullReset()
        {
            altActive = false;
            main = NewScreen(Cols, Rows);
            alt = NewScreen(Cols, Rows);
            CursorX = CursorY = 0;
            pendingWrap = false;
            ResetModes();
        }

        // ---------------------------------------------------------- OSC

        /// <summary>ウィンドウタイトル(OSC 0 / 2)。今のところ表示には使っていない。</summary>
        public string Title { get; private set; }

        public void OscDispatch(string data)
        {
            int semi = data.IndexOf(';');
            if (semi < 0) return;
            string code = data.Substring(0, semi);
            if (code == "0" || code == "2") Title = data.Substring(semi + 1);
        }

        // ---------------------------------------------------------- CSI シーケンス

        public void CsiDispatch(char priv, List<int> ps, List<bool> sub, char intermediate, char final)
        {
            if (priv == '?')
            {
                if (intermediate != '\0') return;
                if (final == 'h' || final == 'l')
                {
                    foreach (int mode in ps) SetPrivateMode(mode, final == 'h');
                }
                else if (final == 'J') EraseInDisplay(Raw(ps, 0, 0));
                else if (final == 'K') EraseInLine(Raw(ps, 0, 0));
                else if (final == 'n' && Raw(ps, 0, 0) == 6)
                    SendReply("\x1b[?" + (CursorY + 1) + ";" + (CursorX + 1) + "R");
                return;
            }
            if (priv == '>')
            {
                if (final == 'c' && intermediate == '\0') SendReply("\x1b[>0;10;1c");
                return;
            }
            if (priv != '\0') return;

            if (intermediate == ' ')
            {
                if (final == 'q') CursorStyle = Raw(ps, 0, 0);
                return;
            }
            if (intermediate == '!')
            {
                if (final == 'p') SoftReset();
                return;
            }
            if (intermediate != '\0') return;

            switch (final)
            {
                case '@': InsertCells(P(ps, 0, 1)); break;
                case 'A': MoveCursor(0, -P(ps, 0, 1)); break;
                case 'B': MoveCursor(0, P(ps, 0, 1)); break;
                case 'C': MoveCursor(P(ps, 0, 1), 0); break;
                case 'D': MoveCursor(-P(ps, 0, 1), 0); break;
                case 'E':
                    MoveCursor(0, P(ps, 0, 1));
                    CursorX = 0;
                    break;
                case 'F':
                    MoveCursor(0, -P(ps, 0, 1));
                    CursorX = 0;
                    break;
                case 'G':
                case '`':
                    CursorX = Math.Min(P(ps, 0, 1) - 1, Cols - 1);
                    pendingWrap = false;
                    break;
                case 'H':
                case 'f':
                    SetCursor(P(ps, 1, 1) - 1, P(ps, 0, 1) - 1);
                    break;
                case 'I': TabForward(P(ps, 0, 1)); break;
                case 'J': EraseInDisplay(Raw(ps, 0, 0)); break;
                case 'K': EraseInLine(Raw(ps, 0, 0)); break;
                case 'L': InsertLines(P(ps, 0, 1)); break;
                case 'M': DeleteLines(P(ps, 0, 1)); break;
                case 'P': DeleteCells(P(ps, 0, 1)); break;
                case 'S': ScrollUp(P(ps, 0, 1)); break;
                case 'T':
                    if (ps.Count <= 1) ScrollDown(P(ps, 0, 1));
                    break;
                case 'X':
                    EraseCells(CursorY, CursorX, CursorX + P(ps, 0, 1));
                    break;
                case 'Z': TabBackward(P(ps, 0, 1)); break;
                case 'a': MoveCursor(P(ps, 0, 1), 0); break;
                case 'b':
                    for (int i = Math.Min(P(ps, 0, 1), 65535); i > 0; i--) Print(lastPrinted);
                    break;
                case 'c': SendReply("\x1b[?1;2c"); break;
                case 'd':
                    {
                        int x = CursorX;
                        SetCursor(x, P(ps, 0, 1) - 1);
                        break;
                    }
                case 'e': MoveCursor(0, P(ps, 0, 1)); break;
                case 'g':
                    {
                        int mode = Raw(ps, 0, 0);
                        if (mode == 0 && CursorX < Cols) tabStops[CursorX] = false;
                        else if (mode == 3) tabStops = new bool[Cols];
                        break;
                    }
                case 'h':
                case 'l':
                    foreach (int mode in ps)
                    {
                        if (mode == 4) InsertMode = final == 'h';
                    }
                    break;
                case 'm': Sgr(ps, sub); break;
                case 'n':
                    {
                        int q = Raw(ps, 0, 0);
                        if (q == 5) SendReply("\x1b[0n");
                        else if (q == 6)
                        {
                            int row = OriginMode ? CursorY - scrollTop : CursorY;
                            SendReply("\x1b[" + (row + 1) + ";" + (CursorX + 1) + "R");
                        }
                        break;
                    }
                case 'r':
                    {
                        int top = P(ps, 0, 1) - 1;
                        int bottom = (ps.Count > 1 && ps[1] > 0 ? ps[1] : Rows) - 1;
                        bottom = Math.Min(bottom, Rows - 1);
                        if (top < bottom)
                        {
                            scrollTop = top;
                            scrollBottom = bottom;
                            SetCursor(0, 0);
                        }
                        break;
                    }
                case 's':
                    if (ps.Count == 0) SaveCursor();
                    break;
                case 'u':
                    if (ps.Count == 0) RestoreCursor();
                    break;
            }
        }

        private void SoftReset()
        {
            CursorVisible = true;
            InsertMode = false;
            OriginMode = false;
            AutoWrap = true;
            AppCursorKeys = false;
            fg = bg = ColorCode.Default;
            flags = 0;
            scrollTop = 0;
            scrollBottom = Rows - 1;
            savedMain = savedAlt = null;
        }

        private void SetPrivateMode(int mode, bool on)
        {
            switch (mode)
            {
                case 1: AppCursorKeys = on; break;
                case 6:
                    OriginMode = on;
                    SetCursor(0, 0);
                    break;
                case 7: AutoWrap = on; break;
                case 25: CursorVisible = on; break;
                case 47:
                case 1047:
                    if (on) EnterAlt(false);
                    else LeaveAlt(false);
                    break;
                case 1049:
                    if (on) EnterAlt(true);
                    else LeaveAlt(true);
                    break;
                case 1048:
                    if (on) SaveCursor();
                    else RestoreCursor();
                    break;
                case 1000:
                case 1002:
                case 1003:
                    MouseTracking = on ? mode : 0;
                    break;
                case 1006: MouseSgr = on; break;
                case 1004: FocusReporting = on; break;
                case 2004: BracketedPaste = on; break;
            }
        }

        // ---------------------------------------------------------- SGR

        private void Sgr(List<int> ps, List<bool> sub)
        {
            if (ps.Count == 0)
            {
                fg = bg = ColorCode.Default;
                flags = 0;
                return;
            }

            for (int i = 0; i < ps.Count; i++)
            {
                int p = Math.Max(0, ps[i]);
                switch (p)
                {
                    case 0:
                        fg = bg = ColorCode.Default;
                        flags = 0;
                        break;
                    case 1: flags |= Attr.Bold; break;
                    case 2: flags |= Attr.Dim; break;
                    case 3: flags |= Attr.Italic; break;
                    case 4:
                        // 4:0 は下線なし、4:1〜4:5 は各種下線。
                        if (i + 1 < ps.Count && sub[i + 1])
                        {
                            if (ps[i + 1] == 0) flags &= unchecked((ushort)~Attr.Underline);
                            else flags |= Attr.Underline;
                            i++;
                            while (i + 1 < ps.Count && sub[i + 1]) i++;
                        }
                        else
                        {
                            flags |= Attr.Underline;
                        }
                        break;
                    case 5:
                    case 6: flags |= Attr.Blink; break;
                    case 7: flags |= Attr.Inverse; break;
                    case 8: flags |= Attr.Hidden; break;
                    case 9: flags |= Attr.Strike; break;
                    case 21: flags |= Attr.Underline; break;
                    case 22: flags &= unchecked((ushort)~(Attr.Bold | Attr.Dim)); break;
                    case 23: flags &= unchecked((ushort)~Attr.Italic); break;
                    case 24: flags &= unchecked((ushort)~Attr.Underline); break;
                    case 25: flags &= unchecked((ushort)~Attr.Blink); break;
                    case 27: flags &= unchecked((ushort)~Attr.Inverse); break;
                    case 28: flags &= unchecked((ushort)~Attr.Hidden); break;
                    case 29: flags &= unchecked((ushort)~Attr.Strike); break;
                    case 38:
                        {
                            int c = ExtendedColor(ps, sub, ref i);
                            if (c != int.MinValue) fg = c;
                            break;
                        }
                    case 39: fg = ColorCode.Default; break;
                    case 48:
                        {
                            int c = ExtendedColor(ps, sub, ref i);
                            if (c != int.MinValue) bg = c;
                            break;
                        }
                    case 49: bg = ColorCode.Default; break;
                    case 58:
                        // 下線色は使わないが、引数は読み飛ばす。
                        ExtendedColor(ps, sub, ref i);
                        break;
                    default:
                        if (p >= 30 && p <= 37) fg = p - 30;
                        else if (p >= 40 && p <= 47) bg = p - 40;
                        else if (p >= 90 && p <= 97) fg = p - 90 + 8;
                        else if (p >= 100 && p <= 107) bg = p - 100 + 8;
                        break;
                }
            }
        }

        /// <summary>
        /// 38/48 に続く拡張色を読む。`38;5;n` `38;2;r;g;b` と、
        /// コロン区切りの `38:5:n` `38:2::r:g:b` の両方に対応する。読めなければ int.MinValue。
        /// </summary>
        private static int ExtendedColor(List<int> ps, List<bool> sub, ref int i)
        {
            if (i + 1 < ps.Count && sub[i + 1])
            {
                var args = new List<int>();
                while (i + 1 < ps.Count && sub[i + 1])
                {
                    args.Add(Math.Max(0, ps[i + 1]));
                    i++;
                }
                if (args.Count >= 2 && args[0] == 5) return Math.Min(args[1], 255);
                if (args[0] == 2)
                {
                    // 38:2:<色空間>:r:g:b(色空間は省略されうる)
                    if (args.Count >= 5) return ColorCode.Rgb(args[2], args[3], args[4]);
                    if (args.Count == 4) return ColorCode.Rgb(args[1], args[2], args[3]);
                }
                return int.MinValue;
            }

            if (i + 1 >= ps.Count) return int.MinValue;
            int mode = ps[i + 1];
            if (mode == 5 && i + 2 < ps.Count)
            {
                int n = Math.Max(0, ps[i + 2]);
                i += 2;
                return Math.Min(n, 255);
            }
            if (mode == 2 && i + 4 < ps.Count)
            {
                int c = ColorCode.Rgb(Math.Max(0, ps[i + 2]), Math.Max(0, ps[i + 3]), Math.Max(0, ps[i + 4]));
                i += 4;
                return c;
            }
            i++;
            return int.MinValue;
        }

        // ---------------------------------------------------------- 補助

        /// <summary>省略・0 のときは既定値になる引数。</summary>
        private static int P(List<int> ps, int index, int def)
        {
            if (index >= ps.Count || ps[index] <= 0) return def;
            return ps[index];
        }

        /// <summary>0 に意味がある引数(省略時のみ既定値)。</summary>
        private static int Raw(List<int> ps, int index, int def)
        {
            if (index >= ps.Count || ps[index] < 0) return def;
            return ps[index];
        }

        private void SendReply(string s)
        {
            var handler = Reply;
            if (handler != null) handler(s);
        }
    }
}
