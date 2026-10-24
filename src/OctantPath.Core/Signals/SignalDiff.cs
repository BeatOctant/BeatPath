using System;
using System.Collections.Generic;
using System.Linq;
using OctantPath.Core.Maps;

namespace OctantPath.Core.Signals
{
    // 今の状態と正本を比べて、足りない signal を作る
    public static class SignalDiff
    {
        // お気に入り: 付いたら like、外れたら like_removed（最後に送った状態と変わったときだけ）
        public static List<SignalEvent> Favorites(IEnumerable<string> currentHashes, HashSet<string> liked, DateTime at, string source)
        {
            var current = new HashSet<string>(currentHashes.Select(MapHash.Normalize).Where(h => h != null));
            var events = new List<SignalEvent>();
            foreach (var hash in current.Where(h => !liked.Contains(h)).OrderBy(h => h, StringComparer.Ordinal))
            {
                events.Add(new SignalEvent { Type = SignalTypes.Like, Hash = hash, Source = source, At = at });
            }
            // 導入前の取り込みでは、外したことは分からないので作らない
            if (source == null)
            {
                foreach (var hash in liked.Where(h => !current.Contains(h)).OrderBy(h => h, StringComparer.Ordinal))
                {
                    events.Add(new SignalEvent { Type = SignalTypes.LikeRemoved, Hash = hash, At = at });
                }
            }
            return events;
        }

        // 投票: 新しい投票か、前と違う投票だけ。投票の取り消しは記録されないので作らない（外部依存）
        public static List<SignalEvent> Votes(IDictionary<string, string> currentVotes, Dictionary<string, string> sent, DateTime at, string source)
        {
            var events = new List<SignalEvent>();
            foreach (var pair in currentVotes.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                var hash = MapHash.Normalize(pair.Key);
                var type = pair.Value == "Upvote" ? SignalTypes.Upvote : pair.Value == "Downvote" ? SignalTypes.Downvote : null;
                if (hash == null || type == null)
                {
                    continue;
                }
                if (sent.TryGetValue(hash, out var last) && last == type)
                {
                    continue;
                }
                events.Add(new SignalEvent { Type = type, Hash = hash, Source = source, At = at });
            }
            return events;
        }
    }
}
