using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;
using OctantPath.Core.Languages;

namespace OctantPath.Core.Maps
{
    // マップの版の hash（OctantPath 法 2.1）。ZIP 全体ではなく、Info.dat とそれが指すファイルを決まった順に連結した SHA-1。
    // 順は SongCore 3.15.3 と同じ: Info.dat が v4 なら音源、続いて難易度ごとに譜面とライティング。それより前の形式は難易度の譜面。
    public static class MapHash
    {
        public static bool IsValid(string hash) => hash != null && hash.Length == 40 && hash.All(Uri.IsHexDigit);

        // 比べる前に小文字にそろえる（OctantPath 法 1.2）
        public static string Normalize(string hash) => IsValid(hash) ? hash.ToLowerInvariant() : null;

        public static MapHashResult Compute(string folder)
        {
            var infoPath = FindInfo(folder);
            if (infoPath == null)
            {
                return MapHashResult.Fail(Lang.T("map.noInfo"));
            }
            string infoText;
            JObject info;
            try
            {
                infoText = File.ReadAllText(infoPath);
                info = JObject.Parse(infoText);
            }
            catch (Exception ex)
            {
                return MapHashResult.Fail(Lang.T("map.infoUnreadable", new { error = ex.Message }));
            }

            List<string> names;
            try
            {
                names = ReferencedFiles(info);
            }
            catch (Exception ex)
            {
                return MapHashResult.Fail(Lang.T("map.infoMalformed", new { error = ex.Message }));
            }

            using (var sha1 = SHA1.Create())
            {
                var infoBytes = Encoding.UTF8.GetBytes(infoText);
                sha1.TransformBlock(infoBytes, 0, infoBytes.Length, null, 0);
                foreach (var name in names)
                {
                    if (!SafeName(name))
                    {
                        return MapHashResult.Fail(Lang.T("map.infoPointsOutside", new { name }));
                    }
                    var path = Path.Combine(folder, name);
                    if (!File.Exists(path))
                    {
                        return MapHashResult.Fail(Lang.T("map.infoFileMissing", new { name }));
                    }
                    var bytes = File.ReadAllBytes(path);
                    sha1.TransformBlock(bytes, 0, bytes.Length, null, 0);
                }
                sha1.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                return MapHashResult.Ok(string.Concat(sha1.Hash.Select(b => b.ToString("x2"))), names.Prepend(Path.GetFileName(infoPath)).ToList());
            }
        }

        public static string FindInfo(string folder)
        {
            foreach (var name in new[] { "Info.dat", "info.dat" })
            {
                var path = Path.Combine(folder, name);
                if (File.Exists(path))
                {
                    return path;
                }
            }
            return null;
        }

        internal static List<string> ReferencedFiles(JObject info)
        {
            var names = new List<string>();
            var version = (string)info["version"];
            if (version != null && version.StartsWith("4", StringComparison.Ordinal))
            {
                names.Add((string)info["audio"]["audioDataFilename"]);
                foreach (var diff in (JArray)info["difficultyBeatmaps"])
                {
                    names.Add((string)diff["beatmapDataFilename"]);
                    names.Add((string)diff["lightshowDataFilename"]);
                }
            }
            else
            {
                foreach (var set in (JArray)info["_difficultyBeatmapSets"])
                {
                    foreach (var diff in (JArray)set["_difficultyBeatmaps"])
                    {
                        names.Add((string)diff["_beatmapFilename"]);
                    }
                }
            }
            if (names.Any(string.IsNullOrEmpty))
            {
                throw new FormatException("ファイル名が空");
            }
            return names;
        }

        // マップのフォルダの中だけを指す名前か（OctantPath 法 2.3）
        public static bool SafeName(string name)
        {
            if (string.IsNullOrEmpty(name) || Path.IsPathRooted(name) || name.Contains(":"))
            {
                return false;
            }
            var parts = name.Split('/', '\\');
            return parts.All(p => p.Length > 0 && p != "." && p != "..");
        }
    }

    public sealed class MapHashResult
    {
        private MapHashResult(string hash, IReadOnlyList<string> files, string error)
        {
            Hash = hash;
            Files = files;
            Error = error;
        }

        public string Hash { get; }
        public IReadOnlyList<string> Files { get; }
        public string Error { get; }
        public bool Succeeded => Hash != null;

        internal static MapHashResult Ok(string hash, IReadOnlyList<string> files) => new MapHashResult(hash, files, null);
        internal static MapHashResult Fail(string error) => new MapHashResult(null, null, error);
    }
}
