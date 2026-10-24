using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using OctantPath.Core.Languages;
using OctantPath.Installers;
using OctantPath.Maps;
using IPA.Utilities;
using SongCore.Data;
using IPA;
using SiraUtil.Zenject;
using IPALogger = IPA.Logging.Logger;

namespace OctantPath
{
    [Plugin(RuntimeOptions.SingleStartInit)]
    public class Plugin
    {
        internal static IPALogger Log { get; private set; }

        internal static string Version { get; private set; } = "0.0.0";

        // BeatSaver の API・CDN への要求に付ける User-Agent（external.md: どの部品か分かるもの）。共有の HttpClient と試聴が使う
        internal static string UserAgent => $"OctantPath/{Version}";

        [Init]
        public void Init(IPALogger logger, Zenjector zenjector)
        {
            Log = logger;
            Version = typeof(Plugin).Assembly.GetName().Version.ToString(3);

            // 専用フォルダを SongCore の別のパックとして登録する。SongCore が曲を読む前に行う
            var folders = OctantPathFolders.InUserData();
            var settings = Core.Settings.OctantPathSettings.Load(folders.Root, Log.Warn);
            UseLanguage(folders, settings);
            SongCore.Collections.AddSeparateSongFolder(LevelLookup.PackName, folders.Levels, FolderLevelPack.NewPack);

            // 起動日を記録し、SongCore が曲を読む前にお試し中のマップを残すか消す（U2 は 2026-10-08 決定 で起動直後だけ）
            RunJanitor(folders, settings);
            zenjector.UseLogger(logger);
            zenjector.Install<OctantPathAppInstaller>(Location.App);
            zenjector.Install<OctantPathMenuInstaller>(Location.Menu);
            Log.Info("OctantPath initialized.");
        }

        // 画面の言語を決める（設定の language。auto はゲームの言語、OS の言語、英語の順）。起動のときと、メニューを作り直すとき（OctantPathMenuInstaller）に呼ぶ。
        // UserData/OctantPath/lang/<言語>.json があれば同梱の文より優先する
        internal static void UseLanguage(OctantPathFolders folders, Core.Settings.OctantPathSettings settings)
        {
            try
            {
                var langFolder = folders.Languages;
                var game = GameLanguage();
                var language = Lang.Resolve(settings.Language, game, CultureInfo.CurrentUICulture, Lang.Available(langFolder));
                Lang.Use(language, langFolder, Log.Warn);
                Log.Info($"[lang] {language} (setting {settings.Language}, game {game ?? "unknown"}, OS {CultureInfo.CurrentUICulture.Name})");
            }
            catch (System.Exception ex)
            {
                Log.Error($"[lang] {ex}");
            }
        }

        // ゲーム本体の言語（Polyglot）。取れなければ null。型は版で変わり得るので、名前で探す（推測: BGLib.Polyglot.Localization.Instance.SelectedLanguage）
        internal static string GameLanguage()
        {
            try
            {
                var type = System.Type.GetType("BGLib.Polyglot.Localization, BGLib.Polyglot");
                var instance = type?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null, null);
                var selected = instance?.GetType().GetProperty("SelectedLanguage", BindingFlags.Public | BindingFlags.Instance)?.GetValue(instance, null)?.ToString();
                switch (selected)
                {
                    case "English": return "en";
                    case "Japanese": return "ja";
                    case "Korean": return "ko";
                    case "French": return "fr";
                    case "German": return "de";
                    case "Spanish": return "es";
                    case "Simplified_Chinese": return "zh-Hans";
                    case "Traditional_Chinese": return "zh-Hant";
                    default: return null;
                }
            }
            catch (System.Exception)
            {
                return null;
            }
        }

        private static void RunJanitor(OctantPathFolders folders, Core.Settings.OctantPathSettings settings)
        {
            try
            {
                var state = Core.Signals.ImportState.Load(folders.Root, out var readable);
                if (!readable)
                {
                    Log.Warn("[janitor] state.json could not be read; nothing is deleted");
                    return;
                }
                // 暦は固定する（タイの仏暦などで書くと、読むときの暦と年がずれて全部の起動日が「取った後」に数えられる）
                var today = System.DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                if (!state.LaunchDays.Contains(today))
                {
                    state.LaunchDays.Add(today);
                    state.Save(folders.Root);
                }

                var install = UnityGame.InstallPath;
                var janitor = new Core.Trials.Janitor(new Core.Trials.JanitorPaths
                {
                    Root = folders.Root,
                    Levels = folders.Levels,
                    CustomLevels = Path.Combine(install, "Beat Saber_Data", "CustomLevels"),
                    Playlists = Path.Combine(install, "Playlists"),
                    VotedSongs = Path.Combine(UnityGame.UserDataPath, "votedSongs.json"),
                    VotingPlugin = Path.Combine(install, "Plugins", "BeatSaverVoting.dll"),
                    PlayerData = Path.Combine(UnityEngine.Application.persistentDataPath, "PlayerData.dat"),
                }, settings, Log.Warn);
                // 今日より後の起動日（時計が進んでいた日や、ほかの暦で書かれた日）は数えない
                var report = janitor.Run(state.LaunchDays.Where(d => string.CompareOrdinal(d, today) <= 0));
                foreach (var line in report.Lines)
                {
                    Log.Info($"[janitor] {line}");
                }
                if (report.Stopped)
                {
                    Log.Warn($"[janitor] stopped: {report.StopReason}");
                }
                Log.Info($"[janitor] done ({(report.DryRun ? "dry run" : "live")}, {report.Lines.Count} decisions, {state.LaunchDays.Count} launch days)");
            }
            catch (System.Exception ex)
            {
                // 入れ替えに失敗しても、ゲームの起動は妨げない
                Log.Error($"[janitor] {ex}");
            }
        }
    }
}
