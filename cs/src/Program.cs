// ccterm-cs : Claude Code 特化ターミナル(C# / WinForms 版)
//
// 構成(TS 版との対応):
//   ConPTY       (PseudoConsole.cs)  … node-pty の代わり。シェルを起動して読み書き
//   VT 解釈・画面 (VtParser.cs / TerminalBuffer.cs) … xterm.js の代わり
//   描画・入力    (TerminalView.cs)  … xterm.js の描画とキー入力の代わり(GDI)
//   周辺 UI       (MainForm.cs)      … Electron + DOM の代わり(WinForms)
//
// 軽量化のためタブは持たず、1 ウィンドウ = 1 セッションにしている。
//
// 外部ライブラリは使わず、Windows 標準の csc.exe(C# 5)だけでビルドできる。

using System;
using System.Globalization;
using System.Threading;
using System.Windows.Forms;

namespace CcTerm
{
    internal static class Program
    {
        /// <summary>ウィンドウの不透明度の既定値。ごくうっすらデスクトップが透ける程度。</summary>
        private const double DefaultOpacity = 0.93;

        [STAThread]
        private static void Main(string[] args)
        {
            // `--shell` 指定時は claude を挟まず素のシェルを起動する。
            bool plainShell = Array.IndexOf(args, "--shell") >= 0;
            double opacity = DefaultOpacity;
            foreach (var a in args)
            {
                // `--opacity=0.85` のように指定する(1 で透過なし)。
                double v;
                if (a.StartsWith("--opacity=", StringComparison.Ordinal) &&
                    double.TryParse(a.Substring(10), NumberStyles.Float, CultureInfo.InvariantCulture, out v))
                {
                    opacity = Math.Max(0.3, Math.Min(1.0, v));
                }
            }

            Environment.SetEnvironmentVariable("TERM", "xterm-256color");

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.ThreadException += delegate(object sender, ThreadExceptionEventArgs e)
            {
                MessageBox.Show(e.Exception.ToString(), "my-term", MessageBoxButtons.OK, MessageBoxIcon.Error);
            };
            Application.Run(new MainForm(plainShell, opacity));
        }
    }
}
