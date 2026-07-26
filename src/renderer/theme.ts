// 配色: 標準 xterm 相当のパレット。
// xterm.js が 256 色の解決を自前で持っているので、
// ここでは基本 16 色と前景/背景/カーソルだけ指定すればよい。

import type { ITheme } from '@xterm/xterm';

/** 既定の前景色(明るいグレー)。 */
export const DEFAULT_FG = '#d0d0d0';
/** 既定の背景色(ほぼ黒)。 */
export const DEFAULT_BG = '#0c0c0c';
/** カーソル色。 */
export const CURSOR = '#c0c0c0';
/** 承認まちを示すアクセント色(オレンジ)。 */
export const ACCENT = '#ff9d00';

export const THEME: ITheme = {
  foreground: DEFAULT_FG,
  background: DEFAULT_BG,
  cursor: CURSOR,
  cursorAccent: DEFAULT_BG,
  selectionBackground: '#3a4a66',

  black: '#000000',
  red: '#cd0000',
  green: '#00cd00',
  yellow: '#cdcd00',
  blue: '#0000ee',
  magenta: '#cd00cd',
  cyan: '#00cdcd',
  white: '#e5e5e5',

  brightBlack: '#7f7f7f',
  brightRed: '#ff0000',
  brightGreen: '#00ff00',
  brightYellow: '#ffff00',
  brightBlue: '#5c5cff',
  brightMagenta: '#ff00ff',
  brightCyan: '#00ffff',
  brightWhite: '#ffffff',
};

/**
 * 欧文は同梱の Cascadia Mono(バックスラッシュ等が正しく、配布時も依存なし)。
 * 和文は Cascadia に無いためシステムの等幅和文フォントへフォールバックする。
 */
export const FONT_FAMILY =
  "'Cascadia Mono', 'BIZ UDGothic', 'MS Gothic', 'Meiryo', 'Yu Gothic', monospace";

export const FONT_SIZE = 15;
