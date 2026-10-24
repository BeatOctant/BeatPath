using OctantPath.Maps;
using OctantPath.Recommendations;
using OctantPath.UI;
using Zenject;

namespace OctantPath.Installers
{
    internal class OctantPathMenuInstaller : Installer
    {
        public override void InstallBindings()
        {
            // メニューは設定の OK（ゲームの軽い再起動）でも作り直される。画面を組み立てる前に、設定の言語をもう一度当てる
            // （起動のときだけ当てていたので、言語を変えて OK を押しても切り替わらなかった。2026-10-16）
            Plugin.UseLanguage(Container.Resolve<OctantPathFolders>(), Container.Resolve<SettingsHolder>().Current);
            Container.Bind<CoverCache>().AsSingle();
            Container.Bind<PreviewPlayer>().AsSingle();
            Container.Bind<PanelPlacement>().AsSingle();
            Container.BindInterfacesAndSelfTo<SettingsMenu>().AsSingle();
            Container.BindInterfacesTo<RecommendationTab>().AsSingle();
            Container.Bind<ProfileViewController>().FromNewComponentAsViewController().AsSingle();
            Container.Bind<ConnectionViewController>().FromNewComponentAsViewController().AsSingle();
            Container.Bind<OctantPathFlowCoordinator>().FromNewComponentOnNewGameObject().AsSingle();
            Container.BindInterfacesTo<MenuButtonRegistrar>().AsSingle();
        }
    }
}
