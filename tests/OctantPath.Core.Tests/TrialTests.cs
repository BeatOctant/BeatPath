using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OctantPath.Core.Maps;
using OctantPath.Core.Presets;
using OctantPath.Core.Settings;
using OctantPath.Core.Signals;
using OctantPath.Core.Trials;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace OctantPath.Core.Tests
{
    public class TrialPolicyTests
    {
        private static readonly string H = new string('a', 40);
        private static readonly DateTime Acquired = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

        private static TrialPolicy Policy(HashSet<string> liked = null, Dictionary<string, string> votes = null, List<SignalEvent> plays = null, IEnumerable<string> days = null) =>
            new TrialPolicy(liked ?? new HashSet<string>(), votes ?? new Dictionary<string, string>(), plays ?? new List<SignalEvent>(), days ?? new string[0], 3, 15);

        private static OwnedMap Map() => new OwnedMap { Hash = H, AcquiredAt = Acquired, State = MapStates.Trial };

        private static SignalEvent Play(string result, DateTime at) => new SignalEvent { Type = SignalTypes.Play, Hash = H, Result = result, At = at };

        [Fact]
        public void KeepsLikedEvenWhenDownvoted()
        {
            var decision = Policy(liked: new HashSet<string> { H }, votes: new Dictionary<string, string> { [H] = SignalTypes.Downvote }).Decide(Map());
            Assert.Equal(TrialDecision.Keep, decision.Decision);
        }

        [Fact]
        public void KeepsUpvotedAndDeletesDownvoted()
        {
            Assert.Equal(TrialDecision.Keep, Policy(votes: new Dictionary<string, string> { [H] = SignalTypes.Upvote }).Decide(Map()).Decision);
            Assert.Equal(TrialDecision.Delete, Policy(votes: new Dictionary<string, string> { [H] = SignalTypes.Downvote }).Decide(Map()).Decision);
        }

        [Fact]
        public void CountsOnlyClearsAndFailsAfterAcquiring()
        {
            var after = Acquired.AddHours(1);
            var plays = new List<SignalEvent>
            {
                Play(PlayResults.Clear, after), Play(PlayResults.Fail, after), Play(PlayResults.Quit, after), Play(PlayResults.Restart, after),
                Play(PlayResults.Clear, Acquired.AddDays(-1)),
            };
            Assert.Equal(TrialDecision.Hold, Policy(plays: plays).Decide(Map()).Decision);
            plays.Add(Play(PlayResults.Clear, after));
            Assert.Equal(TrialDecision.Keep, Policy(plays: plays).Decide(Map()).Decision);
        }

        [Fact]
        public void ExpiresAfterTheLaunchDays()
        {
            var days = Enumerable.Range(0, 15).Select(i => Acquired.ToLocalTime().Date.AddDays(i).ToString("yyyy-MM-dd")).ToList();
            days.Add(Acquired.ToLocalTime().Date.AddDays(-3).ToString("yyyy-MM-dd"));
            Assert.Equal(TrialDecision.Hold, Policy(days: days).Decide(Map()).Decision);
            days.Add(Acquired.ToLocalTime().Date.AddDays(40).ToString("yyyy-MM-dd"));
            Assert.Equal(TrialDecision.Delete, Policy(days: days).Decide(Map()).Decision);
        }
    }

    public class JanitorTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "octantpath-janitor-" + Guid.NewGuid().ToString("N"));
        private readonly JanitorPaths _paths;
        private readonly OctantPathSettings _settings = new OctantPathSettings { DeleteDryRun = false };
        private string _hash;

        public JanitorTests()
        {
            _paths = new JanitorPaths
            {
                Root = Path.Combine(_root, "UserData", "OctantPath"),
                Levels = Path.Combine(_root, "UserData", "OctantPath", "Levels"),
                CustomLevels = Path.Combine(_root, "CustomLevels"),
                Playlists = Path.Combine(_root, "Playlists"),
                VotedSongs = Path.Combine(_root, "UserData", "votedSongs.json"),
                VotingPlugin = Path.Combine(_root, "Plugins", "BeatSaverVoting.dll"),
            };
            Directory.CreateDirectory(_paths.Levels);
            Directory.CreateDirectory(_paths.CustomLevels);
            var source = TestMap.CreateFolder(out _hash);
            Directory.Move(source, Path.Combine(_paths.Levels, "k1 (Song - Mapper)"));
            new OwnershipLedger(_paths.Root).Add(new OwnedMap { Hash = _hash, MapKey = "k1", Folder = "k1 (Song - Mapper)", AcquiredAt = DateTime.UtcNow, State = MapStates.Trial });
        }

        public void Dispose() => Directory.Delete(_root, true);

        private string MapFolder => Path.Combine(_paths.Levels, "k1 (Song - Mapper)");
        private OwnedMap Ledger() => new OwnershipLedger(_paths.Root).Load().Maps.Single();
        private JanitorReport Run() => new Janitor(_paths, _settings, null).Run(new[] { DateTime.Now.ToString("yyyy-MM-dd") });

        private void Vote(string type) => File.WriteAllText(_paths.VotedSongs, JsonConvert.SerializeObject(new Dictionary<string, object> { [_hash] = new { hash = _hash, voteType = type } }));

        [Fact]
        public void DeletesADownvotedTrialMap()
        {
            Vote("Downvote");
            var report = Run();
            Assert.False(report.Stopped, report.StopReason);
            Assert.False(Directory.Exists(MapFolder));
            Assert.Equal(MapStates.Deleted, Ledger().State);
        }

        [Theory]
        [InlineData("trialExpiryLaunchDays", 0)]
        [InlineData("trialExpiryLaunchDays", -1)]
        [InlineData("keepMinPlays", 0)]
        [InlineData("trialMaxMegabytes", 0)]
        [InlineData("trialMaxMegabytes", -1)]
        [InlineData("trialMaxMegabytes", double.NaN)]
        [InlineData("trialMaxMegabytes", double.PositiveInfinity)]
        public void DeletesNothingWithLimitsOutOfRange(string key, double value)
        {
            switch (key)
            {
                case "trialExpiryLaunchDays": _settings.TrialExpiryLaunchDays = (int)value; break;
                case "keepMinPlays": _settings.KeepMinPlays = (int)value; break;
                default: _settings.TrialMaxMegabytes = value; break;
            }
            Vote("Downvote");
            var report = Run();
            Assert.True(report.Stopped);
            Assert.Contains(key, report.StopReason);
            Assert.True(Directory.Exists(MapFolder));
            Assert.Equal(MapStates.Trial, Ledger().State);
        }

        [Theory]
        [InlineData("{\"deleteDryRun\": true")]
        [InlineData("{\"deleteDryRun\": true, \"trialExpiryLaunchDays\": 99999999999}")]
        public void DeletesNothingWhenTheSettingsCannotBeRead(string text)
        {
            Directory.CreateDirectory(_paths.Root);
            File.WriteAllText(Path.Combine(_paths.Root, OctantPathSettings.FileName), text);
            var settings = OctantPathSettings.Load(_paths.Root, null);
            Assert.True(settings.Unreadable);
            Vote("Downvote");
            var report = new Janitor(_paths, settings, null).Run(new[] { DateTime.Now.ToString("yyyy-MM-dd") });
            Assert.True(report.Stopped);
            Assert.True(Directory.Exists(MapFolder));
            Assert.Equal(MapStates.Trial, Ledger().State);
        }

        [Fact]
        public void HugeSizeLimitDoesNotWrapAround()
        {
            _settings.TrialMaxMegabytes = 1e300;
            Assert.Equal(long.MaxValue, _settings.TrialMaxBytes);
            var report = Run();
            Assert.False(report.Stopped, report.StopReason);
            Assert.True(Directory.Exists(MapFolder));
        }

        private string AddSecondMap(string folder, DateTime acquiredAt)
        {
            var source = TestMap.CreateFolder(out _);
            File.WriteAllText(Path.Combine(source, "Expert.dat"), "{\"_notes\":[],\"other\":1}");
            var hash = MapHash.Compute(source).Hash;
            Directory.Move(source, Path.Combine(_paths.Levels, folder));
            new OwnershipLedger(_paths.Root).Add(new OwnedMap { Hash = hash, MapKey = "k2", Folder = folder, AcquiredAt = acquiredAt, State = MapStates.Trial });
            return hash;
        }

        private void WritePlayerData(string text)
        {
            _paths.PlayerData = Path.Combine(_root, "PlayerData.dat");
            File.WriteAllText(_paths.PlayerData, text);
        }

        [Fact]
        public void RecordsAKeptMapEvenWhenALaterDeletionStops()
        {
            // 先に取った k1 は Upvote で残す。後に取った k2 は Downvote で消そうとするが、脇へ移せずに止まる
            var second = AddSecondMap("k2 (Other - Mapper)", DateTime.UtcNow.AddMinutes(1));
            File.WriteAllText(_paths.VotedSongs, JsonConvert.SerializeObject(new Dictionary<string, object>
            {
                [_hash] = new { hash = _hash, voteType = "Upvote" },
                [second] = new { hash = second, voteType = "Downvote" },
            }));
            File.WriteAllText(Path.Combine(_paths.Root, Janitor.DeletingFolderName), "not a folder");
            var report = Run();
            Assert.True(report.Stopped);
            Assert.True(Directory.Exists(Path.Combine(_paths.CustomLevels, "k1 (Song - Mapper)")));
            var maps = new OwnershipLedger(_paths.Root).Load().Maps;
            Assert.Equal(MapStates.Kept, maps.Single(m => m.MapKey == "k1").State);
            Assert.Equal(MapStates.Trial, maps.Single(m => m.MapKey == "k2").State);
        }

        [Fact]
        public void KeepsAFavoriteFromThePlayerDataEvenWithoutASignal()
        {
            Vote("Downvote");
            WritePlayerData("{\"version\":\"2.0.26\",\"localPlayers\":[{\"favoritesLevelIds\":[\"custom_level_" + _hash.ToUpperInvariant() + "\"]}]}");
            var report = Run();
            Assert.False(report.Stopped, report.StopReason);
            Assert.Equal(MapStates.Kept, Ledger().State);
        }

        [Theory]
        [InlineData("{")]
        [InlineData("{\"localPlayers\":[{}]}")]
        [InlineData("{\"localPlayers\":[{\"favoritesLevelIds\":\"x\"}]}")]
        public void DeletesNothingWhenThePlayerDataCannotBeRead(string text)
        {
            Vote("Downvote");
            WritePlayerData(text);
            var report = Run();
            Assert.True(report.Stopped);
            Assert.True(Directory.Exists(MapFolder));
            Assert.Equal(MapStates.Trial, Ledger().State);
        }

        [Fact]
        public void OnlyLogsInDryRun()
        {
            _settings.DeleteDryRun = true;
            Vote("Downvote");
            var report = Run();
            Assert.Contains(report.Lines, l => l.StartsWith("消す（試し運転）"));
            Assert.True(Directory.Exists(MapFolder));
            Assert.Equal(MapStates.Trial, Ledger().State);
        }

        [Fact]
        public void MovesAKeptMapToCustomLevels()
        {
            Vote("Upvote");
            Run();
            Assert.True(Directory.Exists(Path.Combine(_paths.CustomLevels, "k1 (Song - Mapper)")));
            Assert.Equal(MapStates.Kept, Ledger().State);
        }

        [Fact]
        public void DoesNotDeleteWhenTheContentChanged()
        {
            Vote("Downvote");
            File.AppendAllText(Path.Combine(MapFolder, "Expert.dat"), " ");
            Run();
            Assert.True(Directory.Exists(MapFolder));
            Assert.Equal(MapStates.Trial, Ledger().State);
        }

        [Fact]
        public void DoesNotDeleteAMapInAnotherPlaylist()
        {
            Vote("Downvote");
            Directory.CreateDirectory(_paths.Playlists);
            File.WriteAllText(Path.Combine(_paths.Playlists, "mine.bplist"), $"{{\"playlistTitle\":\"mine\",\"songs\":[{{\"hash\":\"{_hash.ToUpperInvariant()}\"}}]}}");
            Run();
            Assert.True(Directory.Exists(MapFolder));
        }

        [Fact]
        public void IgnoresOctantPathsOwnPlaylist()
        {
            Vote("Downvote");
            Directory.CreateDirectory(_paths.Playlists);
            File.WriteAllText(Path.Combine(_paths.Playlists, "OctantPath.bplist"), $"{{\"customData\":{{\"OctantPath\":\"trial\"}},\"songs\":[{{\"hash\":\"{_hash}\"}}]}}");
            Run();
            Assert.False(Directory.Exists(MapFolder));
        }

        [Fact]
        public void DoesNotDeleteAMapInASavedMixInOctantPathsFolder()
        {
            Vote("Downvote");
            Directory.CreateDirectory(Path.Combine(_paths.Playlists, PlaylistFolder.Name));
            File.WriteAllText(Path.Combine(_paths.Playlists, PlaylistFolder.Name, "OctantPath-mix-1.bplist"), $"{{\"customData\":{{\"OctantPathMix\":true}},\"songs\":[{{\"hash\":\"{_hash}\"}}]}}");
            Run();
            Assert.True(Directory.Exists(MapFolder));
        }

        [Fact]
        public void StopsWhenAPlaylistCannotBeRead()
        {
            Vote("Downvote");
            Directory.CreateDirectory(_paths.Playlists);
            File.WriteAllText(Path.Combine(_paths.Playlists, "broken.bplist"), "{ broken");
            Assert.True(Run().Stopped);
            Assert.True(Directory.Exists(MapFolder));
        }

        [Fact]
        public void StopsWhenTheVotesCannotBeReadAndTheVotingModIsInstalled()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_paths.VotingPlugin));
            File.WriteAllText(_paths.VotingPlugin, "");
            File.WriteAllText(_paths.VotedSongs, "{ broken");
            Assert.True(Run().Stopped);
        }

        [Fact]
        public void StopsWhenTheLedgerCannotBeRead()
        {
            Vote("Downvote");
            File.WriteAllText(Path.Combine(_paths.Root, OwnershipLedger.FileName), "{ broken");
            Assert.True(Run().Stopped);
            Assert.True(Directory.Exists(MapFolder));
        }

        [Fact]
        public void StopsWhenTheSignalLogCannotBeRead()
        {
            Vote("Downvote");
            File.WriteAllText(Path.Combine(_paths.Root, SignalLog.FileName), "{\"seq\":1,\"type\":\"like\",\"hash\":\"x\",\"at\":\"2026-10-08T00:00:00Z\"}\n{broken\n{\"seq\":2,\"type\":\"like\",\"hash\":\"y\",\"at\":\"2026-10-08T00:00:00Z\"}\n");
            Assert.True(Run().Stopped);
            Assert.True(Directory.Exists(MapFolder));
        }

        [Fact]
        public void DeletesWhenTheTrialsAreOverTheSizeLimit()
        {
            _settings.TrialMaxMegabytes = 0.000001;
            var report = Run();
            Assert.False(report.Stopped, report.StopReason);
            Assert.Contains(report.Lines, l => l.StartsWith("消した") && l.Contains("合計が上限"));
            Assert.False(Directory.Exists(MapFolder));
            Assert.Equal(MapStates.Deleted, Ledger().State);
        }

        [Fact]
        public void HoldsWhenTheTrialsFitTheSizeLimit()
        {
            Run();
            Assert.True(Directory.Exists(MapFolder));
            Assert.Equal(MapStates.Trial, Ledger().State);
        }

        [Fact]
        public void KeepsAMapLikedInTheSignalLog()
        {
            Vote("Downvote");
            SignalLog.Open(_paths.Root, out _).Append(new[] { new SignalEvent { Type = SignalTypes.Like, Hash = _hash, At = DateTime.UtcNow } });
            Run();
            Assert.Equal(MapStates.Kept, Ledger().State);
        }
    }

    public class TrialFrameSettingsTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "octantpath-frames-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, true);
            }
        }

        [Fact]
        public void ReplacesTheDefaultFramesInsteadOfAppending()
        {
            Directory.CreateDirectory(_dir);
            File.WriteAllText(Path.Combine(_dir, OctantPathSettings.FileName), "{\"trialFrames\":[{\"id\":\"song\",\"count\":3,\"preset\":\"vocal\"}]}");
            var settings = OctantPathSettings.Load(_dir, null);
            Assert.Single(settings.TrialFrames);
            Assert.Equal(3, settings.TrialFrames[0].Count);
            Assert.Equal("vocal", settings.TrialFrames[0].Preset);
            Assert.Empty(settings.TrialFrames[0].Choices);
        }

        [Fact]
        public void DefaultFramesFetchNothing()
        {
            Assert.All(new OctantPathSettings().TrialFrames, f => Assert.Equal<int?>(0, f.Count));
        }

        // 枠の id は変えずに、公式の手札を名指しする（2026-10-14）
        [Fact]
        public void DefaultFramesNameTheOfficialHands()
        {
            var frames = new OctantPathSettings().TrialFrames;
            Assert.Equal(new[] { "song", "chart", "difficulty" }, frames.Select(f => f.Id));
            Assert.Equal(new[] { "song", "chart", "for_you" }, frames.Select(f => f.Preset));
            Assert.All(frames, f => Assert.Empty(f.Choices));
        }

        // 公式の手札の入力の形（OctantHarvest presets/official.json）: 似た曲・似たマップは起点が任意、おすすめは入力なし
        private static List<Preset> OfficialHands() => new List<Preset>
        {
            new Preset { Id = "song", Label = "Similar songs", Inputs = { new PresetInput { Kind = PresetInput.Seed, Optional = true } } },
            new Preset { Id = "chart", Label = "Similar maps", Inputs = { new PresetInput { Kind = PresetInput.Seed, Optional = true } } },
            new Preset { Id = "for_you", Label = "Recommended" },
        };

        [Fact]
        public void DefaultFramesNeedNoSeed()
        {
            var hands = OfficialHands();
            Assert.All(new OctantPathSettings().TrialFrames, f => Assert.Null(PresetRules.Unmet(hands.Single(p => p.Id == f.Preset), f.Choices)));
        }

        [Fact]
        public void AChoiceIsSentOnlyWhenItIsAnOption()
        {
            var genre = new Preset { Id = "genre", Label = "Genre", Inputs = { new PresetInput { Kind = PresetInput.Choice, Name = "genre", Options = { new PresetOption { Value = "jazz music" } } } } };
            Assert.Null(PresetRules.Unmet(genre, new Dictionary<string, string> { ["genre"] = "jazz music" }));
            // 一覧の選択肢にない語（DB にない語）は送らない
            Assert.NotNull(PresetRules.Unmet(genre, new Dictionary<string, string> { ["genre"] = "rock" }));
            Assert.NotNull(PresetRules.Unmet(genre, null));
        }

        [Fact]
        public void AHandThatNeedsASeedCannotFillAFrame()
        {
            var all = new Preset { Id = "all", Label = "All", Inputs = { new PresetInput { Kind = PresetInput.Seed } } };
            Assert.NotNull(PresetRules.Unmet(all, null));
            var future = new Preset { Id = "future", Label = "未来", Inputs = { new PresetInput { Kind = "range" }, new PresetInput { Kind = "other", Optional = true } } };
            Assert.NotNull(PresetRules.Unmet(future, null));
            future.Inputs.RemoveAt(0);
            Assert.Null(PresetRules.Unmet(future, null));
        }

        [Fact]
        public void TheFrameRequestNamesTheHandWithoutASeed()
        {
            var song = OfficialHands().Single(p => p.Id == "song");
            var request = JObject.Parse(JsonConvert.SerializeObject(PresetRules.Request(song, null, null, 4)));
            Assert.Equal("song", (string)request["preset"]);
            Assert.Equal(4, (int)request["count"]);
            Assert.Null(request["seed"]);
            Assert.Null(request["weights"]);
            Assert.Null(request["genre"]);
            Assert.Null(request["params"]);
        }
    }
}

namespace OctantPath.Core.Tests
{
    public class SettingsFileTests : System.IDisposable
    {
        private readonly string _dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "octantpath-settingsfile-" + System.Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            if (System.IO.Directory.Exists(_dir))
            {
                System.IO.Directory.Delete(_dir, true);
            }
        }

        [Xunit.Fact]
        public void HasOnlyTheSettingKeys()
        {
            var json = Newtonsoft.Json.Linq.JObject.FromObject(new OctantPath.Core.Settings.OctantPathSettings());
            Xunit.Assert.All(json.Properties(), p => Xunit.Assert.True(char.IsLower(p.Name[0]), p.Name));
        }

        [Xunit.Fact]
        public void KeepsTheDeletionChoiceEvenWhenItIsTheDefault()
        {
            System.IO.Directory.CreateDirectory(_dir);
            var defaults = OctantPath.Core.Settings.OctantPathSettings.DefaultValues();
            OctantPath.Core.Settings.OctantPathSettings.SaveValue(_dir, "deleteDryRun", defaults["deleteDryRun"]);
            OctantPath.Core.Settings.OctantPathSettings.SaveValue(_dir, "panelOpacity", defaults["panelOpacity"]);
            var saved = Newtonsoft.Json.Linq.JObject.Parse(System.IO.File.ReadAllText(System.IO.Path.Combine(_dir, "settings.json")));
            Xunit.Assert.NotNull(saved["deleteDryRun"]);
            Xunit.Assert.Null(saved["panelOpacity"]);
        }

        [Xunit.Fact]
        public void ReadableSettingsAreNotMarkedUnreadable()
        {
            Xunit.Assert.False(OctantPath.Core.Settings.OctantPathSettings.Load(_dir, null).Unreadable);
            Xunit.Assert.Null(new OctantPath.Core.Settings.OctantPathSettings().DeletionProblem());
        }

        [Xunit.Fact]
        public void LoadDoesNotCreateTheFile()
        {
            OctantPath.Core.Settings.OctantPathSettings.Load(_dir, null);
            Xunit.Assert.False(System.IO.File.Exists(System.IO.Path.Combine(_dir, "settings.json")));
        }
    }
}
