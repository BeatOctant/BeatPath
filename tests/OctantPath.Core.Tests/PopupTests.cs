using System.Linq;
using System.Xml;
using OctantPath.Core.UI;
using Xunit;

namespace OctantPath.Core.Tests
{
    // 中央の小窓が枠に収まるか（ゲームを起動せずに、BSML の数だけで見る）
    public sealed class PopupTests
    {
        [Fact]
        public void EveryPopupFitsInsideItsMargin()
        {
            foreach (var popup in Popups.All)
            {
                Assert.Empty(popup.Problems());
            }
        }

        [Fact]
        public void EveryPopupUsesTheSameMargin()
        {
            var document = new XmlDocument();
            document.LoadXml(Popups.Markup().Replace(Popups.DraftCellToken, ""));
            var modals = document.DocumentElement.ChildNodes.OfType<XmlElement>().ToList();
            Assert.Equal(Popups.All.Length, modals.Count);
            foreach (var modal in modals)
            {
                Assert.Equal("modal", modal.Name);
                // 外周の枠線の image と、中身の枠（vertical）
                var children = modal.ChildNodes.OfType<XmlElement>().ToList();
                Assert.Equal("image", children[0].Name);
                Assert.Equal(modal.GetAttribute("id") + "-frame", children[0].GetAttribute("id"));
                var frame = children[1];
                Assert.Equal("vertical", frame.Name);
                Assert.Equal(Popup.F(Popup.Margin), frame.GetAttribute("pad"));
                // 縁の余白は枠だけが持つ
                Assert.DoesNotContain(frame.Attributes.OfType<XmlAttribute>(), a => a.Name.StartsWith("pad-"));
            }
        }

        [Fact]
        public void LeavesNoTokensInTheMarkup()
        {
            var markup = Popups.Markup();
            Assert.DoesNotContain(Popup.InnerWidthToken, markup);
            Assert.DoesNotContain(Popup.FillHeightToken, markup);
        }

        [Fact]
        public void TheDraftListStillShowsSixRows()
        {
            Assert.Equal(6 * 7.5f, Popups.DraftListHeight);
            Assert.True(Popups.Draft.FillHeight >= 0, $"{Popups.Draft.FillHeight}");
        }

        // 一覧の高さが行の高さの倍でないと、選ぶたびに一覧が上へずれる（HMUI の ReloadDataKeepingPosition が位置を行の頭へ切り下げる）
        [Fact]
        public void TheDraftListHeightIsAWholeNumberOfRows()
        {
            var document = new XmlDocument();
            document.LoadXml(Popups.Markup().Replace(Popups.DraftCellToken, ""));
            var list = (XmlElement)document.SelectSingleNode("//custom-list[@id='draft-list']");
            var cell = float.Parse(list.GetAttribute("cell-size"), System.Globalization.CultureInfo.InvariantCulture);
            foreach (var attribute in new[] { "list-height", "preferred-height" })
            {
                var height = float.Parse(list.GetAttribute(attribute), System.Globalization.CultureInfo.InvariantCulture);
                var rows = height / cell;
                Assert.True(System.Math.Abs(rows - System.Math.Round(rows)) < 0.001, $"{attribute} {height} は {cell} の倍でない");
                Assert.Equal(int.Parse(list.GetAttribute("visible-cells")), (int)System.Math.Round(rows));
            }
        }

        // 種類の小窓と下見の小窓は、左上の角に戻るボタンを置く。上の縁と左の縁からの距離はどちらも Popup.Margin、高さは見出しの行と同じ。
        // 下線の付く button は使わず、OctantPath の「戻る」の絵を押せるようにする
        [Theory]
        [InlineData("choice-modal", "choice-back")]
        [InlineData("draft-modal", "draft-back")]
        public void TheBackButtonSitsInTheTopLeftCorner(string modalId, string action)
        {
            var document = new XmlDocument();
            document.LoadXml(Popups.Markup().Replace(Popups.DraftCellToken, ""));
            var back = (XmlElement)document.SelectSingleNode($"//modal[@id='{modalId}']/clickable-image[@on-click='{action}']");
            Assert.NotNull(back);
            Assert.Equal(Popup.F(Popup.Margin), back.GetAttribute("anchor-pos-x"));
            Assert.Equal(Popup.F(-Popup.Margin), back.GetAttribute("anchor-pos-y"));
            Assert.Equal("0", back.GetAttribute("pivot-x"));
            Assert.Equal("1", back.GetAttribute("pivot-y"));
            // 見出しの行（下見では起点の行と 1 つの vertical にまとめた中の 1 行目）
            var header = (XmlElement)document.SelectSingleNode($"//modal[@id='{modalId}']/vertical/*[1]");
            if (header.Name == "vertical")
            {
                header = (XmlElement)header.SelectSingleNode("*[1]");
            }
            Assert.Equal("horizontal", header.Name);
            Assert.Equal(header.GetAttribute("preferred-height"), back.GetAttribute("size-delta-y"));
            Assert.Null(document.SelectSingleNode($"//modal[@id='{modalId}']//button[@on-click='{action}']"));
            // 見出しの左右に、戻るボタンと同じ幅の空き（見出しは真ん中のまま、戻るボタンに重ならない）
            var items = header.ChildNodes.OfType<XmlElement>().ToList();
            Assert.Equal(back.GetAttribute("size-delta-x"), items[0].GetAttribute("preferred-width"));
            Assert.Equal(back.GetAttribute("size-delta-x"), items[2].GetAttribute("preferred-width"));
        }

        // 行の高さと行の間の合計は、どの小窓も中身の高さちょうど。足りないと枠が縮んで真ん中に寄り、
        // 行が上の縁から Margin より下に出た（2026-10-12、戻るボタンより見出しが下に出た）
        [Fact]
        public void EveryPopupsRowsFillItsInnerHeight()
        {
            var document = new XmlDocument();
            document.LoadXml(Popups.Markup().Replace(Popups.DraftCellToken, ""));
            foreach (var popup in Popups.All)
            {
                var frame = (XmlElement)document.SelectSingleNode($"//modal[@id='{popup.Id}']/vertical");
                var rows = frame.ChildNodes.OfType<XmlElement>().ToList();
                var used = rows.Sum(r => float.Parse(r.GetAttribute("preferred-height"), System.Globalization.CultureInfo.InvariantCulture)) + popup.Spacing * (rows.Count - 1);
                Assert.True(System.Math.Abs(used - popup.InnerHeight) < 0.01f, $"{popup.Id}: {used} != {popup.InnerHeight}");
            }
        }

        // 起点の行は種類の行のすぐ下（間をあけない）
        [Fact]
        public void TheSeedLineSitsRightUnderTheTypeLine()
        {
            var document = new XmlDocument();
            document.LoadXml(Popups.Markup().Replace(Popups.DraftCellToken, ""));
            var block = (XmlElement)document.SelectSingleNode("//modal[@id='draft-modal']/vertical/*[1]");
            Assert.Equal("vertical", block.Name);
            Assert.Equal("0", block.GetAttribute("spacing"));
            var rows = block.ChildNodes.OfType<XmlElement>().ToList();
            Assert.NotNull(rows[0].SelectSingleNode(".//text[@text='~draft-title']"));
            Assert.Equal("~draft-seed", rows[1].GetAttribute("text"));
            Assert.Equal("Top", rows[1].GetAttribute("align"));
        }

        // 下見の一覧とスクロールの帯は中身の幅に収まる（帯は一覧の右の外に 8 はみ出して置かれる）
        [Fact]
        public void TheDraftListAndItsScrollbarFitInsideTheMargin()
        {
            var document = new XmlDocument();
            document.LoadXml(Popups.Markup().Replace(Popups.DraftCellToken, ""));
            var list = (XmlElement)document.SelectSingleNode("//custom-list[@id='draft-list']");
            var width = float.Parse(list.GetAttribute("list-width"), System.Globalization.CultureInfo.InvariantCulture);
            Assert.Equal(Popups.Draft.InnerWidth, width + Popups.ListScrollbarWidth);
            Assert.Equal(list.GetAttribute("list-width"), list.GetAttribute("preferred-width"));
            // 一覧は左に寄せ、右の空きに帯が入る
            var row = (XmlElement)list.ParentNode;
            Assert.Equal("horizontal", row.Name);
            Assert.Equal("MiddleLeft", row.GetAttribute("child-align"));
            Assert.Equal(Popup.F(Popups.ListScrollbarWidth), ((XmlElement)list.NextSibling).GetAttribute("preferred-width"));
        }

        // 上の行は種類、下の行は起点
        [Fact]
        public void TheDraftPopupShowsTheTypeAboveTheSeed()
        {
            var markup = Popups.Draft.Markup;
            Assert.True(markup.IndexOf("~draft-title") < markup.IndexOf("~draft-seed"));
        }

        // 種類のタイルは下見の行と同じ高さ・幅で、入りきらない分はスクロールでめくる（2026-10-12、縮めると高さがそろわなかった）
        [Fact]
        public void ChoiceTilesMatchThePreviewRowsAndScroll()
        {
            Assert.Equal(Popups.DraftCellSize, Popups.ChoiceTileHeight);
            Assert.Equal(Popups.DraftListWidth, Popups.ChoiceTileWidth);
            var document = new XmlDocument();
            document.LoadXml(Popups.Markup().Replace(Popups.DraftCellToken, ""));
            var tiles = (XmlElement)document.SelectSingleNode("//modal[@id='choice-modal']/vertical/*[@id='choice-tiles']");
            Assert.Equal("scroll-view", tiles.Name);
            // 欄は中身の幅いっぱいで、タイルとスクロールの帯（右 8）が余白の内側に収まる
            Assert.Equal(Popup.F(Popups.Choice.InnerWidth), tiles.GetAttribute("preferred-width"));
            Assert.Equal(Popups.Choice.InnerWidth, Popups.ChoiceTileWidth + Popups.ListScrollbarWidth);
        }

        [Fact]
        public void FillsTheRestOfTheHeight()
        {
            var popup = new Popup("p", "s", "h", 100f, 50f, 2f, @"
    <text preferred-width='{inner-width}' preferred-height='10'/>
    <vertical preferred-width='{inner-width}' preferred-height='{fill-height}'/>");
            Assert.Equal(94f, popup.InnerWidth);
            Assert.Equal(50f - 2 * Popup.Margin - 10f - 2f, popup.FillHeight);
            Assert.Contains($"preferred-height='{Popup.F(popup.FillHeight)}'", popup.Markup);
            Assert.Contains("preferred-width='94'", popup.Markup);
            Assert.Empty(popup.Problems());
        }

        [Fact]
        public void FindsRowsThatGoPastTheBottom()
        {
            var popup = new Popup("p", "s", "h", 100f, 30f, 1f, @"
    <text preferred-height='20'/>
    <text preferred-height='10'/>");
            Assert.Contains(popup.Problems(), p => p.Contains("はみ出す"));
        }

        [Fact]
        public void FindsRowsWiderThanTheInside()
        {
            var popup = new Popup("p", "s", "h", 100f, 30f, 0f, "<text preferred-width='96' preferred-height='5'/>");
            Assert.Contains(popup.Problems(), p => p.Contains("中身の幅"));
        }

        [Fact]
        public void FindsButtonRowsWiderThanTheRow()
        {
            var popup = new Popup("p", "s", "h", 100f, 30f, 0f, @"
    <horizontal spacing='2' preferred-width='{inner-width}' preferred-height='7'>
      <button preferred-width='50'/>
      <button preferred-width='50'/>
    </horizontal>");
            Assert.Contains(popup.Problems(), p => p.Contains("幅の合計"));
        }

        [Fact]
        public void FindsPopupsTallerThanTheCornersAllow()
        {
            var popup = new Popup("p", "s", "h", 100f, Popup.MaxHeight + 1f, 0f, "<text preferred-height='{fill-height}'/>");
            Assert.Contains(popup.Problems(), p => p.Contains("上限"));
        }

        [Fact]
        public void FindsRowsWithoutAHeight()
        {
            var popup = new Popup("p", "s", "h", 100f, 30f, 0f, "<text text='x'/>");
            Assert.Contains(popup.Problems(), p => p.Contains("preferred-height がない"));
        }
    }
}
