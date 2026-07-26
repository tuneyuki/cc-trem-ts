// 起動設定: どのプログラムを、どのフォルダで PTY 起動するかを決める。

import type { LaunchConfig } from '../shared/types';

const isWindows = process.platform === 'win32';

/** ホームディレクトリ。 */
export function homeDir(): string {
  return process.env.USERPROFILE || process.env.HOME || '.';
}

/**
 * アプリを起動したフォルダ。ここで claude を立ち上げる。
 * (Rust 版は起動時にフォルダ選択ダイアログを出していたが、TS 版では
 *  「立ち上げたフォルダでそのまま実行」に変更している)
 */
export function startupDir(): string {
  try {
    return process.cwd();
  } catch {
    return homeDir();
  }
}

/**
 * 選んだ cwd で claude を起動する設定。
 * `powershell -NoExit -Command claude` とすることで、claude 終了後もシェルに残れる。
 */
export function claudeLaunch(cwd: string): LaunchConfig {
  if (isWindows) {
    return {
      program: 'powershell.exe',
      args: ['-NoExit', '-Command', 'claude'],
      cwd,
    };
  }
  const shell = process.env.SHELL || '/bin/bash';
  // claude 終了後もシェルに残るよう、シェルを exec で置き換える。
  return { program: shell, args: ['-lc', 'claude; exec "$0" -l', shell], cwd };
}

/** 素のシェルを起動する設定(`--shell` モード)。 */
export function shellLaunch(cwd: string): LaunchConfig {
  if (isWindows) {
    return { program: 'powershell.exe', args: ['-NoExit'], cwd };
  }
  return { program: process.env.SHELL || '/bin/bash', args: ['-l'], cwd };
}
