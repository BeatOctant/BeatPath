using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using OctantPath.Core.Languages;

namespace OctantPath.Core.Signals
{
    // signal の正本（UserData/OctantPath/signals.jsonl）。1 行 1 event、追記だけ。
    // 書き込みの途中で止まった最後の行は、書けなかったものとして切り捨てる。
    // 途中の行が読めないときは、正しい状態を作り上げず、読めなかったことを示す（OctantPath 法 5.5）
    public sealed class SignalLog
    {
        public const string FileName = "signals.jsonl";

        private readonly string _path;
        private readonly List<SignalEvent> _events = new List<SignalEvent>();

        private SignalLog(string path)
        {
            _path = path;
        }

        public IReadOnlyList<SignalEvent> Events => _events;
        public int Count => _events.Count;

        // 読めないときは null と理由
        public static SignalLog Open(string directory, out string error)
        {
            error = null;
            var log = new SignalLog(Path.Combine(directory, FileName));
            if (!File.Exists(log._path))
            {
                return log;
            }

            var bytes = File.ReadAllBytes(log._path);
            var text = Encoding.UTF8.GetString(bytes);
            var lines = text.Split('\n');
            long validLength = 0;
            var offset = 0;
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var isLast = i == lines.Length - 1;
                var lineBytes = Encoding.UTF8.GetByteCount(line) + (isLast ? 0 : 1);
                if (line.Trim().Length == 0)
                {
                    offset += lineBytes;
                    if (!isLast)
                    {
                        validLength = offset;
                    }
                    continue;
                }
                SignalEvent e = null;
                try
                {
                    e = JsonConvert.DeserializeObject<SignalEvent>(line);
                }
                catch (JsonException)
                {
                }
                var complete = !isLast;
                if (e == null || string.IsNullOrEmpty(e.Type))
                {
                    if (!complete || IsOnlyBlankAfter(lines, i))
                    {
                        // 最後の行だけが壊れている: 書いている途中で止まったもの
                        break;
                    }
                    error = Lang.T("signals.lineUnreadable", new { line = i + 1 });
                    return null;
                }
                if (!complete)
                {
                    // 改行で終わっていない最後の行も、書いている途中で止まったものとして扱う
                    break;
                }
                log._events.Add(e);
                offset += lineBytes;
                validLength = offset;
            }

            if (validLength < bytes.Length)
            {
                using (var file = new FileStream(log._path, FileMode.Open, FileAccess.Write))
                {
                    file.SetLength(validLength);
                }
            }
            return log;
        }

        private static bool IsOnlyBlankAfter(string[] lines, int index)
        {
            for (var j = index + 1; j < lines.Length; j++)
            {
                if (lines[j].Trim().Length != 0)
                {
                    return false;
                }
            }
            return true;
        }

        // ファイルに書けてから記録に入れる
        public IReadOnlyList<SignalEvent> Append(IEnumerable<SignalEvent> events)
        {
            var added = new List<SignalEvent>();
            var builder = new StringBuilder();
            foreach (var e in events)
            {
                builder.Append(JsonConvert.SerializeObject(e)).Append('\n');
                added.Add(e);
            }
            if (added.Count == 0)
            {
                return added;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(_path));
            var data = new UTF8Encoding(false).GetBytes(builder.ToString());
            using (var file = new FileStream(_path, FileMode.Append, FileAccess.Write))
            {
                file.Write(data, 0, data.Length);
                file.Flush(true);
            }
            _events.AddRange(added);
            return added;
        }

        // 先頭から count 件のあとに残っているもの（OctantCompass にまだ送っていない分）
        public List<SignalEvent> After(int count) => _events.Skip(count).ToList();

        // 最後に送ったお気に入りの状態（hash ごとに、最後の like / like_removed）
        public HashSet<string> Liked()
        {
            var liked = new HashSet<string>();
            foreach (var e in _events)
            {
                if (e.Type == SignalTypes.Like)
                {
                    liked.Add(e.Hash);
                }
                else if (e.Type == SignalTypes.LikeRemoved)
                {
                    liked.Remove(e.Hash);
                }
            }
            return liked;
        }

        // hash ごとの最後の投票
        public Dictionary<string, string> Votes()
        {
            var votes = new Dictionary<string, string>();
            foreach (var e in _events.Where(e => e.Type == SignalTypes.Upvote || e.Type == SignalTypes.Downvote))
            {
                votes[e.Hash] = e.Type;
            }
            return votes;
        }
    }
}
