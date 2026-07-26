// アプリ本体。タブ(TermSession)の管理と、周辺 UI の配線を行う。
// Rust 版 main.rs の MyTermApp に相当する。

import '@xterm/xterm/css/xterm.css';

import type { Bootstrap, LaunchConfig } from '../shared/types';
import { TermSession } from './session';
import { FONT_SIZE } from './theme';

const api = window.api;

class App {
  private readonly sessions: TermSession[] = [];
  private activeIndex = 0;

  private readonly termsEl = byId<HTMLDivElement>('terms');
  private readonly bannerEl = byId<HTMLDivElement>('waiting-banner');
  private readonly cwdEl = byId<HTMLSpanElement>('cwd');
  private readonly pasteImageEl = byId<HTMLButtonElement>('paste-image');
  private readonly tabsEl = byId<HTMLDivElement>('tabs');
  private readonly newTabEl = byId<HTMLButtonElement>('new-tab');
  private readonly memoEl = byId<HTMLElement>('memo');
  private readonly memoListEl = byId<HTMLUListElement>('memo-list');
  private readonly memoCountEl = byId<HTMLDivElement>('memo-count');
  private readonly memoExportEl = byId<HTMLButtonElement>('memo-export');
  private readonly memoClearEl = byId<HTMLButtonElement>('memo-clear');

  /**
   * メモパネルを出すか(全タブ共通、F1 で切替)。
   * 既定は非表示。端末を広く使いたいので、必要なときだけ開く。
   */
  private showMemo = false;

  /** 新規タブの多重生成を防ぐ(フォルダ選択の間に連打されうる)。 */
  private opening = false;

  constructor(private readonly boot: Bootstrap) {}

  /** 開発時の確認用。DevTools から `__myterm.debug()` で状態を覗ける。 */
  debug(): unknown {
    return {
      tabs: this.sessions.map((s) => ({
        title: s.title,
        waiting: s.waiting,
        attention: s.attention,
        screen: s.screenText().split('\n').filter((l) => l.trim()),
      })),
      active: this.activeIndex,
    };
  }

  /** 最初のタブを開いて、PTY からの出力を受け取り始める。 */
  async start(): Promise<void> {
    // main プロセスからの PTY 出力・終了通知を各セッションへ振り分ける。
    api.pty.onData((id, data) => this.find(id)?.receive(data));
    api.pty.onExit((id) => this.handleExit(id));

    this.bindStatusBar();
    this.bindDropTarget();
    this.bindMemoPanel();
    this.newTabEl.addEventListener('click', () => void this.openNewTab());

    await this.openTab(this.boot.initialLaunch);
  }

  private get active(): TermSession | undefined {
    return this.sessions[this.activeIndex];
  }

  /** `＋` から新しいタブを開く(claude モードではフォルダを選ばせる)。 */
  private async openNewTab(): Promise<void> {
    if (this.opening) return;
    this.opening = true;
    try {
      const launch = await api.newTabLaunch();
      if (launch) await this.openTab(launch);
    } finally {
      this.opening = false;
    }
  }

  /** 指定の起動設定で新しいタブを開き、アクティブにする。 */
  private async openTab(launch: LaunchConfig): Promise<void> {
    const session = new TermSession(launch, this.termsEl);
    session.onWaitingChange = (s) => this.handleWaitingChange(s);
    session.onAlert = (s) => this.handleAlert(s);
    session.onShortcut = (e) => this.handleShortcut(e);
    session.memo.onChange = () => {
      if (session === this.active) this.renderMemos();
    };
    this.sessions.push(session);
    this.setActive(this.sessions.length - 1);
    await session.start();
  }

  private setActive(index: number): void {
    this.activeIndex = index;
    this.sessions.forEach((s, i) => s.setActive(i === index));
    // 開いたタブの「要確認」は見たものとして解除する。
    const session = this.sessions[index];
    if (session) session.attention = false;
    this.cwdEl.textContent = session ? `cwd: ${session.cwd}` : '';
    this.cwdEl.title = session?.cwd ?? '';
    this.updateWaitingUi();
    this.renderTabs();
    this.renderMemos();
  }

  // ------------------------------------------------------------- 入力メモ

  private bindMemoPanel(): void {
    this.memoExportEl.addEventListener('click', () => void this.exportMemos());
    this.memoClearEl.addEventListener('click', () => {
      this.active?.memo.clear();
    });
    this.applyMemoVisibility();
  }

  /** F1 でパネルの表示を切り替える。端末の幅が変わるので fit も走る。 */
  private toggleMemo(): void {
    this.showMemo = !this.showMemo;
    this.applyMemoVisibility();
  }

  private applyMemoVisibility(): void {
    this.memoEl.classList.toggle('hidden', !this.showMemo);
    if (this.showMemo) this.renderMemos();
  }

  /**
   * アクティブタブのメモを描き直す。
   * チャットのように古い順に上から並べ、新しい入力は下に積まれる。
   */
  private renderMemos(): void {
    if (!this.showMemo) return;
    const memos = this.active?.memo.memos ?? [];

    // 描き直す前に「最下部を見ていたか」を覚えておく。
    // 上に遡って読んでいる最中に勝手に飛ばされると読めなくなるため。
    const list = this.memoListEl;
    const atBottom =
      list.scrollHeight - list.scrollTop - list.clientHeight < 8;

    this.memoCountEl.textContent = `${memos.length} 件  (F1 で表示切替)`;
    this.memoExportEl.disabled = memos.length === 0;

    if (memos.length === 0) {
      const li = document.createElement('li');
      li.className = 'empty';
      li.textContent = '(入力して Enter で記録されます)';
      list.replaceChildren(li);
      return;
    }

    list.replaceChildren(
      ...memos.map((text) => {
        const li = document.createElement('li');
        li.textContent = text;
        li.title = 'クリックでコピー';
        li.addEventListener('click', () => {
          api.copyText(text);
          // コピーできたことが分かるよう一瞬だけ色を変える。
          li.classList.add('copied');
          setTimeout(() => li.classList.remove('copied'), 400);
        });
        return li;
      }),
    );

    if (atBottom) list.scrollTop = list.scrollHeight;
  }

  /** メモをテキストファイルへ書き出す(保存先はダイアログ)。 */
  private async exportMemos(): Promise<void> {
    const memo = this.active?.memo;
    if (!memo || memo.memos.length === 0) return;
    await api.saveMemos(memo.toText());
  }

  // ------------------------------------------------------------- タブバー

  /** タブバーを組み立て直す。タブ数は多くないので毎回作り直して問題ない。 */
  private renderTabs(): void {
    this.tabsEl.replaceChildren(
      ...this.sessions.map((session, i) => {
        const tab = document.createElement('div');
        tab.className = 'tab';
        tab.classList.toggle('active', i === this.activeIndex);
        tab.classList.toggle('attention', session.attention);
        tab.title = session.cwd;
        tab.addEventListener('click', () => this.setActive(i));

        if (session.attention) {
          const mark = document.createElement('span');
          mark.className = 'mark';
          mark.textContent = '●';
          tab.appendChild(mark);
        }

        const label = document.createElement('span');
        label.className = 'label';
        label.textContent = session.title;
        tab.appendChild(label);

        // 1 つしかない時に閉じられると何も残らないので出さない(Rust 版と同じ)。
        if (this.sessions.length > 1) {
          const close = document.createElement('button');
          close.className = 'close';
          close.type = 'button';
          close.textContent = '×';
          close.title = 'タブを閉じる';
          close.addEventListener('click', (e) => {
            e.stopPropagation(); // タブの選択と二重に反応させない
            this.closeAt(i, true);
          });
          tab.appendChild(close);
        }

        return tab;
      }),
    );
  }

  /** アプリのショートカット。true を返すと端末には送らない。 */
  private handleShortcut(event: KeyboardEvent): boolean {
    if (event.key === 'F1') {
      this.toggleMemo();
      return true;
    }
    if (event.ctrlKey && event.key === 'Tab') {
      this.cycleTab(event.shiftKey ? -1 : 1);
      return true;
    }
    return false;
  }

  /** タブを前後に切り替える(端で折り返す)。 */
  private cycleTab(delta: number): void {
    const n = this.sessions.length;
    if (n < 2) return;
    this.setActive((this.activeIndex + delta + n) % n);
  }

  // ------------------------------------------------- ステータスバー / D&D

  private bindStatusBar(): void {
    this.pasteImageEl.addEventListener('click', () => {
      void this.pasteClipboardImage();
    });
  }

  /**
   * クリップボードの画像を一時 PNG に保存し、そのパスを端末へ挿入する。
   * claude にそのパスを読ませることで画像を渡せる。
   */
  private async pasteClipboardImage(): Promise<void> {
    const session = this.active;
    if (!session) return;
    const file = await api.clipboardImage();
    if (!file) {
      // 画像が無いときは音で軽く知らせる(Rust 版と同じ)。
      api.beep();
      return;
    }
    session.insertText(`${quotePath(file)} `);
    session.focus();
  }

  /** ファイルの D&D でパスを挿入する。 */
  private bindDropTarget(): void {
    const setDragging = (on: boolean) =>
      document.body.classList.toggle('dragging', on);

    // dragover で preventDefault しないと drop が発生しない。
    document.addEventListener('dragover', (e) => {
      e.preventDefault();
      setDragging(true);
    });
    document.addEventListener('dragleave', (e) => {
      // ウィンドウの外に出た時だけ解除する(子要素間の移動では消さない)。
      if (e.relatedTarget === null) setDragging(false);
    });
    document.addEventListener('drop', (e) => {
      e.preventDefault();
      setDragging(false);

      const session = this.active;
      if (!session || !e.dataTransfer) return;
      for (const file of e.dataTransfer.files) {
        // Electron 32 以降 File.path は無いので preload の API を使う。
        const path = api.getPathForFile(file);
        if (path) session.insertText(`${quotePath(path)} `);
      }
      session.focus();
    });
  }

  // ------------------------------------------------- 承認まちの検知と通知

  /**
   * 注意を促すべき事象(承認まちの立ち上がり・ベル)。
   * 目の前に見えている(アクティブ かつ ウィンドウにフォーカスがある)場合は
   * 鳴らしても意味がないので何もしない。
   */
  private handleAlert(session: TermSession): void {
    const isActive = session === this.active;
    if (isActive && document.hasFocus()) return;

    api.flash();
    api.beep();
    if (!isActive) {
      session.attention = true;
      this.renderTabs();
    }
  }

  private handleWaitingChange(session: TermSession): void {
    if (session === this.active) this.updateWaitingUi();
  }

  /** アクティブタブが承認まちの間だけ、枠線と上部バナーを出す。 */
  private updateWaitingUi(): void {
    const waiting = this.active?.waiting ?? false;
    this.termsEl.classList.toggle('waiting', waiting);
    this.bannerEl.classList.toggle('hidden', !waiting);
  }

  // ------------------------------------------------------- タブの後片付け

  private find(ptyId: number): TermSession | undefined {
    return this.sessions.find((s) => s.owns(ptyId));
  }

  /** PTY が終了したタブを片付ける。全部無くなったらウィンドウを閉じる。 */
  private handleExit(ptyId: number): void {
    const index = this.sessions.findIndex((s) => s.owns(ptyId));
    if (index < 0) return;
    this.closeAt(index, false);
  }

  private closeAt(index: number, killPty: boolean): void {
    const session = this.sessions[index];
    if (!session) return;
    session.dispose(killPty);
    this.sessions.splice(index, 1);

    if (this.sessions.length === 0) {
      api.closeWindow();
      return;
    }
    // 閉じたタブより後ろがアクティブだった場合は index がずれる。
    const next = Math.min(
      this.activeIndex > index ? this.activeIndex - 1 : this.activeIndex,
      this.sessions.length - 1,
    );
    this.setActive(next);
  }
}

/**
 * 端末へ挿入するパスを必要なら `"` で括る。
 * スペースを含むパスを裸で渡すと claude 側で 2 つの引数に割れてしまう。
 */
function quotePath(path: string): string {
  return /\s/.test(path) ? `"${path}"` : path;
}

/** 指定 id の要素を取る(無ければ設計ミスなので落とす)。 */
function byId<T extends HTMLElement>(id: string): T {
  const el = document.getElementById(id);
  if (!el) throw new Error(`element #${id} not found`);
  return el as T;
}

/**
 * 同梱フォントの読み込みを待つ。
 * xterm.js は生成時に文字の幅を実測するので、フォントが未ロードだと
 * フォールバックフォントで測ってしまい、桁数と描画がずれる。
 */
async function waitForFont(): Promise<void> {
  try {
    await document.fonts.load(`${FONT_SIZE}px 'Cascadia Mono'`);
    await document.fonts.ready;
  } catch {
    // 読めなくてもフォールバックフォントで動作はする。
  }
}

async function main(): Promise<void> {
  await waitForFont();
  const boot = await api.bootstrap();
  const app = new App(boot);
  // DevTools から状態を確認できるようにしておく(開発用のハンドル)。
  (window as unknown as { __myterm: App }).__myterm = app;
  await app.start();
}

void main();
