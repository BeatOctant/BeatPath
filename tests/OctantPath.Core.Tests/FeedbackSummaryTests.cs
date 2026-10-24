using System;
using System.Collections.Generic;
using System.IO;
using OctantPath.Core.Recommendations;
using OctantPath.Core.Signals;
using Xunit;

namespace OctantPath.Core.Tests
{
    public sealed class FeedbackSummaryTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 10, 16, 10, 0, 0, DateTimeKind.Utc);

        private static SignalEvent Rec(string requestId, string preset, string hash, string stage, int minutes) => new SignalEvent
        {
            Type = SignalTypes.Recommendation, RequestId = requestId, Preset = preset, Hash = hash, Characteristic = "Standard", Difficulty = "Hard", Stage = stage, At = T0.AddMinutes(minutes),
        };

        private static SignalEvent Play(string hash, string result, int minutes) => new SignalEvent
        {
            Type = SignalTypes.Play, Hash = hash, Characteristic = "Standard", Difficulty = "Hard", Result = result, At = T0.AddMinutes(minutes),
        };

        [Fact]
        public void CountsEachTypeFromShownToCleared()
        {
            var events = new List<SignalEvent>
            {
                Play("aa", PlayResults.Clear, -5),
                Rec("r1", "song", "aa", "shown", 0),
                Rec("r1", "song", "bb", "shown", 0),
                Rec("r1", "song", "aa", "downloaded", 1),
                Play("aa", PlayResults.Clear, 2),
                Play("aa", PlayResults.Fail, 3),
                Rec("r2", null, "cc", "shown", 4),
            };
            var text = FeedbackSummary.Build(new List<RecommendationRecord>(), events, "0.0.3", T0.AddHours(1));
            Assert.Contains("| song | 1 | 2 | 0 | 1 | 0 | 1 | 1 |", text);
            Assert.Contains($"| {FeedbackSummary.UnknownPreset} | 1 | 1 | 0 | 0 | 0 | 0 | 0 |", text);
        }

        [Fact]
        public void TakesTheTypeFromTheRecordWhenTheSignalHasNone()
        {
            var records = new List<RecommendationRecord>
            {
                new RecommendationRecord
                {
                    At = T0, RequestId = "r1", Preset = "chart", Count = 30,
                    Candidates = new List<RecordedCandidate> { new RecordedCandidate { Rank = 1, Hash = "aa", Key = "1a2b", Title = "Song", Artist = "A", Mapper = "M", Characteristic = "Standard", Difficulty = "Hard" } },
                },
            };
            var text = FeedbackSummary.Build(records, new List<SignalEvent> { Rec("r1", null, "aa", "shown", 0) }, "0.0.3", T0.AddHours(1));
            Assert.Contains("| chart | 1 | 1 |", text);
            Assert.Contains("- #1 1a2b 'Song' - A [M] Standard Hard · shown", text);
        }

        [Fact]
        public void RecordsRoundTripAndSkipBrokenLines()
        {
            var dir = Path.Combine(Path.GetTempPath(), "octantpath-records-" + Guid.NewGuid().ToString("N"));
            try
            {
                RecommendationRecords.Append(dir, new RecommendationRecord { At = T0, RequestId = "r1", Preset = "song", Count = 10 });
                File.AppendAllText(Path.Combine(dir, RecommendationRecords.FileName), "{\"at\":\n");
                var read = RecommendationRecords.Read(dir);
                Assert.Single(read);
                Assert.Equal("song", read[0].Preset);
            }
            finally
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, true);
                }
            }
        }
    }
}
