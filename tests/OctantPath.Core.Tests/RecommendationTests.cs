using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OctantPath.Core.Connection;
using OctantPath.Core.Maps;
using OctantPath.Core.Recommendations;
using OctantPath.Core.Settings;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace OctantPath.Core.Tests
{
    public class RecommendationExampleTests
    {
        [Theory]
        [InlineData("recommendations.request.json")]
        [InlineData("recommendations.request.explicit.json")]
        [InlineData("recommendations.request.clearchance.json")]
        public void RoundTripsRequests(string file)
        {
            var example = OctantSpecsExamples.Json(file);
            var request = example.ToObject<RecommendationRequest>();
            Assert.True(JToken.DeepEquals(example, JObject.Parse(JsonConvert.SerializeObject(request))), JsonConvert.SerializeObject(request));
        }

        [Fact]
        public void ReadsTheResponseInRankOrder()
        {
            var response = JsonConvert.DeserializeObject<RecommendationResponse>(OctantSpecsExamples.Text("recommendations.response.json"));
            Assert.Equal("r-20261004-000001", response.RequestId);
            Assert.Equal(new[] { 1, 2 }, response.Candidates.Select(c => c.Rank));
            var first = response.Candidates[0];
            Assert.Equal("Standard", first.Characteristic);
            Assert.Equal("Hard", first.Difficulty);
            Assert.Equal(0.83, first.Reason.Difficulty.ClearChance);
            Assert.Equal("Expert", first.Reason.Difficulty.NearestStrong.Difficulty);
            Assert.Null(response.Candidates[1].Reason.Difficulty);
            Assert.Equal("normal", response.Aims["Standard"]);
            Assert.Single(response.Replaced);
            Assert.Empty(response.IgnoredWeights);
        }
    }

    // 推薦の要求の失敗を、MOD と Desktop が同じ決まりで分ける（文はそれぞれが持つ）
    public class RecommendationErrorTests
    {
        private readonly FakeOctantCompass _host = new FakeOctantCompass();

        [Theory]
        [InlineData(404, "seed_not_found", RecommendationProblem.SeedNotFound)]
        [InlineData(404, "seed_not_embedded", RecommendationProblem.SeedNotEmbedded)]
        [InlineData(503, "not_ready", RecommendationProblem.NotReady)]
        [InlineData(400, "unknown_preset", RecommendationProblem.UnknownPreset)]
        [InlineData(400, "invalid_request", RecommendationProblem.InvalidRequest)]
        [InlineData(500, "internal_error", RecommendationProblem.Other)]
        [InlineData(401, "unauthorized", RecommendationProblem.Other)]
        public async Task ClassifiesTheErrorCode(int status, string code, RecommendationProblem expected)
        {
            var body = new JObject { ["error"] = new JObject { ["code"] = code, ["message"] = "message from OctantCompass" } };
            _host.Other = (_, _) => ((HttpStatusCode)status, body.ToString());
            var result = await Recommend();
            Assert.Equal(expected, RecommendationErrors.Classify(result));
            Assert.Equal("message from OctantCompass", result.DetailOrStatus);
        }

        [Fact]
        public async Task ClassifiesTheExampleErrors()
        {
            _host.Other = (_, _) => (HttpStatusCode.ServiceUnavailable, OctantSpecsExamples.Text("error.json"));
            Assert.Equal(RecommendationProblem.NotReady, RecommendationErrors.Classify(await Recommend()));
            _host.Other = (_, _) => (HttpStatusCode.BadRequest, OctantSpecsExamples.Text("error.unknown_preset.json"));
            Assert.Equal(RecommendationProblem.UnknownPreset, RecommendationErrors.Classify(await Recommend()));
        }

        [Fact]
        public async Task ClassifiesAFailureWithoutACode()
        {
            _host.Other = (_, _) => (HttpStatusCode.BadGateway, "not json");
            var result = await Recommend();
            Assert.Equal(RecommendationProblem.Other, RecommendationErrors.Classify(result));
            Assert.Equal("HTTP 502", result.DetailOrStatus);

            _host.Down = true;
            Assert.Equal(RecommendationProblem.Unreachable, RecommendationErrors.Classify(await Recommend()));
        }

        [Fact]
        public async Task StopsReadingAResponseThatIsTooLarge()
        {
            _host.Other = (_, _) => (HttpStatusCode.OK, "{\"pad\":\"" + new string('x', HostClient.MaxResponseBytes) + "\"}");
            var result = await Recommend();
            Assert.Equal(HostResultKind.Unreachable, result.Kind);
            Assert.Equal("response too large", result.Detail);
        }

        private Task<HostResult<RecommendationResponse>> Recommend() =>
            new HostClient(new HttpClient(_host)).RecommendAsync(new Uri("http://127.0.0.1:23287/"), "t", new RecommendationRequest(), TimeSpan.FromSeconds(5), CancellationToken.None);
    }

    // 推薦の候補に BeatSaver の曲の情報を付ける（MOD と Desktop が同じものを使う）
    public class RecommendationLookupTests
    {
        private static readonly string A = new string('a', 40);
        private static readonly string B = new string('b', 40);
        private static readonly string C = new string('c', 40);
        private static readonly string D = new string('d', 40);

        private static RecommendationResponse Response() => new RecommendationResponse
        {
            RequestId = "r-1",
            Candidates = new List<Candidate>
            {
                new Candidate { Rank = 1, Hash = A.ToUpperInvariant(), Characteristic = "Standard", Difficulty = "Hard" },
                new Candidate { Rank = 2, Hash = "not-a-hash", Characteristic = "Standard", Difficulty = "Easy" },
                new Candidate { Rank = 3, Hash = B, Characteristic = "Standard", Difficulty = "Expert" },
                new Candidate { Rank = 4, Hash = D, Characteristic = "OneSaber", Difficulty = "Normal" },
            },
        };

        [Fact]
        public async Task KeepsEveryCandidateInOrderAndUsesOnlyTheRecommendedVersion()
        {
            // B は BeatSaver に別の版（C）しかない。D は BeatSaver にない
            var handler = new BeatSaverClientTests.Handler(_ => (HttpStatusCode.OK, new JObject
            {
                [A] = new JObject
                {
                    ["id"] = "1a",
                    ["metadata"] = new JObject { ["songName"] = "Song A", ["songAuthorName"] = "Artist A", ["levelAuthorName"] = "Mapper A", ["duration"] = 120 },
                    ["versions"] = new JArray
                    {
                        new JObject
                        {
                            ["hash"] = A,
                            ["downloadURL"] = "https://cdn.example/a.zip",
                            ["coverURL"] = "https://cdn.example/a.jpg",
                            ["previewURL"] = "https://cdn.example/a.mp3",
                            ["diffs"] = new JArray
                            {
                                new JObject { ["characteristic"] = "Standard", ["difficulty"] = "Expert", ["nps"] = 7.5, ["njs"] = 20 },
                                new JObject { ["characteristic"] = "Standard", ["difficulty"] = "Hard", ["nps"] = 5.5, ["njs"] = 18 },
                            },
                        },
                    },
                },
                [B] = new JObject { ["id"] = "1b", ["metadata"] = new JObject { ["songName"] = "Song B" }, ["versions"] = new JArray { new JObject { ["hash"] = C } } },
                [D] = JValue.CreateNull(),
            }.ToString()));
            var beatSaver = new BeatSaverClient(new HttpClient(handler), () => new OctantPathSettings(), BeatSaverClientTests.NoDelay);
            var warnings = new List<string>();

            var items = await RecommendationLookup.ItemsAsync(Response(), beatSaver, warnings.Add, CancellationToken.None);

            Assert.Empty(warnings);
            Assert.Single(handler.Paths);
            Assert.Equal(new[] { 1, 2, 3, 4 }, items.Select(i => i.Candidate.Rank));
            Assert.All(items, i => Assert.Equal("r-1", i.RequestId));
            var a = items[0];
            Assert.Equal(A, a.Hash);
            Assert.Equal($"{A}/Standard/Hard", a.Id);
            Assert.Equal("1a", a.Key);
            Assert.Equal("Song A", a.Title);
            Assert.Equal("Artist A", a.Artist);
            Assert.Equal("Mapper A", a.Mapper);
            Assert.Equal(120.0, a.Duration);
            Assert.Equal("https://cdn.example/a.jpg", a.Cover);
            Assert.Equal("https://cdn.example/a.mp3", a.Preview);
            Assert.Equal("https://cdn.example/a.zip", a.DownloadUrl);
            Assert.Equal(5.5, a.Nps);
            Assert.Equal(18.0, a.Njs);
            Assert.Null(items[1].Hash);
            Assert.Equal("not-a-hash", items[1].Title);
            Assert.NotNull(items[2].Map);
            Assert.Null(items[2].Version);
            Assert.Null(items[3].Map);
            Assert.Equal(D, items[3].Title);
            Assert.Equal("", items[3].Artist);
            Assert.Equal(new[] { A }, RecommendationDisplay.Shown(items).Select(i => i.Hash));
        }

        [Fact]
        public async Task ReturnsTheCandidatesWithoutSongInfoWhenBeatSaverFails()
        {
            var handler = new BeatSaverClientTests.Handler(_ => (HttpStatusCode.InternalServerError, ""));
            var beatSaver = new BeatSaverClient(new HttpClient(handler), () => new OctantPathSettings(), BeatSaverClientTests.NoDelay);
            var warnings = new List<string>();

            var items = await RecommendationLookup.ItemsAsync(Response(), beatSaver, warnings.Add, CancellationToken.None);

            Assert.Single(warnings);
            Assert.Equal(4, items.Count);
            Assert.All(items, i => Assert.Null(i.Map));
            Assert.Empty(RecommendationDisplay.Shown(items));
        }

        [Fact]
        public async Task ReadsTheExampleResponse()
        {
            var response = JsonConvert.DeserializeObject<RecommendationResponse>(OctantSpecsExamples.Text("recommendations.response.json"));
            var handler = new BeatSaverClientTests.Handler(_ => (HttpStatusCode.OK, "{}"));
            var beatSaver = new BeatSaverClient(new HttpClient(handler), () => new OctantPathSettings(), BeatSaverClientTests.NoDelay);
            var items = await RecommendationLookup.ItemsAsync(response, beatSaver, null, CancellationToken.None);
            Assert.Equal(response.Candidates.Select(c => c.Hash), items.Select(i => i.Hash));
            Assert.Equal(response.Candidates.Select(c => c.Characteristic), items.Select(i => i.Characteristic));
            Assert.Equal(response.Candidates.Select(c => c.Difficulty), items.Select(i => i.Difficulty));
        }
    }

    public class BeatSaverClientTests
    {
        private static readonly string A = new string('a', 40);
        private static readonly string B = new string('b', 40);

        [Fact]
        public async Task ReadsOneAndManyHashes()
        {
            var handler = new Handler(path => path.Contains(",")
                ? (HttpStatusCode.OK, new JObject { [A] = Map(A), [B] = Map(B) }.ToString())
                : (HttpStatusCode.OK, Map(A).ToString()));
            var client = new BeatSaverClient(new HttpClient(handler), () => new OctantPathSettings(), NoDelay);

            var one = await client.MapsByHashAsync(new[] { A.ToUpperInvariant() }, CancellationToken.None);
            Assert.Equal("key-" + A.Substring(0, 3), one[A].Key);
            var many = await client.MapsByHashAsync(new[] { A, B, A }, CancellationToken.None);
            Assert.Equal(2, many.Count);
            Assert.Equal(2, handler.Paths.Count);
        }

        [Fact]
        public async Task ReadsAMapByKeyAndReturnsNullWhenThereIsNone()
        {
            var handler = new Handler(path => path.EndsWith("/maps/id/1a2b3") ? (HttpStatusCode.OK, Map(A).ToString()) : (HttpStatusCode.NotFound, "{\"error\":\"Not Found\"}"));
            var client = new BeatSaverClient(new HttpClient(handler), () => new OctantPathSettings(), NoDelay);

            var map = await client.MapByKeyAsync("1a2b3", CancellationToken.None);
            Assert.Equal("key-" + A.Substring(0, 3), map.Key);
            Assert.NotNull(map.Version(A));
            Assert.Null(await client.MapByKeyAsync("fffff", CancellationToken.None));
            Assert.EndsWith("/maps/id/fffff", handler.Paths.Last());
        }

        [Fact]
        public async Task FailsToReadAMapByKeyWhenBeatSaverFails()
        {
            var handler = new Handler(_ => (HttpStatusCode.InternalServerError, ""));
            var client = new BeatSaverClient(new HttpClient(handler), () => new OctantPathSettings(), NoDelay);
            await Assert.ThrowsAsync<BeatSaverException>(() => client.MapByKeyAsync("1a2b3", CancellationToken.None));
        }

        [Fact]
        public async Task SplitsIntoFifty()
        {
            var handler = new Handler(_ => (HttpStatusCode.OK, "{}"));
            var client = new BeatSaverClient(new HttpClient(handler), () => new OctantPathSettings(), NoDelay);
            var hashes = Enumerable.Range(0, 120).Select(i => i.ToString("x40"));
            await client.MapsByHashAsync(hashes, CancellationToken.None);
            Assert.Equal(3, handler.Paths.Count);
        }

        [Fact]
        public async Task BacksOffOn429UpToTheLimit()
        {
            var delays = new List<TimeSpan>();
            var handler = new Handler(_ => ((HttpStatusCode)429, ""));
            var client = new BeatSaverClient(new HttpClient(handler), () => new OctantPathSettings(), (d, _) => { delays.Add(d); return Task.CompletedTask; });
            await Assert.ThrowsAsync<BeatSaverException>(() => client.MapsByHashAsync(new[] { A }, CancellationToken.None));
            var backoffs = delays.Where(d => d >= TimeSpan.FromSeconds(2)).Select(d => (int)Math.Round(d.TotalSeconds)).ToArray();
            Assert.Equal(new[] { 2, 4, 8, 16, 32 }, backoffs);
        }

        [Fact]
        public async Task WaitsAtLeastASecondBetweenRequestsEvenWhenTheSettingIsZero()
        {
            var delays = new List<TimeSpan>();
            var handler = new Handler(_ => (HttpStatusCode.OK, Map(A).ToString()));
            var settings = new OctantPathSettings { BeatSaverApiIntervalSeconds = 0 };
            var client = new BeatSaverClient(new HttpClient(handler), () => settings, (d, _) => { delays.Add(d); return Task.CompletedTask; });
            await client.MapsByHashAsync(new[] { A }, CancellationToken.None);
            await client.MapsByHashAsync(new[] { B }, CancellationToken.None);
            Assert.Equal(2, handler.Paths.Count);
            Assert.InRange(delays.Single(), TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(1));
        }

        [Fact]
        public async Task SetsNoUserAgentByItself()
        {
            // User-Agent は HttpClient を作る側（MOD）が付ける
            var handler = new Handler(_ => (HttpStatusCode.OK, Map(A).ToString()));
            var http = new HttpClient(handler);
            http.DefaultRequestHeaders.UserAgent.ParseAdd("OctantPath/0.0.3");
            await new BeatSaverClient(http, () => new OctantPathSettings(), NoDelay).MapsByHashAsync(new[] { A }, CancellationToken.None);
            Assert.Equal("OctantPath/0.0.3", handler.UserAgents.Single());
        }

        internal static Task NoDelay(TimeSpan delay, CancellationToken token) => Task.CompletedTask;

        private static JObject Map(string hash) => new JObject
        {
            ["id"] = "key-" + hash.Substring(0, 3),
            ["metadata"] = new JObject { ["songName"] = "Song", ["levelAuthorName"] = "Mapper" },
            ["versions"] = new JArray { new JObject { ["hash"] = hash, ["downloadURL"] = "https://cdn.example/" + hash + ".zip" } },
        };

        // 決まった応答を返す偽の BeatSaver（引数は要求のパス）
        internal sealed class Handler : HttpMessageHandler
        {
            private readonly Func<string, (HttpStatusCode, string)> _respond;
            public readonly List<string> Paths = new List<string>();
            public readonly List<string> UserAgents = new List<string>();

            public Handler(Func<string, (HttpStatusCode, string)> respond)
            {
                _respond = respond;
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Paths.Add(request.RequestUri.AbsolutePath);
                UserAgents.Add(request.Headers.UserAgent.ToString());
                var (status, body) = _respond(request.RequestUri.AbsolutePath);
                return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
            }
        }
    }
}
