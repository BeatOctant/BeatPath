using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using OctantPath.Core.Maps;
using UnityEngine.Networking;
using Zenject;

namespace OctantPath.UI
{
    // 推薦一覧で選んだ曲の試聴。BeatSaver の試聴の音源を取り、ゲームの試聴の仕組みで流す。
    // 取っている間は前の曲を流したままにする（止めると音が途切れる。2026-10-18）。取っている様子は Progress で画面に出す。
    // URL は BeatSaver の API の応答から来るが、取るのは https の beatsaver.com とその下からだけ（BeatSaverUrls）。
    // UnityWebRequest は受信の途中で止められないので、大きさは取り終えてから cdnMaxMegabytes と比べ、超えたものは使わない。
    // 下見に並んだ曲の試聴は、選ぶ前に 1 曲ずつ先読みする（Prefetch。2026-10-22）。取るのは画面に出ている曲の分だけで、カバーと同じ範囲。
    // 持っておく音源は MaxClips まで。超えたら古いものから捨てる（流しているものは捨てない）
    internal sealed class PreviewPlayer
    {
        private readonly SongPreviewPlayer _songPreviewPlayer;
        private readonly Installers.SettingsHolder _settings;
        private readonly Dictionary<string, AudioClip> _clips = new Dictionary<string, AudioClip>();
        private readonly Dictionary<string, UnityWebRequest> _loading = new Dictionary<string, UnityWebRequest>();
        // 取っている途中の音源。同じ URL を続けて頼まれたら、2 度取らずに同じ結果を待つ
        private readonly Dictionary<string, Task<AudioClip>> _pending = new Dictionary<string, Task<AudioClip>>();
        private string _wanted;
        // いま流している音源の URL（捨てない）
        private string _playing;
        // 持っている音源の古い順（捨てる順）
        private readonly LinkedList<string> _order = new LinkedList<string>();
        // 先読みで取れなかった URL。先読みでは取り直さない（選んだときは取り直す）
        private readonly HashSet<string> _prefetchFailed = new HashSet<string>();
        // 先読みの世代。新しく頼まれたら前の先読みはやめる
        private int _prefetchGeneration;

        // 持っておく音源の数の上限（下見の曲数の上限の 2 倍。推測で決めた値）。試聴の音源は圧縮したまま持つので 1 つ数百 KB（推測）
        private const int MaxClips = 2 * Core.Mix.MixDraftRules.MaxSize;

        // 試聴の音源を取るのにかかった時間の目安（秒）。取り終えるたびに、これまでの値と混ぜて更新する（最初の値は推測）
        public float TypicalSeconds { get; private set; } = 1.5f;

        public PreviewPlayer([InjectOptional] SongPreviewPlayer songPreviewPlayer, Installers.SettingsHolder settings)
        {
            _songPreviewPlayer = songPreviewPlayer ?? Resources.FindObjectsOfTypeAll<SongPreviewPlayer>().FirstOrDefaultSafe();
            _settings = settings;
        }

        public async void Play(string url)
        {
            if (_songPreviewPlayer == null || string.IsNullOrEmpty(url))
            {
                return;
            }
            _wanted = url;
            var clip = await LoadAsync(url);
            // 読み込んでいる間に別の曲を選んだら流さない
            if (_wanted != url)
            {
                return;
            }
            // 取れなければ前の曲を止める（画面は選んだ曲に移っているので、前の曲を流し続けると食い違う）
            if (clip == null)
            {
                Stop();
                return;
            }
            _playing = url;
            _songPreviewPlayer.CrossfadeTo(clip, -4f, 0f, clip.length, null);
        }

        // urls の試聴の音源を、並んだ順に 1 曲ずつ取っておく（同時には取らない）。選んだ曲を取っている途中なら、それが終わってから始める。
        // wanted が偽になるか、次の Prefetch か StopPrefetch が呼ばれたらやめる
        public async void Prefetch(IReadOnlyList<string> urls, Func<bool> wanted)
        {
            var generation = ++_prefetchGeneration;
            bool Current() => generation == _prefetchGeneration && wanted();
            try
            {
                if (_wanted != null && _pending.TryGetValue(_wanted, out var selected))
                {
                    await selected;
                }
                var fetched = 0;
                foreach (var url in urls)
                {
                    if (!Current())
                    {
                        return;
                    }
                    if (string.IsNullOrEmpty(url) || _clips.ContainsKey(url) || _prefetchFailed.Contains(url))
                    {
                        continue;
                    }
                    if (await LoadAsync(url) == null)
                    {
                        _prefetchFailed.Add(url);
                    }
                    else
                    {
                        fetched++;
                    }
                }
                if (fetched > 0)
                {
                    Plugin.Log.Debug($"[preview] prefetched {fetched} of {urls.Count}");
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.Warn($"[preview] prefetch stopped: {ex.Message}");
            }
        }

        public void StopPrefetch() => _prefetchGeneration++;

        // その試聴の音源を取っている途中なら、受け取った割合（0〜1。大きさが分からなければ 0）。取っていなければ null。メインスレッドで呼ぶ
        public float? Progress(string url)
        {
            if (url == null || !_loading.TryGetValue(url, out var request))
            {
                return null;
            }
            return Mathf.Clamp01(request.downloadProgress);
        }

        public void Stop()
        {
            _wanted = null;
            _playing = null;
            if (_songPreviewPlayer != null)
            {
                _songPreviewPlayer.CrossfadeToDefault();
            }
        }

        private Task<AudioClip> LoadAsync(string url)
        {
            if (_clips.TryGetValue(url, out var cached))
            {
                // 使ったものは捨てる順の後ろへ
                _order.Remove(url);
                _order.AddLast(url);
                return Task.FromResult(cached);
            }
            if (_pending.TryGetValue(url, out var pending))
            {
                return pending;
            }
            var task = FetchAsync(url);
            // 最初の await の前に終わっていたら（要求を作れなかったなど）、FetchAsync の finally はもう走っているので、ここでは覚えない
            if (!task.IsCompleted)
            {
                _pending[url] = task;
            }
            return task;
        }

        private async Task<AudioClip> FetchAsync(string url)
        {
            try
            {
                return await DownloadAsync(url);
            }
            finally
            {
                _pending.Remove(url);
            }
        }

        private void Keep(string url, AudioClip clip)
        {
            _clips[url] = clip;
            _order.Remove(url);
            _order.AddLast(url);
            var node = _order.First;
            while (_clips.Count > MaxClips && node != null)
            {
                var next = node.Next;
                var old = node.Value;
                if (old != _playing && old != _wanted && old != url)
                {
                    if (_clips.TryGetValue(old, out var dropped) && dropped != null)
                    {
                        UnityEngine.Object.Destroy(dropped);
                    }
                    _clips.Remove(old);
                    _order.Remove(node);
                }
                node = next;
            }
        }

        private async Task<AudioClip> DownloadAsync(string url)
        {
            if (!BeatSaverUrls.Allowed(url))
            {
                Plugin.Log.Debug($"preview {url}: not a BeatSaver URL");
                return null;
            }
            using (var request = UnityWebRequestMultimedia.GetAudioClip(url, AudioType.MPEG))
            {
                // 共有の HttpClient を通らないので、BeatSaver の API への要求と同じ User-Agent をここで付ける
                request.SetRequestHeader("User-Agent", Plugin.UserAgent);
                // 受け取った音源を一度に展開すると、その瞬間に画面が止まる。圧縮したまま持ち、流すときに少しずつ展開させる（推測で選んだ設定）
                ((DownloadHandlerAudioClip)request.downloadHandler).compressed = true;
                var done = new TaskCompletionSource<bool>();
                _loading[url] = request;
                var started = Time.unscaledTime;
                try
                {
                    request.SendWebRequest().completed += _ => done.TrySetResult(true);
                    await done.Task;
                }
                finally
                {
                    _loading.Remove(url);
                }
                if (request.result != UnityWebRequest.Result.Success)
                {
                    Plugin.Log.Debug($"preview {url}: {request.error}");
                    return null;
                }
                if (request.downloadedBytes > (ulong)_settings.Current.CdnMaxBytes)
                {
                    Plugin.Log.Debug($"preview {url}: larger than {_settings.Current.CdnMaxBytes} bytes");
                    return null;
                }
                TypicalSeconds = Mathf.Lerp(TypicalSeconds, Mathf.Max(0.2f, Time.unscaledTime - started), 0.3f);
                var clip = DownloadHandlerAudioClip.GetContent(request);
                Keep(url, clip);
                return clip;
            }
        }
    }

    internal static class EnumerableExtensions
    {
        public static T FirstOrDefaultSafe<T>(this T[] items) where T : class => items != null && items.Length > 0 ? items[0] : null;
    }
}
