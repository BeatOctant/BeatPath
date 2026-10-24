using System.Collections.Generic;
using OctantPath.Core.Recommendations;
using Xunit;

namespace OctantPath.Core.Tests
{
    public sealed class RecommendationLogTests
    {
        [Fact]
        public void WritesTheRequestAndEachSongWithItsReason()
        {
            var request = new RecommendationRequest { Preset = "song", Seed = new ChartRef { Hash = "aa", Characteristic = "Standard", Difficulty = "Expert" }, Count = 30 };
            var response = new RecommendationResponse { RequestId = "r1", DbVersion = "v1" };
            var candidate = new Candidate
            {
                Rank = 1,
                Hash = "bb",
                Characteristic = "Standard",
                Difficulty = "Hard",
                Reason = new CandidateReason { Views = new Dictionary<string, double> { ["song"] = 0.8312 }, Difficulty = new DifficultyReason { Aim = "fit", ClearChance = 0.9 } },
            };
            var lines = RecommendationLog.Lines(request, response, new[] { new RecommendationItem("r1", candidate, null) }, null);
            Assert.Equal("preset=song seed=aa Standard Expert count=30 requestId=r1 db=v1 candidates=1", lines[0]);
            Assert.Equal("  #1 - (not on BeatSaver) Standard Hard views=song:0.831 difficulty(aim=fit clear=0.9)", lines[1]);
        }

        [Fact]
        public void WritesTheFailure()
        {
            var lines = RecommendationLog.Lines(new RecommendationRequest { Preset = "for_you", Count = 10 }, null, null, "not connected");
            Assert.Equal(new[] { "preset=for_you seed=none count=10 failed: not connected" }, lines);
        }
    }
}
