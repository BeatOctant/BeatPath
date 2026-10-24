using System;
using System.Threading;
using OctantPath.Connection;
using OctantPath.Core.Connection;
using OctantPath.Core.Profile;
using BeatSaberMarkupLanguage.Attributes;
using BeatSaberMarkupLanguage.ViewControllers;
using Zenject;
using OctantPath.Core.Languages;

namespace OctantPath.UI
{
    // OctantCompass が推定した実力と好みの要約（/v1/profile）。表示だけに使う
    [ViewDefinition("OctantPath.UI.ProfileView.bsml")]
    [HotReload(RelativePathToLayout = @"ProfileView.bsml")]
    internal class ProfileViewController : BSMLAutomaticViewController
    {
        private ConnectionService _connection;
        private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();
        private string _text = "";
        private bool _busy;

        [Inject]
        public void Construct(ConnectionService connection)
        {
            _connection = connection;
        }

        [UIValue("title-text")]
        public string TitleText => Lang.T("profileView.title");

        [UIValue("refresh-text")]
        public string RefreshText => Lang.T("profileView.refresh");

        [UIValue("profile-text")]
        public string ProfileText => _text;

        [UIValue("refresh-interactable")]
        public bool RefreshInteractable => !_busy;

        protected override void DidActivate(bool firstActivation, bool addedToHierarchy, bool screenSystemEnabling)
        {
            base.DidActivate(firstActivation, addedToHierarchy, screenSystemEnabling);
            Plugin.Log.Info($"[profile] view activated (first={firstActivation})");
            if (firstActivation)
            {
                Refresh();
            }
        }

        protected override void OnDestroy()
        {
            _lifetime.Cancel();
            base.OnDestroy();
        }

        [UIAction("refresh-click")]
        private async void Refresh()
        {
            if (_busy)
            {
                return;
            }
            _busy = true;
            _text = Lang.T("common.loading");
            Notify();
            try
            {
                var result = await _connection.ProfileAsync(new ProfileRequest(), _lifetime.Token);
                _text = result == null ? Lang.T("connection.notConnected")
                    : result.Kind != HostResultKind.Ok ? Lang.T("common.loadFailedDetail", new { detail = result.DetailOrStatus })
                    : Core.Profile.ProfileText.Describe(result.Body);
                Plugin.Log.Info($"[profile] {(result == null ? "not connected" : result.Kind.ToString())}");
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Plugin.Log.Error(ex);
                _text = Lang.T("common.loadFailed", new { error = ex.Message });
            }
            finally
            {
                _busy = false;
                Notify();
            }
        }

        private void Notify()
        {
            NotifyPropertyChanged(nameof(ProfileText));
            NotifyPropertyChanged(nameof(RefreshInteractable));
        }
    }
}
