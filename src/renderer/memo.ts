// 入力メモ: ユーザが打った 1 行を Enter 確定のタイミングで記録していく。
//
// xterm.js の onData は「端末へ送られる生バイト列」が流れてくるので、
// エスケープシーケンスを読み飛ばしつつ印字文字だけを拾う。

export class MemoRecorder {
  /** 現在編集中の行バッファ(Enter で確定してメモへ)。 */
  private buffer = '';
  /** 確定した入力メモ(時系列・古い順)。 */
  readonly memos: string[] = [];
  /** メモが増減したときに呼ばれる(パネルの再描画用)。 */
  onChange?: () => void;

  /** 端末へ送るデータを覗いて行バッファを更新する。 */
  feed(data: string): void {
    // bracketed paste のマーカーは中身ではないので取り除く。
    const s = data.replace(/\x1b\[20[01]~/g, '');

    for (let i = 0; i < s.length; i++) {
      const ch = s[i];

      if (ch === '\x1b') {
        // カーソルキー等のエスケープシーケンスは記録しない。
        i = skipEscape(s, i);
        continue;
      }
      if (ch === '\r' || ch === '\n') {
        this.commit();
        continue;
      }
      if (ch === '\x7f' || ch === '\b') {
        this.buffer = this.buffer.slice(0, -1);
        continue;
      }
      // Ctrl+C などの制御文字は記録しない。
      if (ch.charCodeAt(0) < 0x20) continue;

      this.buffer += ch;
    }
  }

  /** ボタン等から挿入したテキスト(画像パスなど)も行バッファに積む。 */
  append(text: string): void {
    this.buffer += text;
  }

  /** 行バッファを確定してメモに積む(空行・直前と同一は除く)。 */
  private commit(): void {
    const line = this.buffer.trim();
    this.buffer = '';
    if (!line) return;
    if (this.memos[this.memos.length - 1] === line) return;
    this.memos.push(line);
    this.onChange?.();
  }

  clear(): void {
    this.memos.length = 0;
    this.buffer = '';
    this.onChange?.();
  }

  /** エクスポート用テキスト(古い順・Windows で開きやすいよう CRLF)。 */
  toText(): string {
    return this.memos.join('\r\n') + '\r\n';
  }
}

/**
 * `start` 位置の ESC から始まるシーケンスの終端 index を返す。
 * 戻り値は for ループの i に代入される前提(次の反復で +1 される)。
 */
function skipEscape(s: string, start: number): number {
  const next = s[start + 1];

  // CSI: ESC [ ... 終端は @ ~ の範囲
  if (next === '[') {
    let i = start + 2;
    while (i < s.length) {
      const c = s.charCodeAt(i);
      if (c >= 0x40 && c <= 0x7e) return i;
      i++;
    }
    return s.length;
  }

  // OSC: ESC ] ... BEL または ESC \ で終端
  if (next === ']') {
    let i = start + 2;
    while (i < s.length) {
      if (s[i] === '\x07') return i;
      if (s[i] === '\x1b' && s[i + 1] === '\\') return i + 1;
      i++;
    }
    return s.length;
  }

  // SS3 (ESC O A など) は 3 文字、それ以外は 2 文字とみなす。
  if (next === 'O') return Math.min(start + 2, s.length - 1);
  return Math.min(start + 1, s.length - 1);
}
