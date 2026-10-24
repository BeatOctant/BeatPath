using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace OctantPath.Core.Languages
{
    // 画面に出す文。言語ごとの JSON（キー → 文）から引く（2026-10-11: 英語を既定に、日本語を同梱）。
    // 同梱の言語は DLL に埋め込み、UserData/OctantPath/lang/<言語>.json があればそちらを優先する。
    // 新しい言語はファイルを置くだけで足せる。引けないキーは英語、それもなければキーそのものを出す。
    // ログには使わない（ログは言語を変えない）
    public static class Lang
    {
        public const string Default = "en";
        public const string Auto = "auto";

        private static readonly Regex Placeholder = new Regex(@"\{(\w+)\}", RegexOptions.Compiled);

        private static Dictionary<string, string> _fallback = new Dictionary<string, string>();
        private static Dictionary<string, string> _current = new Dictionary<string, string>();

        public static string Current { get; private set; } = Default;

        static Lang()
        {
            Use(Default, null);
        }

        // 同梱の言語（埋め込みの名前は OctantPath.Lang.<言語>.json）
        public static IEnumerable<string> Bundled =>
            typeof(Lang).Assembly.GetManifestResourceNames()
                .Where(n => n.StartsWith("OctantPath.Lang.", StringComparison.Ordinal) && n.EndsWith(".json", StringComparison.Ordinal))
                .Select(n => n.Substring("OctantPath.Lang.".Length, n.Length - "OctantPath.Lang.".Length - ".json".Length));

        // 使える言語。同梱のものと、フォルダに置いたもの
        public static List<string> Available(string folder)
        {
            var found = new HashSet<string>(Bundled, StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
            {
                foreach (var file in Directory.GetFiles(folder, "*.json"))
                {
                    found.Add(Path.GetFileNameWithoutExtension(file));
                }
            }
            return found.OrderBy(l => l, StringComparer.Ordinal).ToList();
        }

        // その言語自身での名前（言語ファイルの "_language"。フォルダに置いたものを優先）。なければ言語の名前そのもの
        public static string NameOf(string language, string folder)
        {
            if (!string.IsNullOrEmpty(folder))
            {
                var path = Path.Combine(folder, language + ".json");
                try
                {
                    if (File.Exists(path) && JObject.Parse(File.ReadAllText(path))["_language"] is JValue named && named.Type == JTokenType.String)
                    {
                        return (string)named;
                    }
                }
                catch (Exception)
                {
                    // 読めないファイルは Use が知らせる。名前は同梱のものか言語の名前にする
                }
            }
            var name = typeof(Lang).Assembly.GetManifestResourceNames()
                .FirstOrDefault(n => string.Equals(n, $"OctantPath.Lang.{language}.json", StringComparison.OrdinalIgnoreCase));
            if (name != null)
            {
                using (var stream = typeof(Lang).Assembly.GetManifestResourceStream(name))
                using (var reader = new StreamReader(stream))
                {
                    if (JObject.Parse(reader.ReadToEnd())["_language"] is JValue named && named.Type == JTokenType.String)
                    {
                        return (string)named;
                    }
                }
            }
            return language;
        }

        // 設定の値（"auto" か言語）から使う言語を決める。auto はゲームの言語（英語以外のとき）、
        // OS の言語の順に、使えるものを選ぶ。どれも使えなければ英語
        public static string Resolve(string setting, string gameLanguage, CultureInfo os, IEnumerable<string> available)
        {
            var known = new HashSet<string>(available ?? Bundled, StringComparer.OrdinalIgnoreCase);
            string Pick(string language)
            {
                if (string.IsNullOrEmpty(language))
                {
                    return null;
                }
                if (known.Contains(language))
                {
                    return known.First(k => string.Equals(k, language, StringComparison.OrdinalIgnoreCase));
                }
                var dash = language.IndexOfAny(new[] { '-', '_' });
                return dash > 0 ? Pick(language.Substring(0, dash)) : null;
            }

            if (!string.IsNullOrEmpty(setting) && !string.Equals(setting, Auto, StringComparison.OrdinalIgnoreCase))
            {
                return Pick(setting) ?? Default;
            }
            if (!string.IsNullOrEmpty(gameLanguage) && !string.Equals(gameLanguage, Default, StringComparison.OrdinalIgnoreCase))
            {
                var game = Pick(gameLanguage);
                if (game != null)
                {
                    return game;
                }
            }
            return Pick(os?.Name) ?? Default;
        }

        // 言語を切り替える。folder は差し替えのファイルを置くフォルダ（なければ同梱だけ）。warn は読めないファイルの知らせ
        public static void Use(string language, string folder, Action<string> warn = null)
        {
            language = string.IsNullOrEmpty(language) ? Default : language;
            _fallback = Read(Default, folder, warn);
            _current = string.Equals(language, Default, StringComparison.OrdinalIgnoreCase) ? _fallback : Read(language, folder, warn);
            Current = language;
        }

        public static string T(string key, object args = null)
        {
            var text = _current.TryGetValue(key, out var found) ? found : _fallback.TryGetValue(key, out var english) ? english : key;
            return Interpolate(text, args);
        }

        // 文の {名前} を args の同じ名前のプロパティの値に置き換える（数は InvariantCulture）。知らない名前はそのまま残す。
        // Desktop の文（DesktopLang）も同じ決まりで差し込む
        public static string Interpolate(string text, object args)
        {
            if (args == null || text == null)
            {
                return text;
            }
            var values = args.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .ToDictionary(p => p.Name, p => p.GetValue(args, null), StringComparer.Ordinal);
            return Placeholder.Replace(text, m => values.TryGetValue(m.Groups[1].Value, out var v) ? Format(v) : m.Value);
        }

        // BSML の中の {{キー}} を、その文に置き換える（属性の値に入るので、XML の記号は逃がす）
        public static string Fill(string markup) =>
            markup == null ? null : Regex.Replace(markup, @"\{\{([\w.]+)\}\}", m => System.Security.SecurityElement.Escape(T(m.Groups[1].Value)));

        // 外から来る表示名（手札の名前など）。文字列ならそのまま、{言語: 文字列} なら今の言語、英語、最初の値の順に選ぶ
        public static string Pick(JToken text)
        {
            if (text == null || text.Type == JTokenType.Null)
            {
                return null;
            }
            if (text.Type == JTokenType.String)
            {
                return (string)text;
            }
            if (text is JObject map)
            {
                foreach (var language in new[] { Current, Default })
                {
                    var exact = map.Properties().FirstOrDefault(p => string.Equals(p.Name, language, StringComparison.OrdinalIgnoreCase));
                    if (exact?.Value.Type == JTokenType.String)
                    {
                        return (string)exact.Value;
                    }
                }
                return map.Properties().Where(p => p.Value.Type == JTokenType.String).Select(p => (string)p.Value).FirstOrDefault();
            }
            return text.ToString(Formatting.None);
        }

        // キーの一覧（テストで、言語どうしの抜けを確かめる）
        public static Dictionary<string, string> Bundle(string language) => ReadEmbedded(language) ?? new Dictionary<string, string>();

        private static string Format(object value) =>
            value is IFormattable f ? f.ToString(null, CultureInfo.InvariantCulture) : value?.ToString() ?? "";

        private static Dictionary<string, string> Read(string language, string folder, Action<string> warn)
        {
            var texts = ReadEmbedded(language) ?? new Dictionary<string, string>();
            if (!string.IsNullOrEmpty(folder))
            {
                var path = Path.Combine(folder, language + ".json");
                if (File.Exists(path))
                {
                    try
                    {
                        foreach (var pair in Parse(File.ReadAllText(path)))
                        {
                            texts[pair.Key] = pair.Value;
                        }
                    }
                    catch (Exception ex)
                    {
                        warn?.Invoke($"{path} could not be read; using the bundled texts: {ex.Message}");
                    }
                }
            }
            return texts;
        }

        private static Dictionary<string, string> ReadEmbedded(string language)
        {
            var name = typeof(Lang).Assembly.GetManifestResourceNames()
                .FirstOrDefault(n => string.Equals(n, $"OctantPath.Lang.{language}.json", StringComparison.OrdinalIgnoreCase));
            if (name == null)
            {
                return null;
            }
            using (var stream = typeof(Lang).Assembly.GetManifestResourceStream(name))
            using (var reader = new StreamReader(stream))
            {
                return Parse(reader.ReadToEnd());
            }
        }

        // "_" で始まるキーは説明用で、文としては使わない
        private static Dictionary<string, string> Parse(string json) =>
            JObject.Parse(json).Properties()
                .Where(p => !p.Name.StartsWith("_", StringComparison.Ordinal) && p.Value.Type == JTokenType.String)
                .ToDictionary(p => p.Name, p => (string)p.Value, StringComparer.Ordinal);
    }

    // 外から来る表示名を、文字列か {言語: 文字列} のどちらでも読み、今の言語の文字列にする
    public sealed class LangTextConverter : JsonConverter
    {
        public override bool CanConvert(Type objectType) => objectType == typeof(string);

        public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer) =>
            Lang.Pick(JToken.Load(reader));

        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer) => writer.WriteValue((string)value);
    }
}
