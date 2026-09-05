# KroModIx — Entwicklungs-Historie

Ausgelagert aus `CLAUDE.md` am 2026-09-03: Release-Journal und Plugin-Roadmap.
Nicht mehr Teil des Session-Kontexts — hier nachschlagen, wenn die Begruendung
hinter einer aelteren Design-Entscheidung gebraucht wird.

## Aktueller Stand

**Host v1.28.2 — Discovery-Delta meldet Plugins, keine Install-Karte fuer geladene Plugins (2026-09-05):**
- **Regression aus v1.28.1.** Eine Ren'Py-Kachel zeigte statt der Tabs eine „⬇ Installieren"-Karte fuer das laengst installierte Ren'Py Assist. `RenderContentForSelected` hat drei Zweige — Tabs, Install-Karte, Placeholder — und **nur der Placeholder-Zweig** feuert das Reconcile-Sicherheitsnetz (`ReconcileEngineGamesAsync`, v1.24.2). Vor v1.28.1 lieferte `FindIndexEntryFor` fuer Engine-Games immer null (SteamAppId-only), sie landeten also zwangslaeufig im Placeholder-Zweig und heilten sich selbst. Mit dem Engine-Match greift die Install-Karte davor, und ihr `return` sprang das Sicherheitsnetz aus.
- **`PluginIndexMatcher.InstallOfferFor`** (neu): bietet ein Plugin nur an, wenn es NICHT schon geladen ist. Eine Install-Karte fuer ein geladenes Plugin ist immer falsch — der Klick holt dasselbe ZIP ein zweites Mal von GitHub. Der Fall bedeutet nicht „Plugin fehlt", sondern „Plugin ist da, kennt dieses eine Spiel noch nicht".
- **Die Ursache dahinter:** `RefreshDiscoveryAsync` hat neue Spiele in `_allGames` gehaengt, aber nie `PluginActivator.NotifyGameAddedAsync` gerufen — anders als der `ManualGamesService.GameAdded`-Pfad. Damit fehlte das Spiel in `LoadedPlugin.DetectedGames` und `MatchesGame` schlug fehl. Traf real jeden Ordner-Scan-Import, der erst in der Folge-Session als Discovery-Delta ankommt. Der UI-Thread-Lambda ist dafuer jetzt `async` (`Func<Task>`-Ueberladung, Reihenfolge bleibt erhalten).
- **Log-Pfad gefixt** — und genau deshalb lief die Diagnose ueber Datei-Zeitstempel statt ueber Logs: `nlog.config` hatte `fileName="logs/KroModIx.log"`, relativ. NLog loest das gegen das **Arbeitsverzeichnis** auf; beim AppImage ist das der read-only Mount bzw. der Startordner. Ergebnis: gar kein Log, genau dann wenn man eins braucht. `Program.ConfigureLogDirectory()` setzt die NLog-Variable `logDir` jetzt vor dem ersten Logger-Aufruf auf `AppPaths.StateRoot/logs` (Linux `~/.local/state/KroModIx/logs/`). Faellt still auf den alten Wert zurueck, wenn das Anlegen scheitert — ein kaputter Log-Pfad darf nie den Start verhindern.
- **4 neue Tests** (`InstallOfferFor`: bietet fehlendes an, bietet geladenes nicht, case-insensitive Plugin-Id, andere geladene Plugins stoeren nicht). Suite 184/184 gruen.
- **E2E** in isolierter Sandbox mit genau der gemeldeten Konstellation (Sidebar-Cache kennt 1 Spiel, `manual-games.json` 2, Plugin installiert): `Discovery-Refresh: +14 / -0` → `OnGameAddedAsync fuer .../Streets Of Sorcery aufgerufen` → `Ren'Py-Kachel zur Laufzeit uebernommen: 'Streets Of Sorcery' (sub=StreetsOfSorcery-1.9b-pc, v1.9b)`, keine Install-Karte. Das Log lag am neuen Ort.
- **Ren'Py Assist v0.22.0** gehoert dazu, damit das Spiel auch in der Plugin-Registry landet; der Host-Teil (DetectedGames + keine falsche Karte) wirkt unabhaengig davon.

**Host v1.28.1 — Engine-Matching im PluginIndex + Auto-Install fehlender Plugins (2026-09-03):**
- **Bug: Ren'Py-Kacheln bekamen nie ein Plugin angeboten.** `MainWindowViewModel.FindIndexEntryFor` matchte ausschliesslich ueber `SteamAppId`; Manual-Games aus dem Wizard „🎮 Ordner mit Spielen scannen" haben aber keine, und `kroste.renpyassist` steht mit `"steamAppIds": []` im Index. Folge: statt der Install-Karte kam „Fuer *X* ist kein Plugin verfuegbar" — fuer alle 31 Ren'Py-Container. Der `PluginActivationPlanner` wertete `Targets[].Engine` schon immer aus, nur der Index kannte das Feld nicht.
- **`PluginIndexEntry.Engines`** als Index-Gegenstueck zu `PluginManifest.Targets[].Engine` (`"engines": ["renpy"]` in `KroModIx.PluginIndex/plugins.json`). Fehlt das Feld → leere Liste, reines SteamAppId-Matching wie bisher.
- **`PluginIndexMatcher`** (neu): eine Stelle fuer „welche Index-Plugins bedienen dieses Spiel" — SteamAppId ODER Engine-Slug, case-insensitive. `FindIndexEntryFor`, `CategoriesForGame` und der `PluginState.Available`-Sweep in `RefreshPluginStates` gehen jetzt alle darueber. Nebeneffekt: die Sidebar-Suche findet Ren'Py-Kacheln auch ueber ihre Kategorien (`visual-novel`, `adult`).
- **`PluginAutoInstallService` + `PluginAutoInstallPlanner`** (neu): der Host zieht beim Start jedes Plugin nach, fuer das ein Spiel in der Sidebar steht und das lokal fehlt — der Neuinstallations-Fall (`plugins/` leer, `manual-games.json` + Steam-Library noch da). Download via `PluginInstaller`, danach `PlanSingle` + `ActivateOneAsync` wie in der Install-Karte, also live ohne Restart. Ein Toast nennt die nachinstallierten Plugins.
- **Bewusst konservativ:** nur Plugins zu vorhandenen Spielen (nie „auf Vorrat"), nur Eintraege mit brauchbarer GitHub-`updateSource`, und ein in der Plugin-Verwaltung deinstalliertes Plugin landet in `AppSettings.AutoInstallOptOutPluginIds` — sonst waere „deinstallieren" beim naechsten Start wirkungslos. Ein manueller Re-Install ueber die Install-Karte hebt das Opt-out wieder auf. Gescheiterte Versuche haben 15 min Cooldown (GitHub-Rate-Limit 403 soll keine Anfrage-Schleife ausloesen).
- **Aufruf-Punkte:** nach jedem `LoadPluginIndexAsync` (also auch nach dem `IndexRefreshed`-Background-Refresh), nach dem Ordner-Scan-Import und nach einem Discovery-Refresh der neue Spiele gebracht hat. `SemaphoreSlim`-Gate serialisiert die Laeufe.
- **Settings-Toggle** „Fehlende Plugins zu erkannten Spielen automatisch installieren" (`PluginAutoInstallForMatchedGames`, Default **an**) im Tab „Plugin-Verhalten".
- **15 neue Tests** (`PluginIndexMatcherTests` 7, `PluginAutoInstallPlannerTests` 8). Suite 180/180 gruen.
- **E2E verifiziert** in isolierter `XDG_CONFIG_HOME`-Sandbox (leerer `plugins/`-Ordner + 3 Ren'Py-Container in `manual-games.json`): Start → 9 Kacheln „available" → 7 Plugins automatisch geholt und aktiviert → Ren'Py-Kachel rendert `Loaded=kroste.renpyassist` ohne Neustart.
- **PluginIndex-Repo muss mit:** ohne das gepushte `"engines": ["renpy"]` in `plugins.json` matcht der Host nichts — Host-Release und Index-Push gehoeren zusammen.

**Host v1.24.0 — IConflictScanner-Baukasten + Konflikt-Fenster (2026-08-17):**
- **Neuer Contract `IConflictScanner` + `IConflictSource`** (Contracts v1.24.0): Plugins die eine Mod-File-Karte kennen (BepInEx-DLLs, PAKs, .archive, REDmods etc.) implementieren `IConflictSource.GetOwnedFilesAsync(gameKey)` und liefern eine Liste `ModFileset(ModId, ModDisplayName, RelativeFiles)`. Host aggregiert on-demand und findet Files mit &gt; 1 Owner. `FileConflict(RelativePath, Owners)` mit `ConflictOwner(PluginId, PluginDisplayName, ModId, ModDisplayName)` — der User sieht sofort welche Mods sich streiten und aus welchen Plugins sie kommen.
- **Design „Pull statt Push"**: Plugins liefern on-demand, Host cached nichts. Kein Deployment-Diff-Tracking, kein Zwang zu Push-Notifications bei Install/Uninstall. Wenn ein Plugin `IConflictSource` NICHT implementiert, taucht es einfach nicht in der Konflikt-Liste auf (kein Cross-Cutting-Zwang, alte Plugins bleiben kompatibel).
- **Path-Normalisierung** case-insensitive (Windows-FS + Linux-Mixed) + `\\` → `/` + Trim leading `/`. Fehler einzelner Plugins blocken nicht — Warn ins Log, andere Plugins liefern weiter.
- **Konflikt-Fenster** (`ConflictsWindow`, 820×600): via Sidebar-Kontextmenue „⚠ Konflikte pruefen…" pro Spiel. Zeigt jede Datei mit Owner-Count-Badge (`3x`) + Path in Monospace + Owner-Liste (`Mod-A [DSP], Mod-B [DSP], Mod-C [Cyberpunk]`). Fix bleibt beim User (deaktivieren im Plugin-Tab oder Load-Order aendern) — dieser Baukasten macht nur den Konflikt sichtbar.
- **4 neue Tests** (`HostConflictScannerTests`): No-Overlap, Case-Insensitive-Overlap, Backslash-+-Leading-Slash-Normalisierung, Merge-Across-Plugins. Suite 142/142 gruen.
- **Kein Plugin-Update in dieser Runde** — Contract ist da, Plugins koennen opt-in nachziehen (MinHostVersion 1.24.0). Cyberpunk als Referenz-Migration folgt im naechsten Release.

**Host v1.23.0 — IBackupService-Baukasten + Backups-Fenster (2026-08-17):**
- **Neuer Contract `IBackupService`** (Contracts v1.23.0 via MinVer): `CreateSnapshotAsync(pluginId, gameKey, dirs, label)` packt eine Liste Verzeichnisse als ZIP unter `~/.local/share/KroModIx/backups/<pluginId>/<id>.zip` + `.json`-Meta. `ListSnapshotsAsync(pluginId, gameKey)` sortiert neu→alt. `RestoreSnapshotAsync(id)` extrahiert das ZIP — existierende Ziel-Verzeichnisse werden VOR dem Extract in `<dir>.pre-restore-<ts>/` umbenannt (Doppel-Sicherheitsnetz, User kann selbst zurueck). `DeleteSnapshotAsync` + `PruneAsync(keepLast)` fuer Rotation. `NullBackupService`-Default fuer aeltere Hosts. `HostBackupServiceImpl` mit `SemaphoreSlim(1)`-Gate (kein paralleles Schreiben auf denselben ZIP-Pfad).
- **Design-Entscheidung „kein Auto-Rollback"**: bewusst so — nach dem Snapshot kann der User weitere Aenderungen gemacht haben, ein blindes Rollback ueber die Snapshot-Grenze zerstoert dann bewusste Arbeit. Deshalb: Snapshot ist Sicherheitsnetz, User waehlt selbst im UI welchen er zurueckspielt.
- **Backups-Fenster** (`BackupsWindow`, 760×580): via Sidebar-Kontextmenue „🗄 Backups verwalten…" pro Spiel. `BackupsViewModel` aggregiert Snapshots ueber ALLE geladenen Plugins fuer diese `GameKey` (Multi-Plugin-Spiele wie hypothetisch Skyrim mit mehreren Loader-Plugins sehen alle Snapshots auf einen Blick). Restore + Delete direkt in der Row. `AppPaths.BackupsRoot` unter `StateRoot` (nicht CacheRoot — Backups duerfen bei Cache-Wipe NICHT verloren gehen).
- **5 neue Tests** (`HostBackupServiceTests`): Create → ZIP + Meta persistiert, List filtert by gameKey + neuestes zuerst, Prune behaelt keepLast neueste, Restore ersetzt Ziel + legt .pre-restore- daneben, Delete entfernt Zip + Meta. Nutzt `XDG_STATE_HOME=<TempDir>` Interferenz-Schutz. Suite 138/138 gruen (5 neu).
- **Kein Plugin-Update in dieser Runde** — Plugins koennen den Contract ab jetzt opt-in aufrufen (`_host.Backup.CreateSnapshotAsync(...)` vor Install), MinHostVersion 1.23.0. Migration in DSP/Cyberpunk/Icarus/Schedule I kommt bedarfsweise, nicht als Zwangs-Bump.

**Host v1.22.0 — Aggregierter Mod-Update-View + Plugin-Health-Dashboard (2026-08-17):**
- **Aggregierter Update-View**: der Header-Text „🎮 N Mod-Updates" ist bis v1.21 ein reiner TextBlock — schon lange geplant war „Klick soll die Uebersicht oeffnen". Jetzt ist es ein Ghost-Button der `ModUpdatesOverviewWindow` (720×560) oeffnet. Fenster zeigt pro Spiel eine Card mit Cover (64×86), Name, gruenem „↑N"-Badge, Plugin-Tooltip-Summary und „🎮 Zum Spiel"-Button. Klick auf den Button setzt `SelectedGame` in der Sidebar und schliesst das Fenster — der User springt direkt in den Installiert-Tab wo die Update-Rows zum Installieren stehen. Kein Bulk-Install-Button (der wuerde einen neuen `IUpdateNotifier.InstallAllPendingAsync`-Contract brauchen und damit einen Contracts-Bump auf jedes Plugin — Scope zu gross fuer diese Runde). `ModUpdatesOverviewViewModel` bekommt die `_allGames.Where(HasUpdates)`-Liste + Select-Callback + Close-Callback ueber den Ctor injiziert, kein DI-Overhead.
- **Plugin-Health-Dashboard**: neuer Settings-Tab „🩺 Plugin-Health" — Read-only-Uebersicht ueber alle geladenen Plugins. Pro Plugin: DisplayName + Version + `bundled`/`user`-Chip, Plugin-Id, Zielspiele-Count (aus `LoadedPlugin.DetectedGames`), Bundle-Groesse (Summe aller Dateien im Plugin-Ordner rekursiv via `Directory.EnumerateFiles(SearchOption.AllDirectories)`), Anzahl Dateien, MinHostVersion, letzter Update-Check-Zeitstempel + Latest-Tag aus dem `PluginUpdateService`-Cache. Refresh-Button rescant. Kein Fehler-Feed (haette einen zentralen `ErrorTracker` gebraucht — bewusst weggelassen, Scope). `PluginUpdateService.TryGetCachedRelease(pluginId)` neu als Public-API fuer den Cache-Lookup. `SettingsWindowViewModel` exposed `PluginHealth` als Lazy-Property (`_pluginHealth ??= _services.GetRequiredService<PluginHealthViewModel>()`) damit Nutzer die den Tab nie oeffnen keinen Directory-Scan-Overhead haben.

**M3–M5 (Live-Install + LS25 + Icarus) — abgeschlossen mit v0.3.0:**
- **Steam-Tools-Filter** (`SteamGameFilter`): Steam Linux Runtimes, alle Proton-Versionen 4.11–10.0, Steamworks Common Redistributables werden aus der Sidebar ausgeblendet. Bekannte AppIds als Blacklist + Namens-Präfixe (`Steam Linux Runtime`, `Proton `, `Steamworks Common Redistributables`) für neue/unbekannte Versionen. Auf Lars' Bazzite: 32 Manifeste → 8 Tools weg → 24 echte Spiele.
- **PluginIndex-Anbindung** (M4): Meta-Repo `KroModIx/KroModIx.PluginIndex` mit `plugins.json`; `PluginIndexService` lädt es beim App-Start (24 h Cache, Stale-Fallback bei Netzfehler). Matcht `steamAppIds` gegen installierte Spiele → **umrandeter goldener Stern** (`PluginState.Available`) auf der Sidebar-Kachel.
- **Install-Karte** (M4): Klick auf Kachel mit umrandetem Stern → Content-Bereich zeigt Karte „Plugin verfügbar. ⬇ Installieren". `PluginInstaller` löst das neueste GitHub-Release-Asset (ZIP), entpackt es nach `~/.config/KroModIx/plugins/<id>/`, `PluginActivator.ActivateOneAsync` bringt es **live in den laufenden Prozess** (kein App-Restart), Stern wechselt sofort zu gefüllt, TabHost rendert die Plugin-Tabs.
- **Erste zwei echte Game-Plugins** (M3 + M5):
  - **`KroModIx/KroModIx.Plugin.LS25` v0.1.0**: Extraktion des LS-ModManager-Kerns (ModDescReader für modDesc.xml, ModInstallService mit ZIP-Install/Enable/Disable/Uninstall via `.zip.disabled`-Rename). `Ls25PathResolver` nutzt vom Host geliefertes `DetectedGame.UserDataDir` (Proton-Docs auf Linux, Documents/My Games auf Windows). Tab „Installiert" mit Toolbar (Install-ZIP, Refresh, Ordner öffnen, Toggle, Uninstall mit Confirm). Katalog/KI/Backup in v0.2–v0.5 der Plugin-Roadmap.
  - **`KroModIx/KroModIx.Plugin.Icarus` v0.1.0**: PAK-Mod-Verwaltung für Icarus (Unreal Engine). `IcarusPathResolver` baut `<InstallDir>/Icarus/Content/Paks/~mods/`. Tab „Installiert" analog LS25 mit .pak/.pak.disabled-Toggle.
- Tests: 25 grün (+9 SteamGameFilterTests seit v0.2.0).

**M2 (Steam-Discovery + Sidebar + Plugin-Loading) — abgeschlossen mit v0.2.0:**
- `KroModIx.Plugin.Contracts` inhaltlich gefüllt (flacher Namespace, C# 12 Records + Interfaces): `PluginManifest` (Schema 1, System.Text.Json), `PluginMetadata`, `PluginUpdateSource`, `GameTarget`, `DetectedGame`, `Platforms`, `RuntimeKind`, `GameSource`; `IGameModPlugin`, `IGameTabContribution`; Host-Contracts `IHostServices`, `ISecretProtection`, `IDialogService`, `INotificationSink`, `IProgressScope`, `ILocalization`, `IHostShell`. Wird beim Host-Release als NuGet-Package auf GitHub Packages veröffentlicht.
- Steam-Discovery: `SteamLibraryService` (extrahiert aus LS-ModManager `ModPathService`, generisiert — enumeriert Library-Roots, parst `appmanifest_*.acf`, findet Proton-Präfixe, deduped nach AppId gegen Bazzite-`/var/home`-Symlink-Duplikate). Auf Lars' Bazzite-System: 32 Steam-Spiele in 6 Library-Roots (Home + 2 externe Platten mit Symlink-Split).
- `GameCoverService`: 4-stufige Cover-Auflösung Steam-Cache → Steam-CDN → User-Custom → null, Cache in `~/.cache/KroModIx/game-covers/`.
- `ManualGamesService`: JSON-Persistenz atomar in `~/.config/KroModIx/manual-games.json` mit `.broken`-Backup.
- `GameDiscoveryService`: verheiratet Steam + Manual, dedupliziert auf identische SteamAppId (Steam gewinnt, Manual-Eintrag bleibt persistiert aber nicht doppelt sichtbar).
- Plugin-Loader-Kette: `PluginRegistryScanner` (findet plugin.json OHNE Assembly-Load), `PluginActivationPlanner` (matcht SteamAppIds + `AlwaysActivePluginIds`, prüft `MinHostVersion`, löst Konflikte via höhere SemVer), `PluginActivator` (`Assembly.LoadFrom` ohne LoadContext — Checkmk-Muster; unterstützt Runtime-Aktivierung für M4).
- Host-Impls der Plugin-Contracts: `HostServicesImpl` (plugin-scoped Logger/PluginDataDir/PluginCacheDir + Proxy-aware HttpClient-Factory), `HostShellImpl`, `LocalizationBridge`, `DialogServiceImpl` (StorageProvider für File/Folder-Picker + eigener Confirm-Dialog), `NotificationSinkImpl`, `StatusProgressCoordinator`.
- MainWindow-Sidebar mit **großen Cover-Kacheln** (176×238, Portrait 600×900 Steam-Library-Look), Suchfeld, „nur mit Plugin"-Toggle, „➕ Spiel hinzufügen"-Button, Sortierung Plugin-Spiele zuerst + alphabetisch, gefüllter goldener ★ bei Spielen mit geladenem Plugin. Content-Bereich mit TabControl für Plugin-Tabs oder Placeholder.
- `AddGameDialog`: Formular Name/Verzeichnis/Executable/Cover/optional SteamAppId, File-Picker für alle drei Pfade.
- `LastSelectedGameId` in Settings persistiert und beim Start wiederhergestellt.
- Release-Workflow um `publish-contracts`-Job erweitert: `dotnet pack` + `dotnet nuget push` auf `https://nuget.pkg.github.com/KroModIx/index.json` mit `GITHUB_TOKEN` (packages:write).
- Tests: 16 grün — `AppPathsTests`, `SecretProtectionTests`, `PluginManifestTests`, `PluginActivationPlannerTests`, `ManualGamesServiceTests`.

**Begleit-Repo `KroModIx/KroModIx.Plugin.Dummy` (v0.1.0):** minimales Plugin als End-to-End-Beweis. Zwei Tabs („Hello", „Info") mit Kontext-Info + drei Test-Aktionen (Log, Notification, HTTP-Probe). Targets: CS2 (730), TF2 (440), Proton Experimental (1493710) als Linux-Dev-Fallback. Referenziert `KroModIx.Plugin.Contracts` als PackageReference aus GitHub Packages, nuget.config mit Package-Source-Mapping (Pflicht bei CPM+multi-source).

**M4.5 (PluginUpdateService) — abgeschlossen mit v0.4.0:**
- `PluginUpdateService`: prüft pro geladenem Plugin die aktuelle Version im `updateSource`-Repo auf GitHub. Async-Check läuft im Hintergrund am App-Start; `UpdatesChanged`-Event synchronisiert das MainWindow-Badge.
- Update-Badge im MainWindow-Header (nur sichtbar wenn Updates > 0, zeigt „↑ N", Kroste-Akzentfarbe). Klick öffnet `PluginUpdatesWindow` mit Liste (Alte→Neue Version, ⬇ Installieren pro Zeile, „Jetzt prüfen"-Button).
- Install-Flow: Download ZIP → Sibling-Staging-Ordner → File.Copy overwrite ins Plugin-Verzeichnis. Auf Windows: geladene DLL lockt sich; Fallback legt `.dll.new` daneben. Nach Install: Restart-Hint-Banner in gelb-rot im Fenster („⚠ Bitte die App neu starten…").
- End-to-End verifiziert mit lokal auf 0.1.0 gepinnt Dummy-Plugin vs. GitHub v0.1.1 → Update-Check meldet 1 verfügbares Update.

**M5–M6 (Icarus + Satisfactory) — abgeschlossen mit v1.5.x:**
- **`KroModIx/KroModIx.Plugin.Icarus`**: Nexus-Katalog, PAK-Manual + Steam-Workshop, Premium-Direct-Download.
- **`KroModIx/KroModIx.Plugin.Satisfactory`**: ficsit.app GraphQL-Katalog, `.smod`-Direct-Download, `.uplugin`-Manifest-Parsing.

**M7 (Ren'Py-Plugin) — abgeschlossen mit v1.9.0 (Host) + v0.10.1 (Plugin):**
- **`KroModIx/KroModIx.Plugin.RenPyAssist`**: f95zone-Anbindung mit Login, Sub-Path-Rotation für Updates, Save-Kopie beim Update, ZIP-Archivierung + Auto-Cleanup alter Sub-Ordner, RPA-Archive-Browser + Save-Editor + Media-Preview mit **Inline-Video-Playback** (ffmpeg-MJPEG-Stream, portiert aus RenPack — kein LibVLC), KrosteMod-Pipeline (Walkthrough/Cheat/Rename/Translate), KI-Übersetzung der Beschreibungen, Container-Ordner-Rename mit Host-Integration.
- **Host-Erweiterungen für RenPyAssist:**
  - v1.9.0: `GameTarget.Engine` als init-Property + engine-basiertes Matching für Manual-Games ohne SteamAppId
  - v1.9.3: `IHostServices.TrySetManualGameCover` — Plugin propagiert Cover an Host-Sidebar
  - v1.9.4: `PluginUpdateService`-Race-Guard (Semaphore) gegen Update-Row-Duplikate
  - v1.9.5: `TrySetManualGameCover`-Event immer feuern (Cover-Refresh nach Re-Save mit gleichem Path)
  - v1.10.0: `GameUpdateInfo.InstallDir` — engine-Multi-Tile-Match für Sidebar-Update-Badge
  - v1.10.1: `IHostServices.RequestUpdateBadgeRefreshAsync` — Plugin triggert Sidebar-Refresh nach lokalen State-Changes
  - v1.10.2: **Plugin-Update-Cache** — persistenter JSON-Cache in `~/.config/KroModIx/plugin-update-cache.json` mit Rate-Limit-Fallback (GitHub 60 req/h ohne Token). Bei 403 wird der Cache als Fallback genutzt statt `_available` zu leeren. Optionaler `GITHUB_TOKEN`-Env-Var-Support (5000 req/h).
  - v1.10.3: `IHostServices.TryRenameManualGame` + `ManualGameRenamed`-Event — Plugin kann Container-Ordner umbenennen, Host re-keyed Sidebar-Kachel + Manual-Games-Store atomar und baut die Detail-View neu.
  - v1.10.4: Suchfilter im PluginUpdates-Window — TextBox filtert live beide Sektionen (Verfügbare Updates + Installierte Plugins) nach DisplayName oder PluginId.
  - v1.11.0: PluginIndex-Kategorien — optionales `categories`-Feld pro Plugin im `plugins.json`, gerendert als Chips im InstallCard.
  - v1.12.0: **Multi-Host-Setup** — `HostProfileService` exportiert/importiert Plugin-Liste + Manual-Games als JSON zum Nachziehen auf einem zweiten Rechner. Kein Auto-Sync (rclone/git-Backend wäre zu komplex für seltenen Use-Case).
  - v1.12.1: Plugin-Update-Install crash 0x80131130 gefixt — DLLs werden als `<name>.dll.new` abgelegt, `PluginRegistryScanner.PromotePendingUpdates()` beim App-Start renamed sie vor dem Plugin-Load.
  - v1.12.2: `OllamaProvider.CompleteAsync` nutzt jetzt `stream: true` + `HttpCompletionOption.ResponseHeadersRead` — Timeout gilt nur bis first-byte, große Modelle (14B+) hängen nicht mehr am Gesamt-Timeout.
  - v1.13.0: **Favoriten + Update-Sortierung** — `AppSettings.FavoriteGameKeys` persistiert, Sidebar sortiert Favoriten > Games mit Update > Plugin-Spiele > Rest. Rechtsklick-Kontextmenü toggelt Favorit. Header-Badge „🎮 N Mod-Updates" zeigt globale Anzahl.
  - v1.14.0: **Nexus-Baukasten** — `IHostServices.Nexus` (Contracts v1.14.0), `HostNexusServiceImpl` portiert aus Icarus. User pflegt Personal-API-Key im Host-Settings-Tab „🌐 Nexus", `NexusApiKeyStore` verschlüsselt via `ISecretProtection`. Alle Nexus-basierten Plugins (Icarus v1.15, Cyberpunk 2077 v0.2+) teilen den Key.

**Neues Plugin: Cyberpunk 2077 (KroModIx/KroModIx.Plugin.Cyberpunk2077):**
- v0.1.0: Installiert-Tab mit Discovery aller fünf gängigen Mod-Typen (Archive, REDmod, CET, RED4ext, redscript). Enable/Disable via `.disabled`-Suffix, Uninstall + Bulk-Aktionen.
- v0.2.0: Nexus-Katalog-Tab (aggregiert latest_added/updated/trending für game_slug `cyberpunk2077`), Cover-Enrichment, Kategorien-Filter.
- v0.3.0: Downloads-Tab + `CyberpunkZipInstaller` mit Auto-Layout-Detection (bekannte ZIP-Root-Präfixe → direktes Extract; Flat-Layout-Fallback für `.archive`-only-ZIPs).
- v0.4.0: `IUpdateNotifier` — REDmod-Version vs Nexus-Katalog-latest, grüner ↑-Badge auf der Cyberpunk-Sidebar-Kachel.

**Icarus-Migration:**
- v1.15.0: eigenen `NexusApiClient`/`NexusSettingsService` durch Adapter auf `_host.Nexus` ersetzt. `NexusSettingsTab` entfernt (User nutzt Host-Settings-Tab „🌐 Nexus"). MinHostVersion 1.7.0 → 1.14.0. Migrations-Toast bei erstem Start wenn Plugin-Key ohne Host-Key existiert.

**RenPyAssist v0.11+ und LS25 v1.13+ Ausbauten:**
- RenPyAssist v0.11.0: Animierte GIF-Cover in Detail-View via `Avalonia.Labs.Gif` — CoverCache persistiert bei GIF-URLs sowohl die konvertierte PNG (Sidebar-Kachel) als auch das Original-GIF nebenbei (Detail-View loopt autoplay).
- RenPyAssist v0.11.1: Screenshot-Timeline im Save-Editor — chronologische Thumbnail-Leiste (128×72) unten am Editor, Klick selektiert den Save. Paralleles Screenshot-Extract mit SemaphoreSlim(4).
- RenPyAssist v0.12.0: KrosteMod Choice-Auto-Expand + Konditional-Cheats — Walkthrough zeigt `if`-Condition als `(K req: …)`-Tag, Cheat-Menu markiert Choice-Gate-Vars mit 🔓.
- LS25 v1.13.0: Bulk-ZIP-Import — Ordner wählen, alle .zip darin werden sequenziell installiert (Progress-Scope im Host-Statusbar).

**Host v1.17.0 + Grosse Uebersetzungs-Runde:**
- Host v1.17.0: `IWorkshopService`-Contract (`workshop/content/<appId>/`-Discovery in allen SteamLibrary-Roots + optional `GetPublishedFileDetails`-Enrichment, default `NullWorkshopService`). `IGameModPlugin.OnGameAddedAsync` Default-Method + `PluginActivator.NotifyGameAddedAsync`: Manual-Add propagiert live an geladene Plugins, kein App-Neustart. `HostUpdateCheckLoopAsync` 24h-Timer + Toast bei neuer Host-Version. Sidebar-Suche matched jetzt auch PluginIndex-Kategorien. `/debug/plugin-games` REST-Endpoint fuer „Plugin nicht sichtbar"-Diagnose. Neues App-Icon `build_icon.py v2` (gestapelte Kroste-Gold-Cards + Stern-Akzent auf Dark-Gradient).
- Cyberpunk v0.10.0: Update-Install-Button pro REDmod-Row (`⬆ vX.Y.Z`, nur wenn UpdateChecker eine neuere Nexus-Version erkennt), REDmod-Deploy-Trigger in der Toolbar (`redmod.exe deploy`, Windows-nativ), Client-side Kategorie-Filter im Katalog, Cover-Loading-Progress im Katalog-Header, Retrofit-Dialog (🔗 Nexus-Match zuweisen) fuer Alt-Mods ohne InstallManifest.
- LS25 v1.14.0: DDS-Preview mit ffmpeg-Fallback (BC7 + exotische DXT-Kompressionen).
- **DE+EN-Uebersetzungs-Retrofit** analog Cyberpunk-Muster (Strings.T-Helper) in vier Plugins parallel via Sub-Agents:
  - LS25 v1.15.0 (128 Keys)
  - Icarus v1.16.0 (164 Keys)
  - Satisfactory v0.6.0 (106 Keys)
  - RenPyAssist v0.14.0 (218 Keys)

**Host v1.18.0 + Zentraler Image-Decoder-Baukasten:**
- Host v1.18.0: `IImageDecoder`-Contract (`_host.Images.DecodeAsync(bytes|stream|path)`) mit Format-Chain (PNG/JPEG/BMP/GIF nativ + WebP/AVIF/DDS/HEIC via ffmpeg-Convert), Magic-Byte-Detection statisch testbar. Bitmap-Instantiation IMMER auf `Dispatcher.UIThread` (Skia hat Thread-Affinity). `NullImageDecoder`-Default fuer Hosts < v1.18. 26 grüne Tests im `KroModIx.Tests`-Projekt (Format-Detection + Empty/Null-Edge-Cases + Cover-Sanity-Check gegen HTML-Login-Wall).
- **Getrieben durch DSP-Cover-Bug**: 4 fehlgeschlagene Fix-Anlaeufe v0.2.1-v0.2.2 (Plugin-lokales WebP-Convert, static row-cache, UI-Thread-Bitmap-Load, IsVisible-Toggle) haben den Bug nicht geloest. DSP v0.3.0 auf `_host.Images` migriert = Bug live gefixt, per Screenshot-Test verifiziert.
- **5 Plugin-Migrationen** (Cross-Cutting-Concern-Bereinigung, parallel via Sub-Agents):
  - DSP v0.3.0 — Cover-Bug LIVE gefixt
  - Cyberpunk v0.11.0 — 5 Bitmap-Ctor-Stellen migriert inkl. ScreenshotViewer
  - Icarus v1.18.0 — 4 ViewModels + PreviewCoverCache
  - LS25 v1.17.0 — 4 ViewModels + `ModPreviewService` (parallele Bytes-API)
  - Satisfactory v0.8.0 — FicsitCoverLoader; **SixLabors.ImageSharp komplett entfernt** (~2 MB Bundle-Ersparnis)
  - RenPyAssist v0.15.0 — 6 Bitmap-Ctor-Stellen; animierter GIF-Pfad `IGifSource` unberuehrt
- **DSP-BepInEx-Bootstrap 4-fach-Iteration** (v0.3.0→v0.3.3): (1) Fehlerhafter Asset-Filter `Unity.IL2CPP` statt v5-Mono, (2) `System.Text.Json`-Deserializer matchte kein snake_case (`tag_name`→`TagName`), (3) GitHub-Anonymous-Rate-Limit 60/h war schon durch, (4) Hartcoded CDN-Fallback-URL statt zweitem API-Call. Alle vier Fallen jetzt im Skill `references/pitfalls.md#github-api-im-plugin` dokumentiert.

**DSP v0.4 + v0.5 + LS25 v1.18.0 Nachlese (2026-08-17):**
- **DSP v0.4.0**: `IUpdateNotifier` — `DspInstallManifestStore` persistiert pro installierter BepInEx-Plugin-DLL ein JSON-Manifest mit `NexusModId`+`NexusVersion`+`OriginalFilename` in `plugin-data/install-manifests/`. `NexusFileNameParser` extrahiert Mod-ID + Version + Name aus Nexus-CDN-Filenames (analog Cyberpunk-Pattern). `DspUpdateChecker` iteriert die Manifests, matcht gegen den Nexus-Katalog und meldet echte Versions-Deltas per `GameUpdateInfo` als grünen ↑-Badge auf der DSP-Kachel. Auto-Check 15s nach Init + Re-Check auf jedes `ModInstalled`-Event.
- **DSP v0.5.0**: Nexus-Mod-Detail-Dialog (780x640, mit Cover, Meta-Row, KI-Panel, Scrollable-Description). `NexusModDetailViewModel` lädt via `_nexus.GetModDetailAsync(GameSlug, ModId)`, HTML→Text-Parser strippt Tags + decodiert Entities. `SummarizeCommand` nutzt `_host.Ai.CompleteAsync` mit sprachabhängigem Prompt via `Strings.T("ai.prompt.summary_system")`. Cover-Enrichment via `CoverCache.GetOrDownloadBytesAsync` + `_host.Images.DecodeAsync`. Details-Button (🔍) in der Nexus-Row zwischen Download und "Open Nexus".
- **LS25 v1.18.0**: Pfim + SkiaSharp **komplett aus dem Plugin-Bundle raus** (~15 MB Ersparnis inkl. nativer libs, Total-Bundle 640 KB). `DdsToPngConverter.cs` gelöscht. `ModDescReader.PreviewPngBytes` → `PreviewBytes` (roh, kann PNG/JPG oder DDS sein); `TryExtractPreview` liefert DDS-Bytes 1:1. `ModPreviewService` nimmt jetzt `IImageDecoder` im Ctor — bei DDS wird `_host.Images.DecodeAsync` gerufen und die Avalonia-Bitmap via `Bitmap.Save(Stream, PngBitmapEncoderOptions.Default)` als PNG in den Cache persistiert. PNG/JPG landen direkt (unverändert). Damit ist LS25 auch für den zentralen Image-Baukasten voll auf `_host.Images` konsolidiert.

**Host v1.21.0 — Rich-HTML-Description-Renderer via Avalonia.HtmlRenderer (2026-08-17):**
- **Anlass**: User-Frage „gibt es doch fertige BBCode/HTML-Parser fuer C# — brauchst du nichts selber bauen". Avalonia.HtmlRenderer 12.0.0 ist aktiv gepflegt und laeuft mit Avalonia 12.1. `CodeKicker.BBCode 5.0.0` wollten wir als BBCode→HTML-Convert-Layer dazunehmen, aber es ist nur `.NET Framework 4.x` (NU1701-Blocker) — daher weiter mit Host-eigenem Regex-BBCode→HTML-Konverter.
- **Contracts v1.21 erweitert**: `IDescriptionParser.ToHtml(bbcodeOrHtml)` (BBCode-Tags werden zu HTML-Aequivalenten aufgeloest: `[b]`→`<strong>`, `[url=..]X[/url]`→`<a href="...">X</a>`, `[img …]URL[/img]`→`<img>`, `[color=..]`→`<span style="color:.."`, `[list][*]Item[/list]`→`<ul><li>Item</li></ul>`) + `Control CreateRichView(html)` liefert einen `HtmlPanel` mit Kroste-CSS (Gold-Accent-Headlines, Selection-faehig, Links via OS-Handler). `NullDescriptionParser` liefert bei aelteren Hosts einen Passthrough-TextBlock.
- **11 neue Tests** (Container/URL/Img/Color/List/HTML-Passthrough/Null), 32/32 Descriptions-Tests + 122/122 Host gesamt. NuGet-Neuzugang: `Avalonia.HtmlRenderer 12.0.0` (~500 KB, nur Host-side — Plugins konsumieren nur die Interface-Rueckgabe).
- **4 Plugin-Migrationen abgeschlossen** (Detail-Dialog rendert Rich-HTML statt Plain-Text): **Schedule I v0.4.0** (PoC-Referenz, Commit `544172d`), **DSP v0.6.3** (Commit `e05a8b5`), **Icarus v1.19.1** (Commit `533f195`), **Cyberpunk v0.12.1** (Commit `29f54d0`). Muster: `[ObservableProperty] Control? _descriptionView` in VM, `_host.Descriptions.ToHtml + CreateRichView` via `Dispatcher.UIThread.InvokeAsync` (Control-Instanziierung UI-Thread-Pflicht), `TextBlock{DescriptionText}` → `ContentControl{DescriptionView}` mit Loading-Fallback via `FuncValueConverter<Control?, bool>(c => c is null)`. Plain-Text bleibt in `DescriptionText` fuer AI-Prompts + Loading-Placeholder. PluginIndex-Updates fuer alle 4 mit-gepusht (Rebase-Runden weil parallel via Sub-Agents in dieselbe Datei). GitHub-Runner waren nach der frueheren 429/502-Wave wieder gesund — kein Release-Retry noetig.

**Host v1.20.0 + Zentraler Description-Parser-Baukasten (2026-08-17):**
- **`IDescriptionParser`-Contract** (Contracts-Package via MinVer an Host-Tag gebunden → **v1.20.0**, initial als „v1.19.0" geplant aber MinVer bindet Contracts automatisch an das Host-Release-Tag): `string ToPlainText(html)` (HTML+BBCode-Mix zu Plain-Text) + `IReadOnlyList<InlineImage> ExtractImages(html)` (BBCode `[img …]URL[/img]` + HTML `<img src=…>`, dokumenten-position-sortiert). Analog `IImageDecoder`-Muster (v1.18) — Cross-Cutting-Concern-Baukasten, gemeinsame Bug-Fixes an einer Stelle. `NullDescriptionParser`-Default fuer Hosts < v1.20 (Passthrough).
- **Host-Impl `HostDescriptionParserImpl`** (KroModIx/Services/Text/) portiert aus dem Schedule-I-v0.2.0-`NexusDescriptionParser`. Container-Tags `[center]/[right]/[b]/[i]/[u]/[size=..]/[color=..]/[font=..]/[quote]/[spoiler]/[code]/[sub]/[sup]/[list]/[credit]/[youtube]` → Inhalt behalten. `[url=..]Text[/url]` → Text. `[img …]URL[/img]` → gedroppt (in Plain-Text) bzw. extrahiert (in ExtractImages). `[line]` → ASCII-Trenner. HTML zusaetzlich: `<br>`, `</p>`, `<[^>]+>` strippen, Entities dekodieren. 18 neue Tests im `KroModIx.Tests`-Projekt (Container-Tags + URL + Img + Line + Real-Fixture aus User-Screenshot + Empty/Null + Entities + ExtractImages BBCode/HTML/Mixed).
- **Getrieben durch Schedule-I-v0.1-BBCode-Bug**: der User sah rohen `[center][url=..]-Muell` im Detail-Dialog. Fix ging zuerst in Schedule I v0.2, aber DSP/Cyberpunk/Icarus haben denselben halben HTML-Only-Parser. Zentraler Baukasten verhindert dass in 4 Plugins parallel derselbe Fix nachgezogen werden muss. Kernprinzip 4 aus dem KroModIx-Plugin-Skill („Wenn Plugin B das Gleiche braeuchte → Host-Contract").
- **4 Plugin-Migrationen** (Cross-Cutting-Concern-Bereinigung, parallel via Sub-Agents):
  - **Schedule I v0.3.0** — `NexusDescriptionParser.cs` + `BBCodeParserTests.cs` **geloescht** (wandern in den Host, Doppelpflege bringt nichts). Aufruf → `_host.Descriptions.ToPlainText`.
  - **DSP v0.6.2** — `HtmlToText`-Methode im `NexusModDetailViewModel` geloescht, using `System.Text.RegularExpressions` weg.
  - **Cyberpunk 2077 v0.12.0** — `Services/HtmlStrip.cs` geloescht, Aufruf umgestellt.
  - **Icarus v1.19.0** — `Services/Nexus/HtmlStrip.cs` geloescht, Aufruf umgestellt.
- Alle vier haben `minHostVersion: "1.20.0"` + Contracts-Package `1.20.0`. PluginIndex-Descriptions synchron aktualisiert (SHA: DSP `311db9d5`, Schedule I `92dafdf`, Cyberpunk `77733dc`, Icarus `6eaba98`). Fun-Fact aus der Runde: Contracts-Package-Version ist an das Host-Release-Tag gekoppelt (MinVer), nicht separat versionierbar — d.h. wenn Host v1.20.0 released wird, ist die Contracts-DLL im GitHub-Packages-Feed automatisch v1.20.0.

**Host v1.19.4 — HostUpdateService Rate-Limit-Fallback (2026-08-17):**
- **Root-Cause (User-Screenshot)**: About-Fenster zeigte Version 1.19.2+..., Klick auf „Auf Updates pruefen" ergab nur `?` (statt `v1.19.3` mit Install-Button). Der `CheckForUpdateAsync`-Handler setzt `?` wenn `LatestVersion` null ist — genau das war der Fall, weil der API-Call `HttpRequestException` warf (Rate-Limit-403) und der catch-Block ohne Fallback null returned.
- **Fix (analog PluginInstaller v1.19.2 + PluginUpdateService v1.19.3)**: `TryChaseLatestAsync` als Rate-Limit-Fallback — `github.com/KroModIx/KroModIx/releases/latest` mit `AllowAutoRedirect=false`, 302-Location-Header enthaelt `/tag/vX.Y.Z`. Konventions-Asset-URL wird gebaut: **verifiziertes Naming `KroModIx-{ver}-win-x64.zip` bzw. `KroModIx-{ver}-x86_64.AppImage`** (nicht `ModManager-*` — habe ich zunaechst falsch geraten und dann via `gh api releases/latest` verifiziert). Damit funktioniert Update-Check + Install-Button auch bei ausgeschoepftem GitHub-Rate-Limit.

**Host v1.19.3 — PluginUpdateService Rate-Limit-Fallback (2026-08-17):**
- **Root-Cause (User-Screenshot)**: Schedule I v0.2.0 released, im PluginManager stand aber „v0.1.0 - Keine Updates verfuegbar". Cache-Entry (`~/.config/KroModIx/plugin-update-cache.json`) hatte `LatestTag: 0.1.0` von 12:39 — vor dem v0.2.0-Release. Grund: `PluginUpdateService.CheckAllAsync` schlug beim naechsten App-Start ins Rate-Limit-403 und blieb beim gecachten alten Tag.
- **Fix (analog PluginInstaller v1.19.2)**: `TryRedirectChaseAsync` als Fallback bei 403 — `github.com/{Repo}/releases/latest` mit `AllowAutoRedirect=false` → 302-Location-Header enthaelt den Tag, kein API-Call. Konventions-Asset-URL wird gebaut. `rateLimited=true` schaltet den ganzen Loop auf Redirect-Chase um, statt weitere 60/h zu verbraten. Zusaetzlich User-Cache manuell auf v0.2.0 gepatcht (Sofort-Effekt beim naechsten Start).

**Host v1.19.2 — Plugin-Install Rate-Limit-Fallback (2026-08-17):**
- **Root-Cause im Log (User-Screenshot)**: nach Klick „Installieren" auf der Schedule-I-Install-Card → „Download fehlgeschlagen — siehe Log". Log zeigt `HTTP 403 rate limit exceeded` — anonymer GitHub-API-Aufruf ist auf 60/h pro IP begrenzt; bei 7 aktiven Plugin-Update-Checks + Host-Check am App-Start ist der User schnell durch. Dieselbe Falle wie beim DSP-BepInEx-Bootstrap (v0.3.3).
- **Fix (`PluginInstaller` Fallback-Chain, analog `MelonLoaderBootstrapper`)**: (1) API-Call mit optional `GITHUB_TOKEN`-Env-Var (5000/h statt 60/h). (2) Bei 403/Netz-Fehler: **Redirect-Chase** auf `github.com/{Repo}/releases/latest` mit `AllowAutoRedirect=false` — der 302-Location-Header enthaelt den Tag, kein API-Call. (3) Direkte CDN-Download-URL aus Repo+Tag+Konventions-Asset-Name (`{RepoBase}-{version}.zip`, das ist das Naming-Schema des Kroste-Release-Workflows). `PluginInstallResult` als neuer Rueckgabe-Type mit `ErrorMessage` — die InstallCard zeigt jetzt konkret was schiefging („HTTP 403 + Fallback lieferte keinen Tag — Tipp: GITHUB_TOKEN setzen oder 1h warten") statt lapidar „siehe Log".

**Host v1.19.1 — PluginIndex-Cache aktualisiert sich selbst (2026-08-17):**
- **Root-Cause (Screenshot vom User)**: Schedule I war in der Sidebar sichtbar (Filter-Fix v1.18.1 wirkte), aber „Fuer 'Schedule I' ist kein Plugin verfuegbar" — obwohl das Plugin released war und der `plugins.json`-Eintrag im PluginIndex-Repo drin. Grund: `PluginIndexService` hatte 24h-TTL auf dem `~/.cache/KroModIx/plugin-index.json`, das lokale File war ~22h alt → Live-Fetch wurde ausgelassen, App sah die neuen Eintraege nicht. Kein manueller Refresh-Weg.
- **Fix (drei-teilig)**: (1) TTL 24h → 6h (aktives Repo mit >1 Plugin-Release pro Monat rechtfertigt haeufigere Checks). (2) `PluginIndexService.RefreshAsync(force)` als expliziter Weg fuer den Plugins-Fenster-„Jetzt pruefen"-Button — ignoriert TTL, feuert `IndexRefreshed`-Event. (3) `TriggerBackgroundRefreshOnce`: beim ersten `GetAsync`-Call einer Session laeuft fire-and-forget ein Fetch im Hintergrund; MainWindowViewModel abonniert `IndexRefreshed` und re-load die Sterne live — der User bekommt neue Plugins spaetestens beim naechsten App-Start ohne warten.
- **Skill-Update `KroModIx-Plugin`**: neuer **Kernprinzip 11 „PluginIndex-Update ist Teil des Release-Rituals"** — nach jedem Plugin-Release (neu oder Version-Bump) MUSS `KroModIx/KroModIx.PluginIndex/plugins.json` im gleichen Zug gepushed werden, sonst zeigt die Sidebar „kein Plugin verfuegbar" trotz Release. Neue `references/plugin-index.md` mit Schema + Feld-Erklaerungen + Ritual + Cache-Reset-Kommando. Description-Frontmatter erweitert damit der Skill bei „Plugin released"-Kontexten sofort greift. Bewusst NICHT automatisiert (Race-Condition bei parallelen Plugin-Releases in dieselbe Datei).

**Host v1.19.0 — Echtes Self-Update im AboutWindow (2026-08-17):**
- Der `HostUpdateService` konnte technisch schon alles (Check + Download + Windows-Batch/Linux-Bash-Installer + Restart), aber das AboutWindow hatte nur den „Auf Updates pruefen"-Button — kein UI-Weg zum tatsaechlichen Install. Nach dem kroste-avalonia-Skill (`references/autoupdate.md`) ist **echtes Self-Update Pflicht**, reine „Version X verfuegbar"-Notification reicht nicht.
- Ergaenzt: Install-Panel im AboutWindow (Sektions-Card, sichtbar nur wenn Update verfuegbar), Buttons „⬇ Update installieren" + „Release-Seite oeffnen", ProgressBar mit %-Label. Confirm-Dialog vor dem Download (Kroste-Standard, keine Silent-Installs). Bei fehlendem Asset fuer die aktuelle Plattform bleibt der Install-Button disabled, nur die Release-Seite geht auf.
- **Pflicht-Falle laut Skill umgesetzt** (siehe DTM v2.3.6-Fall): nach `ApplyUpdateAndRestart` **muss** der Aufrufer die App selbst beenden — sonst wartet das Installer-Skript ewig auf das PID-Ende und der User sieht „Update laedt: 100%"-Haenger. Umsetzung: `Process.GetCurrentProcess().Kill()` als Primaer-Weg (kein `Environment.Exit`, das wuerde blockierende Finalizer triggern), + 1,5s-Fallback via `Environment.Exit(0)` fuer den Headless-Corner-Case. 10 neue L10n-Keys DE+EN. Version 1.18.1 → 1.19.0 (neues User-facing-Feature).

**Host v1.18.1 — Schedule-I-Sichtbarkeits-Fix (2026-08-17):**
- `SteamGameFilter.KnownToolAppIds` hatte `3164500, // Proton 10.0 (vermutlich)` — real ist das Schedule I. Beim v1.18-Steam-Sweep war die Id blind aus einer Liste hoher Ids "geraten" worden; das Wort **"vermutlich"** war schon damals das Warnsignal. Wirkung: Schedule I wurde in der Sidebar ausgeblendet und das ScheduleI-Plugin nie aktiviert, obwohl das Spiel installiert war und das Plugin bereitstand. Fix: Id ersatzlos raus, kein neuer Ratekandidat rein — der Namens-Praefix-Fallback (`"Proton "`) faengt neue Proton-Versionen auch ohne konkrete Id. **Regel als Kommentar im Code**: neue Ids nur nach steamdb.info-Verifikation ergaenzen. Regression-Test-Coverage: 3 neue Cases (Schedule I, DSP, Cyberpunk) im `SteamGameFilterTests`.

**Schedule I v0.1.0 (2026-08-17):**
- Neues Plugin `KroModIx.Plugin.ScheduleI` — MelonLoader-basiert (IL2CPP-Game), 1:1 DSP-Muster geklont und adaptiert. Fakten-Recherche: Nexus-Slug `schedule1` (curl-verifiziert, `scheduleone`/`schedulei` geben 403/404), Executable `Schedule I.exe` mit Space, MelonLoader-Bootstrap via `MelonLoader.x64.zip` vom LavaGang-GitHub-Release (Fallback pinned auf v0.7.3 gegen 60/h-Anonymous-Rate-Limit). MelonLoader-Semantik-Unterschiede zu BepInEx bewusst umgesetzt: `Mods/*.dll` flat statt `BepInEx/plugins/{Name}/`, kein Ordner-Layout, `version.dll`-Marker im Game-Root statt `BepInEx/core/BepInEx.dll`. Alle 5 DSP-Kernprinzipien greifen (Nexus-Katalog + Detail-Dialog mit KI + Downloads-Enricher + InstallManifest + Row-Konsistenz mit Cover/Details/Doppelklick). 11 Tests gruen (Parser Dash + Space + Non-Nexus, Scanner Flat-Only + Marker-Check). PluginIndex-Eintrag registriert.

**DSP v0.6.0 + Skill-Verschaerfung Row-Interaction (2026-08-17):**
- **DSP v0.6.0**: Row-Konsistenz in allen drei Tabs — Downloads + Installiert bekommen jetzt Cover (140×90 aus Nexus-Katalog), Autor/Version/Summary + Details-Button + Doppelklick. Neue Services: `DspNexusRowEnricher` (SemaphoreSlim(4)-throttled Nexus-GetModDetailAsync-Batch fuer eine Liste `IDspEnrichableRow`), `DspNexusDetailLauncher` (shared Owner-Lookup+Show fuer alle drei Tabs), `NexusModDetailViewModel` mit neuem ModId-only-Ctor (fuer Downloads/Installed ohne NexusRow). Downloads-ModId aus `NexusFileNameParser`, Installed-ModId aus `DspInstallManifestStore` (v0.4-Persistenz zahlt sich hier aus). `ListBox.DoubleTapped`-Handler pro Tab, Details-Button `IsEnabled` an `HasNexusMatch` — manuell reinkopierte Dateien ohne ModId bleiben passiv.
- **Skill-Update `KroModIx-Plugin`**: Kernprinzip 6 verschaerft (Cover + Details-Button + Doppelklick in allen drei Tabs sind MUST-HAVE, kein Nice-to-have); neuer **Kernprinzip 10 „Row-Interaction in ALLEN Tabs"** mit den fuenf Pflicht-Bausteinen (Shared-Launcher, ModId-only-Ctor, `ShowDetailCommand`, Details-Button mit `HasNexusMatch`-Bindung, `DoubleTapped`-Handler), ModId-Herkunft je Tab, Enricher-Pattern mit `I<Plugin>EnrichableRow`, Refresh-Trigger mit `CancellationTokenSource`-Cycling. Description-Frontmatter ergaenzt damit der Skill bei „Row/Detail/Doppelklick in Plugin"-Kontexten getriggert wird. Kanonische Referenzen sind Cyberpunk v0.8.3+ und DSP v0.6.0+.

## Roadmap

Nach Lars' Steam-Library-Sweep (2026-08-16) identifizierte
Kandidaten für neue Plugins. Bereits abgedeckt: Cyberpunk 2077, LS25,
Icarus, Satisfactory, RenPyAssist. Neue Kandidaten priorisiert nach
Community-Grösse, Mod-Loader-Reife und Aufwand.

### Tier 1 — Starke Kandidaten (Nexus-Baukasten wiederverwendbar)

- **v1.18: Dyson Sphere Program** (AppId 1366540, Repo `KroModIx.Plugin.DysonSphereProgram`)
  BepInEx-Loader (`BepInEx/plugins/*.dll`), ~500 Nexus-Mods aktiv, klarer
  Ordner-Scan. Muster nahezu identisch zu Cyberpunk — Fork-und-Anpass.
- ~~**v1.19: Schedule I**~~ **✓ erledigt** — `KroModIx.Plugin.ScheduleI`
  v0.1.0 released. MelonLoader-Loader statt BepInEx, ansonsten 1:1
  DSP-Muster (Nexus-Katalog + Detail-Dialog + Downloads + IUpdateNotifier +
  Row-Enricher). Nexus-Slug `schedule1`, AppId 3164500, IL2CPP.
- **v1.20: 7 Days to Die** (AppId 251570, Repo `KroModIx.Plugin.SevenDaysToDie`)
  Direktes `Mods/<ModName>/ModInfo.xml`-Loading, XML+DLL kombiniert,
  riesige Nexus-Präsenz. Vanilla-Loader (kein Extra-Framework).

### Tier 2 — Workshop-Consumers (trivial dank v1.17-Contract)

Jeweils nur ein `WorkshopViewModel` analog Icarus v1.17.

- **v1.21: shapez 2** (AppId 2162800, Repo `KroModIx.Plugin.Shapez2`)
- **v1.22: Captain of Industry** (AppId 1594320, Repo `KroModIx.Plugin.CaptainOfIndustry`)
- **v1.23: Going Medieval** (AppId 1029780, Repo `KroModIx.Plugin.GoingMedieval`)
- **v1.24: Workers & Resources: Soviet Republic** (AppId 784150, Repo `KroModIx.Plugin.SovietRepublic`)

### Tier 3 — Beobachten

- **Space Engineers 2** (AppId 1133870) — Early Access, offizieller Mod-
  Support noch nicht final. Warten bis SDK stabil.
- **Diablo II: Resurrected** (AppId 2536520) — MPQ-Modding komplex,
  PlugY-basiert, braucht eigenen Framework-Wrapper.
- **Vampires: Bloodlord Rising** (AppId 2191500) — Unreal-Nischenspiel,
  kaum Community.

### Nicht empfohlen

Belts of Iron, Enshrouded, Everwind, FOUNDRY, Mineral Mining Simulator,
StarRupture, Survival: Fountain of Youth, Tempest Rising, ZeroSpace
(alle Early Access / kein Mod-Ökosystem) · Batman Arkham Knight
(`.pak`-only, komplex, Legacy) · C&C Zero Hour (Retro, kaum aktiv) ·
OpenTTD (eigenes BaNaNaS-System).

### Nachlese (nach jeder Plugin-Runde)

- **Sprachabhängige AI-Prompts**: neue Plugins gleich sprachabhängig
  bauen (`Strings.T("ai.prompt.summary_system")`), nicht später retrofitten.
- **Workshop-Consumer-Ports für LS25/Icarus/Satisfactory**: Icarus hat
  bereits einen (v1.17.0). LS25 & Satisfactory brauchen keinen (nutzen
  GIANTS ModHub bzw. ficsit.app). Erledigt.

