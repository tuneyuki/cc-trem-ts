// PTY セッション管理。Rust 版の term.rs のうち「PTY 起動・読み書き」に相当する部分。
// ANSI の解釈と画面グリッド保持は renderer 側の xterm.js が担当する。

import * as nodePty from '@lydell/node-pty';
import type { LaunchConfig } from '../shared/types';

type Emit = (channel: string, ...args: unknown[]) => void;

export class PtyManager {
  private sessions = new Map<number, nodePty.IPty>();
  private nextId = 1;

  constructor(private emit: Emit) {}

  /** 指定プログラムを PTY 上で起動し、出力を renderer へ流し始める。 */
  spawn(launch: LaunchConfig, cols: number, rows: number): number {
    const id = this.nextId++;

    const proc = nodePty.spawn(launch.program, launch.args, {
      name: 'xterm-256color',
      cols: Math.max(1, cols),
      rows: Math.max(1, rows),
      cwd: launch.cwd,
      env: { ...process.env, TERM: 'xterm-256color' } as Record<string, string>,
    });

    // PTY -> renderer(xterm.js が ANSI を解釈して画面を作る)。
    proc.onData((data) => this.emit('pty:data', id, data));
    proc.onExit(() => {
      this.sessions.delete(id);
      this.emit('pty:exit', id);
    });

    this.sessions.set(id, proc);
    return id;
  }

  /** シェルへ文字列を送る。 */
  write(id: number, data: string): void {
    this.sessions.get(id)?.write(data);
  }

  /** 端末のサイズ変更を PTY に伝える。 */
  resize(id: number, cols: number, rows: number): void {
    const proc = this.sessions.get(id);
    if (!proc) return;
    try {
      proc.resize(Math.max(1, cols), Math.max(1, rows));
    } catch {
      // 終了直後などにサイズ変更が飛んでくることがあるので握りつぶす。
    }
  }

  /** セッションを終了させる(タブを閉じた時)。 */
  kill(id: number): void {
    const proc = this.sessions.get(id);
    if (!proc) return;
    this.sessions.delete(id);
    try {
      proc.kill();
    } catch {
      // 既に死んでいる場合。
    }
  }

  /** 全セッションを終了(アプリ終了時)。 */
  killAll(): void {
    for (const id of [...this.sessions.keys()]) {
      this.kill(id);
    }
  }
}
