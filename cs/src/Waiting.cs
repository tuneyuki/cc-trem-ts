// 承認まち検知: claude が「実行してよいか」を尋ねて止まっている状態を見つける。
// (TS 版 src/renderer/waiting.ts の移植)
//
// claude 側に機械可読な合図が無いので、可視画面に出る文言で判定するしかない。
// ここに列挙した文言が画面に見えていれば「承認まち」とみなす。

namespace CcTerm
{
    internal static class Waiting
    {
        public static readonly string[] Patterns =
        {
            "Do you want",
            "Would you like to proceed",
            "❯ 1. Yes", // ❯ 1. Yes
        };

        /// <summary>可視画面のテキストから承認プロンプトを検知する。</summary>
        public static bool Detect(string screenText)
        {
            foreach (var p in Patterns)
            {
                if (screenText.Contains(p)) return true;
            }
            return false;
        }
    }
}
