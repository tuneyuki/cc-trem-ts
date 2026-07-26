// 承認まち検知: claude が「実行してよいか」を尋ねて止まっている状態を見つける。
// Rust 版 main.rs の `detect_waiting` 相当。
//
// claude 側に機械可読な合図が無いので、可視画面に出る文言で判定するしかない。
// ここに列挙した文言が画面に見えていれば「承認まち」とみなす。

export const WAITING_PATTERNS = [
  'Do you want',
  'Would you like to proceed',
  '❯ 1. Yes',
] as const;

/** 可視画面のテキストから承認プロンプトを検知する。 */
export function detectWaiting(screenText: string): boolean {
  return WAITING_PATTERNS.some((pattern) => screenText.includes(pattern));
}
