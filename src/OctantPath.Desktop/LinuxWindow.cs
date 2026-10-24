using System;
using System.Drawing;
using System.Runtime.InteropServices;

namespace OctantPath.Desktop
{
    // Linux で Photino が作った GTK の窓と、その中の Web 表示（WebKitGTK）。Photino はどちらも渡さないので、GTK の窓の一覧から探す。
    // Photino は Web 表示を窓にそのまま入れる。窓は Desktop の 1 つだけ。窓のスレッドで使う
    internal static class LinuxWindow
    {
        // 窓と Web 表示。見つからなければ両方 IntPtr.Zero
        public static (IntPtr Window, IntPtr WebView) Find()
        {
            var webViewType = webkit_web_view_get_type();
            var toplevels = gtk_window_list_toplevels();
            try
            {
                for (var item = toplevels; item != IntPtr.Zero; item = Marshal.ReadIntPtr(item, IntPtr.Size))
                {
                    var window = Marshal.ReadIntPtr(item);
                    var child = gtk_bin_get_child(window);
                    if (child != IntPtr.Zero && g_type_check_instance_is_a(child, webViewType) != 0)
                    {
                        return (window, child);
                    }
                }
                return (IntPtr.Zero, IntPtr.Zero);
            }
            finally
            {
                g_list_free(toplevels);
            }
        }

        // 窓の大きさ（GTK の窓の中。Photino の Size）の外にある、枠とタイトルの帯の分（論理 px）。
        // 窓が見つからなかったとき（IntPtr.Zero）と、窓が画面に出るまでは分からないので 0
        public static Size Frame(IntPtr window)
        {
            if (window == IntPtr.Zero)
            {
                return Size.Empty;
            }
            var gdkWindow = gtk_widget_get_window(window);
            if (gdkWindow == IntPtr.Zero || gtk_widget_get_mapped(window) == 0)
            {
                return Size.Empty;
            }
            gdk_window_get_frame_extents(gdkWindow, out var frame);
            gtk_window_get_size(window, out var width, out var height);
            return new Size(Math.Max(0, frame.Width - width), Math.Max(0, frame.Height - height));
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct GdkRectangle
        {
            public int X;
            public int Y;
            public int Width;
            public int Height;
        }

        [DllImport("libgtk-3.so.0")]
        private static extern IntPtr gtk_window_list_toplevels();

        [DllImport("libgtk-3.so.0")]
        private static extern IntPtr gtk_bin_get_child(IntPtr bin);

        [DllImport("libgtk-3.so.0")]
        private static extern IntPtr gtk_widget_get_window(IntPtr widget);

        [DllImport("libgtk-3.so.0")]
        private static extern int gtk_widget_get_mapped(IntPtr widget);

        [DllImport("libgtk-3.so.0")]
        private static extern void gtk_window_get_size(IntPtr window, out int width, out int height);

        [DllImport("libgdk-3.so.0")]
        private static extern void gdk_window_get_frame_extents(IntPtr window, out GdkRectangle rect);

        [DllImport("libglib-2.0.so.0")]
        private static extern void g_list_free(IntPtr list);

        [DllImport("libgobject-2.0.so.0")]
        private static extern int g_type_check_instance_is_a(IntPtr instance, IntPtr type);

        [DllImport("libwebkit2gtk-4.1.so.0")]
        private static extern IntPtr webkit_web_view_get_type();
    }
}
