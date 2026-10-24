using OctantPath.Core.Languages;

namespace OctantPath.Core.Connection
{
    // 接続状態を画面に出す文（言語ファイルの connection.*）
    public static class StatusText
    {
        public static string Describe(ConnectionStatus status)
        {
            var ours = ContractVersion.Api.ToString();
            switch (status.State)
            {
                case ConnectionState.Unknown:
                    return Lang.T("connection.connecting");
                case ConnectionState.LocalInfoUnreadable:
                    return Lang.T("connection.localUnreadable");
                case ConnectionState.Unreachable:
                    return Lang.T("connection.unreachable");
                case ConnectionState.ApiVersionMismatch:
                    return ApiMismatch(status.HostApiVersion, ours);
                case ConnectionState.NeedsPairing:
                    return Lang.T("connection.needsPairing");
                case ConnectionState.PairingRejected:
                    return Lang.T("connection.pairingRejected");
                case ConnectionState.Loading:
                    return Lang.T("connection.loading");
                case ConnectionState.Degraded:
                    return Lang.T("connection.degraded", new { reason = status.Reason ?? Lang.T("common.unknownReason") });
                case ConnectionState.Ready:
                    return status.UpdateReason != null ? Lang.T("connection.readyNoUpdate", new { reason = status.UpdateReason }) : Lang.T("connection.ready");
                default:
                    return Lang.T("connection.unexpected");
            }
        }

        private static string ApiMismatch(string host, string ours)
        {
            if (host != null && ContractVersion.TryParse(host, out var h) && h.Major != ContractVersion.Api.Major)
            {
                var update = h.Major > ContractVersion.Api.Major ? "OctantPath" : "OctantCompass";
                return Lang.T("connection.apiMismatchUpdate", new { host, ours, update });
            }
            return Lang.T("connection.apiMismatch", new { ours });
        }
    }
}
