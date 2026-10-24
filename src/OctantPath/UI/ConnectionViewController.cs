using System;
using OctantPath.Connection;
using OctantPath.Core.Connection;
using OctantPath.Core.Languages;
using BeatSaberMarkupLanguage.Attributes;
using BeatSaberMarkupLanguage.ViewControllers;
using Zenject;

namespace OctantPath.UI
{
    [ViewDefinition("OctantPath.UI.ConnectionView.bsml")]
    [HotReload(RelativePathToLayout = @"ConnectionView.bsml")]
    internal class ConnectionViewController : BSMLAutomaticViewController
    {
        private ConnectionService _connection;
        private Signals.SignalService _signals;
        private bool _refreshing;
        private string _code = "";
        private string _problem;

        [Inject]
        public void Construct(ConnectionService connection, Signals.SignalService signals)
        {
            _signals = signals;
            _connection = connection;
        }

        [UIValue("title-text")]
        public string TitleText => Lang.T("connectionView.title");

        [UIValue("refresh-text")]
        public string RefreshText => Lang.T("connectionView.refresh");

        [UIValue("status-text")]
        public string StatusText => _problem ?? Core.Connection.StatusText.Describe(_connection.Status);

        // 契約6（client to OctantCompass）2.0: つなぐ先（空なら同じ PC の既定）と、OctantCompass の画面に出る合言葉を入れてペアリングする
        [UIValue("address-text")]
        public string AddressText => Lang.T("connectionView.address");

        [UIValue("address-hint")]
        public string AddressHint => Lang.T("connectionView.addressHint", new { port = ConnectionInfo.DefaultPort });

        // 入れたまま、ペアリングが通るまでは接続情報に書かない
        [UIValue("address")]
        public string Address { get; set; } = "";

        [UIValue("code-text")]
        public string CodeText => Lang.T("connectionView.code");

        [UIValue("code-hint")]
        public string CodeHint => Lang.T("connectionView.codeHint");

        [UIValue("code")]
        public string Code
        {
            get => _code;
            set
            {
                _code = value ?? "";
                NotifyPropertyChanged(nameof(PairInteractable));
            }
        }

        [UIValue("pair-text")]
        public string PairText => Lang.T("connectionView.pair");

        [UIValue("pair-interactable")]
        public bool PairInteractable => !_refreshing && _code.Trim().Length > 0;

        [UIValue("detail-text")]
        public string DetailText
        {
            get
            {
                var s = _connection.Status;
                var parts = new System.Collections.Generic.List<string>();
                if (s.Endpoint != null) parts.Add(s.Endpoint);
                if (s.HostVersion != null) parts.Add($"OctantCompass {s.HostVersion} (API {s.HostApiVersion})");
                if (s.DbVersion != null) parts.Add($"DB {s.DbVersion}");
                if (s.Detail != null) parts.Add(s.Detail);
                if (_signals.Problem != null) parts.Add(_signals.Problem);
                return string.Join("\n", parts);
            }
        }

        [UIValue("refresh-interactable")]
        public bool RefreshInteractable => !_refreshing;

        protected override void DidActivate(bool firstActivation, bool addedToHierarchy, bool screenSystemEnabling)
        {
            base.DidActivate(firstActivation, addedToHierarchy, screenSystemEnabling);
            _connection.StatusChanged += HandleStatusChanged;
            Notify();
            Refresh();
        }

        protected override void DidDeactivate(bool removedFromHierarchy, bool screenSystemDisabling)
        {
            base.DidDeactivate(removedFromHierarchy, screenSystemDisabling);
            _connection.StatusChanged -= HandleStatusChanged;
        }

        [UIAction("pair-click")]
        private void Pair()
        {
            if (_refreshing)
            {
                return;
            }
            _refreshing = true;
            _problem = null;
            Notify();
            _ = PairAsync();
        }

        private async System.Threading.Tasks.Task PairAsync()
        {
            try
            {
                if (await _connection.PairAsync(Address, _code).ConfigureAwait(true))
                {
                    if (_connection.Status.State != ConnectionState.PairingRejected && _connection.Status.State != ConnectionState.Unreachable)
                    {
                        _code = "";
                        NotifyPropertyChanged(nameof(Code));
                    }
                }
                else
                {
                    _problem = Lang.T("connectionView.badAddress");
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.Error(ex);
            }
            finally
            {
                _refreshing = false;
                Notify();
            }
        }

        [UIAction("refresh-click")]
        private void Refresh()
        {
            if (_refreshing)
            {
                return;
            }
            _refreshing = true;
            _problem = null;
            NotifyPropertyChanged(nameof(RefreshInteractable));
            _ = RefreshAsync();
        }

        private async System.Threading.Tasks.Task RefreshAsync()
        {
            try
            {
                await _connection.RefreshNowAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                Plugin.Log.Error(ex);
            }
            finally
            {
                _refreshing = false;
                Notify();
            }
        }

        private void HandleStatusChanged(ConnectionStatus status) => Notify();

        private void Notify()
        {
            NotifyPropertyChanged(nameof(StatusText));
            NotifyPropertyChanged(nameof(DetailText));
            NotifyPropertyChanged(nameof(RefreshInteractable));
            NotifyPropertyChanged(nameof(PairInteractable));
        }
    }
}
