using System;
using System.IO;
using OctantPath.Maps;
using Newtonsoft.Json;
using UnityEngine;

namespace OctantPath.UI
{
    // 浮かぶ板の置き場所。持ち手で動かしたら UserData/OctantPath/ui.json に残す（画面の状態で、設定ではない）
    internal sealed class PanelPlacement
    {
        private const string FileName = "ui.json";
        private readonly string _path;

        public PanelPlacement(OctantPathFolders folders)
        {
            _path = Path.Combine(folders.Root, FileName);
        }

        public bool TryLoad(out Vector3 position, out Quaternion rotation)
        {
            position = default;
            rotation = default;
            try
            {
                if (!File.Exists(_path))
                {
                    return false;
                }
                var data = JsonConvert.DeserializeObject<Data>(File.ReadAllText(_path));
                if (data?.Position == null || data.Position.Length != 3 || data.Rotation == null || data.Rotation.Length != 4)
                {
                    return false;
                }
                position = new Vector3(data.Position[0], data.Position[1], data.Position[2]);
                rotation = new Quaternion(data.Rotation[0], data.Rotation[1], data.Rotation[2], data.Rotation[3]);
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Log.Warn($"[panel] ui.json could not be read: {ex.Message}");
                return false;
            }
        }

        public void Save(Vector3 position, Quaternion rotation)
        {
            try
            {
                var data = new Data
                {
                    Position = new[] { position.x, position.y, position.z },
                    Rotation = new[] { rotation.x, rotation.y, rotation.z, rotation.w },
                };
                Core.Settings.JsonFile.WriteAtomic(_path, JsonConvert.SerializeObject(data, Formatting.Indented));
            }
            catch (Exception ex)
            {
                Plugin.Log.Warn($"[panel] ui.json could not be written: {ex.Message}");
            }
        }

        // 置き直した: 残した置き場所を消して、次からも既定の位置に置く
        public void Clear()
        {
            try
            {
                if (File.Exists(_path))
                {
                    File.Delete(_path);
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.Warn($"[panel] ui.json could not be removed: {ex.Message}");
            }
        }

        private sealed class Data
        {
            [JsonProperty("recommendationPanelPosition")] public float[] Position { get; set; }
            [JsonProperty("recommendationPanelRotation")] public float[] Rotation { get; set; }
        }
    }
}
