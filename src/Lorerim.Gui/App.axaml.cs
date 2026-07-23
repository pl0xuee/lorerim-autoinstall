using System;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using Lorerim.Gui.Services;
using Lorerim.Gui.Services.Engine;
using Lorerim.Gui.Services.Modlist;
using Lorerim.Gui.Services.Nexus;
using Lorerim.Gui.ViewModels;
using Lorerim.Gui.Views;

namespace Lorerim.Gui;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        Services = BuildServices();

        // Single-instance socket for OAuth callbacks; register the jackify:// handler so the
        // browser can reach us. Registration shells out to xdg tooling, so it runs in the
        // background rather than holding up the window.
        if (!Services.GetRequiredService<OAuthCallbackListener>().TryStart())
        {
            Services
                .GetRequiredService<LogService>()
                .Append(
                    "Another instance already owns the sign-in callback socket — "
                        + "Nexus sign-in only works in the first instance."
                );
        }
        _ = Services.GetRequiredService<ProtocolHandlerRegistrar>().EnsureRegisteredAsync();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow
            {
                DataContext = Services.GetRequiredService<MainViewModel>(),
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static ServiceProvider BuildServices() =>
        new ServiceCollection()
            .AddHttpClient()
            // GitHub's API rejects requests without a User-Agent, and this client both talks
            // to api.github.com and downloads the update AppImage.
            .ConfigureHttpClientDefaults(b =>
                b.ConfigureHttpClient(c =>
                    c.DefaultRequestHeaders.UserAgent.ParseAdd(
                        $"lorerim-autoinstall/{AppUpdateService.CurrentVersion}"
                    )
                )
            )
            .AddSingleton<SettingsService>()
            .AddSingleton<LogService>()
            .AddSingleton<OperationRunner>()
            .AddSingleton<AppUpdateService>()
            .AddSingleton<PreflightService>()
            .AddSingleton<InstallOrchestrator>()
            .AddSingleton<EngineProgress>()
            .AddSingleton<JackifyEngineLocator>()
            .AddSingleton<JackifyEngineRunner>()
            .AddSingleton<ModlistResolverService>()
            .AddSingleton<ModFixupService>()
            .AddSingleton<NexusOAuthService>()
            .AddSingleton<NexusTokenStore>()
            .AddSingleton<ProtocolHandlerRegistrar>()
            .AddSingleton<OAuthCallbackListener>()
            .AddSingleton<Services.Steam.SteamLocator>()
            .AddSingleton<Services.Steam.SkyrimLocator>()
            .AddSingleton<Services.Steam.CompatToolCatalog>()
            .AddSingleton<Services.Steam.ShortcutsVdfService>()
            .AddSingleton<Services.Steam.ConfigVdfService>()
            .AddSingleton<Services.Steam.SteamProcessService>()
            .AddSingleton<Services.Steam.ProtonPrefixService>()
            .AddSingleton<Services.Steam.ProtontricksService>()
            .AddSingleton<Services.Steam.SteamGridArtService>()
            .AddSingleton<Services.Steam.SteamIntegrationService>()
            .AddSingleton<MainViewModel>()
            .AddSingleton<InstallViewModel>()
            .AddSingleton<SettingsViewModel>()
            .AddSingleton<SteamSetupViewModel>()
            .BuildServiceProvider();
}
