using ScoreSaber.Features.Replays.Services;
using ScoreSaber.Features.Replays.UI;
using Zenject;

namespace ScoreSaber.Features.Replays {
    internal class ReplayFeatureInstaller : Installer {
        public override void InstallBindings() {
#if BSIPA_MODS
            Container.BindInterfacesAndSelfTo<Playback.DesktopSettings>().AsSingle();
#endif
            Container.Bind<ReplayLoader>().AsSingle().NonLazy();
            Container.Bind<ReplayStorageService>().AsSingle();
            Container.Bind<ReplayQueryService>().AsSingle();
            Container.BindInterfacesTo<ResultsViewReplayButtonController>().AsSingle();
            Container.BindInterfacesTo<ReplayXrEventHandler>().AsSingle().NonLazy();
        }
    }
}
