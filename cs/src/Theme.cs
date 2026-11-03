// 配色: 標準 xterm 相当のパレット(TS 版 src/renderer/theme.ts / style.css と同じ値)。
// xterm.js が持っていた 256 色の解決もここで行う。

using System;
using System.Drawing;

namespace CcTerm
{
    internal static class Theme
    {
        /// <summary>既定の前景色(明るいグレー)。</summary>
        public static readonly Color Fg = Color.FromArgb(0xd0, 0xd0, 0xd0);
        /// <summary>既定の背景色(ほぼ黒)。</summary>
        public static readonly Color Bg = Color.FromArgb(0x0c, 0x0c, 0x0c);
        public static readonly Color Cursor = Color.FromArgb(0xc0, 0xc0, 0xc0);
        public static readonly Color Selection = Color.FromArgb(0x3a, 0x4a, 0x66);
        /// <summary>承認まちを示すアクセント色(オレンジ)。</summary>
        public static readonly Color Accent = Color.FromArgb(0xff, 0x9d, 0x00);
        /// <summary>ドラッグ中にドロップ先を示す色。</summary>
        public static readonly Color Drop = Color.FromArgb(0x4a, 0x7a, 0xc0);

        /// <summary>タブバー・ステータスバーの背景。</summary>
        public static readonly Color Chrome = Color.FromArgb(0x1a, 0x1a, 0x1a);
        /// <summary>ホバー・アクティブ。</summary>
        public static readonly Color ChromeHi = Color.FromArgb(0x2a, 0x2a, 0x2a);
        public static readonly Color Border = Color.FromArgb(0x33, 0x33, 0x33);
        public static readonly Color Muted = Color.FromArgb(0x99, 0x99, 0x99);
        public static readonly Color Copied = Color.FromArgb(0x2f, 0x4a, 0x2f);

        /// <summary>端末の文字サイズ(96 DPI でのピクセル)。</summary>
        public const int FontPx = 15;

        /// <summary>256 色パレット(0xRRGGBB)。</summary>
        private static readonly int[] Palette = BuildPalette();

        private static int[] BuildPalette()
        {
            var p = new int[256];
            int[] basic =
            {
                0x000000, 0xcd0000, 0x00cd00, 0xcdcd00, 0x0000ee, 0xcd00cd, 0x00cdcd, 0xe5e5e5,
                0x7f7f7f, 0xff0000, 0x00ff00, 0xffff00, 0x5c5cff, 0xff00ff, 0x00ffff, 0xffffff,
            };
            Array.Copy(basic, p, 16);

            // 16〜231: 6x6x6 の色立方体
            int[] levels = { 0, 95, 135, 175, 215, 255 };
            for (int i = 0; i < 216; i++)
            {
                int r = levels[i / 36];
                int g = levels[(i / 6) % 6];
                int b = levels[i % 6];
                p[16 + i] = (r << 16) | (g << 8) | b;
            }
            // 232〜255: グレースケール
            for (int i = 0; i < 24; i++)
            {
                int v = 8 + i * 10;
                p[232 + i] = (v << 16) | (v << 8) | v;
            }
            return p;
        }

        public static int ToRgb(Color c)
        {
            return (c.R << 16) | (c.G << 8) | c.B;
        }

        /// <summary>セルの色コードを 0xRRGGBB に解決する。太字の基本 8 色は明るい側に寄せる。</summary>
        public static int Resolve(int code, bool foreground, bool bold)
        {
            if (code == ColorCode.Default) return ToRgb(foreground ? Fg : Bg);
            if ((code & ColorCode.RgbFlag) != 0) return code & 0xFFFFFF;
            if (foreground && bold && code < 8) code += 8;
            return Palette[code & 0xFF];
        }

        /// <summary>2 色の中間(薄字 SGR 2 用)。</summary>
        public static int Blend(int a, int b)
        {
            int r = (((a >> 16) & 0xFF) + ((b >> 16) & 0xFF)) / 2;
            int g = (((a >> 8) & 0xFF) + ((b >> 8) & 0xFF)) / 2;
            int bl = ((a & 0xFF) + (b & 0xFF)) / 2;
            return (r << 16) | (g << 8) | bl;
        }

        /// <summary>0xRRGGBB → GDI の COLORREF(0x00BBGGRR)。</summary>
        public static int ColorRef(int rgb)
        {
            return ((rgb & 0xFF) << 16) | (rgb & 0xFF00) | ((rgb >> 16) & 0xFF);
        }
    }
}
