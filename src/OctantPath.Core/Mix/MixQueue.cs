using System;
using System.Collections.Generic;
using System.Linq;

namespace OctantPath.Core.Mix
{
    public enum MixFilter
    {
        All,
        Familiar,
        New,
    }

    // ミックスの並び。並びは OctantCompass が返した順のまま持ち、絞り込みは表示だけ（憲法の原則 6）
    public sealed class MixQueue<T>
    {
        private readonly List<T> _items;
        private readonly Func<T, bool> _installed;

        public MixQueue(IEnumerable<T> items, Func<T, bool> installed)
        {
            _items = items.ToList();
            _installed = installed;
        }

        public IReadOnlyList<T> All => _items;
        public MixFilter Filter { get; set; } = MixFilter.All;

        // 今の曲（並び全体での位置）。まだ始めていなければ -1
        public int Current { get; private set; } = -1;

        public List<T> Visible()
        {
            switch (Filter)
            {
                case MixFilter.Familiar:
                    return _items.Where(_installed).ToList();
                case MixFilter.New:
                    return _items.Where(i => !_installed(i)).ToList();
                default:
                    return _items.ToList();
            }
        }

        public void Select(T item)
        {
            Current = _items.IndexOf(item);
        }

        // 今の絞り込みで、今の曲の次。なければ default
        public T Next()
        {
            var visible = Visible();
            var after = visible.Where(i => _items.IndexOf(i) > Current).ToList();
            return after.Count > 0 ? after[0] : default;
        }

        // 今の曲のすぐ後ろに入れる（結果画面で選んだおすすめ）。すでに並びにあれば、そこへ動かす
        public void InsertNext(T item)
        {
            var current = Current >= 0 && Current < _items.Count ? _items[Current] : default;
            _items.Remove(item);
            Current = current != null ? _items.IndexOf(current) : -1;
            _items.Insert(Current + 1, item);
        }

        // 今の曲から先で、まだ入っていない曲を最大 count 件（今の曲も含む）
        public List<T> ToPrefetch(int count)
        {
            var start = Math.Max(0, Current);
            return _items.Skip(start).Where(i => !_installed(i)).Take(Math.Max(0, count)).ToList();
        }
    }
}
