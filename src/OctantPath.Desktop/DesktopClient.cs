using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Threading;
using System;
using OctantPath.Core.Connection;
using OctantPath.Core.Languages;
using OctantPath.Core.Maps;
using OctantPath.Core.Mix;
using OctantPath.Core.Presets;
using OctantPath.Core.Recommendations;
using OctantPath.Core.Settings;
using ConnectionInfo = OctantPath.Core.Connection.ConnectionInfo;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json;

namespace OctantPath.Desktop
{
    // MOD の RecommendationTab と同じ流れ（板 → 種類 → 下見 → 作成）を、ゲームの外で持つ。
    // 順は OctantCompass が返した順のまま（憲法の原則 6）。signal は送らない（遊んだ記録がないため）
    public sealed class DesktopClient
    {
        // BeatSaver の API・CDN と ArcViewer の取得に付ける User-Agent
        public const string UserAgent = "OctantPath.Desktop";

        private readonly DesktopOptions _options;
        private readonly HttpClient _http = new HttpClient();
        private readonly HostConnection _connection;
        private readonly IConnectionStore _store;
        private readonly BeatSaverClient _beatSaver;
        private readonly MapInstaller _installer;
        private readonly ReceivedStore _received;
        // 設定の画面でゲームのフォルダを変えると作り直す（2026-10-16）
        private volatile MapLibrary _library;
        private readonly SemaphoreSlim _refreshLock = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _downloadLock = new SemaphoreSlim(1, 1);
        private readonly object _gate = new object();
        private OctantPathSettings _settings;
        private ConnectionStatus _status = new ConnectionStatus { State = ConnectionState.Unknown };
        private readonly PresetsCache _presets = new PresetsCache();

        private Seed _seed;
        private Draft _draft;
        private string _lastRequest;
        private string _lastResponse;
        private string _languageSetting;
        // プレイリストを前に保存したフォルダ（この起動のあいだ。次の保存の選ぶ窓はここから開く）
        private string _playlistFolder;

        public DesktopClient(DesktopOptions options)
        {
            _options = options;
            Folders = options.Data != null ? DesktopFolders.At(Path.GetFullPath(options.Data)) : DefaultFolders();
            UserData = options.UserData ?? Folders.Config;
            Folders.Create();
            Directory.CreateDirectory(UserData);
            _http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
            ReloadSettings();
            UseLanguage(_settings.Language);
            // Desktop は MOD とは別の環境として、自分の接続情報とトークンを持つ（2026-10-13）。OctantCompass は起動しない（契約6（client to OctantCompass）2.0）
            // トークンは設定とは別の「状態」の場所に置く（--userdata のときはそのフォルダのものを読むだけ）
            _store = new DesktopConnectionStore(new ConnectionStore(options.UserData ?? Folders.State, Warn), options.ReadOnly);
            _connection = new HostConnection(new HostClient(_http), _store, () => _settings, $"OctantPath.Desktop ({Environment.MachineName})");
            _beatSaver = new BeatSaverClient(_http, () => _settings);
            _installer = new MapInstaller(_http, () => _settings);
            _received = new ReceivedStore(Folders.Levels);
            UseGameFolder();
            ArcViewer = new ArcViewerMirror(Folders.Cache, () => _settings);
            _ = ArcViewer.StartAsync();
            Cdn = new BeatSaverCdn(UserAgent, () => _settings);
        }

        public ArcViewerMirror ArcViewer { get; }
        public BeatSaverCdn Cdn { get; }

        public string UserData { get; }
        public DesktopFolders Folders { get; }

        // 板の見出し（接続の状態）と手札。ready でなければ確かめ直し、revision が変わったら手札を取り直す（MOD の ConnectionService と同じ）
        public async Task<object> StatusAsync(CancellationToken ct)
        {
            await RefreshAsync(ct);
            var status = _status;
            var all = _presets.Presets?.Presets ?? new List<Preset>();
            return new
            {
                state = status.State.ToString(),
                ready = status.State == ConnectionState.Ready,
                text = DescribeStatus(status),
                // 下見の詳細で、理由のビューに役割名を添える
                viewRoles = status.ViewRoles,
                presetsProblem = PresetsProblem(),
                // 板のカード: 起点の要らない手札（MOD の BuildHomeButtons）
                home = PresetRules.Shown(all, false).Select(PresetView),
                // 「ミックス」の種類の小窓: 曲を起点にできる場所で出す手札（MOD の MixClick）
                mix = PresetRules.Shown(all, true).Select(PresetView),
                seed = _seed,
                // 難しさの上限（settings の difficultyCeiling）。種類の上のボタンで切り替える
                ceiling = _settings.DifficultyCeiling,
                userData = UserData,
                readOnly = _options.ReadOnly,
                data = Folders.Root,
                library = new { game = _library.Game, scanning = _library.Scanning, problem = _library.Problem },
                arcViewer = new { state = ArcViewer.State, progress = ArcViewer.Progress, error = ArcViewer.Error, version = ArcViewer.Version },
                // 画面が状態を確かめ直す間隔（設定の healthRetryIntervalSeconds。MOD が未接続のときに確かめ直す間隔と同じ）
                recheckSeconds = _settings.HealthRetryInterval.TotalSeconds,
            };
        }

        // 起点の曲（MOD では曲選択で選んでいる譜面）。BeatSaver のキー・hash・URL を受け付け、難易度は BeatSaver の版の一覧から選ぶ
        public async Task<object> SetSeedAsync(JObject body, CancellationToken ct)
        {
            var text = ((string)body["text"] ?? "").Trim();
            if (text.Length == 0)
            {
                _seed = null;
                return new { seed = (Seed)null };
            }
            var characteristic = NullIfEmpty((string)body["characteristic"]);
            var difficulty = NullIfEmpty((string)body["difficulty"]);
            if (_seed != null && (string.Equals(text, _seed.Input, StringComparison.OrdinalIgnoreCase) || MapHash.Normalize(text) == _seed.Hash))
            {
                // 同じ曲で難易度だけを変えた
                _seed.Characteristic = characteristic ?? _seed.Characteristic;
                _seed.Difficulty = difficulty ?? _seed.Difficulty;
                return new { seed = _seed };
            }

            ReloadSettings();
            BeatSaverMap map = null;
            string hash = MapHash.Normalize(text);
            string error = null;
            try
            {
                if (hash != null)
                {
                    (await _beatSaver.MapsByHashAsync(new[] { hash }, ct)).TryGetValue(hash, out map);
                }
                else
                {
                    var key = KeyOf(text);
                    if (key == null)
                    {
                        return new { error = DesktopLang.T("seed.badInput") };
                    }
                    map = await _beatSaver.MapByKeyAsync(key, ct);
                    if (map == null)
                    {
                        return new { error = DesktopLang.T("seed.keyNotFound", new { key }) };
                    }
                    var published = map.Versions.FirstOrDefault(v => v.State == "Published") ?? map.Versions.FirstOrDefault();
                    hash = MapHash.Normalize(published?.Hash);
                    if (hash == null)
                    {
                        return new { error = DesktopLang.T("seed.noVersion", new { key }) };
                    }
                }
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is JsonException || ex is BeatSaverException)
            {
                if (hash == null)
                {
                    return new { error = DesktopLang.T("seed.beatSaverFailed", new { error = ex.Message }) };
                }
                error = DesktopLang.T("seed.hashOnly", new { error = ex.Message });
            }

            var version = map?.Version(hash);
            var diffs = version?.Diffs?.Select(d => new SeedDiff { Characteristic = d.Characteristic, Difficulty = d.Difficulty, Nps = d.Nps, Njs = d.Njs }).ToList() ?? new List<SeedDiff>();
            // BeatSaver で引けない曲（消された版・自作）は、所有しているマップの Info.dat から出す
            var owned = version == null ? _library.Find(hash) : null;
            if (owned != null)
            {
                diffs = owned.Diffs.Select(d => new SeedDiff { Characteristic = d.Characteristic, Difficulty = d.Difficulty }).ToList();
                error = null;
            }
            // 難易度を言われなければ、Standard のいちばん上（推測: ふだん遊ぶ難易度に近いとみなす）
            var pick = diffs.LastOrDefault(d => d.Characteristic == "Standard") ?? diffs.LastOrDefault();
            _seed = new Seed
            {
                Input = text,
                Hash = hash,
                Key = map?.Key ?? owned?.Key,
                Title = map?.Metadata?.SongName ?? owned?.Title ?? hash,
                Artist = map?.Metadata?.SongAuthorName ?? owned?.Artist,
                Mapper = map?.Metadata?.LevelAuthorName ?? owned?.Mapper,
                Cover = version?.CoverUrl ?? (owned?.FilePath(owned.Cover) != null ? $"/api/library/{hash}/cover" : null),
                Diffs = diffs,
                Characteristic = characteristic ?? pick?.Characteristic,
                Difficulty = difficulty ?? pick?.Difficulty,
            };
            return new { seed = _seed, warning = error };
        }

        // 種類を選んだ（MOD の PresetChosen → BuildMix）。fromSong なら今の起点を使う。count×3 件を受け取り、count 件を並べる
        public async Task<object> BuildDraftAsync(JObject body, CancellationToken ct)
        {
            ReloadSettings();
            var id = (string)body["preset"];
            var preset = _presets.Presets?.Presets?.FirstOrDefault(p => p.Id == id);
            if (preset == null)
            {
                await ReloadPresetsAsync(ct);
                return new { error = DesktopLang.T("draft.presetGone") };
            }
            var fromSong = (bool?)body["fromSong"] ?? false;
            var seed = fromSong ? _seed?.ToChartRef() : null;
            if (PresetRules.NeedsSeed(preset) && seed == null)
            {
                return new { error = DesktopLang.T("seed.needed") };
            }
            var choices = body["choices"]?.ToObject<Dictionary<string, string>>();
            var draftCount = MixDraftRules.Size(_settings.MixDraftCount);
            var request = MixDraftRules.FirstRequest(preset, seed, choices, draftCount, _settings.DifficultyCeiling);
            var result = await RecommendAsync(request, ct);
            if (result.Error != null)
            {
                return new { error = result.Error };
            }
            var displayable = RecommendationDisplay.Shown(result.Items);
            var draft = new Draft
            {
                Preset = preset,
                Choices = choices,
                ChoiceLabel = (string)body["choiceLabel"],
                Seed = request.Seed,
                SeedLabel = request.Seed != null ? _seed?.Label : null,
                Mix = new MixDraft<RecommendationItem>(displayable, draftCount),
            };
            draft.Message = () => draft.Mix.Picked.Count == 0 ? DesktopLang.T("draft.noRecommendations") : DesktopLang.T("draft.hint");
            lock (_gate)
            {
                _draft = draft;
            }
            return DraftView();
        }

        // 下見の曲を外し、入れ替え用の次の曲をその場所に入れる。少なくなったら裏で追加を受け取る
        public object Replace(string id)
        {
            var draft = _draft;
            if (draft == null || draft.Creating)
            {
                return DraftView();
            }
            RecommendationItem next;
            lock (_gate)
            {
                var item = draft.Mix.Picked.FirstOrDefault(i => i.Id == id);
                if (item == null)
                {
                    return DraftView();
                }
                next = draft.Mix.Replace(item);
                draft.Message = () => next != null ? "" : DesktopLang.T("draft.toppingUp");
            }
            if (draft.Mix.RunningLow)
            {
                _ = TopUpAsync(draft);
            }
            return DraftView();
        }

        // 下見の 1 曲だけを取得する（MOD の ↓）
        public async Task<object> ReceiveOneAsync(string id)
        {
            var draft = _draft;
            var item = draft?.Mix.Picked.FirstOrDefault(i => i.Id == id);
            if (item == null || draft.Creating || _library.InGame(item.Hash))
            {
                return DraftView();
            }
            var outcome = (await ReceiveAsync(new[] { item }, CancellationToken.None, null)).Single();
            draft.Message = () => outcome.Error != null ? DesktopLang.T("draft.receiveFailed", new { error = outcome.Error }) : DesktopLang.T("draft.received", new { title = item.Title });
            return DraftView();
        }

        // 下見を確定する（MOD の「作成」）: 並んでいる曲だけを取得し、プレイリストを書く。進み具合は DraftView で見る
        public object Create()
        {
            var draft = _draft;
            if (draft == null || draft.Creating || draft.Mix.Picked.Count == 0)
            {
                return DraftView();
            }
            draft.Creating = true;
            _ = CreateAsync(draft);
            return DraftView();
        }

        public object CloseDraft()
        {
            lock (_gate)
            {
                if (_draft != null && !_draft.Creating)
                {
                    _draft = null;
                }
            }
            return DraftView();
        }

        public object DraftView()
        {
            var draft = _draft;
            if (draft == null)
            {
                return new { draft = (object)null };
            }
            lock (_gate)
            {
                return new
                {
                    draft = new
                    {
                        title = DesktopLang.T("draft.heading", new { preset = draft.Preset.Label + (string.IsNullOrEmpty(draft.ChoiceLabel) ? "" : ": " + draft.ChoiceLabel) }),
                        seed = draft.SeedLabel != null ? DesktopLang.T("draft.from", new { seed = draft.SeedLabel }) : DesktopLang.T("types.noSeed"),
                        items = draft.Mix.Picked.Select(i => ItemView(i)),
                        reserve = draft.Mix.ReserveCount,
                        message = draft.Message?.Invoke() ?? "",
                        creating = draft.Creating,
                        progress = draft.Progress,
                        playlist = draft.Playlist,
                    },
                };
            }
        }

        // 取得したマップの中身（Info.dat）。下見の詳細で、受け取ったものを見せる
        public object ReceivedInfo(string hash)
        {
            var folder = OwnedFolder(hash);
            if (folder == null)
            {
                return new { error = DesktopLang.T("preview.notReceivedYet") };
            }
            // 手元のマップの一覧と同じ読み方で Info.dat を読む
            var map = MapLibrary.ReadInfo(folder);
            if (map.Problem != null)
            {
                return new { folder, error = map.Problem };
            }
            return new
            {
                folder,
                bpm = map.Bpm,
                sets = map.Diffs.GroupBy(d => d.Characteristic).Select(g => new
                {
                    characteristic = g.Key,
                    difficulties = g.Select(d => new { difficulty = d.Difficulty }),
                }),
            };
        }

        // 取得したマップを zip にまとめ直して返す（ArcViewer の url= に渡すため）
        public byte[] ReceivedZip(string hash)
        {
            var folder = OwnedFolder(hash);
            if (folder == null)
            {
                return null;
            }
            using (var memory = new MemoryStream())
            {
                using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true))
                {
                    foreach (var file in Directory.GetFiles(folder).Where(f => Path.GetFileName(f) != ReceivedStore.MarkFile))
                    {
                        zip.CreateEntryFromFile(file, Path.GetFileName(file));
                    }
                }
                return memory.ToArray();
            }
        }

        // 所有しているマップの一覧（MapLibrary）。source は game・desktop・空（すべて）
        public object Library(string source, string q, int offset, int limit)
        {
            var maps = _library.List(source, q);
            return new
            {
                total = maps.Count,
                scanning = _library.Scanning,
                gameCount = _library.Count(MapLibrary.GameSource),
                desktopCount = _library.Count(MapLibrary.DesktopSource),
                items = maps.Skip(Math.Max(0, offset)).Take(Math.Max(1, Math.Min(500, limit))).Select(m => new
                {
                    hash = m.Hash,
                    title = m.Title,
                    subTitle = m.SubTitle,
                    artist = m.Artist,
                    mapper = m.Mapper,
                    bpm = m.Bpm,
                    folder = m.Folder,
                    hasCover = m.FilePath(m.Cover) != null,
                    hasSong = m.FilePath(m.Song) != null,
                    diffs = m.Diffs,
                    problem = m.Problem,
                }),
            };
        }

        // 所有しているマップのカバーか曲のファイル（読むだけ）
        public string LibraryFile(string hash, string kind)
        {
            var map = _library.Find(hash);
            return map == null ? null : map.FilePath(kind == "cover" ? map.Cover : map.Song);
        }

        private string OwnedFolder(string hash) => _received.Folder(hash) ?? _library.Find(hash)?.Folder;

        public string PlaylistPath(string name)
        {
            if (!MapInstaller.SafeFolderName(name) || !name.EndsWith(".bplist", StringComparison.Ordinal))
            {
                return null;
            }
            var path = Path.Combine(Folders.Playlists, name);
            return File.Exists(path) ? path : null;
        }

        // 「保存…」。作ったプレイリストを、pickFolder（OS のフォルダ選択）で選んだフォルダに同じ名前で写す。
        // 名前は作った時刻を含むので、同じ名前があればそれは前に保存した同じプレイリストで、上書きする
        public object SavePlaylist(string name, Func<string, string> pickFolder)
        {
            var source = PlaylistPath(name);
            if (source == null)
            {
                return new { error = DesktopLang.T("top.saveMissing") };
            }
            var folder = pickFolder(_playlistFolder);
            if (folder == null)
            {
                return new { cancelled = true };
            }
            var target = Path.Combine(folder, name);
            try
            {
                File.Copy(source, target, true);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return new { error = DesktopLang.T("top.saveFailed", new { problem = ex.Message }) };
            }
            _playlistFolder = folder;
            return new { path = target, text = DesktopLang.T("top.saved", new { path = DesktopLang.ShownPath(target) }) };
        }

        // 窓なし（--no-window）では保存先を選べないので、プレイリストのある場所を伝える
        public object PlaylistWithoutWindow(string name)
        {
            var source = PlaylistPath(name);
            return new { error = source == null ? DesktopLang.T("top.saveMissing") : DesktopLang.T("top.saveNoWindow", new { path = DesktopLang.ShownPath(source) }) };
        }

        // 最後に送った要求と受け取った応答（画面の「通信」で見る）
        public object Debug() => new { request = _lastRequest, response = _lastResponse };

        // 設定の画面（2026-10-13）。値と、置き場所などの情報
        public object SettingsView()
        {
            ReloadSettings();
            var build = DesktopSettings.Build();
            return new
            {
                path = System.IO.Path.Combine(UserData, OctantPathSettings.FileName),
                readOnly = _options.ReadOnly,
                items = DesktopSettings.View(_settings, DesktopLocal.Load(Folders.Config), _library.Game),
                info = new
                {
                    version = build.Version,
                    commit = build.Commit,
                    data = Folders.Root,
                    userData = UserData,
                    token = _options.UserData ?? Folders.State,
                    cache = Folders.Cache,
                    levels = Folders.Levels,
                    playlists = Folders.Playlists,
                    game = _library.Game,
                    octantCompass = _connection.ReadyBaseUri?.ToString(),
                },
            };
        }

        // 難しさの上限の入切（MOD の板の札と同じ settings の difficultyCeiling）。下見を開いていれば、画面が同じ種類で取り直す
        public object SetCeiling(bool on)
        {
            if (_options.ReadOnly)
            {
                return new { error = DesktopLang.T("settings.readOnly") };
            }
            OctantPathSettings.SaveValue(UserData, "difficultyCeiling", on);
            ReloadSettings();
            return new { ceiling = _settings.DifficultyCeiling };
        }

        // 1 つの項目を保存する。保存した値は次の操作からすぐ使う（Desktop は操作のたびに設定を読み直す）
        public object SaveSetting(JObject body)
        {
            if (_options.ReadOnly)
            {
                return new { error = DesktopLang.T("settings.readOnly") };
            }
            var key = (string)body["key"];
            var (value, error) = DesktopSettings.Parse(key, (string)body["value"]);
            if (error != null)
            {
                return new { error = DesktopLang.T(error) };
            }
            if (DesktopSettings.IsLocal(key))
            {
                // Desktop だけの項目（ゲームのフォルダ）は settings.json ではなく desktop.json に書き、すぐ読み直す（2026-10-16）
                DesktopLocal.Save(Folders.Config, key, value);
                UseGameFolder();
            }
            else
            {
                OctantPathSettings.SaveValue(UserData, key, value);
            }
            return SettingsView();
        }

        // 画面のテーマ（desktop.json。知らない値は auto）。ページを返すときに入れるので、描く前に決まる
        public string Theme()
        {
            var theme = DesktopLocal.Load(Folders.Config).Theme;
            return DesktopLocal.Themes.Contains(theme) ? theme : "auto";
        }

        // テーマを変える。Desktop 自身のフォルダの desktop.json に書くので、--userdata で読むだけのときも残る
        public object SetTheme(string value)
        {
            if (!DesktopLocal.Themes.Contains(value))
            {
                return new { error = "invalid_theme" };
            }
            DesktopLocal.Save(Folders.Config, "theme", value);
            return new { theme = value };
        }

        // 画面の言語。setting は設定の値（auto か言語）、language は今使っている言語。texts は画面の文
        public object LangView() => new
        {
            language = DesktopLang.Current,
            setting = _languageSetting ?? _settings.Language,
            auto = DesktopLang.Resolve(Core.Languages.Lang.Auto),
            available = DesktopLang.Available().Select(a => new { id = a.Id, label = a.Label }),
            texts = DesktopLang.Texts(),
            // 画面がパスの「~」を作るためのホームフォルダ（DesktopLang.ShownPath と同じ）
            home = DesktopLang.Home,
        };

        // 言語を変える。設定の language に書く（--userdata で読むだけのときは、この起動の間だけ）。
        // 手札の名前は受け取ったときの言語で読んでいるので、取り直す
        public async Task<object> SetLanguageAsync(string value, CancellationToken ct)
        {
            var setting = string.Equals(value, Core.Languages.Lang.Auto, StringComparison.OrdinalIgnoreCase) ? Core.Languages.Lang.Auto
                : DesktopLang.Available().Select(a => a.Id).FirstOrDefault(id => string.Equals(id, value, StringComparison.OrdinalIgnoreCase));
            if (setting == null)
            {
                return LangView();
            }
            if (_options.ReadOnly)
            {
                _languageSetting = setting;
            }
            else
            {
                OctantPathSettings.SaveValue(UserData, "language", setting);
                ReloadSettings();
            }
            UseLanguage(setting);
            if (_connection.ReadyBaseUri != null)
            {
                await ReloadPresetsAsync(ct);
            }
            return LangView();
        }

        private async Task CreateAsync(Draft draft)
        {
            try
            {
                var picked = draft.Mix.Picked.ToList();
                // ゲームに入っている曲は取らない（MOD の作成と同じ）
                var targets = picked.Where(i => !_library.InGame(i.Hash) && !_received.Has(i.Hash) && !string.IsNullOrEmpty(i.DownloadUrl)).ToList();
                draft.Progress = new Progress { Done = 0, Total = targets.Count };
                draft.Message = () => targets.Count > 0 ? DesktopLang.T("draft.fetching", new { done = 0, total = targets.Count }) : DesktopLang.T("draft.makingPlaylist");
                var outcomes = await ReceiveAsync(targets, CancellationToken.None, (done, total) =>
                {
                    draft.Progress = new Progress { Done = done, Total = total };
                    draft.Message = () => DesktopLang.T("draft.fetching", new { done, total });
                });
                var ready = picked.Where(i => _library.InGame(i.Hash) || _received.Has(i.Hash)).ToList();
                if (ready.Count == 0)
                {
                    draft.Message = () => DesktopLang.T("draft.nothingFetched");
                    return;
                }
                var title = $"OctantPath {draft.Preset.Label}{(string.IsNullOrEmpty(draft.ChoiceLabel) ? "" : ": " + draft.ChoiceLabel)}{(draft.SeedLabel != null ? " (" + draft.SeedLabel + ")" : "")}";
                draft.Playlist = WritePlaylist(title, ready);
                var missing = picked.Count - ready.Count;
                var fetched = outcomes.Count(o => o.Error == null);
                draft.Message = () => (fetched > 0 ? DesktopLang.T("draft.fetchedSome", new { count = fetched }) : "") + DesktopLang.T("draft.playlistDone", new { ready = ready.Count, folder = DesktopLang.ShownPath(Folders.Playlists) })
                    + (missing > 0 ? DesktopLang.T("draft.someFailed", new { count = missing, errors = string.Join(DesktopLang.T("draft.listSeparator"), outcomes.Where(o => o.Error != null).Select(o => o.Error).Distinct()) }) : "");
            }
            catch (Exception ex)
            {
                draft.Message = () => DesktopLang.T("draft.createFailed", new { error = ex.Message });
            }
            finally
            {
                draft.Creating = false;
            }
        }

        // まとめて取得する。同時数と 1 秒あたりの開始数は MOD と同じ設定に従う。ほかの取得の途中なら終わるのを待つ
        private async Task<List<ReceiveOutcome>> ReceiveAsync(IReadOnlyList<RecommendationItem> items, CancellationToken ct, Action<int, int> progress)
        {
            foreach (var item in items)
            {
                item.InFlight = true;
            }
            await _downloadLock.WaitAsync(ct);
            try
            {
                ReloadSettings();
                var throttle = new DownloadThrottle(_settings);
                var finished = 0;
                var tasks = new List<Task<ReceiveOutcome>>();
                foreach (var item in items)
                {
                    tasks.Add(await throttle.StartAsync(() => Task.Run(async () =>
                    {
                        try
                        {
                            return await ReceiveOneCoreAsync(item, ct);
                        }
                        finally
                        {
                            item.InFlight = false;
                            progress?.Invoke(Interlocked.Increment(ref finished), items.Count);
                        }
                    }), ct));
                }
                return (await Task.WhenAll(tasks)).ToList();
            }
            finally
            {
                foreach (var item in items)
                {
                    item.InFlight = false;
                }
                _downloadLock.Release();
            }
        }

        private async Task<ReceiveOutcome> ReceiveOneCoreAsync(RecommendationItem item, CancellationToken ct)
        {
            if (_received.Has(item.Hash))
            {
                return new ReceiveOutcome { Item = item };
            }
            InstallResult result;
            try
            {
                var folderName = MapInstaller.FolderName(item.Key ?? item.Hash.Substring(0, 8), item.Title, item.Mapper);
                result = await _installer.InstallFromSourceAsync(item.Hash, item.Version, folderName, Folders.Levels, Folders.Work, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException)
            {
                return new ReceiveOutcome { Item = item, Error = ex.Message };
            }
            if (!result.Succeeded)
            {
                return new ReceiveOutcome { Item = item, Error = result.Error };
            }
            _received.Add(item.Hash, result.Path);
            _library.Add(result.Path);
            return new ReceiveOutcome { Item = item };
        }

        // 同じ種類・起点・選択肢で、今まで出したものを除いて追加を受け取り、入れ替え用に足す（MOD の TopUpDraftAsync）
        private async Task TopUpAsync(Draft draft)
        {
            if (draft.ToppingUp)
            {
                return;
            }
            draft.ToppingUp = true;
            try
            {
                List<string> seen;
                lock (_gate)
                {
                    seen = draft.Mix.Seen.Select(i => i.Hash).ToList();
                }
                var request = MixDraftRules.TopUpRequest(draft.Preset, draft.Seed, draft.Choices, MixDraftRules.Size(_settings.MixDraftCount), seen, _settings.DifficultyCeiling);
                var result = await RecommendAsync(request, CancellationToken.None);
                if (result.Error != null)
                {
                    TopUpFailed(draft, result.Error);
                    return;
                }
                lock (_gate)
                {
                    var added = draft.Mix.AddReserve(RecommendationDisplay.Shown(result.Items));
                    if (draft.Message?.Invoke() == DesktopLang.T("draft.toppingUp"))
                    {
                        draft.Message = () => added > 0 ? "" : DesktopLang.T("draft.noMoreReserve");
                    }
                }
            }
            catch (Exception ex)
            {
                // 裏で動くので、ここで受け止めて知らせる（MOD の TopUpDraftAsync と同じ）
                Warn($"could not top up the draft: {ex.Message}");
                TopUpFailed(draft, ex.Message);
            }
            finally
            {
                draft.ToppingUp = false;
            }
        }

        // 「受け取っています」を出したままなら、受け取れなかったことに替える
        private void TopUpFailed(Draft draft, string error)
        {
            lock (_gate)
            {
                if (draft.Message?.Invoke() == DesktopLang.T("draft.toppingUp"))
                {
                    draft.Message = () => DesktopLang.T("draft.topUpFailed", new { error });
                }
            }
        }

        // 推薦の要求（MOD の RecommendationService と同じ: 表示のために BeatSaver から曲の情報を引く）
        private async Task<RecommendationResult> RecommendAsync(RecommendationRequest request, CancellationToken ct)
        {
            ReloadSettings();
            if (_connection.ReadyBaseUri == null)
            {
                await RefreshAsync(ct);
            }
            _lastRequest = JsonConvert.SerializeObject(request, Formatting.Indented);
            _lastResponse = null;
            var result = await _connection.RecommendAsync(request, ct);
            if (result == null)
            {
                _lastResponse = StatusText.Describe(_status);
                return RecommendationResult.Fail(DesktopLang.T("host.notConnected", new { status = StatusText.Describe(_status) }));
            }
            _lastResponse = result.Body != null ? JsonConvert.SerializeObject(result.Body, Formatting.Indented) : $"{result.Kind} HTTP {result.StatusCode} {result.Error?.Code} {result.Detail}";
            if (result.Kind != HostResultKind.Ok)
            {
                if (PresetsCache.NeedsReload(result))
                {
                    await ReloadPresetsAsync(ct);
                }
                else if (result.ShouldRecheck)
                {
                    await RefreshAsync(ct);
                }
                return RecommendationResult.Fail(Describe(result));
            }

            // 順は OctantCompass が返した順のまま（並べ替えない）
            return RecommendationResult.Ok(await RecommendationLookup.ItemsAsync(result.Body, _beatSaver, Warn, ct));
        }

        private static string Describe(HostResult<RecommendationResponse> result)
        {
            switch (RecommendationErrors.Classify(result))
            {
                case RecommendationProblem.SeedNotFound:
                    return DesktopLang.T("host.seedNotInDb");
                case RecommendationProblem.SeedNotEmbedded:
                    return DesktopLang.T("host.seedNotComputed");
                case RecommendationProblem.NotReady:
                    return DesktopLang.T("host.notReady");
                case RecommendationProblem.UnknownPreset:
                    return DesktopLang.T("draft.presetGone");
                case RecommendationProblem.InvalidRequest:
                    return DesktopLang.T("host.badRequest", new { message = result.Error.Message });
                case RecommendationProblem.Unreachable:
                    return DesktopLang.T("host.unreachable");
                default:
                    return DesktopLang.T("host.recommendFailed", new { detail = result.DetailOrStatus });
            }
        }

        private async Task RefreshAsync(CancellationToken ct)
        {
            if (!await _refreshLock.WaitAsync(0, ct))
            {
                return;
            }
            try
            {
                ReloadSettings();
                try
                {
                    _status = await _connection.RefreshAsync(ct);
                }
                catch (Exception ex) when (!(ex is OperationCanceledException))
                {
                    _status = new ConnectionStatus { State = ConnectionState.Error, Detail = ex.Message };
                }
                if (_presets.NeedsReload(_status))
                {
                    await ReloadPresetsAsync(ct);
                }
            }
            finally
            {
                _refreshLock.Release();
            }
        }

        private static string DescribeStatus(ConnectionStatus status) => StatusText.Describe(status);

        // 「確かめ直す」
        public async Task<object> RecheckAsync(CancellationToken ct)
        {
            await RefreshAsync(ct);
            return new { text = DescribeStatus(_status) };
        }

        // つなぐ先と合言葉でペアリングする（契約6 2.0。同じ PC でも合言葉を入れる）。address が空なら今のつなぐ先のまま
        public async Task<object> PairAsync(JObject body, CancellationToken ct)
        {
            var address = ((string)body["address"] ?? "").Trim();
            var code = ((string)body["code"] ?? "").Trim();
            string host = null;
            int? port = null;
            if (address.Length > 0 && !ConnectionInfo.TryParseAddress(address, out host, out port))
            {
                return new { ok = false, text = Lang.T("connectionView.badAddress") };
            }
            if (code.Length == 0)
            {
                return new { ok = false, text = DesktopLang.T("pair.noCode") };
            }
            await _refreshLock.WaitAsync(ct);
            try
            {
                _status = await _connection.PairAsync(code, host, port, ct);
            }
            finally
            {
                _refreshLock.Release();
            }
            await RefreshAsync(ct);
            return new { ok = _status.State != ConnectionState.PairingRejected && _status.State != ConnectionState.Unreachable, text = DescribeStatus(_status) };
        }

        // 画面のペアリングの欄に出す、今のつなぐ先（空なら同じ PC の既定）
        public string Address() => _store.Load()?.Address ?? "";

        private async Task ReloadPresetsAsync(CancellationToken ct)
        {
            _presets.Update(await _connection.GetPresetsAsync(ct));
        }

        // 手札の一覧が取れなかった理由。取れたら null
        private string PresetsProblem()
        {
            switch (_presets.Failure)
            {
                case null:
                    return null;
                case PresetsFailure.NotConnected:
                    return DesktopLang.T("host.notConnectedShort");
                default:
                    return DesktopLang.T("host.presetsFailed", new { detail = _presets.FailureDetail });
            }
        }

        private string WritePlaylist(string title, List<RecommendationItem> songs)
        {
            var name = "OctantPath-mix-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".bplist";
            var playlist = new JObject
            {
                ["playlistTitle"] = title,
                ["playlistAuthor"] = "OctantPath",
                ["playlistDescription"] = DesktopLang.T("draft.playlistDescription"),
                ["customData"] = new JObject { [OctantPath.Core.Trials.PlaylistFolder.SavedMixKey] = true },
                ["songs"] = new JArray(songs.Select(s => new JObject
                {
                    ["hash"] = s.Hash.ToUpperInvariant(),
                    ["songName"] = s.Title,
                    ["key"] = s.Key,
                    ["levelAuthorName"] = s.Mapper,
                    ["difficulties"] = new JArray(new JObject { ["characteristic"] = s.Characteristic, ["name"] = s.Difficulty }),
                })),
            };
            File.WriteAllText(Path.Combine(Folders.Playlists, name), playlist.ToString(Formatting.Indented));
            return name;
        }

        private object ItemView(RecommendationItem i) => new
        {
            id = i.Id,
            rank = i.Candidate.Rank,
            hash = i.Hash,
            characteristic = i.Characteristic,
            difficulty = i.Difficulty,
            reason = i.Candidate.Reason,
            key = i.Key,
            title = i.Title,
            subTitle = i.SubTitle,
            artist = i.Artist,
            mapper = i.Mapper,
            duration = i.Duration,
            cover = i.Cover,
            preview = i.Preview,
            nps = i.Nps,
            njs = i.Njs,
            state = _library.InGame(i.Hash) ? "installed" : _received.Has(i.Hash) ? "received" : i.InFlight ? "receiving" : string.IsNullOrEmpty(i.DownloadUrl) && ZipSources.IsBeatSaver(_settings.ZipSource) ? "unavailable" : "",
        };

        private static object PresetView(Preset p) => new
        {
            id = p.Id,
            label = p.Label,
            hint = p.Hint,
            group = p.Group,
            needsSeed = PresetRules.NeedsSeed(p),
            usesSeed = PresetRules.UsesSeed(p),
            choices = PresetRules.Choices(p).Select(c => new { name = c.Name, label = c.Label, options = c.Options.Select(o => new { value = o.Value, label = o.Label ?? o.Value }) }),
        };

        // 画面の言語を決めて切り替える（設定の language。auto は OS の言語、なければ英語。決め方は DesktopLang.Resolve）。
        // MOD と共通の文は UserData/lang に置いたファイルを同梱の文より優先する（読むだけ）
        private void UseLanguage(string setting)
        {
            var language = DesktopLang.Resolve(setting);
            DesktopLang.Use(language, Path.Combine(UserData, "lang"), Warn);
            Console.WriteLine($"言語: {language}（設定 {setting}）");
        }

        private void ReloadSettings()
        {
            // Load はファイルに書かないので、--userdata で読むだけのときもそのまま読む
            var settings = OctantPathSettings.Load(UserData, Warn);
            settings.ZipSource = ZipSources.IsBeatSaver(settings.ZipSource) ? settings.ZipSource : LocalPath(settings.ZipSource);
            _settings = settings;
        }

        // 既定の置き場所（--data を省いたとき。単体の実行ファイルで配る前提に合わせる。2026-10-13）:
        // 1. このリポジトリの中で動かしているなら、リポジトリの .desktop 1 か所（開発用。git には入れない）
        // 2. 実行ファイルの隣に OctantPath.Desktop-data があればそこ 1 か所（持ち歩き用。そのフォルダを作っておけばそこに置く）
        // 3. Linux は XDG の決まりに分けて置く（2026-10-14。DesktopFolders.Xdg）
        // 4. Windows・macOS は OS の決まった場所の OctantPath/Desktop 1 か所（Windows は %LOCALAPPDATA%、macOS は ~/Library/Application Support）
        //    実行ファイルを入れ替えたり、書けない場所に置いたりしても、設定・トークン・取得したマップが残る
        // 5. そこに作れなければ、起動したフォルダの .desktop 1 か所
        internal static DesktopFolders DefaultFolders()
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "src", "OctantPath.Desktop", "OctantPath.Desktop.csproj")))
                {
                    return DesktopFolders.At(Path.Combine(dir.FullName, ".desktop"));
                }
            }
            var portable = Path.Combine(AppContext.BaseDirectory, "OctantPath.Desktop-data");
            if (Directory.Exists(portable))
            {
                return DesktopFolders.At(portable);
            }
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    var xdg = DesktopFolders.Xdg();
                    xdg.Create();
                    return xdg;
                }
                var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify);
                if (!string.IsNullOrEmpty(local))
                {
                    var standard = DesktopFolders.At(Path.Combine(local, "OctantPath", "Desktop"));
                    standard.Create();
                    return standard;
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // 下の起動したフォルダに置く
            }
            return DesktopFolders.At(Path.Combine(Directory.GetCurrentDirectory(), ".desktop"));
        }

        // 今のゲームのフォルダで所有マップを読む。フォルダが変わらなければ今の一覧のまま
        private void UseGameFolder()
        {
            var game = GameFolder(_options, Folders.Config);
            if (_library != null && _library.Game == game)
            {
                return;
            }
            var library = new MapLibrary(game, Folders.Levels, Path.Combine(Folders.Cache, "library-cache.json"));
            _library = library;
            _ = library.ScanAsync();
        }

        // ゲームのフォルダ。--game か、--userdata が <ゲーム>/UserData/OctantPath ならそこから。
        // どちらもなければ設定の画面で選んだもの（設定の場所の desktop.json。2026-10-13）。それもなければ導入済みを出さない
        private static string GameFolder(DesktopOptions options, string config)
        {
            if (!string.IsNullOrEmpty(options.Game))
            {
                return Path.GetFullPath(options.Game);
            }
            if (!string.IsNullOrEmpty(options.UserData))
            {
                var userData = new DirectoryInfo(Path.GetFullPath(options.UserData.TrimEnd('/', '\\')));
                return userData.Name == "OctantPath" && userData.Parent?.Name == "UserData" ? userData.Parent.Parent?.FullName : null;
            }
            var saved = DesktopLocal.Load(config).GameFolder;
            return string.IsNullOrEmpty(saved) ? null : LocalPath(saved);
        }

        // Proton の設定は Z:\home\... の形なので、Linux で動かすときは /home/... に読み替える
        internal static string LocalPath(string path)
        {
            if (string.IsNullOrEmpty(path) || Path.DirectorySeparatorChar == '\\')
            {
                return path;
            }
            if (path.Length > 2 && (path[0] == 'Z' || path[0] == 'z') && path[1] == ':' && (path[2] == '\\' || path[2] == '/'))
            {
                return path.Substring(2).Replace('\\', '/');
            }
            return path;
        }

        // キーそのものか、https://beatsaver.com/maps/<キー> の形
        private static string KeyOf(string text)
        {
            var slash = text.TrimEnd('/').LastIndexOf('/');
            var key = (slash >= 0 ? text.TrimEnd('/').Substring(slash + 1) : text).Trim().ToLowerInvariant();
            if (key.StartsWith("!bsr ", StringComparison.Ordinal))
            {
                key = key.Substring(5).Trim();
            }
            return key.Length > 0 && key.Length <= 8 && key.All(Uri.IsHexDigit) ? key : null;
        }

        private static string NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        private static void Warn(string message) => Console.Error.WriteLine($"[warn] {message}");

        private sealed class Draft
        {
            public Preset Preset;
            public Dictionary<string, string> Choices;
            public string ChoiceLabel;
            public ChartRef Seed;
            public string SeedLabel;
            public MixDraft<RecommendationItem> Mix;
            // 知らせは出すときに今の言語で作る（言語を変えたあとも、その言語で出す）
            public volatile Func<string> Message;
            public volatile bool Creating;
            public volatile bool ToppingUp;
            public volatile Progress Progress;
            public volatile string Playlist;
        }

        private sealed class RecommendationResult
        {
            public List<RecommendationItem> Items = new List<RecommendationItem>();
            public string Error;

            public static RecommendationResult Ok(List<RecommendationItem> items) => new RecommendationResult { Items = items };
            public static RecommendationResult Fail(string error) => new RecommendationResult { Error = error };
        }

        private sealed class ReceiveOutcome
        {
            public RecommendationItem Item;
            public string Error;
        }
    }

    public sealed class Progress
    {
        [JsonProperty("done")] public int Done { get; set; }
        [JsonProperty("total")] public int Total { get; set; }
    }

    public sealed class Seed
    {
        [JsonProperty("input")] public string Input { get; set; }
        [JsonProperty("hash")] public string Hash { get; set; }
        [JsonProperty("key")] public string Key { get; set; }
        [JsonProperty("title")] public string Title { get; set; }
        [JsonProperty("artist")] public string Artist { get; set; }
        [JsonProperty("mapper")] public string Mapper { get; set; }
        [JsonProperty("cover")] public string Cover { get; set; }
        [JsonProperty("diffs")] public List<SeedDiff> Diffs { get; set; }
        [JsonProperty("characteristic")] public string Characteristic { get; set; }
        [JsonProperty("difficulty")] public string Difficulty { get; set; }

        [JsonProperty("label")] public string Label => $"{Title}{(Difficulty != null ? " " + Difficulty : "")}{(Characteristic != null && Characteristic != "Standard" ? " (" + Characteristic + ")" : "")}";

        public ChartRef ToChartRef() => new ChartRef { Hash = Hash, Characteristic = Characteristic, Difficulty = Difficulty };
    }

    public sealed class SeedDiff
    {
        [JsonProperty("characteristic")] public string Characteristic { get; set; }
        [JsonProperty("difficulty")] public string Difficulty { get; set; }
        [JsonProperty("nps")] public double Nps { get; set; }
        [JsonProperty("njs")] public double Njs { get; set; }
    }

    // Desktop が使う場所。--data（と持ち歩き用・開発用）では 1 つのフォルダに、Linux の既定では XDG の決まりに分けて置く（2026-10-14）:
    // Config（settings.json・desktop.json・lang）、State（connection.json。トークン）、Data（Levels・Playlists・Work）、Cache（library-cache.json・arcviewer）。
    // 取得したマップと作業用の場所は同じファイルシステムに置く（OctantPath 法 2.4）ので、Work は Data に置く
    public sealed class DesktopFolders
    {
        private DesktopFolders(string config, string state, string data, string cache)
        {
            Config = config;
            State = state;
            Root = data;
            Cache = cache;
            Levels = Path.Combine(data, "Levels");
            Work = Path.Combine(data, "Work");
            Playlists = Path.Combine(data, "Playlists");
        }

        public static DesktopFolders At(string root) => new DesktopFolders(root, root, root, root);

        public static DesktopFolders Xdg()
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string Place(string variable, string fallback)
            {
                // 空や相対パスは使わない（XDG Base Directory の決まり）
                var value = Environment.GetEnvironmentVariable(variable);
                var folder = !string.IsNullOrEmpty(value) && Path.IsPathRooted(value) ? value : Path.Combine(home, fallback);
                return Path.Combine(folder, "OctantPath", "Desktop");
            }
            return new DesktopFolders(Place("XDG_CONFIG_HOME", ".config"), Place("XDG_STATE_HOME", Path.Combine(".local", "state")),
                Place("XDG_DATA_HOME", Path.Combine(".local", "share")), Place("XDG_CACHE_HOME", ".cache"));
        }

        public string Config { get; }
        public string State { get; }
        // 取得したマップなどを置くデータの場所（1 か所のときはそのフォルダ）
        public string Root { get; }
        public string Cache { get; }
        public string Levels { get; }
        public string Work { get; }
        public string Playlists { get; }

        public void Create()
        {
            foreach (var folder in new[] { Config, State, Root, Cache, Levels, Work, Playlists })
            {
                Directory.CreateDirectory(folder);
            }
        }
    }

    // 取得したマップ（hash → フォルダ）。Levels の中身から作り直せるように、記録は Levels の外に置かず、フォルダの中の印で持つ
    public sealed class ReceivedStore
    {
        public const string MarkFile = ".octantpath-desktop-hash";

        private readonly string _levels;
        private readonly Dictionary<string, string> _folders = new Dictionary<string, string>();
        private readonly object _gate = new object();

        public ReceivedStore(string levels)
        {
            _levels = levels;
            foreach (var folder in Directory.GetDirectories(levels))
            {
                var mark = Path.Combine(folder, MarkFile);
                var hash = File.Exists(mark) ? MapHash.Normalize(File.ReadAllText(mark).Trim()) : null;
                if (hash != null)
                {
                    _folders[hash] = folder;
                }
            }
        }

        public bool Has(string hash) => Folder(hash) != null;

        public string Folder(string hash)
        {
            var h = MapHash.Normalize(hash);
            lock (_gate)
            {
                return h != null && _folders.TryGetValue(h, out var folder) && Directory.Exists(folder) ? folder : null;
            }
        }

        public void Add(string hash, string folder)
        {
            var h = MapHash.Normalize(hash);
            File.WriteAllText(Path.Combine(folder, MarkFile), h);
            lock (_gate)
            {
                _folders[h] = folder;
            }
        }
    }

    // 読むだけのときは、ペアリングで受け取ったトークンをメモリにだけ持つ
    public sealed class DesktopConnectionStore : IConnectionStore
    {
        private readonly IConnectionStore _inner;
        private readonly bool _readOnly;
        private ConnectionInfo _memory;

        public DesktopConnectionStore(IConnectionStore inner, bool readOnly)
        {
            _inner = inner;
            _readOnly = readOnly;
        }

        public ConnectionInfo Load() => _memory ?? _inner.Load();

        public void Save(ConnectionInfo info)
        {
            if (_readOnly)
            {
                _memory = info;
                return;
            }
            _inner.Save(info);
        }
    }
}
