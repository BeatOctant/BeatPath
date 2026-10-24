using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Photino.NET;

namespace OctantPath.Desktop
{
    // Desktop の窓（Photino: Windows は WebView2、Linux は WebKitGTK 4.1。2026-10-18）。ブラウザの --app ではなく Desktop 自身の窓なので、
    // タスクバーには OctantPath として出て、ブラウザの都合で開かないこともない。中身は手元のサーバーのページ。
    // 窓は 1 つで、閉じると Desktop も終わる。OS の選択の窓（フォルダ）もこの窓から開く。
    // 窓のスレッドは、この窓を作ったスレッド（Run もそこで呼ぶ）。Present・PickFolder・Close はサーバーの要求や signal のスレッドから呼ぶが、
    // Photino は窓への呼び出し（選択画面、最小化、いちばん上、閉じる）を PhotinoWindow.Invoke で窓のスレッドに回し、終わるまで待つ（Photino.NET 4.0.16）
    public sealed class DesktopWindow
    {
        public const string Title = "OctantPath";

        // Linux の窓の名前（X11 の WM_CLASS、Wayland の app_id）。OctantCompass の "octantcompass" に揃える
        private const string AppId = "octantpath";

        private readonly PhotinoWindow _window;

        // 窓の最小の大きさ。窓ができてから作る
        private WindowMinSize _minSize;

        // 前に閉じたときの位置と大きさで開き、閉じるときに覚える
        private readonly WindowPlacement _placement;

        // 窓ができてから閉じるまで true。Close はそのあいだだけ窓に届ける（窓ができる前の Photino の Close は例外、閉じたあとは返ってこない）
        private volatile bool _open;

        // webViewData: WebView2 が自分のデータを置くフォルダ（Windows。省くと実行ファイルの隣に作るので、Desktop のキャッシュに置く）。
        // config: 窓の位置と大きさを覚える desktop.json のあるフォルダ
        public DesktopWindow(string url, string webViewData, string config)
        {
            if (OperatingSystem.IsLinux())
            {
                // GTK が始まる前に。デスクトップが実行ファイルの名前でなく OctantPath として扱う
                g_set_prgname(AppId);
                g_set_application_name(Title);
            }
            _window = new PhotinoWindow()
                .SetLogVerbosity(0)
                .SetTitle(Title)
                .SetTemporaryFilesPath(webViewData)
                .RegisterWindowCreatedHandler((sender, e) => Created())
                .RegisterLocationChangedHandler((sender, e) => Changed())
                .RegisterSizeChangedHandler((sender, e) => Changed())
                .RegisterWindowClosingHandler((sender, e) => Closing())
                .Load(new Uri(url));
            // 初めては、画面いっぱい（最大化。戻すと 1920×1080）
            _placement = new WindowPlacement(_window, config, first => first.SetUseOsDefaultSize(false).SetSize(1920, 1080).SetMaximized(true));
        }

        // 窓のスレッドで、Photino が窓を作ったあと、窓のループが回る前に呼ばれる
        private void Created()
        {
            var gtkWindow = IntPtr.Zero;
            if (OperatingSystem.IsLinux())
            {
                var (window, webView) = LinuxWindow.Find();
                gtkWindow = window;
                // 下見の ArcViewer が押さずに再生できるよう、ページに音ありの自動再生を許す（WebKitAutoplay）
                if (webView == IntPtr.Zero)
                {
                    Console.Error.WriteLine("窓の Web 表示が見つからないので、譜面のプレビューは押したときだけ音が出ます");
                }
                else
                {
                    WebKitAutoplay.Allow(webView);
                }
            }
            _minSize = new WindowMinSize(_window, gtkWindow);
            _minSize.Fit();
            _open = true;
        }

        private void Changed()
        {
            _minSize?.Fit();
            if (_open)
            {
                _placement.Changed();
            }
        }

        // 窓を閉じるとき（閉じるのは止めない）
        private bool Closing()
        {
            _placement.Save();
            return false;
        }

        // 窓を閉じるまで戻らない。窓を作ったスレッドで呼ぶ（Windows は STA のスレッド）
        public void Run()
        {
            HideConsole();
            _window.WaitForClose();
            _open = false;
        }

        // ほかのスレッドから窓を閉じる（端末の Ctrl+C や OS からの終了で）。窓がまだないか、もう閉じていれば何もしない
        public void Close()
        {
            if (_open)
            {
                _window.Close();
            }
        }

        // Windows でダブルクリックすると、窓のほかにコンソールの窓も開く。そのコンソールを使っているのが Desktop だけなら隠す
        // （端末から起動したときは端末を残す。OctantCompass の窓と同じ決まり）
        private static void HideConsole()
        {
            if (!OperatingSystem.IsWindows())
            {
                return;
            }
            var processes = new uint[2];
            if (GetConsoleProcessList(processes, (uint)processes.Length) == 1)
            {
                ShowWindow(GetConsoleWindow(), 0); // SW_HIDE
            }
        }

        // 2 つ目の Desktop の起動で、開いている窓を前に出す。Photino には窓を前に出す呼び出しがないので、最小化を戻し、いちばん上にしてすぐ戻す
        public void Present()
        {
            _window.SetMinimized(false);
            _window.SetTopMost(true);
            _window.SetTopMost(false);
        }

        // OS のフォルダ選択。start（いまの値）のフォルダから開く。選ばなければ null。選び終わるまで戻らない
        public string PickFolder(string title, string start)
        {
            var chosen = _window.ShowOpenFolder(title, StartFolder(start));
            return chosen?.FirstOrDefault(path => !string.IsNullOrEmpty(path));
        }

        // いまの値のフォルダから開く。なければあるところまで上へ。空ならホーム
        private static string StartFolder(string start)
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrWhiteSpace(start))
            {
                return home;
            }
            if (start == "~" || start.StartsWith("~/", StringComparison.Ordinal))
            {
                start = home + start.Substring(1);
            }
            try
            {
                var dir = new DirectoryInfo(Path.GetFullPath(start));
                while (dir != null && !dir.Exists)
                {
                    dir = dir.Parent;
                }
                return dir?.FullName ?? home;
            }
            catch (Exception)
            {
                return home;
            }
        }

        [DllImport("libglib-2.0.so.0")]
        private static extern void g_set_prgname(string name);

        [DllImport("libglib-2.0.so.0")]
        private static extern void g_set_application_name(string name);

        [DllImport("kernel32.dll")]
        private static extern uint GetConsoleProcessList(uint[] processes, uint count);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetConsoleWindow();

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr window, int command);
    }
}
