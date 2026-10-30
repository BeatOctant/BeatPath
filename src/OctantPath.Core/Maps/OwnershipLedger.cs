using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OctantPath.Core.Settings;
using Newtonsoft.Json;

namespace OctantPath.Core.Maps
{
    // OctantPath が入れたマップの所有の記録（UserData/OctantPath/ledger.json）。
    // 検証と設置が済み、SongCore が読み込んだあとにだけ足す（OctantPath 法 3.1）
    public sealed class OwnedMap
    {
        [JsonProperty("hash")] public string Hash { get; set; }
        [JsonProperty("mapKey")] public string MapKey { get; set; }
        [JsonProperty("folder")] public string Folder { get; set; }
        [JsonProperty("acquiredAt")] public DateTime AcquiredAt { get; set; }
        [JsonProperty("state")] public string State { get; set; } = MapStates.Trial;

        // どの枠で入ったか（同じマップが複数の枠に出たら、すべて）。取った譜面と要求 id も枠ごとに持つ
        [JsonProperty("frames")] public List<FrameEntry> Frames { get; set; } = new List<FrameEntry>();

        // 状態が変わった日時と、残したときの移し先
        [JsonProperty("changedAt", NullValueHandling = NullValueHandling.Ignore)] public DateTime? ChangedAt { get; set; }
        [JsonProperty("keptFolder", NullValueHandling = NullValueHandling.Ignore)] public string KeptFolder { get; set; }

        public IEnumerable<string> FrameIds() => Frames.Select(f => f.Frame);
    }

    public sealed class FrameEntry
    {
        public const string Manual = "manual";
        // ミックスで取ったマップの枠
        public const string Mix = "mix";

        [JsonProperty("frame")] public string Frame { get; set; }
        [JsonProperty("requestId")] public string RequestId { get; set; }
        // 推薦の要求で使った手札の id。取得を確かめたときの recommendation の signal に入れる
        [JsonProperty("preset", NullValueHandling = NullValueHandling.Ignore)] public string Preset { get; set; }
        // その譜面を出した手札の id（混ぜた推薦のとき。候補の reason.hand）。同じく signal に入れる
        [JsonProperty("hand", NullValueHandling = NullValueHandling.Ignore)] public string Hand { get; set; }
        [JsonProperty("characteristic")] public string Characteristic { get; set; }
        [JsonProperty("difficulty")] public string Difficulty { get; set; }
        [JsonProperty("at")] public DateTime At { get; set; }
    }

    public static class MapStates
    {
        public const string Trial = "trial";
        public const string Kept = "kept";
        public const string Deleted = "deleted";
        public const string DeletedByUser = "deleted_by_user";
    }

    public sealed class LedgerFile
    {
        [JsonProperty("formatVersion")] public int FormatVersion { get; set; } = 1;
        [JsonProperty("maps")] public List<OwnedMap> Maps { get; set; } = new List<OwnedMap>();
    }

    public sealed class OwnershipLedger
    {
        public const string FileName = "ledger.json";

        private readonly string _path;

        public OwnershipLedger(string directory)
        {
            _path = Path.Combine(directory, FileName);
        }

        // 読めないとき（壊れている、知らない形式）は null。そのときは所有を推測しない（OctantPath 法 3.2）
        public LedgerFile Load()
        {
            if (!File.Exists(_path))
            {
                return new LedgerFile();
            }
            try
            {
                var ledger = JsonConvert.DeserializeObject<LedgerFile>(File.ReadAllText(_path));
                return ledger != null && ledger.FormatVersion == 1 && ledger.Maps != null ? ledger : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        // ユーザーが消した: 台帳で削除済みにする（記録は残す）
        public bool MarkDeletedByUser(string hash, string folder)
        {
            var ledger = Load();
            var map = ledger?.Maps.Find(m => m.Hash == hash && m.Folder == folder && m.State == MapStates.Trial);
            if (map == null)
            {
                return false;
            }
            map.State = MapStates.DeletedByUser;
            map.ChangedAt = DateTime.UtcNow;
            JsonFile.WriteAtomic(_path, JsonConvert.SerializeObject(ledger, Formatting.Indented));
            return true;
        }

        // 足せなかったら false（台帳が読めないときは書き換えない）
        public bool Add(OwnedMap map)
        {
            var ledger = Load();
            if (ledger == null)
            {
                return false;
            }
            ledger.Maps.RemoveAll(m => m.Hash == map.Hash && m.Folder == map.Folder);
            ledger.Maps.Add(map);
            Save(ledger);
            return true;
        }

        // お試し中の同じマップに、別の枠を足す
        public bool AddFrame(string hash, FrameEntry frame)
        {
            var ledger = Load();
            var map = ledger?.Maps.Find(m => m.Hash == hash && m.State == MapStates.Trial);
            if (map == null)
            {
                return false;
            }
            if (!map.Frames.Any(f => f.Frame == frame.Frame))
            {
                map.Frames.Add(frame);
                Save(ledger);
            }
            return true;
        }

        public void Save(LedgerFile ledger)
        {
            JsonFile.WriteAtomic(_path, JsonConvert.SerializeObject(ledger, Formatting.Indented));
        }
    }
}
