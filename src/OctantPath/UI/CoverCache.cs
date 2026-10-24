using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using OctantPath.Core.Maps;
using UnityEngine;

namespace OctantPath.UI
{
    // 推薦一覧に出すカバー画像。1 枚ずつ取り、メモリに置く。
    // URL は BeatSaver の API の応答から来るが、取るのは https の beatsaver.com とその下からだけ（BeatSaverUrls）で、
    // 大きさは設定の cdnMaxMegabytes まで（Desktop の /api/cdn と同じ決まり。notes/security-audit-2026-10-21.md, 1）
    internal sealed class CoverCache
    {
        private readonly HttpClient _http;
        private readonly Installers.SettingsHolder _settings;
        private readonly Dictionary<string, Sprite> _sprites = new Dictionary<string, Sprite>();
        private readonly SemaphoreSlim _serial = new SemaphoreSlim(1, 1);

        public CoverCache(HttpClient http, Installers.SettingsHolder settings)
        {
            _http = http;
            _settings = settings;
        }

        public bool TryGet(string url, out Sprite sprite) => _sprites.TryGetValue(url ?? "", out sprite);

        public async Task<Sprite> GetAsync(string url, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(url))
            {
                return null;
            }
            if (_sprites.TryGetValue(url, out var cached))
            {
                return cached;
            }
            if (!BeatSaverUrls.Allowed(url))
            {
                Plugin.Log.Debug($"cover {url}: not a BeatSaver URL");
                _sprites[url] = null;
                return null;
            }
            await _serial.WaitAsync(cancellationToken).ConfigureAwait(true);
            try
            {
                if (_sprites.TryGetValue(url, out cached))
                {
                    return cached;
                }
                var bytes = await DownloadAsync(url, _settings.Current.CdnMaxBytes, cancellationToken).ConfigureAwait(true);
                // Proton では System.Drawing を使う読み込みが失敗するので、Unity の読み込みを使う
                var sprite = BeatSaberMarkupLanguage.Utilities.LoadSpriteRaw(bytes);
                _sprites[url] = sprite;
                return sprite;
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                Plugin.Log.Debug($"cover {url}: {ex.Message}");
                _sprites[url] = null;
                return null;
            }
            finally
            {
                _serial.Release();
            }
        }

        // 転送された先も同じ決まりで確かめ、上限を超える応答は途中で止める（MapInstaller.DownloadAsync と同じ形）
        private async Task<byte[]> DownloadAsync(string url, long maxBytes, CancellationToken cancellationToken)
        {
            using (var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
            {
                if (!BeatSaverUrls.Allowed(response.RequestMessage?.RequestUri))
                {
                    throw new HttpRequestException("redirected off beatsaver.com");
                }
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength > maxBytes)
                {
                    throw new HttpRequestException($"larger than {maxBytes} bytes");
                }
                using (var body = new MemoryStream())
                using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                {
                    var buffer = new byte[81920];
                    int read;
                    while ((read = await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false)) > 0)
                    {
                        if (body.Length + read > maxBytes)
                        {
                            throw new HttpRequestException($"larger than {maxBytes} bytes");
                        }
                        body.Write(buffer, 0, read);
                    }
                    return body.ToArray();
                }
            }
        }
    }
}
