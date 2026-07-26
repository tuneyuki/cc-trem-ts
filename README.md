# my-term-ts

Claude Code を使うことに特化したターミナル(TypeScript / Electron 版)。
Rust 版 [`../my-term`](../my-term) の移植で、機能はほぼ同等。

素の端末機能に加えて、

- 起動したフォルダで `claude` を自動起動する
- **承認まち**を検知して、裏に回っていても気付ける(枠 + バナー + タスクバー点滅 + 音)
- 画像をクリップボード / D&D から渡せる(パスを入力欄に挿入する)
- 自分が打った入力を**メモとして自動で記録**し、あとで書き出せる
- 複数プロジェクトをタブで並行して回せる

## 使い方

```powershell
npm install     # 初回のみ

npm start       # claude を起動して立ち上げる(カレントフォルダで動く)
npm run shell   # claude を挟まず素の PowerShell で立ち上げる
```

開発時は別の窓で `npm run watch` を回しておくと、保存のたびに再ビルドされる。
`npm run typecheck` で型チェックのみ実行。

## キー操作

| キー | 動作 |
| --- | --- |
| `F1` | 入力メモパネルの表示切替(既定は非表示) |
| `Ctrl+Tab` / `Ctrl+Shift+Tab` | タブを次 / 前へ切替 |
| `Ctrl+C` `Ctrl+V` など | 通常どおり端末へ送られる |

その他は画面の操作で行う。

- タブバーの `＋` — 新しいタブ(フォルダを選ぶ)、`×` — タブを閉じる
- タブの `●` — 裏で承認まちになったタブ。開くと消える
- ステータスバーの「画像を貼り付け」— クリップボードの画像を PNG 保存してパスを挿入
- ファイルをウィンドウにドロップ — そのパスを挿入(スペース入りは自動で `"` で括る)
- メモパネルの行をクリック — その行をクリップボードへコピー
- 「エクスポート」— メモをテキストファイルに保存 / 「クリア」— メモを消す

## 構成

| レイヤ | 使っているもの | 場所 |
| --- | --- | --- |
| PTY(ConPTY)の起動・読み書き | `@lydell/node-pty` | `src/main/pty.ts` |
| ANSI 解釈・画面グリッド・描画 | `@xterm/xterm` | `src/renderer/session.ts` |
| ウィンドウ・ダイアログ・クリップボード | Electron | `src/main/main.ts` |
| 周辺 UI(タブ / メモ / ステータス) | 素の DOM | `src/renderer/renderer.ts` |

renderer は `contextIsolation` 有効で Node API を直接触らず、
`src/preload/preload.ts` が公開する `window.api` 経由で main に依頼する。

ビルドは `build.mjs`(esbuild)で main / preload / renderer の 3 バンドルを作り、
`dist/` に HTML・CSS・フォントを並べる。

## ドキュメント

- [`docs/BUILD-PLAN.md`](docs/BUILD-PLAN.md) — 段階ごとの実装計画と進捗、Rust 版との差分
