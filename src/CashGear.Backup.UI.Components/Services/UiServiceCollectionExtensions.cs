using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CashGear.Backup.UI.Components.Services;

public static class UiServiceCollectionExtensions
{
    /// <summary>
    /// The UI's own services. The host adds <see cref="Client.IAgentClient"/>, <see cref="IFolderPicker"/> and
    /// <see cref="IDesktopShell"/>.
    /// </summary>
    public static IServiceCollection AddBackupUi(this IServiceCollection services, UiOptions? options = null)
    {
        services.AddSingleton(options ?? new UiOptions());
        services.AddSingleton<AgentMonitor>();
        services.TryAddSingleton(TimeProvider.System);
        return services;
    }
}
