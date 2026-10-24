using System;
using System.Threading;
using System.Threading.Tasks;
using OctantPath.Core.Connection;
using OctantPath.Core.Presets;
using OctantPath.Installers;
using IPA.Utilities;
using Zenject;
using OctantPath.Core.Languages;

namespace OctantPath.Connection
{
    // OctantCompass との接続を保つ。ready でない間は設定の間隔で /v1/health を確かめ直す（契約3（OctantPath to OctantCompass）の失敗時の振る舞い）。
    // OctantCompass は起動しない（契約6（client to OctantCompass）2.0。OctantCompass は自分で常駐する）。プレイ中は通信しない。
    internal sealed class ConnectionService : IInitializable, IDisposable
    {
        private readonly HostConnection _connection;
        private readonly SettingsHolder _settings;
        private readonly GameScenesManager _gameScenesManager;
        private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();
        private readonly SemaphoreSlim _refreshLock = new SemaphoreSlim(1, 1);
        private readonly PresetsCache _presets = new PresetsCache();

        public ConnectionService(HostClient client, IConnectionStore store, SettingsHolder settings, GameScenesManager gameScenesManager)
        {
            _settings = settings;
            _gameScenesManager = gameScenesManager;
            _connection = new HostConnection(client, store, () => _settings.Current, DefaultEnvironmentName());
        }

        public HostConnection Connection => _connection;

        public ConnectionStatus Status { get; private set; } = new ConnectionStatus { State = ConnectionState.Unknown };

        public event Action<ConnectionStatus> StatusChanged;

        // OctantCompass の開示するプリセットの一覧（OctantSpecs plans/presets.md）。まだ取っていなければ null
        public PresetsResponse Presets => _presets.Presets;

        // 一覧が取れなかった理由。取れたら null
        public string PresetsProblem { get; private set; }

        public event Action PresetsChanged;

        public void Initialize()
        {
            _ = RunAsync();
        }

        public void Dispose()
        {
            _lifetime.Cancel();
        }

        // 推薦の要求。失敗したら接続の状態を確かめ直す
        public async Task<HostResult<Core.Recommendations.RecommendationResponse>> RecommendAsync(Core.Recommendations.RecommendationRequest request, CancellationToken cancellationToken)
        {
            var result = await SendAsync(() => _connection.RecommendAsync(request, cancellationToken)).ConfigureAwait(true);
            if (PresetsCache.NeedsReload(result))
            {
                await ReloadPresetsAsync().ConfigureAwait(true);
            }
            return result;
        }

        public Task<HostResult<Core.Profile.ProfileResponse>> ProfileAsync(Core.Profile.ProfileRequest request, CancellationToken cancellationToken) =>
            SendAsync(() => _connection.ProfileAsync(request, cancellationToken));

        // ready でなければ確かめ直してから送り、失敗の種類によって確かめ直す
        private async Task<HostResult<T>> SendAsync<T>(Func<Task<HostResult<T>>> send)
            where T : class
        {
            if (Status.State != ConnectionState.Ready || _connection.ReadyBaseUri == null)
            {
                await RefreshAsync().ConfigureAwait(true);
            }
            var result = await send().ConfigureAwait(true);
            if (NeedsRefresh(result))
            {
                await RefreshAsync().ConfigureAwait(true);
            }
            return result;
        }

        private static bool NeedsRefresh<T>(HostResult<T> result)
            where T : class =>
            result == null || result.ShouldRecheck;

        // 一覧を取り直す。unknown_preset を受けたときや、revision が変わったとき
        public async Task ReloadPresetsAsync()
        {
            var result = await _connection.GetPresetsAsync(_lifetime.Token).ConfigureAwait(true);
            if (_presets.Update(result))
            {
                PresetsProblem = null;
                Plugin.Log.Info($"[presets] revision {result.Body.Revision}: {string.Join(", ", result.Body.Presets.ConvertAll(p => p.Id))}");
            }
            else
            {
                PresetsProblem = _presets.Failure == PresetsFailure.NotConnected ? Lang.T("connection.notConnected")
                    : Lang.T("presets.failed", new { detail = _presets.FailureDetail });
                Plugin.Log.Warn($"[presets] {PresetsProblem}");
            }
            PresetsChanged?.Invoke();
        }

        // 画面から「確かめ直す」を押したとき
        public async Task RefreshNowAsync()
        {
            _settings.Reload();
            await RefreshAsync().ConfigureAwait(true);
        }

        // 画面から合言葉を入れたとき（契約6 2.0）。address が空なら今のつなぐ先のまま。読めないアドレスなら false
        public async Task<bool> PairAsync(string address, string code)
        {
            string host = null;
            int? port = null;
            if (!string.IsNullOrWhiteSpace(address) && !ConnectionInfo.TryParseAddress(address, out host, out port))
            {
                return false;
            }
            await _refreshLock.WaitAsync(_lifetime.Token).ConfigureAwait(true);
            try
            {
                ConnectionStatus status;
                try
                {
                    status = await _connection.PairAsync(code, host, port, _lifetime.Token).ConfigureAwait(true);
                }
                catch (OperationCanceledException)
                {
                    return true;
                }
                catch (Exception ex)
                {
                    status = new ConnectionStatus { State = ConnectionState.Error, Detail = ex.Message };
                }
                Plugin.Log.Info($"[connection] pairing: {status}");
                Status = status;
                StatusChanged?.Invoke(status);
            }
            finally
            {
                _refreshLock.Release();
            }
            if (Status.State == ConnectionState.Ready)
            {
                await ReloadPresetsAsync().ConfigureAwait(true);
            }
            return true;
        }

        private async Task RunAsync()
        {
            var token = _lifetime.Token;
            try
            {
                while (!token.IsCancellationRequested)
                {
                    // ready の間は定期には確かめない。画面を開いたときや要求の失敗で確かめ直す
                    if (Status.State != ConnectionState.Ready && !_gameScenesManager.InGameplay())
                    {
                        await RefreshAsync().ConfigureAwait(true);
                    }
                    await Task.Delay(_settings.Current.HealthRetryInterval, token).ConfigureAwait(true);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Plugin.Log.Error($"connection loop stopped: {ex}");
            }
        }

        private async Task RefreshAsync()
        {
            if (!await _refreshLock.WaitAsync(0).ConfigureAwait(true))
            {
                return;
            }
            try
            {
                ConnectionStatus status;
                try
                {
                    status = await _connection.RefreshAsync(_lifetime.Token).ConfigureAwait(true);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    status = new ConnectionStatus { State = ConnectionState.Error, Detail = ex.Message };
                }

                var changed = status.State != Status.State || status.Reason != Status.Reason || status.Endpoint != Status.Endpoint;
                Status = status;
                if (changed)
                {
                    Plugin.Log.Info($"[connection] {status}");
                }
                StatusChanged?.Invoke(status);

            }
            finally
            {
                _refreshLock.Release();
            }
            // まだ取っていないときと、revision が変わったときだけ取り直す
            if (_presets.NeedsReload(Status))
            {
                await ReloadPresetsAsync().ConfigureAwait(true);
            }
        }

        private static string DefaultEnvironmentName()
        {
            var folder = System.IO.Path.GetFileName(UnityGame.InstallPath.TrimEnd('/', '\\'));
            return $"Beat Saber {UnityGame.GameVersion} ({folder})";
        }
    }
}
