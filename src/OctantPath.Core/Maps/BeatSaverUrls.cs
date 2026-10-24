using System;

namespace OctantPath.Core.Maps
{
    // BeatSaver の API が返す取得先（zip・カバー・試聴の CDN）として受け付ける URL。https で、ホストが beatsaver.com かその下だけ。
    // API の URL や応答が差し替えられても、ほかのホストからは取らない（OctantHarvest の allowed_beatsaver_url のホストの決まりと同じ。
    // ファイル名が <hash>.zip かは見ない: zip は展開した中身の hash を OctantPath 法 2.1 で確かめ、カバーと試聴の URL にも使うため）
    public static class BeatSaverUrls
    {
        public static bool Allowed(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) && Allowed(uri);

        public static bool Allowed(Uri uri)
        {
            if (uri == null || !uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps)
            {
                return false;
            }
            var host = uri.Host.TrimEnd('.').ToLowerInvariant();
            return host == "beatsaver.com" || host.EndsWith(".beatsaver.com", StringComparison.Ordinal);
        }
    }
}
