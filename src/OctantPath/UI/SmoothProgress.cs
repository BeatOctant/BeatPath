using UnityEngine;

namespace OctantPath.UI
{
    // 進み具合の帯をなめらかに伸ばす（下見の試聴の行と、結果画面の取得の帯）。
    // 届く割合は途中をほとんど返さない（大きな刻みで増えるか、0 のまま終わる）。取得元がローカルのフォルダのときや、
    // ほかの取得が終わるのを待っている間は、まったく届かない。なので伸び方はおもに時間で決める:
    // これまでにかかった時間の目安（typicalSeconds）で 8 割ほどに届き、9 割へ近づき続ける見込みと、届いた割合の大きい方へ寄せる。
    // 終わったら、残りを短い時間でなめらかに埋めてから消す（一気に埋めると急に速くなって見えた。2026-10-18）。
    // 描き直しは目に見えるだけ伸びたときだけにする
    internal sealed class SmoothProgress
    {
        // 見せている割合（0〜1）。見せていなければ負
        public float Shown { get; private set; } = -1f;

        public bool Visible => Shown >= 0f;

        private float _since;

        // 見せ始める（最小の幅から）
        public void Start()
        {
            _since = Time.unscaledTime;
            Shown = MinWidth;
        }

        // 終わりを待たずに消す（失敗したときなど）
        public void Stop()
        {
            Shown = -1f;
        }

        // 毎フレーム呼ぶ。fraction は届いた割合、null なら終わった。描き直しが要るときだけ真を返す
        public bool Update(float? fraction, float typicalSeconds)
        {
            if (!Visible)
            {
                if (!fraction.HasValue)
                {
                    return false;
                }
                Start();
                return true;
            }
            float next;
            if (fraction.HasValue)
            {
                var expected = Creep * (1f - Mathf.Exp(-Reach * (Time.unscaledTime - _since) / Mathf.Max(0.2f, typicalSeconds)));
                var target = Mathf.Max(MinWidth, fraction.Value, expected);
                next = Mathf.Lerp(Shown, target, Mathf.Min(1f, Time.unscaledDeltaTime * Ease));
            }
            else
            {
                next = Mathf.MoveTowards(Shown, 1f, Time.unscaledDeltaTime / FinishSeconds);
                if (next >= 1f)
                {
                    Shown = -1f;
                    return true;
                }
            }
            if (next - Shown < Step)
            {
                return false;
            }
            Shown = next;
            return true;
        }

        // 推測で決めた値: 最小の幅、見込みが近づく先と、目安の時間でどこまで近づくか（1.8 なら近づく先の 8 割強）、
        // 寄せる速さ（1 秒あたり）、終わってから全体を埋める速さ（全体を埋める秒数で）、描き直す最小の伸び
        private const float MinWidth = 0.05f;
        private const float Creep = 0.9f;
        private const float Reach = 1.8f;
        private const float Ease = 6f;
        private const float FinishSeconds = 0.35f;
        private const float Step = 0.01f;
    }
}
