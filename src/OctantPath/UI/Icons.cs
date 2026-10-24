using System.Collections.Generic;
using System.IO;

namespace OctantPath.UI
{
    // ボタンの絵。OctantPath が自分で描いた白一色の絵（細めの線、角は尖らせる）（UI/Icons/*.png）を MOD に同梱して使う。
    // 見た目はゲームの絵（白一色・太い線）に揃えるが、ゲームの絵は写さず借りもしない（ライセンスをきれいに保つ。2026-10-16）。
    // 読めなかったら null を返し、呼ぶ側は文字の記号に戻す
    internal static class Icons
    {
        public const string Download = "download";
        public const string Swap = "swap";
        public const string Play = "play";
        public const string Discard = "discard";
        public const string Add = "add";

        private static readonly Dictionary<string, UnityEngine.Sprite> Loaded = new Dictionary<string, UnityEngine.Sprite>();

        public static UnityEngine.Sprite Find(string name)
        {
            if (Loaded.TryGetValue(name, out var sprite) && sprite != null)
            {
                return sprite;
            }
            using (var stream = typeof(Icons).Assembly.GetManifestResourceStream($"OctantPath.Icons.{name}.png"))
            {
                if (stream == null)
                {
                    Plugin.Log.Warn($"[icons] {name}.png is not in the MOD; using the text symbol");
                    return null;
                }
                using (var memory = new MemoryStream())
                {
                    stream.CopyTo(memory);
                    sprite = BeatSaberMarkupLanguage.Utilities.LoadSpriteRaw(memory.ToArray());
                }
            }
            Loaded[name] = sprite;
            return sprite;
        }
    }
}
