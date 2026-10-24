namespace OctantPath.Core.Maps
{
    // 譜面の短い書き方（「Cat Rubbing（EX+）」のように曲名の後ろに付ける）。Standard は書かない
    public static class DifficultyLabel
    {
        public static string Short(string difficulty)
        {
            switch (difficulty)
            {
                case "Easy": return "E";
                case "Normal": return "N";
                case "Hard": return "H";
                case "Expert": return "EX";
                case "ExpertPlus": return "EX+";
                default: return difficulty ?? "";
            }
        }

        public static string Chart(string characteristic, string difficulty)
        {
            var shortDifficulty = Short(difficulty);
            return string.IsNullOrEmpty(characteristic) || characteristic == "Standard" ? shortDifficulty : $"{characteristic} {shortDifficulty}";
        }
    }
}
