using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OctantPath.Core.Maps;
using OctantPath.Core.Recommendations;
using OctantPath.Core.Settings;
using Newtonsoft.Json.Linq;
using Xunit;

namespace OctantPath.Core.Tests
{
    public class MapHashTests
    {
        // ゲームの SongCore が計算した hash（UserData/SongCore/SongHashData.dat）と、手元のマップで比べる。
        // OCTANTPATH_TEST_GAME_DIR にゲームのフォルダを指すと動く
        [Fact]
        public void MatchesSongCoreForInstalledMaps()
        {
            var game = Environment.GetEnvironmentVariable("OCTANTPATH_TEST_GAME_DIR");
            if (string.IsNullOrEmpty(game))
            {
                return;
            }
            var cache = JObject.Parse(File.ReadAllText(Path.Combine(game, "UserData", "SongCore", "SongHashData.dat")));
            int compared = 0, v4 = 0;
            foreach (var property in cache.Properties())
            {
                var relative = property.Name.Replace(".\\", "").Replace('\\', Path.DirectorySeparatorChar);
                var folder = Path.Combine(game, relative);
                if (!Directory.Exists(folder))
                {
                    continue;
                }
                var expected = ((string)property.Value["songHash"]).ToLowerInvariant();
                var result = MapHash.Compute(folder);
                if (!result.Succeeded)
                {
                    // SongCore は欠けたファイルを飛ばすが、OctantPath は欠けたものを入れない（OctantPath 法 2.1）
                    continue;
                }
                Assert.True(expected == result.Hash, $"{folder}: SongCore {expected}, OctantPath {result.Hash}");
                compared++;
                if (((string)JObject.Parse(File.ReadAllText(MapHash.FindInfo(folder)))["version"])?.StartsWith("4") == true)
                {
                    v4++;
                }
            }
            Assert.True(compared > 0);
            Console.WriteLine($"compared {compared} maps ({v4} v4)");
        }

        [Fact]
        public void HashesInfoThenDifficultyFiles()
        {
            var dir = TestMap.CreateFolder(out var expected);
            try
            {
                Assert.Equal(expected, MapHash.Compute(dir).Hash);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void RefusesAMissingReferencedFile()
        {
            var dir = TestMap.CreateFolder(out _);
            try
            {
                File.Delete(Path.Combine(dir, "Expert.dat"));
                Assert.False(MapHash.Compute(dir).Succeeded);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Theory]
        [InlineData("../evil.dat", false)]
        [InlineData("/etc/passwd", false)]
        [InlineData("C:\\x.dat", false)]
        [InlineData("sub/ok.dat", true)]
        [InlineData("Expert.dat", true)]
        public void ChecksNames(string name, bool ok) => Assert.Equal(ok, MapHash.SafeName(name));

        [Fact]
        public void NormalizesHashes()
        {
            Assert.Equal(new string('a', 40), MapHash.Normalize(new string('A', 40)));
            Assert.Null(MapHash.Normalize("xyz"));
        }
    }

    // BeatSaver の取得先として受け付ける URL（zip、Desktop が中継するカバーと試聴）
    public class BeatSaverUrlsTests
    {
        [Theory]
        [InlineData("https://r2cdn.beatsaver.com/0123abcd.zip", true)]
        [InlineData("https://cdn.beatsaver.com/0123abcd.jpg", true)]
        [InlineData("https://beatsaver.com/maps/1a", true)]
        [InlineData("https://CDN.BeatSaver.com./0123abcd.mp3", true)]
        [InlineData("http://r2cdn.beatsaver.com/0123abcd.zip", false)]
        [InlineData("ftp://beatsaver.com/0123abcd.zip", false)]
        [InlineData("https://beatsaver.com.example/0123abcd.zip", false)]
        [InlineData("https://evilbeatsaver.com/0123abcd.zip", false)]
        [InlineData("https://example.com/beatsaver.com/0123abcd.zip", false)]
        [InlineData("https://127.0.0.1/0123abcd.zip", false)]
        [InlineData("/api/library/0123abcd/cover", false)]
        [InlineData("not a url", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void AcceptsOnlyHttpsOnBeatSaver(string url, bool allowed) => Assert.Equal(allowed, BeatSaverUrls.Allowed(url));
    }

    public class MapInstallerTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "octantpath-install-" + Guid.NewGuid().ToString("N"));
        private readonly OctantPathSettings _settings = new OctantPathSettings();
        private string Levels => Path.Combine(_root, "Levels");
        private string Work => Path.Combine(_root, "Work");

        public void Dispose()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, true);
            }
        }

        [Fact]
        public async Task InstallsAVerifiedMap()
        {
            var zip = TestMap.Zip(out var hash);
            var result = await Install(zip, hash.ToUpperInvariant());
            Assert.True(result.Succeeded, result.Error);
            Assert.True(File.Exists(Path.Combine(Levels, "abc (Song - Mapper)", "Info.dat")));
            Assert.Empty(Directory.GetFileSystemEntries(Work));
        }

        [Fact]
        public async Task InstallsAMapInsideOneFolder()
        {
            var zip = TestMap.Zip(out var hash, root: "inner/");
            var result = await Install(zip, hash);
            Assert.True(result.Succeeded, result.Error);
            Assert.True(File.Exists(Path.Combine(Levels, "abc (Song - Mapper)", "Expert.dat")));
        }

        [Fact]
        public async Task RefusesADifferentHash()
        {
            var zip = TestMap.Zip(out _);
            var result = await Install(zip, new string('0', 40));
            Assert.False(result.Succeeded);
            Assert.False(Directory.Exists(Path.Combine(Levels, "abc (Song - Mapper)")));
            Assert.Empty(Directory.GetFileSystemEntries(Work));
        }

        [Fact]
        public async Task RefusesZipSlip()
        {
            var zip = TestMap.Zip(out var hash, extra: "../../evil.txt");
            var result = await Install(zip, hash);
            Assert.False(result.Succeeded);
            Assert.False(File.Exists(Path.Combine(_root, "evil.txt")));
        }

        [Fact]
        public async Task RefusesTwoRoots()
        {
            var zip = TestMap.Zip(out var hash, extra: "other/Info.dat");
            Assert.False((await Install(zip, hash)).Succeeded);
        }

        [Fact]
        public async Task RefusesTooManyEntries()
        {
            _settings.ZipMaxEntries = 1;
            var zip = TestMap.Zip(out var hash);
            Assert.False((await Install(zip, hash)).Succeeded);
        }

        [Fact]
        public async Task RefusesTooLargeDownloads()
        {
            _settings.ZipMaxDownloadMegabytes = 0.0001;
            var zip = TestMap.Zip(out var hash, padding: 1024);
            Assert.False((await Install(zip, hash)).Succeeded);
        }

        [Fact]
        public async Task RefusesTooLargeExtraction()
        {
            _settings.ZipMaxExtractedMegabytes = 0.001;
            var zip = TestMap.Zip(out var hash, padding: 4096);
            Assert.False((await Install(zip, hash)).Succeeded);
        }

        [Fact]
        public async Task DoesNotOverwriteAnExistingFolder()
        {
            var existing = Path.Combine(Levels, "abc (Song - Mapper)");
            Directory.CreateDirectory(existing);
            File.WriteAllText(Path.Combine(existing, "keep.txt"), "mine");
            var zip = TestMap.Zip(out var hash);
            Assert.False((await Install(zip, hash)).Succeeded);
            Assert.Equal("mine", File.ReadAllText(Path.Combine(existing, "keep.txt")));
        }

        // Windows で使えない文字は、どの OS でもフォルダ名に入れない
        [Fact]
        public void MakesFolderNamesWithoutWindowsUnsafeCharacters()
        {
            var name = MapInstaller.FolderName("1a", "A/B\\C:D*E?F\"G<H>I|J", "Mapper.");
            Assert.Equal("1a (A_B_C_D_E_F_G_H_I_J - Mapper.)", name);
            Assert.True(MapInstaller.SafeFolderName(name));
            foreach (var c in "/\\:*?\"<>|")
            {
                Assert.False(MapInstaller.SafeFolderName("map " + c));
            }
            Assert.False(MapInstaller.SafeFolderName("map."));
            Assert.False(MapInstaller.SafeFolderName(".."));
        }

        [Fact]
        public void PicksOnlyTheRequestedVersion()
        {
            var map = new BeatSaverMap();
            map.Versions.Add(new BeatSaverVersion { Hash = new string('a', 40) });
            map.Versions.Add(new BeatSaverVersion { Hash = new string('b', 40) });
            Assert.Equal(new string('b', 40), map.Version(new string('B', 40)).Hash);
            Assert.Null(map.Version(new string('c', 40)));
        }

        [Fact]
        public void ShowsOnlyRecommendationsWhoseVersionBeatSaverHas()
        {
            // マップはあっても、推薦された hash が古い版なら出さない（MOD と Desktop の下見と結果画面で同じ）
            var map = new BeatSaverMap();
            map.Versions.Add(new BeatSaverVersion { Hash = new string('b', 40) });
            Assert.True(RecommendationDisplay.Shows(map.Version(new string('b', 40))));
            Assert.False(RecommendationDisplay.Shows(map.Version(new string('a', 40))));
            Assert.False(RecommendationDisplay.Shows(null));
        }

        // zip は https の beatsaver.com とその下からだけ取る。ほかの URL には問い合わせもしない
        [Theory]
        [InlineData("http://r2cdn.beatsaver.com/map.zip")]
        [InlineData("https://cdn.example/map.zip")]
        [InlineData("https://beatsaver.com.example/map.zip")]
        [InlineData("https://notbeatsaver.com/map.zip")]
        public async Task RefusesZipsFromOtherPlaces(string url)
        {
            var zip = TestMap.Zip(out var hash);
            var handler = new StaticHandler(zip);
            var result = await Install(zip, hash, url, handler);
            Assert.False(result.Succeeded);
            Assert.Equal(OctantPath.Core.Languages.Lang.T("install.notBeatSaver"), result.Error);
            Assert.Empty(handler.Requests);
            Assert.False(Directory.Exists(Path.Combine(Levels, "abc (Song - Mapper)")));
        }

        // BeatSaver の URL がほかのホストへ転送したら、その中身は使わない
        [Fact]
        public async Task RefusesARedirectAwayFromBeatSaver()
        {
            var zip = TestMap.Zip(out var hash);
            var handler = new StaticHandler(zip) { RedirectTo = new Uri("https://cdn.example/map.zip") };
            var result = await Install(zip, hash, "https://r2cdn.beatsaver.com/map.zip", handler);
            Assert.False(result.Succeeded);
            Assert.Equal(OctantPath.Core.Languages.Lang.T("install.notBeatSaver"), result.Error);
            Assert.False(Directory.Exists(Path.Combine(Levels, "abc (Song - Mapper)")));
            Assert.Empty(Directory.GetFileSystemEntries(Work));
        }

        // 受け取った割合を、大きさが分かるときに知らせる（結果画面の板の進み具合）
        [Fact]
        public async Task ReportsHowMuchHasBeenReceived()
        {
            var zip = TestMap.Zip(out var hash);
            var received = new List<double>();
            var result = await Install(zip, hash, received: received.Add);
            Assert.True(result.Succeeded);
            Assert.NotEmpty(received);
            Assert.Equal(1.0, received.Last());
        }

        private Task<InstallResult> Install(byte[] zip, string hash, string url = "https://r2cdn.beatsaver.com/map.zip", StaticHandler handler = null, Action<double> received = null)
        {
            var http = new HttpClient(handler ?? new StaticHandler(zip));
            var installer = new MapInstaller(http, () => _settings);
            Directory.CreateDirectory(Work);
            return installer.InstallAsync(hash, new Uri(url), MapInstaller.FolderName("abc", "Song", "Mapper"), Levels, Work, CancellationToken.None, received);
        }

        private sealed class StaticHandler : HttpMessageHandler
        {
            private readonly byte[] _body;
            public readonly List<string> Requests = new List<string>();

            // 転送を真似る（応答の RequestMessage を転送先の URL にする。HttpClientHandler も転送の後はそうする）
            public Uri RedirectTo { get; set; }

            public StaticHandler(byte[] body)
            {
                _body = body;
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Requests.Add(request.RequestUri.ToString());
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(_body) };
                if (RedirectTo != null)
                {
                    request.RequestUri = RedirectTo;
                    response.RequestMessage = request;
                }
                return Task.FromResult(response);
            }
        }
    }

    internal static class TestMap
    {
        private const string Info = "{\"_version\":\"2.1.0\",\"_songName\":\"Song\",\"_difficultyBeatmapSets\":[{\"_beatmapCharacteristicName\":\"Standard\",\"_difficultyBeatmaps\":[{\"_difficulty\":\"Expert\",\"_beatmapFilename\":\"Expert.dat\"}]}]}";
        private const string Expert = "{\"_notes\":[]}";

        public static string CreateFolder(out string hash)
        {
            var dir = Path.Combine(Path.GetTempPath(), "octantpath-map-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "Info.dat"), Info);
            File.WriteAllText(Path.Combine(dir, "Expert.dat"), Expert);
            hash = Expected();
            return dir;
        }

        public static byte[] Zip(out string hash, string root = "", string extra = null, int padding = 0)
        {
            hash = Expected();
            using (var stream = new MemoryStream())
            {
                using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
                {
                    Add(zip, root + "Info.dat", Info);
                    Add(zip, root + "Expert.dat", Expert);
                    if (extra != null)
                    {
                        Add(zip, extra, Info);
                    }
                    if (padding > 0)
                    {
                        var entry = zip.CreateEntry(root + "song.egg", CompressionLevel.NoCompression);
                        using (var s = entry.Open())
                        {
                            var bytes = new byte[padding];
                            new Random(1).NextBytes(bytes);
                            s.Write(bytes, 0, bytes.Length);
                        }
                    }
                }
                return stream.ToArray();
            }
        }

        private static void Add(ZipArchive zip, string name, string text)
        {
            using (var writer = new StreamWriter(zip.CreateEntry(name).Open()))
            {
                writer.Write(text);
            }
        }

        private static string Expected()
        {
            using (var sha1 = SHA1.Create())
            {
                var bytes = Encoding.UTF8.GetBytes(Info + Expert);
                return string.Concat(sha1.ComputeHash(bytes).Select(b => b.ToString("x2")));
            }
        }
    }
}
