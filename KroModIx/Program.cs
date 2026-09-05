using System;
using Avalonia;
using KroModIx.Logging;
using KroModIx.Services;
using KroModIx.Views;
using NLog;

namespace KroModIx;

internal static class Program
{
    /// <summary>Vom <see cref="App"/> beim Startup abgeholt. Statisch, weil der
    /// Avalonia-Lifecycle keinen sauberen Punkt hat, um Args in die App-Instanz
    /// zu injecten — der übliche Weg ist ein IPublicClientApplication-artiger
    /// Wrapper, den wir uns hier sparen. Die Alternative wäre CommandLine erst
    /// im App.OnFrameworkInitializationCompleted zu parsen, aber dann würden
    /// Warnungen des Parsers erst nach dem NLog-Setup landen.</summary>
    public static AppLaunchOptions LaunchOptions { get; private set; } = new();

    /// <summary>Setzt die NLog-Variable <c>logDir</c> auf
    /// <c>&lt;StateRoot&gt;/logs</c>. Schlaegt das fehl (Rechte, volle Platte),
    /// bleibt der Default aus der nlog.config stehen — ein kaputter Log-Pfad
    /// darf den App-Start nie verhindern.</summary>
    private static void ConfigureLogDirectory()
    {
        try
        {
            var dir = System.IO.Path.Combine(AppPaths.StateRoot, "logs");
            System.IO.Directory.CreateDirectory(dir);
            // Indexer, nicht Add() — die nlog.config bringt „logDir" schon als
            // Default mit, Add() wuerde auf dem vorhandenen Key werfen.
            var cfg = LogManager.Configuration;
            if (cfg is null) return;
            cfg.Variables["logDir"] = dir;
            LogManager.Configuration = cfg;
        }
        catch
        {
            // Bewusst still: hier laeuft noch kein Logger, dem wir das melden
            // koennten. Die Konsolen-Ausgabe funktioniert unabhaengig davon.
        }
    }

    [STAThread]
    public static void Main(string[] args)
    {
        // MaskingLayoutRenderer VOR dem ersten Logger-Aufruf registrieren —
        // sonst wird ${masked:...} nicht aufgelöst und die Log-Zeile ist kaputt.
        MaskingLayoutRenderer.Register();

        // v1.28.2: Log-Verzeichnis auf einen garantiert beschreibbaren Pfad
        // setzen, ebenfalls vor dem ersten Logger-Aufruf. Die nlog.config hatte
        // ein relatives „logs/" — das loest NLog gegen das Arbeitsverzeichnis
        // auf, und beim AppImage ist das der read-only Mount bzw. der Ordner,
        // aus dem der User gestartet hat. Ergebnis: gar kein Log, genau dann
        // wenn man eins braucht. StateRoot ist XDG_STATE_HOME/KroModIx
        // (Linux) bzw. %LOCALAPPDATA%\KroModIx\state (Windows).
        ConfigureLogDirectory();

        var log = LogManager.GetCurrentClassLogger();
        log.Info("KroModIx start (args: {Args})", string.Join(" ", args));

        LaunchOptions = AppLaunchOptions.Parse(args);
        if (LaunchOptions.ApiPortOverride is not null)
            log.Info("CLI: API auf Port {Port} aktiviert{Auto}",
                LaunchOptions.ApiPortOverride,
                LaunchOptions.AutoShutdownAfter is { } d ? $" — Auto-Shutdown nach {d.TotalSeconds:0}s" : "");

        // Einmalige Migration von ModManager-Config (falls User bereits
        // installiert hatte) nach KroModIx-Config-Verzeichnis. VOR allen
        // AppPaths-Zugriffen, sonst wird ~/.config/KroModIx/ leer angelegt
        // bevor die alte Config kopiert werden kann.
        ConfigMigrationService.MigrateIfNeeded();

        GlobalExceptionHandler.Install();

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(LaunchOptions.RemainingArgs);
        }
        catch (Exception ex)
        {
            log.Fatal(ex, "App terminated with unhandled exception");
            throw;
        }
        finally
        {
            log.Info("KroModIx exit");
            LogManager.Shutdown();
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
