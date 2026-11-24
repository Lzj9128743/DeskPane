using Microsoft.Extensions.DependencyInjection;

namespace DeskPane.Services;

/// <summary>
/// Central DI registration for all core DeskPane services.
/// These registrations have application lifetime. Feature/window factories
/// own shorter-lived instances; App disposes this container after its consumers.
/// </summary>
public static class ServiceRegistry
{
    /// <summary>
    /// Registers all core application services into the given service collection.
    /// </summary>
    public static IServiceCollection AddDeskPaneServices(this IServiceCollection services)
    {
        // ── Core infrastructure ──────────────────────────────────────────
        services.AddSingleton<SettingsService>();
        services.AddSingleton<DeskPaneDataBackupService>();
        services.AddSingleton<ICredentialStore>(_ => new PasswordVaultCredentialStore());
        services.AddSingleton<CloudBackupService>(sp =>
            new CloudBackupService(
                sp.GetRequiredService<DeskPaneDataBackupService>(),
                sp.GetRequiredService<SettingsService>(),
                sp.GetRequiredService<ICredentialStore>()));
        services.AddSingleton<DeskPaneDiagnosticsBundleService>();
        services.AddSingleton<FileService>();
        services.AddSingleton<ResizeGuideOverlayService>();
        services.AddSingleton<ManagedStorageDesktopShortcutService>();

        // ── Feature services ─────────────────────────────────────────────
        services.AddSingleton<OrganizerService>(sp =>
            new OrganizerService(
                sp.GetRequiredService<SettingsService>(),
                sp.GetRequiredService<FileService>()));
        services.AddSingleton<LocalizationService>();
        services.AddSingleton<ThemeService>();

        return services;
    }
}
