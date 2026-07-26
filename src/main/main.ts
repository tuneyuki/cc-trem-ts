// my-term-ts : Claude Code 特化ターミナル(Electron / TypeScript)
//
// 構成:
//   ③ @lydell/node-pty : シェルを PTY(ConPTY) で起動して読み書き
//   ② xterm.js         : バイト列を解釈して画面グリッド状態を保持
//   ① Electron/HTML    : グリッド描画・キー入力を PTY へ送る
//
// このファイルは main プロセス。ウィンドウ生成・PTY 管理・OS 機能(ダイアログ、
// クリップボード画像、ビープ、タスクバー点滅)を担当する。

import { app, BrowserWindow, clipboard, dialog, ipcMain, shell } from 'electron';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';

import { claudeLaunch, homeDir, shellLaunch, startupDir } from './launch';
import { PtyManager } from './pty';
import type { Bootstrap, LaunchConfig } from '../shared/types';

/** `--shell` 指定時はダイアログを出さず、ホームで素のシェルを起動する。 */
const plainShell = process.argv.includes('--shell');

let win: BrowserWindow | null = null;
let ptys: PtyManager | null = null;

function createWindow(): void {
  win = new BrowserWindow({
    width: 960,
    height: 600,
    title: 'my-term',
    backgroundColor: '#0c0c0c',
    autoHideMenuBar: true,
    webPreferences: {
      preload: path.join(__dirname, '../preload/preload.js'),
      contextIsolation: true,
      nodeIntegration: false,
      spellcheck: false,
    },
  });

  ptys = new PtyManager((channel, ...args) => {
    // ウィンドウが閉じた後に PTY の出力が届くことがあるので破棄済みを確認する。
    if (win && !win.isDestroyed()) {
      win.webContents.send(channel, ...args);
    }
  });

  win.on('closed', () => {
    ptys?.killAll();
    ptys = null;
    win = null;
  });

  // 端末に文字を打ち込むだけなので、外部リンクは既定ブラウザに逃がす。
  win.webContents.setWindowOpenHandler(({ url }) => {
    void shell.openExternal(url);
    return { action: 'deny' };
  });

  void win.loadFile(path.join(__dirname, '../renderer/index.html'));
}

app.whenReady().then(() => {
  createWindow();
  app.on('activate', () => {
    if (BrowserWindow.getAllWindows().length === 0) createWindow();
  });
});

app.on('window-all-closed', () => {
  ptys?.killAll();
  app.quit();
});

app.on('before-quit', () => {
  ptys?.killAll();
});

// ---------------------------------------------------------------- IPC: 起動情報

ipcMain.handle('app:bootstrap', (): Bootstrap => {
  const cwd = plainShell ? homeDir() : startupDir();
  return {
    plainShell,
    initialLaunch: plainShell ? shellLaunch(cwd) : claudeLaunch(cwd),
  };
});

// ---------------------------------------------------------------- IPC: PTY

ipcMain.handle(
  'pty:spawn',
  (_e, launch: LaunchConfig, cols: number, rows: number): number => {
    if (!ptys) throw new Error('pty manager is not ready');
    return ptys.spawn(launch, cols, rows);
  },
);

ipcMain.on('pty:write', (_e, id: number, data: string) => ptys?.write(id, data));
ipcMain.on('pty:resize', (_e, id: number, cols: number, rows: number) =>
  ptys?.resize(id, cols, rows),
);
ipcMain.on('pty:kill', (_e, id: number) => ptys?.kill(id));

// ---------------------------------------------------------------- IPC: ダイアログ

/**
 * 新規タブの起動設定。素シェルモードならホームでそのまま開き、
 * claude モードなら開くフォルダを選ばせる(キャンセルなら null)。
 */
ipcMain.handle('app:newTabLaunch', async (): Promise<LaunchConfig | null> => {
  if (plainShell) return shellLaunch(homeDir());
  if (!win) return null;

  const result = await dialog.showOpenDialog(win, {
    title: 'Claude Code を起動するフォルダを選択',
    defaultPath: startupDir(),
    properties: ['openDirectory'],
  });
  if (result.canceled || result.filePaths.length === 0) return null;
  return claudeLaunch(result.filePaths[0]);
});

/** 入力メモをテキストファイルへ書き出す(保存先はダイアログで選択)。 */
ipcMain.handle('dialog:saveMemos', async (_e, text: string): Promise<boolean> => {
  if (!win) return false;
  const result = await dialog.showSaveDialog(win, {
    title: '入力メモの保存先',
    defaultPath: 'input-memo.txt',
    filters: [{ name: 'テキスト', extensions: ['txt'] }],
  });
  if (result.canceled || !result.filePath) return false;
  await fs.promises.writeFile(result.filePath, text, 'utf8');
  return true;
});

// ---------------------------------------------------------------- IPC: OS 機能

/** クリップボードの画像を一時 PNG に保存し、そのパスを返す。 */
ipcMain.handle('clipboard:image', async (): Promise<string | null> => {
  const image = clipboard.readImage();
  if (image.isEmpty()) return null;
  const file = path.join(os.tmpdir(), `my-term-paste-${Date.now()}.png`);
  await fs.promises.writeFile(file, image.toPNG());
  return file;
});

ipcMain.on('clipboard:copy', (_e, text: string) => clipboard.writeText(text));

ipcMain.on('app:beep', () => shell.beep());

ipcMain.on('app:flash', () => {
  // 既にフォーカスがあるなら点滅させても意味がない。
  if (win && !win.isDestroyed() && !win.isFocused()) {
    win.flashFrame(true);
  }
});

ipcMain.on('app:close', () => {
  if (win && !win.isDestroyed()) win.close();
});
