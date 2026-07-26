# my-term-ts ビルドプラン

Rust 版 `../my-term`(eframe + alacritty_terminal + portable-pty)の
**TypeScript / Electron 移植版**を段階的に作るための計画書。

進捗はこのファイルのチェックボックスで管理する(`[ ]` 未着手 / `[x]` 完了)。
各マイルストーンは「そこで一度動かして確認できる」単位で区切ってある。

- 最終更新: 2026-07-26
- 現在のフェーズ: **M6 完了(M0〜M6 すべて完了)**

---

## 1. ゴール

Claude Code を快適に使うことだけに特化したターミナル。素の端末機能に加えて、

- 起動したフォルダで `claude` を自動起動する
- **承認まち**(`Do you want ...`)を検知して、裏に回っていても気付ける
- 画像をクリップボード/D&D から貼れる(パスを挿入して claude に読ませる)
- 自分が打った入力を**メモとして自動で残す**
- 複数プロジェクトをタブで並行して回せる

を Rust 版と同等に実現する。

---

## 2. アーキテクチャ(Rust 版との対応)

| レイヤ | Rust 版 | TS 版 | 担当プロセス |
| --- | --- | --- | --- |
| ③ PTY 起動・読み書き | `portable-pty` | `@lydell/node-pty` | main |
| ② ANSI 解釈・画面グリッド | `alacritty_terminal` | `@xterm/xterm` | renderer |
| ① 描画・入力 | `eframe` / `egui` | Electron (Chromium) + DOM | renderer |
| 色解決 | `palette.rs` | `theme.ts`(xterm の `ITheme`) | renderer |
| OS 機能(ダイアログ/クリップボード/ビープ) | `rfd` / `arboard` / `MessageBeep` | Electron `dialog` / `clipboard` / `shell.beep` | main |

Rust 版は「PTY のバイト列を自前で解釈してセル単位に描画」していたが、
TS 版は **xterm.js が ② と ① の描画を一手に引き受ける**のが最大の構造差。
そのぶん renderer 側は「xterm.js を並べて周辺 UI を付ける」設計になる。

プロセス分離(`contextIsolation: true`)のため、renderer は Node API を直接触らず
`preload` が `contextBridge` で公開する `window.api` 経由で main に依頼する。

```
renderer (xterm.js, UI)  <--IPC-->  main (node-pty, dialog, clipboard)
        ^ preload が window.api を橋渡し
```

---

## 3. ファイル構成

```
my-term-ts/
├─ build.mjs                  esbuild ビルド(main/preload/renderer の 3 バンドル)
├─ package.json
├─ tsconfig.json
├─ docs/BUILD-PLAN.md         ← このファイル
├─ assets/CascadiaMono-Regular.ttf
└─ src/
   ├─ shared/types.ts         main/preload/renderer 共有の型
   ├─ main/
   │   ├─ main.ts             ウィンドウ生成・IPC ハンドラ
   │   ├─ launch.ts           何をどこで起動するか(LaunchConfig)
   │   └─ pty.ts              PTY セッション管理(PtyManager)
   ├─ preload/preload.ts      contextBridge で window.api を公開
   └─ renderer/
       ├─ index.html          画面骨格(タブバー/端末/メモ/ステータス)
       ├─ style.css           レイアウトと配色
       ├─ theme.ts            xterm のパレット(= palette.rs)
       ├─ memo.ts             入力メモの記録ロジック
       ├─ waiting.ts          承認まち検知(= detect_waiting)
       ├─ session.ts          1 タブ = xterm + PTY + メモ + 検知
       └─ renderer.ts         アプリ本体(タブ管理・UI 配線)
```

---

## 4. マイルストーン

### M0. 土台 — ビルドが通る状態 `[x]`

- [x] `package.json` / `tsconfig.json` / `build.mjs`(esbuild 3 バンドル構成)
- [x] `src/shared/types.ts`(`LaunchConfig` / `Bootstrap` / `RendererApi`)
- [x] `src/main/launch.ts`(claude 起動 / 素シェル起動、Windows・POSIX 両対応)
- [x] `src/main/pty.ts`(`PtyManager`: spawn/write/resize/kill/killAll)
- [x] `src/main/main.ts`(ウィンドウ + IPC 一式)
- [x] `src/preload/preload.ts`(`window.api`)
- [x] `src/renderer/theme.ts` / `src/renderer/memo.ts`
- [x] `npm install`(`@lydell/node-pty` は prebuilt N-API なので electron-rebuild 不要)

**受け入れ条件**: `npm run typecheck` が通る(renderer 未実装のため M1 と同時に確認)。

---

### M1. 素の端末として使える `[x]`

Rust 版 M1 相当。まずは 1 タブ・周辺 UI なしで「端末として成立する」ことを取る。

- [x] `index.html` — 画面骨格(タブバー / 端末領域 / メモパネル / ステータスバー)
- [x] `style.css` — ダーク配色・フォント埋め込み(`@font-face` で Cascadia Mono)
- [x] `session.ts` — `TermSession` クラス
  - [x] xterm.js 生成(`theme` / `fontFamily` / `fontSize` / `scrollback`)
  - [x] `FitAddon` でコンテナサイズ → 桁数・行数を算出
  - [x] `Unicode11Addon`(全角幅の判定を claude の記号に合わせる)
  - [x] `WebglAddon`(失敗時は自動でフォールバック)
  - [x] `onData` → `api.pty.write`、`pty:data` → `term.write`
  - [x] `ResizeObserver` → `fit()` → `api.pty.resize`
- [x] `renderer.ts` — 起動時に `bootstrap()` して 1 タブ生成、PTY 出力の振り分け
- [x] 日本語入力(IME)が化けずに打てる ※xterm.js の textarea が標準対応
- [x] コピー/ペースト(Ctrl+C / Ctrl+V、bracketed paste は xterm.js 任せ)

**受け入れ条件**: `npm start` で claude が立ち上がり、日本語を含む対話ができる。
ウィンドウをリサイズすると端末も追従する。

---

### M2. 承認まち検知と通知 `[x]`

Rust 版の `detect_waiting` + `RequestUserAttention` + `MessageBeep` 相当。

- [x] `waiting.ts` — 可視画面テキストから承認プロンプトを検知
  - [x] `Do you want` / `Would you like to proceed` / `❯ 1. Yes`
- [x] `session.ts` — データ受信後にデバウンスして検知(毎バイト走査しない)
- [x] ベル(`onBell`)も通知トリガに含める
- [x] 立ち上がり(false → true)のときだけ通知する
- [x] 非フォーカス時のみ通知(見えているのに鳴らさない)
  - [x] `api.flash()` — タスクバー点滅 ※OS 挙動のため実機で手動確認
  - [x] `api.beep()` — 注意音 ※同上
- [x] アクティブタブが承認まちの間は**枠線 + 上部バナー**で強調

**受け入れ条件**: claude に承認が必要な操作をさせ、別ウィンドウにフォーカスした状態で
タスクバーが点滅し音が鳴る。戻ると枠とバナーが出ている。

---

### M3. ステータスバーと画像貼り付け `[x]`

- [x] ステータスバー — cwd 表示
- [x] 「画像を貼り付け」ボタン → `clipboard:image` で一時 PNG 保存 → パスを端末へ挿入
- [x] 画像が無いときはビープで知らせる
- [x] ファイルの D&D → パスを端末へ挿入(`webUtils.getPathForFile`) ※実ファイルのドロップは手動確認
  - [x] スペースを含むパスは `"` で括る(Rust 版からの改善)

**受け入れ条件**: スクショを撮って(Win+Shift+S)ボタンを押すと、
claude の入力欄にパスが入り、`Read` させると画像が読める。

---

### M4. タブ `[x]`

- [x] タブバー — 見出しは cwd のフォルダ名
- [x] `＋` 新規タブ(claude モードならフォルダ選択ダイアログ、素シェルならホーム)
- [x] `×` タブを閉じる(2 つ以上あるときだけ表示、PTY も kill)
- [x] タブ切替(アクティブのみ表示、切替時に `fit()` し直す)
- [x] 背景タブの承認まちは `●` マークで示し、開いたら解除
- [x] PTY 終了でタブを自動的に片付ける。全部消えたらウィンドウを閉じる
- [x] `Ctrl+Tab` / `Ctrl+Shift+Tab` でタブ切替(Rust 版にはない追加)

**受け入れ条件**: 2 つのフォルダでタブを開き、裏のタブが承認まちになると `●` が付く。

---

### M5. 入力メモ `[x]`

- [x] `memo.ts` を `session.ts` に接続(`onData` を覗いて行を組み立てる)
- [x] 右サイドパネル — **チャット風に古い順(新しい入力が下に積まれる)**、件数表示
  - [x] 新しい行が入ったら自動で最下部へスクロールする
  - [x] ただしユーザが上に遡って読んでいる間は自動スクロールしない
- [x] クリックでクリップボードにコピー
- [x] 「エクスポート」— ダイアログで保存先を選んで `.txt` 出力(CRLF) ※保存ダイアログは手動確認
- [x] 「クリア」
- [x] `F1` で表示トグル(PTY には送らない)。**既定は非表示**

**受け入れ条件**: 何行か入力したあとパネルに履歴が並び、エクスポートしたファイルを
メモ帳で開いて読める。

---

### M6. 仕上げ `[x]`

- [x] `npm run typecheck` が無警告
- [x] README(使い方・キー操作一覧)
- [x] `.gitignore`(`node_modules` / `dist`)
- [x] 配布ビルドの検討(下記)

**配布ビルドについて(現時点では見送り)**

自分の PC で `npm start` して使う分には不要なので、いまは入れない。
必要になったら `electron-builder` を足す。その際の注意点だけ残しておく。

- `@lydell/node-pty` はネイティブモジュールなので `asar` に固めると読めない。
  `build.asarUnpack` に `**/node_modules/@lydell/node-pty*/**` を指定する。
- 配布物には prebuilt バイナリ(`@lydell/node-pty-win32-x64`)が要る。
  他 OS 向けに配るならその OS 用のパッケージも含める。
- `dist/`(esbuild の出力)と `assets/` を `files` に含める。
- インストーラを作らず `--dir` でフォルダごと配るのが一番手軽。

---

## 5. 動作確認手順

```powershell
npm install          # 初回のみ
npm run typecheck    # 型チェック
npm start            # claude を起動して立ち上げ
npm run shell        # 素の PowerShell で立ち上げ(端末機能だけ試す時)
npm run watch        # 別窓で監視ビルド(開発中)
```

---

## 6. Rust 版との意図的な差分

| 項目 | Rust 版 | TS 版 | 理由 |
| --- | --- | --- | --- |
| 起動フォルダ | 起動時にダイアログで選択 | **起動したフォルダをそのまま使う** | `my-term-ts .` のように使いたいため。新規タブでは従来通りダイアログを出す |
| D&D パス | そのまま挿入 | **スペース入りは `"` で括る** | パスにスペースがあると claude が解釈できないため |
| 入力メモの並び | 新しいものが上 | **古い順に上から。新しい入力が下に積まれる** | チャットのように読み下せるほうが会話の流れを追いやすいため |
| 入力メモの初期表示 | 表示 | **非表示(F1 で開く)** | ふだんは端末を広く使いたいため |
| タブ切替キー | なし | `Ctrl+Tab` を追加 | マウスなしで回したいため |
| 描画 | 自前でセル描画(2 パス) | xterm.js(WebGL) | 全角文字の背景欠け等は xterm.js 側で解決済み |
| スクロールバック | なし(可視領域のみ) | 10000 行 | xterm.js の標準機能で得られるため |

---

## 7. 今後の課題(スコープ外)

- 承認まち検知が文言依存。claude の英語 UI 更新で壊れうる
- 検知ワードや配色・フォントサイズの設定ファイル対応
- Windows 以外での動作確認(コードは POSIX 対応済みだが未検証)
- `electron-builder` によるインストーラ生成
