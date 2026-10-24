using OctantPath.Core.UI;
using Xunit;

namespace OctantPath.Core.Tests
{
    public sealed class DisplayTextTests
    {
        [Theory]
        [InlineData("Haru-ichiban\r\n", "Haru-ichiban")]
        [InlineData("a\nb", "a b")]
        [InlineData("a \t\r\n  b", "a b")]
        [InlineData("  Cat Rubbing  ", "Cat Rubbing")]
        [InlineData("ダーリンダーリン", "ダーリンダーリン")]
        [InlineData("", "")]
        [InlineData(null, "")]
        public void KeepsOneLine(string text, string expected)
        {
            Assert.Equal(expected, DisplayText.Line(text));
        }
    }
}
