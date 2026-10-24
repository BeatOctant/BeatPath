using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OctantPath.Core.Maps;
using OctantPath.Core.Settings;
using Xunit;

namespace OctantPath.Core.Tests
{
    // まとめて取るときの同時数と開始の間隔（MOD と Desktop が同じものを使う）
    public class DownloadThrottleTests
    {
        [Fact]
        public async Task StartsNoMoreThanTheConcurrencyAtOnce()
        {
            var throttle = new DownloadThrottle(new OctantPathSettings { DownloadConcurrency = 2, DownloadStartsPerSecond = 2 }, NoDelay);
            var gates = Enumerable.Range(0, 3).Select(_ => new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously)).ToList();
            var started = new List<int>();
            Func<Task<int>> Work(int i) => () =>
            {
                lock (started)
                {
                    started.Add(i);
                }
                return gates[i].Task;
            };

            var first = await throttle.StartAsync(Work(0), CancellationToken.None);
            var second = await throttle.StartAsync(Work(1), CancellationToken.None);
            var third = throttle.StartAsync(Work(2), CancellationToken.None);
            Assert.False(third.IsCompleted);
            Assert.Equal(new[] { 0, 1 }, started);

            gates[0].SetResult(10);
            Assert.Equal(10, await first);
            var thirdWork = await third;
            Assert.Equal(new[] { 0, 1, 2 }, started);
            gates[1].SetResult(11);
            gates[2].SetResult(12);
            Assert.Equal(new[] { 11, 12 }, await Task.WhenAll(second, thirdWork));
        }

        // 開始の間隔は 1 / downloadStartsPerSecond 秒（0.1 回より少なくはしない）。最初の 1 曲は待たない
        [Theory]
        [InlineData(2, 0.5)]
        [InlineData(0.5, 2)]
        [InlineData(0, 10)]
        public async Task SpacesTheStarts(double startsPerSecond, double seconds)
        {
            var delays = new List<TimeSpan>();
            var throttle = new DownloadThrottle(new OctantPathSettings { DownloadConcurrency = 5, DownloadStartsPerSecond = startsPerSecond }, (d, _) =>
            {
                delays.Add(d);
                return Task.CompletedTask;
            });
            for (var i = 0; i < 3; i++)
            {
                await await throttle.StartAsync(() => Task.FromResult(i), CancellationToken.None);
            }
            Assert.Equal(2, delays.Count);
            Assert.All(delays, d => Assert.InRange(d.TotalSeconds, seconds - 0.5, seconds));
        }

        [Fact]
        public async Task GivesTheSlotBackWhenWaitingIsCancelled()
        {
            var cancel = true;
            var throttle = new DownloadThrottle(new OctantPathSettings { DownloadConcurrency = 1, DownloadStartsPerSecond = 0.5 }, (d, _) =>
                cancel ? Task.FromCanceled(new CancellationToken(true)) : Task.CompletedTask);
            Assert.Equal(1, await await throttle.StartAsync(() => Task.FromResult(1), CancellationToken.None));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => throttle.StartAsync(() => Task.FromResult(2), CancellationToken.None));

            cancel = false;
            var next = throttle.StartAsync(() => Task.FromResult(3), CancellationToken.None);
            Assert.Same(next, await Task.WhenAny(next, Task.Delay(TimeSpan.FromSeconds(10))));
            Assert.Equal(3, await await next);
        }

        private static Task NoDelay(TimeSpan delay, CancellationToken token) => Task.CompletedTask;
    }
}
