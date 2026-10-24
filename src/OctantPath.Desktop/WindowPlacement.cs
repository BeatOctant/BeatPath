using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Photino.NET;

namespace OctantPath.Desktop
{
    // 窓を前に閉じたときの位置と大きさで開く（2026-10-21）。最大化していたら最大化して開き、元に戻すと最大化の前の位置と大きさになる。
    // Desktop の設定の場所の desktop.json の "window" に {x, y, width, height, maximized} で置く。単位は Photino の Location と Size のまま:
    //   Windows: 枠を含む外の位置と大きさ、物理の px（Photino は窓のスレッドを画面ごとの DPI に合わせる）
    //   Linux:   位置は枠の左上、大きさは窓の中、GTK の論理 px
    // その位置の窓の上の端がどの画面の作業領域にも十分に入らないとき（画面を外したときなど）と、まだないときは、初めてと同じに開く（最大化）。
    // Linux の Wayland ではプログラムが窓の位置を知ることも決めることもできないので、大きさと最大化だけを戻し、位置はデスクトップが決める（x, y は null）。
    // 窓のスレッドで使う（Photino の窓の出来事もそこで来る）
    internal sealed class WindowPlacement
    {
        private const string Key = "window";

        // 窓の上の端（タイトルの帯）のうち、これだけが作業領域に入っていればその位置で開く（マウスでつかめる）
        private const int Top = 40;
        private const int SeenWidth = 100;
        private const int SeenHeight = Top / 2;

        public sealed class Bounds
        {
            [JsonProperty("x")] public int? X;
            [JsonProperty("y")] public int? Y;
            [JsonProperty("width")] public int Width;
            [JsonProperty("height")] public int Height;
            [JsonProperty("maximized")] public bool Maximized;
        }

        private readonly PhotinoWindow _window;
        private readonly string _config;
        private readonly bool _wayland;

        // 最大化・最小化していないときの最後の位置と大きさ
        private Bounds _normal;

        // window: まだ作っていない窓。config: desktop.json のあるフォルダ。前の位置と大きさがあれば窓に入れ、なければ first で初めての形にする
        public WindowPlacement(PhotinoWindow window, string config, Action<PhotinoWindow> first)
        {
            _window = window;
            _config = config;
            var (areas, wayland) = Screens();
            _wayland = wayland;
            var start = Fit(Load(config), areas);
            if (start == null)
            {
                first(window);
                return;
            }
            _normal = start;
            window.SetUseOsDefaultSize(false).SetSize(start.Width, start.Height).SetMaximized(start.Maximized);
            if (start.X != null && !_wayland)
            {
                window.SetUseOsDefaultLocation(false).SetLocation(new Point(start.X.Value, start.Y.Value));
            }
        }

        // 開くときの位置と大きさ。saved は前の窓のもの、workAreas は画面の作業領域。窓はその画面の作業領域より大きくしない。
        // 初めてと同じに開くときは null
        public static Bounds Fit(Bounds saved, IReadOnlyList<Rectangle> workAreas)
        {
            if (saved == null || saved.Width <= 0 || saved.Height <= 0 || (saved.X == null) != (saved.Y == null) || workAreas.Count == 0)
            {
                return null;
            }
            var area = workAreas[0];
            if (saved.X != null)
            {
                var found = false;
                foreach (var a in workAreas)
                {
                    if (ShowsTop(saved.X.Value, saved.Y.Value, saved.Width, a))
                    {
                        area = a;
                        found = true;
                        break;
                    }
                }
                if (!found)
                {
                    return null;
                }
            }
            return new Bounds
            {
                X = saved.X,
                Y = saved.Y,
                Width = Math.Min(saved.Width, area.Width),
                Height = Math.Min(saved.Height, area.Height),
                Maximized = saved.Maximized,
            };
        }

        // 窓の上の端（x, y, 幅）が作業領域に十分に入っているか
        private static bool ShowsTop(int x, int y, int width, Rectangle area)
        {
            var across = Math.Min(x + width, area.Right) - Math.Max(x, area.Left);
            var down = Math.Min(y + Top, area.Bottom) - Math.Max(y, area.Top);
            return across >= SeenWidth && down >= SeenHeight;
        }

        // desktop.json の "window"。ない、または形が違えば null
        private static Bounds Load(string config)
        {
            try
            {
                var path = Path.Combine(config, DesktopLocal.FileName);
                if (!File.Exists(path) || !(JObject.Parse(File.ReadAllText(path))[Key] is JObject saved))
                {
                    return null;
                }
                int? Whole(string name) => saved[name]?.Type == JTokenType.Integer ? (int?)saved[name] : null;
                var (x, y, width, height) = (Whole("x"), Whole("y"), Whole("width"), Whole("height"));
                // 位置は x と y の両方が数か、両方が null（Wayland）
                var unplaced = saved["x"]?.Type == JTokenType.Null && saved["y"]?.Type == JTokenType.Null;
                if (width == null || height == null || saved["maximized"]?.Type != JTokenType.Boolean || ((x == null || y == null) && !unplaced))
                {
                    return null;
                }
                return new Bounds { X = x, Y = y, Width = width.Value, Height = height.Value, Maximized = (bool)saved["maximized"] };
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is JsonException || e is OverflowException)
            {
                return null;
            }
        }

        // 窓が動いたか大きさが変わったとき。最大化・最小化していなければ、その位置と大きさを覚えておく
        public void Changed()
        {
            if (_window.Maximized || _window.Minimized)
            {
                return;
            }
            var location = _window.Location;
            var size = _window.Size;
            _normal = new Bounds { X = _wayland ? (int?)null : location.X, Y = _wayland ? (int?)null : location.Y, Width = size.Width, Height = size.Height };
        }

        // 窓を閉じるとき。いまの位置と大きさを desktop.json に書く（書けなくても窓は閉じる）
        public void Save()
        {
            var maximized = _window.Maximized;
            if (_normal == null && !_window.Minimized)
            {
                // 初めから最大化したまま（ほかの画面へ移したときも）: 最大化した窓の位置と大きさ。次もその画面で最大化して開く
                var location = _window.Location;
                var size = _window.Size;
                _normal = new Bounds { X = _wayland ? (int?)null : location.X, Y = _wayland ? (int?)null : location.Y, Width = size.Width, Height = size.Height };
            }
            if (_normal == null)
            {
                return;
            }
            _normal.Maximized = maximized;
            try
            {
                DesktopLocal.Save(_config, Key, JObject.FromObject(_normal));
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"窓の位置を覚えられませんでした: {e.Message}");
            }
        }

        // 画面の作業領域（タスクバーなどを除いた広さ）と、Wayland か。窓を作る前に、窓と同じ単位で
        private static (IReadOnlyList<Rectangle> Areas, bool Wayland) Screens()
        {
            var areas = new List<Rectangle>();
            if (OperatingSystem.IsWindows())
            {
                // Photino が窓のスレッドに入れるのと同じ DPI の扱い（画面ごと）。物理の px になる
                SetThreadDpiAwarenessContext(new IntPtr(-3)); // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE
                EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, dc, rect, data) =>
                {
                    var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
                    if (GetMonitorInfo(monitor, ref info))
                    {
                        areas.Add(Rectangle.FromLTRB(info.Work.Left, info.Work.Top, info.Work.Right, info.Work.Bottom));
                    }
                    return true;
                }, IntPtr.Zero);
                return (areas, false);
            }
            if (!OperatingSystem.IsLinux())
            {
                return (areas, false);
            }
            // Photino が GTK を始める前に Xlib をスレッドで使えるようにするので、GTK を先に始めるここでも先にする
            XInitThreads();
            if (gtk_init_check(IntPtr.Zero, IntPtr.Zero) == 0)
            {
                return (areas, false);
            }
            var display = gdk_display_get_default();
            var count = gdk_display_get_n_monitors(display);
            for (var i = 0; i < count; i++)
            {
                gdk_monitor_get_workarea(gdk_display_get_monitor(display, i), out var work);
                areas.Add(new Rectangle(work.X, work.Y, work.Width, work.Height));
            }
            return (areas, IsWayland(display));
        }

        private static bool IsWayland(IntPtr display)
        {
            try
            {
                return g_type_check_instance_is_a(display, gdk_wayland_display_get_type()) != 0;
            }
            catch (EntryPointNotFoundException)
            {
                // Wayland なしで作られた GTK
                return false;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Rect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MonitorInfo
        {
            public int Size;
            public Rect Monitor;
            public Rect Work;
            public uint Flags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct GdkRectangle
        {
            public int X;
            public int Y;
            public int Width;
            public int Height;
        }

        private delegate bool MonitorEnum(IntPtr monitor, IntPtr dc, IntPtr rect, IntPtr data);

        [DllImport("user32.dll")]
        private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);

        [DllImport("user32.dll")]
        private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorEnum callback, IntPtr data);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

        [DllImport("libX11.so.6")]
        private static extern int XInitThreads();

        [DllImport("libgtk-3.so.0")]
        private static extern int gtk_init_check(IntPtr argc, IntPtr argv);

        [DllImport("libgdk-3.so.0")]
        private static extern IntPtr gdk_display_get_default();

        [DllImport("libgdk-3.so.0")]
        private static extern int gdk_display_get_n_monitors(IntPtr display);

        [DllImport("libgdk-3.so.0")]
        private static extern IntPtr gdk_display_get_monitor(IntPtr display, int monitor);

        [DllImport("libgdk-3.so.0")]
        private static extern void gdk_monitor_get_workarea(IntPtr monitor, out GdkRectangle workArea);

        [DllImport("libgdk-3.so.0")]
        private static extern IntPtr gdk_wayland_display_get_type();

        [DllImport("libgobject-2.0.so.0")]
        private static extern int g_type_check_instance_is_a(IntPtr instance, IntPtr type);
    }
}
