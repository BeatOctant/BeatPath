using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using OctantPath.Core.Settings;
using OctantPath.Core.Languages;

namespace OctantPath.Core.Maps
{
    // マップの zip を取り、作業用の場所で展開と検証をして、通ったものだけを置き場所に移す（OctantPath 法 2.1〜2.4）
    public sealed class MapInstaller
    {
        private readonly HttpClient _http;
        private readonly Func<OctantPathSettings> _settings;

        public MapInstaller(HttpClient http, Func<OctantPathSettings> settings)
        {
            _http = http;
            _settings = settings;
        }

        // levelsDirectory と workDirectory は同じファイルシステムに置く（移すときに途中の状態を見せないため）。
        // 取るのは https の beatsaver.com とその下からだけ（BeatSaverUrls）
        // received は受け取った割合（0〜1）。大きさが分かるときだけ、受け取るたびに呼ぶ（呼ぶスレッドは決まっていない）
        public Task<InstallResult> InstallAsync(string expectedHash, Uri downloadUrl, string folderName, string levelsDirectory, string workDirectory, CancellationToken cancellationToken, Action<double> received = null)
        {
            if (!BeatSaverUrls.Allowed(downloadUrl))
            {
                return Task.FromResult(InstallResult.Fail(Lang.T("install.notBeatSaver")));
            }
            return InstallAsync(expectedHash, (zipPath, ct) => DownloadAsync(downloadUrl, zipPath, _settings().ZipMaxDownloadBytes, ct, received), folderName, levelsDirectory, workDirectory, cancellationToken);
        }

        // 設定の取得元（zipSource）から入れる。BeatSaver なら推薦された版（version）の URL から、ローカルのフォルダならそこの zip から。
        // ローカルにないマップは BeatSaver から取らずに飛ばす（OctantPath 法 2.8）。MOD と Desktop が同じものを使う
        public Task<InstallResult> InstallFromSourceAsync(string expectedHash, BeatSaverVersion version, string folderName, string levelsDirectory, string workDirectory, CancellationToken cancellationToken, Action<double> received = null)
        {
            var source = _settings().ZipSource;
            if (ZipSources.IsBeatSaver(source))
            {
                if (string.IsNullOrEmpty(version?.DownloadUrl))
                {
                    return Task.FromResult(InstallResult.Fail(Lang.T("download.noVersion")));
                }
                Uri.TryCreate(version.DownloadUrl, UriKind.Absolute, out var url);
                return InstallAsync(expectedHash, url, folderName, levelsDirectory, workDirectory, cancellationToken, received);
            }
            var zip = ZipSources.LocalZip(source, expectedHash);
            if (zip == null)
            {
                return Task.FromResult(InstallResult.Fail(Lang.T("download.notInFolder")));
            }
            return InstallFromFileAsync(expectedHash, zip, folderName, levelsDirectory, workDirectory, cancellationToken);
        }

        // ローカルの zip から入れる。取得元のフォルダは読むだけで、書き込まず消さない（OctantPath 法 2.8）
        public Task<InstallResult> InstallFromFileAsync(string expectedHash, string sourceZip, string folderName, string levelsDirectory, string workDirectory, CancellationToken cancellationToken)
        {
            return InstallAsync(expectedHash, (zipPath, ct) => CopyAsync(sourceZip, zipPath, _settings().ZipMaxDownloadBytes, ct), folderName, levelsDirectory, workDirectory, cancellationToken);
        }

        private async Task<InstallResult> InstallAsync(string expectedHash, Func<string, CancellationToken, Task<string>> fetch, string folderName, string levelsDirectory, string workDirectory, CancellationToken cancellationToken)
        {
            var hash = MapHash.Normalize(expectedHash);
            if (hash == null)
            {
                return InstallResult.Fail(Lang.T("install.badHash"));
            }
            if (!SafeFolderName(folderName))
            {
                return InstallResult.Fail(Lang.T("install.badFolderName"));
            }
            var target = Path.Combine(levelsDirectory, folderName);
            if (Directory.Exists(target) || File.Exists(target))
            {
                return InstallResult.Fail(Lang.T("install.nameTaken"));
            }

            var settings = _settings();
            var job = Path.Combine(workDirectory, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(job);
            try
            {
                var zipPath = Path.Combine(job, "map.zip");
                var downloaded = await fetch(zipPath, cancellationToken).ConfigureAwait(false);
                if (downloaded != null)
                {
                    return InstallResult.Fail(downloaded);
                }

                var extracted = Path.Combine(job, "map");
                var extractError = Extract(zipPath, extracted, settings.ZipMaxEntries, settings.ZipMaxExtractedBytes);
                if (extractError != null)
                {
                    return InstallResult.Fail(extractError);
                }

                var computed = MapHash.Compute(extracted);
                if (!computed.Succeeded)
                {
                    return InstallResult.Fail(computed.Error);
                }
                if (computed.Hash != hash)
                {
                    return InstallResult.Fail(Lang.T("install.hashMismatch", new { hash = computed.Hash }));
                }

                cancellationToken.ThrowIfCancellationRequested();
                Directory.CreateDirectory(levelsDirectory);
                if (Directory.Exists(target) || File.Exists(target))
                {
                    return InstallResult.Fail(Lang.T("install.nameTaken"));
                }
                Directory.Move(extracted, target);
                return InstallResult.Ok(target, computed.Hash);
            }
            finally
            {
                // 片付けるのは、この処理で作ったものだけ
                TryDelete(job);
            }
        }

        private static async Task<string> CopyAsync(string source, string path, long maxBytes, CancellationToken cancellationToken)
        {
            if (!File.Exists(source))
            {
                return Lang.T("install.noZipInFolder");
            }
            if (new FileInfo(source).Length > maxBytes)
            {
                return Lang.T("install.zipTooLarge");
            }
            using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var output = File.Create(path))
            {
                await input.CopyToAsync(output, 81920, cancellationToken).ConfigureAwait(false);
            }
            return null;
        }

        private async Task<string> DownloadAsync(Uri url, string path, long maxBytes, CancellationToken cancellationToken, Action<double> received)
        {
            using (var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
            {
                // 転送された先も同じ決まりで確かめる
                if (!BeatSaverUrls.Allowed(response.RequestMessage?.RequestUri ?? url))
                {
                    return Lang.T("install.notBeatSaver");
                }
                if (!response.IsSuccessStatusCode)
                {
                    return Lang.T("install.zipStatus", new { status = (int)response.StatusCode });
                }
                if (response.Content.Headers.ContentLength > maxBytes)
                {
                    return Lang.T("install.zipTooLarge");
                }
                using (var source = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                using (var file = File.Create(path))
                {
                    var buffer = new byte[81920];
                    long total = 0;
                    int read;
                    while ((read = await source.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false)) > 0)
                    {
                        total += read;
                        if (total > maxBytes)
                        {
                            return Lang.T("install.zipTooLarge");
                        }
                        await file.WriteAsync(buffer, 0, read, cancellationToken).ConfigureAwait(false);
                        if (response.Content.Headers.ContentLength > 0)
                        {
                            received?.Invoke(Math.Min(1.0, (double)total / response.Content.Headers.ContentLength.Value));
                        }
                    }
                }
            }
            return null;
        }

        // 失敗の理由を返す。成功なら null
        internal static string Extract(string zipPath, string destination, int maxEntries, long maxBytes)
        {
            ZipArchive archive;
            try
            {
                archive = ZipFile.OpenRead(zipPath);
            }
            catch (Exception ex)
            {
                return Lang.T("install.zipUnreadable", new { error = ex.Message });
            }
            using (archive)
            {
                var entries = archive.Entries;
                if (entries.Count > maxEntries)
                {
                    return Lang.T("install.zipTooManyEntries");
                }
                foreach (var entry in entries)
                {
                    if (!MapHash.SafeName(entry.FullName.TrimEnd('/', '\\')))
                    {
                        return Lang.T("install.zipBadName", new { name = entry.FullName });
                    }
                }

                // マップの根は Info.dat のある場所。1 つに決まらなければ入れない
                var infos = entries.Where(e => string.Equals(Path.GetFileName(e.FullName), "Info.dat", StringComparison.OrdinalIgnoreCase)).ToList();
                if (infos.Count != 1)
                {
                    return Lang.T(infos.Count == 0 ? "install.zipNoInfo" : "install.zipManyRoots");
                }
                var root = Normalize(Path.GetDirectoryName(infos[0].FullName) ?? "");
                var prefix = root.Length == 0 ? "" : root + "/";

                Directory.CreateDirectory(destination);
                var destinationFull = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
                long total = 0;
                var buffer = new byte[81920];
                foreach (var entry in entries)
                {
                    var name = Normalize(entry.FullName);
                    if (!name.StartsWith(prefix, StringComparison.Ordinal))
                    {
                        continue;
                    }
                    var relative = name.Substring(prefix.Length);
                    if (relative.Length == 0 || relative.EndsWith("/", StringComparison.Ordinal))
                    {
                        continue;
                    }
                    var path = Path.GetFullPath(Path.Combine(destination, relative.Replace('/', Path.DirectorySeparatorChar)));
                    if (!path.StartsWith(destinationFull, StringComparison.Ordinal))
                    {
                        return Lang.T("install.zipPointsOutside", new { name = entry.FullName });
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    using (var source = entry.Open())
                    using (var file = new FileStream(path, FileMode.CreateNew))
                    {
                        int read;
                        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            total += read;
                            if (total > maxBytes)
                            {
                                return Lang.T("install.extractedTooLarge");
                            }
                            file.Write(buffer, 0, read);
                        }
                    }
                }
            }
            return null;
        }

        private static string Normalize(string name) => name.Replace('\\', '/');

        // フォルダ名に使わない文字。Path.GetInvalidFileNameChars は OS で違う（Linux では '/' だけ）ので、Windows で使えないものを足す
        private static readonly char[] UnsafeChars = { '/', '\\', ':', '*', '?', '"', '<', '>', '|' };

        public static bool SafeFolderName(string name)
        {
            return !string.IsNullOrWhiteSpace(name) && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && name.IndexOfAny(UnsafeChars) < 0
                   && name != "." && name != ".." && !name.EndsWith(".", StringComparison.Ordinal) && !name.EndsWith(" ", StringComparison.Ordinal);
        }

        // BeatSaver の key と曲名から、フォルダ名に使える名前を作る
        public static string FolderName(string key, string songName, string mapper)
        {
            var label = $"{key} ({songName} - {mapper})";
            var invalid = Path.GetInvalidFileNameChars().Concat(UnsafeChars).ToArray();
            var cleaned = new string(label.Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray());
            if (cleaned.Length > 80)
            {
                cleaned = cleaned.Substring(0, 80);
            }
            return cleaned.TrimEnd('.', ' ');
        }

        private static void TryDelete(string directory)
        {
            try
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, true);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    public sealed class InstallResult
    {
        private InstallResult(string path, string hash, string error)
        {
            Path = path;
            Hash = hash;
            Error = error;
        }

        public string Path { get; }
        public string Hash { get; }
        public string Error { get; }
        public bool Succeeded => Path != null;

        internal static InstallResult Ok(string path, string hash) => new InstallResult(path, hash, null);
        internal static InstallResult Fail(string error) => new InstallResult(null, null, error);
    }
}
