namespace OctantPath.Core.Connection
{
    public enum ConnectionState
    {
        // まだ確かめていない
        Unknown,
        // OctantPath の接続情報が読めない
        LocalInfoUnreadable,
        // つなぐ先に届かない
        Unreachable,
        // 契約3（OctantPath to OctantCompass）のメジャー版が違う
        ApiVersionMismatch,
        // ペアリングが要る（まだ合言葉を入れていない、またはトークンが取り消された）
        NeedsPairing,
        // ペアリングを断られた
        PairingRejected,
        // DB の読み込み中
        Loading,
        // 推薦が使えない（理由は Reason）
        Degraded,
        // 推薦が使える
        Ready,
        // 想定外の応答
        Error,
    }

    public sealed class ConnectionStatus
    {
        public ConnectionState State { get; set; }
        public string HostApiVersion { get; set; }
        public string HostVersion { get; set; }
        public string Reason { get; set; }
        public string UpdateReason { get; set; }
        public string DbVersion { get; set; }

        // OctantCompass の起動を表す値と、そこにあるこの環境の signal の件数（ペアリング済みのときだけ）。起動が変われば signal を丸ごと送り直す
        public string HostInstance { get; set; }
        public int? Signals { get; set; }

        // /v1/presets の revision。変わったときだけ一覧を取り直す
        public string PresetsRevision { get; set; }

        // ビュー（id@version）から役割名を引く表（/v1/health の db.roles を裏返したもの）。推薦の理由をビューの中の名前でなく
        // 役割の名前で見せるのに使う（2026-10-13）。OctantCompass が知らせていなければ null
        public System.Collections.Generic.Dictionary<string, string> ViewRoles { get; set; }
        public string Endpoint { get; set; }

        // つなぐ先が同じ PC（127.0.0.1 など）か
        public bool IsLocal { get; set; }

        public string Detail { get; set; }

        public override string ToString()
        {
            return $"{State} local={IsLocal} endpoint={Endpoint} host={HostVersion} api={HostApiVersion} reason={Reason} update={UpdateReason} db={DbVersion} instance={HostInstance} signals={Signals} detail={Detail}";
        }
    }
}
