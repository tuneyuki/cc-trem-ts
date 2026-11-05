// メインウィンドウ。1 ウィンドウ = 1 セッションで、周辺 UI の配線を行う。
// (TS 版 src/renderer/renderer.ts と src/main/main.ts の OS 機能部分に相当)
//
//   中央: 端末(左)+ 入力メモ(右、F1 で開閉)/ 下: ステータスバー
//
// 軽量化のため、タブは持たない(別のフォルダで使うときはもう 1 つ起動する)。
// 複数起動したときに見分けやすいよう、起動時に画面の 4 分割の空いている区画へ収まり、
// ウィンドウごとに色を付け、タイトルに claude の作業内容を出す。
// 入力メモのパネルは F1 で初めて開いたときに作る。

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace CcTerm
{
    internal sealed class MainForm : Form
    {
        private readonly bool plainShell;
        private readonly float scale;
        private TermSession session;

        /// <summary>メモパネルを出すか(F1 で切替)。既定は非表示。端末を広く使いたいので必要なときだけ開く。</summary>
        private bool showMemo;
        /// <summary>ファイルをドラッグ中か(ドロップ先を枠で示す)。</summary>
        private bool dragging;

        private readonly Panel termHost;
        private readonly Label banner;
        private readonly Label cwdLabel;
        private readonly Panel status;
        private readonly Panel statusInner;

        /// <summary>4 分割のどの区画にいるか(-1 は区画なし)。</summary>
        private int slot = -1;
        /// <summary>このウィンドウの色(Theme.Tints の番号)。</summary>
        private int colorIndex;
        /// <summary>色を付けた端末の背景色。</summary>
        private Color termBg = Theme.Bg;

        // 入力メモのパネル(初めて開くまで null)。
        private Panel memoPanel;
        private Label memoCount;
        private Button memoExport;
        private ListBox memoList;
        private Timer memoCopiedTimer;
        private int memoHover = -1;
        private int memoCopied = -1;

        /// <summary>メモが空のときに出す案内(クリックしてもコピーしない)。</summary>
        private sealed class EmptyMemo
        {
            public override string ToString()
            {
                return "(入力して Enter で記録されます)";
            }
        }

        public MainForm(bool plainShell, double opacity)
        {
            this.plainShell = plainShell;
            using (var g = CreateGraphics()) scale = g.DpiX / 96f;

            Text = "my-term";
            BackColor = Theme.Bg;
            ForeColor = Theme.Fg;
            Font = SystemFonts.MessageBoxFont;
            ClientSize = new Size(S(960), S(600));
            StartPosition = FormStartPosition.WindowsDefaultLocation;
            // ウィンドウ全体を少し透かして、うっすらデスクトップが見えるようにする。
            if (opacity < 1.0) Opacity = opacity;

            SuspendLayout();

            // --- 中央: 端末領域
            termHost = new Panel();
            termHost.Dock = DockStyle.Fill;
            termHost.BackColor = Theme.Bg;

            // 承認まちの帯。端末の上に重ねて出す(端末の大きさは変えない)。
            banner = new Label();
            banner.Text = "⚠ Claude Code が承認まちです — 確認してください";
            banner.BackColor = Theme.Accent;
            banner.ForeColor = Color.Black;
            banner.TextAlign = ContentAlignment.MiddleCenter;
            banner.Height = Font.Height + S(8);
            banner.Visible = false;
            termHost.Controls.Add(banner);
            termHost.Resize += delegate { banner.Width = termHost.ClientSize.Width; };

            // --- 下: ステータスバー
            status = new Panel();
            status.Dock = DockStyle.Bottom;
            status.Height = Font.Height + S(12);
            status.BackColor = Theme.Border; // 上端 1px を境界線に見せる
            status.Padding = new Padding(0, 1, 0, 0);

            statusInner = new Panel();
            statusInner.Dock = DockStyle.Fill;
            statusInner.BackColor = Theme.Chrome;
            statusInner.Padding = new Padding(S(8), S(3), S(8), S(3));

            cwdLabel = new Label();
            cwdLabel.Dock = DockStyle.Fill;
            cwdLabel.AutoEllipsis = true;
            cwdLabel.TextAlign = ContentAlignment.MiddleLeft;
            cwdLabel.ForeColor = Color.FromArgb(0xaa, 0xaa, 0xaa);

            var hint = new Label();
            hint.Text = "(画像ファイルはD&Dも可)";
            hint.Dock = DockStyle.Right;
            hint.AutoSize = true;
            hint.TextAlign = ContentAlignment.MiddleRight;
            hint.ForeColor = Color.FromArgb(0x77, 0x77, 0x77);

            var pasteImage = MakeButton("画像を貼り付け");
            pasteImage.Dock = DockStyle.Right;
            pasteImage.Click += delegate { PasteClipboardImage(); };

            // Dock は後から追加したものが外側に来る(Fill を最初に足す)。
            statusInner.Controls.Add(cwdLabel);
            statusInner.Controls.Add(hint);
            statusInner.Controls.Add(Spacer(S(8)));
            statusInner.Controls.Add(pasteImage);
            status.Controls.Add(statusInner);

            Controls.Add(termHost);
            Controls.Add(status);

            // ファイルのドロップはウィンドウのどこで受けてもよい。
            EnableDrop(this);
            EnableDrop(termHost);
            EnableDrop(banner);
            EnableDrop(statusInner);

            ResumeLayout();
        }

        private int S(int v)
        {
            return (int)Math.Round(v * scale);
        }

        /// <summary>フォーカスを奪わないボタン(押しても端末に打ち続けられるように)。</summary>
        private sealed class FlatButton : Button
        {
            public FlatButton()
            {
                SetStyle(ControlStyles.Selectable, false);
                TabStop = false;
            }
        }

        private Button MakeButton(string text)
        {
            var b = new FlatButton();
            b.Text = text;
            b.AutoSize = true;
            b.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            b.FlatStyle = FlatStyle.Flat;
            b.BackColor = Theme.ChromeHi;
            b.ForeColor = Theme.Fg;
            b.FlatAppearance.BorderColor = Theme.Border;
            b.FlatAppearance.MouseOverBackColor = Color.FromArgb(0x3a, 0x3a, 0x3a);
            b.Padding = new Padding(S(2), 0, S(2), 0);
            b.Cursor = Cursors.Hand;
            return b;
        }

        private static Control Spacer(int width)
        {
            var p = new Panel();
            p.Dock = DockStyle.Right;
            p.Width = width;
            return p;
        }

        // ------------------------------------------------------------ 配置と色分け

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);

            // 他の ccterm-cs と被らない色を選ぶ(同じフォルダならなるべく同じ色)。
            colorIndex = WindowLayout.ChooseColor(Handle, Launch.StartupDir());
            WindowLayout.SetColor(Handle, colorIndex);
            ApplyTint();

            // 4 分割の空いている区画へ収まる。全部埋まっていれば Windows 既定の位置のまま。
            slot = WindowLayout.ChooseSlot(Handle);
            if (slot >= 0)
            {
                WindowLayout.SetSlot(Handle, slot);
                WindowLayout.MoveToSlot(this, slot, Screen.FromPoint(Cursor.Position));
            }
        }

        /// <summary>端末の背景とステータスバーに、このウィンドウの色をうっすら混ぜる。</summary>
        private void ApplyTint()
        {
            Color tint = Theme.Tints[colorIndex];
            termBg = Theme.Mix(Theme.Bg, tint, 0.12);
            BackColor = termBg;
            termHost.BackColor = termBg;
            status.BackColor = tint; // ステータスバー上端の線をその色にする
            statusInner.BackColor = Theme.Mix(Theme.Chrome, tint, 0.3);
            if (session != null) session.View.SetBackground(termBg);
        }

        /// <summary>開いている ccterm-cs をすべて 4 分割に並べ直す。</summary>
        private void ArrangeAll()
        {
            WindowLayout.ArrangeAll(Handle);
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            WindowLayout.Clear(Handle);
            base.OnHandleDestroyed(e);
        }

        // ------------------------------------------------------------ 起動・終了

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);

            // 表示前は見えない枠の大きさが正しく取れないので、表示後に合わせ直す。
            if (slot >= 0) WindowLayout.MoveToSlot(this, slot, Screen.FromControl(this));

            // どちらのモードでも「起動したフォルダ」で開く。
            string cwd = Launch.StartupDir();
            var launch = plainShell ? Launch.Shell(cwd) : Launch.Claude(cwd);

            session = new TermSession(launch);
            session.WaitingChanged += delegate
            {
                UpdateWaitingUi();
                UpdateTitle();
            };
            session.View.TitleChanged += UpdateTitle;
            session.View.SetBackground(termBg);
            session.Alert += HandleAlert;
            // シェルが終わったらウィンドウも閉じる。
            session.Exited += delegate { Close(); };
            session.Memo.Changed += RenderMemos;
            session.View.PasteImageRequested += PasteClipboardImage;
            EnableDrop(session.View);

            termHost.Controls.Add(session.View);
            banner.BringToFront();
            UpdateTitle();
            cwdLabel.Text = "cwd: " + session.Cwd;
            session.View.Focus();

            try
            {
                session.Start();
            }
            catch (Exception ex)
            {
                string message = ex is EntryPointNotFoundException
                    ? "ConPTY が使えません。Windows 10 1809 以降が必要です。"
                    : "起動に失敗しました: " + launch.CommandLine() + "\r\n" + ex.Message;
                MessageBox.Show(this, message, "my-term", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Close();
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);
            if (session != null) session.Dispose(true);
        }

        protected override void OnActivated(EventArgs e)
        {
            base.OnActivated(e);
            FocusTerminal();
        }

        private void FocusTerminal()
        {
            if (session != null) session.View.Focus();
        }

        // ------------------------------------------------------------ キー

        /// <summary>アプリのショートカット。端末へは送らない。</summary>
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.F1)
            {
                ToggleMemo();
                return true;
            }
            if (keyData == (Keys.Control | Keys.Shift | Keys.A))
            {
                ArrangeAll();
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void WndProc(ref Message m)
        {
            // 他の ccterm-cs(または自分)からの並べ直しの依頼。
            if (m.Msg == WindowLayout.ArrangeMessage && WindowLayout.ArrangeMessage != 0)
            {
                slot = m.WParam.ToInt32();
                WindowLayout.SetSlot(Handle, slot);
                WindowLayout.MoveToSlot(this, slot, Screen.FromHandle(m.LParam));
                return;
            }

            // Alt を単独で押して離すとシステムメニューにフォーカスが移り、
            // 以後のキーが端末に届かなくなる。Alt+Space(システムメニュー)以外は握りつぶす。
            if (m.Msg == Native.WM_SYSCOMMAND &&
                (m.WParam.ToInt64() & 0xFFF0) == Native.SC_KEYMENU &&
                m.LParam.ToInt64() != ' ')
            {
                return;
            }
            base.WndProc(ref m);
        }

        // ------------------------------------------------------------ タイトル

        /// <summary>
        /// ウィンドウタイトル = [承認まちの印] フォルダ名 — claude の作業内容。
        /// タスクバーや Alt+Tab で、どのウィンドウが何をしているか見分けられるようにする。
        /// </summary>
        private void UpdateTitle()
        {
            if (session == null) return;
            var sb = new StringBuilder();
            if (session.Waiting) sb.Append("\u26a0 ");
            sb.Append(session.Title);
            string task = TaskTitle(session.View.Buffer.Title);
            if (task.Length > 0) sb.Append(" \u2014 ").Append(task);
            sb.Append(" - my-term");
            string text = sb.ToString();
            if (Text != text) Text = text;
        }

        /// <summary>
        /// 端末に送られてきたタイトルのうち、作業内容として出す価値があるもの。
        /// 起動直後に ConPTY が送ってくるシェルの実行ファイルのパスなどは出さない。
        /// </summary>
        private static string TaskTitle(string title)
        {
            if (string.IsNullOrEmpty(title)) return "";
            string t = title.Trim();
            string lower = t.ToLowerInvariant();
            bool isPath = t.Length >= 3 && t[1] == ':' && (t[2] == '\\' || t[2] == '/');
            if (isPath || lower.EndsWith(".exe") || lower == "windows powershell" || lower == "claude") return "";
            return t.Length > 80 ? t.Substring(0, 80) + "\u2026" : t;
        }

        // ------------------------------------------------------------ 承認まちの検知と通知

        /// <summary>
        /// 注意を促すべき事象(承認まちの立ち上がり・ベル)。
        /// 目の前に見えている(ウィンドウにフォーカスがある)場合は鳴らしても意味がないので何もしない。
        /// </summary>
        private void HandleAlert(TermSession s)
        {
            if (ActiveForm == this && WindowState != FormWindowState.Minimized) return;

            var info = new Native.FLASHWINFO();
            info.cbSize = Marshal.SizeOf(typeof(Native.FLASHWINFO));
            info.hwnd = Handle;
            // フォアグラウンドに戻るまでタスクバーの点滅を続ける。
            info.dwFlags = Native.FLASHW_ALL | Native.FLASHW_TIMERNOFG;
            Native.FlashWindowEx(ref info);
            Native.MessageBeep(0);
        }

        /// <summary>承認まちの間だけ、枠線と上部バナーを出す。</summary>
        private void UpdateWaitingUi()
        {
            bool waiting = session != null && session.Waiting;
            banner.Visible = waiting;
            if (waiting) banner.BringToFront();
            ApplyBorder();
        }

        /// <summary>端末の枠: ドラッグ中は青、承認まちはオレンジ。</summary>
        private void ApplyBorder()
        {
            if (session == null) return;
            if (dragging) session.View.SetBorder(Theme.Drop, S(3));
            else if (session.Waiting) session.View.SetBorder(Theme.Accent, S(4));
            else session.View.SetBorder(Color.Empty, 0);
        }

        // ------------------------------------------------------------ 入力メモ

        /// <summary>F1 でパネルの表示を切り替える。端末の幅が変わるので桁数も測り直される。</summary>
        private void ToggleMemo()
        {
            showMemo = !showMemo;
            if (showMemo && memoPanel == null) BuildMemoPanel();
            if (memoPanel != null) memoPanel.Visible = showMemo;
            RenderMemos();
            FocusTerminal();
        }

        private void BuildMemoPanel()
        {
            SuspendLayout();

            memoPanel = new Panel();
            memoPanel.Dock = DockStyle.Right;
            memoPanel.Width = S(280);
            memoPanel.BackColor = Theme.Border; // 左端 1px を境界線に見せる
            memoPanel.Padding = new Padding(1, 0, 0, 0);

            var inner = new Panel();
            inner.Dock = DockStyle.Fill;
            inner.BackColor = Theme.Chrome;

            memoList = new ListBox();
            memoList.Dock = DockStyle.Fill;
            memoList.BorderStyle = BorderStyle.None;
            memoList.BackColor = Theme.Chrome;
            memoList.ForeColor = Theme.Fg;
            memoList.Font = new Font("Consolas", 9f);
            memoList.DrawMode = DrawMode.OwnerDrawVariable;
            memoList.IntegralHeight = false;
            memoList.SelectionMode = SelectionMode.None;
            memoList.MeasureItem += MemoMeasureItem;
            memoList.DrawItem += MemoDrawItem;
            memoList.MouseClick += MemoClick;
            memoList.MouseMove += delegate(object sender, MouseEventArgs e)
            {
                int i = memoList.IndexFromPoint(e.Location);
                if (i == memoHover) return;
                memoHover = i;
                memoList.Invalidate();
            };
            memoList.MouseLeave += delegate
            {
                memoHover = -1;
                memoList.Invalidate();
            };
            EnableDrop(memoList);

            memoCount = new Label();
            memoCount.Dock = DockStyle.Top;
            memoCount.Height = Font.Height + S(6);
            memoCount.ForeColor = Color.FromArgb(0x88, 0x88, 0x88);
            memoCount.Padding = new Padding(S(6), 0, 0, 0);
            memoCount.TextAlign = ContentAlignment.MiddleLeft;

            var sep = new Panel();
            sep.Dock = DockStyle.Top;
            sep.Height = 1;
            sep.BackColor = Theme.Border;

            var head = new Panel();
            head.Dock = DockStyle.Top;
            head.Height = Font.Height + S(14);
            head.Padding = new Padding(S(6), S(5), S(6), S(3));

            var title = new Label();
            title.Text = "入力メモ";
            title.Dock = DockStyle.Fill;
            title.TextAlign = ContentAlignment.MiddleLeft;
            title.Font = new Font(Font, FontStyle.Bold);

            memoExport = MakeButton("エクスポート");
            memoExport.Dock = DockStyle.Right;
            memoExport.Click += delegate { ExportMemos(); };

            var clear = MakeButton("クリア");
            clear.Dock = DockStyle.Right;
            clear.Click += delegate
            {
                if (session != null) session.Memo.Clear();
                FocusTerminal();
            };

            head.Controls.Add(title);
            head.Controls.Add(memoExport);
            head.Controls.Add(Spacer(S(6)));
            head.Controls.Add(clear);

            inner.Controls.Add(memoList);
            inner.Controls.Add(sep);
            inner.Controls.Add(memoCount);
            inner.Controls.Add(head);
            memoPanel.Controls.Add(inner);

            // 端末(Fill)より先に場所を取らせるため、Fill の後ろ(z 順の奥)に置く。
            Controls.Add(memoPanel);
            Controls.SetChildIndex(memoPanel, Controls.GetChildIndex(termHost) + 1);

            memoCopiedTimer = new Timer();
            memoCopiedTimer.Interval = 400;
            memoCopiedTimer.Tick += delegate
            {
                memoCopiedTimer.Stop();
                memoCopied = -1;
                memoList.Invalidate();
            };

            ResumeLayout();
        }

        /// <summary>
        /// メモを描き直す。チャットのように古い順に上から並べ、新しい入力は下に積まれる。
        /// </summary>
        private void RenderMemos()
        {
            if (!showMemo || memoList == null) return;
            var memos = session != null ? session.Memo.Memos : new List<string>();

            // 描き直す前に「最下部を見ていたか」を覚えておく。
            // 上に遡って読んでいる最中に勝手に飛ばされると読めなくなるため。
            int count = memoList.Items.Count;
            bool atBottom = count == 0 ||
                memoList.GetItemRectangle(count - 1).Bottom <= memoList.ClientSize.Height + S(8);

            memoCount.Text = memos.Count + " 件  (F1 で表示切替)";
            memoExport.Enabled = memos.Count > 0;

            memoList.BeginUpdate();
            memoList.Items.Clear();
            if (memos.Count == 0) memoList.Items.Add(new EmptyMemo());
            else foreach (var m in memos) memoList.Items.Add(m);
            memoList.EndUpdate();

            if (atBottom && memoList.Items.Count > 0) memoList.TopIndex = memoList.Items.Count - 1;
        }

        private const TextFormatFlags MemoTextFlags =
            TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPrefix;

        private void MemoMeasureItem(object sender, MeasureItemEventArgs e)
        {
            object item = memoList.Items[e.Index];
            var size = TextRenderer.MeasureText(
                e.Graphics, item.ToString(), item is EmptyMemo ? Font : memoList.Font,
                new Size(memoList.ClientSize.Width - S(16), int.MaxValue), MemoTextFlags);
            // ListBox の項目の高さは 255 が上限。
            e.ItemHeight = Math.Min(255, size.Height + S(6));
        }

        private void MemoDrawItem(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0) return;
            object item = memoList.Items[e.Index];
            bool empty = item is EmptyMemo;

            Color bg = Theme.Chrome;
            if (!empty && e.Index == memoCopied) bg = Theme.Copied;
            else if (!empty && e.Index == memoHover) bg = Theme.ChromeHi;
            using (var b = new SolidBrush(bg)) e.Graphics.FillRectangle(b, e.Bounds);

            var rect = new Rectangle(e.Bounds.X + S(8), e.Bounds.Y + S(3), e.Bounds.Width - S(16), e.Bounds.Height - S(3));
            TextRenderer.DrawText(
                e.Graphics, item.ToString(), empty ? Font : memoList.Font, rect,
                empty ? Color.FromArgb(0x77, 0x77, 0x77) : Theme.Fg, MemoTextFlags);
        }

        /// <summary>メモの行をクリックしたらクリップボードへコピーする。</summary>
        private void MemoClick(object sender, MouseEventArgs e)
        {
            int index = memoList.IndexFromPoint(e.Location);
            if (index < 0 || memoList.Items[index] is EmptyMemo) return;
            try
            {
                Clipboard.SetText(memoList.Items[index].ToString());
            }
            catch (ExternalException)
            {
                return;
            }
            // コピーできたことが分かるよう一瞬だけ色を変える。
            memoCopied = index;
            memoList.Invalidate();
            memoCopiedTimer.Stop();
            memoCopiedTimer.Start();
            FocusTerminal();
        }

        /// <summary>メモをテキストファイルへ書き出す(保存先はダイアログ)。</summary>
        private void ExportMemos()
        {
            if (session == null || session.Memo.Memos.Count == 0) return;
            using (var dlg = new SaveFileDialog())
            {
                dlg.Title = "入力メモの保存先";
                dlg.FileName = "input-memo.txt";
                dlg.Filter = "テキスト (*.txt)|*.txt|すべてのファイル (*.*)|*.*";
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    File.WriteAllText(dlg.FileName, session.Memo.ToText(), new UTF8Encoding(false));
                }
            }
            FocusTerminal();
        }

        // ------------------------------------------------------------ 画像貼り付け / D&D

        /// <summary>
        /// クリップボードの画像を一時 PNG に保存し、そのパスを端末へ挿入する。
        /// claude にそのパスを読ませることで画像を渡せる。
        /// </summary>
        private void PasteClipboardImage()
        {
            if (session == null) return;
            string file = SaveClipboardImage();
            if (file == null)
            {
                // 画像が無いときは音で軽く知らせる。
                Native.MessageBeep(0);
                return;
            }
            session.InsertText(QuotePath(file) + " ");
            FocusTerminal();
        }

        private static string SaveClipboardImage()
        {
            try
            {
                if (!Clipboard.ContainsImage()) return null;
                using (var image = Clipboard.GetImage())
                {
                    if (image == null) return null;
                    string file = Path.Combine(
                        Path.GetTempPath(), "my-term-paste-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + ".png");
                    image.Save(file, ImageFormat.Png);
                    return file;
                }
            }
            catch (ExternalException)
            {
                return null;
            }
        }

        private void EnableDrop(Control c)
        {
            c.AllowDrop = true;
            c.DragEnter += OnDragEnter;
            c.DragOver += OnDragEnter;
            c.DragLeave += delegate { SetDragging(false); };
            c.DragDrop += OnDragDrop;
        }

        private void OnDragEnter(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            e.Effect = DragDropEffects.Copy;
            SetDragging(true);
        }

        /// <summary>ファイルの D&D でパスを挿入する。</summary>
        private void OnDragDrop(object sender, DragEventArgs e)
        {
            SetDragging(false);
            var files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (session == null || files == null) return;
            foreach (var path in files) session.InsertText(QuotePath(path) + " ");
            Activate();
            FocusTerminal();
        }

        private void SetDragging(bool on)
        {
            if (dragging == on) return;
            dragging = on;
            ApplyBorder();
        }

        /// <summary>
        /// 端末へ挿入するパスを必要なら " で括る。
        /// スペースを含むパスを裸で渡すと claude 側で 2 つの引数に割れてしまう。
        /// </summary>
        private static string QuotePath(string path)
        {
            foreach (char c in path)
            {
                if (char.IsWhiteSpace(c)) return "\"" + path + "\"";
            }
            return path;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && memoCopiedTimer != null) memoCopiedTimer.Dispose();
            base.Dispose(disposing);
        }
    }
}
