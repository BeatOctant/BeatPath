using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using OctantPath.Core.Languages;
using OctantPath.Core.Presets;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace OctantPath.Core.Tests
{
    // 言語を切り替えるテストはほかのテストの文を変えるので、ほかと並べて走らせない
    [CollectionDefinition("Lang", DisableParallelization = true)]
    public sealed class LangCollection
    {
    }

    [Collection("Lang")]
    public sealed class LangTests : IDisposable
    {
        public void Dispose() => Lang.Use(Lang.Default, null);

        [Fact]
        public void NamesEachLanguageInItself()
        {
            Assert.Equal("日本語", Lang.NameOf("ja", null));
            Assert.Equal("English", Lang.NameOf("en", null));
            Assert.Equal("xx", Lang.NameOf("xx", null));
        }

        [Fact]
        public void BundlesEnglishAndJapaneseWithTheSameKeys()
        {
            Assert.Contains("en", Lang.Bundled);
            Assert.Contains("ja", Lang.Bundled);
            var en = Lang.Bundle("en");
            var ja = Lang.Bundle("ja");
            Assert.NotEmpty(en);
            Assert.Empty(en.Keys.Except(ja.Keys));
            Assert.Empty(ja.Keys.Except(en.Keys));
            foreach (var key in en.Keys)
            {
                // 差し込みの名前は、言語が違っても同じ
                Assert.Equal(Placeholders(en[key]), Placeholders(ja[key]));
            }
        }

        [Fact]
        public void EveryKeyTheCodeUsesIsInEnglish()
        {
            var src = Path.Combine(OctantSpecsExamples.RepositoryRoot(), "src");
            var used = new HashSet<string>();
            foreach (var file in Directory.GetFiles(src, "*.*", SearchOption.AllDirectories).Where(f => f.EndsWith(".cs") || f.EndsWith(".bsml")))
            {
                var text = File.ReadAllText(file);
                foreach (Match m in Regex.Matches(text, @"(?<!Desktop)Lang\.T\(""([A-Za-z0-9_.]+)""|\{\{([A-Za-z0-9_.]+)\}\}|Lang\.T\([^)]*\?\s*""([A-Za-z0-9_.]+)""\s*:\s*""([A-Za-z0-9_.]+)"""))
                {
                    foreach (var g in m.Groups.Cast<Group>().Skip(1).Where(g => g.Success))
                    {
                        used.Add(g.Value);
                    }
                }
            }
            Assert.NotEmpty(used);
            Assert.Empty(used.Except(Lang.Bundle("en").Keys).OrderBy(k => k));
        }

        // Desktop の文は Desktop の言語ファイル（src/OctantPath.Desktop/Languages）にある。サーバーの DesktopLang.T と、
        // 画面の T・H・data-t* が使うキーが英語と日本語の両方にあり、二つのファイルのキーと差し込みの名前がそろっていること
        [Fact]
        public void DesktopKeysAreInBothDesktopLanguages()
        {
            var desktop = Path.Combine(OctantSpecsExamples.RepositoryRoot(), "src", "OctantPath.Desktop");
            Dictionary<string, string> Read(string language) =>
                JObject.Parse(File.ReadAllText(Path.Combine(desktop, "Languages", language + ".json"))).Properties()
                    .ToDictionary(p => p.Name, p => (string)p.Value);
            var en = Read("en");
            var ja = Read("ja");
            Assert.Empty(en.Keys.Except(ja.Keys));
            Assert.Empty(ja.Keys.Except(en.Keys));
            foreach (var key in en.Keys)
            {
                Assert.Equal(Placeholders(en[key]), Placeholders(ja[key]));
            }

            var used = new HashSet<string>();
            foreach (var file in Directory.GetFiles(desktop, "*.cs"))
            {
                foreach (Match m in Regex.Matches(File.ReadAllText(file), @"DesktopLang\.T\(""([A-Za-z0-9_.]+)"""))
                {
                    used.Add(m.Groups[1].Value);
                }
            }
            var page = File.ReadAllText(Path.Combine(desktop, "wwwroot", "index.html"));
            foreach (Match m in Regex.Matches(page, @"\b[TH]\('([A-Za-z0-9_.]+)'|data-t(?:-title|-ph)?=""([A-Za-z0-9_.]+)"""))
            {
                used.Add(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value);
            }
            Assert.NotEmpty(used);
            Assert.Empty(used.Except(en.Keys).OrderBy(k => k));
        }

        [Fact]
        public void FillsPlaceholdersAndFallsBackToEnglishThenTheKey()
        {
            Lang.Use("ja", null);
            Assert.Equal("接続済", Lang.T("connection.ready"));
            Assert.Equal("OctantCompass と OctantPath（1.2）の版が合いません", Lang.T("connection.apiMismatch", new { ours = "1.2" }));
            Assert.Equal("no.such.key", Lang.T("no.such.key"));
            Lang.Use("en", null);
            Assert.Equal("Connected", Lang.T("connection.ready"));
        }

        [Fact]
        public void AFileInTheFolderOverridesTheBundledTextsAndAddsALanguage()
        {
            var dir = Path.Combine(Path.GetTempPath(), "octantpath-lang-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                File.WriteAllText(Path.Combine(dir, "ja.json"), "{\"connection.ready\": \"つながった\"}");
                File.WriteAllText(Path.Combine(dir, "de.json"), "{\"_language\": \"Deutsch\", \"connection.ready\": \"Verbunden\"}");
                File.WriteAllText(Path.Combine(dir, "fr.json"), "{ not json");
                Assert.Equal(new[] { "de", "en", "fr", "ja" }, Lang.Available(dir));

                Lang.Use("ja", dir);
                Assert.Equal("つながった", Lang.T("connection.ready"));
                Assert.Equal("接続中", Lang.T("connection.connecting"));

                Lang.Use("de", dir);
                Assert.Equal("Verbunden", Lang.T("connection.ready"));
                Assert.Equal("Connecting", Lang.T("connection.connecting"));

                var warnings = new List<string>();
                Lang.Use("fr", dir, warnings.Add);
                Assert.Single(warnings);
                Assert.Equal("Connected", Lang.T("connection.ready"));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Theory]
        [InlineData("auto", "ja", "en-US", "ja")]
        [InlineData("auto", "en", "ja-JP", "ja")]
        [InlineData("auto", null, "ja-JP", "ja")]
        [InlineData("auto", null, "de-DE", "en")]
        [InlineData("auto", "ko", "ja-JP", "ja")]
        [InlineData("", null, "en-GB", "en")]
        [InlineData("ja", "en", "en-US", "ja")]
        [InlineData("EN", "ja", "ja-JP", "en")]
        [InlineData("xx", "ja", "ja-JP", "en")]
        public void ResolvesTheLanguage(string setting, string game, string os, string expected)
        {
            Assert.Equal(expected, Lang.Resolve(setting, game, new CultureInfo(os), new[] { "en", "ja" }));
        }

        [Fact]
        public void PresetTextsMayBeOnePerLanguage()
        {
            const string json = "{\"revision\":\"r\",\"presets\":[" +
                "{\"id\":\"song\",\"group\":{\"en\":\"Song\",\"ja\":\"曲\"},\"label\":{\"en\":\"Similar songs\",\"ja\":\"似た曲\"},\"hint\":\"音が近い\"," +
                "\"inputs\":[{\"kind\":\"choice\",\"name\":\"genre\",\"label\":{\"ja\":\"ジャンル\"},\"options\":[{\"value\":\"rock\",\"label\":{\"en\":\"Rock\",\"ja\":\"ロック\"}}]}]}]}";
            var english = JsonConvert.DeserializeObject<PresetsResponse>(json).Presets[0];
            Assert.Equal("Similar songs", english.Label);
            Assert.Equal("Song", english.Group);
            Assert.Equal("音が近い", english.Hint);
            Assert.Equal("ジャンル", english.Inputs[0].Label);
            Assert.Equal("Rock", english.Inputs[0].Options[0].Label);

            Lang.Use("ja", null);
            var japanese = JsonConvert.DeserializeObject<PresetsResponse>(json).Presets[0];
            Assert.Equal("似た曲", japanese.Label);
            Assert.Equal("ロック", japanese.Inputs[0].Options[0].Label);
            Assert.Equal("似た曲", JObject.Parse(JsonConvert.SerializeObject(japanese))["label"]);
        }

        [Fact]
        public void FillEscapesTextForBsmlAttributes()
        {
            Assert.Equal("<button hover-hint='Swap or drop songs that don&apos;t fit; if it looks good, “Create” downloads them and opens the playlist. Once a song is downloaded, its ↓ turns into a play button'/>",
                Lang.Fill("<button hover-hint='{{draft.hint}}'/>"));
        }

        [Fact]
        public void InterpolateFillsKnownNamesAndLeavesTheRest()
        {
            Assert.Equal("1.5 MB of {total}", Lang.Interpolate("{size} MB of {total}", new { size = 1.5 }));
            Assert.Equal("{size}", Lang.Interpolate("{size}", null));
        }

        [Fact]
        public void TrialFramesWithoutANameUseTheLanguageFile()
        {
            // 名前は枠の id の trialFrame.<id> で、名指しした手札の名前ではない（難易度の枠は for_you の「おすすめ」にならない）
            var frames = new Core.Settings.OctantPathSettings().TrialFrames;
            Assert.Equal(new[] { "Similar songs", "Similar maps", "Right difficulty" }, frames.Select(f => f.DisplayName));
            Lang.Use("ja", null);
            Assert.Equal(new[] { "曲が似ている", "譜面が似ている", "難易度が合う" }, frames.Select(f => f.DisplayName));
            Assert.Equal("mine", new Core.Settings.TrialFrameSettings { Id = "mine" }.DisplayName);
            Assert.Equal("私の枠", new Core.Settings.TrialFrameSettings { Id = "song", Name = "私の枠" }.DisplayName);
        }

        private static string[] Placeholders(string text) =>
            Regex.Matches(text, @"\{(\w+)\}").Cast<Match>().Select(m => m.Groups[1].Value).OrderBy(n => n).ToArray();
    }
}
