using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OctantPath.Core.Maps;
using OctantPath.Installers;
using OctantPath.Core.Recommendations;
using Zenject;
using OctantPath.Core.Languages;

namespace OctantPath.Maps
{
    // 推薦から選んだマップを取り、専用フォルダに置く。置いたことは placed.json に残し、
    // SongCore の読み込みで譜面が解決できたのを確かめてから所有を記録し、専用プレイリストに入れる
    // （OctantPath 法 2.1〜2.5、3.1、3.7）。
    // 読み直し（曲の一覧が作り直され、曲選択の選択が外れる）は、頼まれたときだけ行う
    internal sealed class DownloadService : IInitializable, IDisposable
    {
        private readonly MapInstaller _installer;
        private readonly OctantPathFolders _folders;
        private readonly OwnershipLedger _ledger;
        private readonly PlacedStore _placed;
        private readonly PlaylistSync _playlist;
        private readonly GameScenesManager _gameScenesManager;
        private readonly Signals.SignalService _signals;
        private readonly SettingsHolder _settings;
        private readonly SemaphoreSlim _one = new SemaphoreSlim(1, 1);
        private readonly HashSet<string> _inFlight = new HashSet<string>();

        public DownloadService(MapInstaller installer, OctantPathFolders folders, OwnershipLedger ledger, PlaylistSync playlist, GameScenesManager gameScenesManager, Signals.SignalService signals, SettingsHolder settings)
        {
            _installer = installer;
            _folders = folders;
            _ledger = ledger;
            _placed = new PlacedStore(folders.Root);
            _playlist = playlist;
            _gameScenesManager = gameScenesManager;
            _signals = signals;
            _settings = settings;
        }

        public event Action Changed;

        public void Initialize()
        {
            // 起動時の読み込みを含め、読み込みが終わるたびに、置いたものを確かめる
            SongCore.Loader.SongsLoadedEvent += HandleSongsLoaded;
        }

        public void Dispose()
        {
            SongCore.Loader.SongsLoadedEvent -= HandleSongsLoaded;
        }

        public bool IsInFlight(string hash) => hash != null && _inFlight.Contains(hash);

        // 置いたが、まだ読み込みを確かめていない
        public bool IsPlaced(string hash) => hash != null && _placed.Load().Any(p => p.Hash == hash);

        // 今の取得元から取れるか。取得元がローカルのフォルダなら、そこに zip があるものだけ（OctantPath 法 2.8）
        public bool IsAvailable(RecommendationItem item) => ZipSources.CanFetch(_settings.Current.ZipSource, item.Hash, item.Version);

        // まとめて取る。取るのは同時数と 1 秒あたりの開始数の上限の中。ほかの取得の途中なら、終わるのを待つ。
        // refreshNow が真なら、最後に SongCore を 1 回読み直して確かめる。偽なら置くところまで（確かめるのは次の読み込み）。
        // プレイ中なら null
        // progress は 1 曲終わるごとに（終わった数, 全体の数）で呼ぶ（メインスレッドで）。
        // received は BeatSaver から受け取った割合（曲, 0〜1）で呼ぶ（呼ぶスレッドは決まっていない）
        public async Task<List<InstallOutcome>> InstallManyAsync(IReadOnlyList<RecommendationItem> items, string frameId, bool refreshNow, CancellationToken cancellationToken, Action<int, int> progress = null, Action<RecommendationItem, double> received = null)
        {
            if (_gameScenesManager.InGameplay())
            {
                return null;
            }
            foreach (var item in items)
            {
                _inFlight.Add(item.Hash);
            }
            Changed?.Invoke();
            await _one.WaitAsync(cancellationToken).ConfigureAwait(true);
            try
            {
                var throttle = new DownloadThrottle(_settings.Current);
                var tasks = new List<Task<InstallOutcome>>();
                var finished = 0;
                async Task<InstallOutcome> Track(Task<InstallOutcome> task)
                {
                    var outcome = await task.ConfigureAwait(true);
                    finished++;
                    progress?.Invoke(finished, items.Count);
                    return outcome;
                }
                foreach (var item in items)
                {
                    if (item.Installed || IsPlaced(item.Hash))
                    {
                        // 先に頼まれた取得で、もう置かれている
                        tasks.Add(Track(Task.FromResult(new InstallOutcome { Item = item, AlreadyThere = true })));
                        continue;
                    }
                    tasks.Add(Track(await throttle.StartAsync(() => InstallOneAsync(item, frameId, cancellationToken, received), cancellationToken).ConfigureAwait(true)));
                }
                var outcomes = (await Task.WhenAll(tasks).ConfigureAwait(true)).ToList();
                if (refreshNow && outcomes.Any(o => o.Path != null || o.AlreadyThere))
                {
                    await RefreshAsync(cancellationToken).ConfigureAwait(true);
                    foreach (var outcome in outcomes)
                    {
                        outcome.Item.Installed = LevelLookup.IsInstalled(outcome.Item.Hash);
                    }
                }
                foreach (var outcome in outcomes.Where(o => o.Path != null || o.AlreadyThere))
                {
                    if (!outcome.Item.Installed && refreshNow)
                    {
                        outcome.Error = outcome.Error ?? Lang.T("download.songCoreFailed");
                    }
                }
                return outcomes;
            }
            finally
            {
                foreach (var item in items)
                {
                    _inFlight.Remove(item.Hash);
                }
                _one.Release();
                Changed?.Invoke();
            }
        }

        // SongCore を読み直し、置いたものを確かめる。メニューにいるときだけ
        public async Task RefreshAsync(CancellationToken cancellationToken)
        {
            if (_gameScenesManager.InGameplay())
            {
                return;
            }
            var loaded = new TaskCompletionSource<bool>();
            void OnLoaded(SongCore.Loader loader, System.Collections.Concurrent.ConcurrentDictionary<string, BeatmapLevel> levels) => loaded.TrySetResult(true);
            SongCore.Loader.SongsLoadedEvent += OnLoaded;
            try
            {
                if (SongCore.Loader.AreSongsLoading)
                {
                    await loaded.Task.ConfigureAwait(true);
                    loaded = new TaskCompletionSource<bool>();
                }
                SongCore.Loader.Instance.RefreshSongs(false);
                using (cancellationToken.Register(() => loaded.TrySetCanceled()))
                {
                    await loaded.Task.ConfigureAwait(true);
                }
            }
            finally
            {
                SongCore.Loader.SongsLoadedEvent -= OnLoaded;
            }
        }

        public bool HasPlaced => _placed.Load().Count > 0;

        private async Task<InstallOutcome> InstallOneAsync(RecommendationItem item, string frameId, CancellationToken cancellationToken, Action<RecommendationItem, double> received)
        {
            var outcome = new InstallOutcome { Item = item };
            try
            {
                var folderName = MapInstaller.FolderName(item.Map?.Key ?? item.Hash.Substring(0, 8), item.Title, item.Mapper);
                // 取得元がローカルのフォルダなら、そこにないマップは BeatSaver から取らずに飛ばす（OctantPath 法 2.8）
                var installed = await _installer.InstallFromSourceAsync(item.Hash, item.Version, folderName, _folders.Levels, _folders.Work, cancellationToken, received == null ? (Action<double>)null : f => received(item, f)).ConfigureAwait(true);
                if (!installed.Succeeded)
                {
                    outcome.Error = installed.Error;
                    Plugin.Log.Warn($"[download] {item.Hash}: {installed.Error}");
                    return outcome;
                }
                outcome.Path = installed.Path;
                _placed.Add(new PlacedMap
                {
                    Hash = item.Hash,
                    MapKey = item.Map?.Key,
                    Folder = Path.GetFileName(installed.Path),
                    Frame = Frame(item, frameId, DateTime.UtcNow),
                    PlacedAt = DateTime.UtcNow,
                });
                Plugin.Log.Info($"[download] placed {item.Hash} at {installed.Path}");
                return outcome;
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                Plugin.Log.Error($"[download] {item.Hash}: {ex}");
                outcome.Error = ex.Message;
                return outcome;
            }
        }

        // 置いたものの読み込みを確かめ、確かめられたものだけ所有を記録する（OctantPath 法 2.5、3.1）
        private void HandleSongsLoaded(SongCore.Loader loader, System.Collections.Concurrent.ConcurrentDictionary<string, BeatmapLevel> levels)
        {
            try
            {
                ConfirmPlaced();
            }
            catch (Exception ex)
            {
                Plugin.Log.Error($"[download] confirming placed maps failed: {ex}");
            }
        }

        private void ConfirmPlaced()
        {
            var placed = _placed.Load();
            if (placed.Count == 0)
            {
                return;
            }
            var done = new List<PlacedMap>();
            var confirmed = 0;
            foreach (var map in placed)
            {
                var folder = Path.Combine(_folders.Levels, map.Folder);
                if (!Directory.Exists(folder))
                {
                    done.Add(map);
                    continue;
                }
                var level = LevelLookup.FindInSeparateFolder(_folders.Levels, folder, map.Hash);
                if (level == null)
                {
                    continue;
                }
                if (map.Frame != null && !LevelLookup.HasChart(level, map.Frame.Characteristic, map.Frame.Difficulty))
                {
                    Plugin.Log.Warn($"[download] {map.Hash}: {map.Frame.Characteristic} {map.Frame.Difficulty} is not in the map");
                }
                var recorded = _ledger.Add(new OwnedMap
                {
                    Hash = map.Hash,
                    MapKey = map.MapKey,
                    Folder = map.Folder,
                    AcquiredAt = DateTime.UtcNow,
                    Frames = map.Frame != null ? new List<FrameEntry> { map.Frame } : new List<FrameEntry>(),
                });
                if (!recorded)
                {
                    // 所有の記録が読めない: OctantPath のものとして記録しない。置いた記録は残して次に回す（OctantPath 法 3.2）
                    Plugin.Log.Warn("[download] ledger.json could not be read; ownership was not recorded");
                    continue;
                }
                if (map.Frame != null)
                {
                    _signals.RecordRecommendation(map.Frame.RequestId, map.Frame.Preset, map.Hash, map.Frame.Characteristic, map.Frame.Difficulty, "downloaded");
                }
                done.Add(map);
                confirmed++;
                Plugin.Log.Info($"[download] ready {map.Hash} frame={map.Frame?.Frame} requestId={map.Frame?.RequestId}");
            }
            if (done.Count > 0)
            {
                _placed.Remove(done);
            }
            if (confirmed > 0)
            {
                _playlist.Rebuild(_ledger, _settings.Current);
            }
            Changed?.Invoke();
        }

        public static FrameEntry Frame(RecommendationItem item, string frameId, DateTime at) => new FrameEntry
        {
            Frame = frameId,
            RequestId = item.RequestId,
            Preset = item.Preset,
            Characteristic = item.Candidate.Characteristic,
            Difficulty = item.Candidate.Difficulty,
            At = at,
        };
    }

    internal sealed class InstallOutcome
    {
        public RecommendationItem Item { get; set; }
        public string Path { get; set; }
        public string Error { get; set; }
        public bool AlreadyThere { get; set; }
    }
}
