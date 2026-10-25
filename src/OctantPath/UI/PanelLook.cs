using System.Linq;

namespace OctantPath.UI
{
    // 板の下の縁の真ん中の位置と、板の向き・曲がり方
    internal readonly struct PanelBase
    {
        public PanelBase(UnityEngine.Vector3 bottom, UnityEngine.Quaternion rotation, float radius)
        {
            Bottom = bottom;
            Rotation = rotation;
            Radius = radius;
        }

        public UnityEngine.Vector3 Bottom { get; }
        public UnityEngine.Quaternion Rotation { get; }
        public float Radius { get; }
    }

    // 浮かぶ板の見た目と置き方。BSML の浮かぶ画面の背景（Background）は不透明に近い灰色なので、透けるようにする
    internal static class PanelLook
    {
        // ゲームの画面との隙間（m）と、手前へ寄せる距離（m）と、手前へ傾ける角度。
        // 画面のすぐ上には戻るボタンの帯と、入れている人の多い時計の MOD（SimpleClock）が出るので、それより上に置く。
        // 上げるだけだと遠く見えたので、手前へも寄せる（2026-10-24）。値は開発用の PC で手で動かして置いていた位置
        // （中心 y 3.76・z 3.68。曲選択の画面の中心は y 1.28・z 4.35、元の既定の中心は y 3.29）に合わせた推測の値
        private const float Gap = 0.52f;
        private const float Closer = 0.67f;
        private const float Tilt = 20f;

        // BSML の浮かぶ画面の標準の大きさ。設定の倍率（recommendationPanelScale）をかける
        private const float StandardScale = 0.02f;

        // 設定の倍率の範囲。設定画面のスライダーもこの範囲（SettingsMenu）
        public const float MinScale = 0.5f;
        public const float MaxScale = 3f;

        public static float Scale(double setting) => StandardScale * UnityEngine.Mathf.Clamp((float)setting, MinScale, MaxScale);

        // ゲームの画面（曲選択・結果画面）の上に、少し手前へ寄せ、同じ曲がり方で置き、少し手前へ（下向きに）傾ける。
        // height は板の高さ（m）
        public static (UnityEngine.Vector3 Position, UnityEngine.Quaternion Rotation, float Radius) Above(HMUI.Screen screen, float height)
        {
            var rect = (UnityEngine.RectTransform)screen.transform;
            var scale = rect.lossyScale.y;
            var curved = screen.GetComponentInChildren<HMUI.CurvedCanvasSettings>() ?? screen.GetComponentInParent<HMUI.CurvedCanvasSettings>();
            var radius = curved != null ? curved.radius : 0f;
            var position = rect.position + rect.up * (rect.rect.height * scale / 2f + height / 2f + Gap) - rect.forward * Closer;
            var rotation = rect.rotation * UnityEngine.Quaternion.Euler(-Tilt, 0f, 0f);
            return (position, rotation, radius);
        }

        // 持ち手は、板の上の縁の真ん中のすぐ上に小さなつまみとして出す（2026-10-18）。BSML の Top は上の縁いっぱいの棒なので、幅を詰めて縁の外へ出す。
        // 板は手前へ曲がるが、真ん中は曲がる前の面（z = 0）にあるので、厚みの分だけ手前へ出す。持ち手の大きさが変わる BSML の UpdateHandle の後に呼ぶ
        public static void PlaceKnob(BeatSaberMarkupLanguage.FloatingScreen.FloatingScreen panel)
        {
            var handle = panel.Handle;
            if (handle == null)
            {
                return;
            }
            var size = panel.ScreenSize;
            handle.transform.localPosition = new UnityEngine.Vector3(0f, size.y / 2f + KnobHeight / 2f + KnobGap, -KnobHeight);
            handle.transform.localScale = new UnityEngine.Vector3(KnobWidth, KnobHeight, KnobHeight);
        }

        // 持ち手の色は OctantPath の青（自動で次への札と同じ #4C9AFF。2026-10-24 に白より青がよいと言われた）。
        // BSML の持ち手は、作ったときに自分の材質を白にし、指したときにもその材質を白に塗り直す。
        // 見せる材質を別のものに差し替えると、BSML が塗り直すのは見えない元の材質になり、青のまま残る。
        // 光らせない（a は Custom/Glowing の光の強さで、BSML の白も 0）
        public static void PaintKnob(BeatSaberMarkupLanguage.FloatingScreen.FloatingScreen panel)
        {
            var renderer = panel.Handle != null ? panel.Handle.GetComponent<UnityEngine.MeshRenderer>() : null;
            if (renderer == null)
            {
                return;
            }
            var material = new UnityEngine.Material(renderer.sharedMaterial);
            material.SetColor(ColorId, KnobColor);
            renderer.sharedMaterial = material;
        }

        private static readonly int ColorId = UnityEngine.Shader.PropertyToID("_Color");
        private static readonly UnityEngine.Color KnobColor = new UnityEngine.Color(0.298f, 0.604f, 1f, 0f);

        // つまみの大きさと、縁との隙間（UI の単位。推測で決めた値）
        private const float KnobWidth = 20f;
        private const float KnobHeight = 3f;
        private const float KnobGap = 0.5f;

        // 背景の不透明度（設定の panelOpacity）。透けすぎて文字が読めなくならないよう下限を置く（設定画面のスライダーも同じ範囲）
        public const float MinOpacity = 0.2f;

        public static float Opacity(double setting) => UnityEngine.Mathf.Clamp((float)setting, MinOpacity, 1f);

        // 進み具合の帯の伸びる所を、地の左から割合（0〜1）の所まで伸ばす（width は全部終わったときの長さ）
        public static void SetFill(UnityEngine.GameObject fill, float fraction, float width)
        {
            var layout = fill.GetComponent<UnityEngine.UI.LayoutElement>() ?? fill.AddComponent<UnityEngine.UI.LayoutElement>();
            layout.preferredWidth = width * UnityEngine.Mathf.Clamp01(fraction);
        }

        // BSML の bg は、ゲームの板（キーボードの下地）の絵を、色の掛け方（グラデーションの色と材質）ごと写す。
        // そのままだと bg-color に明るい色を書いても暗く出た（帯が黒く見えた）。色の掛け方を、BSML の image と同じ素直なものに直す
        public static void Brighten(UnityEngine.GameObject withBackground)
        {
            var background = withBackground != null ? withBackground.GetComponent<BeatSaberMarkupLanguage.Components.Backgroundable>()?.Background as HMUI.ImageView : null;
            if (background == null)
            {
                return;
            }
            background.material = BeatSaberMarkupLanguage.Utilities.ImageResources.NoGlowMat;
            background.gradient = false;
            background.color0 = UnityEngine.Color.white;
            background.color1 = UnityEngine.Color.white;
        }

        public static void MakeTranslucent(BeatSaberMarkupLanguage.FloatingScreen.FloatingScreen panel, double opacity)
        {
            var background = panel.GetComponentsInChildren<HMUI.ImageView>(true).FirstOrDefault(i => i.name == "Background");
            if (background == null)
            {
                Plugin.Log.Warn($"[panel] {panel.name} has no background to make translucent");
                return;
            }
            var color = background.color;
            color.a = Opacity(opacity);
            background.color = color;
        }
    }
}
