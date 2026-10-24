using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using OctantPath.Core.Maps;
using OctantPath.Core.Signals;

namespace OctantPath.Core.Trials
{
    public enum TrialDecision
    {
        Hold,
        Keep,
        Delete,
    }

    // お試し中のマップを残すか消すか（実装構想の「シグナルとマップの入れ替え」、OctantPath 法 3.5）
    public sealed class TrialPolicy
    {
        private readonly HashSet<string> _liked;
        private readonly Dictionary<string, string> _votes;
        private readonly List<SignalEvent> _plays;
        private readonly List<DateTime> _launchDays;
        private readonly int _keepMinPlays;
        private readonly int _expiryLaunchDays;

        public TrialPolicy(HashSet<string> liked, Dictionary<string, string> votes, List<SignalEvent> plays, IEnumerable<string> launchDays, int keepMinPlays, int expiryLaunchDays)
        {
            _liked = liked;
            _votes = votes;
            _plays = plays;
            _launchDays = launchDays.Select(d => DateTime.TryParseExact(d, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) ? day : (DateTime?)null)
                .Where(d => d != null).Select(d => d.Value.Date).Distinct().ToList();
            _keepMinPlays = keepMinPlays;
            _expiryLaunchDays = expiryLaunchDays;
        }

        public (TrialDecision Decision, string Reason) Decide(OwnedMap map)
        {
            if (map.State != MapStates.Trial)
            {
                return (TrialDecision.Hold, "お試し中ではない");
            }
            var hash = map.Hash;
            _votes.TryGetValue(hash, out var vote);

            // 好意の印があるマップは消さない（OctantPath 法 3.5）
            if (_liked.Contains(hash))
            {
                return (TrialDecision.Keep, "お気に入り");
            }
            if (vote == SignalTypes.Upvote)
            {
                return (TrialDecision.Keep, "Upvote");
            }
            if (vote == SignalTypes.Downvote)
            {
                return (TrialDecision.Delete, "Downvote");
            }

            // クリアと失敗を数える。途中終了とやり直しは数えない
            var plays = _plays.Count(e => e.Hash == hash && e.At >= map.AcquiredAt && (e.Result == PlayResults.Clear || e.Result == PlayResults.Fail));
            if (plays >= _keepMinPlays)
            {
                return (TrialDecision.Keep, $"{plays} 回遊んだ");
            }

            var days = LaunchDaysSince(map.AcquiredAt);
            if (days > _expiryLaunchDays)
            {
                return (TrialDecision.Delete, $"お試しの期限（{_expiryLaunchDays} 起動日）を過ぎた");
            }
            return (TrialDecision.Hold, $"{days} 起動日目、{plays} 回");
        }

        // 取得した日も含めて、ゲームを起動した日を数える
        public int LaunchDaysSince(DateTime acquiredAtUtc)
        {
            var acquired = acquiredAtUtc.ToLocalTime().Date;
            return _launchDays.Count(d => d >= acquired);
        }
    }
}
