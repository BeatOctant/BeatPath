using System;
using System.IO;

namespace OctantPath.Core.Maps
{
    public static class ZipSources
    {
        public const string BeatSaver = "BeatSaver";

        public static bool IsBeatSaver(string source) => string.IsNullOrWhiteSpace(source) || string.Equals(source.Trim(), BeatSaver, StringComparison.OrdinalIgnoreCase);

        // 今の取得元から取れるか。BeatSaver なら推薦された版に受け付ける URL（BeatSaverUrls）があるもの、ローカルのフォルダならそこに zip があるものだけ（OctantPath 法 2.8）
        public static bool CanFetch(string source, string hash, BeatSaverVersion version) =>
            IsBeatSaver(source) ? BeatSaverUrls.Allowed(version?.DownloadUrl) : LocalZip(source, hash) != null;

        // ローカルのフォルダのとき、その hash の zip の場所。ないなら null（OctantPath 法 2.8: BeatSaver から取らずに飛ばす）。
        // フォルダは <hash の先頭 2 文字>/<hash>.zip。大文字と小文字は区別しない
        public static string LocalZip(string folder, string hash)
        {
            var h = MapHash.Normalize(hash);
            if (h == null || string.IsNullOrWhiteSpace(folder))
            {
                return null;
            }
            foreach (var candidate in new[] { h, h.ToUpperInvariant() })
            {
                foreach (var prefix in new[] { candidate.Substring(0, 2), candidate.Substring(0, 2).ToUpperInvariant(), candidate.Substring(0, 2).ToLowerInvariant() })
                {
                    var path = Path.Combine(folder, prefix, candidate + ".zip");
                    if (File.Exists(path))
                    {
                        return path;
                    }
                }
            }
            return null;
        }
    }
}
