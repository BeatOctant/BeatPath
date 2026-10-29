using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OctantPath.Core.Languages;

namespace OctantPath.Core.Settings
{
    // UserData/OctantPath/settings.json。既定値の正本は同じフォルダの defaults.json（ビルドに埋め込む）で、ここには書かない。
    // settings.json には人（と MOD の設定画面）が変えた項目だけを置き、読むときは既定値にファイルの値を重ねる。
    public sealed class TrialFrameSettings
    {
        [JsonProperty("id")] public string Id { get; set; }
        // 空なら言語ファイルの trialFrame.<id>（なければ id）を出す
        [JsonProperty("name")] public string Name { get; set; }

        [JsonIgnore]
        public string DisplayName
        {
            get
            {
                if (!string.IsNullOrEmpty(Name))
                {
                    return Name;
                }
                var key = "trialFrame." + Id;
                var text = Lang.T(key);
                return text == key ? Id : text;
            }
        }

        // 省くと trialSlotCount
        [JsonProperty("count")] public int? Count { get; set; }

        // 枠が名指しする手札の id（GET /v1/presets の id。OctantSpecs plans/presets.md）。起点は送らない。
        // OctantCompass の一覧にない手札と、起点が必須の手札は使わない
        [JsonProperty("preset")] public string Preset { get; set; }

        // 手札の choice の入力に渡す値（入力の name → 値）。必須の choice に値がなければ、その枠は使わない
        [JsonProperty("choices", ObjectCreationHandling = ObjectCreationHandling.Replace)] public Dictionary<string, string> Choices { get; set; } = new Dictionary<string, string>();
    }

    public sealed class OctantPathSettings
    {
        public const string FileName = "settings.json";

        // 埋め込んだ defaults.json の名前（MOD も同じ名前で埋め込む）
        public const string DefaultsResource = "OctantPath.Settings.defaults.json";

        private static readonly Lazy<JObject> DefaultsJson = new Lazy<JObject>(ReadDefaults);

        // 作ったときの値は defaults.json の値
        public OctantPathSettings()
        {
            using (var reader = DefaultsJson.Value.CreateReader())
            {
                JsonSerializer.CreateDefault().Populate(reader, this);
            }
        }

        // defaults.json の中身（写し）。画面に既定値を出すときと、既定値に戻した項目をファイルから消すときに使う
        public static JObject DefaultValues() => (JObject)DefaultsJson.Value.DeepClone();

        private static JObject ReadDefaults()
        {
            using (var stream = typeof(OctantPathSettings).Assembly.GetManifestResourceStream(DefaultsResource))
            {
                if (stream == null)
                {
                    throw new InvalidOperationException($"{DefaultsResource} is not embedded in the build");
                }
                using (var reader = new StreamReader(stream))
                {
                    return JObject.Parse(reader.ReadToEnd());
                }
            }
        }

        // OctantCompass の要求のタイムアウト（秒）
        [JsonProperty("hostRequestTimeoutSeconds")]
        public double HostRequestTimeoutSeconds { get; set; }

        // 推薦の要求のタイムアウト（秒）
        [JsonProperty("recommendationRequestTimeoutSeconds")]
        public double RecommendationRequestTimeoutSeconds { get; set; }

        // 未接続のときの /v1/health の再試行の間隔（秒）
        [JsonProperty("healthRetryIntervalSeconds")]
        public double HealthRetryIntervalSeconds { get; set; }

        // お試し枠の件数（1 枠あたり）
        [JsonProperty("trialSlotCount")]
        public int TrialSlotCount { get; set; }

        // 「似ている」の枠。件数は枠ごとに指定でき、0 ならその枠を使わない。省くと trialSlotCount。
        // 既定の枠（defaults.json）は実装構想の「似ている」の分け方で、枠ごとに公式の手札を名指しする（2026-10-14）:
        // 曲が似ている → song、譜面が似ている → chart、難易度が合う → for_you。
        // 枠の id は台帳とプレイリストの名前（OctantPath-<id>）に使うので変えない
        // 既定ではどの枠も 0 件（使わない）。起動ごとに BeatSaver から先に取る量を、ユーザーが選んで増やすまで 0 にする（2026-10-11）
        [JsonProperty("trialFrames", ObjectCreationHandling = ObjectCreationHandling.Replace)]
        public List<TrialFrameSettings> TrialFrames { get; set; }

        // お試し中のマップを、クリアと失敗を合わせてこの回数以上遊んだら「残す」にする
        [JsonProperty("keepMinPlays")]
        public int KeepMinPlays { get; set; }

        // お試しの期限（起動日）。ゲームを起動した日を 1 日と数え、この日数を過ぎてもお試し中なら消す
        [JsonProperty("trialExpiryLaunchDays")]
        public int TrialExpiryLaunchDays { get; set; }

        // お試し中のマップの合計の上限（MB）。期限の日数とどちらかを超えたら、古く取ったものから消す（2026-10-20）
        [JsonProperty("trialMaxMegabytes")]
        public double TrialMaxMegabytes { get; set; }

        // zip の取得元。"BeatSaver" か、ローカルのフォルダ（<hash の先頭 2 文字>/<hash>.zip）のパス
        [JsonProperty("zipSource")]
        public string ZipSource { get; set; }

        // 自動ダウンロード。切ると推薦の表示だけになる
        [JsonProperty("autoDownload")]
        public bool AutoDownload { get; set; }

        // 削除の試し運転。消す代わりにログに書くだけ
        [JsonProperty("deleteDryRun")]
        public bool DeleteDryRun { get; set; }

        // 自動ダウンロードの同時数
        [JsonProperty("downloadConcurrency")]
        public int DownloadConcurrency { get; set; }

        // 1 秒あたりのダウンロード開始数
        [JsonProperty("downloadStartsPerSecond")]
        public double DownloadStartsPerSecond { get; set; }

        // BeatSaver API の問い合わせの間隔（秒）。1 秒より短くはしない（0 にすると間隔を空けなくなるため）。429 が返ったら倍にして再試行する
        [JsonProperty("beatSaverApiIntervalSeconds")]
        public double BeatSaverApiIntervalSeconds { get; set; }

        // 429 で延ばす間隔の上限（秒）
        [JsonProperty("beatSaverApiMaxBackoffSeconds")]
        public double BeatSaverApiMaxBackoffSeconds { get; set; }

        // 1 つの zip の受信量の上限（MB）
        [JsonProperty("zipMaxDownloadMegabytes")]
        public double ZipMaxDownloadMegabytes { get; set; }

        // 展開後の合計の上限（MB）
        [JsonProperty("zipMaxExtractedMegabytes")]
        public double ZipMaxExtractedMegabytes { get; set; }

        // zip の entry 数の上限（フォルダの entry も数える）
        [JsonProperty("zipMaxEntries")]
        public int ZipMaxEntries { get; set; }

        // マップを取る先（BeatSaver API）
        [JsonProperty("beatSaverApiUrl")]
        public string BeatSaverApiUrl { get; set; }

        // Desktop の画面のカバーと試聴（/api/cdn で BeatSaver の CDN から取る）の、1 つの応答の受信量の上限（MB）。
        // カバーの画像と数秒の試聴の MP3 が収まる大きさにする（推測: BeatSaver の実物の大きさは確かめていない）
        [JsonProperty("cdnMaxMegabytes")]
        public double CdnMaxMegabytes { get; set; }

        // Desktop がカバーと試聴を同時に取る数（起動したときの値を使う）
        [JsonProperty("cdnConcurrency")]
        public int CdnConcurrency { get; set; }

        // Desktop がカバーと試聴を 1 つ取る待ち時間の上限（秒）。空きを待つ間と本文を読み終えるまでを含める
        [JsonProperty("cdnTimeoutSeconds")]
        public double CdnTimeoutSeconds { get; set; }

        // Desktop が初回に ArcViewer を取るときの、1 つのファイルの待ち時間の上限（秒）。いちばん大きい .wasm（51 MiB）を遅い回線でも取れる長さにする
        [JsonProperty("arcViewerFileTimeoutSeconds")]
        public double ArcViewerFileTimeoutSeconds { get; set; }

        // ミックスで、今の曲の先を前もって取得しておく曲数
        [JsonProperty("mixPrefetchCount")]
        public int MixPrefetchCount { get; set; }

        // ミックスの下見に並べる曲数。入れ替え用に、その 2 倍を余分に受け取る。
        // 取得するのは「作成」を押したときに並んでいる曲だけ（値は推測）
        [JsonProperty("mixDraftCount")]
        public int MixDraftCount { get; set; }

        // 曲選択の上の板に、作成したミックスの並びの一覧を出すか（出さなくても曲選択のプレイリストで見える）
        [JsonProperty("mixListOnPanel")]
        public bool MixListOnPanel { get; set; }

        // ミックスの曲を遊び終えて曲選択に戻ったら、並びの次の曲を自動で選ぶか。切っていても、結果画面で選んだ曲は選ぶ。
        // 曲選択の上の板のボタンで切り替える
        [JsonProperty("mixAutoAdvance")]
        public bool MixAutoAdvance { get; set; }

        // 難しさの上限（足切り）。入なら、どの手札でも、最近のクリアより難しい譜面（Pass で見て、最近のクリアの 97% より重いもの）を出さない。
        // 推薦の要求の params の ceiling で送る。曲選択の上の板と Desktop のボタンで切り替える（2026-10-25）
        [JsonProperty("difficultyCeiling")]
        public bool DifficultyCeiling { get; set; }

        // 推薦の板（曲選択と結果画面の上）の大きさ。BSML の浮かぶ画面の標準（1）に対する倍率
        [JsonProperty("recommendationPanelScale")]
        public double RecommendationPanelScale { get; set; }

        // 推薦の板の背景の不透明度（0〜1）。後ろのゲームの光る物で文字が読みにくいときに上げる
        [JsonProperty("panelOpacity")]
        public double PanelOpacity { get; set; }

        // 画面の言語。"auto" はゲームの言語（英語以外のとき）、OS の言語、英語の順に選ぶ。
        // "en" や "ja" で決められる。UserData/OctantPath/lang/<言語>.json を置けば、その言語も選べる
        [JsonProperty("language")]
        public string Language { get; set; }

        // ペアリングで OctantCompass に伝える環境の表示名。空なら OctantPath が作る
        [JsonProperty("environmentName")]
        public string EnvironmentName { get; set; }

        [JsonIgnore]
        public TimeSpan HostRequestTimeout => TimeSpan.FromSeconds(Math.Max(1, HostRequestTimeoutSeconds));
        [JsonIgnore]
        public TimeSpan RecommendationRequestTimeout => TimeSpan.FromSeconds(Math.Max(1, RecommendationRequestTimeoutSeconds));
        [JsonIgnore]
        public TimeSpan BeatSaverApiInterval => TimeSpan.FromSeconds(Math.Max(1, BeatSaverApiIntervalSeconds));
        [JsonIgnore]
        public TimeSpan BeatSaverApiMaxBackoff => TimeSpan.FromSeconds(Math.Max(1, BeatSaverApiMaxBackoffSeconds));
        [JsonIgnore]
        public long ZipMaxDownloadBytes => (long)(ZipMaxDownloadMegabytes * 1024 * 1024);
        [JsonIgnore]
        public long ZipMaxExtractedBytes => (long)(ZipMaxExtractedMegabytes * 1024 * 1024);
        // long に入らない値は long.MaxValue にそろえる（そのまま変換すると負になり、全部が上限を超えたことになる）
        [JsonIgnore]
        public long TrialMaxBytes => TrialMaxMegabytes * 1024 * 1024 >= long.MaxValue ? long.MaxValue : (long)(TrialMaxMegabytes * 1024 * 1024);

        // settings.json があるのに読めなかった。既定値で動くが、お試しの片付けはしない（利用者が切った自動削除が入に戻るため）
        [JsonIgnore]
        public bool Unreadable { get; private set; }

        // お試しの片付けを止める理由。なければ null。
        // 読めない設定と、0 や負の期限・上限（「0 = 無制限」のつもりで書かれやすい）では、お試しを全部消す側に倒れるので何もしない
        public string DeletionProblem()
        {
            if (Unreadable)
            {
                return "settings.json が読めないので、何も消さない";
            }
            if (KeepMinPlays < 1)
            {
                return $"keepMinPlays（{KeepMinPlays}）が 1 より小さいので、何も消さない";
            }
            if (TrialExpiryLaunchDays < 1)
            {
                return $"trialExpiryLaunchDays（{TrialExpiryLaunchDays}）が 1 より小さいので、何も消さない";
            }
            if (!(TrialMaxMegabytes > 0) || double.IsInfinity(TrialMaxMegabytes))
            {
                return $"trialMaxMegabytes（{TrialMaxMegabytes}）が正の有限の数ではないので、何も消さない";
            }
            return null;
        }
        [JsonIgnore]
        public TimeSpan HealthRetryInterval => TimeSpan.FromSeconds(Math.Max(1, HealthRetryIntervalSeconds));
        [JsonIgnore]
        public long CdnMaxBytes => (long)(CdnMaxMegabytes * 1024 * 1024);
        [JsonIgnore]
        public TimeSpan CdnTimeout => TimeSpan.FromSeconds(Math.Max(1, CdnTimeoutSeconds));
        [JsonIgnore]
        public TimeSpan ArcViewerFileTimeout => TimeSpan.FromSeconds(Math.Max(1, ArcViewerFileTimeoutSeconds));

        // 既定値と同じでもファイルに残す項目。お試しの片付けに関わるもので、既定を変えたときに利用者の選んだ値が覆らないようにする
        private static readonly HashSet<string> KeptEvenIfDefault = new HashSet<string> { "deleteDryRun", "keepMinPlays", "trialExpiryLaunchDays", "trialMaxMegabytes" };

        // 1 つの項目だけを書き換える。ほかの項目と知らない項目はそのまま残す。
        // 既定値と同じ値なら、その項目をファイルから消す（settings.json には変えた値だけを置く）。KeptEvenIfDefault の項目は消さない
        public static void SaveValue(string directory, string key, JToken value)
        {
            var defaults = DefaultsJson.Value[key];
            var drop = defaults != null && SameValue(defaults, value) && !KeptEvenIfDefault.Contains(key);
            JsonFile.SetValue(Path.Combine(directory, FileName), key, drop ? null : value);
        }

        // 数は 2 と 2.0 を同じと見る（画面は小数で、defaults.json は整数で書くことがある）
        private static bool SameValue(JToken a, JToken b)
        {
            if (IsNumber(a) && IsNumber(b))
            {
                return a.Value<double>() == b.Value<double>();
            }
            return JToken.DeepEquals(a, b);
        }

        private static bool IsNumber(JToken token) => token.Type == JTokenType.Integer || token.Type == JTokenType.Float;

        // 既定値（defaults.json）に settings.json の値を重ねて読む。ファイルには書かない（なくても作らない）。
        // 知らない項目は読み飛ばす。読めないときは既定値で動き、Unreadable を立てる（お試しの片付けを止める）。
        public static OctantPathSettings Load(string directory, Action<string> warn)
        {
            var path = Path.Combine(directory, FileName);
            if (!File.Exists(path))
            {
                return new OctantPathSettings();
            }
            try
            {
                // 作ったときに既定値が入り、ファイルにある項目だけが上書きされる
                return JObject.Parse(File.ReadAllText(path)).ToObject<OctantPathSettings>() ?? new OctantPathSettings();
            }
            catch (Exception ex)
            {
                warn?.Invoke($"settings.json could not be read; using defaults and deleting nothing: {ex.Message}");
                return new OctantPathSettings { Unreadable = true };
            }
        }
    }
}
