// メインウィンドウ。タブ(TermSession)の管理と、周辺 UI の配線を行う。
// (TS 版 src/renderer/renderer.ts と src/main/main.ts の OS 機能部分に相当)
//
//   上: タブバー / 中央: 端末(左)+ 入力メモ(右、F1 で開閉)/ 下: ステータスバー

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
        private readonly List<TermSession> sessions = new List<TermSession>();
        private int activeIndex;

        /// <summary>
        /// メモパネルを出すか(全タブ共通、F1 で切替)。
        /// 既定は非表示。端末を広く使いたいので、必要なときだけ開く。
        /// </summary>
        private bool showMemo;
        /// <summary>新規タブの多重生成を防ぐ(フォルダ選択の間に連打されうる)。</summary>
        private bool opening;
        /// <summary>ファイルをドラッグ中か(ドロップ先を枠で示す)。</summary>
        private bool dragging;

        private readonly float scale;
        private readonly TabStrip tabStrip;
        private readonly Panel termHost;
        private readonly Label banner;
        private readonly Panel memoPanel;
        private readonly Label memoCount;
        private readonly Button memoExport;
        private readonly ListBox memoList;
        private readonly Label cwdLabel;
        private readonly ToolTip toolTip = new ToolTip();

        private int memoHover = -1;
        private int memoCopied = -1;
        private readonly Timer memoCopiedTimer = new Timer();

        /// <summary>メモが空のときに出す案内(クリックしてもコピーしない)。</summary>
        private sealed class EmptyMemo
        {
            public override string ToString()
            {
                return "(入力して Enter で記録されます)";
            }
        }

        public MainForm(bool plainShell)
        {
            this.plainShell = plainShell;

            using (var g = CreateGraphics()) scale = g.DpiX / 96f;

            Text = "my-term";
            BackColor = Theme.Bg;
            ForeColor = Theme.Fg;
            Font = SystemFonts.MessageBoxFont;
            ClientSize = new Size(S(960), S(600));
            StartPosition = FormStartPosition.WindowsDefaultLocation;
            AllowDrop = true;
            Icon = LoadIcon();

            SuspendLayout();

            // --- 中央: 端末領域(各タブの TerminalView が重なって入る)
            termHost = new Panel();
            termHost.Dock = DockStyle.Fill;
            termHost.BackColor = Theme.Bg;

            // 承認まちの帯。端末の上に重ねて出す(端末の大きさは変えない)。
            banner = new Label();
            banner.Text = "⚠ Claude Code が承認まちです — 確認してください";
            banner.BackColor = Theme.Accent;
            banner.ForeColor = Color.Black;
            banner.TextAlign = ContentAlignment.MiddleCenter;
            banner.AutoSize = false;
            banner.Height = Font.Height + S(8);
            banner.Visible = false;
            termHost.Controls.Add(banner);
            termHost.Resize += delegate { banner.Width = termHost.ClientSize.Width; };

            // --- 右: 入力メモ
            memoPanel = new Panel();
            memoPanel.Dock = DockStyle.Right;
            memoPanel.Width = S(280);
            memoPanel.BackColor = Theme.Border; // 左端 1px を境界線に見せる
            memoPanel.Padding = new Padding(1, 0, 0, 0);
            memoPanel.Visible = false;

            var memoInner = new Panel();
            memoInner.Dock = DockStyle.Fill;
            memoInner.BackColor = Theme.Chrome;

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
            toolTip.SetToolTip(memoList, "クリックでコピー");

            memoCount = new Label();
            memoCount.Dock = DockStyle.Top;
            memoCount.Height = Font.Height + S(6);
            memoCount.ForeColor = Color.FromArgb(0x88, 0x88, 0x88);
            memoCount.Padding = new Padding(S(6), 0, 0, 0);
            memoCount.TextAlign = ContentAlignment.MiddleLeft;

            var memoSep = new Panel();
            memoSep.Dock = DockStyle.Top;
            memoSep.Height = 1;
            memoSep.BackColor = Theme.Border;

            var memoHead = new Panel();
            memoHead.Dock = DockStyle.Top;
            memoHead.Height = Font.Height + S(14);
            memoHead.Padding = new Padding(S(6), S(5), S(6), S(3));

            var memoTitle = new Label();
            memoTitle.Text = "入力メモ";
            memoTitle.Dock = DockStyle.Fill;
            memoTitle.TextAlign = ContentAlignment.MiddleLeft;
            memoTitle.Font = new Font(Font, FontStyle.Bold);

            memoExport = MakeButton("エクスポート");
            memoExport.Dock = DockStyle.Right;
            memoExport.Click += delegate { ExportMemos(); };

            var memoClear = MakeButton("クリア");
            memoClear.Dock = DockStyle.Right;
            memoClear.Click += delegate
            {
                if (Active != null) Active.Memo.Clear();
                FocusTerminal();
            };

            // Dock は後から追加したものが外側に来る。
            memoHead.Controls.Add(memoTitle);
            memoHead.Controls.Add(memoExport);
            memoHead.Controls.Add(Spacer(S(6)));
            memoHead.Controls.Add(memoClear);

            memoInner.Controls.Add(memoList);
            memoInner.Controls.Add(memoSep);
            memoInner.Controls.Add(memoCount);
            memoInner.Controls.Add(memoHead);
            memoPanel.Controls.Add(memoInner);

            // --- 下: ステータスバー
            var status = new Panel();
            status.Dock = DockStyle.Bottom;
            status.Height = Font.Height + S(12);
            status.BackColor = Theme.Border; // 上端 1px を境界線に見せる
            status.Padding = new Padding(0, 1, 0, 0);

            var statusInner = new Panel();
            statusInner.Dock = DockStyle.Fill;
            statusInner.BackColor = Theme.Chrome;
            statusInner.Padding = new Padding(S(8), S(3), S(8), S(3));

            cwdLabel = new Label();
            cwdLabel.Dock = DockStyle.Fill;
            cwdLabel.AutoEllipsis = true;
            cwdLabel.TextAlign = ContentAlignment.MiddleLeft;
            cwdLabel.ForeColor = Color.FromArgb(0xaa, 0xaa, 0xaa);
            cwdLabel.Font = new Font("Consolas", 8.25f);

            var hint = new Label();
            hint.Text = "(画像ファイルはD&Dも可)";
            hint.Dock = DockStyle.Right;
            hint.AutoSize = true;
            hint.TextAlign = ContentAlignment.MiddleRight;
            hint.ForeColor = Color.FromArgb(0x77, 0x77, 0x77);

            var pasteImage = MakeButton("画像を貼り付け");
            pasteImage.Dock = DockStyle.Right;
            pasteImage.Click += delegate { PasteClipboardImage(); };

            statusInner.Controls.Add(cwdLabel);
            statusInner.Controls.Add(hint);
            statusInner.Controls.Add(Spacer(S(8)));
            statusInner.Controls.Add(pasteImage);
            status.Controls.Add(statusInner);

            // --- 上: タブバー
            tabStrip = new TabStrip(scale);
            tabStrip.Dock = DockStyle.Top;
            tabStrip.Font = Font;
            tabStrip.UpdateItems();
            tabStrip.TabClicked += delegate(int i) { SetActive(i); };
            tabStrip.CloseClicked += delegate(int i) { CloseAt(i, true); };
            tabStrip.NewTabClicked += OpenNewTab;

            // Dock は後から追加したものが先に場所を取る(Fill を最初に足す)。
            Controls.Add(termHost);
            Controls.Add(memoPanel);
            Controls.Add(status);
            Controls.Add(tabStrip);

            // ファイルのドロップはウィンドウのどこで受けてもよい。
            EnableDrop(this);
            EnableDrop(termHost);
            EnableDrop(banner);
            EnableDrop(tabStrip);
            EnableDrop(statusInner);
            EnableDrop(memoList);

            memoCopiedTimer.Interval = 400;
            memoCopiedTimer.Tick += delegate
            {
                memoCopiedTimer.Stop();
                memoCopied = -1;
                memoList.Invalidate();
            };

            ResumeLayout();
        }

        private int S(int v)
        {
            return (int)Math.Round(v * scale);
        }

        private static Icon LoadIcon()
        {
            try
            {
                return Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            }
            catch
            {
                return null;
            }
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

        // ------------------------------------------------------------ 起動・終了

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            // どちらのモードでも「起動したフォルダ」で開く。
            string cwd = Launch.StartupDir();
            OpenTab(plainShell ? Launch.Shell(cwd) : Launch.Claude(cwd));
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);
            foreach (var s in sessions.ToArray()) s.Dispose(true);
            sessions.Clear();
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
            if (keyData == (Keys.Control | Keys.Tab))
            {
                CycleTab(1);
                return true;
            }
            if (keyData == (Keys.Control | Keys.Shift | Keys.Tab))
            {
                CycleTab(-1);
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void WndProc(ref Message m)
        {
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

        // ------------------------------------------------------------ タブ

        private TermSession Active
        {
            get { return activeIndex >= 0 && activeIndex < sessions.Count ? sessions[activeIndex] : null; }
        }

        /// <summary>＋ から新しいタブを開く(claude モードではフォルダを選ばせる)。</summary>
        private void OpenNewTab()
        {
            if (opening) return;
            opening = true;
            try
            {
                LaunchConfig launch;
                if (plainShell)
                {
                    // 素シェルモードは「すぐシェルが欲しい」用途なので、選ばせずに起動フォルダで開く。
                    launch = Launch.Shell(Launch.StartupDir());
                }
                else
                {
                    using (var dlg = new FolderBrowserDialog())
                    {
                        dlg.Description = "Claude Code を起動するフォルダを選択";
                        dlg.SelectedPath = Launch.StartupDir();
                        dlg.ShowNewFolderButton = false;
                        if (dlg.ShowDialog(this) != DialogResult.OK || dlg.SelectedPath.Length == 0)
                        {
                            FocusTerminal();
                            return;
                        }
                        launch = Launch.Claude(dlg.SelectedPath);
                    }
                }
                OpenTab(launch);
            }
            finally
            {
                opening = false;
            }
        }

        /// <summary>指定の起動設定で新しいタブを開き、アクティブにする。</summary>
        private void OpenTab(LaunchConfig launch)
        {
            var session = new TermSession(launch);
            session.WaitingChanged += HandleWaitingChange;
            session.Alert += HandleAlert;
            session.Exited += HandleExit;
            session.Memo.Changed += delegate
            {
                if (session == Active) RenderMemos();
            };
            session.View.PasteImageRequested += PasteClipboardImage;
            EnableDrop(session.View);

            termHost.Controls.Add(session.View);
            banner.BringToFront();
            sessions.Add(session);
            SetActive(sessions.Count - 1);

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
                int index = sessions.IndexOf(session);
                if (index >= 0) CloseAt(index, false);
            }
        }

        private void SetActive(int index)
        {
            if (index < 0 || index >= sessions.Count) return;
            activeIndex = index;
            for (int i = 0; i < sessions.Count; i++) sessions[i].View.Visible = i == index;

            // 開いたタブの「要確認」は見たものとして解除する。
            var session = sessions[index];
            session.Attention = false;
            cwdLabel.Text = "cwd: " + session.Cwd;
            toolTip.SetToolTip(cwdLabel, session.Cwd);

            UpdateWaitingUi();
            RenderTabs();
            RenderMemos();
            FocusTerminal();
        }

        private void FocusTerminal()
        {
            var s = Active;
            if (s != null && s.View.Visible) s.View.Focus();
        }

        /// <summary>タブを前後に切り替える(端で折り返す)。</summary>
        private void CycleTab(int delta)
        {
            int n = sessions.Count;
            if (n < 2) return;
            SetActive((activeIndex + delta + n) % n);
        }

        private void RenderTabs()
        {
            tabStrip.Items.Clear();
            for (int i = 0; i < sessions.Count; i++)
            {
                var item = new TabStrip.Item();
                item.Title = sessions[i].Title;
                item.Tooltip = sessions[i].Cwd;
                item.Active = i == activeIndex;
                item.Attention = sessions[i].Attention;
                tabStrip.Items.Add(item);
            }
            tabStrip.UpdateItems();
        }

        /// <summary>シェルが終了したタブを片付ける。全部無くなったらウィンドウを閉じる。</summary>
        private void HandleExit(TermSession session)
        {
            int index = sessions.IndexOf(session);
            if (index >= 0) CloseAt(index, false);
        }

        private void CloseAt(int index, bool killPty)
        {
            if (index < 0 || index >= sessions.Count) return;
            var session = sessions[index];
            sessions.RemoveAt(index);
            session.Dispose(killPty);

            if (sessions.Count == 0)
            {
                Close();
                return;
            }
            // 閉じたタブより後ろがアクティブだった場合は番号がずれる。
            int next = Math.Min(activeIndex > index ? activeIndex - 1 : activeIndex, sessions.Count - 1);
            SetActive(next);
        }

        // ------------------------------------------------------------ 承認まちの検知と通知

        /// <summary>
        /// 注意を促すべき事象(承認まちの立ち上がり・ベル)。
        /// 目の前に見えている(アクティブ かつ ウィンドウにフォーカスがある)場合は
        /// 鳴らしても意味がないので何もしない。
        /// </summary>
        private void HandleAlert(TermSession session)
        {
            bool isActive = session == Active;
            bool focused = ActiveForm == this && WindowState != FormWindowState.Minimized;
            if (isActive && focused) return;

            if (!focused) FlashTaskbar();
            Native.MessageBeep(0);
            if (!isActive)
            {
                session.Attention = true;
                RenderTabs();
            }
        }

        private void FlashTaskbar()
        {
            var info = new Native.FLASHWINFO();
            info.cbSize = Marshal.SizeOf(typeof(Native.FLASHWINFO));
            info.hwnd = Handle;
            // フォアグラウンドに戻るまで点滅を続ける。
            info.dwFlags = Native.FLASHW_ALL | Native.FLASHW_TIMERNOFG;
            Native.FlashWindowEx(ref info);
        }

        private void HandleWaitingChange(TermSession session)
        {
            if (session == Active) UpdateWaitingUi();
        }

        /// <summary>アクティブタブが承認まちの間だけ、枠線と上部バナーを出す。</summary>
        private void UpdateWaitingUi()
        {
            var s = Active;
            bool waiting = s != null && s.Waiting;
            banner.Visible = waiting;
            if (waiting) banner.BringToFront();
            ApplyBorder();
        }

        /// <summary>端末の枠: ドラッグ中は青、承認まちはオレンジ。</summary>
        private void ApplyBorder()
        {
            foreach (var s in sessions)
            {
                if (dragging) s.View.SetBorder(Theme.Drop, S(3));
                else if (s.Waiting) s.View.SetBorder(Theme.Accent, S(4));
                else s.View.SetBorder(Color.Empty, 0);
            }
        }

        // ------------------------------------------------------------ 入力メモ

        /// <summary>F1 でパネルの表示を切り替える。端末の幅が変わるので桁数も測り直される。</summary>
        private void ToggleMemo()
        {
            showMemo = !showMemo;
            memoPanel.Visible = showMemo;
            RenderMemos();
            FocusTerminal();
        }

        /// <summary>
        /// アクティブタブのメモを描き直す。
        /// チャットのように古い順に上から並べ、新しい入力は下に積まれる。
        /// </summary>
        private void RenderMemos()
        {
            if (!showMemo) return;
            var s = Active;
            var memos = s != null ? s.Memo.Memos : new List<string>();

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
            string text = memoList.Items[e.Index].ToString();
            Font f = memoList.Items[e.Index] is EmptyMemo ? Font : memoList.Font;
            var size = TextRenderer.MeasureText(
                e.Graphics, text, f, new Size(memoList.ClientSize.Width - S(16), int.MaxValue), MemoTextFlags);
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
            var s = Active;
            if (s == null || s.Memo.Memos.Count == 0) return;
            using (var dlg = new SaveFileDialog())
            {
                dlg.Title = "入力メモの保存先";
                dlg.FileName = "input-memo.txt";
                dlg.Filter = "テキスト (*.txt)|*.txt|すべてのファイル (*.*)|*.*";
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    File.WriteAllText(dlg.FileName, s.Memo.ToText(), new UTF8Encoding(false));
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
            var s = Active;
            if (s == null) return;
            string file = SaveClipboardImage();
            if (file == null)
            {
                // 画像が無いときは音で軽く知らせる。
                Native.MessageBeep(0);
                return;
            }
            s.InsertText(QuotePath(file) + " ");
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
            var s = Active;
            var files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (s == null || files == null) return;
            foreach (var path in files) s.InsertText(QuotePath(path) + " ");
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
            if (disposing)
            {
                toolTip.Dispose();
                memoCopiedTimer.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
