using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OctantPath.Core.Maps;
using OctantPath.Core.Settings;
using OctantPath.Core.Signals;
using Newtonsoft.Json.Linq;

namespace OctantPath.Core.Trials
{
    // お試し中のマップを残すか消す。ゲームの起動直後、SongCore が曲を読む前にだけ動く（OctantPath 法 未決 U2 → 2026-10-08 決定 で A）。
    // 守る情報が読めないとき、中身が記録と違うとき、戻せない食い違いが起きたときは止める（OctantPath 法 3.2〜3.6）
    public sealed class Janitor
    {
        public const string DeletingFolderName = "Deleting";

        private readonly JanitorPaths _paths;
        private readonly OctantPathSettings _settings;
        private readonly Action<string> _log;

        public Janitor(JanitorPaths paths, OctantPathSettings settings, Action<string> log)
        {
            _paths = paths;
            _settings = settings;
            _log = log;
        }

        public JanitorReport Run(IEnumerable<string> launchDays)
        {
            var report = new JanitorReport { DryRun = _settings.DeleteDryRun };
            var settingsProblem = _settings.DeletionProblem();
            if (settingsProblem != null)
            {
                return report.Stop(settingsProblem);
            }
            var ledgerStore = new OwnershipLedger(_paths.Root);
            var ledger = ledgerStore.Load();
            if (ledger == null)
            {
                return report.Stop("所有の記録（ledger.json）が読めないので、何も消さない");
            }
            var signals = SignalLog.Open(_paths.Root, out var signalError);
            if (signals == null)
            {
                return report.Stop($"signal の正本が読めないので、何も消さない: {signalError}");
            }
            var votes = ReadVotes(out var voteError);
            if (voteError != null)
            {
                return report.Stop(voteError);
            }
            var favorites = ReadFavorites(out var favoriteError);
            if (favoriteError != null)
            {
                return report.Stop(favoriteError);
            }
            var protectedHashes = PlaylistGuard.ProtectedHashes(_paths.Playlists, out var playlistError);
            if (protectedHashes == null)
            {
                return report.Stop($"プレイリストが読めないので、何も消さない: {playlistError}");
            }

            // 投票は votedSongs.json が正本なので、signal の正本より新しいものを優先する
            var lastVotes = signals.Votes();
            foreach (var pair in votes)
            {
                lastVotes[pair.Key] = pair.Value;
            }
            // お気に入りは、signal の正本とゲームの PlayerData の両方を見る（signal に書けなかったお気に入りも守る。OctantPath 法 3.4・3.5）
            var liked = signals.Liked();
            liked.UnionWith(favorites);
            var policy = new TrialPolicy(liked, lastVotes, signals.Events.Where(e => e.Type == SignalTypes.Play).ToList(), launchDays, _settings.KeepMinPlays, _settings.TrialExpiryLaunchDays);

            var decisions = ledger.Maps.Where(m => m.State == MapStates.Trial).OrderBy(m => m.AcquiredAt).Select(m => (Map: m, Result: policy.Decide(m))).ToList();

            // 期限の日数のほかに、お試しの合計の大きさでも消す（どちらかを超えたら。2026-10-20）。
            // 残るものの合計が上限を超えるあいだ、古く取ったものから消す。他のプレイリストに入っているものは消さない
            var held = decisions.Where(d => d.Result.Decision == TrialDecision.Hold).Select(d => d.Map).ToList();
            var total = held.Sum(m => FolderBytes(Path.Combine(_paths.Levels, m.Folder)));
            var overCapacity = new HashSet<OwnedMap>();
            foreach (var map in held.Where(m => !protectedHashes.Contains(m.Hash)))
            {
                if (total <= _settings.TrialMaxBytes)
                {
                    break;
                }
                overCapacity.Add(map);
                total -= FolderBytes(Path.Combine(_paths.Levels, map.Folder));
            }

            var changed = false;
            foreach (var (map, result) in decisions)
            {
                var (decision, reason) = overCapacity.Contains(map) ? (TrialDecision.Delete, $"お試しの合計が上限（{_settings.TrialMaxMegabytes} MB）を超えた") : result;
                if (decision == TrialDecision.Hold)
                {
                    continue;
                }
                if (decision == TrialDecision.Delete && protectedHashes.Contains(map.Hash))
                {
                    report.Add(map, "残す", "他のプレイリストに入っている");
                    continue;
                }

                var folder = Path.Combine(_paths.Levels, map.Folder);
                var check = CheckFolder(map, folder);
                if (check != null)
                {
                    report.Add(map, "そのまま", check);
                    continue;
                }

                if (decision == TrialDecision.Keep)
                {
                    var target = Path.Combine(_paths.CustomLevels, map.Folder);
                    if (Directory.Exists(target) || File.Exists(target))
                    {
                        report.Add(map, "そのまま", $"残す（{reason}）が、CustomLevels に同じ名前がある");
                        continue;
                    }
                    if (_settings.DeleteDryRun)
                    {
                        report.Add(map, "残す（試し運転）", reason);
                        continue;
                    }
                    try
                    {
                        Directory.Move(folder, target);
                    }
                    catch (Exception ex)
                    {
                        report.Add(map, "そのまま", $"CustomLevels に移せない: {ex.Message}");
                        continue;
                    }
                    map.State = MapStates.Kept;
                    map.KeptFolder = Path.GetFileName(target);
                    map.ChangedAt = DateTime.UtcNow;
                    // 移したらすぐ記録する。後で止まったときに trial のまま残ると、次の起動で「利用者が消した」と記録してしまう
                    try
                    {
                        ledgerStore.Save(ledger);
                    }
                    catch (Exception ex)
                    {
                        // 記録できないなら元に戻して止める
                        try
                        {
                            Directory.Move(target, folder);
                        }
                        catch (Exception)
                        {
                        }
                        map.State = MapStates.Trial;
                        map.KeptFolder = null;
                        map.ChangedAt = null;
                        report.Stop($"所有の記録を書けないので止める: {ex.Message}");
                        break;
                    }
                    report.Add(map, "残した", reason);
                    continue;
                }

                if (_settings.DeleteDryRun)
                {
                    report.Add(map, "消す（試し運転）", reason);
                    continue;
                }

                // 戻せる写しを残すため、まず同じファイルシステムの中で脇へ移してから消す（OctantPath 法 3.6）
                var aside = Path.Combine(_paths.Root, DeletingFolderName, map.Folder + "." + Guid.NewGuid().ToString("N"));
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(aside));
                    Directory.Move(folder, aside);
                }
                catch (Exception ex)
                {
                    report.Add(map, "そのまま", $"脇へ移せない: {ex.Message}");
                    report.Stop("移せない食い違いが起きたので、残りは消さない");
                    break;
                }
                map.State = MapStates.Deleted;
                map.ChangedAt = DateTime.UtcNow;
                changed = true;
                try
                {
                    ledgerStore.Save(ledger);
                }
                catch (Exception ex)
                {
                    // 記録できないなら元に戻して止める
                    try
                    {
                        Directory.Move(aside, folder);
                    }
                    catch (Exception)
                    {
                    }
                    map.State = MapStates.Trial;
                    report.Stop($"所有の記録を書けないので止める: {ex.Message}");
                    break;
                }
                try
                {
                    Directory.Delete(aside, true);
                }
                catch (Exception ex)
                {
                    _log?.Invoke($"[janitor] {aside} could not be removed yet: {ex.Message}");
                }
                report.Add(map, "消した", reason);
            }

            if (changed && !report.Stopped)
            {
                ledgerStore.Save(ledger);
            }
            return report;
        }

        // フォルダの中のファイルの合計（バイト）。読めなければ 0
        private static long FolderBytes(string folder)
        {
            try
            {
                return Directory.Exists(folder) ? new DirectoryInfo(folder).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) : 0;
            }
            catch (Exception)
            {
                return 0;
            }
        }

        // 消す・移す直前に、置き場所の中にあり、中身の hash が記録と同じか確かめる（OctantPath 法 3.3）
        private string CheckFolder(OwnedMap map, string folder)
        {
            if (!MapInstaller.SafeFolderName(map.Folder))
            {
                return "記録のフォルダ名が使えない";
            }
            var full = Path.GetFullPath(folder);
            var levels = Path.GetFullPath(_paths.Levels).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(levels, StringComparison.OrdinalIgnoreCase))
            {
                return "専用フォルダの外を指している";
            }
            if (!Directory.Exists(folder))
            {
                return "フォルダがない";
            }
            var hash = MapHash.Compute(folder);
            if (!hash.Succeeded || hash.Hash != map.Hash)
            {
                return "中身が記録と違う";
            }
            return null;
        }

        // ゲームの PlayerData.dat のお気に入り（hash）。ファイルがなければ空。あるのに読めないか、形が分からなければ error
        private HashSet<string> ReadFavorites(out string error)
        {
            error = null;
            var favorites = new HashSet<string>();
            if (string.IsNullOrEmpty(_paths.PlayerData) || !File.Exists(_paths.PlayerData))
            {
                return favorites;
            }
            try
            {
                var lists = JToken.Parse(File.ReadAllText(_paths.PlayerData)).SelectTokens("$..favoritesLevelIds").ToList();
                if (lists.Count == 0 || lists.Any(l => l.Type != JTokenType.Array))
                {
                    error = "ゲームのお気に入り（PlayerData.dat の favoritesLevelIds）が読めないので、何も消さない";
                    return favorites;
                }
                foreach (var id in lists.SelectMany(l => l.Values<string>()))
                {
                    var hash = LevelIds.Hash(id);
                    if (hash != null)
                    {
                        favorites.Add(hash);
                    }
                }
            }
            catch (Exception ex)
            {
                error = $"ゲームのお気に入り（PlayerData.dat）が読めないので、何も消さない: {ex.Message}";
            }
            return favorites;
        }

        private Dictionary<string, string> ReadVotes(out string error)
        {
            error = null;
            var votes = new Dictionary<string, string>();
            var installed = File.Exists(_paths.VotingPlugin);
            if (!File.Exists(_paths.VotedSongs))
            {
                // 投票の MOD が入っていないことと、入っているのに記録が読めないことは分ける（OctantPath 法 3.4）
                return votes;
            }
            try
            {
                foreach (var p in JObject.Parse(File.ReadAllText(_paths.VotedSongs)).Properties())
                {
                    var hash = MapHash.Normalize(p.Name);
                    var type = (string)p.Value["voteType"];
                    if (hash != null && (type == "Upvote" || type == "Downvote"))
                    {
                        votes[hash] = type == "Upvote" ? SignalTypes.Upvote : SignalTypes.Downvote;
                    }
                }
            }
            catch (Exception ex)
            {
                error = installed ? $"投票の記録が読めないので、何も消さない: {ex.Message}" : null;
            }
            return votes;
        }
    }

    public sealed class JanitorPaths
    {
        // UserData/OctantPath
        public string Root { get; set; }
        public string Levels { get; set; }
        public string CustomLevels { get; set; }
        public string Playlists { get; set; }
        public string VotedSongs { get; set; }
        public string VotingPlugin { get; set; }
        // ゲームの PlayerData.dat（お気に入りを読む）。空なら読まない
        public string PlayerData { get; set; }
    }

    public sealed class JanitorReport
    {
        public bool DryRun { get; set; }
        public bool Stopped { get; private set; }
        public string StopReason { get; private set; }
        public List<string> Lines { get; } = new List<string>();

        public void Add(OwnedMap map, string action, string reason) => Lines.Add($"{action}: {map.MapKey} {map.Hash} {map.Folder}（{reason}）");

        public JanitorReport Stop(string reason)
        {
            Stopped = true;
            StopReason = reason;
            return this;
        }
    }
}
