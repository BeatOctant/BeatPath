using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using OctantPath.Core.UI;

namespace OctantPath.Core.Recommendations
{
    // 推薦 1 回分を、ログで読める行にする（種類・起点・選択肢と、返った曲ごとの曲名・譜面・理由）。
    // ユーザーが見られるのはゲームの Logs の OctantPath のログだけなので、そこで推薦を振り返れるようにする（2026-10-16）。
    // ログは言語を変えないので英語で書く
    public static class RecommendationLog
    {
        public static List<string> Lines(RecommendationRequest request, RecommendationResponse response, IReadOnlyList<RecommendationItem> items, string error)
        {
            var lines = new List<string>();
            var seed = request?.Seed == null ? "none" : $"{request.Seed.Hash} {request.Seed.Characteristic} {request.Seed.Difficulty}".TrimEnd();
            var choices = request?.Choices == null || request.Choices.Count == 0 ? "" : " choices=" + string.Join(",", request.Choices.Select(c => c.Key + "=" + c.Value));
            var head = $"preset={request?.Preset ?? "none"} seed={seed}{choices} count={request?.Count}";
            if (error != null)
            {
                lines.Add($"{head} failed: {error}");
                return lines;
            }
            var aims = response?.Aims == null || response.Aims.Count == 0 ? "" : " aims=" + string.Join(",", response.Aims.Select(a => a.Key + "=" + a.Value));
            lines.Add($"{head} requestId={response?.RequestId} db={response?.DbVersion}{aims} candidates={items?.Count ?? 0}");
            foreach (var item in items ?? new List<RecommendationItem>())
            {
                lines.Add("  " + Line(item));
            }
            return lines;
        }

        public static string Line(RecommendationItem item)
        {
            var c = item.Candidate;
            var known = item.Map != null ? $"'{DisplayText.Line(item.Title)}' - {DisplayText.Line(item.Artist)} [{DisplayText.Line(item.Mapper)}]" : "(not on BeatSaver)";
            return $"#{c.Rank} {item.Key ?? "-"} {known} {c.Characteristic} {c.Difficulty}{Reason(c.Reason)}";
        }

        public static string Reason(CandidateReason reason)
        {
            if (reason == null)
            {
                return "";
            }
            var parts = new List<string>();
            if (reason.Views != null && reason.Views.Count > 0)
            {
                parts.Add("views=" + string.Join(",", reason.Views.Select(v => v.Key + ":" + F(v.Value))));
            }
            var d = reason.Difficulty;
            if (d != null)
            {
                var difficulty = new List<string>();
                if (d.Aim != null)
                {
                    difficulty.Add("aim=" + d.Aim);
                }
                if (d.Distance != null)
                {
                    difficulty.Add("distance=" + F(d.Distance.Value));
                }
                if (d.Target != null)
                {
                    difficulty.Add("target=" + F(d.Target.Value));
                }
                if (d.ClearChance != null)
                {
                    difficulty.Add("clear=" + F(d.ClearChance.Value));
                }
                if (d.NearestStrong != null)
                {
                    difficulty.Add("nearestStrong=" + d.NearestStrong.Hash);
                }
                if (difficulty.Count > 0)
                {
                    parts.Add("difficulty(" + string.Join(" ", difficulty) + ")");
                }
            }
            return parts.Count == 0 ? "" : " " + string.Join(" ", parts);
        }

        private static string F(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
    }
}
