using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using OctantPath.Core.Connection;
using OctantPath.Core.Presets;
using OctantPath.Core.Settings;
using Xunit;

namespace OctantPath.Core.Tests
{
    // 動いている本物の OctantCompass とつなぐ。OCTANTPATH_TEST_PAIR_CODE に OctantCompass の合言葉（`octantcompass pair code`）と、
    // OCTANTPATH_TEST_ADDRESS につなぐ先を入れると動く。ペアリングのたびに環境が 1 つ増えるので、つなぐ先はテスト用の
    // データフォルダで起動した OctantCompass にする（README の「テスト」）。普段の OctantCompass（同じ PC の既定のポート）に
    // 溜まらないよう、つなぐ先を省いたときは失敗にする
    public class RealOctantCompassTests
    {
        [Fact]
        public async Task PairsAndReadsHealth()
        {
            var connection = await Paired();
            if (connection == null)
            {
                return;
            }
            var (store, paired) = connection.Value;
            var first = await paired.RefreshAsync(CancellationToken.None);
            Assert.True(first.State == ConnectionState.Ready || first.State == ConnectionState.Degraded || first.State == ConnectionState.Loading, first.ToString());
            Assert.False(string.IsNullOrEmpty(store.Info.Token));
            Assert.NotNull(first.HostInstance);
            Assert.NotNull(first.Signals);

            var token = store.Info.Token;
            var second = await paired.RefreshAsync(CancellationToken.None);
            Assert.Equal(first.State, second.State);
            Assert.Equal(token, store.Info.Token);
        }

        // 一覧のプリセットを全部順に要求する（ゲームでボタンを押す代わり）
        [Fact]
        public async Task RequestsEveryPresetInTheList()
        {
            var paired = await Paired();
            if (paired == null)
            {
                return;
            }
            var connection = paired.Value.Connection;
            var status = await connection.RefreshAsync(CancellationToken.None);
            Assert.Equal(ConnectionState.Ready, status.State);

            var list = await connection.GetPresetsAsync(CancellationToken.None);
            Assert.Equal(HostResultKind.Ok, list.Kind);
            Assert.Equal(status.PresetsRevision, list.Body.Revision);
            var shown = PresetRules.Shown(list.Body.Presets, true);
            Console.WriteLine($"presets {list.Body.Revision}: {string.Join(", ", shown.Select(p => p.Id + "=" + p.Label))}");

            // 起点は OCTANTPATH_TEST_SEED（hash/characteristic/difficulty）。なければ「挑戦」の結果の 1 曲目
            Recommendations.ChartRef seed;
            var seedText = Environment.GetEnvironmentVariable("OCTANTPATH_TEST_SEED");
            if (!string.IsNullOrEmpty(seedText))
            {
                var parts = seedText.Split('/');
                seed = new Recommendations.ChartRef { Hash = parts[0], Characteristic = parts[1], Difficulty = parts[2] };
            }
            else
            {
                var challenge = await connection.RecommendAsync(PresetRules.Request(shown.Single(p => p.Id == "challenge"), null, null, 5), CancellationToken.None);
                Assert.Equal(HostResultKind.Ok, challenge.Kind);
                var top = challenge.Body.Candidates.First();
                seed = new Recommendations.ChartRef { Hash = top.Hash, Characteristic = top.Characteristic, Difficulty = top.Difficulty };
            }

            foreach (var preset in shown)
            {
                var choices = PresetRules.Choices(preset).ToDictionary(i => i.Name, i => i.Options[0].Value);
                var result = await connection.RecommendAsync(PresetRules.Request(preset, seed, choices, 5), CancellationToken.None);
                Assert.True(result.Kind == HostResultKind.Ok, $"{preset.Id}: {result.StatusCode} {result.Error?.Code} {result.Detail}");
                Console.WriteLine($"{preset.Id} {string.Join(",", choices.Values)}: {string.Join(" ", result.Body.Candidates.Select(c => c.Hash.Substring(0, 6) + "/" + c.Difficulty))} aims={string.Join(",", result.Body.Aims?.Values ?? new Dictionary<string, string>().Values)}");
            }

            var unknown = await connection.RecommendAsync(new Recommendations.RecommendationRequest { Preset = "no-such-preset", Count = 1, Difficulty = null }, CancellationToken.None);
            Assert.True(unknown.IsUnknownPreset, $"{unknown.StatusCode} {unknown.Error?.Code}");
        }

        // ゲームのフォルダを複製すると、トークンも signal の正本も一緒に写る。複製した側が同じ番号で違う signal を
        // 送ったら、OctantCompass が断り、元の側の記録は混ざらない
        [Fact]
        public async Task RefusesACopiedRecordUnderTheSameToken()
        {
            var paired = await Paired();
            if (paired == null)
            {
                return;
            }
            var connection = paired.Value.Connection;
            var root = Path.Combine(Path.GetTempPath(), "octantpath-copied-" + Guid.NewGuid().ToString("N"));
            var original = Path.Combine(root, "original");
            var copied = Path.Combine(root, "copied");
            Directory.CreateDirectory(original);
            Directory.CreateDirectory(copied);
            try
            {
                var log = Signals.SignalLog.Open(original, out _);
                log.Append(new[] { Like("a"), Like("b") });
                Assert.True((await connection.RefreshAsync(CancellationToken.None)).HostInstance != null);
                Assert.True((await Signals.SignalSync.SyncAsync(log, connection, null, CancellationToken.None)).Succeeded);
                File.Copy(Path.Combine(original, Signals.SignalLog.FileName), Path.Combine(copied, Signals.SignalLog.FileName));

                log.Append(new[] { Like("c") });
                var sent = await Signals.SignalSync.SyncAsync(log, connection, 2, CancellationToken.None);
                Assert.True(sent.Succeeded, sent.Error);
                Assert.Equal(3, sent.Sent);
                Assert.Equal(3, (await connection.RefreshAsync(CancellationToken.None)).Signals);

                // 複製した側がつなぐと、その正本に置き換わる
                var copy = Signals.SignalLog.Open(copied, out _);
                copy.Append(new[] { Like("x"), Like("y") });
                var replaced = await Signals.SignalSync.SyncAsync(copy, connection, null, CancellationToken.None);
                Assert.True(replaced.Succeeded, replaced.Error);
                Assert.Equal(4, (await connection.RefreshAsync(CancellationToken.None)).Signals);

                // 元の側は、件数が合わないのに気づいて丸ごと送り直す
                log.Append(new[] { Like("d") });
                Assert.Equal(4, (await Signals.SignalSync.SyncAsync(log, connection, null, CancellationToken.None)).Sent);
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        private static Signals.SignalEvent Like(string hash) =>
            new Signals.SignalEvent { Type = Signals.SignalTypes.Like, Hash = new string(hash[0], 40), At = DateTime.UtcNow };

        // 合言葉は 1 回しか使えないので、同じ実行の中ではペアリングを 1 度だけにする
        private static (MemoryStore Store, HostConnection Connection)? _paired;

        private static async Task<(MemoryStore Store, HostConnection Connection)?> Paired()
        {
            var code = Environment.GetEnvironmentVariable("OCTANTPATH_TEST_PAIR_CODE");
            var address = Environment.GetEnvironmentVariable("OCTANTPATH_TEST_ADDRESS");
            if (string.IsNullOrEmpty(code))
            {
                return null;
            }
            Assert.False(string.IsNullOrEmpty(address), "OCTANTPATH_TEST_ADDRESS を入れてください（テスト用のデータフォルダで起動した OctantCompass。README の「テスト」）");
            if (_paired == null)
            {
                var store = new MemoryStore();
                var settings = new OctantPathSettings();
                var connection = new HostConnection(new HostClient(new HttpClient()), store, () => settings, "OctantPath tests");
                string host = null;
                int? port = null;
                Assert.True(ConnectionInfo.TryParseAddress(address, out host, out port), address);
                var status = await connection.PairAsync(code, host, port, CancellationToken.None);
                Assert.False(string.IsNullOrEmpty(store.Info.Token), status.ToString());
                _paired = (store, connection);
            }
            return _paired;
        }
    }
}
