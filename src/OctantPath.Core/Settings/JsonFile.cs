using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace OctantPath.Core.Settings
{
    public static class JsonFile
    {
        // 途中で失敗しても前の正しいファイルが残るよう、別のファイルに書いてから入れ替える（OctantPath 法 5.5）
        public static void WriteAtomic(string path, string text)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            var temp = path + ".tmp";
            // 入れ替えたあとに電源が落ちても中身が空にならないよう、入れ替える前にディスクまで書く
            using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var bytes = new UTF8Encoding(false).GetBytes(text);
                file.Write(bytes, 0, bytes.Length);
                file.Flush(true);
            }
            if (File.Exists(path))
            {
                File.Replace(temp, path, null);
            }
            else
            {
                File.Move(temp, path);
            }
        }

        // JSON のオブジェクトを書いたファイルの、1 つの項目だけを書き換える。ほかの項目（知らないものも）は残す。
        // value が null ならその項目を消す（もとからなければ書かない）。ファイルがオブジェクトとして読めなければ JsonException を投げ、書かない
        public static void SetValue(string path, string key, JToken value)
        {
            var current = File.Exists(path) ? JObject.Parse(File.ReadAllText(path)) : new JObject();
            if (value == null)
            {
                if (!current.Remove(key))
                {
                    return;
                }
            }
            else
            {
                current[key] = value;
            }
            WriteAtomic(path, current.ToString(Formatting.Indented));
        }
    }
}
