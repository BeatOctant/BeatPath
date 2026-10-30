using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace OctantPath.Core.Signals
{
    // 契約3（OctantPath to OctantCompass）の /v1/signals の event。この環境の正本として、送ったものも含めて残す（憲法の原則 5・7）
    public sealed class SignalEvent
    {
        [JsonProperty("type")] public string Type { get; set; }
        [JsonProperty("source", NullValueHandling = NullValueHandling.Ignore)] public string Source { get; set; }
        [JsonProperty("requestId", NullValueHandling = NullValueHandling.Ignore)] public string RequestId { get; set; }
        // recommendation の、元の要求で使ったプリセットの id（任意。契約3 の 1.2 で足された）。OctantCompass のキャッシュを失っても、どの手札の推薦か分かるように入れる（2026-10-16）
        [JsonProperty("preset", NullValueHandling = NullValueHandling.Ignore)] public string Preset { get; set; }
        // recommendation の、その譜面を出した手札の id（任意）。「おまかせ」のように複数の手札を混ぜた推薦では preset と違う。候補の reason.hand をそのまま入れる。OctantVector が手札ごとの反応を数えるのに使う（2026-10-29）
        [JsonProperty("hand", NullValueHandling = NullValueHandling.Ignore)] public string Hand { get; set; }
        [JsonProperty("hash")] public string Hash { get; set; }
        [JsonProperty("characteristic", NullValueHandling = NullValueHandling.Ignore)] public string Characteristic { get; set; }
        [JsonProperty("difficulty", NullValueHandling = NullValueHandling.Ignore)] public string Difficulty { get; set; }
        [JsonProperty("result", NullValueHandling = NullValueHandling.Ignore)] public string Result { get; set; }
        [JsonProperty("modifiers", NullValueHandling = NullValueHandling.Ignore)] public Dictionary<string, JToken> Modifiers { get; set; }
        [JsonProperty("accuracy", NullValueHandling = NullValueHandling.Ignore)] public double? Accuracy { get; set; }
        [JsonProperty("stage", NullValueHandling = NullValueHandling.Ignore)] public string Stage { get; set; }
        [JsonProperty("count", NullValueHandling = NullValueHandling.Ignore)] public int? Count { get; set; }
        // imported_play の、PlayerData にその譜面のスコアがあること（修飾ありも含めて一度はクリアした）。OctantVector が実力の初めの手がかりにする（2026-10-25）
        [JsonProperty("cleared", NullValueHandling = NullValueHandling.Ignore)] public bool? Cleared { get; set; }
        [JsonProperty("at")] public DateTime At { get; set; }
    }

    public static class SignalTypes
    {
        public const string Upvote = "upvote";
        public const string Downvote = "downvote";
        public const string Like = "like";
        public const string LikeRemoved = "like_removed";
        public const string Play = "play";
        public const string UserDelete = "user_delete";
        public const string Recommendation = "recommendation";
        public const string ImportedPlay = "imported_play";
    }

    public static class PlayResults
    {
        public const string Clear = "clear";
        public const string Fail = "fail";
        public const string Quit = "quit";
        public const string Restart = "restart";

        // ゲームの終わり方を契約3 の結果にする。読み替えず、当てはまらないものは null（記録しない。OctantPath 法 4.1）
        public static string From(string endState, string endAction)
        {
            switch (endAction)
            {
                case "Restart":
                    return Restart;
                case "Quit":
                    return endState == "Incomplete" ? Quit : null;
                case "None":
                    return endState == "Cleared" ? Clear : endState == "Failed" ? Fail : null;
                default:
                    return null;
            }
        }
    }

    public sealed class SignalsRequest
    {
        [JsonProperty("events")] public List<SignalEvent> Events { get; set; } = new List<SignalEvent>();
    }

    public sealed class SignalsResponse
    {
        // OctantCompass がこの起動で持っている、この環境の件数（履歴に入れなかったものも数える）
        [JsonProperty("count")] public int Count { get; set; }
        [JsonProperty("rejected")] public List<RejectedSignal> Rejected { get; set; } = new List<RejectedSignal>();
    }

    public sealed class RejectedSignal
    {
        [JsonProperty("index")] public int Index { get; set; }
        [JsonProperty("reason")] public string Reason { get; set; }
    }
}
