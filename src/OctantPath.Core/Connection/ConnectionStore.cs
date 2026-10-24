using System;
using System.IO;
using System.Net;
using Newtonsoft.Json;
using OctantPath.Core.Settings;

namespace OctantPath.Core.Connection
{
    // OctantPath の接続情報（UserData/OctantPath/connection.json）。つなぐ先とトークンの正本（憲法の原則 7）。
    // 契約6（client to OctantCompass）2.0 では OctantCompass のファイルを読まず、ここにあるつなぐ先へ通信だけでつなぐ
    public sealed class ConnectionInfo
    {
        // 同じ PC の OctantCompass が既定で待ち受けるところ（契約6 2.0）
        public const string DefaultHost = "127.0.0.1";
        public const int DefaultPort = 23287;

        // つなぐ先のアドレスとポート。空なら同じ PC の既定（DefaultHost と DefaultPort）
        [JsonProperty("host")] public string Host { get; set; }
        [JsonProperty("port")] public int? Port { get; set; }
        [JsonProperty("token")] public string Token { get; set; }

        [JsonIgnore] public string EffectiveHost => string.IsNullOrEmpty(Host) ? DefaultHost : Host;
        [JsonIgnore] public int EffectivePort => Port ?? DefaultPort;

        // つなぐ先が同じ PC か（OctantPath Desktop が OctantCompass の管理画面を開けるのは同じ PC のときだけ）
        [JsonIgnore] public bool IsLocal => IsLoopback(EffectiveHost);

        // 人に見せるつなぐ先（TryParseAddress で読み戻せる形）。空なら同じ PC の既定
        [JsonIgnore] public string Address => string.IsNullOrEmpty(Host) ? "" : Port == null ? BracketIPv6(Host) : $"{BracketIPv6(Host)}:{Port}";

        // IPv6 のアドレスは、ポートや URL と並べるときに角括弧で囲む
        public static string BracketIPv6(string host) => host.Contains(":") && !host.StartsWith("[", StringComparison.Ordinal) ? $"[{host}]" : host;

        public static bool IsLoopback(string host)
        {
            var h = (host ?? "").Trim('[', ']');
            return h == "localhost" || (IPAddress.TryParse(h, out var address) && IPAddress.IsLoopback(address));
        }

        // 人が入れたつなぐ先（"192.168.0.10"、"192.168.0.10:23287"、"[fe80::1]:23287" など）を読む。
        // ポートを省けば null（既定のポート）。読めなければ false
        public static bool TryParseAddress(string text, out string host, out int? port)
        {
            host = null;
            port = null;
            text = (text ?? "").Trim();
            if (text.Length == 0 || text.Contains("/") || text.Contains("@"))
            {
                return false;
            }
            // 角括弧のない IPv6 はポートを付けられないので、そのままアドレスとして読む
            if (text.IndexOf(':') != text.LastIndexOf(':') && !text.StartsWith("[", StringComparison.Ordinal))
            {
                if (!IPAddress.TryParse(text, out _))
                {
                    return false;
                }
                host = text;
                return true;
            }
            if (!Uri.TryCreate("http://" + text, UriKind.Absolute, out var uri) || uri.Host.Length == 0 || uri.PathAndQuery != "/")
            {
                return false;
            }
            host = uri.Host.Trim('[', ']');
            var explicitPort = text.StartsWith("[", StringComparison.Ordinal) ? text.Contains("]:") : text.Contains(":");
            port = explicitPort ? uri.Port : (int?)null;
            return true;
        }
    }

    public interface IConnectionStore
    {
        // 読めないときは null（壊れたファイルを新しい中身で上書きしないため）
        ConnectionInfo Load();
        void Save(ConnectionInfo info);
    }

    public sealed class ConnectionStore : IConnectionStore
    {
        public const string FileName = "connection.json";

        private readonly string _path;
        private readonly Action<string> _warn;

        public ConnectionStore(string directory, Action<string> warn)
        {
            _path = Path.Combine(directory, FileName);
            _warn = warn;
        }

        public ConnectionInfo Load()
        {
            if (!File.Exists(_path))
            {
                return new ConnectionInfo();
            }
            try
            {
                return JsonConvert.DeserializeObject<ConnectionInfo>(File.ReadAllText(_path)) ?? new ConnectionInfo();
            }
            catch (Exception ex)
            {
                _warn?.Invoke($"connection.json could not be read: {ex.Message}");
                return null;
            }
        }

        public void Save(ConnectionInfo info)
        {
            JsonFile.WriteAtomic(_path, JsonConvert.SerializeObject(info, Formatting.Indented));
        }
    }
}
