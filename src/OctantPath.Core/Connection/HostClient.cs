using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OctantPath.Core.Recommendations;
using Newtonsoft.Json;

namespace OctantPath.Core.Connection
{
    // 契約3（OctantPath to OctantCompass）の MOD 側。HTTP の失敗は例外にせず HostResult で返す
    public sealed class HostClient
    {
        public const string ApiVersionHeader = "OctantPath-Api-Version";

        private readonly HttpClient _http;

        public HostClient(HttpClient http)
        {
            _http = http;
        }

        public Task<HostResult<HealthResponse>> GetHealthAsync(Uri baseUri, string token, TimeSpan timeout, CancellationToken cancellationToken)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUri, "v1/health"));
            return SendAsync<HealthResponse>(request, token, timeout, cancellationToken);
        }

        public Task<HostResult<PairResponse>> PairAsync(Uri baseUri, PairRequest body, TimeSpan timeout, CancellationToken cancellationToken)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri, "v1/pair"))
            {
                Content = new StringContent(JsonConvert.SerializeObject(body), Encoding.UTF8, "application/json"),
            };
            return SendAsync<PairResponse>(request, null, timeout, cancellationToken);
        }

        public Task<HostResult<RecommendationResponse>> RecommendAsync(Uri baseUri, string token, RecommendationRequest body, TimeSpan timeout, CancellationToken cancellationToken)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri, "v1/recommendations"))
            {
                Content = new StringContent(JsonConvert.SerializeObject(body), Encoding.UTF8, "application/json"),
            };
            return SendAsync<RecommendationResponse>(request, token, timeout, cancellationToken);
        }

        public Task<HostResult<Presets.PresetsResponse>> GetPresetsAsync(Uri baseUri, string token, TimeSpan timeout, CancellationToken cancellationToken)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUri, "v1/presets"));
            return SendAsync<Presets.PresetsResponse>(request, token, timeout, cancellationToken);
        }

        public Task<HostResult<Signals.SignalsResponse>> SendSignalsAsync(Uri baseUri, string token, Signals.SignalsRequest body, bool replace, TimeSpan timeout, CancellationToken cancellationToken)
        {
            var request = new HttpRequestMessage(replace ? HttpMethod.Put : HttpMethod.Post, new Uri(baseUri, "v1/signals"))
            {
                Content = new StringContent(JsonConvert.SerializeObject(body), Encoding.UTF8, "application/json"),
            };
            return SendAsync<Signals.SignalsResponse>(request, token, timeout, cancellationToken);
        }

        public Task<HostResult<Profile.ProfileResponse>> ProfileAsync(Uri baseUri, string token, Profile.ProfileRequest body, TimeSpan timeout, CancellationToken cancellationToken)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri, "v1/profile"))
            {
                Content = new StringContent(JsonConvert.SerializeObject(body), Encoding.UTF8, "application/json"),
            };
            return SendAsync<Profile.ProfileResponse>(request, token, timeout, cancellationToken);
        }

        // OctantCompass の応答の大きさの上限。推薦や要約の JSON には十分に大きく、悪意のある相手や壊れた相手にメモリを使い切らせない
        public const int MaxResponseBytes = 16 * 1024 * 1024;

        // 本文を UTF-8 の文字列として読む。MaxResponseBytes を超えたら読むのをやめて null
        private static async Task<string> ReadLimitedAsync(HttpContent content, CancellationToken cancellationToken)
        {
            if (content.Headers.ContentLength > MaxResponseBytes)
            {
                return null;
            }
            using (var stream = await content.ReadAsStreamAsync().ConfigureAwait(false))
            using (var buffer = new System.IO.MemoryStream())
            {
                var chunk = new byte[81920];
                int read;
                while ((read = await stream.ReadAsync(chunk, 0, chunk.Length, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    if (buffer.Length + read > MaxResponseBytes)
                    {
                        return null;
                    }
                    buffer.Write(chunk, 0, read);
                }
                return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
            }
        }

        public static Uri BaseUri(string host, int port) => new Uri($"http://{ConnectionInfo.BracketIPv6(host)}:{port}/");

        private async Task<HostResult<T>> SendAsync<T>(HttpRequestMessage request, string token, TimeSpan timeout, CancellationToken cancellationToken)
            where T : class
        {
            request.Headers.Add(ApiVersionHeader, ContractVersion.Api.ToString());
            if (!string.IsNullOrEmpty(token))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }

            using (var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                timeoutSource.CancelAfter(timeout);
                HttpResponseMessage response;
                string text;
                try
                {
                    response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutSource.Token).ConfigureAwait(false);
                    text = await ReadLimitedAsync(response.Content, timeoutSource.Token).ConfigureAwait(false);
                    if (text == null)
                    {
                        response.Dispose();
                        return HostResult<T>.Unreachable("response too large");
                    }
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    return HostResult<T>.Unreachable("timeout");
                }
                catch (HttpRequestException ex)
                {
                    return HostResult<T>.Unreachable(ex.InnerException?.Message ?? ex.Message);
                }
                finally
                {
                    request.Dispose();
                }

                using (response)
                {
                    if (response.IsSuccessStatusCode)
                    {
                        try
                        {
                            var body = JsonConvert.DeserializeObject<T>(text);
                            return body != null ? HostResult<T>.Ok(body) : HostResult<T>.Malformed((int)response.StatusCode, "empty body");
                        }
                        catch (JsonException ex)
                        {
                            return HostResult<T>.Malformed((int)response.StatusCode, ex.Message);
                        }
                    }

                    ErrorDetail error = null;
                    try
                    {
                        error = JsonConvert.DeserializeObject<ErrorBody>(text)?.Error;
                    }
                    catch (JsonException)
                    {
                    }
                    return HostResult<T>.Failed((int)response.StatusCode, error);
                }
            }
        }
    }

    public enum HostResultKind
    {
        Ok,
        Unreachable,
        Failed,
        Malformed,
    }

    public sealed class HostResult<T>
        where T : class
    {
        private HostResult(HostResultKind kind, T body, int status, ErrorDetail error, string detail)
        {
            Kind = kind;
            Body = body;
            StatusCode = status;
            Error = error;
            Detail = detail;
        }

        public HostResultKind Kind { get; }
        public T Body { get; }
        public int StatusCode { get; }
        public ErrorDetail Error { get; }
        public string Detail { get; }

        // 失敗を画面に出すときの詳しい理由。Detail（OctantCompass の message か、届かなかった理由）がなければ HTTP の状態コード
        public string DetailOrStatus => Detail ?? "HTTP " + StatusCode;

        public bool IsUnauthorized => Kind == HostResultKind.Failed && (StatusCode == (int)HttpStatusCode.Unauthorized || Error?.Code == "unauthorized");
        public bool IsUnknownPreset => Kind == HostResultKind.Failed && Error?.Code == "unknown_preset";
        public bool IsVersionMismatch => Kind == HostResultKind.Failed && (StatusCode == 426 || Error?.Code == "version_mismatch");

        // つなぐ先を忘れる失敗: トークンが通らない・版が違う・届かない（HostConnection は、次は /v1/health で確かめ直してから送る）
        internal bool LosesConnection => IsUnauthorized || IsVersionMismatch || Kind == HostResultKind.Unreachable;

        // 接続の状態を確かめ直すべき失敗: 上の 3 つと、OctantCompass の準備ができていない（503）。MOD と Desktop が同じ決まりで確かめ直す
        public bool ShouldRecheck => LosesConnection || StatusCode == 503;

        internal static HostResult<T> Ok(T body) => new HostResult<T>(HostResultKind.Ok, body, 200, null, null);
        internal static HostResult<T> Unreachable(string detail) => new HostResult<T>(HostResultKind.Unreachable, null, 0, null, detail);
        internal static HostResult<T> Failed(int status, ErrorDetail error) => new HostResult<T>(HostResultKind.Failed, null, status, error, error?.Message);
        internal static HostResult<T> Malformed(int status, string detail) => new HostResult<T>(HostResultKind.Malformed, null, status, null, detail);
    }
}
