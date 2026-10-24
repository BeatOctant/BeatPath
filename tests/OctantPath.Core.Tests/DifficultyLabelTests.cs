using OctantPath.Core.Maps;
using Xunit;

namespace OctantPath.Core.Tests
{
    public sealed class DifficultyLabelTests
    {
        [Theory]
        [InlineData("Standard", "ExpertPlus", "EX+")]
        [InlineData("Standard", "Expert", "EX")]
        [InlineData("Standard", "Hard", "H")]
        [InlineData("Standard", "Normal", "N")]
        [InlineData("Standard", "Easy", "E")]
        [InlineData("OneSaber", "ExpertPlus", "OneSaber EX+")]
        [InlineData(null, "Hard", "H")]
        public void ShortensTheChart(string characteristic, string difficulty, string expected)
        {
            Assert.Equal(expected, DifficultyLabel.Chart(characteristic, difficulty));
        }
    }
}
