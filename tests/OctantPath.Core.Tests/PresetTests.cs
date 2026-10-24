using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using OctantPath.Core.Connection;
using OctantPath.Core.Presets;
using OctantPath.Core.Recommendations;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace OctantPath.Core.Tests
{
    // プリセット（OctantSpecs plans/presets.md）: 一覧をボタンにする決まりと、押したときの要求
    public class PresetTests
    {
        private static PresetsResponse Example() => JsonConvert.DeserializeObject<PresetsResponse>(OctantSpecsExamples.Text("presets.response.json"));

        [Fact]
        public void ReadsTheExampleListInOrder()
        {
            var list = Example();
            Assert.False(string.IsNullOrEmpty(list.Revision));
            Assert.Equal(new[] { "all", "song", "chart", "genre", "challenge", "again" }, list.Presets.Select(p => p.Id));
            var genre = list.Presets.Single(p => p.Id == "genre");
            Assert.Equal(new[] { "jpop", "rock" }, genre.Inputs[0].Options.Select(o => o.Value));
        }

        [Fact]
        public void ShowsAllExamplePresetsBesideTheSongList()
        {
            Assert.Equal(Example().Presets.Select(p => p.Id), PresetRules.Shown(Example().Presets, true).Select(p => p.Id));
        }

        [Fact]
        public void HidesPresetsThatNeedASeedWhereThereIsNone()
        {
            Assert.Equal(new[] { "genre", "challenge", "again" }, PresetRules.Shown(Example().Presets, false).Select(p => p.Id));
        }

        [Fact]
        public void HidesUnknownInputKindsUnlessOptional()
        {
            var presets = new List<Preset>
            {
                new Preset { Id = "future", Label = "未来", Inputs = { new PresetInput { Kind = "range" } } },
                new Preset { Id = "soft", Label = "任意", Inputs = { new PresetInput { Kind = "range", Optional = true } } },
                new Preset { Id = "empty", Label = "空", Inputs = { new PresetInput { Kind = PresetInput.Choice, Name = "genre" } } },
            };
            Assert.Equal(new[] { "soft" }, PresetRules.Shown(presets, true).Select(p => p.Id));
        }

        [Fact]
        public void WritesThePresetRequestInTheExampleShape()
        {
            var example = OctantSpecsExamples.Json("recommendations.request.preset.json");
            var genre = Example().Presets.Single(p => p.Id == "genre");
            var request = PresetRules.Request(genre, new ChartRef { Hash = "1111111111111111111111111111111111111111", Characteristic = "Standard", Difficulty = "Expert" },
                new Dictionary<string, string> { ["genre"] = "jpop" }, (int)example["count"]);
            request.Exclude = example["exclude"].ToObject<List<ExcludeEntry>>();
            request.Params = example["params"].ToObject<Dictionary<string, JToken>>();
            var written = JObject.Parse(JsonConvert.SerializeObject(request));
            // 起点を使わないプリセットには起点を送らない。重み・狙い・ジャンルの語も送らない
            Assert.True(JToken.DeepEquals(example, written), written.ToString());
        }

        [Fact]
        public void SendsTheSeedOnlyForPresetsThatUseIt()
        {
            var seed = new ChartRef { Hash = "1111111111111111111111111111111111111111", Characteristic = "Standard", Difficulty = "Expert" };
            var song = JObject.Parse(JsonConvert.SerializeObject(PresetRules.Request(Example().Presets.Single(p => p.Id == "song"), seed, null, 30)));
            Assert.Equal("song", (string)song["preset"]);
            Assert.Equal(seed.Hash, (string)song["seed"]["hash"]);
            Assert.Null(song["difficulty"]);
            Assert.Null(song["weights"]);
            Assert.Null(song["choices"]);
            var again = JObject.Parse(JsonConvert.SerializeObject(PresetRules.Request(Example().Presets.Single(p => p.Id == "again"), seed, null, 30)));
            Assert.Null(again["seed"]);
        }

        [Fact]
        public void ReadsPresetsRevisionAndUnknownPreset()
        {
            var health = JsonConvert.DeserializeObject<HealthResponse>(OctantSpecsExamples.Text("health.ready.json"));
            Assert.Equal(Example().Revision, health.PresetsRevision);
            var error = JsonConvert.DeserializeObject<ErrorBody>(OctantSpecsExamples.Text("error.unknown_preset.json"));
            Assert.Equal("unknown_preset", error.Error.Code);
        }
    }

    // 手札の一覧を取り直す決まり（MOD の ConnectionService と Desktop が同じものを使う）
    public class PresetsCacheTests
    {
        private static readonly Uri Host = new Uri("http://127.0.0.1:23287/");
        private readonly FakeOctantCompass _host = new FakeOctantCompass();

        [Fact]
        public async Task ReloadsFirstAndWhenTheRevisionChanges()
        {
            var cache = new PresetsCache();
            Assert.True(cache.NeedsReload(new ConnectionStatus { State = ConnectionState.Ready }));
            Assert.False(cache.NeedsReload(new ConnectionStatus { State = ConnectionState.Loading }));

            Assert.True(cache.Update(await GetPresets(HttpStatusCode.OK, "presets.response.json")));
            var revision = cache.Presets.Revision;
            Assert.False(cache.NeedsReload(new ConnectionStatus { State = ConnectionState.Ready, PresetsRevision = revision }));
            Assert.True(cache.NeedsReload(new ConnectionStatus { State = ConnectionState.Ready, PresetsRevision = revision + "-next" }));
            // revision が来なければ比べない
            Assert.False(cache.NeedsReload(new ConnectionStatus { State = ConnectionState.Ready }));
            Assert.False(cache.NeedsReload(new ConnectionStatus { State = ConnectionState.Degraded, PresetsRevision = revision + "-next" }));
        }

        [Fact]
        public async Task KeepsTheLastListWhenAReloadFails()
        {
            var cache = new PresetsCache();
            Assert.True(cache.Update(await GetPresets(HttpStatusCode.OK, "presets.response.json")));
            Assert.Null(cache.Failure);
            var list = cache.Presets;

            Assert.False(cache.Update(await GetPresets(HttpStatusCode.ServiceUnavailable, "error.json")));
            Assert.Equal(PresetsFailure.Failed, cache.Failure);
            Assert.Equal((string)OctantSpecsExamples.Json("error.json")["error"]["message"], cache.FailureDetail);
            Assert.Same(list, cache.Presets);

            Assert.False(cache.Update(await GetPresets(HttpStatusCode.NotFound, "")));
            Assert.Equal("HTTP 404", cache.FailureDetail);

            Assert.False(cache.Update(null));
            Assert.Equal(PresetsFailure.NotConnected, cache.Failure);
            Assert.Same(list, cache.Presets);

            Assert.True(cache.Update(await GetPresets(HttpStatusCode.OK, "presets.response.json")));
            Assert.Null(cache.Failure);
            Assert.Null(cache.FailureDetail);
        }

        // 推薦の要求が失敗したあと: unknown_preset なら一覧を取り直す。接続を確かめ直すものは、確かめ直したあとの revision に任せる
        [Theory]
        [InlineData(400, "error.unknown_preset.json", true)]
        [InlineData(503, "error.json", false)]
        [InlineData(401, "error.unauthorized.json", false)]
        public async Task ReloadsAfterAnUnknownPresetOnly(int status, string example, bool reload)
        {
            _host.Other = (_, _) => ((HttpStatusCode)status, OctantSpecsExamples.Text(example));
            var result = await Client().RecommendAsync(Host, "t", new RecommendationRequest(), TimeSpan.FromSeconds(5), CancellationToken.None);
            Assert.Equal(reload, PresetsCache.NeedsReload(result));
            Assert.False(PresetsCache.NeedsReload<RecommendationResponse>(null));
        }

        private HostClient Client() => new HostClient(new HttpClient(_host));

        // example が空なら、本文のない応答
        private Task<HostResult<PresetsResponse>> GetPresets(HttpStatusCode status, string example)
        {
            _host.Other = (_, _) => (status, example.Length == 0 ? "" : OctantSpecsExamples.Text(example));
            return Client().GetPresetsAsync(Host, "t", TimeSpan.FromSeconds(5), CancellationToken.None);
        }
    }
}
