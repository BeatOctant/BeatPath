using System.Collections.Generic;
using Newtonsoft.Json;

namespace OctantPath.Core.Connection
{
    // 契約3（OctantPath to OctantCompass）の GET /v1/health の応答。知らない項目は無視する
    public sealed class HealthResponse
    {
        [JsonProperty("apiVersion")] public string ApiVersion { get; set; }
        [JsonProperty("hostVersion")] public string HostVersion { get; set; }
        [JsonProperty("status")] public string Status { get; set; }
        [JsonProperty("reason")] public string Reason { get; set; }
        [JsonProperty("db")] public HealthDb Db { get; set; }
        [JsonProperty("update")] public HealthUpdate Update { get; set; }
        [JsonProperty("paired")] public bool? Paired { get; set; }
        // この OctantCompass の起動を表す値と、この環境の signal の件数。ペアリング済みのときだけ返る（契約3 2.0）
        [JsonProperty("instanceId")] public string InstanceId { get; set; }
        [JsonProperty("signals")] public int? Signals { get; set; }

        // /v1/presets の revision。ペアリング済みで、OctantCompass が ready のときだけ返る（ほかは null）
        [JsonProperty("presetsRevision")] public string PresetsRevision { get; set; }
    }

    public sealed class HealthDb
    {
        [JsonProperty("dbVersion")] public string DbVersion { get; set; }
        [JsonProperty("genres")] public List<string> Genres { get; set; }

        // 役割名ごとのビュー。契約3 1.2 で足した項目なので、マイナー版が古い（1.1 の）OctantCompass では null
        [JsonProperty("roles")] public Dictionary<string, List<string>> Roles { get; set; }
    }

    public sealed class HealthUpdate
    {
        [JsonProperty("reason")] public string Reason { get; set; }
    }

    public sealed class PairRequest
    {
        [JsonProperty("secret")] public string Secret { get; set; }
        [JsonProperty("name")] public string Name { get; set; }
    }

    public sealed class PairResponse
    {
        [JsonProperty("token")] public string Token { get; set; }
    }

    public sealed class ErrorBody
    {
        [JsonProperty("error")] public ErrorDetail Error { get; set; }
    }

    public sealed class ErrorDetail
    {
        [JsonProperty("code")] public string Code { get; set; }
        [JsonProperty("message")] public string Message { get; set; }
        [JsonProperty("apiVersion")] public string ApiVersion { get; set; }
    }

    public static class HealthStatus
    {
        public const string Loading = "loading";
        public const string Ready = "ready";
        public const string Degraded = "degraded";
    }
}
