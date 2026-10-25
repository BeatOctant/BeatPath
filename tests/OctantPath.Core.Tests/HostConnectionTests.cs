using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using OctantPath.Core.Connection;
using OctantPath.Core.Settings;
using Newtonsoft.Json.Linq;
using Xunit;

namespace OctantPath.Core.Tests
{
    // 契約3（OctantPath to OctantCompass）の失敗時の振る舞いと、契約6（client to OctantCompass）2.0 の接続の手順（通信だけ、合言葉でペアリング）を、偽の OctantCompass で確かめる
    public class HostConnectionTests : IDisposable
    {
        private const string Secret = "482913";
        private const string Token = "bc1_6Zt0Qm3vX9pL2sK8wR4yN7aH5dF1gJ0c";

        private readonly string _dir = Path.Combine(Path.GetTempPath(), "octantpath-tests-" + Guid.NewGuid().ToString("N"));
        private readonly FakeOctantCompass _host = new FakeOctantCompass();
        private readonly MemoryStore _store = new MemoryStore();
        private readonly OctantPathSettings _settings = new OctantPathSettings();

        public HostConnectionTests()
        {
            Directory.CreateDirectory(_dir);
            _host.Health = request => FakeOctantCompass.Token(request) == Token
                ? (HttpStatusCode.OK, OctantSpecsExamples.Text("health.ready.json"))
                : (HttpStatusCode.OK, Unpaired("health.ready.json"));
            _host.Pair = (request, body) => (string)JObject.Parse(body)["secret"] == Secret
                ? (HttpStatusCode.OK, OctantSpecsExamples.Text("pair.response.json"))
                : (HttpStatusCode.Unauthorized, OctantSpecsExamples.Text("error.unauthorized.json"));
        }

        public void Dispose()
        {
            Directory.Delete(_dir, true);
        }

        [Fact]
        public async Task PairsWithACodeOnTheSamePcAndBecomesReady()
        {
            var status = await Pair(Secret);

            Assert.Equal(ConnectionState.Ready, status.State);
            Assert.True(status.IsLocal);
            Assert.Equal(Token, _store.Info.Token);
            Assert.Null(_store.Info.Host);
            Assert.Equal("4f1c2a9d8b7e6f50", status.HostInstance);
            Assert.Equal(9, status.Signals);
            var pair = JObject.Parse(_host.RequestBodies.Single(b => b != null));
            Assert.Equal(Secret, (string)pair["secret"]);
            Assert.Equal("Beat Saber (test)", (string)pair["name"]);
            Assert.Equal(Token, FakeOctantCompass.Token(_host.Requests.Last()));
            Assert.All(_host.Requests, r => Assert.Equal(new Uri($"http://127.0.0.1:{ConnectionInfo.DefaultPort}/"), new Uri(r.RequestUri, "/")));
        }

        [Fact]
        public async Task ConnectsToTheDefaultPortOnThisPcWithoutPairingByItself()
        {
            var status = await Refresh();
            Assert.Equal(ConnectionState.NeedsPairing, status.State);
            Assert.True(status.IsLocal);
            Assert.All(_host.Requests, r => Assert.Equal(ConnectionInfo.DefaultPort, r.RequestUri.Port));
            Assert.DoesNotContain(_host.Requests, r => r.RequestUri.AbsolutePath == "/v1/pair");
        }

        [Fact]
        public async Task SavesTheAddressItPairedWith()
        {
            var status = await Pair(Secret, "192.168.0.10", 50123);
            Assert.Equal(ConnectionState.Ready, status.State);
            Assert.False(status.IsLocal);
            Assert.Equal("192.168.0.10", _store.Info.Host);
            Assert.Equal(50123, _store.Info.Port);
            Assert.All(_host.Requests, r => Assert.Equal("192.168.0.10:50123", r.RequestUri.Authority));
        }

        [Fact]
        public async Task SendsTheApiVersionOnEveryRequest()
        {
            await Refresh();
            Assert.All(_host.Requests, r => Assert.Equal(ContractVersion.Api.ToString(), FakeOctantCompass.ApiVersion(r)));
        }

        [Fact]
        public async Task UsesTheSavedTokenWithoutPairingAgain()
        {
            _store.Info.Token = Token;
            var status = await Refresh();
            Assert.Equal(ConnectionState.Ready, status.State);
            Assert.DoesNotContain(_host.Requests, r => r.RequestUri.AbsolutePath == "/v1/pair");
        }

        [Fact]
        public async Task AsksForACodeWhenTheTokenWasRevoked()
        {
            _store.Info.Token = "revoked";
            var status = await Refresh();
            Assert.Equal(ConnectionState.NeedsPairing, status.State);
            Assert.DoesNotContain(_host.Requests, r => r.RequestUri.AbsolutePath == "/v1/pair");
        }

        [Fact]
        public async Task ReportsRejectedPairingAndKeepsTheConnectionInfo()
        {
            var status = await Pair("000000", "192.168.0.10", null);
            Assert.Equal(ConnectionState.PairingRejected, status.State);
            Assert.Null(_store.Info.Token);
            Assert.Null(_store.Info.Host);
            Assert.Equal(0, _store.Saves);
        }

        [Fact]
        public async Task ReportsDegradedWithTheReason()
        {
            _store.Info.Token = Token;
            _host.Health = _ => (HttpStatusCode.OK, Paired("health.degraded.json"));
            var status = await Refresh();
            Assert.Equal(ConnectionState.Degraded, status.State);
            Assert.Equal("schema_unsupported", status.Reason);
        }

        [Fact]
        public async Task ReportsLoading()
        {
            _store.Info.Token = Token;
            _host.Health = _ => (HttpStatusCode.OK, Paired("health.ready.json", status: "loading"));
            var status = await Refresh();
            Assert.Equal(ConnectionState.Loading, status.State);
        }

        [Fact]
        public async Task KeepsReadyWhenOnlyTheUpdateFails()
        {
            _store.Info.Token = Token;
            _host.Health = _ => (HttpStatusCode.OK, Paired("health.update_unsupported.json"));
            var status = await Refresh();
            Assert.Equal(ConnectionState.Ready, status.State);
            Assert.Equal("index_unsupported", status.UpdateReason);
        }

        [Fact]
        public async Task StopsOnAMajorVersionMismatchFromHealth()
        {
            _store.Info.Token = Token;
            _host.Health = _ => (HttpStatusCode.OK, Paired("health.ready.json", apiVersion: "3.0"));
            var status = await Refresh();
            Assert.Equal(ConnectionState.ApiVersionMismatch, status.State);
            Assert.Contains("Update OctantPath", StatusText.Describe(status));
        }

        [Theory]
        [InlineData(ConnectionState.Ready, StatusTone.Ready)]
        [InlineData(ConnectionState.Unknown, StatusTone.Waiting)]
        [InlineData(ConnectionState.Loading, StatusTone.Waiting)]
        [InlineData(ConnectionState.NeedsPairing, StatusTone.Waiting)]
        [InlineData(ConnectionState.Unreachable, StatusTone.Problem)]
        [InlineData(ConnectionState.PairingRejected, StatusTone.Problem)]
        [InlineData(ConnectionState.ApiVersionMismatch, StatusTone.Problem)]
        [InlineData(ConnectionState.Degraded, StatusTone.Problem)]
        public void SortsTheStateForItsMark(ConnectionState state, StatusTone tone)
        {
            Assert.Equal(tone, StatusText.Tone(new ConnectionStatus { State = state }));
        }

        [Fact]
        public void PutsWhatToCheckOnItsOwnLine()
        {
            var unreachable = new ConnectionStatus { State = ConnectionState.Unreachable };
            Assert.DoesNotContain("(", StatusText.Describe(unreachable));
            Assert.NotNull(StatusText.Hint(unreachable));
            Assert.Null(StatusText.Hint(new ConnectionStatus { State = ConnectionState.Ready }));
        }

        [Fact]
        public async Task StopsOnA426()
        {
            _host.Health = _ => (HttpStatusCode.OK, Unpaired("health.ready.json"));
            _host.Pair = (_, _) => ((HttpStatusCode)426, OctantSpecsExamples.Text("error.version_mismatch.json"));
            var status = await Pair(Secret);
            Assert.Equal(ConnectionState.ApiVersionMismatch, status.State);
            Assert.Equal("2.0", status.HostApiVersion);
        }

        [Fact]
        public async Task UsesAnotherMinorVersionAsIs()
        {
            _store.Info.Token = Token;
            _host.Health = _ => (HttpStatusCode.OK, Paired("health.ready.json", apiVersion: "2.1"));
            var status = await Refresh();
            Assert.Equal(ConnectionState.Ready, status.State);
        }

        [Fact]
        public async Task IgnoresUnknownFields()
        {
            _store.Info.Token = Token;
            _host.Health = _ =>
            {
                var json = JObject.Parse(OctantSpecsExamples.Text("health.ready.json"));
                json["somethingNew"] = new JObject { ["x"] = 1 };
                return (HttpStatusCode.OK, json.ToString());
            };
            Assert.Equal(ConnectionState.Ready, (await Refresh()).State);
        }

        [Fact]
        public async Task ReportsUnreachable()
        {
            _host.Down = true;
            var status = await Refresh();
            Assert.Equal(ConnectionState.Unreachable, status.State);
            Assert.True(status.IsLocal);
        }

        [Fact]
        public async Task AsksForPairingOnAnotherDevice()
        {
            _store.Info.Host = "192.168.0.10";
            _store.Info.Port = 50123;
            var status = await Refresh();
            Assert.Equal(ConnectionState.NeedsPairing, status.State);
            Assert.False(status.IsLocal);
            Assert.All(_host.Requests, r => Assert.Equal("192.168.0.10", r.RequestUri.Host));
            Assert.DoesNotContain(_host.Requests, r => r.RequestUri.AbsolutePath == "/v1/pair");
        }

        [Fact]
        public async Task DoesNotOverwriteUnreadableConnectionInfo()
        {
            _store.Unreadable = true;
            var status = await Refresh();
            Assert.Equal(ConnectionState.LocalInfoUnreadable, status.State);
            Assert.Equal(0, _store.Saves);
        }

        // 推薦・手札・プロファイルの要求が失敗したとき: 確かめ直すかは MOD と Desktop が ShouldRecheck で決め、
        // つなぐ先を忘れるのはトークンが通らない・版が違う・届かないときだけ
        [Theory]
        [InlineData(401, "error.unauthorized.json", true, true)]
        [InlineData(426, "error.version_mismatch.json", true, true)]
        [InlineData(503, "error.json", true, false)]
        [InlineData(400, "error.unknown_preset.json", false, false)]
        public async Task ForgetsTheHostOnlyWhenTheTokenOrTheVersionFails(int status, string example, bool recheck, bool forgets)
        {
            _store.Info.Token = Token;
            _host.Other = (_, _) => ((HttpStatusCode)status, OctantSpecsExamples.Text(example));
            var connection = NewConnection();
            Assert.Equal(ConnectionState.Ready, (await connection.RefreshAsync(CancellationToken.None)).State);

            var result = await connection.GetPresetsAsync(CancellationToken.None);

            Assert.Equal(HostResultKind.Failed, result.Kind);
            Assert.Equal(recheck, result.ShouldRecheck);
            Assert.Equal(forgets, connection.ReadyBaseUri == null);
            Assert.Equal(forgets, await connection.GetPresetsAsync(CancellationToken.None) == null);
        }

        [Fact]
        public async Task ForgetsTheHostWhenItStopsAnswering()
        {
            _store.Info.Token = Token;
            var connection = NewConnection();
            Assert.Equal(ConnectionState.Ready, (await connection.RefreshAsync(CancellationToken.None)).State);
            _host.Down = true;

            var result = await connection.RecommendAsync(new Recommendations.RecommendationRequest(), CancellationToken.None);

            Assert.Equal(HostResultKind.Unreachable, result.Kind);
            Assert.True(result.ShouldRecheck);
            Assert.Null(connection.ReadyBaseUri);
        }

        [Fact]
        public async Task SendsNothingBeforeItIsReady()
        {
            var connection = NewConnection();
            Assert.Null(await connection.GetPresetsAsync(CancellationToken.None));
            Assert.Null(await connection.RecommendAsync(new Recommendations.RecommendationRequest(), CancellationToken.None));
            Assert.Empty(_host.Requests);
        }

        [Theory]
        [InlineData("192.168.0.10", "192.168.0.10", null)]
        [InlineData(" 192.168.0.10:50123 ", "192.168.0.10", 50123)]
        [InlineData("octantcompass.local:23287", "octantcompass.local", 23287)]
        [InlineData("[fe80::1]:50123", "fe80::1", 50123)]
        [InlineData("[fe80::1]", "fe80::1", null)]
        [InlineData("fe80::1", "fe80::1", null)]
        public void ReadsTypedAddresses(string text, string host, int? port)
        {
            Assert.True(ConnectionInfo.TryParseAddress(text, out var h, out var p));
            Assert.Equal(host, h);
            Assert.Equal(port, p);
        }

        // 画面に出すつなぐ先は、読み戻せる形で
        [Theory]
        [InlineData(null, null, "")]
        [InlineData(null, 50123, "")]
        [InlineData("192.168.0.10", null, "192.168.0.10")]
        [InlineData("192.168.0.10", 50123, "192.168.0.10:50123")]
        [InlineData("fe80::1", 50123, "[fe80::1]:50123")]
        [InlineData("fe80::1", null, "[fe80::1]")]
        public void ShowsTheAddressSoItCanBeReadBack(string host, int? port, string address)
        {
            var info = new ConnectionInfo { Host = host, Port = port };
            Assert.Equal(address, info.Address);
            if (host != null)
            {
                Assert.True(ConnectionInfo.TryParseAddress(address, out var h, out var p));
                Assert.Equal(host, h);
                Assert.Equal(port, p);
            }
            Assert.Null(JObject.Parse(Newtonsoft.Json.JsonConvert.SerializeObject(info))["address"]);
        }

        [Theory]
        [InlineData("127.0.0.1", "http://127.0.0.1:23287/")]
        [InlineData("fe80::1", "http://[fe80::1]:23287/")]
        [InlineData("[fe80::1]", "http://[fe80::1]:23287/")]
        public void PutsIPv6InBracketsInTheUrl(string host, string url)
        {
            Assert.Equal(new Uri(url), HostClient.BaseUri(host, 23287));
        }

        [Theory]
        [InlineData("")]
        [InlineData("http://192.168.0.10/")]
        [InlineData("192.168.0.10:99999")]
        [InlineData("user@192.168.0.10")]
        public void RejectsOtherText(string text)
        {
            Assert.False(ConnectionInfo.TryParseAddress(text, out _, out _));
        }

        [Fact]
        public void ReadsTheConnectionInfo()
        {
            var info = Newtonsoft.Json.JsonConvert.DeserializeObject<ConnectionInfo>("{\"host\":\"192.168.0.10\",\"port\":50123,\"token\":\"t\"}");
            Assert.Equal("192.168.0.10", info.Host);
            Assert.Equal(50123, info.Port);
            var saved = JObject.Parse(Newtonsoft.Json.JsonConvert.SerializeObject(info));
            Assert.Equal("192.168.0.10", (string)saved["host"]);
            Assert.Equal(50123, (int)saved["port"]);
            Assert.True(new ConnectionInfo().IsLocal);
            Assert.False(info.IsLocal);
        }

        private HostConnection NewConnection() => new HostConnection(new HostClient(new HttpClient(_host)), _store, () => _settings, "Beat Saber (test)");

        private Task<ConnectionStatus> Refresh() => NewConnection().RefreshAsync(CancellationToken.None);

        private Task<ConnectionStatus> Pair(string code, string host = null, int? port = null) => NewConnection().PairAsync(code, host, port, CancellationToken.None);

        private static string Unpaired(string example)
        {
            var json = JObject.Parse(OctantSpecsExamples.Text(example));
            json.Remove("paired");
            json.Remove("instanceId");
            json.Remove("signals");
            return json.ToString();
        }

        private static string Paired(string example, string status = null, string apiVersion = null)
        {
            var json = JObject.Parse(OctantSpecsExamples.Text(example));
            json["paired"] = true;
            json["instanceId"] = "test";
            json["signals"] = 0;
            if (status != null)
            {
                json["status"] = status;
            }
            if (apiVersion != null)
            {
                json["apiVersion"] = apiVersion;
            }
            return json.ToString();
        }
    }

    public class SettingsTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "octantpath-tests-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, true);
            }
        }

        [Fact]
        public void UsesTheDefaultsWithoutWritingAFile()
        {
            var settings = OctantPathSettings.Load(_dir, null);
            Assert.Equal(10, settings.HostRequestTimeoutSeconds);
            Assert.False(File.Exists(Path.Combine(_dir, OctantPathSettings.FileName)));
        }

        [Fact]
        public void OverlaysTheFileOnTheDefaultsWithoutWritingBack()
        {
            Directory.CreateDirectory(_dir);
            var path = Path.Combine(_dir, OctantPathSettings.FileName);
            const string text = "{\"healthRetryIntervalSeconds\": 3, \"future\": 1}";
            File.WriteAllText(path, text);
            var settings = OctantPathSettings.Load(_dir, null);
            Assert.Equal(3, settings.HealthRetryIntervalSeconds);
            Assert.Equal(10, settings.HostRequestTimeoutSeconds);
            Assert.Equal(text, File.ReadAllText(path));
        }

        // BeatSaver API の間隔は、設定に 0 や小さい値を書いても 1 秒より短くならない
        [Theory]
        [InlineData(0, 1)]
        [InlineData(0.2, 1)]
        [InlineData(-5, 1)]
        [InlineData(2.5, 2.5)]
        public void KeepsTheBeatSaverApiIntervalAtOneSecondOrMore(double setting, double seconds)
        {
            Assert.Equal(TimeSpan.FromSeconds(seconds), new OctantPathSettings { BeatSaverApiIntervalSeconds = setting }.BeatSaverApiInterval);
        }

        // Desktop のカバーと試聴、ArcViewer の待ち時間も、0 や負の値で待たずに切れることはない
        [Fact]
        public void KeepsTheDesktopFetchLimitsUsable()
        {
            var settings = new OctantPathSettings { CdnTimeoutSeconds = 0, ArcViewerFileTimeoutSeconds = -5, CdnMaxMegabytes = 0.5 };
            Assert.Equal(TimeSpan.FromSeconds(1), settings.CdnTimeout);
            Assert.Equal(TimeSpan.FromSeconds(1), settings.ArcViewerFileTimeout);
            Assert.Equal(512L * 1024, settings.CdnMaxBytes);
        }

        [Fact]
        public void DefaultsFileHasEveryKey()
        {
            var keys = JObject.FromObject(new OctantPathSettings()).Properties().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal);
            var defaults = OctantPathSettings.DefaultValues().Properties().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal);
            Assert.Equal(keys, defaults);
        }

        [Fact]
        public void SavesOnlyTheChangedKeyAndDropsItAtTheDefault()
        {
            var path = Path.Combine(_dir, OctantPathSettings.FileName);
            OctantPathSettings.SaveValue(_dir, "autoDownload", false);
            OctantPathSettings.SaveValue(_dir, "downloadStartsPerSecond", 3.0);
            Assert.Equal(new[] { "autoDownload", "downloadStartsPerSecond" }, JObject.Parse(File.ReadAllText(path)).Properties().Select(p => p.Name));
            Assert.False(OctantPathSettings.Load(_dir, null).AutoDownload);

            // 既定値（defaults.json は 2 と書く）に戻すと、その項目は消える
            OctantPathSettings.SaveValue(_dir, "downloadStartsPerSecond", 2.0);
            OctantPathSettings.SaveValue(_dir, "autoDownload", true);
            Assert.Empty(JObject.Parse(File.ReadAllText(path)).Properties());
        }

        [Fact]
        public void DoesNotOverwriteABrokenFile()
        {
            Directory.CreateDirectory(_dir);
            var path = Path.Combine(_dir, OctantPathSettings.FileName);
            File.WriteAllText(path, "{ broken");
            string warning = null;
            var settings = OctantPathSettings.Load(_dir, w => warning = w);
            Assert.Equal(10, settings.HostRequestTimeoutSeconds);
            Assert.Equal("{ broken", File.ReadAllText(path));
            Assert.NotNull(warning);
        }
    }

    // 1 つの項目だけを書き換える（MOD と Desktop の設定、Desktop の desktop.json が使う）
    public class JsonFileTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "octantpath-tests-" + Guid.NewGuid().ToString("N"));
        private string FilePath => Path.Combine(_dir, "sub", "file.json");

        public void Dispose()
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, true);
            }
        }

        [Fact]
        public void SetsOneKeyAndKeepsTheOthers()
        {
            JsonFile.SetValue(FilePath, "a", 1);
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
            File.WriteAllText(FilePath, "{\"a\": 1, \"unknown\": {\"x\": true}}");
            JsonFile.SetValue(FilePath, "b", "text");
            var json = JObject.Parse(File.ReadAllText(FilePath));
            Assert.Equal(new[] { "a", "unknown", "b" }, json.Properties().Select(p => p.Name));
            Assert.True((bool)json["unknown"]["x"]);
            Assert.Equal("text", (string)json["b"]);
            Assert.False(File.Exists(FilePath + ".tmp"));
        }

        [Fact]
        public void RemovesAKeyWithNullAndWritesNothingWhenItWasNotThere()
        {
            JsonFile.SetValue(FilePath, "a", null);
            Assert.False(File.Exists(FilePath));
            JsonFile.SetValue(FilePath, "a", 1);
            JsonFile.SetValue(FilePath, "b", 2);
            JsonFile.SetValue(FilePath, "a", null);
            Assert.Equal(new[] { "b" }, JObject.Parse(File.ReadAllText(FilePath)).Properties().Select(p => p.Name));
        }

        [Fact]
        public void DoesNotOverwriteAFileItCannotRead()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
            File.WriteAllText(FilePath, "{ broken");
            Assert.ThrowsAny<Newtonsoft.Json.JsonException>(() => JsonFile.SetValue(FilePath, "a", 1));
            Assert.Equal("{ broken", File.ReadAllText(FilePath));
        }
    }
}
