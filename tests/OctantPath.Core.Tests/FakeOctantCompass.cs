using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OctantPath.Core.Connection;

namespace OctantPath.Core.Tests
{
    // 決まった応答を返す偽の OctantCompass
    internal sealed class FakeOctantCompass : HttpMessageHandler
    {
        public readonly List<HttpRequestMessage> Requests = new List<HttpRequestMessage>();
        public readonly List<string> RequestBodies = new List<string>();
        public Func<HttpRequestMessage, (HttpStatusCode, string)> Health { get; set; }
        public Func<HttpRequestMessage, string, (HttpStatusCode, string)> Pair { get; set; }

        // /v1/health と /v1/pair のほか（/v1/presets・/v1/recommendations など）。省くと 404
        public Func<HttpRequestMessage, string, (HttpStatusCode, string)> Other { get; set; }
        public bool Down { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Down)
            {
                throw new HttpRequestException("connection refused");
            }
            Requests.Add(request);
            var body = request.Content != null ? await request.Content.ReadAsStringAsync(cancellationToken) : null;
            RequestBodies.Add(body);
            (HttpStatusCode status, string text) response = request.RequestUri.AbsolutePath switch
            {
                "/v1/health" => Health(request),
                "/v1/pair" => Pair(request, body),
                _ => Other != null ? Other(request, body) : (HttpStatusCode.NotFound, "{}"),
            };
            return new HttpResponseMessage(response.status) { Content = new StringContent(response.text, Encoding.UTF8, "application/json") };
        }

        public static string Token(HttpRequestMessage request) => request.Headers.Authorization?.Parameter;

        public static string ApiVersion(HttpRequestMessage request) =>
            request.Headers.TryGetValues(HostClient.ApiVersionHeader, out var values) ? values.Single() : null;
    }

    internal sealed class MemoryStore : IConnectionStore
    {
        public ConnectionInfo Info { get; set; } = new ConnectionInfo();
        public bool Unreadable { get; set; }
        public int Saves { get; private set; }

        public ConnectionInfo Load() => Unreadable ? null : new ConnectionInfo { Host = Info.Host, Port = Info.Port, Token = Info.Token };

        public void Save(ConnectionInfo info)
        {
            Info = info;
            Saves++;
        }
    }
}
