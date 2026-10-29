using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OctantPath.Connection;
using OctantPath.Core.Connection;
using OctantPath.Core.Maps;
using OctantPath.Core.Presets;
using OctantPath.Core.Recommendations;
using OctantPath.Core.Signals;
using OctantPath.Installers;
using OctantPath.Maps;
using OctantPath.Recommendations;
using Zenject;
using OctantPath.Core.Languages;

namespace OctantPath.Trials
{
    // お試し枠を埋める。枠ごとに、枠が名指しする手札で推薦を受け取り、上位から枠が埋まるまで取る（実装構想の「マップの入れ替え」）。
    // メニューにいて、OctantCompass が ready で、自動ダウンロードが入のときだけ動く
    internal sealed class TrialService : IInitializable, IDisposable
    {
        private readonly ConnectionService _connection;
        private readonly RecommendationService _recommendations;
        private readonly DownloadService _downloads;
        private readonly OwnershipLedger _ledger;
        private readonly SettingsHolder _settings;
        private readonly PlaylistSync _playlist;
        private readonly Signals.SignalService _signals;
        private readonly GameScenesManager _gameScenesManager;
        private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();
        private bool _running;
        private bool _filledThisSession;

        public TrialService(ConnectionService connection, RecommendationService recommendations, DownloadService downloads, OwnershipLedger ledger, SettingsHolder settings, PlaylistSync playlist, Signals.SignalService signals, GameScenesManager gameScenesManager)
        {
            _connection = connection;
            _recommendations = recommendations;
            _downloads = downloads;
            _ledger = ledger;
            _settings = settings;
            _playlist = playlist;
            _signals = signals;
            _gameScenesManager = gameScenesManager;
        }

        public string Status { get; private set; } = "";

        public event Action StatusChanged;

        public void Initialize()
        {
            _connection.StatusChanged += HandleConnectionChanged;
            _connection.PresetsChanged += HandlePresetsChanged;
            SongCore.Loader.SongsLoadedEvent += HandleSongsLoaded;
            TryStart();
        }

        public void Dispose()
        {
            _lifetime.Cancel();
            _connection.StatusChanged -= HandleConnectionChanged;
            _connection.PresetsChanged -= HandlePresetsChanged;
            SongCore.Loader.SongsLoadedEvent -= HandleSongsLoaded;
        }

        private void HandleConnectionChanged(ConnectionStatus status) => TryStart();

        private void HandlePresetsChanged() => TryStart();

        private void HandleSongsLoaded(SongCore.Loader loader, System.Collections.Concurrent.ConcurrentDictionary<string, BeatmapLevel> levels) => TryStart();

        // 起動ごとに 1 回、枠を埋める。枠は手札を名指しするので、手札の一覧を受け取ってから始める。
        // 取れなかったときは、取り直して受け取れるまで待つ（一覧なしで始めると、どの枠も埋められずにその回を使ってしまう）
        private void TryStart()
        {
            if (_running || _filledThisSession || !SongCore.Loader.AreSongsLoaded || _connection.Status.State != ConnectionState.Ready || _gameScenesManager.InGameplay()
                || _connection.Presets == null)
            {
                return;
            }
            _ = FillAsync();
        }

        private async Task FillAsync()
        {
            _running = true;
            try
            {
                var settings = _settings.Current;
                _playlist.Rebuild(_ledger, settings);
                if (!settings.AutoDownload)
                {
                    // 切ったら新しく取らない。すでに入れたマップも消さない（OctantPath 法 2.7）
                    SetStatus(Lang.T("trial.autoDownloadOff"));
                    _filledThisSession = true;
                    return;
                }
                var ledger = _ledger.Load();
                if (ledger == null)
                {
                    SetStatus(Lang.T("trial.ledgerUnreadable"));
                    _filledThisSession = true;
                    return;
                }

                var total = 0;
                foreach (var frame in settings.TrialFrames)
                {
                    var count = frame.Count ?? settings.TrialSlotCount;
                    if (count <= 0)
                    {
                        continue;
                    }
                    var skip = SkipReason(frame, out var preset);
                    if (skip != null)
                    {
                        Plugin.Log.Info($"[trial] frame {frame.Id}: {skip}; skipped");
                        continue;
                    }
                    ledger = _ledger.Load() ?? ledger;
                    var current = ledger.Maps.Count(m => m.State == MapStates.Trial && m.FrameIds().Contains(frame.Id));
                    var need = count - current;
                    if (need <= 0)
                    {
                        continue;
                    }
                    SetStatus(Lang.T("trial.filling", new { frame = frame.DisplayName, current, count }));
                    // 起点は送らない。値のない choice も送らない
                    var choices = frame.Choices?.Where(c => !string.IsNullOrEmpty(c.Value)).ToDictionary(c => c.Key, c => c.Value);
                    var request = PresetRules.Request(preset, null, choices, need, settings.DifficultyCeiling);
                    request.Exclude = Excluded(ledger);
                    total += await FillFrameAsync(frame.Id, need, request).ConfigureAwait(true);
                }
                _playlist.Rebuild(_ledger, settings);
                SetStatus(total > 0 ? Lang.T("trial.added", new { total }) : Lang.T("trial.full"));
                _filledThisSession = true;
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Plugin.Log.Error($"[trial] {ex}");
                SetStatus(Lang.T("trial.failed", new { error = ex.Message }));
            }
            finally
            {
                _running = false;
            }
        }

        // 枠を使えない理由。使えるなら null で、名指しした手札を preset に入れる
        private string SkipReason(Core.Settings.TrialFrameSettings frame, out Preset preset)
        {
            // OctantCompass の一覧にない手札は使わない（DB が自分の手札を持たなければ、一覧は OctantVector の既定の「すべて」だけ）
            preset = string.IsNullOrEmpty(frame.Preset) ? null : _connection.Presets?.Presets?.FirstOrDefault(p => p.Id == frame.Preset);
            if (preset == null)
            {
                return string.IsNullOrEmpty(frame.Preset) ? "no hand is named" : $"hand '{frame.Preset}' is not in the hands OctantCompass offers";
            }
            return PresetRules.Unmet(preset, frame.Choices);
        }

        private async Task<int> FillFrameAsync(string frameId, int need, RecommendationRequest request)
        {
            var list = await _recommendations.RequestAsync(request, _lifetime.Token).ConfigureAwait(true);
            if (list.Error != null)
            {
                Plugin.Log.Warn($"[trial] frame {frameId}: {list.Error}");
                return 0;
            }
            Plugin.Log.Info($"[trial] frame {frameId}: preset={request.Preset} requestId={list.Response.RequestId} candidates={list.Items.Count}");

            // すでにお試し中のマップが別の枠に出たら、その枠にも入れる（曲ファイルは 1 つ）
            var ledger = _ledger.Load();
            var trial = new HashSet<string>(ledger?.Maps.Where(m => m.State == MapStates.Trial).Select(m => m.Hash) ?? Enumerable.Empty<string>());
            var added = 0;
            var toDownload = new List<RecommendationItem>();
            foreach (var item in list.Items)
            {
                if (added + toDownload.Count >= need)
                {
                    break;
                }
                if (item.Hash == null)
                {
                    continue;
                }
                if (trial.Contains(item.Hash))
                {
                    if (_ledger.AddFrame(item.Hash, DownloadService.Frame(item, frameId, DateTime.UtcNow)))
                    {
                        added++;
                    }
                    continue;
                }
                // 順は OctantCompass が返した順のまま。取れないもの（導入済み、BeatSaver にない）だけを外す（憲法の原則 6）
                if (item.Installed || !_downloads.IsAvailable(item))
                {
                    continue;
                }
                toDownload.Add(item);
            }
            if (toDownload.Count == 0)
            {
                return added;
            }
            var outcomes = await _downloads.InstallManyAsync(toDownload, frameId, true, _lifetime.Token).ConfigureAwait(true);
            return added + (outcomes?.Count(o => o.Error == null && o.Path != null) ?? 0);
        }

        // 出さないもの: SongCore が読み込んだマップ（お試し中のものは別の枠に出てよいので除く）、
        // OctantPath が消したか残したマップ、Downvote したマップ
        private List<ExcludeEntry> Excluded(LedgerFile ledger)
        {
            var trial = new HashSet<string>(ledger.Maps.Where(m => m.State == MapStates.Trial).Select(m => m.Hash));
            var hashes = new HashSet<string>();
            foreach (var levelId in SongCore.Loader.CustomLevels.Values.Select(l => l.levelID))
            {
                var hash = LevelIds.Hash(levelId);
                if (hash != null && !trial.Contains(hash))
                {
                    hashes.Add(hash);
                }
            }
            var reasons = new Dictionary<string, string>();
            foreach (var map in ledger.Maps.Where(m => m.State != MapStates.Trial))
            {
                hashes.Add(map.Hash);
                reasons[map.Hash] = map.State;
            }
            foreach (var pair in _signals.LastVotes().Where(p => p.Value == SignalTypes.Downvote))
            {
                hashes.Add(pair.Key);
                reasons[pair.Key] = "downvoted";
            }
            return hashes.OrderBy(h => h, StringComparer.Ordinal).Select(h => new ExcludeEntry { Hash = h, Reason = reasons.TryGetValue(h, out var r) ? r : "installed" }).ToList();
        }

        private void SetStatus(string status)
        {
            Status = status;
            Plugin.Log.Info($"[trial] {status}");
            StatusChanged?.Invoke();
        }
    }
}
