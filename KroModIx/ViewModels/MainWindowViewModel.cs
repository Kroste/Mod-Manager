using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using KroModIx.Plugin.Contracts;
using KroModIx.Services;
using KroModIx.Services.Games;
using KroModIx.Services.Plugins;
using KroModIx.Services.Steam;
using KroModIx.Views;
using NLog;

namespace KroModIx.ViewModels;

/// <summary>
/// Haupt-VM des MainWindow. Discovery bei Init + Filter/Sortierung der Sidebar
/// + Anzeige der Plugin-Tabs im Content-Bereich (aktuell selektiertes Spiel).
/// </summary>
public sealed partial class MainWindowViewModel : ViewModelBase
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private readonly IServiceProvider _services;
    private readonly GameDiscoveryService _discovery;
    private readonly GamesCacheService _gamesCache;
    private readonly GameCoverService _covers;
    private readonly GameLauncherService _launcher;
    private readonly ManualGamesService _manual;
    private readonly PluginRegistryScanner _pluginScanner;
    private readonly PluginActivationPlanner _pluginPlanner;
    private readonly PluginActivator _pluginActivator;
    private readonly PluginIndexService _pluginIndex;
    private readonly PluginInstaller _pluginInstaller;
    private readonly PluginUpdateService _pluginUpdates;
    private readonly PluginUninstaller _pluginUninstaller;
    private readonly PluginAutoInstallService _pluginAutoInstall;
    private readonly GameUpdateBadgeService _updateBadges;
    private readonly NotificationSinkImpl _notifications;
    private readonly AppSettingsService _settings;
    private readonly HostUpdateService _hostUpdate;
    private readonly StatusProgressCoordinator _statusProgress;
    private readonly Services.Ai.AiSettingsService _aiSettings;

    private int _nextToastId;

    private PluginIndex? _indexCache;

    private readonly List<GameEntry> _allGames = new();

    public MainWindowViewModel(IServiceProvider services)
    {
        _services = services;
        _discovery = services.GetRequiredService<GameDiscoveryService>();
        _gamesCache = services.GetRequiredService<GamesCacheService>();
        _covers = services.GetRequiredService<GameCoverService>();
        _launcher = services.GetRequiredService<GameLauncherService>();
        _manual = services.GetRequiredService<ManualGamesService>();
        _pluginScanner = services.GetRequiredService<PluginRegistryScanner>();
        _pluginPlanner = services.GetRequiredService<PluginActivationPlanner>();
        _pluginActivator = services.GetRequiredService<PluginActivator>();
        _pluginIndex = services.GetRequiredService<PluginIndexService>();
        _pluginInstaller = services.GetRequiredService<PluginInstaller>();
        _pluginUpdates = services.GetRequiredService<PluginUpdateService>();
        _pluginUninstaller = services.GetRequiredService<PluginUninstaller>();
        _pluginAutoInstall = services.GetRequiredService<PluginAutoInstallService>();
        _updateBadges = services.GetRequiredService<GameUpdateBadgeService>();
        _notifications = services.GetRequiredService<NotificationSinkImpl>();
        _settings = services.GetRequiredService<AppSettingsService>();
        _hostUpdate = services.GetRequiredService<HostUpdateService>();
        _statusProgress = services.GetRequiredService<StatusProgressCoordinator>();
        _aiSettings = services.GetRequiredService<Services.Ai.AiSettingsService>();

        _aiSettings.SettingsChanged += (_, _) => Dispatcher.UIThread.Post(RefreshAiChip);
        RefreshAiChip();

        // Plugin-Notifications direkt als Toast anzeigen (bislang gingen sie
        // nur in den Log). Marshallen auf UI-Thread, weil Plugins vom Worker-
        // Thread notifizieren dürfen.
        _notifications.Notified += (_, e) => Dispatcher.UIThread.Post(() =>
            EnqueueToast(e.Message, e.Level));

        _pluginActivator.LoadedChanged += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            RefreshPluginStates();
            // v1.24.2: nach jedem Plugin-Load-Event einmal reconciliieren —
            // fixt den Fall dass ein Manual-Game mit Engine schon in der
            // Sidebar steht, das passende Plugin aber nachtraeglich geladen
            // wurde (oder die manual-games.json extern editiert wurde) und
            // dadurch NotifyGameAddedAsync nie fuer dieses Game gefeuert wurde.
            _ = ReconcileEngineGamesAsync();
        });

        // v1.19.1: PluginIndex kann sich zur Laufzeit aendern (Background-
        // Refresh beim App-Start, expliziter „Jetzt pruefen"-Klick).
        // Sterne + Install-Card sofort mitziehen — sonst muesste der User
        // den App-Neustart abwarten.
        _pluginIndex.IndexRefreshed += (_, _) => Dispatcher.UIThread.Post(() =>
            _ = LoadPluginIndexAsync(CancellationToken.None));

        // v1.16.0: Manual-Add zur Laufzeit → Sidebar-Kachel sofort einfuegen +
        // geladene Plugins per OnGameAddedAsync benachrichtigen, damit sich
        // Watcher/Registry ohne App-Neustart aufs neue Spiel ausrichten. Muss
        // auf dem UI-Thread laufen weil _allGames + VisibleGames dort leben.
        _manual.GameAdded += (_, entry) => Dispatcher.UIThread.Post(async () =>
        {
            var key = $"manual:{entry.Id}";
            if (_allGames.Any(g => g.Key == key)) return; // AddBulk-Dedupe
            var discovered = new DiscoveredGame(
                Key: key,
                DisplayName: entry.DisplayName,
                InstallDir: entry.InstallDir,
                SteamAppId: entry.SteamAppId,
                ManualId: entry.Id,
                CustomCoverPath: entry.CoverPath,
                Source: DiscoveredGameSource.Manual,
                ExecutablePath: entry.ExecutablePath,
                Engine: entry.Engine);
            var newEntry = new GameEntry(discovered);
            _allGames.Add(newEntry);
            ApplyFilterAndSort();
            _ = LoadCoversAsync(new[] { newEntry }, default);
            try { await _pluginActivator.NotifyGameAddedAsync(discovered).ConfigureAwait(true); }
            catch (Exception ex) { Log.Debug(ex, "NotifyGameAddedAsync warf"); }
            RefreshPluginStates();
        });

        // Plugin hat via IHostServices.TrySetManualGameCover den Cover-Pfad
        // eines Manual-Games gesetzt → betroffene Sidebar-Kachel neu laden.
        Services.Plugins.HostServicesImpl.ManualCoverChanged += (_, manualId) =>
            Dispatcher.UIThread.Post(() =>
            {
                var entry = _allGames.FirstOrDefault(g => g.Source.ManualId == manualId);
                if (entry is null) return;
                // Cover-Cache-Path wechselte → CustomCoverPath im DiscoveredGame
                // ist immutable, also frisch neu bauen aus ManualEntry.
                var fresh = _manual.All.FirstOrDefault(m => m.Id == manualId);
                if (fresh is null) return;
                entry.Source = entry.Source with { CustomCoverPath = fresh.CoverPath };
                _ = LoadCoversAsync(new[] { entry }, default);
            });

        // Plugin hat via IHostServices.TryRenameManualGame den Container-Ordner
        // umbenannt → In-Memory Kachel-VM auf den neuen Pfad re-keyen und —
        // wenn's die aktuell selected Kachel ist — Detail-View neu bauen
        // (Tab-VMs cachen sonst den alten containerPath aus ihrem ctor).
        Services.Plugins.HostServicesImpl.ManualGameRenamed += (_, args) =>
            Dispatcher.UIThread.Post(() =>
            {
                var (id, newInstallDir) = args;
                var entry = _allGames.FirstOrDefault(g => g.Source.ManualId == id);
                if (entry is null) return;
                entry.Source = entry.Source with { InstallDir = newInstallDir };
                if (SelectedGame == entry)
                {
                    // Deselect + Reselect erzwingt RenderContentForSelected —
                    // Plugin liefert frische Tab-Contributions mit neuem
                    // DetectedGame.InstallDir.
                    var pinned = entry;
                    SelectedGame = null;
                    Dispatcher.UIThread.Post(() => SelectedGame = pinned);
                }
            });

        _updateBadges.Changed += (_, _) => Dispatcher.UIThread.Post(RefreshUpdateBadges);
        _pluginUpdates.UpdatesChanged += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            AvailableUpdateCount = _pluginUpdates.AvailableUpdates.Count;
        });
        _statusProgress.Changed += (_, e) => Dispatcher.UIThread.Post(() =>
        {
            ProgressIsActive = e.IsActive;
            ProgressTitle = e.Title ?? "";
            ProgressMessage = e.Message ?? "";
            ProgressFraction = e.Fraction;
            ProgressIndeterminate = e.Indeterminate;
        });

        // v1.14.7: Bei Sprachwechsel den kompletten Plugin-Tab-Cache
        // invalidieren + Content-View neu rendern. Ohne das behaelt der
        // v1.14.6-Cache die vorhandenen View-Instanzen mit ihren zum
        // Construct-Zeitpunkt gelesenen Strings.T()-Werten — die Uebersetzung
        // wird erst beim naechsten Plugin-Reload sichtbar.
        Localization.LocalizationService.Instance.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is not (nameof(Localization.LocalizationService.Current)
                or nameof(Localization.LocalizationService.CurrentIso))) return;
            Dispatcher.UIThread.Post(() =>
            {
                foreach (var stale in _pluginTabsCache.Values) DisposeTabs(stale);
                _pluginTabsCache.Clear();
                _tabCacheOrder.Clear();
                _lastRenderKey = null;
                if (SelectedGame is not null) RenderContentForSelected(SelectedGame);
            });
        };

        // Persistierten Sidebar-Filter beim Start übernehmen.
        _showAllGames = _settings.Current.SidebarShowAllGames;
    }

    public ObservableCollection<GameEntry> VisibleGames { get; } = new();

    /// <summary>Toast-Overlay unten rechts im MainWindow. Neue Einträge landen
    /// über <see cref="EnqueueToast"/>; nach 6 Sekunden werden sie via Timer
    /// wieder entfernt (kein Fade-Out — für die UX reicht ein instant remove).</summary>
    public ObservableCollection<ToastItem> Toasts { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedGame))]
    [NotifyPropertyChangedFor(nameof(CanLaunchSelected))]
    private GameEntry? _selectedGame;

    public bool HasSelectedGame => SelectedGame is not null;

    [ObservableProperty]
    private string _searchText = string.Empty;

    /// <summary>Sidebar-Filter: wenn true, werden auch Non-Plugin-Games
    /// angezeigt (ausgegraut). Persistiert in <see cref="AppSettings.SidebarShowAllGames"/>.
    /// Default: false (nur Plugin-Games).</summary>
    [ObservableProperty]
    private bool _showAllGames;

    [ObservableProperty]
    private ObservableCollection<TabItem>? _pluginTabs;

    [ObservableProperty]
    private InstallCardViewModel? _installCard;

    [ObservableProperty]
    private string _contentPlaceholderText = "Wähle links ein Spiel aus.";

    [ObservableProperty]
    private bool _showPluginTabs;

    [ObservableProperty]
    private bool _showInstallCard;

    [ObservableProperty]
    private bool _showContentPlaceholder = true;

    [ObservableProperty]
    private string _statusText = "Starte …";

    // Progress-Anzeige — wird vom StatusProgressCoordinator gefüttert. Plugin-
    // Aktionen wie Downloads rufen IHostServices.BeginProgress → das feuert
    // Changed-Events, die hier landen und die Statusbar aktualisieren.
    [ObservableProperty] private bool _progressIsActive;
    [ObservableProperty] private string _progressTitle = "";
    [ObservableProperty] private string _progressMessage = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProgressFraction))]
    private double? _progressFraction;
    [ObservableProperty] private bool _progressIndeterminate;

    public bool HasProgressFraction => ProgressFraction is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAvailableUpdates))]
    [NotifyPropertyChangedFor(nameof(UpdateBadgeText))]
    private int _availableUpdateCount;

    public bool HasAvailableUpdates => AvailableUpdateCount > 0;
    public string UpdateBadgeText => AvailableUpdateCount > 0 ? $"↑ {AvailableUpdateCount}" : "";

    /// <summary>Kurz-Bezeichner des aktiven KI-Modells fürs Header-Chip
    /// (z.B. „Ollama · llama3.1:8b"). Leer wenn kein Provider konfiguriert.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAiChip))]
    private string _aiChipLabel = "";

    [ObservableProperty] private string _aiChipTooltip = "";

    public bool HasAiChip => !string.IsNullOrEmpty(AiChipLabel);

    private void RefreshAiChip()
    {
        var s = _aiSettings.Current;
        if (s.Provider == Services.Ai.AiProviderType.None)
        {
            AiChipLabel = "";
            AiChipTooltip = "";
            return;
        }
        var cfg = s.Active;
        var providerName = s.Provider switch
        {
            Services.Ai.AiProviderType.Ollama => "Ollama",
            Services.Ai.AiProviderType.Anthropic => "Anthropic",
            Services.Ai.AiProviderType.OpenAi => "OpenAI",
            Services.Ai.AiProviderType.Gemini => "Gemini",
            Services.Ai.AiProviderType.Mistral => "Mistral",
            Services.Ai.AiProviderType.OpenAiCompatible => "API",
            _ => s.Provider.ToString(),
        };
        var model = string.IsNullOrWhiteSpace(cfg.Model) ? "?" : cfg.Model;
        AiChipLabel = $"🤖 {providerName} · {model}";
        AiChipTooltip = $"KI-Provider: {providerName}\nModell: {model}\nEndpoint: {cfg.Endpoint}\n\nKlick öffnet Einstellungen.";
    }

    partial void OnSearchTextChanged(string value) => ApplyFilterAndSort();
    partial void OnShowAllGamesChanged(bool value)
    {
        _settings.Update(s => s.SidebarShowAllGames = value);
        ApplyFilterAndSort();
        RefreshDimmingFlags();
    }

    // Wird während des App-Start-Bootstraps auf false gesetzt, damit die
    // vielen impliziten Selection-Wechsel (ListBox-Auto-Select nach Clear+Add,
    // Sortier-Refresh nach PluginIndex-Load) den persistierten
    // LastSelectedGameId nicht überschreiben, bevor RestoreLastSelection läuft.
    private bool _persistSelection;

    // v1.14.5: gesetzt waehrend ApplyFilterAndSort — verhindert dass der
    // transiente ListBox-Clear-Selection=null einen Tab-Rebuild ausloest.
    private bool _inFilterRefresh;

    partial void OnSelectedGameChanged(GameEntry? value)
    {
        if (value is null)
        {
            // Transient null aus ApplyFilterAndSort (VisibleGames.Clear
            // propagiert SelectedItem=null via TwoWay-Bind). Wenn wir hier
            // PluginTabs leeren + Render-Cache invalidieren, wird kurz
            // drauf beim SelectedGame=previouslySelected komplett neu
            // gerendert — neue Plugin-VM-Instanz, alle Cover weg,
            // Tab-Selection faellt auf Tab 0. Solange wir im Filter-
            // Refresh-Fenster sind, ignorieren wir das null.
            if (_inFilterRefresh) return;
            PluginTabs = null;
            ShowPluginTabs = false;
            _lastRenderKey = null;
            return;
        }
        RenderContentForSelected(value);
        if (_persistSelection)
            _settings.Update(s => s.LastSelectedGameId = value.Key);
    }

    /// <summary>Kompletter Init-Ablauf beim App-Start:
    /// <list type="number">
    /// <item>Cache-Load — instant, Sidebar zeigt sofort die zuletzt bekannten Spiele</item>
    /// <item>Plugin-Discovery + Activation — läuft gegen die Cache-Games</item>
    /// <item>UI-Restore (LastSelectedGame)</item>
    /// <item>Fresh Discovery im Background — diff't neue/entfernte Spiele in die
    ///     Sidebar zurück und aktualisiert den Cache</item>
    /// </list>
    /// Beim ersten Start (leerer Cache) läuft Discovery synchron als Fallback.
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        // 1) Cache-Load — instant.
        var cached = _gamesCache.Load();
        if (cached.Count > 0)
        {
            _allGames.Clear();
            foreach (var g in cached) _allGames.Add(new GameEntry(g));
            Log.Info("Sidebar aus Cache: {Count} Spiele geladen", _allGames.Count);
            StatusText = $"{_allGames.Count} Spiele (Cache).";
        }
        else
        {
            // Erster App-Start: Discovery synchron, sonst startet die App
            // mit leerer Sidebar bis der Background-Job fertig ist.
            StatusText = "Discovery …";
            var initial = await Task.Run(() => _discovery.Discover(), ct).ConfigureAwait(true);
            _allGames.Clear();
            foreach (var g in initial) _allGames.Add(new GameEntry(g));
            _gamesCache.Save(initial);
            Log.Info("Sidebar (Erst-Discovery): {Count} Spiele geladen", _allGames.Count);
            StatusText = $"{_allGames.Count} Spiele erkannt.";
        }

        // 2) Cover parallel im Background laden (limitiert).
        _ = LoadCoversAsync(_allGames.ToArray(), ct);

        // 3) Plugin-Discovery + Activation gegen die Cache-Games.
        await ActivatePluginsAsync(ct).ConfigureAwait(true);

        // 4) PluginIndex im Hintergrund laden.
        _ = LoadPluginIndexAsync(ct);

        // 5) UI-Filter + Dimming + LastSelection wiederherstellen.
        RefreshDimmingFlags();
        ApplyFilterAndSort();
        RestoreLastSelection();
        _persistSelection = true;

        // 6) Fresh Discovery im Background — Diff einpflegen.
        _ = RefreshDiscoveryAsync(ct);

        // Plugin-Update-Check im Hintergrund.
        _ = Task.Run(async () =>
        {
            try { await _pluginUpdates.CheckAllAsync(ct); }
            catch (Exception ex) { Log.Debug(ex, "Initial Plugin-Update-Check fehlgeschlagen"); }
        }, ct);

        // Mod-Update-Badges-Loop starten (Plugins mit IUpdateNotifier).
        // Refresh alle 30min, mit initialem 10s-Delay damit der Discovery-
        // Rush erst durchläuft.
        _updateBadges.Start();

        // v1.16.0: Host-Self-Update-Check periodisch. Initial nach 60 s
        // (nicht direkt beim Start um Discovery-Rush nicht zu ueberlagern),
        // dann alle 24 h. Bei verfuegbarem Update ein Toast — kein Auto-
        // Install (der User klickt selbst im About-Dialog).
        _ = HostUpdateCheckLoopAsync(ct);
    }

    private async Task HostUpdateCheckLoopAsync(CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(60), ct); } catch { return; }
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = await _hostUpdate.CheckForUpdateAsync(ct);
                if (result.UpdateAvailable)
                {
                    Log.Info("Host-Update verfuegbar: {Cur} → {Latest}",
                        result.CurrentVersion, result.LatestVersion);
                    Dispatcher.UIThread.Post(() => EnqueueToast(
                        $"🎉 KroModIx v{result.LatestVersion} verfuegbar — im Ueber-Dialog installieren.",
                        NotificationLevel.Info, TimeSpan.FromSeconds(12)));
                }
            }
            catch (Exception ex) { Log.Debug(ex, "Host-Update-Check fehlgeschlagen"); }
            try { await Task.Delay(TimeSpan.FromHours(24), ct); } catch { return; }
        }
    }

    /// <summary>Fresh Steam-Discovery im Hintergrund. Vergleicht das Ergebnis
    /// mit dem aktuellen <see cref="_allGames"/>-State und synchronisiert:
    /// neue Spiele werden hinzugefügt, verschwundene entfernt. Der Cache wird
    /// nur überschrieben wenn die Discovery tatsächlich etwas Neues zeigt —
    /// bei kaputter Steam-Installation bleibt der Cache erhalten.</summary>
    private async Task RefreshDiscoveryAsync(CancellationToken ct)
    {
        try
        {
            var fresh = await Task.Run(() => _discovery.Discover(), ct).ConfigureAwait(false);

            // async-Lambda: der Diff meldet neue Spiele per await an die Plugins
            // (v1.28.2) — die InvokeAsync-Ueberladung fuer Func<Task> haelt die
            // Reihenfolge, der Aufrufer wartet also wirklich bis alles durch ist.
            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                var freshKeys = new HashSet<string>(fresh.Select(g => g.Key), StringComparer.Ordinal);
                var currentKeys = new HashSet<string>(_allGames.Select(g => g.Key), StringComparer.Ordinal);

                var added = new List<DiscoveredGame>();
                foreach (var g in fresh)
                    if (!currentKeys.Contains(g.Key)) added.Add(g);

                var removed = new List<GameEntry>();
                foreach (var entry in _allGames)
                    if (!freshKeys.Contains(entry.Key)) removed.Add(entry);

                foreach (var g in added) _allGames.Add(new GameEntry(g));
                foreach (var entry in removed) _allGames.Remove(entry);

                if (added.Count > 0 || removed.Count > 0)
                {
                    Log.Info("Discovery-Refresh: +{Added} / -{Removed} Spiel(e)", added.Count, removed.Count);
                    _gamesCache.Save(fresh);
                    ApplyFilterAndSort();
                    RefreshPluginStates();
                    if (added.Count > 0)
                        _ = LoadCoversAsync(added.Select(g => _allGames.First(e => e.Key == g.Key)).ToArray(), ct);
                    StatusText = $"{_allGames.Count} Spiele (aktualisiert: +{added.Count}/-{removed.Count}).";

                    // 4.2: Discovery-Diff als Toasts. Kompakt formatiert
                    // (bis zu 3 Namen, sonst „+N weitere") damit die Karten
                    // nicht überquellen wenn Steam viele Spiele gleichzeitig
                    // meldet (z.B. nach einer neu gemounteten Library-Platte).
                    if (added.Count > 0)
                        EnqueueToast($"🎮 +{added.Count} Spiel(e): {FormatGameList(added.Select(g => g.DisplayName))}",
                            NotificationLevel.Info);
                    if (removed.Count > 0)
                        EnqueueToast($"🗑 -{removed.Count} Spiel(e): {FormatGameList(removed.Select(g => g.DisplayName))}",
                            NotificationLevel.Warning);

                    // 4.3: Auto-Cleanup — wenn Setting aktiv UND für ein
                    // geladenes Plugin kein Zielspiel mehr da ist, Plugin-
                    // Ordner löschen. Zur Runtime bleibt das Plugin geladen
                    // (kein AssemblyLoadContext-Unload — Checkmk-Erfahrung),
                    // beim nächsten Start ist es weg.
                    if (_settings.Current.PluginAutoCleanupOnGameUninstall)
                        _ = RunAutoCleanupAsync(fresh, removed, ct);

                    // v1.28.2: Spiele die HIER reinkommen, muessen den geladenen
                    // Plugins genauso gemeldet werden wie ein Manual-Add — sonst
                    // fehlen sie in LoadedPlugin.DetectedGames, MatchesGame
                    // schlaegt fehl und der Content-Bereich zeigt statt der
                    // Plugin-Tabs eine Install-Karte fuer ein laengst
                    // installiertes Plugin. Betraf real den Ordner-Scan-Import,
                    // der erst in der naechsten Session als Discovery-Delta
                    // ankommt.
                    foreach (var g in added)
                    {
                        try { await _pluginActivator.NotifyGameAddedAsync(g, ct).ConfigureAwait(true); }
                        catch (Exception ex)
                        { Log.Warn(ex, "NotifyGameAddedAsync warf fuer {Dir}", g.InstallDir); }
                    }

                    // v1.28.1: neu aufgetauchte Spiele koennen ein Plugin
                    // brauchen das lokal fehlt (frisch installiertes Steam-
                    // Spiel, gemountete Platte). Der Service filtert selbst,
                    // was schon da ist oder in dieser Session schon versucht
                    // wurde — der Aufruf ist also billig wenn es nichts zu
                    // tun gibt.
                    if (added.Count > 0)
                        _ = AutoInstallMissingPluginsAsync(ct);
                }
                else
                {
                    // Cache-Timestamp trotzdem auffrischen — Cache-Files älter als
                    // ein paar Tage würden bei fehlender Steam-Session wieder auf
                    // die alte Liste zeigen.
                    _gamesCache.Save(fresh);
                }
            });
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Discovery-Refresh im Hintergrund fehlgeschlagen");
        }
    }

    private async Task LoadPluginIndexAsync(CancellationToken ct)
    {
        try
        {
            var idx = await _pluginIndex.GetAsync(ct).ConfigureAwait(false);
            // Auf UI-Thread setzen, damit Bindings (Sterne + Install-Karte)
            // die Änderungen sicher sehen.
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _indexCache = idx;
                RefreshPluginStates();
                Log.Info("Plugin-Index in UI übernommen: {N} Plugin(s), Sterne aktualisiert",
                    idx.Plugins.Count);
            });
            // v1.28.1: erst jetzt kann der Host wissen, welche Plugins zu den
            // Sidebar-Spielen gehoeren — also hier die fehlenden nachziehen.
            await Dispatcher.UIThread.InvokeAsync(() => AutoInstallMissingPluginsAsync(ct));
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Plugin-Index-Load fehlgeschlagen — nur installierte Sterne");
        }
    }

    /// <summary>Laesst den <see cref="PluginAutoInstallService"/> alle Plugins
    /// nachladen, fuer die ein Spiel in der Sidebar steht — der Fall „App neu
    /// installiert, Spiele noch da, Plugin-Ordner leer". Laeuft still: nur bei
    /// tatsaechlich installierten Plugins gibt es einen Toast, Fehler (offline,
    /// GitHub-Rate-Limit) stehen im Log und werden pro Session nicht wiederholt.
    /// Muss auf dem UI-Thread laufen — die Aktivierung baut Plugin-Tabs.</summary>
    private async Task AutoInstallMissingPluginsAsync(CancellationToken ct)
    {
        try
        {
            var games = _allGames.Select(g => g.Source).ToList();
            var summary = await _pluginAutoInstall.RunAsync(_indexCache, games, ct)
                .ConfigureAwait(true);
            if (!summary.AnyInstalled) return;

            RefreshPluginStates();
            // Der Render-Cache haengt an PluginState + geladener Plugin-Id;
            // beides hat sich gerade geaendert, also Karte→Tabs erzwingen.
            _lastRenderKey = null;
            if (SelectedGame is not null) RenderContentForSelected(SelectedGame);

            EnqueueToast(
                $"Automatisch nachinstalliert: {FormatGameList(summary.Installed)}",
                NotificationLevel.Info);
        }
        catch (OperationCanceledException) { /* App faehrt runter */ }
        catch (Exception ex)
        {
            Log.Warn(ex, "Auto-Install der fehlenden Plugins schlug fehl");
        }
    }

    private async Task ActivatePluginsAsync(CancellationToken ct)
    {
        try
        {
            var discovered = await Task.Run(_pluginScanner.Scan, ct).ConfigureAwait(true);

            // Plugins mit VirtualGame (z. B. RenPyAssist ohne echten Steam-
            // Bezug): Manual-Anker anlegen falls noch keiner mit der
            // SteamAppId existiert, danach die Sidebar-Games neu einlesen
            // damit der neue Anker im ersten Plan mitgezählt wird.
            bool anyEnsured = false;
            foreach (var disc in discovered)
            {
                var vg = disc.Manifest.VirtualGame;
                if (vg is null || vg.SteamAppId == 0
                    || string.IsNullOrWhiteSpace(vg.DisplayName)) continue;
                if (_manual.EnsureVirtualAnchor(vg.DisplayName, vg.SteamAppId))
                    anyEnsured = true;
            }
            if (anyEnsured)
            {
                var refreshed = await Task.Run(() => _discovery.Discover(), ct).ConfigureAwait(true);
                _allGames.Clear();
                foreach (var g in refreshed) _allGames.Add(new GameEntry(g));
                _gamesCache.Save(refreshed);
                Log.Info("Virtual-Anchor(s) angelegt — Sidebar neu geladen: {N} Spiele", _allGames.Count);
            }

            var currentGames = _allGames.Select(g => g.Source).ToList();
            var hostVer = ParseVersion(_hostUpdate.CurrentVersion);
            var decisions = _pluginPlanner.Plan(discovered, currentGames, hostVer);
            await _pluginActivator.ActivateManyAsync(decisions, ct).ConfigureAwait(true);
            RefreshPluginStates();

            if (anyEnsured)
            {
                // Erst nach Aktivierung Filter/Sort neu anwenden — neue Kachel
                // taucht sonst nicht mit korrektem PluginState in der Liste auf.
                ApplyFilterAndSort();
            }
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Plugin-Aktivierung schlug fehl — App läuft ohne Plugins weiter");
        }
    }

    private static Version ParseVersion(string s)
    {
        int dash = s.IndexOf('-'); if (dash >= 0) s = s[..dash];
        int plus = s.IndexOf('+'); if (plus >= 0) s = s[..plus];
        return Version.TryParse(s, out var v) ? v : new Version(0, 0);
    }

    private void RefreshPluginStates()
    {
        var loaded = _pluginActivator.Loaded;
        // Alle Game-Keys die von einem geladenen Plugin bedient werden
        // (durch SteamAppId ODER Engine-Match — v1.9.0+).
        var keysWithLoadedPlugin = loaded
            .SelectMany(l => l.DetectedGames)
            .Select(BuildKeyForDetectedGame)
            .Where(k => k is not null)
            .Select(k => k!)
            .ToHashSet(StringComparer.Ordinal);

        var appIdsWithAvailablePlugin = _indexCache?.Plugins
            .SelectMany(p => p.SteamAppIds)
            .ToHashSet() ?? new HashSet<int>();
        // v1.28.1: dasselbe fuer Engine-Games (Manual-Kacheln aus dem Ordner-
        // Scan haben keine SteamAppId — ohne diese Menge blieben sie ewig auf
        // PluginState.None und bekamen nie eine Install-Karte).
        var enginesWithAvailablePlugin = PluginIndexMatcher.AvailableEngines(_indexCache);

        // v1.14.4: PluginState des SELECTED Games vor der Neuberechnung
        // merken. Nur wenn er sich aendert, muessen wir die Content-View
        // neu bauen — sonst zerstoeren wir die laufende Plugin-Tab-VM (z.B.
        // NexusViewModel mit geladenen Covers) und die TabControl-Selection
        // faellt auf Tab 0 zurueck. Trigger fuer diesen unnoetigen Re-Render
        // war v.a. LoadPluginIndexAsync (kommt ~10-15 s nach App-Start
        // asynchron vom Netz und ruft dann RefreshPluginStates auf).
        var oldStateOfSelected = SelectedGame?.PluginState;

        int installedCount = 0, availableCount = 0;
        foreach (var g in _allGames)
        {
            if (keysWithLoadedPlugin.Contains(g.Key))
            {
                g.PluginState = PluginState.Installed;
                installedCount++;
                continue;
            }
            if (g.Source.SteamAppId is int appId && appIdsWithAvailablePlugin.Contains(appId))
            {
                g.PluginState = PluginState.Available;
                availableCount++;
                continue;
            }
            if (!string.IsNullOrWhiteSpace(g.Source.Engine)
                && enginesWithAvailablePlugin.Contains(g.Source.Engine))
            {
                g.PluginState = PluginState.Available;
                availableCount++;
                continue;
            }
            g.PluginState = PluginState.None;
        }
        Log.Info("RefreshPluginStates: {Installed} installed, {Available} available (of {Total} games); selected={Sel}",
            installedCount, availableCount, _allGames.Count, SelectedGame?.Key ?? "<none>");

        RefreshFavoriteFlags();
        ApplyFilterAndSort();
        RefreshDimmingFlags();

        if (SelectedGame is null) return;
        var newStateOfSelected = SelectedGame.PluginState;
        if (oldStateOfSelected != newStateOfSelected)
        {
            Log.Info("RefreshPluginStates: PluginState {Old}→{New} fuer selected — Re-Render Tabs",
                oldStateOfSelected, newStateOfSelected);
            RenderContentForSelected(SelectedGame);
        }
    }

    /// <summary>Baut den Sidebar-Key zu einem DetectedGame. Steam-Games via
    /// SteamAppId, Manual-Games via ManualId (die trägt der Plugin-Activator
    /// im HostServicesImpl nicht direkt — wir lookup'en über InstallDir).</summary>
    private string? BuildKeyForDetectedGame(DetectedGame dg)
    {
        if (dg.Target.SteamAppId is int appId)
            return $"steam:{appId}";
        // Manual-Games: match über InstallDir (case-insensitive), da DetectedGame
        // die ManualId nicht kennt.
        var match = _allGames.FirstOrDefault(g =>
            g.Source.Source == Services.Games.DiscoveredGameSource.Manual
            && string.Equals(g.Source.InstallDir, dg.InstallDir, StringComparison.OrdinalIgnoreCase));
        return match?.Key;
    }

    /// <summary>Fügt einen Toast ins Overlay ein und plant sein Auto-Remove
    /// nach der angegebenen Dauer. Muss auf dem UI-Thread aufgerufen werden
    /// (die Kollektion ist an Avalonia-Bindings gekoppelt).</summary>
    public void EnqueueToast(string message, NotificationLevel level = NotificationLevel.Info,
        TimeSpan? duration = null)
    {
        var d = duration ?? TimeSpan.FromSeconds(6);
        var id = System.Threading.Interlocked.Increment(ref _nextToastId);
        var toast = new ToastItem(id, message, level);
        Toasts.Add(toast);
        _ = Task.Run(async () =>
        {
            await Task.Delay(d);
            await Dispatcher.UIThread.InvokeAsync(() => Toasts.Remove(toast));
        });
    }

    [RelayCommand]
    private void DismissToast(ToastItem? toast)
    {
        if (toast is not null) Toasts.Remove(toast);
    }

    private static string FormatGameList(IEnumerable<string> names)
    {
        var list = names.ToList();
        if (list.Count <= 3) return string.Join(", ", list);
        return string.Join(", ", list.Take(3)) + $", +{list.Count - 3} weitere";
    }

    /// <summary>Prüft für jedes geladene Plugin, ob unter den <paramref name="fresh"/>-
    /// Games noch ein Zielspiel existiert. Wenn nicht → <see cref="PluginUninstaller.Uninstall"/>
    /// löscht den Plugin-Ordner unter <c>~/.config/KroModIx/plugins/</c>. Toast
    /// informiert den User über die Aktion. Läuft im Hintergrund, keine
    /// Blockierung des Discovery-Refreshes.</summary>
    private async Task RunAutoCleanupAsync(IReadOnlyList<DiscoveredGame> fresh,
        List<GameEntry> removed, CancellationToken ct)
    {
        if (removed.Count == 0) return;
        var freshAppIds = new HashSet<int>(fresh.Select(g => g.SteamAppId).OfType<int>());
        var candidates = _pluginActivator.Loaded
            .Where(l => l.Manifest.Targets.All(t => t.SteamAppId is not int id || !freshAppIds.Contains(id)))
            .ToList();

        foreach (var loaded in candidates)
        {
            try
            {
                // Auto-Cleanup: nur Plugin-Assembly weg, User-Data + Cache
                // behalten — die will man beim eventuellen Re-Install nicht
                // verlieren (Nexus-Key, Katalog-Snapshot etc.).
                _pluginUninstaller.Uninstall(loaded.Manifest.Id, deleteData: false, deleteCache: false);
                await Dispatcher.UIThread.InvokeAsync(() =>
                    EnqueueToast(
                        $"🧹 Auto-Cleanup: Plugin '{loaded.Manifest.DisplayName}' entfernt (keine Zielspiele mehr installiert). Beim nächsten Start weg.",
                        NotificationLevel.Warning,
                        TimeSpan.FromSeconds(10)));
                Log.Info("Auto-Cleanup: Plugin {Id} deinstalliert", loaded.Manifest.Id);
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "Auto-Cleanup für Plugin {Id} fehlgeschlagen", loaded.Manifest.Id);
            }
            if (ct.IsCancellationRequested) break;
        }
    }

    /// <summary>Setzt <see cref="GameEntry.PendingUpdateCount"/> + Tooltip aus
    /// <see cref="GameUpdateBadgeService.Pending"/>. Läuft auf dem UI-Thread —
    /// die Bindings triggern das Neuzeichnen des grünen ↑-Badges pro Kachel.</summary>
    private void RefreshUpdateBadges()
    {
        var pending = _updateBadges.Pending;
        var pendingByDir = _updateBadges.PendingByInstallDir;
        foreach (var g in _allGames)
        {
            GameUpdateInfo? info = null;
            if (g.Source.SteamAppId is int appId && pending.TryGetValue(appId, out var byAppId))
                info = byAppId;
            else if (!string.IsNullOrEmpty(g.Source.InstallDir)
                     && pendingByDir.TryGetValue(g.Source.InstallDir, out var byDir))
                info = byDir;

            if (info is not null)
            {
                g.PendingUpdateCount = info.PendingCount;
                g.UpdateBadgeTooltip = info.Summary ?? $"{info.PendingCount} Update(s) verfügbar";
            }
            else
            {
                g.PendingUpdateCount = 0;
                g.UpdateBadgeTooltip = null;
            }
        }
        // v1.13: Header-Badge + Sortierung neu triggern — Games mit Update
        // sollen jetzt in der Sidebar oben stehen (nach den Favoriten).
        OnPropertyChanged(nameof(TotalGamesWithUpdates));
        OnPropertyChanged(nameof(HasGamesWithUpdates));
        ApplyFilterAndSort();
    }

    /// <summary>Setzt <see cref="GameEntry.IsDimmed"/> für alle Spiele.
    /// Dimming greift nur wenn <see cref="ShowAllGames"/> aktiv ist UND das
    /// Spiel kein Plugin hat — bei „nur mit Plugin" (Default-Filter) sind
    /// alle sichtbaren Spiele voll deckend.</summary>
    private void RefreshDimmingFlags()
    {
        foreach (var g in _allGames)
            g.IsDimmed = ShowAllGames && g.PluginState == PluginState.None;
    }

    /// <summary>v1.13: syncrhonisiert <see cref="GameEntry.IsFavorite"/> aus
    /// den <c>AppSettings.FavoriteGameKeys</c>. Wird nach Discovery + nach
    /// jedem Toggle aufgerufen.</summary>
    private void RefreshFavoriteFlags()
    {
        var favs = new HashSet<string>(_settings.Current.FavoriteGameKeys, StringComparer.Ordinal);
        foreach (var g in _allGames)
            g.IsFavorite = favs.Contains(g.Key);
    }

    /// <summary>v1.13: Anzahl aller Spiele mit ausstehenden Mod-Updates.
    /// Wird im MainWindow-Header als "🎮 N Mod-Updates" angezeigt — parallel
    /// zum bereits existierenden Plugin-Update-Badge "↑ N".</summary>
    public int TotalGamesWithUpdates => _allGames.Count(g => g.HasUpdates);
    public bool HasGamesWithUpdates => TotalGamesWithUpdates > 0;

    /// <summary>v1.13: toggelt Favoriten-Status des aktuell selektierten
    /// Spiels. Aufruf aus dem Sidebar-Kontextmenü (Rechtsklick auf Kachel).</summary>
    [RelayCommand]
    private void ToggleFavorite(GameEntry? entry)
    {
        entry ??= SelectedGame;
        if (entry is null) return;
        entry.IsFavorite = !entry.IsFavorite;
        _settings.Update(s =>
        {
            var set = new HashSet<string>(s.FavoriteGameKeys, StringComparer.Ordinal);
            if (entry.IsFavorite) set.Add(entry.Key);
            else set.Remove(entry.Key);
            s.FavoriteGameKeys = set.OrderBy(k => k, StringComparer.Ordinal).ToList();
        });
        ApplyFilterAndSort();
    }

    private async Task LoadCoversAsync(GameEntry[] entries, CancellationToken ct)
    {
        // Simpler serieller Load — bei 30 Spielen ok, für Kohärenz besser als
        // 30 parallele CDN-Requests (Rate-Limit).
        foreach (var entry in entries)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                // User-Override aus dem Sidebar-Kontextmenü hat Vorrang vor
                // dem Manual-Game CustomCoverPath aus dem AddGame-Dialog.
                var userOverride = _settings.Current.CustomGameCovers is { } dict
                                    && dict.TryGetValue(entry.Key, out var op) ? op : null;
                var customPath = userOverride ?? entry.Source.CustomCoverPath;
                var path = await _covers.ResolveCoverAsync(
                    entry.Source.SteamAppId, customPath, ct).ConfigureAwait(false);
                if (path is null || !File.Exists(path)) continue;
                Bitmap? bmp = null;
                try
                {
                    // Skia auf Linux: Bitmap-Load auf UI-Thread nötig (Renderer-Init).
                    // Wir laden synchron auf UI-Thread; das ist bei 30 Bildern kein Problem.
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        using var s = File.OpenRead(path);
                        bmp = new Bitmap(s);
                        entry.Cover = bmp;
                    });
                }
                catch (Exception ex)
                {
                    Log.Debug(ex, "Cover-Bitmap-Load für {Key} fehlgeschlagen ({Path})", entry.Key, path);
                }
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Cover-Resolve für {Key} fehlgeschlagen", entry.Key);
            }
        }
    }

    /// <summary>Alle PluginIndex-Kategorien fuer ein Game (leere Sequenz wenn
    /// kein Plugin im Index passt). SteamAppId-Match, ab v1.28.1 zusaetzlich
    /// Engine-Match — damit sind die Kategorien auch fuer Ordner-Sammlungen
    /// (Ren'Py &amp; Co.) durchsuchbar.</summary>
    private IEnumerable<string> CategoriesForGame(GameEntry g)
    {
        if (_indexCache is null) yield break;
        foreach (var p in IndexEntriesFor(g))
            foreach (var c in p.Categories) yield return c;
    }

    private void ApplyFilterAndSort()
    {
        var q = SearchText?.Trim() ?? string.Empty;

        // Vom User versteckte Spiele (Kontextmenü „Aus KroModIx entfernen" auf
        // einem Steam-Spiel) niemals in der Sidebar zeigen — auch nicht wenn
        // ShowAllGames aktiv ist.
        var hidden = new HashSet<string>(_settings.Current.HiddenGameKeys, StringComparer.Ordinal);

        IEnumerable<GameEntry> filtered = _allGames.Where(g => !hidden.Contains(g.Key));
        if (!string.IsNullOrEmpty(q))
        {
            // v1.16.0: Suche matcht DisplayName ODER eine der PluginIndex-
            // Kategorien des Games (z.B. „rpg", „farming"). Damit werden
            // Kategorien nutzbar ohne extra Sidebar-UI-Element.
            filtered = filtered.Where(g =>
                g.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase)
                || CategoriesForGame(g).Any(c => c.Contains(q, StringComparison.OrdinalIgnoreCase)));
        }
        // Default: nur mit Plugin. ShowAllGames aktiv → alles (Non-Plugin-Games
        // werden im XAML ausgegraut via IsDimmed). Favoriten sind IMMER sichtbar
        // egal ob Plugin oder nicht — sonst waere das Feature bei "nur mit
        // Plugin" nutzlos.
        if (!ShowAllGames)
            filtered = filtered.Where(g => g.PluginState != PluginState.None || g.IsFavorite);

        // v1.13: Sortier-Reihenfolge — Favoriten ganz oben, dann Spiele mit
        // Mod-Updates, dann Plugin-Spiele (Installed vor Available), zuletzt
        // Rest. Innerhalb jeder Gruppe alphabetisch.
        var sorted = filtered
            .OrderByDescending(g => g.IsFavorite)
            .ThenByDescending(g => g.HasUpdates)
            .ThenByDescending(g => g.PluginState == PluginState.Installed)
            .ThenByDescending(g => g.PluginState == PluginState.Available)
            .ThenBy(g => g.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        // WICHTIG: Avalonias ListBox setzt SelectedItem auf null wenn die
        // ItemsSource.Clear() aufgerufen wird — bei TwoWay-Binding rennt das
        // durch bis zu unserem SelectedGame und nullt es. Deshalb vorher
        // sichern und nach dem Refill wiederherstellen. Das _inFilterRefresh-
        // Flag signalisiert OnSelectedGameChanged, das transiente null zu
        // ignorieren — sonst wuerden die PluginTabs geleert und der Render-
        // Cache invalidiert, was beim Wiederherstellen zu einem kompletten
        // Tab-Rebuild fuehrt (Cover weg, Tab-Selection auf Tab 0).
        var previouslySelected = SelectedGame;

        _inFilterRefresh = true;
        try
        {
            VisibleGames.Clear();
            foreach (var g in sorted) VisibleGames.Add(g);
        }
        finally { _inFilterRefresh = false; }

        if (previouslySelected is not null && VisibleGames.Contains(previouslySelected))
            SelectedGame = previouslySelected;
        else if (SelectedGame is null)
            SelectedGame = VisibleGames.FirstOrDefault();
    }

    private void RestoreLastSelection()
    {
        var lastKey = _settings.Current.LastSelectedGameId;
        SelectedGame = (lastKey is not null
            ? VisibleGames.FirstOrDefault(g => g.Key == lastKey)
            : null) ?? VisibleGames.FirstOrDefault();
    }

    // Cache-Key des letzten Rendern-Zustands: entry.Key + PluginState + LoadedPluginId +
    // (int)IndexCache-Count. Ändert sich nichts davon, ist der Render redundant.
    private string? _lastRenderKey;

    // v1.14.6: Pro Game-Key gecachte Plugin-Tabs. Ohne den Cache erzeugt
    // jeder Game-Wechsel eine frische Plugin-VM-Instanz — die vorherige
    // (mit geladenen Covers, Screenshots, Katalog-State) wird verworfen.
    // Wechsel zurueck fuehrt zu leeren Rows waehrend die Cover neu
    // geladen werden. Cache-Key = renderKey (entry.Key|State|LoadedId|
    // IdxCount) — aendert sich der Plugin-Zustand des Games, faellt der
    // Eintrag automatisch weg und wird frisch aufgebaut.
    private readonly Dictionary<string, ObservableCollection<TabItem>> _pluginTabsCache = new();

    /// <summary>Zugriffsreihenfolge fuer <see cref="TrimTabCache"/> (aeltester
    /// zuerst).</summary>
    private readonly List<string> _tabCacheOrder = new();

    /// <summary>Wie viele Spiele ihre Plugin-Tabs gleichzeitig im Cache
    /// behalten. Vier deckt das reale Hin-und-Her-Wechseln ab, ohne dass der
    /// Speicher unbegrenzt waechst.</summary>
    private const int MaxCachedTabSets = 4;

    /// <summary>v1.27.1: Der Tab-Cache war unbegrenzt. Jedes einmal
    /// angeklickte Spiel liess seine Plugin-ViewModels dauerhaft am Leben —
    /// mitsamt Katalog-Listen und allen dekodierten Cover-Bitmaps. Wer sich
    /// durch seine Bibliothek klickt, sammelt so pro Spiel ein paar hundert MB
    /// an, bis systemd-oomd den Prozess abschiesst (real beim Screenshot-Lauf
    /// nach acht Spielen, u.a. mit dem 1537 Eintraege grossen
    /// ficsit-Katalog).
    ///
    /// <para>Der Cache selbst bleibt sinnvoll: ohne ihn baut jeder
    /// Spielwechsel die VMs neu und alle Cover laden erneut. Er braucht nur
    /// eine Obergrenze — der aelteste Eintrag fliegt raus und wird sauber
    /// disposed.</para></summary>
    private void TrimTabCache()
    {
        while (_tabCacheOrder.Count > MaxCachedTabSets)
        {
            var oldest = _tabCacheOrder[0];
            _tabCacheOrder.RemoveAt(0);
            if (!_pluginTabsCache.Remove(oldest, out var evicted)) continue;
            // Niemals die gerade sichtbaren Tabs wegwerfen.
            if (ReferenceEquals(evicted, PluginTabs)) continue;
            Log.Debug("Tab-Cache: aeltesten Eintrag verworfen ({Key})", oldest);
            DisposeTabs(evicted);
        }
    }

    /// <summary>v1.24.4: Verworfene Plugin-Tabs aufraeumen. Ohne das bleibt
    /// jede aus dem Cache geworfene Plugin-VM ewig am Leben, sobald sie sich
    /// auf ein langlebiges Service-Event abonniert hat (Registry.Changed,
    /// DownloadEventBus.ModInstalled) — der Event-Delegate haelt die
    /// Referenz. Die toten VMs arbeiten sogar weiter: bei Ren'Py loest jede
    /// Registry-Aenderung in JEDER Leiche Cover-Reload + KI-Beschreibungs-
    /// Uebersetzung aus.
    ///
    /// <para>Mehrere Plugins (DSP, Schedule I, 7DTD, CoI) implementieren
    /// dafuer laengst <see cref="IDisposable"/> auf ihren ViewModels — nur
    /// hat der Host das nie aufgerufen. Fehler beim Dispose duerfen den
    /// Tab-Rebuild nicht kippen, deshalb pro Tab gekapselt.</para></summary>
    private static void DisposeTabs(IEnumerable<TabItem>? tabs)
    {
        if (tabs is null) return;
        foreach (var tab in tabs)
        {
            try
            {
                if (tab.Content is StyledElement el && el.DataContext is IDisposable vm)
                    vm.Dispose();
                if (tab.Content is IDisposable disposableView) disposableView.Dispose();
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "Dispose eines verworfenen Plugin-Tabs fehlgeschlagen: {Tab}", tab.Tag);
            }
        }
    }

    /// <summary>v1.24.2: Sicherheitsnetz gegen den „kein Plugin verfuegbar"-
    /// False-Negative-Fall bei Manual-Games mit Engine-Match. Prueft fuer
    /// jedes Manual-Game mit <see cref="DiscoveredGame.Engine"/> ob ein
    /// geladenes Plugin es schon in seiner DetectedGames-Liste hat. Wenn
    /// nicht (z.B. weil manual-games.json extern editiert wurde, oder das
    /// Plugin per Live-Install nachgeladen wurde nachdem der Add lief), wird
    /// <c>NotifyGameAddedAsync</c> nachtraeglich gefeuert — das Plugin ist
    /// dadurch synchron.
    ///
    /// <para>Trigger: LoadedChanged (Plugin gerade geladen) + einmal beim
    /// SelectedGame-Wechsel wenn das aktuelle Game kein Plugin-Match hat.
    /// Idempotent — feuert nur wenn wirklich ein Delta besteht.</para></summary>
    /// <summary>v1.27.1: Re-Entrancy-Guard. Ohne ihn hat sich diese Methode
    /// mit <see cref="RenderContentForSelected"/> gegenseitig aufgerufen, bis
    /// der Stack voll war — siehe Kommentar am Re-Render unten.</summary>
    private bool _reconcileRunning;

    private async Task ReconcileEngineGamesAsync()
    {
        if (_reconcileRunning) return;
        var loadedSnap = _pluginActivator.Loaded;
        if (loadedSnap.Count == 0) return;
        _reconcileRunning = true;
        try
        {
            var candidates = _allGames
                .Where(g => !string.IsNullOrWhiteSpace(g.Source.Engine)
                            && g.Source.Source == Services.Games.DiscoveredGameSource.Manual)
                .ToList();
            int notified = 0;
            foreach (var g in candidates)
            {
                var alreadyKnown = loadedSnap.Any(l => l.DetectedGames.Any(dg =>
                    string.Equals(dg.InstallDir, g.Source.InstallDir, StringComparison.OrdinalIgnoreCase)));
                if (alreadyKnown) continue;

                // Ist irgend ein geladenes Plugin von der Engine her ueberhaupt zustaendig?
                var wouldMatch = loadedSnap.Any(l => l.Manifest.Targets.Any(t =>
                    !string.IsNullOrWhiteSpace(t.Engine)
                    && string.Equals(t.Engine, g.Source.Engine, StringComparison.OrdinalIgnoreCase)));
                if (!wouldMatch) continue;

                try
                {
                    await _pluginActivator.NotifyGameAddedAsync(g.Source).ConfigureAwait(true);
                    notified++;
                    Log.Info("Reconcile: NotifyGameAddedAsync fuer verwaistes Engine-Game {Name} ({Dir}) nachgefeuert",
                        g.DisplayName, g.Source.InstallDir);
                }
                catch (Exception ex) { Log.Warn(ex, "Reconcile-Notify warf fuer {Dir}", g.Source.InstallDir); }
            }

            // Re-Render NUR wenn wirklich etwas nachgemeldet wurde.
            //
            // v1.27.1 (Absturz-Fix): vorher lief das unbedingt. Bei einem
            // Manual-Game mit Engine, fuer das KEIN geladenes Plugin
            // zustaendig ist, passiert in der Schleife nichts — kein await,
            // also laeuft die Methode synchron durch bis hierher, setzt
            // _lastRenderKey zurueck und ruft RenderContentForSelected. Das
            // landet wieder im selben "kein Plugin"-Zweig, feuert Reconcile
            // erneut … bis der Stack voll ist. Die App starb ~1,5 s nach dem
            // Start mit "Stack overflow", sobald so eine Kachel als zuletzt
            // gewaehltes Spiel wiederhergestellt wurde (real: Ren'Py-Kachel,
            // Plugin nicht im Aktivierungs-Plan).
            if (notified > 0 && SelectedGame is not null)
            {
                _lastRenderKey = null;
                RenderContentForSelected(SelectedGame);
            }
        }
        finally { _reconcileRunning = false; }
    }

    private void RenderContentForSelected(GameEntry entry)
    {
        var loaded = _pluginActivator.Loaded.FirstOrDefault(l => MatchesGame(l, entry));
        var currentKey = $"{entry.Key}|{entry.PluginState}|{loaded?.Manifest.Id ?? ""}|{_indexCache?.Plugins.Count ?? -1}";
        if (currentKey == _lastRenderKey) return;
        _lastRenderKey = currentKey;

        Log.Info("Render {Key} ({Name}) → Loaded={LoadedId} IndexCache={IdxCount} State={State}",
            entry.Key, entry.DisplayName,
            loaded?.Manifest.Id ?? "<none>",
            _indexCache?.Plugins.Count ?? -1,
            entry.PluginState);

        if (loaded is null)
        {
            // Plugin verfügbar, aber nicht installiert? → Install-Karte statt Placeholder.
            var indexEntry = PluginIndexMatcher.InstallOfferFor(
                _indexCache, entry.Source, _pluginActivator.Loaded.Select(l => l.Manifest.Id));

            // v1.28.2: Eine Install-Karte fuer ein BEREITS GELADENES Plugin ist
            // immer falsch — der Klick wuerde dasselbe Plugin nochmal von
            // GitHub holen. Dass wir hier stehen heisst dann naemlich nicht
            // „Plugin fehlt", sondern „Plugin ist da, kennt dieses eine Spiel
            // nur noch nicht" (Kachel kam ueber den Discovery-Refresh rein,
            // ohne dass NotifyGameAddedAsync lief). Dafuer ist das Reconcile-
            // Sicherheitsnetz weiter unten zustaendig — also Eintrag
            // verwerfen und dorthin durchfallen.
            //
            // Regression aus v1.28.1: davor matchte FindIndexEntryFor nur ueber
            // SteamAppId und lieferte fuer Engine-Games immer null, wodurch der
            // Reconcile-Zweig zwangslaeufig erreicht wurde. Mit dem Engine-Match
            // greift das frueher — und das `return` unter der Install-Karte hat
            // das Sicherheitsnetz uebersprungen.
            if (indexEntry is null && FindIndexEntryFor(entry) is { } suppressed)
                Log.Info("Install-Karte fuer {PluginId} unterdrueckt — Plugin ist geladen, "
                    + "kennt '{Game}' nur noch nicht. Reconcile uebernimmt.",
                    suppressed.Id, entry.DisplayName);

            if (indexEntry is not null)
            {
                InstallCard = new InstallCardViewModel(
                    indexEntry, entry.DisplayName,
                    _pluginInstaller, _pluginActivator, _pluginPlanner,
                    ParseVersion(_hostUpdate.CurrentVersion),
                    // Snapshot der aktuellen Games — der Planner braucht sie um
                    // MatchedGames für das Plugin auszurechnen. Ohne diesen
                    // Snapshot wird das Plugin ohne Spiel-Kontext initialisiert
                    // und die Install-Karte bleibt sichtbar statt der Plugin-Tabs.
                    gamesProvider: () => _allGames.Select(g => g.Source).ToList(),
                    onInstalledLive: async () =>
                    {
                        // Manuell wieder installiert → der Auto-Install darf
                        // dieses Plugin kuenftig auch wieder nachziehen.
                        _pluginAutoInstall.ClearOptOut(indexEntry.Id);
                        RefreshPluginStates();
                        RenderContentForSelected(entry);
                        await Task.CompletedTask;
                    });
                ShowInstallCard = true;
                ShowPluginTabs = false;
                ShowContentPlaceholder = false;
                Log.Info("→ Install-Karte gezeigt für Plugin {PluginId}", indexEntry.Id);
                return;
            }

            InstallCard = null;
            ShowInstallCard = false;
            PluginTabs = null;
            ShowPluginTabs = false;
            ShowContentPlaceholder = true;
            ContentPlaceholderText =
                $"Für „{entry.DisplayName}“ ist kein Plugin verfügbar.";
            // v1.24.2: Sicherheitsnetz — vielleicht hat ein geladenes Engine-
            // Plugin dieses Manual-Game einfach noch nicht in seiner
            // DetectedGames-Liste (siehe ReconcileEngineGamesAsync-Kommentar).
            // Feuer den Reconcile einmal an und lass das Ergebnis das
            // Re-Rendern uebernehmen. Idempotent — wenn nichts zu tun,
            // bleibt der Placeholder stehen.
            if (!string.IsNullOrWhiteSpace(entry.Source.Engine))
                _ = ReconcileEngineGamesAsync();
            return;
        }

        // Plugin ist geladen → Install-Karte weg, Tabs zeigen.
        InstallCard = null;
        ShowInstallCard = false;

        var detected = FindDetectedGameFor(loaded, entry);
        if (detected is null)
        {
            ShowPluginTabs = false;
            ShowContentPlaceholder = true;
            ContentPlaceholderText = "Plugin liefert für dieses Spiel keine Ansichten.";
            return;
        }

        // Cache-Reuse: wenn wir fuer denselben renderKey schon Tabs erzeugt
        // haben, wiederverwenden statt neu bauen. Damit ueberleben die
        // Plugin-VMs (Nexus-Rows mit Covers, Screenshot-Thumbnails,
        // Detail-Caches) einen Wechsel zu einem anderen Game und zurueck.
        if (_pluginTabsCache.TryGetValue(currentKey, out var cached))
        {
            _tabCacheOrder.Remove(currentKey);
            _tabCacheOrder.Add(currentKey);
            PluginTabs = cached;
            ShowPluginTabs = cached.Count > 0;
            ShowContentPlaceholder = cached.Count == 0;
            if (cached.Count == 0)
                ContentPlaceholderText = "Plugin geladen, aber ohne sichtbare Tabs.";
            return;
        }

        var tabs = new ObservableCollection<TabItem>();
        foreach (var contribution in loaded.Plugin.GetTabContributions(detected)
                     .Where(c => c.IsVisible(detected))
                     .OrderBy(c => c.Order))
        {
            try
            {
                var view = contribution.CreateView(detected, loaded.Host);
                tabs.Add(new TabItem
                {
                    // Name = "PluginTab_<id>" macht den Tab per REST-API
                    // (/events/click mit elementId="PluginTab_catalog") ansprechbar.
                    // Tag hält die reine tabId für den kommenden /select-tab-Endpoint.
                    Name = $"PluginTab_{contribution.Id}",
                    Tag = contribution.Id,
                    Header = string.IsNullOrEmpty(contribution.Icon)
                        ? contribution.Label
                        : $"{contribution.Icon}  {contribution.Label}",
                    Content = view,
                });
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "Tab-Contribution {Id} vom Plugin {Plugin} warf beim CreateView",
                    contribution.Id, loaded.Manifest.Id);
            }
        }
        // Alte Cache-Eintraege dieses Games (mit anderem renderKey — z.B.
        // vor einem Plugin-Update) verwerfen, damit der Cache nicht ewig
        // waechst.
        var stalePrefix = entry.Key + "|";
        foreach (var k in _pluginTabsCache.Keys.Where(k => k.StartsWith(stalePrefix, StringComparison.Ordinal)).ToList())
        {
            if (_pluginTabsCache.Remove(k, out var staleTabs))
            {
                _tabCacheOrder.Remove(k);
                DisposeTabs(staleTabs);
            }
        }
        _pluginTabsCache[currentKey] = tabs;
        _tabCacheOrder.Remove(currentKey);
        _tabCacheOrder.Add(currentKey);
        TrimTabCache();

        PluginTabs = tabs;
        ShowPluginTabs = tabs.Count > 0;
        ShowContentPlaceholder = tabs.Count == 0;
        if (tabs.Count == 0)
            ContentPlaceholderText = "Plugin geladen, aber ohne sichtbare Tabs.";
    }

    private static bool MatchesGame(LoadedPlugin loaded, GameEntry entry)
        => FindDetectedGameFor(loaded, entry) is not null;

    /// <summary>Findet den DetectedGame eines geladenen Plugins, der zur
    /// gegebenen Sidebar-Kachel passt. Match-Regeln (v1.9.1+):
    /// (1) SteamAppId gleich, ODER (2) beide sind Manual und InstallDir
    /// stimmt überein (case-insensitive) — deckt Engine-basierte Kacheln ab.</summary>
    private static DetectedGame? FindDetectedGameFor(LoadedPlugin loaded, GameEntry entry)
    {
        if (entry.Source.SteamAppId is int appId)
        {
            var byApp = loaded.DetectedGames.FirstOrDefault(dg => dg.Target.SteamAppId == appId);
            if (byApp is not null) return byApp;
        }
        if (entry.Source.Source == Services.Games.DiscoveredGameSource.Manual
            && !string.IsNullOrEmpty(entry.Source.InstallDir))
        {
            return loaded.DetectedGames.FirstOrDefault(dg =>
                string.Equals(dg.InstallDir, entry.Source.InstallDir, StringComparison.OrdinalIgnoreCase));
        }
        return null;
    }

    private PluginIndexEntry? FindIndexEntryFor(GameEntry entry)
        => IndexEntriesFor(entry).FirstOrDefault();

    /// <summary>Alle Index-Plugins die dieses Game bedienen: erst per
    /// SteamAppId, dann per Engine-Slug. Der Engine-Weg ist der einzige fuer
    /// Manual-Kacheln aus dem Ordner-Scan-Wizard (kein SteamAppId) — ohne ihn
    /// bleibt fuer jedes Ren'Py-Spiel die Install-Karte aus.</summary>
    private IEnumerable<PluginIndexEntry> IndexEntriesFor(GameEntry entry)
        => PluginIndexMatcher.EntriesFor(_indexCache, entry.Source);

    [RelayCommand]
    private void OpenSettings()
    {
        var vm = _services.GetRequiredService<SettingsWindowViewModel>();
        var window = new SettingsWindow { DataContext = vm };
        var owner = MainWindow();
        if (owner is not null) window.ShowDialog(owner); else window.Show();
    }

    [RelayCommand]
    private void OpenAbout()
    {
        var window = new AboutWindow(_hostUpdate);
        var owner = MainWindow();
        if (owner is not null) window.ShowDialog(owner); else window.Show();
    }

    /// <summary>Startet das aktuell ausgewählte Spiel. Wird sowohl vom
    /// „▶ Starten"-Button im Content-Header als auch vom Sidebar-
    /// Doppelklick aufgerufen. Ohne Selection ein No-Op.
    ///
    /// <para>Ab v1.10.0: Plugins können <see cref="IGameLauncher"/> implementieren
    /// und den Launch übernehmen (z. B. RenPyAssist öffnet bei verfügbarem
    /// Update den f95zone-Thread statt das Spiel zu starten). Der Host-
    /// Default-Launch (Steam-URL / Executable) läuft nur wenn das Plugin
    /// false zurückgibt oder kein Launcher-Plugin geladen ist.</para></summary>
    [RelayCommand]
    private async Task LaunchSelectedGameAsync()
    {
        if (SelectedGame is null) return;

        // Plugin-Launcher-Delegation
        var loaded = _pluginActivator.Loaded.FirstOrDefault(l => MatchesGame(l, SelectedGame));
        if (loaded?.Plugin is IGameLauncher launcher)
        {
            var detected = FindDetectedGameFor(loaded, SelectedGame);
            if (detected is not null)
            {
                try
                {
                    if (await launcher.TryLaunchAsync(detected, default))
                    {
                        Log.Info("LaunchSelectedGame: Plugin {Id} hat übernommen", loaded.Manifest.Id);
                        return;
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn(ex, "IGameLauncher {Id}.TryLaunchAsync warf — Fallback auf Host-Launch",
                        loaded.Manifest.Id);
                }
            }
        }

        // Host-Default-Launch
        var result = _launcher.Launch(SelectedGame.Source);
        StatusText = result.Message;
        Log.Info("LaunchSelectedGame (Host-Default) → {Ok}: {Msg}", result.Success, result.Message);
    }

    public bool CanLaunchSelected =>
        SelectedGame is not null &&
        // Plugin-Launcher hat immer eine Chance, Host-Fallback nur bei bekannten Startpfaden
        (_pluginActivator.Loaded.Any(l => l.Plugin is IGameLauncher && MatchesGame(l, SelectedGame))
         || SelectedGame.Source.SteamAppId is not null
         || !string.IsNullOrWhiteSpace(SelectedGame.Source.ExecutablePath));

    [RelayCommand]
    private void OpenPluginUpdates()
    {
        // Ehemals „Plugin-Updates" — jetzt kompletter Plugin-Manager mit
        // Updates-Sektion + installierte-Plugins-Sektion + Uninstall.
        var vm = _services.GetRequiredService<PluginUpdatesViewModel>();
        var window = new PluginUpdatesWindow { DataContext = vm };
        var owner = MainWindow();
        if (owner is not null) window.ShowDialog(owner); else window.Show();
    }

    /// <summary>v1.24.0: Konflikt-Fenster pro Spiel — findet Dateien die
    /// mehrere installierte Mods beanspruchen. Ruft <see cref="IConflictScanner"/>
    /// der wiederum alle geladenen Plugins die <see cref="IConflictSource"/>
    /// implementieren nach ihrer File-Karte fragt.</summary>
    [RelayCommand]
    private void OpenConflicts(GameEntry? entry)
    {
        if (entry is null) return;
        var scanner = _services.GetRequiredService<IConflictScanner>();
        var vm = new ConflictsViewModel(scanner, entry.Key, entry.DisplayName);
        var owner = MainWindow();
        var window = new ConflictsWindow { DataContext = vm };
        if (owner is not null) window.ShowDialog(owner); else window.Show();
    }

    /// <summary>v1.23.0: Backup-Fenster pro Spiel — aggregiert Snapshots
    /// aus allen geladenen Plugins fuer diese GameKey. Restore/Delete
    /// direkt im Fenster; Snapshot-Creation macht das jeweilige Plugin.</summary>
    [RelayCommand]
    private void OpenBackups(GameEntry? entry)
    {
        if (entry is null) return;
        var backup = _services.GetRequiredService<IBackupService>();
        var vm = new BackupsViewModel(backup, _pluginActivator,
            gameKey: entry.Key, gameName: entry.DisplayName);
        var owner = MainWindow();
        var window = new BackupsWindow { DataContext = vm };
        if (owner is not null) window.ShowDialog(owner); else window.Show();
    }

    /// <summary>v1.22.0: Aggregierte Uebersicht aller Spiele mit
    /// ausstehenden Mod-Updates — Ersatz fuer den bisher nicht-klickbaren
    /// Header-TextBlock. Klick auf eine Row selektiert das Spiel in der
    /// Sidebar und schliesst das Fenster.</summary>
    [RelayCommand]
    private void OpenModUpdatesOverview()
    {
        var owner = MainWindow();
        var window = new ModUpdatesOverviewWindow();
        var vm = new ModUpdatesOverviewViewModel(
            _allGames.Where(g => g.HasUpdates),
            g => { SelectedGame = g; },
            () => window.Close());
        window.DataContext = vm;
        if (owner is not null) window.ShowDialog(owner); else window.Show();
    }

    [RelayCommand]
    private async Task AddGameAsync()
    {
        var vm = new AddGameDialogViewModel(_manual);
        var dialog = new AddGameDialog { DataContext = vm };
        var owner = MainWindow();
        if (owner is not null) await dialog.ShowDialog(owner); else dialog.Show();
        if (vm.Result is not null)
        {
            var entry = new GameEntry(new DiscoveredGame(
                Key: $"manual:{vm.Result.Id}",
                DisplayName: vm.Result.DisplayName,
                InstallDir: vm.Result.InstallDir,
                SteamAppId: vm.Result.SteamAppId,
                ManualId: vm.Result.Id,
                CustomCoverPath: vm.Result.CoverPath,
                Source: DiscoveredGameSource.Manual));
            _allGames.Add(entry);
            _ = LoadCoversAsync(new[] { entry }, default);
            ApplyFilterAndSort();
            SelectedGame = entry;
        }
    }

    /// <summary>Öffnet den „🎮 Ordner mit Spielen scannen"-Wizard: User wählt
    /// einen Root, der Host scannt nach Engine-Signaturen und legt pro
    /// gefundenem Container eine eigene Sidebar-Kachel an. Die Kacheln matchen
    /// gegen <c>PluginManifest.Targets[].Engine</c>.</summary>
    [RelayCommand]
    private async Task AddFolderCollectionAsync()
    {
        var detector = _services.GetRequiredService<FolderEngineDetector>();
        var vm = new AddFolderCollectionDialogViewModel(_manual, detector);
        var dialog = new AddFolderCollectionDialog { DataContext = vm };
        var owner = MainWindow();
        if (owner is not null) await dialog.ShowDialog(owner); else dialog.Show();
        if (vm.Results.Count == 0) return;

        var newEntries = new List<GameEntry>();
        foreach (var r in vm.Results)
        {
            var entry = new GameEntry(new DiscoveredGame(
                Key: $"manual:{r.Id}",
                DisplayName: r.DisplayName,
                InstallDir: r.InstallDir,
                SteamAppId: r.SteamAppId,
                ManualId: r.Id,
                CustomCoverPath: r.CoverPath,
                Source: DiscoveredGameSource.Manual,
                ExecutablePath: r.ExecutablePath,
                Engine: r.Engine));
            _allGames.Add(entry);
            newEntries.Add(entry);
        }
        _ = LoadCoversAsync(newEntries.ToArray(), default);

        // Plugin-Aktivierung neu triggern — die neuen Engine-Kacheln matchen
        // gegen Plugins mit passendem GameTarget.Engine.
        await ActivatePluginsAsync(default);
        ApplyFilterAndSort();
        if (newEntries.Count > 0) SelectedGame = newEntries[0];
        EnqueueToast($"🎮 {newEntries.Count} Spiel(e) importiert", NotificationLevel.Success);

        // v1.28.1: Das passende Engine-Plugin fehlt nach einem frischen Setup
        // schlicht — hier direkt nachziehen statt den User erst auf eine
        // Install-Karte klicken zu lassen.
        await AutoInstallMissingPluginsAsync(default);
    }

    /// <summary>Sidebar-Kontextmenü „🖼 Kachelbild ändern": öffnet File-Picker,
    /// kopiert das ausgewählte Bild in <see cref="AppPaths.UserCoverDir"/>
    /// (persistent an unser Cache-Verzeichnis gebunden, damit der User seinen
    /// Ordner umbenennen kann), speichert den Pfad in
    /// <see cref="AppSettings.CustomGameCovers"/> und lädt die Kachel neu.</summary>
    [RelayCommand]
    private async Task ChangeCoverAsync(GameEntry? entry)
    {
        if (entry is null) return;
        var dialog = _services.GetRequiredService<IDialogService>();
        var picked = await dialog.PickFileAsync(
            $"Neues Kachelbild für '{entry.DisplayName}'",
            ("Bilder", new[] { "*.png", "*.jpg", "*.jpeg", "*.webp" }));
        if (string.IsNullOrWhiteSpace(picked) || !File.Exists(picked)) return;

        try
        {
            var ext = Path.GetExtension(picked);
            if (string.IsNullOrEmpty(ext)) ext = ".png";
            // Ordner-fähigen Dateinamen aus dem Key ableiten (steam:2300320 → steam_2300320)
            var safeKey = entry.Key.Replace(':', '_').Replace('/', '_');
            var target = Path.Combine(AppPaths.UserCoverDir, safeKey + ext);
            File.Copy(picked, target, overwrite: true);

            _settings.Update(s =>
            {
                s.CustomGameCovers ??= new Dictionary<string, string>();
                s.CustomGameCovers[entry.Key] = target;
            });

            // Cover neu laden — LoadCoversAsync bevorzugt den CustomGameCovers-Override.
            _ = LoadCoversAsync(new[] { entry }, default);
            StatusText = $"Kachelbild für '{entry.DisplayName}' aktualisiert.";
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Kachelbild-Wechsel für {Key} fehlgeschlagen", entry.Key);
            StatusText = $"Fehler beim Setzen des Kachelbilds: {ex.Message}";
        }
    }

    /// <summary>Sidebar-Kontextmenü „🗑 Aus KroModIx entfernen": bei Manual-
    /// Games löscht es den Eintrag in <see cref="ManualGamesService"/>; bei
    /// Steam-Games (die von der Steam-Discovery immer wieder auftauchen)
    /// merkt es sich den Key in <see cref="AppSettings.HiddenGameKeys"/>
    /// als Blacklist, sodass die Discovery ihn beim nächsten Refresh
    /// ausfiltert. In beiden Fällen sofortige UI-Aktualisierung.</summary>
    [RelayCommand]
    private async Task RemoveGameAsync(GameEntry? entry)
    {
        if (entry is null) return;
        var dialog = _services.GetRequiredService<IDialogService>();
        var confirmed = await dialog.ConfirmAsync(
            title: "Spiel entfernen?",
            message: entry.IsManual
                ? $"'{entry.DisplayName}' aus KroModIx entfernen? Der Manual-Eintrag wird gelöscht (Steam-Ordner bleibt unangetastet)."
                : $"'{entry.DisplayName}' aus der Sidebar ausblenden? Steam-Discovery findet es beim nächsten Refresh wieder — die Blacklist verhindert die Anzeige.");
        if (!confirmed) return;

        if (entry.IsManual && !string.IsNullOrEmpty(entry.Source.ManualId))
        {
            _manual.Remove(entry.Source.ManualId);
        }
        else
        {
            _settings.Update(s =>
            {
                s.HiddenGameKeys ??= new List<string>();
                if (!s.HiddenGameKeys.Contains(entry.Key))
                    s.HiddenGameKeys.Add(entry.Key);
            });
        }
        _allGames.Remove(entry);
        if (ReferenceEquals(SelectedGame, entry)) SelectedGame = null;
        ApplyFilterAndSort();
        StatusText = $"'{entry.DisplayName}' entfernt.";
    }

    private static Window? MainWindow() =>
        (Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
}
