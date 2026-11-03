// 入力メモ: ユーザが打った 1 行を Enter 確定のタイミングで記録していく。
// (TS 版 src/renderer/memo.ts の移植)
//
// 端末へ送られる生の文字列を覗くので、エスケープシーケンスを読み飛ばしつつ
// 印字文字だけを拾う。

using System;
using System.Collections.Generic;
using System.Text;

namespace CcTerm
{
    internal sealed class MemoRecorder
    {
        /// <summary>現在編集中の行バッファ(Enter で確定してメモへ)。</summary>
        private readonly StringBuilder buffer = new StringBuilder();
        /// <summary>確定した入力メモ(時系列・古い順)。</summary>
        public readonly List<string> Memos = new List<string>();
        /// <summary>メモが増減したときに呼ばれる(パネルの再描画用)。</summary>
        public event Action Changed;

        /// <summary>端末へ送るデータを覗いて行バッファを更新する。</summary>
        public void Feed(string data)
        {
            // bracketed paste のマーカーは中身ではないので取り除く。
            string s = data.Replace("\u001b[200~", "").Replace("\u001b[201~", "");

            for (int i = 0; i < s.Length; i++)
            {
                char ch = s[i];

                if (ch == '\u001b')
                {
                    // カーソルキー等のエスケープシーケンスは記録しない。
                    i = SkipEscape(s, i);
                    continue;
                }
                if (ch == '\r' || ch == '\n')
                {
                    Commit();
                    continue;
                }
                if (ch == '\u007f' || ch == '\b')
                {
                    if (buffer.Length > 0) buffer.Length--;
                    continue;
                }
                // Ctrl+C などの制御文字は記録しない。
                if (ch < 0x20) continue;

                buffer.Append(ch);
            }
        }

        /// <summary>ボタン等から挿入したテキスト(画像パスや改行)も行バッファに積む。</summary>
        public void Append(string text)
        {
            buffer.Append(text);
        }

        /// <summary>行バッファを確定してメモに積む(空行・直前と同一は除く)。</summary>
        private void Commit()
        {
            string line = buffer.ToString().Trim();
            buffer.Length = 0;
            if (line.Length == 0) return;
            if (Memos.Count > 0 && Memos[Memos.Count - 1] == line) return;
            Memos.Add(line);
            RaiseChanged();
        }

        public void Clear()
        {
            Memos.Clear();
            buffer.Length = 0;
            RaiseChanged();
        }

        /// <summary>エクスポート用テキスト(古い順・Windows で開きやすいよう CRLF)。</summary>
        public string ToText()
        {
            var sb = new StringBuilder();
            foreach (var m in Memos)
            {
                sb.Append(m.Replace("\r\n", "\n").Replace("\n", "\r\n"));
                sb.Append("\r\n");
            }
            return sb.ToString();
        }

        private void RaiseChanged()
        {
            var handler = Changed;
            if (handler != null) handler();
        }

        /// <summary>
        /// start 位置の ESC から始まるシーケンスの終端 index を返す。
        /// 戻り値は for ループの i に代入される前提(次の反復で +1 される)。
        /// </summary>
        private static int SkipEscape(string s, int start)
        {
            char next = start + 1 < s.Length ? s[start + 1] : '\0';

            // CSI: ESC [ ... 終端は @ 〜 ~ の範囲
            if (next == '[')
            {
                for (int i = start + 2; i < s.Length; i++)
                {
                    if (s[i] >= 0x40 && s[i] <= 0x7e) return i;
                }
                return s.Length;
            }

            // OSC: ESC ] ... BEL または ESC \ で終端
            if (next == ']')
            {
                for (int i = start + 2; i < s.Length; i++)
                {
                    if (s[i] == '\u0007') return i;
                    if (s[i] == '\u001b' && i + 1 < s.Length && s[i + 1] == '\\') return i + 1;
                }
                return s.Length;
            }

            // SS3 (ESC O A など) は 3 文字、それ以外は 2 文字とみなす。
            if (next == 'O') return Math.Min(start + 2, s.Length - 1);
            return Math.Min(start + 1, s.Length - 1);
        }
    }
}
