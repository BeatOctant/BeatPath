using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace OctantPath.Core.Recommendations
{
    // 推薦 1 回分の手元の記録（UserData/OctantPath/recommendations.jsonl。1 行 1 回）。OctantCompass は要求の中身を残さないので、
    // あとで「どの手札で何を出したか」をまとめられるように OctantPath が残す（2026-10-16）。外へは送らない。
    // 読めない行は飛ばす（まとめに使うだけで、正本ではない）
    public sealed class RecommendationRecord
    {
        [JsonProperty("at")] public DateTime At { get; set; }
        [JsonProperty("requestId", NullValueHandling = NullValueHandling.Ignore)] public string RequestId { get; set; }
        [JsonProperty("preset", NullValueHandling = NullValueHandling.Ignore)] public string Preset { get; set; }
        [JsonProperty("seed", NullValueHandling = NullValueHandling.Ignore)] public ChartRef Seed { get; set; }
        [JsonProperty("choices", NullValueHandling = NullValueHandling.Ignore)] public Dictionary<string, string> Choices { get; set; }
        [JsonProperty("count")] public int Count { get; set; }
        [JsonProperty("dbVersion", NullValueHandling = NullValueHandling.Ignore)] public string DbVersion { get; set; }
        [JsonProperty("error", NullValueHandling = NullValueHandling.Ignore)] public string Error { get; set; }
        [JsonProperty("candidates")] public List<RecordedCandidate> Candidates { get; set; } = new List<RecordedCandidate>();

        public static RecommendationRecord From(RecommendationRequest request, RecommendationResponse response, IEnumerable<RecommendationItem> items, string error, DateTime at) => new RecommendationRecord
        {
            At = at,
            RequestId = response?.RequestId,
            Preset = request?.Preset,
            Seed = request?.Seed,
            Choices = request?.Choices,
            Count = request?.Count ?? 0,
            DbVersion = response?.DbVersion,
            Error = error,
            Candidates = (items ?? Enumerable.Empty<RecommendationItem>()).Select(i => new RecordedCandidate
            {
                Rank = i.Candidate.Rank,
                Hash = i.Hash,
                Key = i.Key,
                Title = i.Map != null ? UI.DisplayText.Line(i.Title) : null,
                Artist = i.Map != null ? UI.DisplayText.Line(i.Artist) : null,
                Mapper = i.Map != null ? UI.DisplayText.Line(i.Mapper) : null,
                Characteristic = i.Candidate.Characteristic,
                Difficulty = i.Candidate.Difficulty,
                Reason = i.Candidate.Reason,
            }).ToList(),
        };
    }

    public sealed class RecordedCandidate
    {
        [JsonProperty("rank")] public int Rank { get; set; }
        [JsonProperty("hash")] public string Hash { get; set; }
        [JsonProperty("key", NullValueHandling = NullValueHandling.Ignore)] public string Key { get; set; }
        [JsonProperty("title", NullValueHandling = NullValueHandling.Ignore)] public string Title { get; set; }
        [JsonProperty("artist", NullValueHandling = NullValueHandling.Ignore)] public string Artist { get; set; }
        [JsonProperty("mapper", NullValueHandling = NullValueHandling.Ignore)] public string Mapper { get; set; }
        [JsonProperty("characteristic")] public string Characteristic { get; set; }
        [JsonProperty("difficulty")] public string Difficulty { get; set; }
        [JsonProperty("reason", NullValueHandling = NullValueHandling.Ignore)] public CandidateReason Reason { get; set; }
    }

    public static class RecommendationRecords
    {
        public const string FileName = "recommendations.jsonl";

        private static readonly JsonSerializerSettings Settings = new JsonSerializerSettings { Formatting = Formatting.None, DateTimeZoneHandling = DateTimeZoneHandling.Utc };

        public static void Append(string directory, RecommendationRecord record)
        {
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, FileName), JsonConvert.SerializeObject(record, Settings) + "\n");
        }

        public static List<RecommendationRecord> Read(string directory)
        {
            var path = Path.Combine(directory, FileName);
            var records = new List<RecommendationRecord>();
            if (!File.Exists(path))
            {
                return records;
            }
            foreach (var line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }
                try
                {
                    var record = JsonConvert.DeserializeObject<RecommendationRecord>(line, Settings);
                    if (record != null)
                    {
                        records.Add(record);
                    }
                }
                catch (JsonException)
                {
                    // 書きかけの行など。まとめから外す
                }
            }
            return records;
        }
    }
}
