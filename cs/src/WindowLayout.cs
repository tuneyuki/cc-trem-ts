// 複数の ccterm-cs ウィンドウの配置と色分け。
//
// 各ウィンドウは自分のウィンドウハンドルに「区画番号」と「色番号」を
// ウィンドウプロパティ(SetProp)として付けておく。新しく起動したウィンドウは
// EnumWindows で他の ccterm-cs を探してそれを読み、空いている区画・色を選ぶ。
// 常駐プロセスや設定ファイルを使わずに、ウィンドウ同士で調整できる。
//
// 区画は画面の 4 分割(0: 左上 / 1: 右上 / 2: 左下 / 3: 右下)。

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace CcTerm
{
    internal static class WindowLayout
    {
        public const int SlotCount = 4;

        private const string SlotProp = "ccterm-cs.slot";
        private const string ColorProp = "ccterm-cs.color";

        /// <summary>「この区画へ移って」という依頼(wParam = 区画、lParam = 依頼元のウィンドウ)。</summary>
        public static readonly int ArrangeMessage = Native.RegisterWindowMessage("ccterm-cs.arrange");

        // プロパティには 0 以外を入れたいので、番号は +1 して保存する。

        public static int GetSlot(IntPtr hwnd)
        {
            return (int)Native.GetProp(hwnd, SlotProp) - 1;
        }

        public static void SetSlot(IntPtr hwnd, int slot)
        {
            Native.SetProp(hwnd, SlotProp, (IntPtr)(slot + 1));
        }

        public static int GetColor(IntPtr hwnd)
        {
            return (int)Native.GetProp(hwnd, ColorProp) - 1;
        }

        public static void SetColor(IntPtr hwnd, int color)
        {
            Native.SetProp(hwnd, ColorProp, (IntPtr)(color + 1));
        }

        /// <summary>ウィンドウが閉じる前にプロパティを外す(付けたままだと後片付けされない)。</summary>
        public static void Clear(IntPtr hwnd)
        {
            Native.RemoveProp(hwnd, SlotProp);
            Native.RemoveProp(hwnd, ColorProp);
        }

        /// <summary>自分以外の、表示中の ccterm-cs ウィンドウ。</summary>
        public static List<IntPtr> FindOthers(IntPtr self)
        {
            var found = new List<IntPtr>();
            Native.EnumWindows(delegate(IntPtr h, IntPtr lp)
            {
                if (h != self && Native.IsWindowVisible(h) && Native.GetProp(h, SlotProp) != IntPtr.Zero)
                    found.Add(h);
                return true;
            }, IntPtr.Zero);
            return found;
        }

        /// <summary>空いている区画のうち最初のもの。全部埋まっていれば -1。</summary>
        public static int ChooseSlot(IntPtr self)
        {
            var used = new bool[SlotCount];
            foreach (var h in FindOthers(self))
            {
                int s = GetSlot(h);
                if (s >= 0 && s < SlotCount) used[s] = true;
            }
            for (int i = 0; i < SlotCount; i++)
            {
                if (!used[i]) return i;
            }
            return -1;
        }

        /// <summary>
        /// 色を選ぶ。フォルダ名から決まる色を優先し(同じプロジェクトはいつも同じ色)、
        /// 他のウィンドウが使っていれば次の空いている色にずらす。
        /// </summary>
        public static int ChooseColor(IntPtr self, string key)
        {
            int n = Theme.Tints.Length;
            var used = new bool[n];
            foreach (var h in FindOthers(self))
            {
                int c = GetColor(h);
                if (c >= 0 && c < n) used[c] = true;
            }
            int preferred = (int)(Fnv1a(key.ToLowerInvariant()) % (uint)n);
            for (int i = 0; i < n; i++)
            {
                int c = (preferred + i) % n;
                if (!used[c]) return c;
            }
            return preferred;
        }

        /// <summary>文字列の安定したハッシュ(実行のたびに変わらないもの)。</summary>
        private static uint Fnv1a(string s)
        {
            uint h = 2166136261;
            foreach (char c in s)
            {
                h ^= c;
                h *= 16777619;
            }
            return h;
        }

        /// <summary>区画の矩形(作業領域 = タスクバーを除いた範囲の 4 分割)。</summary>
        public static Rectangle SlotBounds(Screen screen, int slot)
        {
            var area = screen.WorkingArea;
            int halfW = area.Width / 2;
            int halfH = area.Height / 2;
            int col = slot % 2;
            int row = slot / 2;
            return new Rectangle(
                area.Left + col * halfW,
                area.Top + row * halfH,
                col == 0 ? halfW : area.Width - halfW,
                row == 0 ? halfH : area.Height - halfH);
        }

        /// <summary>
        /// ウィンドウを区画に収める。Windows 10 以降のウィンドウには見えない枠(リサイズ用)が
        /// 外側に付いているので、その分だけ広げて、見た目の端が区画の端にぴったり合うようにする。
        /// </summary>
        public static void MoveToSlot(Form form, int slot, Screen screen)
        {
            if (form.WindowState != FormWindowState.Normal) form.WindowState = FormWindowState.Normal;
            var target = SlotBounds(screen, slot);

            Native.RECT win, visible;
            if (Native.GetWindowRect(form.Handle, out win) &&
                Native.DwmGetWindowAttribute(
                    form.Handle, Native.DWMWA_EXTENDED_FRAME_BOUNDS, out visible,
                    Marshal.SizeOf(typeof(Native.RECT))) == 0)
            {
                int left = visible.Left - win.Left;
                int top = visible.Top - win.Top;
                int right = win.Right - visible.Right;
                int bottom = win.Bottom - visible.Bottom;
                // 非表示のうちは DWM が正しい値を返さないことがあるので、明らかに変な値は使わない。
                if (left >= 0 && left < 32 && top >= 0 && top < 32 && right >= 0 && right < 32 && bottom >= 0 && bottom < 32)
                {
                    target = new Rectangle(
                        target.Left - left, target.Top - top,
                        target.Width + left + right, target.Height + top + bottom);
                }
            }
            form.Bounds = target;
        }

        /// <summary>
        /// 開いている ccterm-cs をすべて 4 分割に並べ直す(区画の若い順に詰める)。
        /// 各ウィンドウへは依頼のメッセージを送り、移動はそれぞれが自分で行う。
        /// </summary>
        public static void ArrangeAll(IntPtr self)
        {
            var all = FindOthers(self);
            all.Add(self);
            // 今の区画の順を保つ(区画を持たないものは後ろへ)。
            all.Sort(delegate(IntPtr a, IntPtr b)
            {
                int sa = GetSlot(a), sb = GetSlot(b);
                if (sa < 0) sa = int.MaxValue;
                if (sb < 0) sb = int.MaxValue;
                if (sa != sb) return sa.CompareTo(sb);
                return a.ToInt64().CompareTo(b.ToInt64());
            });
            for (int i = 0; i < all.Count && i < SlotCount; i++)
            {
                Native.PostMessage(all[i], ArrangeMessage, (IntPtr)i, self);
            }
        }
    }
}
