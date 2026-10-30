using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OctantPath.Connection;
using OctantPath.Core.Connection;
using OctantPath.Core.Maps;
using OctantPath.Core.Signals;
using OctantPath.Installers;
using OctantPath.Maps;
using IPA.Utilities;
using Newtonsoft.Json.Linq;
using Zenject;
using OctantPath.Core.Languages;

namespace OctantPath.Signals
{
    // signal を集めて正本に残し、OctantCompass に送る（契約3（OctantPath to OctantCompass）の /v1/signals）。
    // プレイ中は書かず送らず、メニューに戻ってからまとめて残す
    internal sealed class SignalService : IInitializable, IDisposable
    {
        private readonly GameScenesManager _gameScenesManager;
        private readonly PlayerDataModel _playerDataModel;
        private readonly ConnectionService _connection;
        private readonly OwnershipLedger _ledger;
        private readonly OctantPathFolders _folders;
        private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();
        private readonly List<SignalEvent> _pending = new List<SignalEvent>();
        private readonly SemaphoreSlim _syncLock = new SemaphoreSlim(1, 1);

        private SignalLog _log;
        private StandardLevelScenesTransitionSetupDataSO _subscribed;
        // つないでいる OctantCompass の起動（/v1/health の instanceId。null なら確かめ直すまで送らない）と、
        // その起動が持っている件数（正本の先頭から。null なら丸ごと送り直す）
        private string _hostInstance;
        private int? _sent;
        private bool _stopped;

        public SignalService(GameScenesManager gameScenesManager, PlayerDataModel playerDataModel, ConnectionService connection, OwnershipLedger ledger, OctantPathFolders folders)
        {
            _gameScenesManager = gameScenesManager;
            _playerDataModel = playerDataModel;
            _connection = connection;
            _ledger = ledger;
            _folders = folders;
        }

        public string Problem { get; private set; }

        // signals.jsonl の event（推薦のまとめが読む）。読めなかったときは空
        public IReadOnlyList<SignalEvent> Events => _log?.Events ?? new List<SignalEvent>();

        public event Action ProblemChanged;

        public void Initialize()
        {
            _log = SignalLog.Open(_folders.Root, out var error);
            if (_log == null)
            {
                Stop(Lang.T("signals.logUnreadable", new { error }));
                return;
            }
            Plugin.Log.Info($"[signals] {_log.Count} events in signals.jsonl");
            _gameScenesManager.transitionDidFinishEvent += HandleTransitionDidFinish;
            _playerDataModel.playerData.favoriteLevelsSetDidChangeEvent += HandleFavoritesChanged;
            _connection.StatusChanged += HandleConnectionChanged;
            SongCore.Loader.SongsLoadedEvent += HandleSongsLoaded;
            ReadHost(_connection.Status);
            if (SongCore.Loader.AreSongsLoaded)
            {
                HandleSongsLoaded(SongCore.Loader.Instance, null);
            }
        }

        public void Dispose()
        {
            _lifetime.Cancel();
            if (_log == null)
            {
                return;
            }
            _gameScenesManager.transitionDidFinishEvent -= HandleTransitionDidFinish;
            _playerDataModel.playerData.favoriteLevelsSetDidChangeEvent -= HandleFavoritesChanged;
            _connection.StatusChanged -= HandleConnectionChanged;
            SongCore.Loader.SongsLoadedEvent -= HandleSongsLoaded;
            if (_subscribed != null)
            {
                _subscribed.didFinishEvent -= HandleLevelDidFinish;
            }
        }

        public Dictionary<string, string> LastVotes() => _log?.Votes() ?? new Dictionary<string, string>();

        // 推薦のその後の出来事（shown、downloaded など）
        public void RecordRecommendation(string requestId, string preset, string hand, string hash, string characteristic, string difficulty, string stage)
        {
            Record(new SignalEvent
            {
                Type = SignalTypes.Recommendation,
                RequestId = requestId,
                Preset = preset,
                Hand = hand,
                Hash = MapHash.Normalize(hash),
                Characteristic = characteristic,
                Difficulty = difficulty,
                Stage = stage,
                At = DateTime.UtcNow,
            });
        }

        private void Record(SignalEvent e)
        {
            if (_stopped || _log == null || e.Hash == null)
            {
                return;
            }
            _pending.Add(e);
            if (!_gameScenesManager.InGameplay())
            {
                FlushAndSync();
            }
        }

        private void HandleSongsLoaded(SongCore.Loader loader, System.Collections.Concurrent.ConcurrentDictionary<string, BeatmapLevel> levels)
        {
            // 最初の読み込みのあとに一度だけ: 導入前の記録の取り込みと、起動していない間の変化
            SongCore.Loader.SongsLoadedEvent -= HandleSongsLoaded;
            if (_stopped)
            {
                return;
            }
            ImportOnce();
            CollectMenuChanges();
            FlushAndSync();
        }

        private void HandleTransitionDidFinish(GameScenesManager.SceneTransitionType type, ScenesTransitionSetupDataSO setupData, DiContainer container)
        {
            if (setupData is StandardLevelScenesTransitionSetupDataSO standard && _subscribed != standard)
            {
                if (_subscribed != null)
                {
                    _subscribed.didFinishEvent -= HandleLevelDidFinish;
                }
                standard.didFinishEvent += HandleLevelDidFinish;
                _subscribed = standard;
            }
            if (type == GameScenesManager.SceneTransitionType.Pop && !_gameScenesManager.InGameplay())
            {
                // メニューに戻った: 投票（結果画面を閉じたあと）、お気に入りの変化も見る
                CollectMenuChanges();
                FlushAndSync();
            }
        }

        private void HandleLevelDidFinish(StandardLevelScenesTransitionSetupDataSO setupData, LevelCompletionResults results)
        {
            try
            {
                var play = PlayEvent(setupData, results);
                if (play != null)
                {
                    // プレイ中なので、ここでは書かない
                    _pending.Add(play);
                }
            }
            catch (Exception ex)
            {
                // 観測に失敗してもゲームを妨げない（OctantPath 法 4.5）
                Plugin.Log.Warn($"[signals] play could not be observed: {ex.Message}");
            }
        }

        private static SignalEvent PlayEvent(StandardLevelScenesTransitionSetupDataSO setupData, LevelCompletionResults results)
        {
            if (results == null || setupData.practiceSettings != null)
            {
                // 練習モードは今は送らない（契約3）
                return null;
            }
            var key = setupData.beatmapKey;
            var hash = LevelIds.Hash(key.levelId);
            var result = PlayResults.From(results.levelEndStateType.ToString(), results.levelEndAction.ToString());
            if (hash == null || result == null || key.beatmapCharacteristic == null)
            {
                return null;
            }
            var e = new SignalEvent
            {
                Type = SignalTypes.Play,
                Hash = hash,
                Characteristic = key.beatmapCharacteristic.serializedName,
                Difficulty = key.difficulty.ToString(),
                Result = result,
                Modifiers = Modifiers(results.gameplayModifiers ?? setupData.gameplayModifiers),
                At = DateTime.UtcNow,
            };
            if (result == PlayResults.Clear || result == PlayResults.Fail)
            {
                e.Accuracy = Accuracy(setupData, results);
            }
            return e;
        }

        // 精度は、修飾をかける前のスコアを、その譜面の最大のスコアで割ったもの。取れなければ省く（OctantPath 法 4.4）
        private static double? Accuracy(StandardLevelScenesTransitionSetupDataSO setupData, LevelCompletionResults results)
        {
            try
            {
                var data = setupData.transformedBeatmapData;
                if (data == null)
                {
                    return null;
                }
                var max = ScoreModel.ComputeMaxMultipliedScoreForBeatmap(data);
                return max > 0 ? Math.Round((double)results.multipliedScore / max, 4) : (double?)null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static Dictionary<string, JToken> Modifiers(GameplayModifiers m)
        {
            if (m == null)
            {
                return null;
            }
            var modifiers = new Dictionary<string, JToken>
            {
                ["speed"] = Math.Round(m.songSpeedMul, 3),
                ["noFail"] = m.noFailOn0Energy,
            };
            // 契約3 の例にない修飾も、付いているものは落とさずに送る
            void Flag(string name, bool on)
            {
                if (on)
                {
                    modifiers[name] = true;
                }
            }
            Flag("instaFail", m.instaFail);
            Flag("batteryEnergy", m.energyType == GameplayModifiers.EnergyType.Battery);
            Flag("failOnSaberClash", m.failOnSaberClash);
            Flag("noObstacles", m.enabledObstacleType == GameplayModifiers.EnabledObstacleType.NoObstacles);
            Flag("noBombs", m.noBombs);
            Flag("fastNotes", m.fastNotes);
            Flag("strictAngles", m.strictAngles);
            Flag("disappearingArrows", m.disappearingArrows);
            Flag("noArrows", m.noArrows);
            Flag("ghostNotes", m.ghostNotes);
            Flag("proMode", m.proMode);
            Flag("zenMode", m.zenMode);
            Flag("smallCubes", m.smallCubes);
            return modifiers;
        }

        private void HandleFavoritesChanged()
        {
            if (!_gameScenesManager.InGameplay())
            {
                CollectFavorites(null);
                FlushAndSync();
            }
        }

        private void HandleConnectionChanged(ConnectionStatus status)
        {
            if (ReadHost(status))
            {
                FlushAndSync();
            }
        }

        // OctantCompass の起動が変わった（起動し直した、別の OctantCompass につないだ）か、持っている件数がこちらの覚えと違えば、丸ごと送り直す
        private bool ReadHost(ConnectionStatus status)
        {
            if (status.HostInstance == null)
            {
                _hostInstance = null;
                return false;
            }
            if (status.HostInstance != _hostInstance || status.Signals != _sent)
            {
                _hostInstance = status.HostInstance;
                _sent = null;
            }
            return true;
        }

        private void ImportOnce()
        {
            var state = ImportState.Load(_folders.Root, out var readable);
            if (!readable)
            {
                Plugin.Log.Warn("[signals] state.json could not be read; not importing");
                return;
            }
            if (state.ImportedAt != null)
            {
                return;
            }
            var at = DateTime.UtcNow;

            CollectFavorites("PlayerData");
            CollectVotes("votedSongs");

            // PlayerData のプレイ回数。途中終了も含むが、初めの手がかりとして取り込む（契約1（OctantCompass to OctantVector）の好みの判定ルール）。
            // スコアがあれば（validScore）一度はクリアした譜面として cleared を付ける。日付と修飾は分からない
            foreach (var stats in _playerDataModel.playerData.levelsStatsData.Values)
            {
                var hash = LevelIds.Hash(stats.levelID);
                if (hash == null || stats.playCount <= 0 || stats.beatmapCharacteristic == null)
                {
                    continue;
                }
                _pending.Add(new SignalEvent
                {
                    Type = SignalTypes.ImportedPlay,
                    Source = "PlayerData",
                    Hash = hash,
                    Characteristic = stats.beatmapCharacteristic.serializedName,
                    Difficulty = stats.difficulty.ToString(),
                    Count = stats.playCount,
                    Cleared = stats.validScore,
                    At = at,
                });
            }

            if (!Flush())
            {
                return;
            }
            state.ImportedAt = at;
            state.Save(_folders.Root);
            Plugin.Log.Info($"[signals] imported records from before OctantPath; the log now has {_log.Count} events");
        }

        private void CollectMenuChanges()
        {
            CollectFavorites(null);
            CollectVotes(null);
            CollectUserDeletes();
        }

        private void CollectFavorites(string source)
        {
            var liked = new HashSet<string>(_log.Liked());
            foreach (var e in _pending)
            {
                if (e.Type == SignalTypes.Like) liked.Add(e.Hash);
                if (e.Type == SignalTypes.LikeRemoved) liked.Remove(e.Hash);
            }
            var current = _playerDataModel.playerData.favoritesLevelIds.Select(LevelIds.Hash).Where(h => h != null);
            _pending.AddRange(SignalDiff.Favorites(current, liked, DateTime.UtcNow, source));
        }

        private void CollectVotes(string source)
        {
            var path = Path.Combine(UnityGame.UserDataPath, "votedSongs.json");
            if (!File.Exists(path))
            {
                return;
            }
            Dictionary<string, string> current;
            try
            {
                current = JObject.Parse(File.ReadAllText(path)).Properties()
                    .Where(p => p.Value.Type == JTokenType.Object)
                    .ToDictionary(p => p.Name, p => (string)p.Value["voteType"]);
            }
            catch (Exception ex)
            {
                Plugin.Log.Warn($"[signals] votedSongs.json could not be read: {ex.Message}");
                return;
            }
            var sent = _log.Votes();
            foreach (var e in _pending.Where(e => e.Type == SignalTypes.Upvote || e.Type == SignalTypes.Downvote))
            {
                sent[e.Hash] = e.Type;
            }
            _pending.AddRange(SignalDiff.Votes(current, sent, DateTime.UtcNow, source));
        }

        // 台帳にあるのに OctantPath が消していないマップがなくなっていたら、ユーザーによる削除
        private void CollectUserDeletes()
        {
            var ledger = _ledger.Load();
            if (ledger == null)
            {
                return;
            }
            foreach (var map in ledger.Maps.Where(m => m.State == "trial"))
            {
                if (!Directory.Exists(Path.Combine(_folders.Levels, map.Folder)))
                {
                    if (_ledger.MarkDeletedByUser(map.Hash, map.Folder))
                    {
                        _pending.Add(new SignalEvent { Type = SignalTypes.UserDelete, Hash = map.Hash, At = DateTime.UtcNow });
                    }
                }
            }
        }

        private bool Flush()
        {
            if (_pending.Count == 0 || _stopped)
            {
                return !_stopped;
            }
            try
            {
                var added = _log.Append(_pending);
                _pending.Clear();
                foreach (var e in added)
                {
                    Plugin.Log.Info($"[signals] {e.Type} {e.Hash} {e.Characteristic} {e.Difficulty} {e.Result}{e.Stage}{(e.Source != null ? " source=" + e.Source : "")}");
                }
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Log.Error($"[signals] could not write signals.jsonl: {ex.Message}");
                return false;
            }
        }

        private void FlushAndSync()
        {
            if (_gameScenesManager.InGameplay() || _stopped)
            {
                return;
            }
            Flush();
            _ = SyncAsync();
        }

        private async Task SyncAsync()
        {
            if (_hostInstance == null || !await _syncLock.WaitAsync(0).ConfigureAwait(true))
            {
                return;
            }
            try
            {
                // 送っている間に足されたものも、続けて送る
                while (_hostInstance != null && _sent != _log.Count && !await SyncOnceAsync().ConfigureAwait(true))
                {
                }
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                _syncLock.Release();
            }
        }

        // 続けて送れないときは true
        private async Task<bool> SyncOnceAsync()
        {
            var result = await SignalSync.SyncAsync(_log, _connection.Connection, _sent, _lifetime.Token).ConfigureAwait(true);
            if (!result.Succeeded)
            {
                Plugin.Log.Warn($"[signals] not sent: {result.Error}");
                // 接続を確かめ直して、OctantCompass の起動と件数が分かるまで送らない。次は丸ごと送り直す
                _hostInstance = null;
                _sent = null;
                return true;
            }
            _sent = result.Sent;
            if (result.Added > 0)
            {
                Plugin.Log.Info($"[signals] sent {result.Added}{(result.Replaced ? " (the whole record)" : "")}, OctantCompass holds {result.Sent}, rejected={result.RejectedEvents.Count}");
            }
            foreach (var r in result.RejectedEvents)
            {
                Plugin.Log.Warn($"[signals] rejected index={r.Index}: {r.Reason}");
            }
            return result.Added == 0;
        }

        private void Stop(string problem)
        {
            _stopped = true;
            SetProblem(problem);
        }

        private void SetProblem(string problem)
        {
            if (Problem == problem)
            {
                return;
            }
            Problem = problem;
            if (problem != null)
            {
                Plugin.Log.Warn($"[signals] {problem}");
            }
            else
            {
                Plugin.Log.Info("[signals] sending resumed");
            }
            ProblemChanged?.Invoke();
        }
    }
}
