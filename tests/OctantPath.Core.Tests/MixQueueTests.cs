using System.Linq;
using System.Collections.Generic;
using OctantPath.Core.Mix;
using OctantPath.Core.Presets;
using OctantPath.Core.Recommendations;
using Xunit;

namespace OctantPath.Core.Tests
{
    public class MixQueueTests
    {
        private static MixQueue<string> Queue(params string[] items)
        {
            var installed = new HashSet<string> { "b", "d" };
            return new MixQueue<string>(items, installed.Contains);
        }

        [Fact]
        public void KeepsTheOrderAndFiltersOnlyForDisplay()
        {
            var q = Queue("a", "b", "c", "d");
            Assert.Equal(new[] { "a", "b", "c", "d" }, q.Visible());
            q.Filter = MixFilter.Familiar;
            Assert.Equal(new[] { "b", "d" }, q.Visible());
            q.Filter = MixFilter.New;
            Assert.Equal(new[] { "a", "c" }, q.Visible());
            Assert.Equal(new[] { "a", "b", "c", "d" }, q.All);
        }

        [Fact]
        public void AdvancesWithinTheFilter()
        {
            var q = Queue("a", "b", "c", "d");
            q.Select("a");
            Assert.Equal("b", q.Next());
            q.Filter = MixFilter.New;
            Assert.Equal("c", q.Next());
            q.Select("c");
            Assert.Null(q.Next());
        }

        [Fact]
        public void PrefetchesNotInstalledFromTheCurrentSong()
        {
            var q = Queue("a", "b", "c", "d", "e", "f");
            Assert.Equal(new[] { "a", "c" }, q.ToPrefetch(2));
            q.Select("c");
            Assert.Equal(new[] { "c", "e", "f" }, q.ToPrefetch(5));
        }
    
        [Fact]
        public void InsertsAfterTheCurrentSong()
        {
            var q = Queue("a", "b", "c", "d");
            q.Select("b");
            q.InsertNext("x");
            Assert.Equal(new[] { "a", "b", "x", "c", "d" }, q.All);
            Assert.Equal("x", q.Next());
            q.InsertNext("d");
            Assert.Equal(new[] { "a", "b", "d", "x", "c" }, q.All);
            Assert.Equal("d", q.Next());
        }

        [Fact]
        public void DraftTopsUpTheReserveWithoutRepeats()
        {
            var draft = new MixDraft<string>(new[] { "a", "b", "c" }, 2);
            draft.Replace("a");
            Assert.Equal(0, draft.ReserveCount);
            Assert.Equal(2, draft.AddReserve(new[] { "a", "b", "c", "d", "e" }));
            Assert.Equal("d", draft.Replace("b"));
            Assert.Equal(new[] { "a", "b", "c", "d", "e" }.OrderBy(x => x), draft.Seen.OrderBy(x => x));
        }

        [Fact]
        public void DraftPicksTheFirstAndReplacesFromTheRestInOrder()
        {
            var draft = new MixDraft<string>(new[] { "a", "b", "c", "d", "e" }, 3);
            Assert.Equal(new[] { "a", "b", "c" }, draft.Picked);
            Assert.Equal("d", draft.Replace("b"));
            Assert.Equal(new[] { "a", "d", "c" }, draft.Picked);
            Assert.Equal("e", draft.Replace("a"));
            Assert.Null(draft.Replace("c"));
            Assert.Equal(new[] { "e", "d" }, draft.Picked);
            Assert.Equal(new[] { "b", "a", "c" }, draft.Removed);
            Assert.Null(draft.Replace("zzz"));
        }

        [Fact]
        public void DraftRunsLowBelowThreeInReserve()
        {
            var draft = new MixDraft<string>(new[] { "a", "b", "c", "d", "e" }, 2);
            Assert.False(draft.RunningLow);
            draft.Replace("a");
            Assert.True(draft.RunningLow);
            Assert.Equal(2, draft.ReserveCount);
        }

        // 下見の数の決まり（MOD と Desktop が同じものを使う）: 並べる数の 3 倍を受け取り、足すときは 2 倍
        [Fact]
        public void AsksForThreeTimesTheDraftAndTwiceForMore()
        {
            var preset = new Preset { Id = "song", Inputs = new List<PresetInput> { new PresetInput { Kind = PresetInput.Seed } } };
            var seed = new ChartRef { Hash = new string('a', 40), Characteristic = "Standard", Difficulty = "Hard" };
            var choices = new Dictionary<string, string> { ["genre"] = "rock" };
            var size = MixDraftRules.Size(10);

            var first = MixDraftRules.FirstRequest(preset, seed, choices, size);
            Assert.Equal(30, first.Count);
            Assert.Equal("song", first.Preset);
            Assert.Same(seed, first.Seed);
            Assert.Equal("rock", first.Choices["genre"]);
            Assert.Null(first.Exclude);

            var more = MixDraftRules.TopUpRequest(preset, seed, choices, size, new[] { "b", null, "a", "b" });
            Assert.Equal(20, more.Count);
            Assert.Same(seed, more.Seed);
            Assert.Equal(new[] { "b", "a" }, more.Exclude.Select(e => e.Hash));
            Assert.All(more.Exclude, e => Assert.Equal("in_draft", e.Reason));
        }

        [Fact]
        public void DraftRemovesWithoutSwappingAndAddsFromTheReserveAtTheEnd()
        {
            var draft = new MixDraft<string>(new[] { "a", "b", "c", "d", "e" }, 3);
            Assert.True(draft.Remove("b"));
            Assert.False(draft.Remove("b"));
            Assert.Equal(new[] { "a", "c" }, draft.Picked);
            Assert.Equal(new[] { "b" }, draft.Removed);
            Assert.Equal("d", draft.Add());
            Assert.Equal("e", draft.Add());
            Assert.Null(draft.Add());
            Assert.Equal(new[] { "a", "c", "d", "e" }, draft.Picked);
            Assert.Equal(new[] { "a", "b", "c", "d", "e" }.OrderBy(x => x), draft.Seen.OrderBy(x => x));
        }

        [Fact]
        public void DraftAddsUpToTheMaximum()
        {
            var draft = new MixDraft<int>(Enumerable.Range(0, MixDraftRules.MaxSize + 5), MixDraftRules.MaxSize - 1);
            Assert.False(draft.Full);
            Assert.Equal(MixDraftRules.MaxSize - 1, draft.Add());
            Assert.True(draft.Full);
            Assert.Equal(0, draft.Add());
            Assert.Equal(MixDraftRules.MaxSize, draft.Picked.Count);
            Assert.Equal(5, draft.ReserveCount);
        }

        [Theory]
        [InlineData(10, 10)]
        [InlineData(1, 1)]
        [InlineData(0, 1)]
        [InlineData(-3, 1)]
        public void DraftHasAtLeastOneSong(int setting, int size)
        {
            Assert.Equal(size, MixDraftRules.Size(setting));
        }
    }
}
