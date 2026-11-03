// 1 タブ = 1 端末セッション。TerminalView と ConPTY を結びつける。
// (TS 版 src/renderer/session.ts に相当)
//
// 持つのは「端末コントロール + ConPTY + タブ固有の状態(承認まち・入力メモ)」。

using System;
using System.IO;
using System.Windows.Forms;

namespace CcTerm
{
    internal sealed class TermSession
    {
        /// <summary>
        /// 承認まち判定を行う間隔(ms)。
        /// 出力のたびに画面全体を走査すると重いので、出力が落ち着いてから 1 回だけ見る。
        /// </summary>
        private const int DetectDelay = 150;

        public readonly TerminalView View;
        public readonly MemoRecorder Memo = new MemoRecorder();
        /// <summary>ステータスバーに出す cwd。</summary>
        public readonly string Cwd;
        /// <summary>タブ見出し(cwd のフォルダ名)。</summary>
        public readonly string Title;

        /// <summary>いま承認まちか。</summary>
        public bool Waiting;
        /// <summary>裏で承認まちになった等、ユーザに確認してほしい状態か(タブに ● を出す)。</summary>
        public bool Attention;

        /// <summary>承認まち状態が変わったときに呼ばれる(UI 更新用)。</summary>
        public event Action<TermSession> WaitingChanged;
        /// <summary>注意を促すべき事象(承認まちの立ち上がり・ベル)が起きたときに呼ばれる。</summary>
        public event Action<TermSession> Alert;
        /// <summary>シェルが終了した(UI スレッドで呼ばれる)。</summary>
        public event Action<TermSession> Exited;

        private readonly LaunchConfig launch;
        private readonly Timer detectTimer;
        private PseudoConsole pty;
        private bool disposed;

        public TermSession(LaunchConfig launch)
        {
            this.launch = launch;
            Cwd = launch.Cwd;
            Title = BaseName(launch.Cwd);

            View = new TerminalView();
            View.Dock = DockStyle.Fill;

            // キー入力・貼り付け → ConPTY。入力メモにも覗かせる。
            View.Input += delegate(string data)
            {
                // ESC CR(Ctrl+Enter などの改行)はメモ上も改行として残す
                // (1 件の複数行入力として記録される)。
                if (data == "\u001b\r") Memo.Append("\n");
                else Memo.Feed(data);
                Write(data);
            };
            View.Reply += Write;
            // ベルも「呼ばれている」合図として扱う。
            View.Bell += delegate { RaiseAlert(); };
            View.GridResized += delegate(int cols, int rows)
            {
                if (pty != null) pty.Resize(cols, rows);
            };
            View.OutputProcessed += delegate
            {
                detectTimer.Stop();
                detectTimer.Start();
            };

            detectTimer = new Timer();
            detectTimer.Interval = DetectDelay;
            detectTimer.Tick += delegate
            {
                detectTimer.Stop();
                Detect();
            };
        }

        /// <summary>ConPTY を起動して入出力を繋ぐ(View がフォームに載ってから呼ぶ)。</summary>
        public void Start()
        {
            pty = PseudoConsole.Start(launch, View.Cols, View.Rows);
            pty.Output += View.EnqueueOutput;
            pty.Exited += OnPtyExited;
            pty.Begin();
        }

        private void OnPtyExited()
        {
            // 読み取りスレッドから呼ばれるので UI スレッドへ移す。
            try
            {
                View.BeginInvoke(new MethodInvoker(delegate
                {
                    if (disposed) return;
                    var handler = Exited;
                    if (handler != null) handler(this);
                }));
            }
            catch (InvalidOperationException)
            {
                // 既にタブが閉じられている。
            }
        }

        private void Write(string data)
        {
            if (!disposed && pty != null) pty.Write(data);
        }

        /// <summary>ボタン等から挿入するテキスト(画像パスなど)。入力メモにも積む。</summary>
        public void InsertText(string text)
        {
            Memo.Append(text);
            Write(text);
            View.ScrollToBottom();
        }

        /// <summary>
        /// 承認まち判定。立ち上がり(false → true)のときだけ通知する(鳴りっぱなしを避けるため)。
        /// </summary>
        private void Detect()
        {
            if (disposed) return;
            bool waiting = CcTerm.Waiting.Detect(View.Buffer.ScreenText());
            if (waiting == Waiting) return;

            Waiting = waiting;
            var handler = WaitingChanged;
            if (handler != null) handler(this);
            if (waiting) RaiseAlert();
        }

        private void RaiseAlert()
        {
            var handler = Alert;
            if (handler != null) handler(this);
        }

        /// <summary>タブを閉じる / シェルが死んだ時の後始末。</summary>
        public void Dispose(bool killPty)
        {
            if (disposed) return;
            disposed = true;
            detectTimer.Stop();
            detectTimer.Dispose();
            if (killPty && pty != null) pty.Kill();
            View.Dispose();
        }

        /// <summary>パスの末尾要素(フォルダ名)を取り出す。</summary>
        private static string BaseName(string p)
        {
            string trimmed = p.TrimEnd('\\', '/');
            string name = Path.GetFileName(trimmed);
            if (!string.IsNullOrEmpty(name)) return name;
            return trimmed.Length > 0 ? trimmed : "shell";
        }
    }
}
