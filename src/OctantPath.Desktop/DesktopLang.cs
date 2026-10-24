using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OctantPath.Core.Languages;
using Newtonsoft.Json.Linq;

namespace OctantPath.Desktop
{
    // Desktop の画面の文（2026-10-11: 言語を画面で切り替え、日本語と英語を分ける）。
    // 文は Languages/<言語>.json（キー → 文）にあり、サーバーの知らせと画面（/api/lang で渡す）の両方がここから引く。
    // MOD と同じ OctantPath.Core の文（接続の状態など）も、同じ言語に切り替える。引けないキーは英語、それもなければキーそのもの
    public static class DesktopLang
    {
        private const string Prefix = "OctantPath.Desktop.Lang.";

        private static Dictionary<string, string> _fallback = Read(Lang.Default);
        private static Dictionary<string, string> _current = _fallback;

        public static string Current { get; private set; } = Lang.Default;

        // 同梱の言語と、その言語自身での名前（"_language"）
        public static List<(string Id, string Label)> Available() =>
            typeof(DesktopLang).Assembly.GetManifestResourceNames()
                .Where(n => n.StartsWith(Prefix, StringComparison.Ordinal) && n.EndsWith(".json", StringComparison.Ordinal))
                .Select(n => n.Substring(Prefix.Length, n.Length - Prefix.Length - ".json".Length))
                .OrderBy(id => id, StringComparer.Ordinal)
                .Select(id => (id, Read(id).TryGetValue("_language", out var label) ? label : id))
                .ToList();

        // 設定の値（"auto" か言語）から使う言語を決める。auto は OS の言語（環境変数 LC_ALL・LC_MESSAGES・LANG）、なければ英語。
        // Desktop は InvariantGlobalization なので CultureInfo からは OS の言語を知れない（推測: Windows では英語になる）
        public static string Resolve(string setting)
        {
            var ids = Available().Select(a => a.Id).ToList();
            string Pick(string language)
            {
                if (string.IsNullOrEmpty(language))
                {
                    return null;
                }
                var exact = ids.FirstOrDefault(id => string.Equals(id, language, StringComparison.OrdinalIgnoreCase));
                if (exact != null)
                {
                    return exact;
                }
                var cut = language.IndexOfAny(new[] { '-', '_', '.', '@' });
                return cut > 0 ? Pick(language.Substring(0, cut)) : null;
            }

            if (!string.IsNullOrEmpty(setting) && !string.Equals(setting, Lang.Auto, StringComparison.OrdinalIgnoreCase))
            {
                return Pick(setting) ?? Lang.Default;
            }
            foreach (var name in new[] { "LC_ALL", "LC_MESSAGES", "LANG" })
            {
                var found = Pick(Environment.GetEnvironmentVariable(name));
                if (found != null)
                {
                    return found;
                }
            }
            return Lang.Default;
        }

        public static void Use(string language, string coreFolder = null, Action<string> warn = null)
        {
            _current = string.Equals(language, Lang.Default, StringComparison.OrdinalIgnoreCase) ? _fallback : Read(language);
            Current = language;
            // OctantPath.Core の文（接続の状態、取得の失敗など）も同じ言語にする。Core に無い言語なら英語
            Lang.Use(Lang.Available(coreFolder).Contains(language) ? language : Lang.Default, coreFolder, warn);
        }

        public static string T(string key, object args = null)
        {
            var text = _current.TryGetValue(key, out var found) ? found : _fallback.TryGetValue(key, out var english) ? english : key;
            return Lang.Interpolate(text, args);
        }

        // 画面に出すパス。ホームフォルダの部分は「~」にする（画面を撮ってもユーザー名が出ないように。2026-10-18）。
        // 表示だけで、保存先や設定の値は変えない
        public static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        public static string ShownPath(string path)
        {
            var home = Home.TrimEnd('/', '\\');
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(home) || !path.StartsWith(home, StringComparison.Ordinal))
            {
                return path;
            }
            if (path.Length == home.Length)
            {
                return "~";
            }
            return path[home.Length] == '/' || path[home.Length] == '\\' ? "~" + path.Substring(home.Length) : path;
        }

        // 画面に渡す文（今の言語に、抜けたキーは英語で埋めたもの）
        public static Dictionary<string, string> Texts()
        {
            var texts = new Dictionary<string, string>(_fallback, StringComparer.Ordinal);
            foreach (var pair in _current)
            {
                texts[pair.Key] = pair.Value;
            }
            return texts;
        }

        private static Dictionary<string, string> Read(string language)
        {
            var name = typeof(DesktopLang).Assembly.GetManifestResourceNames()
                .FirstOrDefault(n => string.Equals(n, $"{Prefix}{language}.json", StringComparison.OrdinalIgnoreCase));
            if (name == null)
            {
                return new Dictionary<string, string>(StringComparer.Ordinal);
            }
            using (var stream = typeof(DesktopLang).Assembly.GetManifestResourceStream(name))
            using (var reader = new StreamReader(stream))
            {
                return JObject.Parse(reader.ReadToEnd()).Properties()
                    .Where(p => p.Value.Type == JTokenType.String)
                    .ToDictionary(p => p.Name, p => (string)p.Value, StringComparer.Ordinal);
            }
        }
    }
}
