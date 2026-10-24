using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using OctantPath.Core.Settings;

namespace OctantPath.Core.Connection
{
    // 契約6（client to OctantCompass）2.0 の OctantPath 側。自分の接続情報にあるつなぐ先（なければ同じ PC の既定のポート）へ通信だけでつなぎ、
    // 人が入れた合言葉でペアリングし、/v1/health で状態を確かめる。OctantCompass のファイルは読まない
    public sealed class HostConnection
    {
        private readonly HostClient _client;
        private readonly IConnectionStore _store;
        private readonly Func<OctantPathSettings> _settings;
        private readonly string _defaultEnvironmentName;

        public HostConnection(HostClient client, IConnectionStore store, Func<OctantPathSettings> settings, string defaultEnvironmentName)
        {
            _client = client;
            _store = store;
            _settings = settings;
            _defaultEnvironmentName = defaultEnvironmentName;
        }

        private Uri _readyBaseUri;
        private string _readyToken;

        // ペアリング済みでつながっている先（signal は推薦が使えないときも受け付けられる）
        private Uri _pairedBaseUri;
        private string _pairedToken;

        // 最後に確かめたときに使えたつなぐ先。使えないときは null
        public Uri ReadyBaseUri => _readyBaseUri;

        // 推薦の要求。つないでいなければ null を返す（呼ぶ側は RefreshAsync で確かめ直す）
        public Task<HostResult<Recommendations.RecommendationResponse>> RecommendAsync(Recommendations.RecommendationRequest request, CancellationToken cancellationToken) =>
            SendReadyAsync((baseUri, token) => _client.RecommendAsync(baseUri, token, request, _settings().RecommendationRequestTimeout, cancellationToken));

        public Task<HostResult<Presets.PresetsResponse>> GetPresetsAsync(CancellationToken cancellationToken) =>
            SendReadyAsync((baseUri, token) => _client.GetPresetsAsync(baseUri, token, _settings().HostRequestTimeout, cancellationToken));

        public Task<HostResult<Profile.ProfileResponse>> ProfileAsync(Profile.ProfileRequest request, CancellationToken cancellationToken) =>
            SendReadyAsync((baseUri, token) => _client.ProfileAsync(baseUri, token, request, _settings().RecommendationRequestTimeout, cancellationToken));

        // replace: 正本を丸ごと送って OctantCompass の履歴を置き換える（PUT）。そうでなければ後ろに足す（POST）
        public async Task<HostResult<Signals.SignalsResponse>> SendSignalsAsync(Signals.SignalsRequest request, bool replace, CancellationToken cancellationToken)
        {
            var baseUri = _pairedBaseUri;
            if (baseUri == null)
            {
                return null;
            }
            var result = await _client.SendSignalsAsync(baseUri, _pairedToken, request, replace, _settings().HostRequestTimeout, cancellationToken).ConfigureAwait(false);
            if (result.LosesConnection)
            {
                _readyBaseUri = null;
                _pairedBaseUri = null;
            }
            return result;
        }

        // 推薦が使えるつなぐ先へ送る。つないでいなければ null。トークンが通らない・版が違う・届かないなら、そのつなぐ先を忘れる
        private async Task<HostResult<T>> SendReadyAsync<T>(Func<Uri, string, Task<HostResult<T>>> send)
            where T : class
        {
            var baseUri = _readyBaseUri;
            if (baseUri == null)
            {
                return null;
            }
            var result = await send(baseUri, _readyToken).ConfigureAwait(false);
            if (result.LosesConnection)
            {
                _readyBaseUri = null;
            }
            return result;
        }

        public async Task<ConnectionStatus> RefreshAsync(CancellationToken cancellationToken)
        {
            var status = await RefreshCoreAsync(cancellationToken).ConfigureAwait(false);
            if (status.State != ConnectionState.Ready)
            {
                _readyBaseUri = null;
            }
            if (status.State != ConnectionState.Ready && status.State != ConnectionState.Loading && status.State != ConnectionState.Degraded)
            {
                _pairedBaseUri = null;
            }
            return status;
        }

        private async Task<ConnectionStatus> RefreshCoreAsync(CancellationToken cancellationToken)
        {
            var info = _store.Load();
            if (info == null)
            {
                return new ConnectionStatus { State = ConnectionState.LocalInfoUnreadable };
            }
            var status = await CheckHealthAsync(HostClient.BaseUri(info.EffectiveHost, info.EffectivePort), info.Token, _settings(), cancellationToken).ConfigureAwait(false);
            status.IsLocal = info.IsLocal;
            return status;
        }

        // 人が入れた合言葉でペアリングする（契約6 2.0。同じ PC でも同じ）。host が null なら今のつなぐ先のまま。
        // 通ったら、つなぐ先とトークンを接続情報に書いて確かめ直す。通らなければ接続情報は変えない
        public async Task<ConnectionStatus> PairAsync(string code, string host, int? port, CancellationToken cancellationToken)
        {
            var info = _store.Load();
            if (info == null)
            {
                return new ConnectionStatus { State = ConnectionState.LocalInfoUnreadable };
            }
            if (host != null)
            {
                info.Host = ConnectionInfo.IsLoopback(host) && port == null ? null : host;
                info.Port = port;
            }
            var settings = _settings();
            var baseUri = HostClient.BaseUri(info.EffectiveHost, info.EffectivePort);
            var name = string.IsNullOrEmpty(settings.EnvironmentName) ? _defaultEnvironmentName : settings.EnvironmentName;
            var pair = await _client.PairAsync(baseUri, new PairRequest { Secret = (code ?? "").Trim(), Name = name }, settings.HostRequestTimeout, cancellationToken).ConfigureAwait(false);
            if (pair.Kind == HostResultKind.Unreachable)
            {
                return new ConnectionStatus { State = ConnectionState.Unreachable, Endpoint = baseUri.ToString(), IsLocal = info.IsLocal, Detail = pair.Detail };
            }
            if (pair.IsVersionMismatch)
            {
                return VersionMismatch(baseUri, pair.Error?.ApiVersion);
            }
            if (pair.Kind != HostResultKind.Ok || string.IsNullOrEmpty(pair.Body.Token))
            {
                return new ConnectionStatus
                {
                    State = pair.IsUnauthorized ? ConnectionState.PairingRejected : ConnectionState.Error,
                    Endpoint = baseUri.ToString(),
                    IsLocal = info.IsLocal,
                    Detail = pair.DetailOrStatus,
                };
            }
            info.Token = pair.Body.Token;
            _store.Save(info);
            return await RefreshAsync(cancellationToken).ConfigureAwait(false);
        }

        private async Task<ConnectionStatus> CheckHealthAsync(Uri baseUri, string token, OctantPathSettings settings, CancellationToken cancellationToken)
        {
            var endpoint = baseUri.ToString();
            var result = await _client.GetHealthAsync(baseUri, token, settings.HostRequestTimeout, cancellationToken).ConfigureAwait(false);
            switch (result.Kind)
            {
                case HostResultKind.Unreachable:
                    return new ConnectionStatus { State = ConnectionState.Unreachable, Endpoint = endpoint, Detail = result.Detail };
                case HostResultKind.Malformed:
                    return new ConnectionStatus { State = ConnectionState.Error, Endpoint = endpoint, Detail = result.Detail };
                case HostResultKind.Failed:
                    if (result.IsVersionMismatch)
                    {
                        return VersionMismatch(baseUri, result.Error?.ApiVersion);
                    }
                    return new ConnectionStatus { State = ConnectionState.Error, Endpoint = endpoint, Detail = result.DetailOrStatus };
            }

            var health = result.Body;
            _readyBaseUri = null;
            _pairedBaseUri = null;
            if (!ContractVersion.TryParse(health.ApiVersion, out var hostApi))
            {
                return new ConnectionStatus { State = ConnectionState.Error, Endpoint = endpoint, Detail = $"apiVersion is malformed: {health.ApiVersion}" };
            }
            if (hostApi.Major != ContractVersion.Api.Major)
            {
                return VersionMismatch(baseUri, health.ApiVersion);
            }

            var status = new ConnectionStatus
            {
                Endpoint = endpoint,
                HostApiVersion = health.ApiVersion,
                HostVersion = health.HostVersion,
                Reason = health.Reason,
                UpdateReason = health.Update?.Reason,
                DbVersion = health.Db?.DbVersion,
                HostInstance = health.InstanceId,
                Signals = health.Signals,
                PresetsRevision = health.PresetsRevision,
                ViewRoles = ViewRolesOf(health.Db?.Roles),
            };

            // トークンが有効なときだけ paired: true が返る
            if (health.Paired != true)
            {
                status.State = ConnectionState.NeedsPairing;
                return status;
            }
            _pairedBaseUri = baseUri;
            _pairedToken = token;

            switch (health.Status)
            {
                case HealthStatus.Ready:
                    status.State = ConnectionState.Ready;
                    _readyBaseUri = baseUri;
                    _readyToken = token;
                    break;
                case HealthStatus.Loading:
                    status.State = ConnectionState.Loading;
                    break;
                case HealthStatus.Degraded:
                    status.State = ConnectionState.Degraded;
                    break;
                default:
                    status.State = ConnectionState.Error;
                    status.Detail = $"unknown status: {health.Status}";
                    break;
            }
            return status;
        }

        private static ConnectionStatus VersionMismatch(Uri baseUri, string hostApiVersion)
        {
            return new ConnectionStatus { State = ConnectionState.ApiVersionMismatch, Endpoint = baseUri.ToString(), HostApiVersion = hostApiVersion };
        }

        // db.roles（役割名 → ビューの一覧）を、ビュー → 役割名に裏返す。1 つのビューが 2 つの役割に出ることは契約にないので、先のものを使う
        public static Dictionary<string, string> ViewRolesOf(Dictionary<string, List<string>> roles)
        {
            if (roles == null)
            {
                return null;
            }
            var result = new Dictionary<string, string>();
            foreach (var role in roles)
            {
                foreach (var view in role.Value ?? new List<string>())
                {
                    if (view != null && !result.ContainsKey(view))
                    {
                        result[view] = role.Key;
                    }
                }
            }
            return result;
        }
    }
}
