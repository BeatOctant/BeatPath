using System.Linq;
using OctantPath.Core.UI;
using Xunit;

namespace OctantPath.Core.Tests
{
    public sealed class HomeTilesTests
    {
        // 今の手札（OctantHarvest の official.json のうち、起点なしで出るもの）と「ミックス」
        [Theory]
        [InlineData("ミックス", "似た曲", "ジャンル", "似たマップ", "おすすめ", "挑戦", "久しぶり")]
        [InlineData("Mix", "Similar songs", "Genre", "Similar maps", "Recommended", "Challenge", "Revisit")]
        public void KeepsTheTextSizeForTodaysCards(params string[] labels)
        {
            var layout = HomeTiles.Arrange(labels);
            Assert.Equal(1f, layout.Scale);
            AssertFits(layout);
        }

        [Fact]
        public void ShrinksOnlyWhenTwoRowsAreNotEnough()
        {
            var labels = Enumerable.Range(0, 14).Select(i => $"A long card name {i}").ToArray();
            var layout = HomeTiles.Arrange(labels);
            Assert.True(layout.Scale < 1f);
            Assert.True(layout.Scale >= HomeTiles.MinScale);
        }

        [Fact]
        public void KeepsTheOrder()
        {
            var layout = HomeTiles.Arrange(new[] { "a", "b", "c", "d", "e", "f", "g", "h", "i" });
            Assert.Equal(layout.RowOf.OrderBy(r => r).ToList(), layout.RowOf);
        }

        private static void AssertFits(HomeTiles.Layout layout)
        {
            for (var row = 0; row < HomeTiles.Rows; row++)
            {
                var widths = layout.Widths.Where((_, i) => layout.RowOf[i] == row).ToList();
                var sum = widths.Sum() + HomeTiles.Gap * System.Math.Max(0, widths.Count - 1);
                Assert.True(sum <= HomeTiles.RowWidth, $"row {row} is {sum} wide");
            }
        }
    }
}
