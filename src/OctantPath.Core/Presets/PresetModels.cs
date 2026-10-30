using System.Collections.Generic;
using System.Linq;
using OctantPath.Core.Recommendations;
using OctantPath.Core.Languages;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace OctantPath.Core.Presets
{
    // GET /v1/presets の応答（OctantSpecs plans/presets.md）。中の条件は OctantVector が持ち（OctantCompass は中継するだけ）、ここには来ない
    public sealed class PresetsResponse
    {
        [JsonProperty("revision")] public string Revision { get; set; }
        [JsonProperty("presets")] public List<Preset> Presets { get; set; } = new List<Preset>();
    }

    // 表示名（label・hint・group と入力・選択肢の label）は、文字列か {言語: 文字列}（2026-10-11）。
    // 受け取ったままの形で持ち、引くたびに今の言語の文字列にする（Lang.Pick）。読むときに決めていたので、
    // 一覧を取り直すまで言語を変えても手札の名前が前の言語のままだった（2026-10-30）
    public sealed class Preset
    {
        [JsonProperty("id")] public string Id { get; set; }
        [JsonProperty("label")] private JToken LabelText { get; set; }
        [JsonProperty("hint")] private JToken HintText { get; set; }

        // 任意。ボタンをまとめる見出し（曲・譜面など）。手札に付いてくる（契約3（OctantPath to OctantCompass）の Preset の group）
        [JsonProperty("group")] private JToken GroupText { get; set; }
        [JsonProperty("inputs")] public List<PresetInput> Inputs { get; set; } = new List<PresetInput>();

        // 任意。手札が履歴から語を選ぶとき（OctantVector の genreFrom）の、語ごとの表示名（語: 文字列か {言語: 文字列}）
        [JsonProperty("genreLabels")] private JToken GenreLabelTexts { get; set; }

        [JsonIgnore] public string Label { get => Lang.Pick(LabelText); set => LabelText = value; }
        [JsonIgnore] public string Hint { get => Lang.Pick(HintText); set => HintText = value; }
        [JsonIgnore] public string Group { get => Lang.Pick(GroupText); set => GroupText = value; }

        // 語ごとの、今の言語の表示名。値が空の語は落とす。genreLabels がなければ null
        [JsonIgnore] public Dictionary<string, string> GenreLabels => Lang.PickEach(GenreLabelTexts);
    }

    public sealed class PresetInput
    {
        public const string Seed = "seed";
        public const string Choice = "choice";

        [JsonProperty("kind")] public string Kind { get; set; }
        [JsonProperty("optional")] public bool Optional { get; set; }
        [JsonProperty("name")] public string Name { get; set; }
        [JsonProperty("label")] private JToken LabelText { get; set; }
        [JsonProperty("options")] public List<PresetOption> Options { get; set; } = new List<PresetOption>();

        [JsonIgnore] public string Label { get => Lang.Pick(LabelText); set => LabelText = value; }
    }

    public sealed class PresetOption
    {
        [JsonProperty("value")] public string Value { get; set; }
        [JsonProperty("label")] private JToken LabelText { get; set; }

        [JsonIgnore] public string Label { get => Lang.Pick(LabelText); set => LabelText = value; }
    }

    // 一覧をボタンにするときの決まり。並びは OctantCompass の返した順のまま
    public static class PresetRules
    {
        // 知らない種類の入力（任意のものを除く）があるプリセットは出さない。
        // 起点のない場所では、起点が必須のプリセットを出さない
        public static bool IsShown(Preset preset, bool hasSeedContext)
        {
            if (preset == null || string.IsNullOrEmpty(preset.Id) || string.IsNullOrEmpty(preset.Label))
            {
                return false;
            }
            foreach (var input in preset.Inputs ?? new List<PresetInput>())
            {
                if (input.Optional)
                {
                    continue;
                }
                if (input.Kind == PresetInput.Seed)
                {
                    if (!hasSeedContext)
                    {
                        return false;
                    }
                }
                else if (input.Kind == PresetInput.Choice)
                {
                    if (string.IsNullOrEmpty(input.Name) || input.Options == null || input.Options.Count == 0)
                    {
                        return false;
                    }
                }
                else
                {
                    return false;
                }
            }
            return true;
        }

        public static List<Preset> Shown(IEnumerable<Preset> presets, bool hasSeedContext) =>
            (presets ?? Enumerable.Empty<Preset>()).Where(p => IsShown(p, hasSeedContext)).ToList();

        public static bool NeedsSeed(Preset preset) => preset.Inputs?.Any(i => i.Kind == PresetInput.Seed && !i.Optional) == true;

        // 手札が選んだジャンルの語の表示名（下見の見出しに「手札名: これ」と出す）。
        // どの候補も同じ語で絞られ、手札を混ぜていないときだけ。表示名がなければ語のまま。それ以外は null
        public static string GenreName(Preset preset, IEnumerable<CandidateReason> reasons)
        {
            var words = (reasons ?? Enumerable.Empty<CandidateReason>()).Select(r => r == null || r.Hand != null ? null : r.Genre).Distinct().ToList();
            if (words.Count != 1 || string.IsNullOrEmpty(words[0]))
            {
                return null;
            }
            return preset?.GenreLabels != null && preset.GenreLabels.TryGetValue(words[0], out var name) && !string.IsNullOrEmpty(name) ? name : words[0];
        }

        public static bool UsesSeed(Preset preset) => preset.Inputs?.Any(i => i.Kind == PresetInput.Seed) == true;

        // 押したあとに選ばせる入力（必須の choice）。任意の choice は選ばせない
        public static List<PresetInput> Choices(Preset preset) =>
            (preset.Inputs ?? new List<PresetInput>()).Where(i => i.Kind == PresetInput.Choice && !i.Optional).ToList();

        // 起点を送らず、渡した choice の値だけで要求できるか（お試し枠）。できないなら理由（ログに書く）、できるなら null。
        // 必須の起点・値のない必須の choice・選択肢にない値・知らない種類の必須の入力があると、要求できない
        public static string Unmet(Preset preset, IDictionary<string, string> choices)
        {
            foreach (var input in preset.Inputs ?? new List<PresetInput>())
            {
                if (input.Kind != PresetInput.Choice)
                {
                    if (!input.Optional)
                    {
                        return input.Kind == PresetInput.Seed ? "needs a seed" : $"needs an input of kind '{input.Kind}'";
                    }
                    continue;
                }
                string value = null;
                if (choices != null && !string.IsNullOrEmpty(input.Name))
                {
                    choices.TryGetValue(input.Name, out value);
                }
                if (string.IsNullOrEmpty(value))
                {
                    if (!input.Optional)
                    {
                        return $"no value for the choice '{input.Name}'";
                    }
                }
                else if (input.Options == null || !input.Options.Any(o => o.Value == value))
                {
                    return $"'{value}' is not an option of the choice '{input.Name}'";
                }
            }
            return null;
        }

        // 押されたときの要求。送るのは id と入力と OctantPath の事実（件数など）と、ユーザーの推薦の設定だけ。
        // 重み・狙い・ジャンルの語は送らない（送るとプリセットの中の条件より優先されるため）。
        // ceiling: 設定の難しさの上限（difficultyCeiling）。入のときだけ params の ceiling で送る（手札の params とは混ぜて使われる）
        public static RecommendationRequest Request(Preset preset, ChartRef seed, IDictionary<string, string> choices, int count, bool ceiling = false)
        {
            return new RecommendationRequest
            {
                Preset = preset.Id,
                Choices = choices != null && choices.Count > 0 ? new Dictionary<string, string>(choices) : null,
                Seed = UsesSeed(preset) ? seed : null,
                Count = count,
                Difficulty = null,
                Params = ceiling ? new Dictionary<string, JToken> { ["ceiling"] = true } : null,
            };
        }
    }
}
