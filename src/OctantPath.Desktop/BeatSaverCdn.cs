using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using OctantPath.Core.Maps;
using OctantPath.Core.Settings;
using Microsoft.AspNetCore.Http;

namespace OctantPath.Desktop
{
    // 画面のカバーと試聴を、Desktop のサーバーが BeatSaver の CDN から取って返す（/api/cdn?url=）。
    // ブラウザが直接取ると User-Agent がブラウザのものになり、開始数の上限もないため（external.md: どの部品か分かる User-Agent）。
    // 取るのは https の beatsaver.com とその下だけ（BeatSaverUrls）、画像と音声だけ。ブラウザの cookie や認証は渡さない（要求は Desktop が新しく作る）。
    // 受信量・同時数・待ち時間の上限は設定（cdnMaxMegabytes・cdnConcurrency・cdnTimeoutSeconds）
    public sealed class BeatSaverCdn
    {
        private readonly HttpClient _http;
        private readonly Func<OctantPathSettings> _settings;
        private readonly SemaphoreSlim _slots;

        public BeatSaverCdn(string userAgent, Func<OctantPathSettings> settings)
        {
            _settings = settings;
            var concurrency = Math.Max(1, settings().CdnConcurrency);
            _slots = new SemaphoreSlim(concurrency, concurrency);
            // 待ち時間は要求ごとに数える（HttpClient.Timeout はヘッダーまでしか数えない）
            _http = new HttpClient(new SocketsHttpHandler { UseCookies = false, MaxAutomaticRedirections = 3 }) { Timeout = Timeout.InfiniteTimeSpan };
            _http.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        }

        public async Task<IResult> GetAsync(string url, HttpResponse response, CancellationToken ct)
        {
            // 返す型は URL の拡張子で決める（画像と音声のほかは返さない）
            var type = Uri.TryCreate(url, UriKind.Absolute, out var uri) && BeatSaverUrls.Allowed(uri) ? Program.ContentType(uri.AbsolutePath) : "";
            if (!type.StartsWith("image/", StringComparison.Ordinal) && !type.StartsWith("audio/", StringComparison.Ordinal))
            {
                return Results.BadRequest();
            }
            var settings = _settings();
            var maxBytes = settings.CdnMaxBytes;
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                // 空きを待つ間と本文を読み終えるまでを含めて数える。止まった CDN が空きを持ち続けると、ほかのカバーと試聴も出なくなる
                timeout.CancelAfter(settings.CdnTimeout);
                try
                {
                    await _slots.WaitAsync(timeout.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
                }
                try
                {
                    using (var upstream = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token))
                    {
                        // 転送された先も同じ決まりで確かめる
                        if (!upstream.IsSuccessStatusCode || !BeatSaverUrls.Allowed(upstream.RequestMessage?.RequestUri) || upstream.Content.Headers.ContentLength > maxBytes)
                        {
                            return Results.StatusCode(StatusCodes.Status502BadGateway);
                        }
                        var body = new MemoryStream();
                        using (var stream = await upstream.Content.ReadAsStreamAsync(timeout.Token))
                        {
                            var buffer = new byte[81920];
                            int read;
                            while ((read = await stream.ReadAsync(buffer, timeout.Token)) > 0)
                            {
                                if (body.Length + read > maxBytes)
                                {
                                    return Results.StatusCode(StatusCodes.Status502BadGateway);
                                }
                                body.Write(buffer, 0, read);
                            }
                        }
                        // CDN のファイルは URL の hash ごとに決まるので（推測）、画面を描き直すたびに取り直さない
                        response.Headers["Cache-Control"] = "private, max-age=86400";
                        response.Headers["X-Content-Type-Options"] = "nosniff";
                        return Results.Bytes(body.ToArray(), type);
                    }
                }
                catch (Exception ex) when (ex is HttpRequestException || ex is IOException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
                {
                    return Results.StatusCode(StatusCodes.Status502BadGateway);
                }
                finally
                {
                    _slots.Release();
                }
            }
        }
    }
}
