using System;
using System.Collections.Generic;
using System.IO;
using OctantPath.Core.Settings;
using Newtonsoft.Json;

namespace OctantPath.Core.Maps
{
    // 検証して置いたが、SongCore の読み込みをまだ確かめていないマップ（UserData/OctantPath/placed.json）。
    // 読み込みを確かめたら所有の記録に移す（OctantPath 法 2.5、3.1）。ゲームを閉じても、次の起動の読み込みで確かめられる
    public sealed class PlacedMap
    {
        [JsonProperty("hash")] public string Hash { get; set; }
        [JsonProperty("mapKey")] public string MapKey { get; set; }
        [JsonProperty("folder")] public string Folder { get; set; }
        [JsonProperty("frame")] public FrameEntry Frame { get; set; }
        [JsonProperty("placedAt")] public DateTime PlacedAt { get; set; }
    }

    public sealed class PlacedStore
    {
        public const string FileName = "placed.json";

        private readonly string _path;
        private readonly object _gate = new object();

        public PlacedStore(string directory)
        {
            _path = Path.Combine(directory, FileName);
        }

        public List<PlacedMap> Load()
        {
            lock (_gate)
            {
                if (!File.Exists(_path))
                {
                    return new List<PlacedMap>();
                }
                try
                {
                    return JsonConvert.DeserializeObject<List<PlacedMap>>(File.ReadAllText(_path)) ?? new List<PlacedMap>();
                }
                catch (Exception)
                {
                    // 読めないときは、置いたことを証拠にしない（所有は記録しない。OctantPath 法 3.2）
                    return new List<PlacedMap>();
                }
            }
        }

        public void Add(PlacedMap map)
        {
            lock (_gate)
            {
                var all = Load();
                all.RemoveAll(m => m.Hash == map.Hash && m.Folder == map.Folder);
                all.Add(map);
                JsonFile.WriteAtomic(_path, JsonConvert.SerializeObject(all, Formatting.Indented));
            }
        }

        public void Remove(IEnumerable<PlacedMap> maps)
        {
            lock (_gate)
            {
                var all = Load();
                foreach (var map in maps)
                {
                    all.RemoveAll(m => m.Hash == map.Hash && m.Folder == map.Folder);
                }
                JsonFile.WriteAtomic(_path, JsonConvert.SerializeObject(all, Formatting.Indented));
            }
        }
    }
}
