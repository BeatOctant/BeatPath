using HMUI;
using Zenject;

namespace OctantPath.UI
{
    internal class OctantPathFlowCoordinator : FlowCoordinator
    {
        private MainFlowCoordinator _mainFlowCoordinator;
        private ConnectionViewController _connectionView;
        private ProfileViewController _profileView;


        [Inject]
        public void Construct(MainFlowCoordinator mainFlowCoordinator, ConnectionViewController connectionView, ProfileViewController profileView)
        {
            _profileView = profileView;
            _mainFlowCoordinator = mainFlowCoordinator;
            _connectionView = connectionView;
        }

        public void Present()
        {
            _mainFlowCoordinator.YoungestChildFlowCoordinatorOrSelf().PresentFlowCoordinator(this);
        }

        protected override void DidActivate(bool firstActivation, bool addedToHierarchy, bool screenSystemEnabling)
        {
            if (firstActivation)
            {
                SetTitle("OctantPath");
                showBackButton = true;
            }
            if (addedToHierarchy)
            {
                ProvideInitialViewControllers(_profileView, _connectionView);
            }
        }

        protected override void BackButtonWasPressed(ViewController topViewController)
        {
            _mainFlowCoordinator.DismissFlowCoordinator(this);
        }
    }
}
