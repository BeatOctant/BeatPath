using System;
using System.Collections.Generic;
using System.IO;
using OctantPath.Core.Settings;
using Newtonsoft.Json;

namespace OctantPath.Core.Signals
{
    // 導入前の記録を取り込んだか（UserData/OctantPath/state.json）。取り込みは初回だけ
    public sealed class ImportState
    {
        public const string FileName = "state.json";

        [JsonProperty("importedAt")] public DateTime? ImportedAt { get; set; }

        // ゲームを起動した日（ローカルの日付、yyyy-MM-dd）。お試しの期限を起動日で数えるため
        [JsonProperty("launchDays", ObjectCreationHandling = ObjectCreationHandling.Replace)] public List<string> LaunchDays { get; set; } = new List<string>();

        public static ImportState Load(string directory, out bool readable)
        {
            readable = true;
            var path = Path.Combine(directory, FileName);
            if (!File.Exists(path))
            {
                return new ImportState();
            }
            try
            {
                return JsonConvert.DeserializeObject<ImportState>(File.ReadAllText(path)) ?? new ImportState();
            }
            catch (Exception)
            {
                readable = false;
                return null;
            }
        }

        public void Save(string directory)
        {
            JsonFile.WriteAtomic(Path.Combine(directory, FileName), JsonConvert.SerializeObject(this, Formatting.Indented));
        }
    }
}
