using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using OctantPath.Core.Settings;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace OctantPath.Desktop
{
    // 設定の画面（2026-10-13: OctantCompass の窓の「詳細設定」と同じ形）に出す項目。
    // settings.json は MOD と同じ形だが、Desktop は自分のデータフォルダの settings.json を読むので、ここで変えても MOD には効かない。
    // 出すのは Desktop が使う項目のうち、人が変える値だけ（お試し枠・板の大きさなどゲームの中だけの項目は出さない）。言語は上の帯で選ぶ。
    // 待ち時間・BeatSaver への間隔・zip の上限・API の URL などの内部の値は出さない（defaults.json にあり、settings.json に書けば変えられる）。
    // settings.json に書くのは、ここで変えた項目と言語だけ（既定値に戻した項目は消す）
    public static class DesktopSettings
    {
        public enum Kind { Int, Number, Text, Folder }

        public sealed class Item
        {
            public string Key;
            public string Group;
            public Kind Kind;
            // 数値の下限（これより小さい値は受け付けない）
            public double Min;
            // settings.json ではなく Desktop だけの desktop.json に書く項目（保存するとすぐ使う）
            public bool Local;
        }

        public static readonly IReadOnlyList<Item> Items = new List<Item>
        {
            new Item { Key = "gameFolder", Group = "game", Kind = Kind.Folder, Local = true },
            new Item { Key = "mixDraftCount", Group = "draft", Kind = Kind.Int, Min = 1 },
            new Item { Key = "zipSource", Group = "maps", Kind = Kind.Text },
            new Item { Key = "environmentName", Group = "compass", Kind = Kind.Text },
        };

        public static bool IsLocal(string key) => Items.Any(i => i.Key == key && i.Local);

        // 画面に出す今の値と既定値。ゲームのフォルダは desktop.json の値（空なら今使っているもの。--game・--userdata から求めたもの）
        public static IEnumerable<object> View(OctantPathSettings settings, DesktopLocal local, string game)
        {
            var current = JObject.FromObject(settings);
            var defaults = OctantPathSettings.DefaultValues();
            current["gameFolder"] = string.IsNullOrEmpty(local.GameFolder) ? game ?? "" : local.GameFolder;
            defaults["gameFolder"] = "";
            return Items.Select(i => new
            {
                key = i.Key,
                group = i.Group,
                kind = i.Kind.ToString().ToLowerInvariant(),
                min = i.Kind == Kind.Int || i.Kind == Kind.Number ? i.Min : (double?)null,
                value = current[i.Key],
                @default = defaults[i.Key],
            });
        }

        // 画面で入れた文字を、項目の型の値にする。使えなければ null と理由のキー
        public static (JToken Value, string Error) Parse(string key, string text)
        {
            var item = Items.FirstOrDefault(i => i.Key == key);
            if (item == null)
            {
                return (null, "settings.unknown");
            }
            text = (text ?? "").Trim();
            switch (item.Kind)
            {
                case Kind.Int:
                    return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var whole) && whole >= item.Min
                        ? (new JValue(whole), null) : (null, "settings.invalid");
                case Kind.Number:
                    return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && !double.IsNaN(number) && !double.IsInfinity(number) && number >= item.Min
                        ? (new JValue(number), null) : (null, "settings.invalid");
                case Kind.Folder:
                    // 空ならゲームのフォルダを使わない（導入済みを出さない）。入れるなら、あるフォルダ
                    return text.Length == 0 || System.IO.Directory.Exists(DesktopClient.LocalPath(text))
                        ? (new JValue(text), null) : (null, "settings.noFolder");
                default:
                    if (key == "zipSource" && text.Length == 0)
                    {
                        return (null, "settings.invalid");
                    }
                    return (new JValue(text), null);
            }
        }

        // 名前の横に出す版。InformationalVersion は "<版>+<コミット>"（.NET 8 の SDK が git のコミットを足す）
        public static (string Version, string Commit) Build()
        {
            var info = typeof(DesktopSettings).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
            var plus = info.IndexOf('+');
            var version = plus < 0 ? info : info.Substring(0, plus);
            var commit = plus < 0 ? null : info.Substring(plus + 1);
            return (version.Length == 0 ? null : version, string.IsNullOrEmpty(commit) ? null : commit.Substring(0, Math.Min(7, commit.Length)));
        }
    }
}
