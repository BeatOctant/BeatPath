using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using OctantPath.Core.Signals;

namespace OctantPath.Core.Recommendations
{
    // 推薦のまとめ（MOD の設定画面の「推薦のまとめを書き出す」）。手札ごとに、出した譜面が外され・取得され・遊ばれ・クリアされ・好意を付けられたかを数え、
    // 最近の推薦を曲ごとに並べる。招待した人からフィードバックをもらうときに、このファイル 1 つを送ってもらう（2026-10-16）。
    // 材料は signals.jsonl（表示・選択・取得・飛ばし・プレイ・お気に入り・投票）と recommendations.jsonl（要求の中身と理由）。外へは送らない。
    // 「遊ばれた」は、その推薦を出したあとに同じ譜面（hash・characteristic・difficulty）のプレイがあったもの。推薦から遊んだとは限らない。
    // 「好意」も同じで、出したあとにそのマップにお気に入りか Upvote が付いたもの（導入前の取り込みは数えない）。
    // 外した率（skipped ÷ 下見で反応のあった数）は、手札ごとの反応を見る目安（2026-10-29 の spike。反応は下見を作った要求にしか残らない）
    public static class FeedbackSummary
    {
        public const string UnknownPreset = "(unknown)";
        public const int RecentRequests = 30;

        public static string Build(IReadOnlyList<RecommendationRecord> records, IReadOnlyList<SignalEvent> events, string version, DateTime now)
        {
            records = records ?? new List<RecommendationRecord>();
            events = events ?? new List<SignalEvent>();
            var recommendations = events.Where(e => e.Type == SignalTypes.Recommendation && e.RequestId != null && e.Hash != null).ToList();
            var plays = events.Where(e => e.Type == SignalTypes.Play && e.Hash != null).ToList();
            var marks = events.Where(e => (e.Type == SignalTypes.Like || e.Type == SignalTypes.Upvote) && e.Hash != null && e.Source == null).ToList();
            var presetOf = records.Where(r => r.RequestId != null && r.Preset != null).GroupBy(r => r.RequestId).ToDictionary(g => g.Key, g => g.First().Preset);

            // 推薦した譜面 1 つ（requestId と譜面）ごとに、起きたことをまとめる
            var charts = recommendations
                .GroupBy(e => (e.RequestId, e.Hash, e.Characteristic, e.Difficulty))
                .Select(g =>
                {
                    var first = g.Min(e => e.At);
                    var played = plays.Where(p => p.Hash == g.Key.Hash && p.Characteristic == g.Key.Characteristic && p.Difficulty == g.Key.Difficulty && p.At >= first).ToList();
                    var preset = g.Select(e => e.Preset).FirstOrDefault(p => p != null) ?? (presetOf.TryGetValue(g.Key.RequestId, out var p0) ? p0 : null);
                    return new Chart
                    {
                        RequestId = g.Key.RequestId,
                        Hash = g.Key.Hash,
                        Characteristic = g.Key.Characteristic,
                        Difficulty = g.Key.Difficulty,
                        Preset = preset ?? UnknownPreset,
                        Stages = new HashSet<string>(g.Select(e => e.Stage).Where(s => s != null)),
                        Plays = played.Count,
                        Clears = played.Count(p => p.Result == PlayResults.Clear),
                        Quits = played.Count(p => p.Result == PlayResults.Quit || p.Result == PlayResults.Restart),
                        Liked = marks.Any(m => m.Hash == g.Key.Hash && m.At >= first),
                    };
                })
                .ToList();

            var text = new StringBuilder();
            text.AppendLine("# OctantPath recommendation summary");
            text.AppendLine();
            var since = recommendations.Count > 0 ? recommendations.Min(e => e.At) : (DateTime?)null;
            text.AppendLine($"OctantPath {version} · written {Time(now)} · {charts.Count} recommended charts{(since != null ? " since " + Time(since.Value) : "")} · {records.Count} recorded requests");
            text.AppendLine();
            text.AppendLine("A chart counts as played (cleared, quit) when the same chart was played after it was recommended (not necessarily from the recommendation), and as liked when the map was favourited or upvoted after it was recommended.");
            text.AppendLine("Created: requests whose draft was downloaded or played from. Skip rate: skipped among the charts reacted to (skipped, downloaded or selected).");
            text.AppendLine($"{UnknownPreset} is a recommendation from before OctantPath recorded its type.");
            text.AppendLine();
            text.AppendLine("## By type");
            text.AppendLine();
            text.AppendLine("| type | requests | created | shown | selected | downloaded | skipped | skip rate | played | cleared | quit | liked |");
            text.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|");
            foreach (var group in charts.GroupBy(c => c.Preset).OrderBy(g => g.Key == UnknownPreset).ThenBy(g => g.Key, StringComparer.Ordinal))
            {
                var requests = group.Select(c => c.RequestId).Distinct().Count();
                var created = group.Where(c => c.Stages.Contains("downloaded") || c.Stages.Contains("selected")).Select(c => c.RequestId).Distinct().Count();
                var skipped = Count(group, "skipped");
                var reacted = skipped + group.Count(c => c.Stages.Contains("downloaded") || c.Stages.Contains("selected"));
                var rate = reacted > 0 ? (100.0 * skipped / reacted).ToString("0", CultureInfo.InvariantCulture) + "%" : "-";
                text.AppendLine($"| {group.Key} | {requests} | {created} | {Count(group, "shown")} | {Count(group, "selected")} | {Count(group, "downloaded")} | {skipped} | {rate} | {group.Count(c => c.Plays > 0)} | {group.Count(c => c.Clears > 0)} | {group.Count(c => c.Quits > 0)} | {group.Count(c => c.Liked)} |");
            }
            text.AppendLine();
            text.AppendLine($"## Recent requests (newest {RecentRequests})");
            var byChart = charts.ToDictionary(c => (c.RequestId, c.Hash, c.Characteristic, c.Difficulty));
            foreach (var record in records.OrderByDescending(r => r.At).Take(RecentRequests))
            {
                text.AppendLine();
                var seed = record.Seed == null ? "no seed" : $"seed {record.Seed.Hash} {record.Seed.Characteristic} {record.Seed.Difficulty}".TrimEnd();
                var choices = record.Choices == null || record.Choices.Count == 0 ? "" : " · " + string.Join(", ", record.Choices.Select(c => c.Key + "=" + c.Value));
                text.AppendLine($"### {Time(record.At)} {record.Preset ?? UnknownPreset} · {seed}{choices}");
                if (record.Error != null)
                {
                    text.AppendLine();
                    text.AppendLine($"failed: {record.Error}");
                    continue;
                }
                text.AppendLine();
                text.AppendLine($"requestId {record.RequestId} · DB {record.DbVersion} · {record.Candidates.Count} of {record.Count}");
                text.AppendLine();
                foreach (var c in record.Candidates)
                {
                    var known = c.Title != null ? $"'{c.Title}' - {c.Artist} [{c.Mapper}]" : "(not on BeatSaver)";
                    var what = byChart.TryGetValue((record.RequestId, c.Hash, c.Characteristic, c.Difficulty), out var chart) ? What(chart) : "not shown";
                    text.AppendLine($"- #{c.Rank} {c.Key ?? "-"} {known} {c.Characteristic} {c.Difficulty} · {what}{RecommendationLog.Reason(c.Reason)}");
                }
            }
            return text.ToString();
        }

        private static int Count(IEnumerable<Chart> charts, string stage) => charts.Count(c => c.Stages.Contains(stage));

        private static string What(Chart chart)
        {
            var parts = new[] { "shown", "selected", "downloaded", "skipped" }.Where(chart.Stages.Contains).ToList();
            if (chart.Plays > 0)
            {
                parts.Add($"played {chart.Plays} (clear {chart.Clears}, quit {chart.Quits})");
            }
            if (chart.Liked)
            {
                parts.Add("liked");
            }
            return parts.Count == 0 ? "not shown" : string.Join(", ", parts);
        }

        private static string Time(DateTime at) => at.ToUniversalTime().ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);

        private sealed class Chart
        {
            public string RequestId { get; set; }
            public string Hash { get; set; }
            public string Characteristic { get; set; }
            public string Difficulty { get; set; }
            public string Preset { get; set; }
            public HashSet<string> Stages { get; set; }
            public int Plays { get; set; }
            public int Clears { get; set; }
            public int Quits { get; set; }
            public bool Liked { get; set; }
        }
    }
}
