// 起動設定: どのプログラムを、どのフォルダで ConPTY 起動するかを決める。
// (TS 版 src/main/launch.ts に相当)

using System;
using System.Collections.Generic;
using System.Text;

namespace CcTerm
{
    /// <summary>ConPTY で起動するプログラムの指定。</summary>
    internal sealed class LaunchConfig
    {
        public readonly string Program;
        public readonly string[] Args;
        public readonly string Cwd;

        public LaunchConfig(string program, string[] args, string cwd)
        {
            Program = program;
            Args = args;
            Cwd = cwd;
        }

        /// <summary>CreateProcess に渡すコマンドライン(引数は MSVCRT の規則でクォートする)。</summary>
        public string CommandLine()
        {
            var parts = new List<string>();
            parts.Add(Quote(Program));
            foreach (var a in Args) parts.Add(Quote(a));
            return string.Join(" ", parts.ToArray());
        }

        private static string Quote(string arg)
        {
            if (arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return arg;

            var sb = new StringBuilder("\"");
            int backslashes = 0;
            foreach (char c in arg)
            {
                if (c == '\\')
                {
                    backslashes++;
                    continue;
                }
                if (c == '"')
                {
                    // 引用符の直前のバックスラッシュは倍にし、引用符自体もエスケープする。
                    sb.Append('\\', backslashes * 2 + 1);
                }
                else
                {
                    sb.Append('\\', backslashes);
                }
                backslashes = 0;
                sb.Append(c);
            }
            // 閉じ引用符の直前のバックスラッシュも倍にする。
            sb.Append('\\', backslashes * 2);
            sb.Append('"');
            return sb.ToString();
        }
    }

    internal static class Launch
    {
        /// <summary>ホームディレクトリ。</summary>
        public static string HomeDir()
        {
            var home = Environment.GetEnvironmentVariable("USERPROFILE");
            return string.IsNullOrEmpty(home) ? "." : home;
        }

        /// <summary>
        /// アプリを起動したフォルダ。ここで claude を立ち上げる。
        /// `ccterm-cs` とだけ打って、いまいる場所でそのまま始められるようにするため、
        /// 起動時にフォルダ選択ダイアログは出さない(新規タブでは選ばせる)。
        /// </summary>
        public static string StartupDir()
        {
            try
            {
                return Environment.CurrentDirectory;
            }
            catch
            {
                return HomeDir();
            }
        }

        /// <summary>
        /// 選んだ cwd で claude を起動する設定。
        /// `powershell -NoExit -Command claude` とすることで、claude 終了後もシェルに残れる。
        /// </summary>
        public static LaunchConfig Claude(string cwd)
        {
            return new LaunchConfig("powershell.exe", new[] { "-NoExit", "-Command", "claude" }, cwd);
        }

        /// <summary>素のシェルを起動する設定(`--shell` モード)。</summary>
        public static LaunchConfig Shell(string cwd)
        {
            return new LaunchConfig("powershell.exe", new[] { "-NoExit" }, cwd);
        }
    }
}
