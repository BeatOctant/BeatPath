using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using OctantPath.Connection;
using OctantPath.Core.Connection;
using OctantPath.Core.Maps;
using OctantPath.Core.Recommendations;
using OctantPath.Maps;
using OctantPath.Core.Languages;

namespace OctantPath.Recommendations
{
    // 推薦の要求（ミックス・結果画面・お試し枠）を OctantCompass へ送り、表示のために BeatSaver から曲の情報を引く
    internal sealed class RecommendationService
    {
        private readonly ConnectionService _connection;
        private readonly BeatSaverClient _beatSaver;
        private readonly OctantPathFolders _folders;

        public RecommendationService(ConnectionService connection, BeatSaverClient beatSaver, OctantPathFolders folders)
        {
            _connection = connection;
            _beatSaver = beatSaver;
            _folders = folders;
        }

        public async Task<RecommendationList> RequestAsync(RecommendationRequest request, CancellationToken cancellationToken)
        {
            var result = await _connection.RecommendAsync(request, cancellationToken).ConfigureAwait(true);
            if (result == null)
            {
                Write(request, null, null, "not connected");
                return RecommendationList.Fail(Lang.T("connection.notConnected"));
            }
            if (result.Kind != HostResultKind.Ok)
            {
                Write(request, null, null, $"{result.Kind} {result.DetailOrStatus}");
                return RecommendationList.Fail(Describe(result));
            }

            // 順は OctantCompass が返した順のまま（並べ替えない）
            var items = await RecommendationLookup.ItemsAsync(result.Body, _beatSaver, message => Plugin.Log.Warn(message), cancellationToken).ConfigureAwait(true);
            foreach (var item in items)
            {
                item.Preset = request.Preset;
                item.Installed = item.Hash != null && LevelLookup.IsInstalled(item.Hash);
            }
            Write(request, result.Body, items, null);
            return RecommendationList.Ok(result.Body, items);
        }

        // 推薦 1 回分をログと手元の記録（recommendations.jsonl。推薦のまとめに使う）に書く
        private void Write(RecommendationRequest request, RecommendationResponse response, List<RecommendationItem> items, string error)
        {
            foreach (var line in RecommendationLog.Lines(request, response, items, error))
            {
                Plugin.Log.Info($"[recommend] {line}");
            }
            try
            {
                RecommendationRecords.Append(_folders.Root, RecommendationRecord.From(request, response, items, error, System.DateTime.UtcNow));
            }
            catch (System.Exception ex) when (ex is System.IO.IOException || ex is System.UnauthorizedAccessException)
            {
                // 記録はまとめのためだけなので、書けなくても推薦は続ける
                Plugin.Log.Warn($"[recommend] {RecommendationRecords.FileName} could not be written: {ex.Message}");
            }
        }

        private static string Describe(HostResult<RecommendationResponse> result)
        {
            switch (RecommendationErrors.Classify(result))
            {
                case RecommendationProblem.SeedNotFound:
                    return Lang.T("recommend.seedNotInDb");
                case RecommendationProblem.SeedNotEmbedded:
                    return Lang.T("recommend.seedNotEmbedded");
                case RecommendationProblem.NotReady:
                    return Lang.T("recommend.notReady");
                case RecommendationProblem.UnknownPreset:
                    return Lang.T("recommend.unknownPreset");
                case RecommendationProblem.InvalidRequest:
                    return Lang.T("recommend.badRequest", new { error = result.Error.Message });
                case RecommendationProblem.Unreachable:
                    return Lang.T("connection.unreachable");
                default:
                    return Lang.T("recommend.failed", new { detail = result.DetailOrStatus });
            }
        }
    }

    internal sealed class RecommendationList
    {
        private RecommendationList(RecommendationResponse response, List<RecommendationItem> items, string error)
        {
            Response = response;
            Items = items;
            Error = error;
        }

        public RecommendationResponse Response { get; }
        public List<RecommendationItem> Items { get; }
        public string Error { get; }

        public static RecommendationList Ok(RecommendationResponse response, List<RecommendationItem> items) => new RecommendationList(response, items, null);
        public static RecommendationList Fail(string error) => new RecommendationList(null, new List<RecommendationItem>(), error);
    }
}
