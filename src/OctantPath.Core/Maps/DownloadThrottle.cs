using System;
using System.Threading;
using System.Threading.Tasks;
using OctantPath.Core.Settings;

namespace OctantPath.Core.Maps
{
    // まとめて取るときの、同時数と 1 秒あたりの開始数の上限（設定の downloadConcurrency と downloadStartsPerSecond）。
    // MOD と Desktop が同じものを使う。まとめ取りごとに 1 つ作る
    public sealed class DownloadThrottle
    {
        private readonly SemaphoreSlim _slots;
        private readonly TimeSpan _spacing;
        private readonly Func<TimeSpan, CancellationToken, Task> _delay;
        private DateTime _nextStart = DateTime.UtcNow;

        public DownloadThrottle(OctantPathSettings settings, Func<TimeSpan, CancellationToken, Task> delay = null)
        {
            _slots = new SemaphoreSlim(Math.Max(1, settings.DownloadConcurrency));
            _spacing = TimeSpan.FromSeconds(1 / Math.Max(0.1, settings.DownloadStartsPerSecond));
            _delay = delay ?? Task.Delay;
        }

        // 空きと開始の間隔を待ってから work を始め、その Task を返す（終わるのは待たない）。work が終わったら空きを返す。
        // 呼んだ側のスレッドで work を始めるため、ConfigureAwait(false) にしない（MOD は Unity のメインスレッドで始める）
        public async Task<Task<T>> StartAsync<T>(Func<Task<T>> work, CancellationToken cancellationToken)
        {
            await _slots.WaitAsync(cancellationToken);
            var wait = _nextStart - DateTime.UtcNow;
            if (wait > TimeSpan.Zero)
            {
                try
                {
                    await _delay(wait, cancellationToken);
                }
                catch
                {
                    _slots.Release();
                    throw;
                }
            }
            _nextStart = DateTime.UtcNow + _spacing;
            return RunAsync(work);
        }

        private async Task<T> RunAsync<T>(Func<Task<T>> work)
        {
            try
            {
                return await work();
            }
            finally
            {
                _slots.Release();
            }
        }
    }
}
