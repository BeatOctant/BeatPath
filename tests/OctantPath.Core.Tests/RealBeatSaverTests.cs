using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using OctantPath.Core.Maps;
using OctantPath.Core.Settings;
using Xunit;

namespace OctantPath.Core.Tests
{
    // 本物の BeatSaver からマップを取り、検証して置けるか。OCTANTPATH_TEST_BEATSAVER_HASH に版の hash を入れると動く
    public class RealBeatSaverTests
    {
        [Fact]
        public async Task DownloadsAndVerifiesARealMap()
        {
            var hash = Environment.GetEnvironmentVariable("OCTANTPATH_TEST_BEATSAVER_HASH");
            if (string.IsNullOrEmpty(hash))
            {
                return;
            }
            var settings = new OctantPathSettings();
            var http = new HttpClient();
            http.DefaultRequestHeaders.UserAgent.ParseAdd("OctantPath/test");
            var maps = await new BeatSaverClient(http, () => settings).MapsByHashAsync(new[] { hash }, CancellationToken.None);
            var map = maps[MapHash.Normalize(hash)];
            var version = map.Version(hash);
            Assert.NotNull(version);

            var root = Path.Combine(Path.GetTempPath(), "octantpath-real-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(Path.Combine(root, "Work"));
                var result = await new MapInstaller(http, () => settings).InstallAsync(hash, new Uri(version.DownloadUrl),
                    MapInstaller.FolderName(map.Key, map.Metadata.SongName, map.Metadata.LevelAuthorName), Path.Combine(root, "Levels"), Path.Combine(root, "Work"), CancellationToken.None);
                Assert.True(result.Succeeded, result.Error);
                Console.WriteLine($"installed {map.Key} {map.Metadata.SongName} -> {result.Path}");
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }
    }
}
