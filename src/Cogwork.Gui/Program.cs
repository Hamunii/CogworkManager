global using static Cogwork.Core.CogworkCoreLogger;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

[assembly: SupportedOSPlatform("Linux")]

namespace Cogwork.Gui;

class Program
{
    public static int Main(string[] args)
    {
        SetFatalLoggers();
        SetNativeLibraryResolvers();

        var app = Adw.Application.New("io.github.hamunii.Cogwork", Gio.ApplicationFlags.FlagsNone);

        app.OnActivate += (sender, e) =>
        {
            var games = Game.SupportedGames;

            var window = Adw.ApplicationWindow.New(app);
            window.SetTitle("Cogwork");
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

            configController = new ConfigureProfileViewController(
                window,
                navView,
                RefreshProfilesCallback
            );

            profilesController = new ProfilesViewController(
                navView,
                configController.Page,
                async lazyProfile => await configController.LoadProfilePageAsync(lazyProfile)
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

    private static void SetFatalLoggers()
    {
        AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
        {
            Cog.Fatal($"Unhandled exception occurred:\n{e.ExceptionObject as Exception}");
            Environment.Exit(1);
        };

        TaskScheduler.UnobservedTaskException += (sender, e) =>
        {
            Cog.Fatal($"Unhandled exception occurred:\n{e.Exception}");
            Environment.Exit(1);
        };

        GLib.UnhandledException.SetHandler(
            (exception) =>
            {
                Cog.Fatal($"Unhandled exception occurred in GTK loop:\n{exception}");
                Environment.Exit(1);
            }
        );
    }

    private static void SetNativeLibraryResolvers()
    {
        NativeLibrary.SetDllImportResolver(
            typeof(WebKit.WebView).Assembly,
            (libraryName, assembly, searchPath) =>
            {
                Console.WriteLine($"{assembly.GetName().Name}: {libraryName}");
                if (libraryName.Equals("WebKit", StringComparison.Ordinal))
                {
                    if (NativeLibrary.TryLoad("libwebkitgtk-6.0.so", out var handle))
                        return handle;
                    if (NativeLibrary.TryLoad("libwebkitgtk-6.0.so.4", out handle))
                        return handle;
                }
                return IntPtr.Zero;
            }
        );

        NativeLibrary.SetDllImportResolver(
            typeof(WebViewNativeLinkHandler).Assembly,
            (libraryName, assembly, searchPath) =>
            {
                Console.WriteLine($"{assembly.GetName().Name}: {libraryName}");
                if (libraryName.Equals("libgobject-2.0.so", StringComparison.Ordinal))
                {
                    if (NativeLibrary.TryLoad("libgobject-2.0.so", out var handle))
                        return handle;
                    if (NativeLibrary.TryLoad("libgobject-2.0.so.0", out handle))
                        return handle;
                }
                else if (libraryName.Equals("libwebkitgtk-6.0.so", StringComparison.Ordinal))
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
