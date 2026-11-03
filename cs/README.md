# ccterm-cs

Claude Code 特化ターミナルの **C# / WinForms 版**。
機能は TypeScript / Electron 版(リポジトリ直下)と同じで、

- 起動したフォルダで `claude` を自動起動する
- **承認まち**を検知して、裏に回っていても気付ける(枠 + バナー + タスクバー点滅 + 音)
- 画像をクリップボード / D&D から渡せる(パスを入力欄に挿入する)
- 自分が打った入力を**メモとして自動で記録**し、あとで書き出せる
- 複数プロジェクトをタブで並行して回せる

**Windows に最初から入っている `csc.exe`(.NET Framework 4.x の C# 5 コンパイラ)だけで
ビルドできる**のが TS 版との違い。Node.js も .NET SDK も NuGet も要らない。

動作環境: Windows 10 1809 以降(ConPTY を使うため)。

## ビルド

```powershell
cd cs
.\build.cmd
```

`bin\ccterm-cs.exe` と `bin\assets\CascadiaMono-Regular.ttf` ができる。
`build.cmd` は `%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe` を呼んでいるだけなので、
手で打つなら次と同じ。

```powershell
& "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" `
  /nologo /target:winexe /optimize+ /codepage:65001 `
  /out:bin\ccterm-cs.exe /win32manifest:app.manifest `
  /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll src\*.cs
```

ソースは UTF-8 で書いてあるので `/codepage:65001` を必ず付ける
(付けないと日本語版 Windows では CP932 として読まれ、文字列が化ける)。

## 使い方

```powershell
cd C:\path\to\your-project
C:\path\to\cc-trem-ts\cs\bin\ccterm-cs.exe            # そのフォルダで claude が立ち上がる
C:\path\to\cc-trem-ts\cs\bin\ccterm-cs.exe --shell    # 素の PowerShell で立ち上がる
```

`ccterm-cs.exe` は GUI アプリなので、コンソールから起動してもすぐプロンプトに戻る。
`cs\bin` を PATH に通すか、`bin` フォルダの中身(exe と `assets`)を
PATH の通った場所へコピーしておくと、どこからでも `ccterm-cs` で開ける。
TS 版の `ccterm.cmd` のようなラッパーは要らない。

## キー操作

TS 版と同じものに、Windows の端末で一般的な操作をいくつか足してある。

| キー | 動作 |
| --- | --- |
| `Ctrl+Enter` / `Shift+Enter` | claude の入力欄で改行する(送信しない) |
| `F1` | 入力メモパネルの表示切替(既定は非表示) |
| `Ctrl+Tab` / `Ctrl+Shift+Tab` | タブを次 / 前へ切替 |
| `Ctrl+C` | 選択があればコピー、なければ `^C` を送る |
| `Ctrl+V` / `Shift+Insert` | 貼り付け(クリップボードが画像なら「画像を貼り付け」と同じ動作) |
| `Ctrl+Insert` | 選択をコピー |
| `Shift+PageUp` / `Shift+PageDown` | スクロールバックを遡る / 戻る |
| マウスドラッグ / ダブルクリック | 範囲選択 / 単語選択 |
| 右クリック | 選択があればコピー、なければ貼り付け |
| ホイール | スクロールバック(全画面アプリ表示中は矢印キーとして送る) |

画面の操作(タブの `＋` / `×` / `●`、ステータスバーの「画像を貼り付け」、
ファイルのドロップ、メモパネルのクリックでコピー・エクスポート・クリア)も TS 版と同じ。

## 構成

TS 版で各ライブラリに任せていた部分を、すべて自前で持っている。

| レイヤ | TS 版 | C# 版 |
| --- | --- | --- |
| PTY の起動・読み書き | `@lydell/node-pty` | `PseudoConsole.cs`(ConPTY を P/Invoke で直接呼ぶ) |
| ANSI 解釈 | xterm.js | `VtParser.cs` |
| 画面グリッド・スクロールバック | xterm.js | `TerminalBuffer.cs` |
| 文字幅(全角・絵文字) | `@xterm/addon-unicode11` | `CharWidth.cs` |
| 描画・キー入力・選択・IME | xterm.js(WebGL) | `TerminalView.cs`(GDI の `ExtTextOut`) |
| フォント | CSS `@font-face` | `TermFont.cs`(`AddFontResourceEx` + グリフ単位のフォールバック) |
| ウィンドウ・タブ・メモ・ステータス | Electron + DOM | `MainForm.cs` / `TabStrip.cs` |
| 承認まち検知・入力メモ | `waiting.ts` / `memo.ts` | `Waiting.cs` / `MemoRecorder.cs`(そのまま移植) |

```
cs/
├─ build.cmd           csc.exe でビルド(bin\ccterm-cs.exe)
├─ app.manifest        DPI 対応・Windows 10 宣言
└─ src/
   ├─ Program.cs       エントリポイント(--shell の判定)
   ├─ Launch.cs        何をどこで起動するか(LaunchConfig)
   ├─ Native.cs        Win32 API の宣言(ConPTY / プロセス / GDI / IME / 通知)
   ├─ PseudoConsole.cs ConPTY セッション(読み取りスレッド・終了監視)
   ├─ VtParser.cs      VT シーケンスの状態機械
   ├─ TerminalBuffer.cs セル・カーソル・スクロール領域・代替画面・スクロールバック
   ├─ CharWidth.cs     文字の表示幅
   ├─ Theme.cs         配色(256 色パレット)
   ├─ TermFont.cs      GDI フォントとフォールバック
   ├─ TerminalView.cs  端末コントロール(描画・キー・マウス・IME)
   ├─ TermSession.cs   1 タブ = 端末 + ConPTY + 入力メモ + 承認まち検知
   ├─ TabStrip.cs      タブバー(自前描画)
   ├─ MainForm.cs      ウィンドウ全体(タブ管理・メモパネル・ステータスバー・D&D)
   ├─ MemoRecorder.cs  入力メモ
   └─ Waiting.cs       承認まち検知
```

## 設計上の判断

| 項目 | 決めたこと | 理由 |
| --- | --- | --- |
| 言語仕様 | **C# 5 まで**(文字列補間・`?.`・式形式メンバなどは使わない) | Windows 同梱の `csc.exe` が C# 5 までしか扱えないため |
| 端末エミュレーション | **自前実装**(xterm 互換の部分集合) | NuGet を使わない方針。相手は ConPTY なので、ConPTY が出すシーケンスが読めれば足りる |
| 描画 | **GDI の `ExtTextOut` に `lpDx` を渡してセル位置に置く** | 和文をフォールバックフォントで描いても桁がずれない。GDI+ より速い |
| 同梱フォント | **`AddFontResourceEx(FR_PRIVATE)` で読み込む** | インストール不要でこのプロセスだけが使える。GDI+ からは見えないので描画は GDI で行う |
| 和文・記号 | **グリフの有無を調べてフォールバック**(BIZ UDGothic → MS Gothic → … → Segoe UI Emoji) | Cascadia Mono に無い文字を豆腐にしないため |
| `Ctrl+C` | **選択があればコピー、なければ `^C`** | Windows Terminal と同じ。claude の中断を潰さない |
| `Alt` 単独押し | **システムメニューへのフォーカス移動を握りつぶす** | 移ると以後のキーが端末に届かなくなるため |
| 承認まち・起動フォルダ・入力メモの扱い | **TS 版と同じ** | 挙動を揃えるため(TS 版の `docs/BUILD-PLAN.md` を参照) |

## 検証状況

- `VtParser` / `TerminalBuffer` / `MemoRecorder` / `Waiting` / `LaunchConfig` は
  Mono 上で単体テストを行い、さらに vim・less・top・bash の実出力を
  参照実装(Python の `pyte`)と同じ画面になるか突き合わせて確認した
  (出力を細切れにして流しても一致する)。
- ConPTY・GDI 描画・IME・クリップボード・ダイアログなど Windows API に依存する部分は、
  コンパイルは通るが**実機の Windows では未確認**。

## 今後の課題

- 実機の Windows での動作確認(特に IME の変換位置、絵文字の描画、高 DPI)
- マウス報告はホイールのみ(クリック・ドラッグは選択に使う)
- リサイズ時の行の再折り返し(ConPTY が描き直すので実害は小さい)
- 承認まち検知が文言依存なのは TS 版と同じ
