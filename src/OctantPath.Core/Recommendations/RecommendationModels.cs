using System.Collections.Generic;
using System.Linq;
using OctantPath.Core.Maps;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace OctantPath.Core.Recommendations
{
    // 推薦の 1 曲を画面（下見、結果画面の次の曲）に出すかの決まり。MOD と Desktop で同じ意味にするため、ここだけで決める
    public static class RecommendationDisplay
    {
        // BeatSaver に推薦された hash の版があるときだけ出す。削除されたマップと古い版は、試聴もカバーも取得もできないので出さない。順は変えない（2026-10-11）
        public static bool Shows(BeatSaverVersion version) => version != null;

        // 出せる曲だけ。順は変えない
        public static List<RecommendationItem> Shown(IEnumerable<RecommendationItem> items) => items.Where(i => Shows(i.Version)).ToList();
    }

    // 譜面の識別（契約3（OctantPath to OctantCompass）: hash + characteristic + difficulty）
    public sealed class ChartRef
    {
        [JsonProperty("hash")] public string Hash { get; set; }
        [JsonProperty("characteristic", NullValueHandling = NullValueHandling.Ignore)] public string Characteristic { get; set; }
        [JsonProperty("difficulty", NullValueHandling = NullValueHandling.Ignore)] public string Difficulty { get; set; }
    }

    public sealed class ExcludeEntry
    {
        [JsonProperty("hash")] public string Hash { get; set; }
        [JsonProperty("characteristic", NullValueHandling = NullValueHandling.Ignore)] public string Characteristic { get; set; }
        [JsonProperty("difficulty", NullValueHandling = NullValueHandling.Ignore)] public string Difficulty { get; set; }
        [JsonProperty("reason", NullValueHandling = NullValueHandling.Ignore)] public string Reason { get; set; }
    }

    // POST /v1/recommendations の要求
    public sealed class RecommendationRequest
    {
        // プリセットの id と、その choice の入力で選んだ値（OctantSpecs plans/presets.md）
        [JsonProperty("preset", NullValueHandling = NullValueHandling.Ignore)] public string Preset { get; set; }
        [JsonProperty("choices", NullValueHandling = NullValueHandling.Ignore)] public Dictionary<string, string> Choices { get; set; }
        [JsonProperty("seed", NullValueHandling = NullValueHandling.Ignore)] public ChartRef Seed { get; set; }
        [JsonProperty("genre", NullValueHandling = NullValueHandling.Ignore)] public string Genre { get; set; }
        [JsonProperty("weights", NullValueHandling = NullValueHandling.Ignore)] public Dictionary<string, double> Weights { get; set; }
        [JsonProperty("count")] public int Count { get; set; }
        [JsonProperty("chartsPerMap", NullValueHandling = NullValueHandling.Ignore)] public int? ChartsPerMap { get; set; }

        // "auto"、狙いの名前、クリアできる見込み（0〜1 の数）のどれか。プリセットの要求では送らない（null）
        [JsonProperty("difficulty", NullValueHandling = NullValueHandling.Ignore)] public JToken Difficulty { get; set; } = "auto";
        [JsonProperty("exclude", NullValueHandling = NullValueHandling.Ignore)] public List<ExcludeEntry> Exclude { get; set; }
        [JsonProperty("params", NullValueHandling = NullValueHandling.Ignore)] public Dictionary<string, JToken> Params { get; set; }
    }

    // POST /v1/recommendations の応答。候補の順は OctantCompass が返した順のまま使う（憲法の原則 6）
    public sealed class RecommendationResponse
    {
        [JsonProperty("requestId")] public string RequestId { get; set; }
        [JsonProperty("dbVersion")] public string DbVersion { get; set; }
        [JsonProperty("aims")] public Dictionary<string, string> Aims { get; set; }
        [JsonProperty("replaced")] public List<Replacement> Replaced { get; set; }
        [JsonProperty("ignoredWeights")] public List<string> IgnoredWeights { get; set; }
        [JsonProperty("candidates")] public List<Candidate> Candidates { get; set; } = new List<Candidate>();
    }

    public sealed class Replacement
    {
        [JsonProperty("from")] public string From { get; set; }
        [JsonProperty("to")] public string To { get; set; }
    }

    public sealed class Candidate
    {
        [JsonProperty("rank")] public int Rank { get; set; }
        [JsonProperty("hash")] public string Hash { get; set; }
        [JsonProperty("characteristic")] public string Characteristic { get; set; }
        [JsonProperty("difficulty")] public string Difficulty { get; set; }
        [JsonProperty("reason")] public CandidateReason Reason { get; set; }
    }

    public sealed class CandidateReason
    {
        [JsonProperty("views")] public Dictionary<string, double> Views { get; set; }
        [JsonProperty("difficulty")] public DifficultyReason Difficulty { get; set; }
    }

    public sealed class DifficultyReason
    {
        [JsonProperty("aim")] public string Aim { get; set; }
        [JsonProperty("distance")] public double? Distance { get; set; }
        [JsonProperty("target")] public double? Target { get; set; }
        [JsonProperty("clearChance")] public double? ClearChance { get; set; }
        [JsonProperty("nearestStrong")] public ChartRef NearestStrong { get; set; }
    }
}
