using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using OctantPath.Core.Maps;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace OctantPath.Desktop
{
    // 手元にあるマップ: ゲームの曲フォルダ（読むだけ）と、Desktop が取得したマップ（Desktop のフォルダ）。
    // Core の OwnedMap（OctantPath が自分で入れた証拠。OctantPath 法 3.1）とは別物なので、名前を LibraryMap にしている。
    // MOD は SongCore に聞くが、Desktop はゲームの外なので、フォルダごとに OctantPath.Core の MapHash で hash を出す。
    // 出した hash と曲の情報はフォルダの更新時刻と一緒に Desktop のフォルダに覚えておき、次の起動では変わったフォルダだけ読み直す
    public sealed class MapLibrary
    {
        public const string GameSource = "game";
        public const string DesktopSource = "desktop";

        private readonly List<LibraryRoot> _roots;
        private readonly string _cachePath;
        private readonly object _gate = new object();
        private Dictionary<string, LibraryMap> _byFolder = new Dictionary<string, LibraryMap>();
        private Dictionary<string, LibraryMap> _byHash = new Dictionary<string, LibraryMap>();

        public MapLibrary(string game, string desktopLevels, string cachePath)
        {
            Game = game;
            _cachePath = cachePath;
            _roots = new List<LibraryRoot>();
            if (game != null)
            {
                _roots.Add(new LibraryRoot(Path.Combine(game, "Beat Saber_Data", "CustomLevels"), GameSource));
                _roots.Add(new LibraryRoot(Path.Combine(game, "Beat Saber_Data", "CustomWIPLevels"), GameSource));
                _roots.Add(new LibraryRoot(Path.Combine(game, "UserData", "OctantPath", "Levels"), GameSource));
            }
            _roots.Add(new LibraryRoot(desktopLevels, DesktopSource));
        }

        public string Game { get; }
        public bool Scanning { get; private set; }
        public string Problem { get; private set; }

        public int Count(string source)
        {
            lock (_gate)
            {
                return _byFolder.Values.Count(m => m.Source == source);
            }
        }

        // ゲームに入っているか（Desktop が取得したものは含めない）
        public bool InGame(string hash) => Find(hash)?.Source == GameSource;

        public LibraryMap Find(string hash)
        {
            var h = MapHash.Normalize(hash);
            lock (_gate)
            {
                return h != null && _byHash.TryGetValue(h, out var map) && Directory.Exists(map.Folder) ? map : null;
            }
        }

        // 新しいもの（フォルダの更新時刻）から。q は曲名・作者・マッパー・キーの部分一致
        public List<LibraryMap> List(string source, string q)
        {
            List<LibraryMap> all;
            lock (_gate)
            {
                all = _byFolder.Values.ToList();
            }
            var words = (q ?? "").Split(new[] { ' ', '　' }, StringSplitOptions.RemoveEmptyEntries);
            return all
                .Where(m => string.IsNullOrEmpty(source) || m.Source == source)
                .Where(m => words.All(w => m.SearchText.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0))
                .OrderByDescending(m => m.Stamp)
                .ToList();
        }

        // 読み始める前から Scanning にしておく（直後に一覧を取った画面が、空のまま読み終わったと思わないように）
        public Task ScanAsync()
        {
            Scanning = true;
            return Task.Run(Scan);
        }

        // Desktop が取得したマップを足す（取得のあとすぐ一覧に出す）
        public void Add(string folder)
        {
            var map = Read(folder, DesktopSource, Stamp(folder));
            if (map?.Hash == null)
            {
                return;
            }
            lock (_gate)
            {
                _byFolder[folder] = map;
                _byHash[map.Hash] = Prefer(_byHash.TryGetValue(map.Hash, out var old) ? old : null, map);
            }
        }

        private void Scan()
        {
            Scanning = true;
            try
            {
                var cache = LoadCache();
                var next = new Dictionary<string, LibraryMap>();
                var missing = _roots.Where(r => r.Source == GameSource && !Directory.Exists(r.Path)).Select(r => r.Path).ToList();
                Problem = Game != null && missing.Count == 3 ? DesktopLang.T("where.gameMissing", new { path = DesktopLang.ShownPath(_roots[0].Path) }) : null;
                foreach (var root in _roots.Where(r => Directory.Exists(r.Path)))
                {
                    foreach (var folder in SafeDirectories(root.Path))
                    {
                        var stamp = Stamp(folder);
                        if (!cache.TryGetValue(folder, out var map) || map.Stamp != stamp || map.Source != root.Source)
                        {
                            map = Read(folder, root.Source, stamp);
                        }
                        if (map != null)
                        {
                            next[folder] = map;
                        }
                        // 読み終えた分から使えるようにする（曲が多いと最初の読み込みに時間がかかる）
                        if (next.Count % 200 == 0)
                        {
                            Publish(next);
                        }
                    }
                }
                Publish(next);
                SaveCache(next);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Problem = DesktopLang.T("where.gameUnreadable", new { error = ex.Message });
            }
            finally
            {
                Scanning = false;
            }
        }

        private void Publish(Dictionary<string, LibraryMap> maps)
        {
            var byHash = new Dictionary<string, LibraryMap>();
            foreach (var map in maps.Values.Where(m => m.Hash != null))
            {
                byHash[map.Hash] = Prefer(byHash.TryGetValue(map.Hash, out var old) ? old : null, map);
            }
            lock (_gate)
            {
                _byFolder = new Dictionary<string, LibraryMap>(maps);
                _byHash = byHash;
            }
        }

        // 同じ曲がゲームにも Desktop にもあるときは、ゲームのものを指す（導入済みとして扱う）
        private static LibraryMap Prefer(LibraryMap a, LibraryMap b) => a == null ? b : a.Source == GameSource ? a : b;

        // 一覧に出す情報を読む。hash が出せないフォルダも、壊れたものとして一覧に残す（理由は hash が出せないことを先に出す）
        private static LibraryMap Read(string folder, string source, long stamp)
        {
            var computed = MapHash.Compute(folder);
            var map = ReadInfo(folder);
            map.Source = source;
            map.Stamp = stamp;
            map.Hash = computed.Succeeded ? computed.Hash : null;
            map.Problem = computed.Succeeded ? map.Problem : computed.Error;
            return map;
        }

        // フォルダの Info.dat（v2 と v4）から、曲の情報と難易度を読む。hash は出さない（取得したマップの詳細もこれで読む）。
        // 読めなければ Problem に理由を入れる
        public static LibraryMap ReadInfo(string folder)
        {
            var map = new LibraryMap
            {
                Folder = folder,
                Title = Path.GetFileName(folder),
                Key = KeyFromFolder(Path.GetFileName(folder)),
            };
            var infoPath = MapHash.FindInfo(folder);
            if (infoPath == null)
            {
                map.Problem = DesktopLang.T("preview.noInfo");
                return map;
            }
            try
            {
                var info = JObject.Parse(File.ReadAllText(infoPath));
                if (info["_difficultyBeatmapSets"] is JArray sets)
                {
                    map.Title = (string)info["_songName"] ?? map.Title;
                    map.SubTitle = (string)info["_songSubName"];
                    map.Artist = (string)info["_songAuthorName"];
                    map.Mapper = (string)info["_levelAuthorName"];
                    map.Bpm = (double?)info["_beatsPerMinute"];
                    map.Cover = (string)info["_coverImageFilename"];
                    map.Song = (string)info["_songFilename"];
                    map.Diffs = sets.SelectMany(s => ((s["_difficultyBeatmaps"] as JArray) ?? new JArray()).Select(d => new LibraryDiff
                    {
                        Characteristic = (string)s["_beatmapCharacteristicName"],
                        Difficulty = (string)d["_difficulty"],
                    })).ToList();
                }
                else if (info["difficultyBeatmaps"] is JArray v4)
                {
                    map.Title = (string)info["song"]?["title"] ?? map.Title;
                    map.SubTitle = (string)info["song"]?["subTitle"];
                    map.Artist = (string)info["song"]?["author"];
                    map.Mapper = string.Join(", ", v4.SelectMany(d => (d["beatmapAuthors"]?["mappers"] as JArray)?.Select(x => (string)x) ?? Enumerable.Empty<string>()).Distinct());
                    map.Bpm = (double?)info["audio"]?["bpm"];
                    map.Cover = (string)info["coverImageFilename"];
                    map.Song = (string)info["audio"]?["songFilename"];
                    map.Diffs = v4.Select(d => new LibraryDiff { Characteristic = (string)d["characteristic"], Difficulty = (string)d["difficulty"] }).ToList();
                }
            }
            catch (Exception ex) when (ex is IOException || ex is JsonException || ex is InvalidCastException || ex is FormatException)
            {
                map.Problem = DesktopLang.T("preview.infoUnreadable", new { error = ex.Message });
            }
            return map;
        }

        // BeatSaver から取ったフォルダ名は「キー (曲名 - マッパー)」の形（推測: よくある取得ツールの名付け）
        private static string KeyFromFolder(string name)
        {
            var space = name.IndexOf(' ');
            var key = (space > 0 ? name.Substring(0, space) : name).ToLowerInvariant();
            return space > 0 && key.Length <= 6 && key.All(Uri.IsHexDigit) ? key : null;
        }

        private static IEnumerable<string> SafeDirectories(string root)
        {
            try
            {
                return Directory.GetDirectories(root);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return Enumerable.Empty<string>();
            }
        }

        // フォルダと Info.dat の更新時刻。どちらかが変われば読み直す
        private static long Stamp(string folder)
        {
            var info = MapHash.FindInfo(folder);
            var time = Directory.GetLastWriteTimeUtc(folder).Ticks;
            return info == null ? time : Math.Max(time, File.GetLastWriteTimeUtc(info).Ticks);
        }

        private Dictionary<string, LibraryMap> LoadCache()
        {
            try
            {
                if (File.Exists(_cachePath))
                {
                    return JsonConvert.DeserializeObject<Dictionary<string, LibraryMap>>(File.ReadAllText(_cachePath)) ?? new Dictionary<string, LibraryMap>();
                }
            }
            catch (Exception ex) when (ex is IOException || ex is JsonException)
            {
            }
            return new Dictionary<string, LibraryMap>();
        }

        private void SaveCache(Dictionary<string, LibraryMap> cache)
        {
            try
            {
                File.WriteAllText(_cachePath, JsonConvert.SerializeObject(cache));
            }
            catch (IOException)
            {
            }
        }

        private sealed class LibraryRoot
        {
            public LibraryRoot(string path, string source)
            {
                Path = path;
                Source = source;
            }

            public string Path { get; }
            public string Source { get; }
        }
    }

    public sealed class LibraryMap
    {
        [JsonProperty("folder")] public string Folder { get; set; }
        [JsonProperty("source")] public string Source { get; set; }
        [JsonProperty("stamp")] public long Stamp { get; set; }
        [JsonProperty("hash")] public string Hash { get; set; }
        [JsonProperty("problem")] public string Problem { get; set; }
        [JsonProperty("key")] public string Key { get; set; }
        [JsonProperty("title")] public string Title { get; set; }
        [JsonProperty("subTitle")] public string SubTitle { get; set; }
        [JsonProperty("artist")] public string Artist { get; set; }
        [JsonProperty("mapper")] public string Mapper { get; set; }
        [JsonProperty("bpm")] public double? Bpm { get; set; }
        [JsonProperty("cover")] public string Cover { get; set; }
        [JsonProperty("song")] public string Song { get; set; }
        [JsonProperty("diffs")] public List<LibraryDiff> Diffs { get; set; } = new List<LibraryDiff>();

        [JsonIgnore] public string SearchText => $"{Title} {SubTitle} {Artist} {Mapper} {Key} {Path.GetFileName(Folder)}";

        // フォルダの中のファイル。名前が Info.dat の外を指していれば null
        public string FilePath(string name)
        {
            if (string.IsNullOrEmpty(name) || !MapHash.SafeName(name))
            {
                return null;
            }
            var path = Path.Combine(Folder, name);
            return File.Exists(path) ? path : null;
        }
    }

    public sealed class LibraryDiff
    {
        [JsonProperty("characteristic")] public string Characteristic { get; set; }
        [JsonProperty("difficulty")] public string Difficulty { get; set; }
    }
}
