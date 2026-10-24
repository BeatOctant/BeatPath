using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using OctantPath.Core.Settings;
using Newtonsoft.Json;

namespace OctantPath.Desktop
{
    // ArcViewer（GPLv3、https://github.com/AllPoland/ArcViewer）の公開ビルドを、初回の起動で Desktop のデータフォルダに取ってくる。
    // 同じオリジン（/arcviewer/）から出すと、下見の画面から ArcViewer の再生を一時停止できる（別のオリジンの埋め込みは外から操作できない）。
    // 取り終えるまでと取れなかったときは、画面は譜面を見せない（公開サイトには切り替えない。2026-10-16）。
    // 取ったコードは Desktop の API と同じオリジンで動くので、決めた版だけを取る: ArcViewer のリポジトリの deploy ブランチ
    // （公開サイトの元のビルド。推測: 公開サイトの index.html と同じ 0.8.1 で、og:image も公開サイトを指す）の 1 つのコミットを、
    // 中身の変わらない raw.githubusercontent.com の URL から取り、ファイルごとに大きさと SHA-256 を確かめる。
    // 合わなければ全部を捨てて失敗にする（画面は失敗を出す）。版を上げるときは Commit・PinnedVersion・Files をいっしょに書き換える
    public sealed class ArcViewerMirror
    {
        // 2026-10-15 に git ls-remote https://github.com/AllPoland/ArcViewer で見た refs/heads/deploy の先頭。index.html の productVersion は 0.8.1
        public const string Commit = "c776256497b66f7c91a74162cfcd943b0f45ee2e";
        public const string PinnedVersion = "0.8.1";
        public const string SourceUrl = "https://raw.githubusercontent.com/AllPoland/ArcViewer/" + Commit + "/";
        private const string MarkFile = ".complete.json";

        // 公開サイトの index.html が読むファイル（2026-10-11 の版 0.8.1 で確かめた。StreamingAssets は使っていない）。
        // 大きさ（バイト）と SHA-256 は、2026-10-15 に SourceUrl から取ったファイルを sha256sum で求めた
        private static readonly (string Path, long Size, string Sha256)[] Files =
        {
            ("index.html", 4442, "ecc3e170e358eab52b778685848c8ea6a1c813b6ce6bae03d67ba1a07aae8400"),
            ("TemplateData/style.css", 478, "aee49204962fe909ab71efc30250a023d668f36900adee7a259670c2136eb22d"),
            ("TemplateData/favicon.ico", 111435, "625497c0207e60f45fee94b5693306dc55b9a2b0882b87d84b773601bf57fd4f"),
            ("TemplateData/progress-bar-empty-dark.png", 96, "bbee7131afe8a3365906240d89184dc86234c119467f390bc4bc6802328fdb4d"),
            ("TemplateData/progress-bar-full-dark.png", 74, "3306a6244dcb3926fca38a28e3ced589df8ff1beed955eb17c0bbf01c918bc62"),
            ("TemplateData/Scripts/oggdecode.js", 251739, "58699f2322ae5a09cde8787a062814b1babe0616c9e4a34cd70c8c6ca9ddcb32"),
            ("Build/ArcViewer.loader.js", 26454, "2769eb0812302c6b034f7cacadf203f3d69469ab7fba7bfc1aae29953666fcbb"),
            ("Build/ArcViewer.framework.js", 488893, "388c046b76ea0d6545f304bfb8eb539079b52e991f6039c05d2f2a2d97cd8324"),
            ("Build/ArcViewer.data", 26034190, "519fa75d48f8b62c05eab2e1d040974387680b13227dc1b9834c4ab1274e591c"),
            ("Build/ArcViewer.wasm", 53532096, "e2a52b226c41db8301c17ca1bb9128cb819228afb6369fdcf18d841de1e49092"),
        };

        // 曲の再生（SongCtx の AudioBufferSourceNode）を見て、鳴っているときだけ再生/一時停止のキー（Space）を送る。
        // ArcViewer の WebSongController は曲を SongCtx（グローバル）の AudioBufferSourceNode で鳴らし、止めるときは stop を呼ぶ
        private const string Bridge = @"<script>
(function () {
  var playing = false, current = null;
  var start = AudioBufferSourceNode.prototype.start, stop = AudioBufferSourceNode.prototype.stop;
  AudioBufferSourceNode.prototype.start = function () {
    if (window.SongCtx && this.context === window.SongCtx) {
      var node = this;
      current = node;
      playing = true;
      node.addEventListener('ended', function () { if (current === node) playing = false; });
      // 譜面の再生が始まったら、下見の画面の試聴を止める
      try { if (window.parent !== window && window.parent.octantPathViewerStarted) window.parent.octantPathViewerStarted(); } catch (e) { }
    }
    return start.apply(this, arguments);
  };
  AudioBufferSourceNode.prototype.stop = function () {
    if (this === current) playing = false;
    return stop.apply(this, arguments);
  };
  function key(type) {
    var e = new KeyboardEvent(type, { key: ' ', code: 'Space', bubbles: true, cancelable: true });
    Object.defineProperty(e, 'keyCode', { get: function () { return 32; } });
    Object.defineProperty(e, 'which', { get: function () { return 32; } });
    (document.getElementById('unity-canvas') || window).dispatchEvent(e);
  }
  function toggle() { key('keydown'); setTimeout(function () { key('keyup'); }, 120); }
  window.octantPathViewer = {
    playing: function () { return playing; },
    pause: function () { if (!playing) return false; toggle(); return true; },
    resume: function () { if (playing) return false; toggle(); return true; },
  };
})();
</script>";

        private readonly string _folder;
        // 1 ファイルの待ち時間の上限は設定（arcViewerFileTimeoutSeconds）
        private readonly Func<OctantPathSettings> _settings;
        private readonly object _gate = new object();

        public ArcViewerMirror(string dataFolder, Func<OctantPathSettings> settings)
        {
            _folder = Path.Combine(dataFolder, "arcviewer");
            _settings = settings;
        }

        public string State { get; private set; } = "missing";
        public double Progress { get; private set; }
        public string Error { get; private set; }
        public string Version { get; private set; }
        public bool Ready => State == "ready";

        public Task StartAsync() => Task.Run(PrepareAsync);

        private async Task PrepareAsync()
        {
            var mark = Path.Combine(_folder, MarkFile);
            if (File.Exists(mark))
            {
                try
                {
                    // 決めた版を取り終えた印があれば使う。版を上げたあと（コミットが違う）は取り直す
                    if (JsonConvert.DeserializeAnonymousType(File.ReadAllText(mark), new { commit = "" })?.commit == Commit)
                    {
                        Version = PinnedVersion;
                        State = "ready";
                        return;
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is JsonException)
                {
                    // 印が壊れていたら取り直す
                }
            }

            State = "downloading";
            // 取っている途中のファイルは .part に置き、全部が合ってから入れ替える（/arcviewer/ が出すのは _folder だけ）
            var temp = _folder + ".part";
            try
            {
                DeleteFolder(temp);
                var fileTimeout = _settings().ArcViewerFileTimeout;
                using (var http = new HttpClient { Timeout = fileTimeout })
                {
                    http.DefaultRequestHeaders.UserAgent.ParseAdd(DesktopClient.UserAgent);
                    for (var i = 0; i < Files.Length; i++)
                    {
                        await DownloadAsync(http, Files[i], temp, fileTimeout);
                        Progress = (i + 1.0) / Files.Length;
                    }
                }
                File.WriteAllText(Path.Combine(temp, MarkFile), JsonConvert.SerializeObject(new { version = PinnedVersion, commit = Commit, source = SourceUrl, fetchedAt = DateTime.UtcNow }));
                lock (_gate)
                {
                    DeleteFolder(_folder);
                    Directory.Move(temp, _folder);
                }
                Version = PinnedVersion;
                State = "ready";
                Console.WriteLine($"ArcViewer {PinnedVersion} を {DesktopLang.ShownPath(_folder)} に取りました");
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException || ex is OperationCanceledException)
            {
                State = "failed";
                Error = ex.Message;
                Console.Error.WriteLine($"ArcViewer を取れませんでした（譜面のプレビューは出しません）: {ex.Message}");
                try
                {
                    DeleteFolder(temp);
                }
                catch (Exception cleanup) when (cleanup is IOException || cleanup is UnauthorizedAccessException)
                {
                    // 残っても .part は出さない。次の起動で消してから取り直す
                }
            }
        }

        // 1 つのファイルを取り、決めた大きさを超えたら止め、取り終えたら大きさと SHA-256 を確かめる
        private static async Task DownloadAsync(HttpClient http, (string Path, long Size, string Sha256) file, string folder, TimeSpan fileTimeout)
        {
            var path = Path.Combine(folder, file.Path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            using (var timeout = new CancellationTokenSource(fileTimeout))
            using (var response = await http.GetAsync(SourceUrl + file.Path, HttpCompletionOption.ResponseHeadersRead, timeout.Token))
            {
                response.EnsureSuccessStatusCode();
                using (var input = await response.Content.ReadAsStreamAsync(timeout.Token))
                using (var output = File.Create(path))
                using (var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
                {
                    var buffer = new byte[81920];
                    long total = 0;
                    int read;
                    while ((read = await input.ReadAsync(buffer, timeout.Token)) > 0)
                    {
                        total += read;
                        if (total > file.Size)
                        {
                            throw new InvalidDataException($"{file.Path} is larger than the pinned ArcViewer {PinnedVersion}");
                        }
                        sha.AppendData(buffer, 0, read);
                        await output.WriteAsync(buffer.AsMemory(0, read), timeout.Token);
                    }
                    if (total != file.Size || Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant() != file.Sha256)
                    {
                        throw new InvalidDataException($"{file.Path} does not match the pinned ArcViewer {PinnedVersion} (size or SHA-256)");
                    }
                }
            }
        }

        private static void DeleteFolder(string folder)
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, true);
            }
        }

        // /arcviewer/ の下のファイル。index.html には一時停止の橋渡しを足して返す
        public string FilePath(string relative)
        {
            if (!Ready)
            {
                return null;
            }
            relative = string.IsNullOrEmpty(relative) ? "index.html" : relative;
            if (!Files.Any(f => f.Path == relative))
            {
                return null;
            }
            var path = Path.Combine(_folder, relative.Replace('/', Path.DirectorySeparatorChar));
            return File.Exists(path) ? path : null;
        }

        public string Index()
        {
            var path = FilePath("index.html");
            if (path == null)
            {
                return null;
            }
            var html = File.ReadAllText(path);
            var at = html.IndexOf("<script", StringComparison.OrdinalIgnoreCase);
            return at < 0 ? html : html.Insert(at, Bridge + "\n  ");
        }
    }
}
