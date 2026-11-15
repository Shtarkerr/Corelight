namespace Corelight.Core.Platform;

/// <summary>Набор системных адаптеров, с которыми работают действия твиков.</summary>
public sealed record PlatformServices(
    IRegistryAccess Registry,
    IServiceControl Services,
    IScheduledTaskControl Tasks,
    IAppxControl Apps,
    IPowerControl Power,
    IOneDriveControl OneDrive,
    IComponentPresence? Components = null);
