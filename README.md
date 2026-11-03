# my-term

Claude Code を使うことに特化した、Windows 用の軽量ターミナル(C# / WinForms)。

素の端末機能に加えて、

- 起動したフォルダで `claude` を自動起動する
- **承認まち**を検知して、裏に回っていても気付ける(枠 + バナー + タスクバー点滅 + 音)
- 画像をクリップボード / D&D から渡せる(パスを入力欄に挿入する)
- 自分が打った入力を**メモとして自動で記録**し、あとで書き出せる
- ウィンドウを**うっすら半透過**にして、背後のデスクトップが透けて見える

**Windows に最初から入っている `csc.exe`(.NET Framework 4.x の C# 5 コンパイラ)だけでビルドできる。**
Node.js も .NET SDK も NuGet も要らない。軽さと省メモリを優先して、タブは持たない(1 ウィンドウ = 1 セッション)。

動作環境: Windows 10 1809 以降(ConPTY を使うため)。

> 同じ機能の TypeScript / Electron 版(タブあり)もこのリポジトリにある。→ [TypeScript / Electron 版](#typescript--electron-版)

## ビルド

```powershell
cd cs
.\build.cmd
```

`cs\bin\` に次の 3 つができる。

| ファイル | 中身 |
| --- | --- |
| `ccterm-cs.exe` | 本体 |
| `ccterm-cs.exe.config` | GC の設定(省メモリ用) |
| `assets\CascadiaMono-Regular.ttf` | 同梱フォント(インストール不要) |

## 使い方

```powershell
cd C:\path\to\your-project
ccterm-cs                   # そのフォルダで claude が立ち上がる
ccterm-cs --shell           # claude を挟まず素の PowerShell で立ち上がる
ccterm-cs --opacity=0.85    # 透け具合を変える(既定 0.93、1 で透過なし)
```

`cs\bin` を PATH に通すか、`bin` フォルダの中身(exe・config・`assets`)を PATH の通った場所へ
コピーしておくと、どこからでも `ccterm-cs` で開ける。GUI アプリなので、起動するとすぐプロンプトに戻る。

別のプロジェクトで使うときは、そのフォルダでもう 1 つ起動する。シェルを終了する(`exit`)とウィンドウも閉じる。

**ソースを変更したら `build.cmd` を実行し直す。**

## キー操作

| キー | 動作 |
| --- | --- |
| `Ctrl+Enter` / `Shift+Enter` | claude の入力欄で改行する(送信しない) |
| `F1` | 入力メモパネルの表示切替(既定は非表示) |
| `Ctrl+C` | 選択があればコピー、なければ `^C` を送る |
| `Ctrl+V` / `Shift+Insert` | 貼り付け(クリップボードが画像なら「画像を貼り付け」と同じ動作) |
| `Ctrl+Insert` | 選択をコピー |
| `Shift+PageUp` / `Shift+PageDown` | スクロールバックを遡る / 戻る |
| マウスドラッグ / ダブルクリック | 範囲選択 / 単語選択 |
| 右クリック | 選択があればコピー、なければ貼り付け |
| ホイール | スクロールバック(全画面アプリ表示中は矢印キーとして送る) |

その他は画面の操作で行う。

- ステータスバーの「画像を貼り付け」— クリップボードの画像を PNG 保存してパスを挿入
- ファイルをウィンドウにドロップ — そのパスを挿入(スペース入りは自動で `"` で括る)
- メモパネルの行をクリック — その行をクリップボードへコピー
- 「エクスポート」— メモをテキストファイルに保存 / 「クリア」— メモを消す

## 構成

外部ライブラリは使わず、端末エミュレーションも含めてすべて自前で持っている。

| レイヤ | ファイル |
| --- | --- |
| ConPTY の起動・読み書き(P/Invoke で直接呼ぶ) | `cs/src/PseudoConsole.cs` |
| VT シーケンスの解釈 | `cs/src/VtParser.cs` |
| 画面グリッド・スクロールバック・文字幅 | `cs/src/TerminalBuffer.cs` / `cs/src/CharWidth.cs` |
| 描画・キー入力・選択・IME(GDI の `ExtTextOut`) | `cs/src/TerminalView.cs` / `cs/src/TermFont.cs` |
| ウィンドウ・メモパネル・ステータスバー・D&D | `cs/src/MainForm.cs` |
| 承認まち検知・入力メモ | `cs/src/Waiting.cs` / `cs/src/MemoRecorder.cs` |

省メモリのため、セルは 12 バイトの構造体にし、スクロールバック(5000 行)は行末の空白を切り詰めて持つ。
120 桁で長いログを流したときのスクロールバックの使用量は約 4.9MB(Mono 上の計測)。

ファイルごとの役割、設計上の判断、軽量化の工夫の一覧は [`cs/README.md`](cs/README.md) にまとめてある。

## 検証状況

- 端末エミュレーション部分(パーサ・画面バッファ)と入力メモ・承認まち検知は、Mono 上の単体テストと、
  vim・less・top・bash の実出力を参照実装(Python の `pyte`)と突き合わせて確認済み。
- ConPTY・描画・IME・クリップボード・ダイアログ・半透過など Windows API に依存する部分は、
  **実機の Windows では未確認**。

## TypeScript / Electron 版

リポジトリ直下(`src/`)には、同じ用途の TypeScript / Electron 版もある。
xterm.js と node-pty を使っていて、**タブで複数プロジェクトを並行して回せる**のが C# 版との違い。

```powershell
npm install     # 初回のみ
npm start       # claude を起動して立ち上げる(カレントフォルダで動く)
npm run shell   # 素の PowerShell で立ち上げる
```

任意のフォルダから開くには、`npm run build` してから `scripts/ccterm.cmd` を PATH の通った場所へコピーし、
中の `set "APP=%~dp0.."` をこのリポジトリの絶対パスに書き換える。
タブ操作は `Ctrl+Tab` / `Ctrl+Shift+Tab`(切替)、タブバーの `＋` / `×`。

設計と実装の経緯は [`docs/BUILD-PLAN.md`](docs/BUILD-PLAN.md) を参照。

## ドキュメント

- [`cs/README.md`](cs/README.md) — C# 版の詳細(構成・設計上の判断・軽量化・今後の課題)
- [`docs/BUILD-PLAN.md`](docs/BUILD-PLAN.md) — TypeScript 版の実装計画と進捗、設計上の判断
