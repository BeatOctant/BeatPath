using System;
using System.IO;
using System.Linq;
using OctantPath.Core.Maps;

namespace OctantPath.Maps
{
    // SongCore に読み込まれたマップを調べる
    internal static class LevelLookup
    {
        public const string PackName = "OctantPath";

        public static bool IsInstalled(string hash)
        {
            return SongCore.Loader.GetLevelByHash(hash) != null;
        }

        // 専用フォルダの、そのフォルダに置いたマップが読み込まれていて、その譜面があるか（OctantPath 法 2.5）。
        // SongCore の hash の表は削除のあとに古くなることがあるので、フォルダの一覧で確かめる
        public static BeatmapLevel FindInSeparateFolder(string levelsDirectory, string folder, string hash)
        {
            var separate = SongCore.Loader.SeparateSongFolders.FirstOrDefault(f => SamePath(f.SongFolderEntry.Path, levelsDirectory));
            if (separate == null)
            {
                return null;
            }
            foreach (var pair in separate.Levels)
            {
                if (SamePath(pair.Key, folder))
                {
                    var loaded = MapHash.Normalize(SongCore.Collections.GetCustomLevelHash(pair.Value.levelID));
                    return loaded == MapHash.Normalize(hash) ? pair.Value : null;
                }
            }
            return null;
        }

        public static bool HasChart(BeatmapLevel level, string characteristic, string difficulty)
        {
            return level.GetBeatmapKeys().Any(k => IsChart(k, characteristic, difficulty));
        }

        // その譜面（characteristic と difficulty）の BeatmapKey か
        public static bool IsChart(BeatmapKey key, string characteristic, string difficulty)
        {
            return key.beatmapCharacteristic.serializedName == characteristic && key.difficulty.ToString() == difficulty;
        }

        private static bool SamePath(string a, string b)
        {
            return string.Equals(Path.GetFullPath(a).TrimEnd('/', '\\'), Path.GetFullPath(b).TrimEnd('/', '\\'), StringComparison.OrdinalIgnoreCase);
        }
    }
}
