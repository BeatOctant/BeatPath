using System;
using BeatSaberMarkupLanguage.MenuButtons;
using Zenject;
using OctantPath.Core.Languages;

namespace OctantPath.UI
{
    // 左のメニューの MOD ボタン。メニューの部品が作り直されるたびに、古い登録を外して登録し直す
    internal sealed class MenuButtonRegistrar : IInitializable, IDisposable
    {
        private readonly MenuButtons _menuButtons;
        private readonly OctantPathFlowCoordinator _flowCoordinator;
        private readonly MenuButton _button;

        public MenuButtonRegistrar(MenuButtons menuButtons, OctantPathFlowCoordinator flowCoordinator)
        {
            _menuButtons = menuButtons;
            _flowCoordinator = flowCoordinator;
            _button = new MenuButton("OctantPath", Lang.T("menu.hint"), OnClick);
        }

        public void Initialize()
        {
            _menuButtons.RegisterButton(_button);
        }

        public void Dispose()
        {
            _menuButtons.UnregisterButton(_button);
        }

        private void OnClick()
        {
            _flowCoordinator.Present();
        }
    }
}
