using OctantPath.Core.Connection;

namespace OctantPath.Core.Recommendations
{
    // 推薦の要求が失敗した理由（契約3（OctantPath to OctantCompass）のエラーの code と、届かなかったこと）。文は MOD と Desktop がそれぞれの言葉で出す
    public enum RecommendationProblem
    {
        // seed_not_found: 起点の譜面が DB にない
        SeedNotFound,

        // seed_not_embedded: 起点の譜面は DB の目録にあるが、比べるベクトルがない
        SeedNotEmbedded,

        // not_ready: OctantCompass の準備ができていない
        NotReady,

        // unknown_preset: 手札が今の一覧にない（一覧は PresetsCache の決まりで取り直す）
        UnknownPreset,

        // invalid_request: 要求の形が違う（Error.Message に OctantCompass の説明）
        InvalidRequest,

        // OctantCompass に届かなかった
        Unreachable,

        // ほかの失敗（HostResult.DetailOrStatus を添える）
        Other,
    }

    public static class RecommendationErrors
    {
        // 失敗した結果（Kind が Ok でないもの）を分ける。エラーの code を先に見て、知らない code なら届いたかどうかで分ける
        public static RecommendationProblem Classify(HostResult<RecommendationResponse> result)
        {
            switch (result.Error?.Code)
            {
                case "seed_not_found":
                    return RecommendationProblem.SeedNotFound;
                case "seed_not_embedded":
                    return RecommendationProblem.SeedNotEmbedded;
                case "not_ready":
                    return RecommendationProblem.NotReady;
                case "unknown_preset":
                    return RecommendationProblem.UnknownPreset;
                case "invalid_request":
                    return RecommendationProblem.InvalidRequest;
            }
            return result.Kind == HostResultKind.Unreachable ? RecommendationProblem.Unreachable : RecommendationProblem.Other;
        }
    }
}
