using System.Text.RegularExpressions;

namespace OctantPath.Core.UI
{
    // 画面に出す曲名・作者・譜面作者を 1 行にする。BeatSaver の値には改行が入っていることがあり
    // （「ふせっしょー」の作者名は "Haru-ichiban\r\n"）、word-wrapping を切っていても改行で折れて次の行に重なった。
    // 表示だけに使い、保存やプレイリストに書く値は変えない
    public static class DisplayText
    {
        private static readonly Regex Spaces = new Regex(@"\s+");

        public static string Line(string text) => string.IsNullOrEmpty(text) ? text ?? "" : Spaces.Replace(text, " ").Trim();
    }
}
