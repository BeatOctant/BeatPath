using System;
using System.Collections.Generic;
using System.Linq;

namespace OctantPath.Core.UI
{
    // 浮かぶ板の手札の並べ方。文字は縮めずに 2 行へ分けて並べる（1 行に詰めると、英語で 1.4 まで縮んで読めなかった。2026-10-16）。
    // 2 行に入りきらないときだけ、全体を縮める
    public static class HomeTiles
    {
        public const int Rows = 2;
        public const float RowWidth = 116f;
        public const float Gap = 1f;
        public const float Height = 8f;
        public const float FontSize = 3.6f;
        public const float MinWidth = 16f;

        // 縮めるときの下限。これより小さくはしない（入らない分は文字の大きさを自動で合わせる。Tile）
        public const float MinScale = 0.6f;

        // 文字の幅の見積もり（UI の単位、大きさ 3.6 のとき）。推測: 全角は 1 文字 3.8、半角は 2.2、左右の余白で 6
        public static float Width(string label)
        {
            var text = label ?? "";
            var width = text.Sum(c => c >= '⺀' ? 3.8f : 2.2f) + 6f;
            return Math.Max(MinWidth, width);
        }

        // 手札（並べる順）の幅から、何行目に置くかと倍率を決める。行の幅の大きい方がなるべく小さくなるところで分ける
        public static Layout Arrange(IReadOnlyList<string> labels)
        {
            var widths = labels.Select(Width).ToList();
            for (var scale = 1f; scale >= MinScale - 0.001f; scale -= 0.05f)
            {
                var split = Split(widths, scale);
                if (split >= 0)
                {
                    return new Layout(widths.Select(w => w * scale).ToList(), widths.Select((_, i) => i < split ? 0 : 1).ToList(), scale);
                }
            }
            // 下限まで縮めても入らない: 下限のまま半分ずつに分ける（はみ出した分は文字の自動の大きさに任せる）
            var half = (widths.Count + 1) / 2;
            return new Layout(widths.Select(w => w * MinScale).ToList(), widths.Select((_, i) => i < half ? 0 : 1).ToList(), MinScale);
        }

        // 1 行目に置く数。どう分けても入らなければ -1
        private static int Split(List<float> widths, float scale)
        {
            var best = -1;
            var bestWidest = float.MaxValue;
            for (var split = 0; split <= widths.Count; split++)
            {
                var first = RowSum(widths.Take(split), scale);
                var second = RowSum(widths.Skip(split), scale);
                var widest = Math.Max(first, second);
                if (widest <= RowWidth && widest < bestWidest)
                {
                    best = split;
                    bestWidest = widest;
                }
            }
            return best;
        }

        private static float RowSum(IEnumerable<float> widths, float scale)
        {
            var list = widths.ToList();
            return list.Count == 0 ? 0f : list.Sum() * scale + Gap * (list.Count - 1);
        }

        public sealed class Layout
        {
            public Layout(List<float> widths, List<int> rows, float scale)
            {
                Widths = widths;
                RowOf = rows;
                Scale = scale;
            }

            public List<float> Widths { get; }
            public List<int> RowOf { get; }
            public float Scale { get; }
            public float FontSize => HomeTiles.FontSize * Scale;
        }
    }
}
