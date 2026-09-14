global using static Cogwork.Core.CogworkCoreLogger;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

[assembly: SupportedOSPlatform("Linux")]

namespace Cogwork.Gui;

class Program
{
    public static int Main(string[] args)
    {
        SetNativeLibraryResolvers();

        var app = Adw.Application.New("io.github.hamunii.cogwork", Gio.ApplicationFlags.FlagsNone);

        app.OnActivate += (sender, e) =>
        {
            var games = Game.SupportedGames;

            var window = Adw.ApplicationWindow.New(app);
            window.SetDefaultSize(1000, 700);

            var navView = Adw.NavigationView.New();

            Game? currentActiveGame = null;

            ConfigureProfileViewController? configController;
            ProfilesViewController profilesController = null!;

            void RefreshProfilesCallback()
            {
                if (currentActiveGame != null)
                {
                    profilesController.UpdatePage(currentActiveGame);
                }
            }

            configController = new ConfigureProfileViewController(navView, RefreshProfilesCallback);

            profilesController = new ProfilesViewController(
                navView,
                configController.Page,
                lazyProfile => configController.UpdateConfiguration(lazyProfile)
            );

            var dashboardController = new DashboardViewController(
                navView,
                games,
                profilesController.Page,
                selectedGame =>
                {
                    currentActiveGame = selectedGame;
                    profilesController.UpdatePage(selectedGame);
                }
            );

            navView.Push(dashboardController.Page);

            window.SetContent(navView);
            window.Present();
        };

        return app.Run(args);
    }

    private static void SetNativeLibraryResolvers()
    {
        NativeLibrary.SetDllImportResolver(
            typeof(JavaScriptCore.Context).Assembly,
            (libraryName, assembly, searchPath) =>
            {
                Console.WriteLine(libraryName);
                if (libraryName.Equals("JavaScriptCore", StringComparison.OrdinalIgnoreCase))
                {
                    if (NativeLibrary.TryLoad("libjavascriptcoregtk-6.0.so", out var handle))
                        return handle;
                    if (NativeLibrary.TryLoad("libjavascriptcoregtk-6.0.so.1", out handle))
                        return handle;
                }
                return IntPtr.Zero;
            }
        );

        NativeLibrary.SetDllImportResolver(
            typeof(WebKit.WebView).Assembly,
            (libraryName, assembly, searchPath) =>
            {
                Console.WriteLine(libraryName);
                if (libraryName.Equals("WebKit", StringComparison.OrdinalIgnoreCase))
                {
                    if (NativeLibrary.TryLoad("libwebkitgtk-6.0.so", out var handle))
                        return handle;
                    if (NativeLibrary.TryLoad("libwebkitgtk-6.0.so.4", out handle))
                        return handle;
                }
                return IntPtr.Zero;
            }
        );
    }
}
