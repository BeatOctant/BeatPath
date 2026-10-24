using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using OctantPath.Installers;
using OctantPath.Maps;
using BeatSaberMarkupLanguage.Attributes;
using BeatSaberMarkupLanguage.Components;
using BeatSaberMarkupLanguage.Settings;
using Zenject;
using OctantPath.Core.Languages;

namespace OctantPath.UI
{
    // MOD 設定画面（Mod Settings）。値の正本は UserData/OctantPath/settings.json で、変えたらその 1 項目だけをすぐ書く（既定値に戻せば項目を消す）
    internal sealed class SettingsMenu : NotifiableBase, IInitializable, IDisposable
    {
        private readonly BSMLSettings _bsmlSettings;
        private readonly SettingsHolder _settings;
        private readonly OctantPathFolders _folders;
        private readonly Signals.SignalService _signals;
        private string _exportResult = "";

        // 下見の曲数と前もって取る曲数の上限（推測で決めた値。下見は 3 倍を受け取る）。SettingsView.bsml の max と同じにする
        private const int MaxDraftCount = Core.Mix.MixDraftRules.MaxSize;
        private const int MaxPrefetchCount = 10;
        // お試しの合計の上限（GB）の幅。SettingsView.bsml の min・max と同じにする
        private const int MinTrialGigabytes = 1;
        private const int MaxTrialGigabytes = 100;
        // お試しの期限（起動日）の幅（上限は推測で決めた値）。SettingsView.bsml の min・max と同じにする
        private const int MinTrialExpiryDays = 1;
        private const int MaxTrialExpiryDays = 60;

        public SettingsMenu(BSMLSettings bsmlSettings, SettingsHolder settings, OctantPathFolders folders, Signals.SignalService signals)
        {
            _signals = signals;
            _bsmlSettings = bsmlSettings;
            _settings = settings;
            _folders = folders;
        }

        public void Initialize()
        {
            _bsmlSettings.AddSettingsMenu("OctantPath", "OctantPath.UI.SettingsView.bsml", this);
        }

        public void Dispose()
        {
            _bsmlSettings.RemoveSettingsMenu(this);
        }

        // 設定ページの中身（SettingsView.bsml の一番外）。開いている間は浮かぶ板も出す（RecommendationTab.Tick）
        [UIObject("settings-root")]
        private UnityEngine.GameObject _root;

        public bool IsOpen => _root != null && _root.activeInHierarchy;

        // 設定ページを出しているゲームの画面。浮かぶ板をまだ作っていないとき、この上に置く
        public HMUI.Screen Screen => _root != null ? _root.GetComponentInParent<HMUI.Screen>() : null;

        [UIValue("language-text")] public string LanguageText => Lang.T("settings.language");
        [UIValue("language-hint")] public string LanguageHint => Lang.T("settings.languageHint");
        [UIValue("mix-draft-count-text")] public string MixDraftCountText => Lang.T("settings.mixDraftCount");
        [UIValue("mix-draft-count-hint")] public string MixDraftCountHint => Lang.T("settings.mixDraftCountHint");
        [UIValue("mix-prefetch-count-text")] public string MixPrefetchCountText => Lang.T("settings.mixPrefetchCount");
        [UIValue("mix-prefetch-count-hint")] public string MixPrefetchCountHint => Lang.T("settings.mixPrefetchCountHint");

        // 選べる言語: 自動と、同梱のものと lang/ に置いたもの。変えた言語は、設定の OK（メニューの作り直し）か次の起動から使う（画面の文は組み立てるときに決まるため）
        [UIValue("languages")]
        public List<object> LanguageOptions => new[] { Lang.Auto }.Concat(Lang.Available(_folders.Languages)).Cast<object>().ToList();

        [UIValue("language")]
        public string Language
        {
            get
            {
                var setting = _settings.Current.Language;
                var match = LanguageOptions.Cast<string>().FirstOrDefault(l => string.Equals(l, setting, StringComparison.OrdinalIgnoreCase));
                return match ?? Lang.Auto;
            }
            set => _settings.Save("language", value);
        }

        [UIAction("language-format")]
        private string LanguageFormat(string value)
        {
            if (!string.Equals(value, Lang.Auto, StringComparison.OrdinalIgnoreCase))
            {
                return Lang.NameOf(value, _folders.Languages);
            }
            var resolved = Lang.Resolve(Lang.Auto, Plugin.GameLanguage(), CultureInfo.CurrentUICulture, Lang.Available(_folders.Languages));
            return Lang.T("settings.languageAuto", new { language = Lang.NameOf(resolved, _folders.Languages) });
        }

        [UIValue("mix-draft-count")]
        public int MixDraftCount
        {
            get => Math.Max(1, Math.Min(MaxDraftCount, _settings.Current.MixDraftCount));
            set => _settings.Save("mixDraftCount", value);
        }

        [UIValue("mix-prefetch-count")]
        public int MixPrefetchCount
        {
            get => Math.Max(0, Math.Min(MaxPrefetchCount, _settings.Current.MixPrefetchCount));
            set => _settings.Save("mixPrefetchCount", value);
        }

        [UIValue("panel-scale-text")] public string PanelScaleText => Lang.T("settings.panelScale");
        [UIValue("panel-scale-hint")] public string PanelScaleHint => Lang.T("settings.panelScaleHint");
        [UIValue("panel-opacity-text")] public string PanelOpacityText => Lang.T("settings.panelOpacity");
        [UIValue("panel-opacity-hint")] public string PanelOpacityHint => Lang.T("settings.panelOpacityHint");
        [UIValue("auto-download-text")] public string AutoDownloadText => Lang.T("settings.autoDownload");
        [UIValue("auto-download-hint")] public string AutoDownloadHint => Lang.T("settings.autoDownloadHint");
        [UIValue("auto-delete-text")] public string AutoDeleteText => Lang.T("settings.autoDelete");
        [UIValue("auto-delete-hint")] public string AutoDeleteHint => Lang.T("settings.autoDeleteHint");

        [UIValue("panel-scale-min")] public string PanelScaleMin => PanelLook.MinScale.ToString(CultureInfo.InvariantCulture);
        [UIValue("panel-scale-max")] public string PanelScaleMax => PanelLook.MaxScale.ToString(CultureInfo.InvariantCulture);

        [UIValue("panel-scale")]
        public float PanelScale
        {
            get => (float)_settings.Current.RecommendationPanelScale;
            set => _settings.Save("recommendationPanelScale", Math.Round(value, 2));
        }

        [UIValue("panel-opacity-min")] public string PanelOpacityMin => PanelLook.MinOpacity.ToString(CultureInfo.InvariantCulture);

        [UIValue("panel-opacity")]
        public float PanelOpacity
        {
            get => PanelLook.Opacity(_settings.Current.PanelOpacity);
            set => _settings.Save("panelOpacity", Math.Round(value, 2));
        }

        [UIValue("auto-download")]
        public bool AutoDownload
        {
            get => _settings.Current.AutoDownload;
            set => _settings.Save("autoDownload", value);
        }

        // 画面では「自動で消す」と表す（入 = 試し運転を切る）。設定のファイルの項目は deleteDryRun のまま
        [UIValue("auto-delete")]
        public bool AutoDelete
        {
            get => !_settings.Current.DeleteDryRun;
            set => _settings.Save("deleteDryRun", !value);
        }

        [UIValue("trial-expiry-text")] public string TrialExpiryText => Lang.T("settings.trialExpiry");
        [UIValue("trial-expiry-hint")] public string TrialExpiryHint => Lang.T("settings.trialExpiryHint");

        [UIValue("trial-expiry")]
        public int TrialExpiryDays
        {
            get => Math.Max(MinTrialExpiryDays, Math.Min(MaxTrialExpiryDays, _settings.Current.TrialExpiryLaunchDays));
            set => _settings.Save("trialExpiryLaunchDays", value);
        }

        [UIAction("launch-days-format")]
        private string LaunchDaysFormat(object value) => Lang.T("settings.launchDays", new { value = Convert.ToInt32(value, CultureInfo.InvariantCulture) });

        [UIValue("trial-max-text")] public string TrialMaxText => Lang.T("settings.trialMax");
        [UIValue("trial-max-hint")] public string TrialMaxHint => Lang.T("settings.trialMaxHint");

        // 画面は GB で、設定のファイルの項目は trialMaxMegabytes（MB）のまま
        [UIValue("trial-max")]
        public int TrialMaxGigabytes
        {
            get => (int)Math.Max(MinTrialGigabytes, Math.Min(MaxTrialGigabytes, Math.Round(_settings.Current.TrialMaxMegabytes / 1024)));
            set => _settings.Save("trialMaxMegabytes", value * 1024);
        }

        [UIAction("gigabytes-format")]
        private string GigabytesFormat(object value) => Lang.T("settings.gigabytes", new { value = Convert.ToInt32(value, CultureInfo.InvariantCulture) });

        [UIValue("export-text")] public string ExportText => Lang.T("settings.exportSummary");
        [UIValue("export-hint")] public string ExportHint => Lang.T("settings.exportSummaryHint");

        [UIValue("export-result")]
        public string ExportResult
        {
            get => _exportResult;
            private set
            {
                _exportResult = value;
                NotifyPropertyChanged();
            }
        }

        // 推薦のまとめを書き出す。外へは送らない（送るかは本人が決める）
        [UIAction("export-click")]
        private void ExportClick()
        {
            try
            {
                var records = Core.Recommendations.RecommendationRecords.Read(_folders.Root);
                var text = Core.Recommendations.FeedbackSummary.Build(records, _signals.Events, Plugin.Version, DateTime.UtcNow);
                var folder = System.IO.Path.Combine(_folders.Root, "summaries");
                System.IO.Directory.CreateDirectory(folder);
                var name = $"summary-{DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.md";
                System.IO.File.WriteAllText(System.IO.Path.Combine(folder, name), text);
                Plugin.Log.Info($"[summary] wrote {name} ({records.Count} requests, {_signals.Events.Count} signal events)");
                ExportResult = Lang.T("settings.exportSummaryDone", new { path = "UserData/OctantPath/summaries/" + name });
            }
            catch (Exception ex) when (ex is System.IO.IOException || ex is UnauthorizedAccessException)
            {
                Plugin.Log.Warn($"[summary] could not write: {ex.Message}");
                ExportResult = Lang.T("settings.exportSummaryFailed", new { error = ex.Message });
            }
        }

        [UIAction("scale-format")]
        private string ScaleFormat(float value) => Lang.T("settings.scale", new { value = value.ToString("0.0", CultureInfo.InvariantCulture) });

        [UIAction("percent-format")]
        private string PercentFormat(float value) => Lang.T("settings.percent", new { value = Math.Round(value * 100).ToString(CultureInfo.InvariantCulture) });
    }
}
