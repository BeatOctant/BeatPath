using System;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using Photino.NET;

namespace OctantPath.Desktop
{
    // 窓の最小の大きさ（2026-10-19: 720p）。ページ（wwwroot/index.html）が崩れずに出る最小を CSS の px で決めておき、窓の中をそれより小さくさせない。
    // ただし窓のある画面の作業領域（タスクバーなどを除いた広さ）より大きくはしない。それより小さい画面では、ページの方がページごと動く。
    // Photino は最小の大きさを OS にそのまま渡すので、OS ごとの単位にする（Photino.Native 4.0.22）:
    //   Windows: 枠とタイトルの帯を含む、物理の px。ページは画面の拡大率で大きくなるので、ページの最小に DPI / 96 を掛けて枠の分を足す。
    //            拡大率の違う画面へ窓を動かすとページの大きさが変わるので、窓が動くたびに合わせ直す（Photino は WM_DPICHANGED を扱わない）
    //   Linux:   窓の中の、GTK の論理 px（ページの CSS の px と同じ）。枠とタイトルの帯は GTK と窓の管理が外に足す
    // 窓のスレッドで使う（Photino の窓の出来事もそこで来る）
    internal sealed class WindowMinSize
    {
        public const int PageMinWidth = 1280;
        public const int PageMinHeight = 720;

        private readonly PhotinoWindow _window;
        private readonly IntPtr _gtkWindow;
        private Size _applied;

        // gtkWindow: Linux の GTK の窓（LinuxWindow.Find）。Windows では使わない
        public WindowMinSize(PhotinoWindow window, IntPtr gtkWindow)
        {
            _window = window;
            _gtkWindow = gtkWindow;
        }

        // いまの画面に合わせて、窓の最小を決め直す。窓ができたとき、動いたとき、大きさが変わったときに呼ぶ。
        // 最小化しているあいだは窓の大きさと位置が画面の外の仮のものなので、決め直さない
        public void Fit()
        {
            if (_window.Minimized)
            {
                return;
            }
            var work = WorkArea();
            Size min;
            if (OperatingSystem.IsWindows())
            {
                var scale = _window.ScreenDpi / 96.0;
                var frame = WindowsFrame();
                min = new Size(
                    Math.Min((int)Math.Ceiling(PageMinWidth * scale) + frame.Width, work.Width),
                    Math.Min((int)Math.Ceiling(PageMinHeight * scale) + frame.Height, work.Height));
            }
            else
            {
                var frame = LinuxWindow.Frame(_gtkWindow);
                min = new Size(Math.Min(PageMinWidth, work.Width - frame.Width), Math.Min(PageMinHeight, work.Height - frame.Height));
            }
            if (min != _applied)
            {
                _applied = min;
                _window.SetMinSize(min.Width, min.Height);
            }
        }

        // 窓の真ん中がある画面の作業領域。Linux の Wayland では窓の位置が分からないので、最初の画面になる
        private Rectangle WorkArea()
        {
            var monitors = _window.Monitors;
            if (monitors.Count == 0)
            {
                return new Rectangle(0, 0, int.MaxValue, int.MaxValue);
            }
            var location = _window.Location;
            var size = _window.Size;
            var center = new Point(location.X + size.Width / 2, location.Y + size.Height / 2);
            return monitors.Where(m => m.MonitorArea.Contains(center)).Select(m => m.WorkArea).DefaultIfEmpty(monitors[0].WorkArea).First();
        }

        // 窓の大きさ（Photino の Size。枠を含む）のうち、ページの外にある枠とタイトルの帯の分
        private Size WindowsFrame()
        {
            GetClientRect(_window.WindowHandle, out var client);
            var size = _window.Size;
            return new Size(size.Width - (client.Right - client.Left), size.Height - (client.Bottom - client.Top));
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Rect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [DllImport("user32.dll")]
        private static extern bool GetClientRect(IntPtr window, out Rect rect);
    }
}
