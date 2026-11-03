// VT シーケンスのパーサ(vt100.net の DEC ANSI パーサの状態遷移を簡略化したもの)。
//
// 文字列を 1 文字ずつ読み、印字文字・C0 制御・ESC / CSI / OSC を
// TerminalBuffer の対応するメソッドへ振り分ける。DCS や APC などは読み捨てる。

using System.Collections.Generic;
using System.Text;

namespace CcTerm
{
    internal sealed class VtParser
    {
        private enum State
        {
            Ground,
            Escape,
            EscapeIntermediate,
            Csi,
            CsiIgnore,
            Osc,
            /// <summary>DCS / SOS / PM / APC。ST まで読み捨てる。</summary>
            IgnoreString,
        }

        private readonly TerminalBuffer buffer;
        private State state = State.Ground;

        private char intermediate;
        private char priv;
        private readonly List<int> ps = new List<int>();
        private readonly List<bool> sub = new List<bool>();
        private int current = -1;
        private bool currentIsSub;
        private readonly StringBuilder osc = new StringBuilder();
        /// <summary>文字列中で ESC を見た(次が \ なら ST)。</summary>
        private bool stringEsc;
        /// <summary>前回の入力の末尾が上位サロゲートだった場合の持ち越し。</summary>
        private char pendingHigh;

        public VtParser(TerminalBuffer buffer)
        {
            this.buffer = buffer;
        }

        public void Feed(string data)
        {
            for (int i = 0; i < data.Length; i++)
            {
                char c = data[i];

                if (state == State.Osc || state == State.IgnoreString)
                {
                    FeedString(c);
                    continue;
                }

                // C0 制御文字はどの状態でも(シーケンスの途中でも)実行する。
                if (c < 0x20)
                {
                    if (c == '\x1b')
                    {
                        BeginEscape();
                        continue;
                    }
                    if (c == '\x18' || c == '\x1a')
                    {
                        // CAN / SUB はシーケンスを打ち切る。
                        state = State.Ground;
                        continue;
                    }
                    buffer.Execute(c);
                    continue;
                }
                if (c == '\x7f') continue;

                switch (state)
                {
                    case State.Ground:
                        PrintChar(c);
                        break;
                    case State.Escape:
                        EscapeChar(c);
                        break;
                    case State.EscapeIntermediate:
                        if (c >= 0x20 && c <= 0x2F) break;
                        buffer.EscDispatch(intermediate, c);
                        state = State.Ground;
                        break;
                    case State.Csi:
                        CsiChar(c);
                        break;
                    case State.CsiIgnore:
                        if (c >= 0x40 && c <= 0x7E) state = State.Ground;
                        break;
                }
            }
        }

        private void PrintChar(char c)
        {
            if (char.IsHighSurrogate(c))
            {
                pendingHigh = c;
                return;
            }
            if (char.IsLowSurrogate(c))
            {
                if (pendingHigh != '\0')
                {
                    int cp = char.ConvertToUtf32(pendingHigh, c);
                    pendingHigh = '\0';
                    buffer.Print(cp);
                }
                return;
            }
            pendingHigh = '\0';
            buffer.Print(c);
        }

        private void BeginEscape()
        {
            state = State.Escape;
            intermediate = '\0';
        }

        private void EscapeChar(char c)
        {
            switch (c)
            {
                case '[':
                    state = State.Csi;
                    priv = '\0';
                    intermediate = '\0';
                    ps.Clear();
                    sub.Clear();
                    current = -1;
                    currentIsSub = false;
                    return;
                case ']':
                    state = State.Osc;
                    osc.Length = 0;
                    stringEsc = false;
                    return;
                case 'P':
                case 'X':
                case '^':
                case '_':
                    state = State.IgnoreString;
                    stringEsc = false;
                    return;
            }
            if (c >= 0x20 && c <= 0x2F)
            {
                intermediate = c;
                state = State.EscapeIntermediate;
                return;
            }
            buffer.EscDispatch('\0', c);
            state = State.Ground;
        }

        private void CsiChar(char c)
        {
            if (c >= '0' && c <= '9')
            {
                if (current < 0) current = 0;
                if (current < 100000) current = current * 10 + (c - '0');
                return;
            }
            if (c == ';' || c == ':')
            {
                PushParam();
                currentIsSub = c == ':';
                return;
            }
            if (c >= '<' && c <= '?')
            {
                // 私用マーカーは先頭にだけ来る。途中に来たら不正なので読み捨てる。
                if (ps.Count == 0 && current < 0 && priv == '\0') priv = c;
                else state = State.CsiIgnore;
                return;
            }
            if (c >= 0x20 && c <= 0x2F)
            {
                intermediate = c;
                return;
            }
            if (c >= 0x40 && c <= 0x7E)
            {
                if (current >= 0 || ps.Count > 0) PushParam();
                buffer.CsiDispatch(priv, ps, sub, intermediate, c);
                state = State.Ground;
                return;
            }
            state = State.CsiIgnore;
        }

        private void PushParam()
        {
            ps.Add(current);
            sub.Add(currentIsSub);
            current = -1;
            currentIsSub = false;
        }

        /// <summary>OSC / DCS などの文字列。BEL か ST(ESC \)で終わる。</summary>
        private void FeedString(char c)
        {
            if (stringEsc)
            {
                stringEsc = false;
                if (c == '\\')
                {
                    EndString();
                    return;
                }
                // ST 以外の ESC は文字列を打ち切り、新しいシーケンスとして読み直す。
                EndString();
                BeginEscape();
                EscapeChar(c);
                return;
            }
            if (c == '\x1b')
            {
                stringEsc = true;
                return;
            }
            if (c == '\x07')
            {
                EndString();
                return;
            }
            if (state == State.Osc && osc.Length < 4096) osc.Append(c);
        }

        private void EndString()
        {
            if (state == State.Osc) buffer.OscDispatch(osc.ToString());
            osc.Length = 0;
            state = State.Ground;
        }
    }
}
