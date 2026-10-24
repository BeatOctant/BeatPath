using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using OctantPath.Core.Maps;
using OctantPath.Core.Settings;
using Xunit;

namespace OctantPath.Core.Tests
{
    public class ZipSourceTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "octantpath-zipsource-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, true);
            }
        }

        [Fact]
        public void FindsZipsByHashPrefixIgnoringCase()
        {
            var hash = new string('a', 40);
            Directory.CreateDirectory(Path.Combine(_root, "maps", "aa"));
            File.WriteAllText(Path.Combine(_root, "maps", "aa", hash + ".zip"), "");
            Assert.NotNull(ZipSources.LocalZip(Path.Combine(_root, "maps"), hash.ToUpperInvariant()));
            Assert.Null(ZipSources.LocalZip(Path.Combine(_root, "maps"), new string('b', 40)));
        }

        [Theory]
        [InlineData("BeatSaver", true)]
        [InlineData("beatsaver", true)]
        [InlineData("", true)]
        [InlineData("/data/maps", false)]
        public void TellsBeatSaverFromAFolder(string source, bool beatSaver) => Assert.Equal(beatSaver, ZipSources.IsBeatSaver(source));

        [Fact]
        public async Task InstallsFromALocalZipWithoutTouchingTheSource()
        {
            var zipBytes = TestMap.Zip(out var hash);
            var source = Path.Combine(_root, "maps", hash.Substring(0, 2), hash + ".zip");
            Directory.CreateDirectory(Path.GetDirectoryName(source));
            File.WriteAllBytes(source, zipBytes);
            var before = File.GetLastWriteTimeUtc(source);
            Directory.CreateDirectory(Path.Combine(_root, "Work"));

            var installer = new MapInstaller(new System.Net.Http.HttpClient(), () => new OctantPathSettings());
            var result = await installer.InstallFromFileAsync(hash, source, "k (Song - Mapper)", Path.Combine(_root, "Levels"), Path.Combine(_root, "Work"), CancellationToken.None);

            Assert.True(result.Succeeded, result.Error);
            Assert.True(File.Exists(source));
            Assert.Equal(before, File.GetLastWriteTimeUtc(source));
            Assert.Equal(zipBytes, File.ReadAllBytes(source));
        }

        [Fact]
        public async Task RefusesALocalZipWithAnotherHash()
        {
            var zipBytes = TestMap.Zip(out _);
            var wrong = new string('c', 40);
            var source = Path.Combine(_root, "maps", "cc", wrong + ".zip");
            Directory.CreateDirectory(Path.GetDirectoryName(source));
            File.WriteAllBytes(source, zipBytes);
            Directory.CreateDirectory(Path.Combine(_root, "Work"));
            var installer = new MapInstaller(new System.Net.Http.HttpClient(), () => new OctantPathSettings());
            var result = await installer.InstallFromFileAsync(wrong, source, "k (Song - Mapper)", Path.Combine(_root, "Levels"), Path.Combine(_root, "Work"), CancellationToken.None);
            Assert.False(result.Succeeded);
        }

        // 取れるかの決まり（MOD の取得と、取れない曲の印に使う）
        [Fact]
        public void CanFetchFromBeatSaverOnlyWithTheVersionAndFromAFolderOnlyWithTheZip()
        {
            var hash = new string('a', 40);
            var folder = Path.Combine(_root, "maps");
            Directory.CreateDirectory(Path.Combine(folder, "aa"));
            File.WriteAllText(Path.Combine(folder, "aa", hash + ".zip"), "");
            var version = new BeatSaverVersion { Hash = hash, DownloadUrl = "https://r2cdn.beatsaver.com/a.zip" };

            Assert.True(ZipSources.CanFetch("BeatSaver", hash, version));
            Assert.False(ZipSources.CanFetch("BeatSaver", hash, null));
            Assert.False(ZipSources.CanFetch("", hash, new BeatSaverVersion { Hash = hash }));
            Assert.False(ZipSources.CanFetch("BeatSaver", hash, new BeatSaverVersion { Hash = hash, DownloadUrl = "https://cdn.example/a.zip" }));
            Assert.True(ZipSources.CanFetch(folder, hash, null));
            Assert.False(ZipSources.CanFetch(folder, new string('b', 40), version));
        }

        [Fact]
        public async Task InstallsFromBeatSaverWhenTheSourceIsBeatSaver()
        {
            var zipBytes = TestMap.Zip(out var hash);
            var http = new ZipServer(zipBytes);
            var result = await InstallFromSource(http, "BeatSaver", hash, new BeatSaverVersion { Hash = hash, DownloadUrl = "https://r2cdn.beatsaver.com/map.zip" });
            Assert.True(result.Succeeded, result.Error);
            Assert.Equal(new[] { "https://r2cdn.beatsaver.com/map.zip" }, http.Requests);
        }

        [Fact]
        public async Task FailsWithoutTheVersionWhenTheSourceIsBeatSaver()
        {
            TestMap.Zip(out var hash);
            var http = new ZipServer(Array.Empty<byte>());
            var result = await InstallFromSource(http, "BeatSaver", hash, new BeatSaverVersion { Hash = hash });
            Assert.False(result.Succeeded);
            Assert.NotNull(result.Error);
            Assert.Empty(http.Requests);
        }

        // API の downloadURL が BeatSaver（https）でなければ取らない。フォルダの取得元はこの決まりと関係ない
        [Fact]
        public async Task FailsWithADownloadUrlOutsideBeatSaver()
        {
            var zipBytes = TestMap.Zip(out var hash);
            var http = new ZipServer(zipBytes);
            foreach (var url in new[] { "http://r2cdn.beatsaver.com/map.zip", "https://cdn.example/map.zip", "not a url" })
            {
                var result = await InstallFromSource(http, "BeatSaver", hash, new BeatSaverVersion { Hash = hash, DownloadUrl = url });
                Assert.False(result.Succeeded);
                Assert.Equal(OctantPath.Core.Languages.Lang.T("install.notBeatSaver"), result.Error);
            }
            Assert.Empty(http.Requests);
        }

        [Fact]
        public async Task InstallsFromTheFolderAndNeverFromBeatSaver()
        {
            var zipBytes = TestMap.Zip(out var hash);
            var folder = Path.Combine(_root, "maps");
            Directory.CreateDirectory(Path.Combine(folder, hash.Substring(0, 2)));
            File.WriteAllBytes(Path.Combine(folder, hash.Substring(0, 2), hash + ".zip"), zipBytes);
            var http = new ZipServer(zipBytes);
            var version = new BeatSaverVersion { Hash = hash, DownloadUrl = "https://r2cdn.beatsaver.com/map.zip" };

            var result = await InstallFromSource(http, folder, hash, version);
            Assert.True(result.Succeeded, result.Error);

            // フォルダにないマップは、BeatSaver に版があっても取らずに飛ばす（OctantPath 法 2.8）
            var missing = await InstallFromSource(http, Path.Combine(_root, "empty"), hash, version);
            Assert.False(missing.Succeeded);
            Assert.NotNull(missing.Error);
            Assert.Empty(http.Requests);
        }

        private Task<InstallResult> InstallFromSource(ZipServer http, string source, string hash, BeatSaverVersion version)
        {
            var settings = new OctantPathSettings { ZipSource = source };
            Directory.CreateDirectory(Path.Combine(_root, "Work"));
            var installer = new MapInstaller(new HttpClient(http), () => settings);
            return installer.InstallFromSourceAsync(hash, version, "k (Song - Mapper)", Path.Combine(_root, "Levels"), Path.Combine(_root, "Work"), CancellationToken.None);
        }

        private sealed class ZipServer : HttpMessageHandler
        {
            private readonly byte[] _zip;
            public readonly List<string> Requests = new List<string>();

            public ZipServer(byte[] zip)
            {
                _zip = zip;
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Requests.Add(request.RequestUri.ToString());
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(_zip) });
            }
        }
    }
}
