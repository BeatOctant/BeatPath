using System.Net.Http;
using OctantPath.Connection;
using OctantPath.Core.Maps;
using OctantPath.Maps;
using OctantPath.Core.Connection;
using OctantPath.Core.Settings;
using Zenject;

namespace OctantPath.Installers
{
    internal class OctantPathAppInstaller : Installer
    {
        public override void InstallBindings()
        {
            var folders = OctantPathFolders.InUserData();
            var directory = folders.Root;
            Container.Bind<OctantPathFolders>().FromInstance(folders).AsSingle();
            Container.Bind<SettingsHolder>().AsSingle();
            var http = new HttpClient();
            http.DefaultRequestHeaders.UserAgent.ParseAdd(Plugin.UserAgent);
            Container.Bind<HttpClient>().FromInstance(http).AsSingle();
            Container.Bind<BeatSaverClient>().FromMethod(ctx =>
            {
                var holder = ctx.Container.Resolve<SettingsHolder>();
                return new BeatSaverClient(http, () => holder.Current);
            }).AsSingle();
            Container.Bind<MapInstaller>().FromMethod(ctx =>
            {
                var holder = ctx.Container.Resolve<SettingsHolder>();
                return new MapInstaller(http, () => holder.Current);
            }).AsSingle();
            Container.Bind<OwnershipLedger>().FromInstance(new OwnershipLedger(directory)).AsSingle();
            Container.Bind<PlaylistSync>().AsSingle();
            Container.Bind<HostClient>().AsSingle();
            Container.Bind<IConnectionStore>().FromMethod(_ => new ConnectionStore(directory, Plugin.Log.Warn)).AsSingle();
            Container.BindInterfacesAndSelfTo<ConnectionService>().AsSingle();
            Container.BindInterfacesAndSelfTo<Signals.SignalService>().AsSingle();
            Container.Bind<Recommendations.RecommendationService>().AsSingle();
            Container.BindInterfacesAndSelfTo<DownloadService>().AsSingle();
            Container.BindInterfacesAndSelfTo<Trials.TrialService>().AsSingle();
        }
    }

    internal sealed class SettingsHolder
    {
        private readonly OctantPathFolders _folders;

        public SettingsHolder(OctantPathFolders folders)
        {
            _folders = folders;
            Reload();
        }

        public OctantPathSettings Current { get; private set; }

        public void Reload()
        {
            Current = OctantPathSettings.Load(_folders.Root, Plugin.Log.Warn);
            Changed?.Invoke();
        }

        public event System.Action Changed;

        // 設定画面で変えた 1 項目を settings.json に書いてから読み直す
        public void Save(string key, Newtonsoft.Json.Linq.JToken value)
        {
            try
            {
                OctantPathSettings.SaveValue(_folders.Root, key, value);
            }
            catch (System.Exception ex)
            {
                Plugin.Log.Warn($"settings.json could not be written: {ex.Message}");
                return;
            }
            Reload();
        }
    }
}
