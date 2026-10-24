using System.Collections.Generic;
using Newtonsoft.Json;

namespace OctantPath.Core.Maps
{
    // BeatSaver API のマップ（外部依存。使う項目だけ）
    public sealed class BeatSaverMap
    {
        [JsonProperty("id")] public string Key { get; set; }
        [JsonProperty("name")] public string Name { get; set; }
        [JsonProperty("metadata")] public BeatSaverMetadata Metadata { get; set; }
        [JsonProperty("versions")] public List<BeatSaverVersion> Versions { get; set; } = new List<BeatSaverVersion>();

        // 頼んだ hash の版だけを返す。先頭の版や別の版で代用しない（OctantPath 法 2.1）
        public BeatSaverVersion Version(string hash)
        {
            var wanted = MapHash.Normalize(hash);
            return wanted == null ? null : Versions.Find(v => MapHash.Normalize(v.Hash) == wanted);
        }
    }

    public sealed class BeatSaverMetadata
    {
        [JsonProperty("songName")] public string SongName { get; set; }
        [JsonProperty("songSubName")] public string SongSubName { get; set; }
        [JsonProperty("songAuthorName")] public string SongAuthorName { get; set; }
        [JsonProperty("levelAuthorName")] public string LevelAuthorName { get; set; }
        [JsonProperty("duration")] public double Duration { get; set; }
    }

    public sealed class BeatSaverVersion
    {
        [JsonProperty("hash")] public string Hash { get; set; }
        [JsonProperty("state")] public string State { get; set; }
        [JsonProperty("downloadURL")] public string DownloadUrl { get; set; }
        [JsonProperty("coverURL")] public string CoverUrl { get; set; }
        [JsonProperty("previewURL")] public string PreviewUrl { get; set; }
        [JsonProperty("diffs")] public List<BeatSaverDiff> Diffs { get; set; } = new List<BeatSaverDiff>();
    }

    public sealed class BeatSaverDiff
    {
        [JsonProperty("characteristic")] public string Characteristic { get; set; }
        [JsonProperty("difficulty")] public string Difficulty { get; set; }
        [JsonProperty("nps")] public double Nps { get; set; }
        [JsonProperty("njs")] public double Njs { get; set; }
    }
}
