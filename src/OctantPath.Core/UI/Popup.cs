using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml;

namespace OctantPath.Core.UI
{
    // 中央の小窓（曲の詳細に重ねる BSML の modal）の枠。縁からの余白（Margin）はここだけで決め、どの小窓も同じ値を使う。
    // 小窓の中身は「上から順に並べる」1 段（vertical）にし、行の幅と、残りの高さを埋める行の高さはここが出す。
    // なので行を足したり高さを変えたりしても縁の余白は変わらず、はみ出しは Problems（テストで見る）で分かる
    public sealed class Popup
    {
        // 縁から中身までの余白（上下左右とも。UI の単位）
        public const float Margin = 3f;

        // 小窓の高さの上限。これより高いと角が切れた（a2d881f）
        public const float MaxHeight = 84f;

        // 中身の行で使える印。{inner-width} は中身の幅、{fill-height} は残りの高さ（ほかの行の高さと行の間を引いたもの）
        public const string InnerWidthToken = "{inner-width}";
        public const string FillHeightToken = "{fill-height}";

        public Popup(string id, string showEvent, string hideEvent, float width, float height, float spacing, string rows, string overlay = null)
        {
            Overlay = overlay ?? "";
            Id = id;
            ShowEvent = showEvent;
            HideEvent = hideEvent;
            Width = width;
            Height = height;
            Spacing = spacing;
            Rows = rows;
        }

        public string Id { get; }
        public string ShowEvent { get; }
        public string HideEvent { get; }
        public float Width { get; }
        public float Height { get; }
        // 行と行の間
        public float Spacing { get; }
        // 中身の行（BSML）。印を含んでよい。active で消える行も、出ているものとして高さを数える
        public string Rows { get; }
        // 行の外に、小窓の角から位置を決めて重ねる部品（左上の戻るボタン）。高さの勘定には入らない
        public string Overlay { get; }

        // 外周の枠線の image の id と色（OctantPath.Desktop の枠の線と同じ、淡い線）
        public string FrameId => Id + "-frame";
        public const string FrameColor = "#FFFFFF2E";

        public float InnerWidth => Width - 2 * Margin;
        public float InnerHeight => Height - 2 * Margin;

        // {fill-height} の行に回る高さ。その行がなければ、下に余る高さ
        public float FillHeight
        {
            get
            {
                var heights = RowHeights().ToList();
                var used = heights.Where(h => h.HasValue).Sum(h => h.Value) + Spacing * Math.Max(0, heights.Count - 1);
                return InnerHeight - used;
            }
        }

        // modal の BSML（言語の {{キー}} はそのまま残る）
        public string Markup =>
            $"<modal id='{Id}' show-event='{ShowEvent}' hide-event='{HideEvent}' click-off-closes='true' move-to-center='true' size-delta-x='{F(Width)}' size-delta-y='{F(Height)}'>\n" +
            // 小窓の外周の細い枠線。絵（ゲームの枠の sprite）と切り方は MOD が入れる（BSML の image では Sliced にできない）
            $"  <image id='{FrameId}' anchor-min-x='0' anchor-max-x='1' anchor-min-y='0' anchor-max-y='1' size-delta-x='0' size-delta-y='0' image-color='{FrameColor}'/>\n" +
            $"  <vertical child-control-height='true' child-expand-height='false' child-align='UpperCenter' pad='{F(Margin)}' spacing='{F(Spacing)}'>\n" +
            FillTokens(Rows) + Filler +
            "\n  </vertical>" + Overlay + "\n</modal>";

        // 行が中身の高さに足りないときの下の空き。枠（vertical）は中身の高さに縮んで小窓の真ん中に置かれるので、
        // 空きがあると行全体が空きの半分だけ下がり、上の縁からの距離が Margin より大きくなった（左上の戻るボタンより見出しが下に出た）。
        // 空きを最後の行として足し、行がいつも上の縁から Margin のところから始まるようにする
        private string Filler
        {
            get
            {
                var spare = RowHeights().Any(h => !h.HasValue) ? 0f : FillHeight - Spacing;
                return spare > 0.001f ? $"\n    <horizontal preferred-width='{F(InnerWidth)}' preferred-height='{F(spare)}'/>" : "";
            }
        }

        // 収まらないところ（ないときは空）。小窓の BSML を変えたらテストがこれを見る
        public IReadOnlyList<string> Problems()
        {
            var problems = new List<string>();
            if (Height > MaxHeight)
            {
                problems.Add($"{Id}: 高さ {F(Height)} は上限 {F(MaxHeight)} を超える（角が切れる）");
            }
            List<XmlElement> rows;
            try
            {
                rows = ParseRows(FillTokens(Rows));
            }
            catch (XmlException ex)
            {
                problems.Add($"{Id}: 行の BSML が読めない: {ex.Message}");
                return problems;
            }
            var fills = RowHeights().Count(h => !h.HasValue);
            if (fills > 1)
            {
                problems.Add($"{Id}: {FillHeightToken} の行が {fills} 個ある（1 個まで）");
            }
            var fill = FillHeight;
            if (fill < 0)
            {
                problems.Add($"{Id}: 行が {F(-fill)} はみ出す（中身の高さ {F(InnerHeight)}）");
            }
            foreach (var row in rows)
            {
                var name = row.GetAttribute("id") is var id && id.Length > 0 ? $"{row.Name}#{id}" : row.Name;
                if (!row.HasAttribute("preferred-height"))
                {
                    problems.Add($"{Id}: {name} に preferred-height がない（高さを数えられない）");
                }
                foreach (var attribute in new[] { "preferred-width", "list-width" })
                {
                    if (TryNumber(row.GetAttribute(attribute), out var width) && width > InnerWidth)
                    {
                        problems.Add($"{Id}: {name} の {attribute} {F(width)} が中身の幅 {F(InnerWidth)} を超える");
                    }
                }
                // 横に並べる行は、並べたものの幅の合計が行の幅に収まるか
                if (row.Name == "horizontal")
                {
                    var limit = TryNumber(row.GetAttribute("preferred-width"), out var own) ? Math.Min(own, InnerWidth) : InnerWidth;
                    var children = row.ChildNodes.OfType<XmlElement>().ToList();
                    TryNumber(row.GetAttribute("spacing"), out var gap);
                    var total = children.Sum(c => TryNumber(c.GetAttribute("preferred-width"), out var w) ? w : 0f) + gap * Math.Max(0, children.Count - 1);
                    if (total > limit)
                    {
                        problems.Add($"{Id}: {name} の中の幅の合計 {F(total)} が {F(limit)} を超える");
                    }
                }
            }
            return problems;
        }

        // 行ごとの高さ。{fill-height} の行は null
        private IEnumerable<float?> RowHeights()
        {
            List<XmlElement> rows;
            try
            {
                rows = ParseRows(Rows.Replace(InnerWidthToken, F(InnerWidth)));
            }
            catch (XmlException)
            {
                yield break;
            }
            foreach (var row in rows)
            {
                var value = row.GetAttribute("preferred-height");
                if (value == FillHeightToken)
                {
                    yield return null;
                }
                else
                {
                    yield return TryNumber(value, out var height) ? height : 0f;
                }
            }
        }

        private string FillTokens(string rows)
        {
            var fill = Math.Max(0f, FillHeight);
            return rows.Replace(InnerWidthToken, F(InnerWidth)).Replace(FillHeightToken, F(fill));
        }

        // 行だけを読む（BSML の名前空間や、言語の {{キー}} は読むのに関係ない）
        private static List<XmlElement> ParseRows(string rows)
        {
            var document = new XmlDocument();
            document.LoadXml("<rows>" + rows + "</rows>");
            return document.DocumentElement.ChildNodes.OfType<XmlElement>().ToList();
        }

        private static bool TryNumber(string value, out float number) =>
            float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number);

        public static string F(float value) => value.ToString("0.##", CultureInfo.InvariantCulture);
    }
}
