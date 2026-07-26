// 1 タブ = 1 端末セッション。xterm.js のインスタンスと PTY を結びつける。
//
// PTY 自体は main プロセスにいるので、ここが持つのは
// 「xterm.js + PTY への IPC + タブ固有の状態(承認まち・入力メモ)」。

import { FitAddon } from '@xterm/addon-fit';
import { Unicode11Addon } from '@xterm/addon-unicode11';
import { WebglAddon } from '@xterm/addon-webgl';
import { Terminal } from '@xterm/xterm';

import type { LaunchConfig } from '../shared/types';
import { MemoRecorder } from './memo';
import { FONT_FAMILY, FONT_SIZE, THEME } from './theme';
import { detectWaiting } from './waiting';

const api = window.api;

/** スクロールバック行数。 */
const SCROLLBACK = 10000;

/**
 * 承認まち判定を行う間隔(ms)。
 * 出力のたびに画面全体を走査すると重いので、出力が落ち着いてから 1 回だけ見る。
 */
const DETECT_DELAY = 150;

export class TermSession {
  /** #terms に差し込まれる、この端末の入れ物。 */
  readonly el: HTMLDivElement;
  readonly term: Terminal;
  readonly memo = new MemoRecorder();
  /** ステータスバーに出す cwd。 */
  readonly cwd: string;
  /** タブ見出し(cwd のフォルダ名)。 */
  readonly title: string;

  /** いま承認まちか。 */
  waiting = false;
  /** 裏で承認まちになった等、ユーザに確認してほしい状態か(タブに ● を出す)。 */
  attention = false;

  /** 承認まち状態が変わったときに呼ばれる(UI 更新用)。 */
  onWaitingChange?: (session: TermSession) => void;
  /** 注意を促すべき事象(承認まちの立ち上がり・ベル)が起きたときに呼ばれる。 */
  onAlert?: (session: TermSession) => void;
  /**
   * アプリ側のショートカット(F1・Ctrl+Tab など)を処理する機会。
   * true を返すと、そのキーは端末へ送らずアプリが受け取ったことになる。
   */
  onShortcut?: (event: KeyboardEvent) => boolean;

  /** main プロセス側の PTY セッション id。spawn 完了までは null。 */
  private ptyId: number | null = null;
  /** spawn 完了前に打たれた入力を貯めておく。 */
  private queued: string[] = [];
  /** 終了済み(PTY が死んだ)か。 */
  private disposed = false;

  private readonly fitAddon = new FitAddon();
  private readonly observer: ResizeObserver;
  private fitHandle = 0;
  private detectHandle: ReturnType<typeof setTimeout> | null = null;

  constructor(private readonly launch: LaunchConfig, parent: HTMLElement) {
    this.cwd = launch.cwd;
    this.title = baseName(launch.cwd);

    this.el = document.createElement('div');
    this.el.className = 'term';
    parent.appendChild(this.el);

    this.term = new Terminal({
      theme: THEME,
      fontFamily: FONT_FAMILY,
      fontSize: FONT_SIZE,
      scrollback: SCROLLBACK,
      cursorBlink: true,
      // Unicode11Addon を使うために必要。
      allowProposedApi: true,
      // ConPTY は行の折り返しを自前で扱うので、その前提を xterm にも伝える。
      ...(navigator.userAgent.includes('Windows')
        ? { windowsPty: { backend: 'conpty' as const } }
        : {}),
    });

    this.term.loadAddon(this.fitAddon);

    // 全角幅の判定を Unicode 11 に合わせる。claude が使う記号(❯ など)の
    // 幅が合っていないとカーソル位置がずれるため。
    const unicode11 = new Unicode11Addon();
    this.term.loadAddon(unicode11);
    this.term.unicode.activeVersion = '11';

    this.term.open(this.el);

    // GPU 描画。使えない環境(WebGL 無効など)では既定の canvas 描画に落ちる。
    try {
      const webgl = new WebglAddon();
      webgl.onContextLoss(() => webgl.dispose());
      this.term.loadAddon(webgl);
    } catch {
      // フォールバックするだけなので握りつぶす。
    }

    // キー入力・貼り付け → PTY。IME や bracketed paste は xterm.js が面倒を見る。
    this.term.onData((data) => {
      this.memo.feed(data);
      this.send(data);
    });

    // アプリのショートカットを先に拾う。false を返すと xterm は処理しない
    // (= PTY へ送られない)。keydown だけを見て二重処理を避ける。
    this.term.attachCustomKeyEventHandler((event) => {
      if (event.type !== 'keydown') return true;
      if (this.onShortcut?.(event)) {
        event.preventDefault();
        return false;
      }
      // Ctrl+Enter は「送信」ではなく「入力欄で改行」。
      // xterm は修飾キーを見ずに CR を送ってしまい claude が送信と解釈するので、
      // ここで横取りして ESC CR(= Alt+Enter 相当)を送る。
      if (event.key === 'Enter' && event.ctrlKey && !event.altKey) {
        this.sendSoftNewline();
        event.preventDefault();
        return false;
      }
      return true;
    });

    // ベルも「呼ばれている」合図として扱う。
    this.term.onBell(() => this.onAlert?.(this));

    // xterm 側のサイズが変わったら PTY にも伝える。
    this.term.onResize(({ cols, rows }) => {
      if (this.ptyId !== null) api.pty.resize(this.ptyId, cols, rows);
    });

    // ウィンドウリサイズやメモパネルの開閉に追従する。
    this.observer = new ResizeObserver(() => this.scheduleFit());
    this.observer.observe(this.el);
  }

  /** PTY を起動して入出力を繋ぐ。 */
  async start(): Promise<void> {
    this.fit();
    const { cols, rows } = this.term;
    this.ptyId = await api.pty.spawn(this.launch, cols, rows);

    // 起動待ちの間に打たれていた入力を流し込む。
    for (const data of this.queued) api.pty.write(this.ptyId, data);
    this.queued.length = 0;

    // fit と spawn の間にリサイズされていた場合に備えて、現在値で揃え直す。
    api.pty.resize(this.ptyId, this.term.cols, this.term.rows);
  }

  /** この PTY id のセッションか(main からの出力振り分けに使う)。 */
  owns(id: number): boolean {
    return this.ptyId === id;
  }

  /** PTY からの出力を画面に反映する。 */
  receive(data: string): void {
    this.term.write(data, () => this.scheduleDetect());
  }

  /**
   * 可視画面のテキスト(承認まち検知に使う)。
   * WebGL 描画では DOM に文字が残らないので、xterm.js のバッファから組み立てる。
   */
  screenText(): string {
    const buf = this.term.buffer.active;
    const lines: string[] = [];
    for (let y = 0; y < this.term.rows; y++) {
      const line = buf.getLine(buf.viewportY + y);
      lines.push(line ? line.translateToString(true) : '');
    }
    return lines.join('\n');
  }

  /** ボタン等から挿入するテキスト(画像パスなど)。入力メモにも積む。 */
  insertText(text: string): void {
    this.memo.append(text);
    this.send(text);
  }

  /**
   * claude の入力欄で改行する(送信はしない)。
   * ESC CR は Alt+Enter として送られるシーケンスで、claude はこれを改行として扱う。
   * 入力メモ側にも改行として残しておく(1 件の複数行入力として記録される)。
   */
  sendSoftNewline(): void {
    this.memo.append('\n');
    this.send('\x1b\r');
  }

  /** タブ切替。表示に戻した時はサイズが変わっている可能性があるので測り直す。 */
  setActive(active: boolean): void {
    this.el.classList.toggle('hidden', !active);
    if (active) {
      this.scheduleFit();
      this.term.focus();
    }
  }

  focus(): void {
    this.term.focus();
  }

  /** タブを閉じる / PTY が死んだ時の後始末。 */
  dispose(killPty: boolean): void {
    if (this.disposed) return;
    this.disposed = true;
    this.observer.disconnect();
    if (this.detectHandle) clearTimeout(this.detectHandle);
    if (killPty && this.ptyId !== null) api.pty.kill(this.ptyId);
    this.term.dispose();
    this.el.remove();
  }

  // ------------------------------------------------------------------ 内部

  private send(data: string): void {
    if (this.disposed) return;
    if (this.ptyId === null) this.queued.push(data);
    else api.pty.write(this.ptyId, data);
  }

  /**
   * 承認まち判定。出力が連続している間は先送りし、落ち着いたところで 1 回だけ走る。
   * 立ち上がり(false → true)のときだけ通知する(鳴りっぱなしを避けるため)。
   */
  private scheduleDetect(): void {
    if (this.detectHandle) clearTimeout(this.detectHandle);
    this.detectHandle = setTimeout(() => {
      this.detectHandle = null;
      if (this.disposed) return;

      const waiting = detectWaiting(this.screenText());
      if (waiting === this.waiting) return;

      this.waiting = waiting;
      this.onWaitingChange?.(this);
      if (waiting) this.onAlert?.(this);
    }, DETECT_DELAY);
  }

  /** リサイズは連続で飛んでくるので、次の描画フレームまで畳む。 */
  private scheduleFit(): void {
    if (this.fitHandle) return;
    this.fitHandle = requestAnimationFrame(() => {
      this.fitHandle = 0;
      this.fit();
    });
  }

  private fit(): void {
    if (this.disposed) return;
    // 非表示のタブは大きさが 0 になり、fit が不正な値を返すので触らない。
    if (this.el.offsetWidth === 0 || this.el.offsetHeight === 0) return;
    try {
      this.fitAddon.fit();
    } catch {
      // 生成直後などに稀に測定できないことがある。次のフレームで直る。
    }
  }
}

/** パスの末尾要素(フォルダ名)を取り出す。Windows / POSIX 両方の区切りに対応。 */
function baseName(p: string): string {
  const trimmed = p.replace(/[\\/]+$/, '');
  const parts = trimmed.split(/[\\/]/);
  return parts[parts.length - 1] || trimmed || 'shell';
}
