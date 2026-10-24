using System;
using Microsoft.AspNetCore.Http;

namespace OctantPath.Desktop
{
    // 手元のサーバー（127.0.0.1 だけで待ち受ける）を、このページのためだけに使わせる。OctantCompass の管理 API（manage.py）に倣った守り。
    // 違い: このページは手元のサーバーから開くので、Origin はこのページのもの（http://<Host>）を通し、状態を変える要求でだけ見る
    // （ほかのサイトの GET は通るが、CORS の許可がないので中身は読めない）。Host はポートも見る。
    // 本文は JSON だけ。相手のアドレスは見ない（127.0.0.1 だけで待ち受けるので、いつも手元から）。
    // ページは Desktop の窓で開くが、サーバーには利用者のブラウザのほかのサイトからも要求が届く
    public static class LocalGuard
    {
        // 断る理由のコード。通すなら null
        public static string Refuse(HttpRequest request, int port)
        {
            // Host はこのサーバーの名前（127.0.0.1 か localhost と、待ち受けのポート）だけ。DNS リバインディングで別の名前から来た要求を止める。
            // [::1] では待ち受けていないので受け付けない
            var host = request.Host;
            var name = host.HasValue ? host.Host.ToLowerInvariant() : "";
            if ((name != "127.0.0.1" && name != "localhost") || (host.Port ?? 80) != port)
            {
                return "other_host";
            }
            if (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method) || HttpMethods.IsOptions(request.Method))
            {
                return null;
            }
            // 状態を変える要求: ブラウザが Origin を付けたなら、このページのもの（http://<Host>）だけ
            var origin = request.Headers.Origin.ToString();
            if (origin.Length > 0 && !string.Equals(origin, "http://" + host.Value, StringComparison.OrdinalIgnoreCase))
            {
                return "other_site";
            }
            // 本文は JSON だけ。text/plain のように事前確認（CORS）なしで別のサイトから送れる形は受け付けない
            if (!request.HasJsonContentType())
            {
                return "content_type";
            }
            return null;
        }
    }
}
