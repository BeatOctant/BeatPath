using System.IO;
using OctantPath.Core.Settings;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace OctantPath.Desktop
{
    // Desktop だけが使う設定（データフォルダの desktop.json）。settings.json は MOD と同じ形なので、そこには足さない（2026-10-13）。
    // ゲームのフォルダ（単体の実行ファイルで配ると --game を渡さないことが多いので、設定の画面で選べるようにする）と、画面のテーマ
    public sealed class DesktopLocal
    {
        public const string FileName = "desktop.json";

        // 画面のテーマ。auto は OS のものに従う（2026-10-19）
        public static readonly string[] Themes = { "auto", "dark", "light" };

        [JsonProperty("gameFolder")] public string GameFolder { get; set; }

        [JsonProperty("theme")] public string Theme { get; set; }

        public static DesktopLocal Load(string data)
        {
            var path = Path.Combine(data, FileName);
            try
            {
                return File.Exists(path) ? JsonConvert.DeserializeObject<DesktopLocal>(File.ReadAllText(path)) ?? new DesktopLocal() : new DesktopLocal();
            }
            catch (JsonException)
            {
                return new DesktopLocal();
            }
        }

        // 1 つの項目を書く。ほかの項目（知らないものも）は残す。読めないファイルは、この項目だけのものに書き直す
        public static void Save(string data, string key, JToken value)
        {
            var path = Path.Combine(data, FileName);
            try
            {
                JsonFile.SetValue(path, key, value);
            }
            catch (JsonException)
            {
                JsonFile.WriteAtomic(path, new JObject { [key] = value }.ToString(Formatting.Indented));
            }
        }
    }
}
