// ConPTY セッション。シェルの起動と読み書きだけを担う。
// (TS 版 src/main/pty.ts に相当。node-pty の代わりに ConPTY を直接叩く)
//
// ANSI の解釈と画面グリッド保持は TerminalBuffer / VtParser が担当する。

using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace CcTerm
{
    internal sealed class PseudoConsole
    {
        private IntPtr hPC;
        private IntPtr hProcess;
        private FileStream input;
        private FileStream output;
        private readonly object writeLock = new object();
        private int closed;

        /// <summary>出力(UTF-8 をデコード済み)。読み取りスレッドから呼ばれる。</summary>
        public event Action<string> Output;
        /// <summary>プロセス終了。読み取りスレッドから 1 回だけ呼ばれる。</summary>
        public event Action Exited;

        private PseudoConsole()
        {
        }

        /// <summary>指定プログラムを ConPTY 上で起動する。出力は Begin() 後に流れ始める。</summary>
        public static PseudoConsole Start(LaunchConfig launch, int cols, int rows)
        {
            var pty = new PseudoConsole();
            pty.Spawn(launch, cols, rows);
            return pty;
        }

        private void Spawn(LaunchConfig launch, int cols, int rows)
        {
            SafeFileHandle inRead, inWrite, outRead, outWrite;
            if (!Native.CreatePipe(out inRead, out inWrite, IntPtr.Zero, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreatePipe(input)");
            if (!Native.CreatePipe(out outRead, out outWrite, IntPtr.Zero, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreatePipe(output)");

            int hr = Native.CreatePseudoConsole(
                new Native.COORD(Math.Max(1, cols), Math.Max(1, rows)), inRead, outWrite, 0, out hPC);
            if (hr != 0) throw new Win32Exception(hr, "CreatePseudoConsole");

            // ConPTY 側が複製を持つので、こちらの端は閉じておく(閉じないと EOF が来ない)。
            inRead.Dispose();
            outWrite.Dispose();

            input = new FileStream(inWrite, FileAccess.Write, 1, false);
            output = new FileStream(outRead, FileAccess.Read, 1, false);

            IntPtr attrSize = IntPtr.Zero;
            Native.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attrSize);
            IntPtr attrList = Marshal.AllocHGlobal(attrSize);
            try
            {
                if (!Native.InitializeProcThreadAttributeList(attrList, 1, 0, ref attrSize))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "InitializeProcThreadAttributeList");
                if (!Native.UpdateProcThreadAttribute(
                        attrList, 0, Native.PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE, hPC,
                        (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "UpdateProcThreadAttribute");

                var si = new Native.STARTUPINFOEX();
                si.StartupInfo.cb = Marshal.SizeOf(typeof(Native.STARTUPINFOEX));
                // 親の標準ハンドルを子に引き継がせない(ConPTY のものだけを使わせる)。
                si.StartupInfo.dwFlags = Native.STARTF_USESTDHANDLES;
                si.lpAttributeList = attrList;

                Native.PROCESS_INFORMATION pi;
                bool ok = Native.CreateProcess(
                    null, new StringBuilder(launch.CommandLine()), IntPtr.Zero, IntPtr.Zero, false,
                    Native.EXTENDED_STARTUPINFO_PRESENT | Native.CREATE_UNICODE_ENVIRONMENT,
                    IntPtr.Zero, launch.Cwd, ref si, out pi);
                if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateProcess: " + launch.Program);

                Native.CloseHandle(pi.hThread);
                hProcess = pi.hProcess;
            }
            finally
            {
                Native.DeleteProcThreadAttributeList(attrList);
                Marshal.FreeHGlobal(attrList);
            }
        }

        /// <summary>読み取りと終了監視のスレッドを起こす(イベントを繋いでから呼ぶ)。</summary>
        public void Begin()
        {
            var reader = new Thread(ReadLoop);
            reader.IsBackground = true;
            reader.Name = "pty-reader";
            reader.Start();

            var waiter = new Thread(WaitLoop);
            waiter.IsBackground = true;
            waiter.Name = "pty-waiter";
            waiter.Start();
        }

        private void ReadLoop()
        {
            var bytes = new byte[16384];
            var chars = new char[bytes.Length + 2];
            // 読み取り境界で UTF-8 の途中が切れても、Decoder が続きを持ち越してくれる。
            var decoder = new UTF8Encoding(false).GetDecoder();
            try
            {
                while (true)
                {
                    int n = output.Read(bytes, 0, bytes.Length);
                    if (n <= 0) break;
                    int cn = decoder.GetChars(bytes, 0, n, chars, 0);
                    if (cn == 0) continue;
                    var handler = Output;
                    if (handler != null) handler(new string(chars, 0, cn));
                }
            }
            catch (IOException)
            {
                // パイプが閉じられた。
            }
            catch (ObjectDisposedException)
            {
            }
            finally
            {
                output.Dispose();
            }

            var exited = Exited;
            if (exited != null) exited();
        }

        /// <summary>プロセスが終わったら ConPTY を閉じる。閉じると ReadLoop に EOF が届く。</summary>
        private void WaitLoop()
        {
            Native.WaitForSingleObject(hProcess, Native.INFINITE);
            Close();
        }

        /// <summary>シェルへ文字列を送る。</summary>
        public void Write(string data)
        {
            if (closed != 0 || string.IsNullOrEmpty(data)) return;
            var bytes = Encoding.UTF8.GetBytes(data);
            lock (writeLock)
            {
                try
                {
                    input.Write(bytes, 0, bytes.Length);
                    input.Flush();
                }
                catch (IOException)
                {
                    // 終了直後に書き込まれた場合。
                }
                catch (ObjectDisposedException)
                {
                }
            }
        }

        /// <summary>端末のサイズ変更を ConPTY に伝える。</summary>
        public void Resize(int cols, int rows)
        {
            if (closed != 0) return;
            Native.ResizePseudoConsole(hPC, new Native.COORD(Math.Max(1, cols), Math.Max(1, rows)));
        }

        /// <summary>セッションを終了させる(タブを閉じた時・アプリ終了時)。</summary>
        public void Kill()
        {
            if (closed != 0) return;
            try
            {
                Native.TerminateProcess(hProcess, 1);
            }
            catch
            {
                // 既に死んでいる場合。
            }
            // WaitLoop が続きの後始末(ConPTY を閉じる)をする。
        }

        /// <summary>
        /// ConPTY を閉じる。ConPTY に繋がっている子プロセス(claude など)も終了する。
        /// 古い Windows では出力を読み切るまで ClosePseudoConsole が戻らないことがあるので、
        /// UI スレッドではなく監視スレッドから呼ぶ。
        /// </summary>
        private void Close()
        {
            if (Interlocked.Exchange(ref closed, 1) != 0) return;
            Native.ClosePseudoConsole(hPC);
            lock (writeLock)
            {
                try
                {
                    input.Dispose();
                }
                catch (IOException)
                {
                }
            }
            Native.CloseHandle(hProcess);
        }
    }
}
