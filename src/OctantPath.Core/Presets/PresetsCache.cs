using OctantPath.Core.Connection;

namespace OctantPath.Core.Presets
{
    public enum PresetsFailure
    {
        // つないでいない（推薦が使えるつなぐ先がない）
        NotConnected,

        // OctantCompass が一覧を返さなかった（FailureDetail に理由）
        Failed,
    }

    // OctantCompass の開示する手札の一覧（OctantSpecs plans/presets.md）と、取り直す決まり。MOD の ConnectionService と Desktop が同じ決まりで使う
    public sealed class PresetsCache
    {
        // 最後に取れた一覧。まだ取っていなければ null（取り直しに失敗しても前のものを使い続ける）
        public PresetsResponse Presets { get; private set; }

        // 最後に取り直したときに取れなかった理由。取れたら null
        public PresetsFailure? Failure { get; private set; }

        // Failed のときの詳しい理由（HostResult.DetailOrStatus）
        public string FailureDetail { get; private set; }

        // 接続を確かめたあと、一覧を取り直すか。まだ取っていないときと、revision が変わったときだけ
        // （revision はペアリング済みで ready のときに必ず返る。null なら比べない）
        public bool NeedsReload(ConnectionStatus status) =>
            status.State == ConnectionState.Ready && (Presets == null || (status.PresetsRevision != null && status.PresetsRevision != Presets.Revision));

        // 推薦の要求が失敗したあと、一覧を取り直すか。今の一覧にない手札を名指しした（unknown_preset）ときだけ。
        // 接続を確かめ直すとき（ShouldRecheck）は、確かめ直したあとの revision で決める
        public static bool NeedsReload<T>(HostResult<T> result)
            where T : class =>
            result != null && !result.ShouldRecheck && result.IsUnknownPreset;

        // HostConnection.GetPresetsAsync の結果を受け取る（つないでいなければ null）。取れたら true
        public bool Update(HostResult<PresetsResponse> result)
        {
            if (result != null && result.Kind == HostResultKind.Ok)
            {
                Presets = result.Body;
                Failure = null;
                FailureDetail = null;
                return true;
            }
            Failure = result == null ? PresetsFailure.NotConnected : PresetsFailure.Failed;
            FailureDetail = result?.DetailOrStatus;
            return false;
        }
    }
}
