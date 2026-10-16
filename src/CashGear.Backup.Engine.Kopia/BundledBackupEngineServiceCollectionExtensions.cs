using CashGear.Backup.Core;
using CashGear.Backup.Engine.Kopia;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Registration for hosts. It lives in the DI namespace so the agent can register the engine without
/// naming it (CLAUDE.md rule 1).
/// </summary>
public static class BundledBackupEngineServiceCollectionExtensions
{
    /// <summary>Registers the product's bundled backup engine as <see cref="IBackupEngineFactory"/>.</summary>
    public static IServiceCollection AddBundledBackupEngine(this IServiceCollection services)
    {
        services.TryAddSingleton<IBackupEngineFactory, KopiaBackupEngineFactory>();
        return services;
    }
}
