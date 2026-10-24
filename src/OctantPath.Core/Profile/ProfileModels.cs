using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OctantPath.Core.Languages;

namespace OctantPath.Core.Profile
{
    // POST /v1/profile の応答（契約1（OctantCompass to OctantVector）の describe の要約）。知らない項目は無視する
    public sealed class ProfileResponse
    {
        [JsonProperty("dbVersion")] public string DbVersion { get; set; }
        [JsonProperty("likedMaps")] public int? LikedMaps { get; set; }
        [JsonProperty("plays")] public int? Plays { get; set; }
        [JsonProperty("playStyles")] public Dictionary<string, PlayStyleSummary> PlayStyles { get; set; } = new Dictionary<string, PlayStyleSummary>();
    }

    public sealed class PlayStyleSummary
    {
        [JsonProperty("strongCharts")] public int? StrongCharts { get; set; }
        [JsonProperty("features")] public Dictionary<string, double> Features { get; set; }
        [JsonProperty("clearCurve")] public bool? ClearCurve { get; set; }

        // 重い順（OctantVector 0.5.0 の describe）
        [JsonProperty("weakDirections")] public List<WeakDirection> WeakDirections { get; set; }
    }

    // 苦手な向き：特性 feature が一番近い得意な譜面より高い（high）か低い（low）とき、その差を設定の重みの weight 倍で数える
    public sealed class WeakDirection
    {
        [JsonProperty("feature")] public string Feature { get; set; }
        [JsonProperty("direction")] public string Direction { get; set; }
        [JsonProperty("weight")] public double Weight { get; set; }
    }

    public sealed class ProfileRequest
    {
        [JsonProperty("params", NullValueHandling = NullValueHandling.Ignore)] public Dictionary<string, JToken> Params { get; set; }
    }

    // 画面に出す文。値は表示にだけ使い、OctantPath の判断には使わない（憲法の原則 6）
    public static class ProfileText
    {
        public static string Describe(ProfileResponse profile)
        {
            var b = new StringBuilder();
            b.Append(Lang.T("profile.summary", new { liked = profile.LikedMaps ?? 0, plays = profile.Plays ?? 0, db = profile.DbVersion }));
            if (profile.PlayStyles == null || profile.PlayStyles.Count == 0)
            {
                b.Append("\n" + Lang.T("profile.notEnough"));
                return b.ToString();
            }
            foreach (var pair in profile.PlayStyles.OrderBy(p => p.Key))
            {
                var s = pair.Value;
                b.Append($"\n\n<b>{pair.Key}</b>　" + Lang.T("profile.strongCharts", new { count = s.StrongCharts ?? 0 }));
                if (s.Features != null && s.Features.Count > 0)
                {
                    b.Append("　" + Lang.T("profile.average") + " " + string.Join("　", s.Features.Select(f => $"{f.Key.ToUpperInvariant()} {f.Value.ToString("0.0", CultureInfo.InvariantCulture)}")));
                }
                if (s.ClearCurve != null)
                {
                    b.Append("\n" + Lang.T(s.ClearCurve.Value ? "profile.clearCurveLearned" : "profile.clearCurveNotYet"));
                }
                if (s.WeakDirections != null && s.WeakDirections.Count > 0)
                {
                    b.Append("\n" + Lang.T("profile.weakDirections") + " " + string.Join(Lang.T("common.listSeparator"), s.WeakDirections.Select(Text)));
                }
            }
            return b.ToString();
        }

        private static string Text(WeakDirection d) =>
            $"{d.Feature?.ToUpperInvariant()} {Lang.T(d.Direction == "high" ? "profile.weakHigh" : "profile.weakLow")} ×{d.Weight.ToString("0.0#", CultureInfo.InvariantCulture)}";
    }
}
