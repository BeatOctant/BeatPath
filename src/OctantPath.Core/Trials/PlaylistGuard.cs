using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OctantPath.Core.Maps;
using Newtonsoft.Json.Linq;

namespace OctantPath.Core.Trials
{
    // 他のプレイリストに入っているマップは消さない。OctantPath が作ったプレイリスト（customData の印）は数えない。
    // 読めないプレイリストがあれば、守るものを見落とさないよう、削除を止める（OctantPath 法 3.4）
    public static class PlaylistGuard
    {
        public const string MarkKey = "OctantPath";

        public static HashSet<string> ProtectedHashes(string playlistsDirectory, out string error)
        {
            error = null;
            var hashes = new HashSet<string>();
            if (!Directory.Exists(playlistsDirectory))
            {
                return hashes;
            }
            foreach (var file in Directory.EnumerateFiles(playlistsDirectory, "*", SearchOption.AllDirectories))
            {
                var ext = Path.GetExtension(file).ToLowerInvariant();
                if (ext != ".bplist" && ext != ".json")
                {
                    continue;
                }
                JObject json;
                try
                {
                    json = JObject.Parse(File.ReadAllText(file));
                }
                catch (Exception ex)
                {
                    error = $"{Path.GetFileName(file)} が読めない: {ex.Message}";
                    return null;
                }
                if (json["customData"]?[MarkKey] != null)
                {
                    continue;
                }
                foreach (var song in json["songs"] as JArray ?? new JArray())
                {
                    var hash = MapHash.Normalize((string)song["hash"]) ?? LevelIds.Hash((string)song["levelid"] ?? (string)song["levelId"]);
                    if (hash != null)
                    {
                        hashes.Add(hash);
                    }
                }
            }
            return hashes;
        }
    }
}
