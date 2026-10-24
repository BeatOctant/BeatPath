namespace OctantPath.Core.Maps
{
    public static class LevelIds
    {
        private const string Prefix = "custom_level_";

        // カスタムの曲の level ID（custom_level_<hash>）から hash を取る。それ以外は null
        public static string Hash(string levelId)
        {
            if (levelId == null || !levelId.StartsWith(Prefix, System.StringComparison.Ordinal) || levelId.Length < Prefix.Length + 40)
            {
                return null;
            }
            return MapHash.Normalize(levelId.Substring(Prefix.Length, 40));
        }
    }
}
