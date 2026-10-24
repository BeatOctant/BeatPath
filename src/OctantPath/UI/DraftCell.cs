using System;
using System.Linq;
using OctantPath.Core.Recommendations;
using BeatSaberMarkupLanguage.Attributes;
using BeatSaberMarkupLanguage.Components;
using HMUI;
using OctantPath.Core.Languages;

namespace OctantPath.UI
{
    // 下見の小窓の 1 行。カバー・曲名・行ごとの外す（✕）・入れ替え（⇔）・単体取得（↓）。取得が済んだ曲は ↓ の場所に遊ぶ（▶）を出す。どれも絵で出す。
    // 一覧の一番下には「曲を足す」の行を置く（AddRow。カバーの場所に ＋ の絵）。
    // 結果画面の板も同じ行を使う（外す・入れ替え・取得は出さず、▶ で次の曲にする）
    internal sealed class DraftCell : NotifiableBase
    {
        // 行の中身（BSML の custom-list の雛形）。行の中ではレイアウトの部品を使わず、行の幅（小窓の中身の幅。OctantPath.Core.UI.Popups.Draft）と高さ（7.5）に対して
        // 位置を直に決める（レイアウトに任せると曲名の欄の幅が 0 になった）。曲名の字体（Teko）は行の高さが大きく、
        // 欄の高さに収まらないと「…」に切られる（行を高くしても曲によって切られた）。なので文字の欄は行の外まで高く取り、
        // 見える位置は揃え（曲名は下揃え、作者は上揃え）と文字の大きさで、枠の上下から内側に収める。入れ替えと取得は OctantPath の絵（Icons）。見つからないときだけ、日本語の字体にある記号を出す（✕ は □ になった）
        // 左から: 試聴中の帯（縁から 0.5、幅 0.8）、帯の幅だけ空けてカバー（2.1 から 7）、2 空けて曲名と作者（11.1 から。左端をそろえる）。
        // 右から: 取得か遊ぶ、入れ替え、外す（絵の中心の間は 10）。曲名と作者は外すの絵の 5 手前（右から 33.9）まで
        public const string Template = @"<bg>
  <image id='glow' anchor-min-x='0' anchor-max-x='1' anchor-min-y='0' anchor-max-y='1' size-delta-x='-1' size-delta-y='0' image-color='#4C9AFF26'/>
  <image id='playing-bar' anchor-min-x='0' anchor-max-x='0' anchor-min-y='0' anchor-max-y='1' pivot-x='0' anchor-pos-x='0.5' size-delta-x='0.8' size-delta-y='0' image-color='#4C9AFF'/>
  <image id='cover' anchor-min-x='0' anchor-max-x='0' anchor-min-y='0.5' anchor-max-y='0.5' pivot-x='0' anchor-pos-x='2.1' size-delta-x='7' size-delta-y='7' preserve-aspect='true'/>
  <image id='add-icon' anchor-min-x='0' anchor-max-x='0' anchor-min-y='0.5' anchor-max-y='0.5' pivot-x='0' anchor-pos-x='3.1' size-delta-x='5' size-delta-y='5' preserve-aspect='true'/>
  <text id='title-text' text='~title' anchor-min-x='0' anchor-max-x='1' anchor-min-y='0.48' anchor-max-y='2' size-delta-x='-45' size-delta-y='0' anchor-pos-x='-11.4' font-size='3' align='BottomLeft' overflow-mode='Ellipsis' word-wrapping='false'/>
  <text id='subtitle-text' text='~subtitle' anchor-min-x='0' anchor-max-x='1' anchor-min-y='-1' anchor-max-y='0.44' size-delta-x='-45' size-delta-y='0' anchor-pos-x='-11.4' font-size='2.2' color='#BBBBBB' align='TopLeft' overflow-mode='Ellipsis' word-wrapping='false'/>
  <clickable-text id='discard-text' text='-' on-click='discard' anchor-min-x='1' anchor-max-x='1' anchor-min-y='0.5' anchor-max-y='0.5' pivot-x='1' anchor-pos-x='-21' size-delta-x='10' size-delta-y='7.5' font-size='5' align='Center' hover-hint='{{draft.discardHint}}'/>
  <clickable-text id='swap-text' text='⇔' on-click='swap' anchor-min-x='1' anchor-max-x='1' anchor-min-y='0.5' anchor-max-y='0.5' pivot-x='1' anchor-pos-x='-11' size-delta-x='10' size-delta-y='7.5' font-size='5' align='Center' hover-hint='{{draft.swapHint}}'/>
  <clickable-text id='download-text' text='~download-text' on-click='download' anchor-min-x='1' anchor-max-x='1' anchor-min-y='0.5' anchor-max-y='0.5' pivot-x='1' anchor-pos-x='-1' size-delta-x='10' size-delta-y='7.5' font-size='5' align='Center' hover-hint='~download-hint'/>
  <clickable-image id='discard-icon' on-click='discard' anchor-min-x='1' anchor-max-x='1' anchor-min-y='0.5' anchor-max-y='0.5' pivot-x='1' anchor-pos-x='-23.25' size-delta-x='5.5' size-delta-y='5.5' preserve-aspect='true' hover-hint='{{draft.discardHint}}'/>
  <clickable-image id='swap-icon' on-click='swap' anchor-min-x='1' anchor-max-x='1' anchor-min-y='0.5' anchor-max-y='0.5' pivot-x='1' anchor-pos-x='-13.25' size-delta-x='5.5' size-delta-y='5.5' preserve-aspect='true' hover-hint='{{draft.swapHint}}'/>
  <clickable-image id='download-icon' on-click='download' anchor-min-x='1' anchor-max-x='1' anchor-min-y='0.5' anchor-max-y='0.5' pivot-x='1' anchor-pos-x='-3.25' size-delta-x='5.5' size-delta-y='5.5' preserve-aspect='true' hover-hint='~download-hint'/>
</bg>";

        // 行の操作。null の操作の絵は出さない（結果画面の板は Play だけ）
        public sealed class Actions
        {
            public Action<RecommendationItem> Swap;
            public Action<RecommendationItem> Discard;
            public Action<RecommendationItem> Download;
            public Action<RecommendationItem> Play;
            // ▶ の説明の文の鍵（null なら下見の文）
            public string PlayHint;
        }

        private readonly Actions _do;
        private readonly Func<RecommendationItem, string> _state;
        private readonly CoverCache _covers;

        [UIComponent("cover")]
        private ImageView _cover;

        [UIComponent("add-icon")]
        private ImageView _addIcon;

        // canEdit: 外す・入れ替えを押せるか。canDownload: 取得できるか（↓）。canPlay: 入っているか置いてあり、曲選択で選べるか（▶。↓ より優先）
        public DraftCell(RecommendationItem item, bool playing, bool canEdit, bool canDownload, bool canPlay, Func<RecommendationItem, string> state, CoverCache covers, Actions actions)
        {
            Item = item;
            Playing = playing;
            CanEdit = canEdit;
            CanDownload = canDownload;
            CanPlay = canPlay;
            _state = state;
            _covers = covers;
            _do = actions;
        }

        // 一覧の一番下の「曲を足す」の行。押すと行を選んだことになり、呼ぶ側が足す（RecommendationTab の draft-select）
        public static DraftCell AddRow(CoverCache covers) => new DraftCell(null, false, false, false, false, _ => "", covers, null);

        public bool IsAddRow => Item == null;

        public RecommendationItem Item { get; }
        public bool Playing { get; private set; }

        // 選んだ行を移すときは、一覧を作り直さずに行の見た目だけを変える。BSML の custom-list は作り直すたびに見えている行を
        // すべて作り直して BSML を読み直すので、選ぶたび・カバーが届くたびに作り直すと、下見を開いたときにカクついた（2026-10-18）
        public void SetPlaying(bool playing, bool loading = false)
        {
            if (Playing == playing)
            {
                return;
            }
            Playing = playing;
            _loading.Stop();
            if (playing && loading)
            {
                StartLoading();
            }
            foreach (var fill in new[] { _glow, _playingBar })
            {
                if (fill != null)
                {
                    fill.gameObject.SetActive(playing);
                }
            }
            ApplyLoading();
        }

        // カバーが届いたら、その行の絵だけを差し替える（一覧は作り直さない）
        public void RefreshCover()
        {
            if (_cover != null && Item != null && _covers.TryGet(Item.Version?.CoverUrl, out var sprite) && sprite != null && _cover.sprite != sprite)
            {
                _cover.sprite = sprite;
            }
        }

        [UIValue("title")]
        public string Title => IsAddRow ? Lang.T("draft.add") : Core.UI.DisplayText.Line(Item.Title);

        // 試聴中の行は、行の枠の左端を角のない帯で塗り、行全体を薄く塗る（OctantPath.Desktop の選んでいる行と同じ青）
        [UIComponent("playing-bar")]
        private ImageView _playingBar;

        [UIComponent("glow")]
        private ImageView _glow;

        [UIComponent("title-text")]
        private TMPro.TextMeshProUGUI _titleText;

        [UIComponent("subtitle-text")]
        private TMPro.TextMeshProUGUI _subtitleText;

        [UIValue("subtitle")]
        public string Subtitle => IsAddRow ? Lang.T("draft.addHint") : $"{Core.UI.DisplayText.Line(Item.Artist)} [{Core.UI.DisplayText.Line(Item.Mapper)}]  {Item.Candidate.Difficulty}{_state(Item)}";

        public bool CanEdit { get; }

        public bool CanDownload { get; }

        public bool CanPlay { get; }

        // 遊べるときは ▶、取得できるときは ↓ を出す（枠のない記号なので、押せないときは消す）
        [UIValue("download-text")]
        public string DownloadText => CanPlay ? "▶" : CanDownload ? "↓" : "";

        [UIValue("download-hint")]
        public string DownloadHint => Lang.T(CanPlay ? _do?.PlayHint ?? "draft.playHint" : "draft.downloadHint");

        [UIComponent("discard-text")]
        private ClickableText _discardText;

        [UIComponent("discard-icon")]
        private ClickableImage _discardIcon;

        [UIComponent("swap-text")]
        private ClickableText _swapText;

        [UIComponent("download-text")]
        private ClickableText _downloadText;

        [UIComponent("swap-icon")]
        private ClickableImage _swapIcon;

        [UIComponent("download-icon")]
        private ClickableImage _downloadIcon;

        [UIAction("#post-parse")]
        private void PostParse()
        {
            var actions = _do != null && !IsAddRow;
            UseIcon(_discardIcon, _discardText, Icons.Discard, actions && _do.Discard != null);
            UseIcon(_swapIcon, _swapText, Icons.Swap, actions && _do.Swap != null);
            UseIcon(_downloadIcon, _downloadText, CanPlay ? Icons.Play : Icons.Download, actions && (CanPlay ? _do.Play != null : CanDownload && _do.Download != null));
            foreach (var (text, shown) in new[] { (_discardText, actions && _do.Discard != null), (_swapText, actions && _do.Swap != null), (_downloadText, actions) })
            {
                if (text != null && !shown)
                {
                    text.gameObject.SetActive(false);
                }
            }
            if (_addIcon != null)
            {
                var plus = IsAddRow ? Icons.Find(Icons.Add) : null;
                _addIcon.sprite = plus;
                _addIcon.raycastTarget = false;
                _addIcon.gameObject.SetActive(plus != null);
            }
            // 文字の欄は行の外まで高く取っているので、押下を受けると上の行を押したのに下の行が選ばれる。押下は受けない
            foreach (var graphic in new UnityEngine.UI.Graphic[] { _titleText, _subtitleText, _glow, _playingBar })
            {
                if (graphic != null)
                {
                    graphic.raycastTarget = false;
                }
            }
            // 帯と行の塗りは角を丸めない（白い四角の絵を色で塗る）
            foreach (var fill in new[] { _glow, _playingBar })
            {
                if (fill != null)
                {
                    fill.sprite = SquareSprite();
                    fill.type = UnityEngine.UI.Image.Type.Simple;
                    fill.gameObject.SetActive(Playing);
                }
            }
            ApplyLoading();
            if (_cover != null)
            {
                _cover.gameObject.SetActive(!IsAddRow);
            }
            if (_cover != null && !IsAddRow)
            {
                // 曲選択のカバーと同じ、角を落とす素材を使う
                var rounded = RoundEdgeMaterial();
                if (rounded != null)
                {
                    _cover.material = rounded;
                }
                if (_covers.TryGet(Item.Version?.CoverUrl, out var sprite) && sprite != null)
                {
                    _cover.sprite = sprite;
                }
            }
        }

        // 選んだ曲の試聴の音源を取っている途中なら、受け取った割合（0〜1）。null なら取り終えている。毎フレーム呼ぶ。
        // 取っている間は、行の薄い塗りを左から伸ばし、左端の帯を淡くする（前の曲はまだ流れている。2026-10-18）。伸び方は SmoothProgress
        private readonly SmoothProgress _loading = new SmoothProgress();

        // 試聴の音源を取り始めた行を、最初から取っている途中の見た目で出す。試聴中の見た目（全体の塗り）で出してから次のフレームで
        // 縮めると、選んだ瞬間に一度だけ行全体が青くなった（2026-10-18）。行を出す（SetPlaying・BSML の読み込み）前に呼ぶ
        public void StartLoading()
        {
            _loading.Start();
            ApplyLoading();
        }

        public void ShowLoading(float? fraction, float typicalSeconds)
        {
            if (Playing && _loading.Update(fraction, typicalSeconds))
            {
                ApplyLoading();
            }
        }

        private void ApplyLoading()
        {
            if (!Playing || _glow == null || _playingBar == null)
            {
                return;
            }
            var loading = _loading.Visible;
            var rect = _glow.rectTransform;
            rect.anchorMax = new UnityEngine.Vector2(loading ? _loading.Shown : 1f, rect.anchorMax.y);
            var color = _playingBar.color;
            color.a = loading ? LoadingBarAlpha : 1f;
            _playingBar.color = color;
        }

        // 取っている間の帯の不透明度（推測で決めた値）
        private const float LoadingBarAlpha = 0.35f;

        // 絵が読めれば絵を出して記号を隠す。なければ記号のまま。押せないときは絵も出さない（記号の ↓ を空にするのと同じ）
        private static void UseIcon(ClickableImage icon, ClickableText text, string name, bool visible)
        {
            if (icon == null)
            {
                return;
            }
            var sprite = Icons.Find(name);
            if (sprite == null)
            {
                icon.gameObject.SetActive(false);
                return;
            }
            icon.sprite = sprite;
            icon.gameObject.SetActive(visible);
            if (text != null)
            {
                text.gameObject.SetActive(false);
            }
        }

        private static UnityEngine.Sprite _square;

        // 角のない白い四角の絵（Unity の白いテクスチャから作る）
        private static UnityEngine.Sprite SquareSprite()
        {
            if (_square == null)
            {
                var texture = UnityEngine.Texture2D.whiteTexture;
                _square = UnityEngine.Sprite.Create(texture, new UnityEngine.Rect(0, 0, texture.width, texture.height), new UnityEngine.Vector2(0.5f, 0.5f));
            }
            return _square;
        }

        private static UnityEngine.Material _roundEdge;

        // ゲームの角の丸い UI 素材（曲選択のカバーが使うもの）。見つからなければ null
        private static UnityEngine.Material RoundEdgeMaterial()
        {
            if (_roundEdge == null)
            {
                _roundEdge = UnityEngine.Resources.FindObjectsOfTypeAll<UnityEngine.Material>().FirstOrDefault(m => m.name == "UINoGlowRoundEdge");
            }
            return _roundEdge;
        }

        [UIAction("swap")]
        private void Swap()
        {
            if (CanEdit && _do != null)
            {
                _do.Swap(Item);
            }
        }

        [UIAction("discard")]
        private void Discard()
        {
            if (CanEdit && _do != null)
            {
                _do.Discard(Item);
            }
        }

        // ↓ と ▶ は同じ場所。取得が済むと ▶ に変わる
        [UIAction("download")]
        private void Download()
        {
            if (_do == null)
            {
                return;
            }
            if (CanPlay)
            {
                _do.Play?.Invoke(Item);
            }
            else if (CanDownload)
            {
                _do.Download?.Invoke(Item);
            }
        }
    }
}
