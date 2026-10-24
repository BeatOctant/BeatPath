using System;
using BeatSaberMarkupLanguage.Attributes;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace OctantPath.UI
{
    // ゲームの Modifiers のタイル（角の丸い暗い板）の形のボタン。BSML の modifier（GameplayModifierToggle の複製）を使い、
    // 押されたら切り替えをすぐ戻して、ボタンとして振る舞わせる（ゲームの画面には手を入れない）
    internal sealed class Tile
    {
        private readonly Action _click;
        private float _lastClick = -10f;

        [UIObject("tile")]
        private GameObject _object;

        private Tile(Action click)
        {
            _click = click;
        }

        public Toggle Toggle { get; private set; }
        private TextMeshProUGUI Text { get; set; }

        public bool Interactable
        {
            get => Toggle != null && Toggle.interactable;
            set
            {
                if (Toggle != null)
                {
                    Toggle.interactable = value;
                }
            }
        }

        // parent に置く。width・height は UI の単位。アイコンは出さない。backing は板の裏に敷く黒の濃さ（0 で敷かない）
        // 名前が長いときに小さくする下限
        private const float MinFontSize = 2.4f;

        public static Tile Create(GameObject parent, string text, float width, float height, Action click, TextAlignmentOptions alignment = TextAlignmentOptions.Center, float fontSize = 3.6f, string hint = null, float backing = 0f)
        {
            var tile = new Tile(click);
            var hintAttribute = string.IsNullOrEmpty(hint) ? "" : $" hover-hint='{System.Security.SecurityElement.Escape(hint)}'";
            BeatSaberMarkupLanguage.BSMLParser.Instance.Parse($"<modifier id='tile'{hintAttribute}/>", parent, tile);
            var go = tile._object;
            if (go == null)
            {
                return tile;
            }
            var layout = go.GetComponent<LayoutElement>() ?? go.AddComponent<LayoutElement>();
            layout.preferredWidth = width;
            layout.preferredHeight = height;
            layout.minWidth = width;
            layout.minHeight = height;
            var rect = (RectTransform)go.transform;
            rect.sizeDelta = new Vector2(width, height);

            var icon = go.transform.Find("Icon");
            if (icon != null)
            {
                icon.gameObject.SetActive(false);
            }
            var name = go.transform.Find("Name");
            if (name != null)
            {
                tile.Text = name.GetComponent<TextMeshProUGUI>();
                // 板いっぱいに文字を置く（アイコンの分を空けない）
                var nameRect = (RectTransform)name;
                nameRect.anchorMin = Vector2.zero;
                nameRect.anchorMax = Vector2.one;
                nameRect.offsetMin = new Vector2(2f, 0.5f);
                nameRect.offsetMax = new Vector2(-2f, -0.5f);
            }
            foreach (var localized in go.GetComponentsInChildren<BGLib.Polyglot.LocalizedTextMeshProUGUI>(true))
            {
                UnityEngine.Object.Destroy(localized);
            }
            if (tile.Text != null)
            {
                tile.Text.richText = true;
                tile.Text.enableWordWrapping = false;
                tile.Text.alignment = alignment;
                tile.Text.fontSize = fontSize;
                // 見積もりより長い名前は、枠に収まるところまで小さくする（はみ出して隣に重ならないように）
                tile.Text.enableAutoSizing = true;
                tile.Text.fontSizeMax = fontSize;
                tile.Text.fontSizeMin = Math.Min(fontSize, MinFontSize);
                tile.Text.text = text;
            }

            // BSML の ToggleSetting は有効になるたびに切り替えへ聞き手を足し、値を戻し合って止まらなくなる（フリーズした）ので外す
            var setting = go.GetComponent<BeatSaberMarkupLanguage.Components.Settings.ToggleSetting>();
            if (setting != null)
            {
                UnityEngine.Object.DestroyImmediate(setting);
            }
            // 押せる範囲をタイルの枠にそろえる。複製の中の絵は元の大きさのままなので、タイルを低くすると下のタイルに重なり、
            // 押したタイルの一つ下が選ばれた。中の絵は押下を受けず、枠いっぱいの透明な板だけが受ける
            foreach (var graphic in go.GetComponentsInChildren<Graphic>(true))
            {
                graphic.raycastTarget = false;
            }
            var hit = new GameObject("HitArea", typeof(RectTransform), typeof(Image), typeof(LayoutElement));
            hit.GetComponent<LayoutElement>().ignoreLayout = true;
            var hitRect = (RectTransform)hit.transform;
            hitRect.SetParent(go.transform, false);
            hitRect.anchorMin = Vector2.zero;
            hitRect.anchorMax = Vector2.one;
            hitRect.offsetMin = Vector2.zero;
            hitRect.offsetMax = Vector2.zero;
            var hitImage = hit.GetComponent<Image>();
            hitImage.color = Color.clear;
            hitImage.raycastTarget = true;

            if (backing > 0f)
            {
                AddBacking(go, backing);
            }

            tile.Toggle = go.GetComponent<Toggle>();
            if (tile.Toggle != null)
            {
                tile.Toggle.onValueChanged.RemoveAllListeners();
                tile.Toggle.group = null;
                tile.Toggle.SetIsOnWithoutNotify(false);
                tile.Toggle.onValueChanged.AddListener(on =>
                {
                    if (!on)
                    {
                        return;
                    }
                    tile.Toggle.SetIsOnWithoutNotify(false);
                    // 続けて届いた押下は無視する
                    if (Time.unscaledTime - tile._lastClick < 0.3f)
                    {
                        return;
                    }
                    tile._lastClick = Time.unscaledTime;
                    tile._click?.Invoke();
                });
            }
            return tile;
        }

        // 透けた板（浮かぶ板）の上では、ゲームのタイルの板が背景に溶けて読みにくかった（2026-10-16）。
        // タイルの板と同じ形の黒い板を重ねて濃くする。タイルの色の切り替え（選んだとき・押せないとき）はそのまま
        private static void AddBacking(GameObject tile, float alpha)
        {
            var bg = tile.transform.Find("BG")?.GetComponent<HMUI.ImageView>();
            if (bg == null)
            {
                Plugin.Log.Debug("[tile] the modifier tile has no BG; no backing is added");
                return;
            }
            // タイルの直下に足すと、タイルの名前が右へずれた（2026-10-16。並びが変わったためと推測）。タイルの子の並びは変えず、BG の子として BG の上に重ねる。
            // 名前は BG より後ろの兄弟なので、黒い板より手前に出る（推測）
            var backing = new GameObject("OctantPathBacking", typeof(RectTransform), typeof(LayoutElement));
            backing.GetComponent<LayoutElement>().ignoreLayout = true;
            var rect = (RectTransform)backing.transform;
            rect.SetParent(bg.transform, false);
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            var image = backing.AddComponent<HMUI.ImageView>();
            image.sprite = bg.sprite;
            image.type = bg.type;
            image.pixelsPerUnitMultiplier = bg.pixelsPerUnitMultiplier;
            image.material = bg.material;
            image.color = new Color(0f, 0f, 0f, alpha);
            image.raycastTarget = false;
        }
    }
}
