using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace OctantPath.Desktop
{
    // HMD なしで OctantPath の板（手札のカード）・種類の小窓・下見・マップの取得とプレビューを使う。
    // OctantCompass とは OctantPath.Core の契約3（OctantPath to OctantCompass）・契約6（client to OctantCompass）の実装でそのまま話す。画面は手元のサーバーが出すページで、Desktop の窓（DesktopWindow）に出す
    public static class Program
    {
        // Windows の窓（WebView2）は STA のスレッドで動かす
        [STAThread]
        public static void Main(string[] args)
        {
            var options = DesktopOptions.Parse(args);
            if (options == null)
            {
                Console.Error.WriteLine(DesktopOptions.Usage);
                Environment.Exit(2);
            }

            if (PortInUse(options.Port))
            {
                // 前に開いた Desktop が動いていれば、その窓を前に出すだけにする（2026-10-12: address already in use で起動できなかった）
                var running = $"http://127.0.0.1:{options.Port}";
                var shown = ShowRunning(running);
                if (shown != null)
                {
                    Console.WriteLine(shown == true
                        ? $"OctantPath.Desktop はもう動いています: {running}（その窓を前に出しました）"
                        : $"OctantPath.Desktop はもう窓なしで動いています: {running}（ブラウザで開いてください）");
                    return;
                }
                Console.Error.WriteLine($"127.0.0.1:{options.Port} はほかのプログラムが使っています。--port でほかの番号を渡してください");
                Environment.Exit(1);
            }

            var client = new DesktopClient(options);
            // 窓なし（--no-window）か、窓がまだないあいだは null。OS の選択の窓は、この窓から開く
            DesktopWindow window = null;
            var builder = WebApplication.CreateBuilder();
            builder.Logging.SetMinimumLevel(LogLevel.Warning);
            builder.WebHost.UseUrls($"http://127.0.0.1:{options.Port}");
            var app = builder.Build();

            // ほかのサイトからの要求を断る（Host・Origin・Content-Type。LocalGuard）
            app.Use(async (context, next) =>
            {
                var refused = LocalGuard.Refuse(context.Request, options.Port);
                if (refused != null)
                {
                    context.Response.StatusCode = refused == "content_type" ? StatusCodes.Status415UnsupportedMediaType : StatusCodes.Status403Forbidden;
                    context.Response.ContentType = "application/json; charset=utf-8";
                    await context.Response.WriteAsync(JsonConvert.SerializeObject(new { error = refused }));
                    return;
                }
                await next();
            });

            app.MapGet("/", () => Results.Content(Page(client.Theme()), "text/html; charset=utf-8"));
            app.MapGet("/api/status", async (CancellationToken ct) => Json(await client.StatusAsync(ct)));
            app.MapPost("/api/recheck", async (CancellationToken ct) => Json(await client.RecheckAsync(ct)));
            app.MapGet("/api/pair", () => Json(new { address = client.Address() }));
            app.MapPost("/api/pair", async (HttpRequest request, CancellationToken ct) => Json(await client.PairAsync(await ReadAsync(request), ct)));
            // 2 つ目の Desktop の起動から（ShowRunning）
            app.MapPost("/api/window/show", () =>
            {
                window?.Present();
                return Json(new { shown = window != null });
            });
            app.MapPost("/api/seed", async (HttpRequest request, CancellationToken ct) => Json(await client.SetSeedAsync(await ReadAsync(request), ct)));
            app.MapGet("/api/draft", () => Json(client.DraftView()));
            app.MapPost("/api/draft", async (HttpRequest request, CancellationToken ct) => Json(await client.BuildDraftAsync(await ReadAsync(request), ct)));
            app.MapPost("/api/draft/replace", async (HttpRequest request) => Json(client.Replace((string)(await ReadAsync(request))["id"])));
            app.MapPost("/api/draft/receive", async (HttpRequest request) => Json(await client.ReceiveOneAsync((string)(await ReadAsync(request))["id"])));
            app.MapPost("/api/draft/create", () => Json(client.Create()));
            app.MapPost("/api/draft/close", () => Json(client.CloseDraft()));
            app.MapGet("/api/maps/{hash}/info", (string hash) => Json(client.ReceivedInfo(hash)));
            app.MapGet("/api/maps/{hash}.zip", (string hash) =>
            {
                var zip = client.ReceivedZip(hash);
                return zip == null ? Results.NotFound() : Results.File(zip, "application/zip", hash + ".zip");
            });
            // 作ったプレイリストを、OS のフォルダ選択で選んだフォルダに保存する
            app.MapPost("/api/playlists/save", async (HttpRequest request) =>
            {
                var name = (string)(await ReadAsync(request))["name"];
                return Json(window == null
                    ? client.PlaylistWithoutWindow(name)
                    : client.SavePlaylist(name, start => window.PickFolder(DesktopLang.T("top.saveFolder"), start)));
            });
            app.MapGet("/api/library", (string source, string q, int? offset, int? limit) => Json(client.Library(source, q, offset ?? 0, limit ?? 200)));
            app.MapGet("/api/library/{hash}/{kind}", (string hash, string kind) =>
            {
                var path = kind == "cover" || kind == "song" ? client.LibraryFile(hash, kind) : null;
                return path == null ? Results.NotFound() : Results.File(path, ContentType(path));
            });
            // 手元に取った ArcViewer（同じオリジンにして、下見の画面から一時停止できるようにする）
            app.MapGet("/arcviewer/{**path}", (string path) =>
            {
                if (string.IsNullOrEmpty(path) || path == "index.html")
                {
                    var index = client.ArcViewer.Index();
                    return index == null ? Results.NotFound() : Results.Content(index, "text/html; charset=utf-8");
                }
                var file = client.ArcViewer.FilePath(path);
                return file == null ? Results.NotFound() : Results.File(file, ContentType(file));
            });
            // 画面のカバーと試聴（BeatSaver の CDN）。Desktop が OctantPath の User-Agent を付けて取る
            app.MapGet("/api/cdn", (string url, HttpResponse response, CancellationToken ct) => client.Cdn.GetAsync(url, response, ct));
            app.MapGet("/api/settings", () => Json(client.SettingsView()));
            app.MapPost("/api/ceiling", async (HttpRequest request) => Json(client.SetCeiling((bool?)(await ReadAsync(request))["on"] ?? false)));
            app.MapPost("/api/settings", async (HttpRequest request) => Json(client.SaveSetting(await ReadAsync(request))));
            // 設定のフォルダを打たずに選ぶ。OS の選択の窓を開き、選んだパスを返す（保存は画面の「保存」で）
            app.MapPost("/api/pick-folder", async (HttpRequest request) =>
            {
                var start = (string)(await ReadAsync(request))["start"];
                if (window == null)
                {
                    return Json(new { error = DesktopLang.T("settings.pickNoWindow") });
                }
                var path = window.PickFolder(DesktopLang.T("settings.pickTitle"), start);
                return Json(path == null ? (object)new { cancelled = true } : new { path });
            });
            app.MapGet("/api/lang", () => Json(client.LangView()));
            app.MapPost("/api/lang", async (HttpRequest request, CancellationToken ct) => Json(await client.SetLanguageAsync((string)(await ReadAsync(request))["language"], ct)));
            app.MapPost("/api/theme", async (HttpRequest request) => Json(client.SetTheme((string)(await ReadAsync(request))["theme"])));
            app.MapGet("/api/debug", () => Json(client.Debug()));

            app.Start();
            var url = app.Urls.FirstOrDefault() ?? $"http://127.0.0.1:{options.Port}";
            Console.WriteLine($"OctantPath.Desktop: {url}");
            Console.WriteLine($"UserData: {DesktopLang.ShownPath(client.UserData)}{(options.ReadOnly ? "（読むだけ）" : "")}");
            Console.WriteLine($"Desktop のデータ: {DesktopLang.ShownPath(client.Folders.Root)}");
            // 画面を待たずに接続を確かめる（OctantCompass は起動しない。契約6（client to OctantCompass）2.0）
            _ = client.StatusAsync(CancellationToken.None);
            if (options.NoWindow)
            {
                // ページはブラウザで開く。Ctrl+C で終わる
                app.WaitForShutdown();
                return;
            }
            // 窓を閉じたら Desktop も終わる
            window = new DesktopWindow(url, Path.Combine(client.Folders.Cache, "webview"), client.Folders.Config);
            // 端末の Ctrl+C や OS からの終了（SIGTERM）ではサーバーが止まるので、窓も閉じる（止まったページの窓を残さない）
            using var interrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT, _ => window.Close());
            using var terminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, _ => window.Close());
            window.Run();
            app.StopAsync().GetAwaiter().GetResult();
        }

        private static bool PortInUse(int port)
        {
            try
            {
                var listener = new TcpListener(IPAddress.Loopback, port);
                listener.Start();
                listener.Stop();
                return false;
            }
            catch (SocketException)
            {
                return true;
            }
        }

        // その番号で答えているのが Desktop なら、その窓を前に出させる。前に出したら true、窓なしで動いていれば false、Desktop でなければ null
        private static bool? ShowRunning(string url)
        {
            try
            {
                using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) })
                using (var body = new StringContent("{}", Encoding.UTF8, "application/json"))
                using (var response = http.PostAsync(url + "/api/window/show", body).GetAwaiter().GetResult())
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        return null;
                    }
                    return (bool?)JObject.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult())["shown"];
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static async Task<JObject> ReadAsync(HttpRequest request)
        {
            using (var reader = new StreamReader(request.Body))
            {
                var text = await reader.ReadToEndAsync();
                return string.IsNullOrWhiteSpace(text) ? new JObject() : JObject.Parse(text);
            }
        }

        private static IResult Json(object value)
        {
            return Results.Content(JsonConvert.SerializeObject(value, Formatting.None), "application/json; charset=utf-8");
        }

        internal static string ContentType(string path)
        {
            switch (Path.GetExtension(path).ToLowerInvariant())
            {
                case ".png": return "image/png";
                case ".jpg":
                case ".jpeg": return "image/jpeg";
                case ".egg":
                case ".ogg": return "audio/ogg";
                case ".wav": return "audio/wav";
                case ".mp3": return "audio/mpeg";
                case ".wasm": return "application/wasm";
                case ".js": return "text/javascript";
                case ".css": return "text/css";
                case ".ico": return "image/x-icon";
                default: return "application/octet-stream";
            }
        }

        // 画面。選んであるテーマ（自動は、ページが描く前に OS のものに決める）と、崩れずに出る最小の大きさ（WindowMinSize）を入れて返す
        private static string Page(string theme)
        {
            const string root = "<html lang=\"en\">";
            using (var stream = typeof(Program).Assembly.GetManifestResourceStream("index.html"))
            using (var reader = new StreamReader(stream))
            {
                var page = reader.ReadToEnd();
                // 入れる所がなければ、テーマも最小の大きさもないページを黙って出さない
                if (!page.Contains(root))
                {
                    throw new InvalidOperationException($"wwwroot/index.html に {root} がない");
                }
                return page.Replace(root,
                    $"<html lang=\"en\" data-theme-setting=\"{theme}\" style=\"--page-min-width: {WindowMinSize.PageMinWidth}px; --page-min-height: {WindowMinSize.PageMinHeight}px\">");
            }
        }
    }

    public sealed class DesktopOptions
    {
        public const string Usage = "使い方: dotnet run --project src/OctantPath.Desktop -- [--userdata <UserData/OctantPath のフォルダ>] [--game <Beat Saber のフォルダ>] [--data <Desktop のデータを置くフォルダ>] [--port <番号>] [--no-window]";

        // 設定とトークンを読むフォルダ。省くと Desktop 専用のフォルダ（別の環境としてペアリングする）
        public string UserData { get; set; }

        // 導入済みを知るために曲フォルダを読むゲームのフォルダ（読むだけ）。省くと --userdata から求める
        public string Game { get; set; }

        // Desktop のデータ（設定とトークン・取得したマップ Levels・作業用 Work・プレイリスト Playlists・所有マップの覚え書き）を置くフォルダ。
        // 渡すと全部をそのフォルダ 1 つに置く。省くと、このリポジトリの .desktop（中で動かしているとき）、実行ファイルの隣の OctantPath.Desktop-data（あれば）、Linux は XDG の決まりに分けた場所、ほかは OS の決まった場所の OctantPath/Desktop の順（DesktopClient.DefaultFolders）
        public string Data { get; set; }
        public int Port { get; set; } = 5791;

        // 窓を出さずに手元のサーバーだけ動かす（ページはブラウザで開く。フォルダを選ぶ窓と、プレイリストの保存先の選択は使えない）
        public bool NoWindow { get; set; }

        // --userdata で渡したフォルダには書かない（ゲームの OctantPath のファイルを守るため）
        public bool ReadOnly => UserData != null;

        public static DesktopOptions Parse(string[] args)
        {
            var options = new DesktopOptions();
            for (var i = 0; i < args.Length; i++)
            {
                var value = i + 1 < args.Length ? args[i + 1] : null;
                switch (args[i])
                {
                    case "--userdata" when value != null:
                        options.UserData = value;
                        i++;
                        break;
                    case "--game" when value != null:
                        options.Game = value;
                        i++;
                        break;
                    case "--data" when value != null:
                        options.Data = value;
                        i++;
                        break;
                    case "--port" when value != null && int.TryParse(value, out var port):
                        options.Port = port;
                        i++;
                        break;
                    case "--no-window":
                        options.NoWindow = true;
                        break;
                    default:
                        return null;
                }
            }
            return options;
        }
    }
}
