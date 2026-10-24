using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OctantPath.Core.Connection;
using OctantPath.Core.Settings;
using OctantPath.Core.Signals;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace OctantPath.Core.Tests
{
    public class SignalExampleTests
    {
        [Fact]
        public void RoundTripsTheSignalsRequest()
        {
            var example = OctantSpecsExamples.Json("signals.request.json");
            var request = example.ToObject<SignalsRequest>();
            var written = JObject.Parse(JsonConvert.SerializeObject(request, new JsonSerializerSettings { DateFormatString = "yyyy-MM-ddTHH:mm:ssZ", DateTimeZoneHandling = DateTimeZoneHandling.Utc }));
            Assert.True(JToken.DeepEquals(example, written), written.ToString());
        }

        [Fact]
        public void ReadsTheSignalsResponse()
        {
            var response = JsonConvert.DeserializeObject<SignalsResponse>(OctantSpecsExamples.Text("signals.response.json"));
            Assert.Equal(9, response.Count);
            Assert.Empty(response.Rejected);
        }

        [Theory]
        [InlineData("Cleared", "None", "clear")]
        [InlineData("Failed", "None", "fail")]
        [InlineData("Incomplete", "Quit", "quit")]
        [InlineData("Incomplete", "Restart", "restart")]
        [InlineData("Incomplete", "None", null)]
        [InlineData("Incomplete", "LostConnection", null)]
        public void MapsGameResults(string state, string action, string expected) => Assert.Equal(expected, PlayResults.From(state, action));
    }

    public class SignalLogTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "octantpath-signals-" + Guid.NewGuid().ToString("N"));
        private string FilePath => Path.Combine(_dir, SignalLog.FileName);

        public SignalLogTests()
        {
            Directory.CreateDirectory(_dir);
        }

        public void Dispose() => Directory.Delete(_dir, true);

        [Fact]
        public void AppendsEventsAndReadsThemBackInOrder()
        {
            var log = SignalLog.Open(_dir, out _);
            log.Append(new[] { Like("a"), Like("b") });
            log.Append(new[] { Like("c") });
            var again = SignalLog.Open(_dir, out var error);
            Assert.Null(error);
            Assert.Equal(new[] { "a", "b", "c" }, again.Events.Select(e => e.Hash));
            Assert.Equal(3, again.Count);
            Assert.Equal(new[] { "c" }, again.After(2).Select(e => e.Hash));
            Assert.DoesNotContain("seq", File.ReadAllText(FilePath));
        }

        [Fact]
        public void DropsAHalfWrittenLastLine()
        {
            SignalLog.Open(_dir, out _).Append(new[] { Like("a"), Like("b") });
            File.AppendAllText(FilePath, "{\"type\":\"li");
            var log = SignalLog.Open(_dir, out var error);
            Assert.Null(error);
            Assert.Equal(2, log.Count);
            log.Append(new[] { Like("c") });
            Assert.Equal(new[] { "a", "b", "c" }, SignalLog.Open(_dir, out _).Events.Select(e => e.Hash));
        }

        [Fact]
        public void ReadsARecordWrittenWithTheOldNumbers()
        {
            // 契約3 1.x の正本には seq が付いている。知らない項目として読み飛ばす（移行はしない）
            File.WriteAllText(FilePath, "{\"seq\":1,\"type\":\"like\",\"hash\":\"a\",\"at\":\"2026-10-08T00:00:00Z\"}\n{\"seq\":2,\"type\":\"like\",\"hash\":\"b\",\"at\":\"2026-10-08T00:00:00Z\"}\n");
            var log = SignalLog.Open(_dir, out var error);
            Assert.Null(error);
            Assert.Equal(new[] { "a", "b" }, log.Events.Select(e => e.Hash));
        }

        [Fact]
        public void RefusesABrokenMiddleLine()
        {
            SignalLog.Open(_dir, out _).Append(new[] { Like("a"), Like("b") });
            var lines = File.ReadAllLines(FilePath);
            File.WriteAllText(FilePath, lines[0] + "\n{broken\n" + lines[1] + "\n");
            var before = File.ReadAllText(FilePath);
            Assert.Null(SignalLog.Open(_dir, out var error));
            Assert.NotNull(error);
            Assert.Equal(before, File.ReadAllText(FilePath));
        }

        [Fact]
        public void TracksTheLastSentFavoriteAndVote()
        {
            var log = SignalLog.Open(_dir, out _);
            log.Append(new[] { Like("a"), Like("b"), Removed("a"), Vote("c", SignalTypes.Upvote), Vote("c", SignalTypes.Downvote) });
            Assert.Equal(new[] { "b" }, log.Liked());
            Assert.Equal(SignalTypes.Downvote, log.Votes()["c"]);
        }

        private static readonly DateTime At = new DateTime(2026, 10, 8, 0, 0, 0, DateTimeKind.Utc);
        private static SignalEvent Like(string hash) => new SignalEvent { Type = SignalTypes.Like, Hash = hash, At = At };
        private static SignalEvent Removed(string hash) => new SignalEvent { Type = SignalTypes.LikeRemoved, Hash = hash, At = At };
        private static SignalEvent Vote(string hash, string type) => new SignalEvent { Type = type, Hash = hash, At = At };
    }

    public class SignalDiffTests
    {
        private static readonly string A = new string('a', 40);
        private static readonly string B = new string('b', 40);
        private static readonly DateTime At = new DateTime(2026, 10, 8, 0, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void LikesNewFavoritesAndRemovesOnlyWhatWasSent()
        {
            var events = SignalDiff.Favorites(new[] { A.ToUpperInvariant() }, new HashSet<string> { B }, At, null);
            Assert.Equal(new[] { (SignalTypes.Like, A), (SignalTypes.LikeRemoved, B) }, events.Select(e => (e.Type, e.Hash)));
        }

        [Fact]
        public void DoesNotInventRemovalsWhenImporting()
        {
            var events = SignalDiff.Favorites(new[] { A }, new HashSet<string> { B }, At, "PlayerData");
            Assert.Equal(new[] { SignalTypes.Like }, events.Select(e => e.Type));
            Assert.Equal("PlayerData", events[0].Source);
        }

        [Fact]
        public void SendsOnlyNewOrChangedVotes()
        {
            var current = new Dictionary<string, string> { [A] = "Upvote", [B] = "Downvote", ["bad"] = "Upvote" };
            var sent = new Dictionary<string, string> { [A] = SignalTypes.Upvote, [B] = SignalTypes.Upvote };
            var events = SignalDiff.Votes(current, sent, At, null);
            Assert.Equal(new[] { (SignalTypes.Downvote, B) }, events.Select(e => (e.Type, e.Hash)));
        }
    }

    public class SignalSyncTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "octantpath-sync-" + Guid.NewGuid().ToString("N"));
        private readonly List<(string Method, JObject Body)> _sent = new List<(string, JObject)>();
        // OctantCompass が持っている件数と、応答で名乗る件数（null なら持っている数のとおり）
        private int _held;
        private int? _hostCount;

        public SignalSyncTests()
        {
            Directory.CreateDirectory(_dir);
        }

        public void Dispose() => Directory.Delete(_dir, true);

        [Fact]
        public async Task SendsTheWholeRecordWhenNothingHasBeenSentToThisHost()
        {
            var log = SignalLog.Open(_dir, out _);
            log.Append(Enumerable.Range(0, 5).Select(i => new SignalEvent { Type = SignalTypes.Like, Hash = i.ToString("x40"), At = DateTime.UtcNow }));

            var result = await SignalSync.SyncAsync(log, await Connected(), null, CancellationToken.None);

            Assert.True(result.Succeeded, result.Error);
            Assert.True(result.Replaced);
            Assert.Equal(5, result.Sent);
            Assert.Equal(5, result.Added);
            var (method, body) = _sent.Single();
            Assert.Equal("PUT", method);
            Assert.Equal(5, body["events"].Count());
            Assert.All(body["events"], e => Assert.Null(e["seq"]));
        }

        [Fact]
        public async Task AddsOnlyWhatTheHostDoesNotHaveYet()
        {
            var log = SignalLog.Open(_dir, out _);
            log.Append(new[] { Event("a"), Event("b"), Event("c") });
            _held = 2;
            var result = await SignalSync.SyncAsync(log, await Connected(), 2, CancellationToken.None);
            Assert.True(result.Succeeded, result.Error);
            Assert.False(result.Replaced);
            Assert.Equal(3, result.Sent);
            Assert.Equal(1, result.Added);
            var (method, body) = _sent.Single();
            Assert.Equal("POST", method);
            Assert.Equal(new[] { new string('c', 40) }, body["events"].Select(e => (string)e["hash"]));
        }

        [Fact]
        public async Task SendsNothingWhenUpToDate()
        {
            var log = SignalLog.Open(_dir, out _);
            log.Append(new[] { Event("a") });
            var result = await SignalSync.SyncAsync(log, await Connected(), 1, CancellationToken.None);
            Assert.True(result.Succeeded);
            Assert.Equal(1, result.Sent);
            Assert.Equal(0, result.Added);
            Assert.Empty(_sent);
        }

        [Fact]
        public async Task ReplacesWithAnEmptyRecord()
        {
            var log = SignalLog.Open(_dir, out _);
            var result = await SignalSync.SyncAsync(log, await Connected(), null, CancellationToken.None);
            Assert.True(result.Succeeded, result.Error);
            Assert.Equal(0, result.Sent);
            Assert.Equal("PUT", _sent.Single().Method);
        }

        [Fact]
        public async Task FailsWhenTheHostCountsDifferently()
        {
            var log = SignalLog.Open(_dir, out _);
            log.Append(new[] { Event("a"), Event("b") });
            _hostCount = 7;
            var result = await SignalSync.SyncAsync(log, await Connected(), 1, CancellationToken.None);
            Assert.False(result.Succeeded);
            Assert.Contains("7", result.Error);
        }

        [Fact]
        public async Task ReportsWhatTheHostKeptOutOfTheHistory()
        {
            var log = SignalLog.Open(_dir, out _);
            log.Append(new[] { Event("a"), new SignalEvent { Type = "future_signal", Hash = new string('b', 40), At = DateTime.UtcNow } });
            var result = await SignalSync.SyncAsync(log, await Connected(), null, CancellationToken.None);
            Assert.True(result.Succeeded, result.Error);
            Assert.Equal(2, result.Sent);
            Assert.Equal(1, result.RejectedEvents.Single().Index);
            Assert.Equal("unsupported_type", result.RejectedEvents.Single().Reason);
        }

        private static SignalEvent Event(string hash) => new SignalEvent { Type = SignalTypes.Like, Hash = new string(hash[0], 40), At = DateTime.UtcNow };

        private async Task<HostConnection> Connected()
        {
            var host = new FakeOctantCompass
            {
                Health = _ => (HttpStatusCode.OK, "{\"apiVersion\":\"2.0\",\"status\":\"ready\",\"paired\":true,\"instanceId\":\"test\",\"signals\":0}"),
                Pair = (_, _) => (HttpStatusCode.OK, "{\"token\":\"t\"}"),
            };
            var handler = new SignalsHandler(host, this);
            var store = new MemoryStore();
            store.Info.Token = "t";
            var connection = new HostConnection(new HostClient(new HttpClient(handler)), store, () => new OctantPathSettings(), "test");
            Assert.Equal(ConnectionState.Ready, (await connection.RefreshAsync(CancellationToken.None)).State);
            return connection;
        }

        private sealed class SignalsHandler : HttpMessageHandler
        {
            private readonly FakeOctantCompass _health;
            private readonly SignalSyncTests _test;

            public SignalsHandler(FakeOctantCompass health, SignalSyncTests test)
            {
                _health = health;
                _test = test;
            }

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                if (request.RequestUri.AbsolutePath != "/v1/signals")
                {
                    var invoker = new HttpMessageInvoker(_health);
                    return await invoker.SendAsync(request, cancellationToken);
                }
                var body = JObject.Parse(await request.Content.ReadAsStringAsync(cancellationToken));
                _test._sent.Add((request.Method.Method, body));
                var events = body["events"].ToList();
                _test._held = (request.Method == HttpMethod.Put ? 0 : _test._held) + events.Count;
                var rejected = new JArray();
                for (var i = 0; i < events.Count; i++)
                {
                    if ((string)events[i]["type"] == "future_signal")
                    {
                        rejected.Add(new JObject { ["index"] = i, ["reason"] = "unsupported_type" });
                    }
                }
                var response = new JObject { ["count"] = _test._hostCount ?? _test._held, ["rejected"] = rejected };
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response.ToString(), Encoding.UTF8, "application/json") };
            }
        }
    }
}
