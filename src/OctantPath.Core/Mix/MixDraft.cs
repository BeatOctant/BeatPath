using System;
using System.Collections.Generic;
using System.Linq;
using OctantPath.Core.Presets;
using OctantPath.Core.Recommendations;

namespace OctantPath.Core.Mix
{
    // 下見の数の決まり。MOD の RecommendationTab と Desktop が同じものを使う（倍率と残りの下限は推測で決めた値）
    public static class MixDraftRules
    {
        // 下見を作るときは、並べる数のこの倍を受け取る（並べなかったものは入れ替え用に取っておく）
        public const int FirstFactor = 3;

        // 入れ替え用を足すときは、並べる数のこの倍を受け取る
        public const int TopUpFactor = 2;

        // 入れ替え用の残りがこの数より少なくなったら、裏で追加を受け取る（MixDraft.RunningLow）
        public const int ReserveLow = 3;

        // 下見の「曲を足す」で並べられる数の上限（MOD の設定の下見の曲数の上限と同じ。推測で決めた値）
        public const int MaxSize = 20;

        // 並べる数（設定の mixDraftCount。1 より少なければ 1）
        public static int Size(int mixDraftCount) => Math.Max(1, mixDraftCount);

        // 下見を作る要求
        public static RecommendationRequest FirstRequest(Preset preset, ChartRef seed, IDictionary<string, string> choices, int size) =>
            PresetRules.Request(preset, seed, choices, size * FirstFactor);

        // 入れ替え用を足す要求。同じ種類・起点・選択肢で、今まで出したもの（MixDraft.Seen の hash）を除く
        public static RecommendationRequest TopUpRequest(Preset preset, ChartRef seed, IDictionary<string, string> choices, int size, IEnumerable<string> seenHashes)
        {
            var request = PresetRules.Request(preset, seed, choices, size * TopUpFactor);
            request.Exclude = seenHashes.Where(h => h != null).Distinct().Select(h => new ExcludeEntry { Hash = h, Reason = "in_draft" }).ToList();
            return request;
        }
    }

    // ミックスの下見。受け取った推薦の先頭から count 件を並べ、残りは入れ替え用に取っておく。
    // 取得は「作成」を押したときに並んでいる曲だけ（下見の間は試聴だけで、取らない）。
    // 入れ替えても順は OctantCompass が返した順のまま使う（憲法の原則 6）
    public sealed class MixDraft<T>
    {
        private readonly List<T> _picked;
        private readonly Queue<T> _reserve;

        public MixDraft(IEnumerable<T> candidates, int count)
        {
            var all = candidates.ToList();
            var take = Math.Max(0, Math.Min(count, all.Count));
            _picked = all.Take(take).ToList();
            _reserve = new Queue<T>(all.Skip(take));
        }

        public IReadOnlyList<T> Picked => _picked;
        public int ReserveCount => _reserve.Count;

        // 入れ替え用が少なくなった（追加を受け取るとき）
        public bool RunningLow => _reserve.Count < MixDraftRules.ReserveLow;

        // 外したもの（入れ替えで並びから出したもの）。次の要求の除外に使える
        public List<T> Removed { get; } = new List<T>();

        // 下見に出したもの・外したもの・取っておいたもの（次の要求で除外に入れる）
        public IEnumerable<T> Seen => _picked.Concat(Removed).Concat(_reserve);

        // 入れ替え用を足す（追加で受け取った推薦）。すでにあるものは足さない。足した数を返す
        public int AddReserve(IEnumerable<T> candidates)
        {
            var seen = new HashSet<T>(Seen);
            var added = 0;
            foreach (var c in candidates)
            {
                if (seen.Add(c))
                {
                    _reserve.Enqueue(c);
                    added++;
                }
            }
            return added;
        }

        // item を外すだけ（入れ替えない）。並びにあって外せたら真
        public bool Remove(T item)
        {
            var index = _picked.IndexOf(item);
            if (index < 0)
            {
                return false;
            }
            Removed.Add(item);
            _picked.RemoveAt(index);
            return true;
        }

        // 取っておいた次の候補を並びの最後に足す。候補が尽きているか、MixDraftRules.MaxSize だけ並んでいれば足さずに default
        public T Add()
        {
            if (_reserve.Count == 0 || _picked.Count >= MixDraftRules.MaxSize)
            {
                return default;
            }
            var next = _reserve.Dequeue();
            _picked.Add(next);
            return next;
        }

        public bool Full => _picked.Count >= MixDraftRules.MaxSize;

        // item を外し、取っておいた次の候補をその場所に入れる。候補が尽きていれば外すだけ。入れたものを返す
        public T Replace(T item)
        {
            var index = _picked.IndexOf(item);
            if (index < 0)
            {
                return default;
            }
            Removed.Add(item);
            if (_reserve.Count == 0)
            {
                _picked.RemoveAt(index);
                return default;
            }
            var next = _reserve.Dequeue();
            _picked[index] = next;
            return next;
        }
    }
}
