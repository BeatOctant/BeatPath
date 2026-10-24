using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using OctantPath.Core.Maps;

namespace OctantPath.Core.Recommendations
{
    // 推薦の 1 譜面と、表示のために BeatSaver から引いた情報。MOD（ミックス・結果画面・お試し枠）と Desktop（下見）が同じものを使う
    public sealed class RecommendationItem
    {
        private volatile bool _inFlight;

        public RecommendationItem(string requestId, Candidate candidate, BeatSaverMap map)
        {
            RequestId = requestId;
            Candidate = candidate;
            Hash = MapHash.Normalize(candidate.Hash);
            Map = map;
            Version = map?.Version(Hash);
            var diff = Version?.Diffs?.FirstOrDefault(d => d.Characteristic == candidate.Characteristic && d.Difficulty == candidate.Difficulty);
            Nps = diff?.Nps;
            Njs = diff?.Njs;
        }

        public string RequestId { get; }

        // 要求で使った手札の id（recommendation の signal に入れる）
        public string Preset { get; set; }
        public Candidate Candidate { get; }

        // 小文字にそろえた hash。OctantCompass が形の違う hash を返したら null
        public string Hash { get; }

        // BeatSaver のマップ。見つからなければ（BeatSaver に届かなかったときも）null
        public BeatSaverMap Map { get; }

        // 推薦された hash の版だけ（OctantPath 法 2.1）。なければ null で、画面には出さない（RecommendationDisplay）
        public BeatSaverVersion Version { get; }
        public double? Nps { get; }
        public double? Njs { get; }

        // 導入済みか。MOD が SongCore で調べて入れる
        public bool Installed { get; set; }

        // 取得中か。Desktop が取得の間だけ立てる（画面の問い合わせと取得が別のスレッドで読み書きする）
        public bool InFlight
        {
            get => _inFlight;
            set => _inFlight = value;
        }

        // 画面で行を指す名前。同じ曲の別の難易度が並んでも区別できるように、譜面まで含める
        public string Id => $"{Hash}/{Candidate.Characteristic}/{Candidate.Difficulty}";
        public string Characteristic => Candidate.Characteristic;
        public string Difficulty => Candidate.Difficulty;

        public string Key => Map?.Key;
        public string Title => Map?.Metadata?.SongName ?? Hash ?? Candidate.Hash;
        public string SubTitle => Map?.Metadata?.SongSubName;
        public string Artist => Map?.Metadata?.SongAuthorName ?? "";
        public string Mapper => Map?.Metadata?.LevelAuthorName ?? "";
        public double? Duration => Map?.Metadata?.Duration;
        public string Cover => Version?.CoverUrl;
        public string Preview => Version?.PreviewUrl;
        public string DownloadUrl => Version?.DownloadUrl;
    }

    // 推薦の応答の候補に、表示のために BeatSaver から曲の情報を付ける
    public static class RecommendationLookup
    {
        // 順は OctantCompass が返した順のまま（並べ替えない。憲法の原則 6）。候補は全部返す（hash の形が違うものも、BeatSaver にないものも）。
        // BeatSaver に問い合わせられなければ、曲の情報なしで返し、理由を warn に渡す
        public static async Task<List<RecommendationItem>> ItemsAsync(RecommendationResponse response, BeatSaverClient beatSaver, Action<string> warn, CancellationToken cancellationToken)
        {
            Dictionary<string, BeatSaverMap> maps;
            try
            {
                maps = await beatSaver.MapsByHashAsync(response.Candidates.Select(c => c.Hash), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is BeatSaverException || ex is HttpRequestException)
            {
                warn?.Invoke($"BeatSaver lookup failed: {ex.Message}");
                maps = new Dictionary<string, BeatSaverMap>();
            }
            return response.Candidates.Select(c =>
            {
                maps.TryGetValue(MapHash.Normalize(c.Hash) ?? "", out var map);
                return new RecommendationItem(response.RequestId, c, map);
            }).ToList();
        }
    }
}
