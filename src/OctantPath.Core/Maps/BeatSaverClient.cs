using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using OctantPath.Core.Settings;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OctantPath.Core.Languages;

namespace OctantPath.Core.Maps
{
    // BeatSaver API（外部依存）。問い合わせはまとめて（1 回 50 件まで）、直列に、間隔を空けて送る。
    // 429 には間隔を倍にして再試行し、上限で止める
    public sealed class BeatSaverClient
    {
        public const int MaxHashesPerRequest = 50;

        private readonly HttpClient _http;
        private readonly Func<OctantPathSettings> _settings;
        private readonly Func<TimeSpan, CancellationToken, Task> _delay;
        private readonly SemaphoreSlim _serial = new SemaphoreSlim(1, 1);
        private DateTime _lastRequest = DateTime.MinValue;

        public BeatSaverClient(HttpClient http, Func<OctantPathSettings> settings, Func<TimeSpan, CancellationToken, Task> delay = null)
        {
            _http = http;
            _settings = settings;
            _delay = delay ?? Task.Delay;
        }

        // 見つからなかった hash は結果に入らない。キーは小文字の hash
        public async Task<Dictionary<string, BeatSaverMap>> MapsByHashAsync(IEnumerable<string> hashes, CancellationToken cancellationToken)
        {
            var result = new Dictionary<string, BeatSaverMap>();
            var wanted = hashes.Select(MapHash.Normalize).Where(h => h != null).Distinct().ToList();
            for (var i = 0; i < wanted.Count; i += MaxHashesPerRequest)
            {
                var chunk = wanted.Skip(i).Take(MaxHashesPerRequest).ToList();
                var text = await GetAsync($"maps/hash/{string.Join(",", chunk)}", cancellationToken).ConfigureAwait(false);
                if (text == null)
                {
                    continue;
                }
                var json = JToken.Parse(text);
                if (chunk.Count == 1)
                {
                    // 1 件だけのときはマップがそのまま返る
                    var map = json.Type == JTokenType.Object && json["versions"] != null ? json.ToObject<BeatSaverMap>() : null;
                    if (map != null)
                    {
                        result[chunk[0]] = map;
                    }
                    continue;
                }
                foreach (var property in ((JObject)json).Properties())
                {
                    var hash = MapHash.Normalize(property.Name);
                    if (hash != null && property.Value.Type == JTokenType.Object)
                    {
                        result[hash] = property.Value.ToObject<BeatSaverMap>();
                    }
                }
            }
            return result;
        }

        // キー（BeatSaver の id）でマップを引く。見つからなければ null
        public async Task<BeatSaverMap> MapByKeyAsync(string key, CancellationToken cancellationToken)
        {
            var text = await GetAsync($"maps/id/{Uri.EscapeDataString(key)}", cancellationToken).ConfigureAwait(false);
            return text == null ? null : JObject.Parse(text).ToObject<BeatSaverMap>();
        }

        private async Task<string> GetAsync(string path, CancellationToken cancellationToken)
        {
            await _serial.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var settings = _settings();
                var interval = settings.BeatSaverApiInterval;
                var backoff = interval;
                while (true)
                {
                    var wait = _lastRequest + interval - DateTime.UtcNow;
                    if (wait > TimeSpan.Zero)
                    {
                        await _delay(wait, cancellationToken).ConfigureAwait(false);
                    }
                    _lastRequest = DateTime.UtcNow;

                    var uri = new Uri(new Uri(settings.BeatSaverApiUrl), path);
                    using (var response = await _http.GetAsync(uri, cancellationToken).ConfigureAwait(false))
                    {
                        if ((int)response.StatusCode == 429)
                        {
                            backoff = TimeSpan.FromTicks(backoff.Ticks * 2);
                            if (backoff > settings.BeatSaverApiMaxBackoff)
                            {
                                throw new BeatSaverException(Lang.T("beatSaver.tooManyRequests"));
                            }
                            await _delay(backoff, cancellationToken).ConfigureAwait(false);
                            continue;
                        }
                        if (response.StatusCode == HttpStatusCode.NotFound)
                        {
                            return null;
                        }
                        if (!response.IsSuccessStatusCode)
                        {
                            throw new BeatSaverException(Lang.T("beatSaver.status", new { status = (int)response.StatusCode }));
                        }
                        return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    }
                }
            }
            finally
            {
                _serial.Release();
            }
        }
    }

    public sealed class BeatSaverException : Exception
    {
        public BeatSaverException(string message) : base(message)
        {
        }
    }
}
