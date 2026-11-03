// ccterm-cs : Claude Code 特化ターミナル(C# / WinForms 版)
//
// 構成(TS 版との対応):
//   ConPTY       (PseudoConsole.cs)  … node-pty の代わり。シェルを起動して読み書き
//   VT 解釈・画面 (VtParser.cs / TerminalBuffer.cs) … xterm.js の代わり
//   描画・入力    (TerminalView.cs)  … xterm.js の描画とキー入力の代わり(GDI)
//   周辺 UI       (MainForm.cs)      … Electron + DOM の代わり(WinForms)
//
// 外部ライブラリは使わず、Windows 標準の csc.exe(C# 5)だけでビルドできる。

using System;
using System.Threading;
using System.Windows.Forms;

namespace CcTerm
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            // `--shell` 指定時は claude を挟まず素のシェルを起動する。
            bool plainShell = Array.IndexOf(args, "--shell") >= 0;

            Environment.SetEnvironmentVariable("TERM", "xterm-256color");

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.ThreadException += delegate(object sender, ThreadExceptionEventArgs e)
            {
                MessageBox.Show(e.Exception.ToString(), "my-term", MessageBoxButtons.OK, MessageBoxIcon.Error);
            };
            Application.Run(new MainForm(plainShell));
        }
    }
}
