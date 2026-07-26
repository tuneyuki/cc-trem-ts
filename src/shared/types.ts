// main / preload / renderer で共有する型定義。

/** PTY で起動するプログラムの指定(Rust 版の LaunchConfig 相当)。 */
export interface LaunchConfig {
  program: string;
  args: string[];
  cwd: string;
}

/** 起動直後に renderer が main から受け取る初期情報。 */
export interface Bootstrap {
  /** `--shell` 指定か(素のシェルモード)。新規タブの起動方法を決める。 */
  plainShell: boolean;
  /** 最初のタブを起動する設定。 */
  initialLaunch: LaunchConfig;
}

/** preload が contextBridge で renderer に公開する API。 */
export interface RendererApi {
  bootstrap(): Promise<Bootstrap>;
  /**
   * 新規タブの起動設定を作る。claude モードではフォルダ選択ダイアログを出す。
   * キャンセルされたら null。
   */
  newTabLaunch(): Promise<LaunchConfig | null>;
  /** 入力メモをファイルへ保存(保存先はダイアログ)。保存したら true。 */
  saveMemos(text: string): Promise<boolean>;
  /** クリップボードの画像を一時 PNG に保存し、そのパスを返す。画像が無ければ null。 */
  clipboardImage(): Promise<string | null>;
  /** テキストをクリップボードへコピーする(入力メモのクリック用)。 */
  copyText(text: string): void;
  /** 注意喚起のビープ音。 */
  beep(): void;
  /** タスクバーを点滅させて注意を促す。 */
  flash(): void;
  /** ウィンドウを閉じる(全タブが終了した時)。 */
  closeWindow(): void;
  /** D&D された File から実パスを得る(Electron 32+ では File.path が使えない)。 */
  getPathForFile(file: File): string;
  pty: {
    spawn(launch: LaunchConfig, cols: number, rows: number): Promise<number>;
    write(id: number, data: string): void;
    resize(id: number, cols: number, rows: number): void;
    kill(id: number): void;
    onData(cb: (id: number, data: string) => void): void;
    onExit(cb: (id: number) => void): void;
  };
}
