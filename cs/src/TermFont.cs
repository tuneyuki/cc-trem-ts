// 端末用の GDI フォント一式。
//
// 欧文は同梱の Cascadia Mono(バックスラッシュ等が正しく、配布時も依存なし)。
// 和文や記号は Cascadia に無いので、グリフの有無を調べてフォールバックフォントで描く。
//
// 同梱フォントは AddFontResourceEx(FR_PRIVATE) でこのプロセスにだけ読み込む。
// GDI+(System.Drawing.Font)からは見えないため、描画は GDI の ExtTextOut で行う。

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace CcTerm
{
    internal sealed class TermFont
    {
        private const string PrimaryFace = "Cascadia Mono";
        private const string PrimaryFallbackFace = "Consolas";

        /// <summary>和文・記号のフォールバック候補(先にあるものを優先)。</summary>
        private static readonly string[] FallbackFaces =
        {
            "BIZ UDGothic", "MS Gothic", "Yu Gothic", "Meiryo", "Segoe UI Symbol", "Segoe UI Emoji",
        };

        private static readonly Dictionary<int, TermFont> cache = new Dictionary<int, TermFont>();
        private static bool privateFontTried;

        public readonly int CellWidth;
        public readonly int CellHeight;
        public readonly int Ascent;
        public readonly Native.LOGFONT LogFont;

        private readonly IntPtr regular;
        private readonly IntPtr bold;
        private readonly IntPtr[] fallbacks;
        private readonly int px;
        private readonly IntPtr measureDc;
        /// <summary>コードポイント → 使うフォント(0 = 主フォント、1〜 = フォールバック)。</summary>
        private readonly Dictionary<int, int> fontOf = new Dictionary<int, int>();
        private readonly ushort[] glyphBuf = new ushort[2];

        /// <summary>DPI ごとに 1 つ作って使い回す。</summary>
        public static TermFont Get(int dpi)
        {
            TermFont f;
            if (!cache.TryGetValue(dpi, out f))
            {
                f = new TermFont(dpi);
                cache[dpi] = f;
            }
            return f;
        }

        private TermFont(int dpi)
        {
            LoadPrivateFont();
            int px = Theme.FontPx * dpi / 96;

            IntPtr screen = Native.GetDC(IntPtr.Zero);
            measureDc = Native.CreateCompatibleDC(screen);
            Native.ReleaseDC(IntPtr.Zero, screen);

            string face = PrimaryFace;
            regular = Create(face, px, Native.FW_NORMAL);
            if (!FaceIs(regular, face))
            {
                Native.DeleteObject(regular);
                face = PrimaryFallbackFace;
                regular = Create(face, px, Native.FW_NORMAL);
            }
            bold = Create(face, px, Native.FW_BOLD);
            LogFont = MakeLogFont(face, px, Native.FW_NORMAL);

            // フォールバックは和文や記号が初めて出てきたときに作る。
            fallbacks = new IntPtr[FallbackFaces.Length];
            this.px = px;

            Native.SelectObject(measureDc, regular);
            Native.TEXTMETRIC tm;
            Native.GetTextMetrics(measureDc, out tm);
            Native.SIZE size;
            Native.GetTextExtentPoint32(measureDc, "M", 1, out size);
            CellWidth = Math.Max(1, size.cx);
            CellHeight = Math.Max(1, tm.tmHeight);
            Ascent = tm.tmAscent;
        }

        /// <summary>同梱の Cascadia Mono をこのプロセス専用に読み込む(exe の隣の assets から)。</summary>
        private static void LoadPrivateFont()
        {
            if (privateFontTried) return;
            privateFontTried = true;
            try
            {
                string path = Path.Combine(
                    Path.GetDirectoryName(Application.ExecutablePath), "assets", "CascadiaMono-Regular.ttf");
                if (File.Exists(path)) Native.AddFontResourceEx(path, Native.FR_PRIVATE, IntPtr.Zero);
            }
            catch
            {
                // 読めなくてもシステムのフォントで動作はする。
            }
        }

        private static Native.LOGFONT MakeLogFont(string face, int px, int weight)
        {
            var lf = new Native.LOGFONT();
            lf.lfHeight = -px;
            lf.lfWeight = weight;
            lf.lfCharSet = Native.DEFAULT_CHARSET;
            lf.lfQuality = Native.CLEARTYPE_QUALITY;
            lf.lfPitchAndFamily = Native.FIXED_PITCH;
            lf.lfFaceName = face;
            return lf;
        }

        private static IntPtr Create(string face, int px, int weight)
        {
            var lf = MakeLogFont(face, px, weight);
            return Native.CreateFontIndirect(ref lf);
        }

        private bool FaceIs(IntPtr font, string face)
        {
            IntPtr old = Native.SelectObject(measureDc, font);
            var sb = new StringBuilder(64);
            Native.GetTextFace(measureDc, sb.Capacity, sb);
            Native.SelectObject(measureDc, old);
            return string.Equals(sb.ToString(), face, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>フォント番号(FontIndexFor の戻り値)に対応する HFONT。</summary>
        public IntPtr Handle(int index, bool isBold)
        {
            if (index <= 0) return isBold ? bold : regular;
            return Fallback(index - 1);
        }

        private IntPtr Fallback(int i)
        {
            if (fallbacks[i] == IntPtr.Zero) fallbacks[i] = Create(FallbackFaces[i], px, Native.FW_NORMAL);
            return fallbacks[i];
        }

        /// <summary>このコードポイントを描くフォント番号。グリフを持つ最初のフォントを選ぶ。</summary>
        public int FontIndexFor(int cp)
        {
            if (cp < 0x80) return 0;
            int index;
            if (fontOf.TryGetValue(cp, out index)) return index;

            index = 0;
            if (cp > 0xFFFF)
            {
                // BMP 外(主に絵文字)は GetGlyphIndices で調べられないので絵文字フォントに任せる。
                index = fallbacks.Length;
            }
            else if (!HasGlyph(regular, cp))
            {
                for (int i = 0; i < fallbacks.Length; i++)
                {
                    if (HasGlyph(Fallback(i), cp))
                    {
                        index = i + 1;
                        break;
                    }
                }
            }
            fontOf[cp] = index;
            return index;
        }

        private bool HasGlyph(IntPtr font, int cp)
        {
            IntPtr old = Native.SelectObject(measureDc, font);
            uint n = Native.GetGlyphIndices(
                measureDc, ((char)cp).ToString(), 1, glyphBuf, Native.GGI_MARK_NONEXISTING_GLYPHS);
            Native.SelectObject(measureDc, old);
            return n != 0xFFFFFFFF && glyphBuf[0] != 0xFFFF;
        }
    }
}
