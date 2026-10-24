using System;
using System.Runtime.InteropServices;

namespace OctantPath.Desktop
{
    // Linux の窓（WebKitGTK）で、ページに音ありの自動再生を許す。下見の ArcViewer は「譜面を見る」を一度押したあと、選んだ曲の譜面を押さずに再生する
    // （再生を始めるのは ArcViewer が譜面を読み終えたときで、押したときではない）。
    // WebKitGTK は音ありの自動再生を、ページを開くときの方針（WebKitWebsitePolicies の autoplay。既定は音なしだけ）で決める。Photino はこの方針を渡さず、
    // Photino の MediaAutoplayEnabled は Linux では WebKit の別の設定（media-playback-requires-user-gesture）に渡るだけで、これを変えない（Photino.Native 4.0.22、WebKitGTK 2.52 で確かめた）。
    // そこで Photino の作った Web 表示の decide-policy で、ページを開くたびに「許す」を渡す。同じオリジンの枠（/arcviewer/）はページの方針に従う。
    // Windows（WebView2）は、Photino が MediaAutoplayEnabled（既定で true）から --autoplay-policy=no-user-gesture-required を渡す
    internal static class WebKitAutoplay
    {
        private const int AutoplayAllow = 0;     // WEBKIT_AUTOPLAY_ALLOW
        private const int NavigationAction = 0;  // WEBKIT_POLICY_DECISION_TYPE_NAVIGATION_ACTION

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int DecidePolicy(IntPtr webView, IntPtr decision, int type, IntPtr data);

        // GTK が呼ぶあいだ集められないよう持っておく
        private static readonly DecidePolicy Decide = OnDecidePolicy;
        private static IntPtr _allow;

        // 窓のスレッドで、Photino が窓と Web 表示を作ったあと、GTK のループが回る前に呼ぶ（最初のページを開く判断はループの中で来る）
        public static void Allow(IntPtr webView)
        {
            _allow = AllowPolicies();
            g_signal_connect_data(webView, "decide-policy", Marshal.GetFunctionPointerForDelegate(Decide), IntPtr.Zero, IntPtr.Zero, 0);
        }

        // ページ（と中の枠）を開く判断に方針を付けて進める。ほかの判断（新しい窓、応答）は WebKit の既定のまま
        private static int OnDecidePolicy(IntPtr webView, IntPtr decision, int type, IntPtr data)
        {
            if (type != NavigationAction)
            {
                return 0;
            }
            webkit_policy_decision_use_with_policies(decision, _allow);
            return 1;
        }

        // webkit_website_policies_new_with_policies は可変長の引数なので、同じものを g_object_new_with_properties で作る
        private static IntPtr AllowPolicies()
        {
            var autoplay = new GValue();
            g_value_init(ref autoplay, webkit_autoplay_policy_get_type());
            g_value_set_enum(ref autoplay, AutoplayAllow);
            var policies = g_object_new_with_properties(webkit_website_policies_get_type(), 1, new[] { "autoplay" }, new[] { autoplay });
            g_value_unset(ref autoplay);
            return policies;
        }

        // GLib の GValue（型と 2 つの値）
        [StructLayout(LayoutKind.Sequential)]
        private struct GValue
        {
            public IntPtr Type;
            public long Data0;
            public long Data1;
        }

        [DllImport("libgobject-2.0.so.0")]
        private static extern ulong g_signal_connect_data(IntPtr instance, string signal, IntPtr handler, IntPtr data, IntPtr destroy, int flags);

        [DllImport("libgobject-2.0.so.0")]
        private static extern IntPtr g_object_new_with_properties(IntPtr type, uint count, string[] names, GValue[] values);

        [DllImport("libgobject-2.0.so.0")]
        private static extern IntPtr g_value_init(ref GValue value, IntPtr type);

        [DllImport("libgobject-2.0.so.0")]
        private static extern void g_value_set_enum(ref GValue value, int number);

        [DllImport("libgobject-2.0.so.0")]
        private static extern void g_value_unset(ref GValue value);

        [DllImport("libwebkit2gtk-4.1.so.0")]
        private static extern IntPtr webkit_website_policies_get_type();

        [DllImport("libwebkit2gtk-4.1.so.0")]
        private static extern IntPtr webkit_autoplay_policy_get_type();

        [DllImport("libwebkit2gtk-4.1.so.0")]
        private static extern void webkit_policy_decision_use_with_policies(IntPtr decision, IntPtr policies);
    }
}
