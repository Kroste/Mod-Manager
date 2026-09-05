using System;
using System.Collections.Generic;
using System.Linq;
using KroModIx.Services.Games;

namespace KroModIx.Services.Plugins;

/// <summary>Ordnet einem discovered Game die passenden PluginIndex-Eintraege zu.
///
/// <para>Zwei Wege, in dieser Reihenfolge:</para>
/// <list type="number">
/// <item><b>SteamAppId</b> — der klassische Weg fuer Steam-Spiele.</item>
/// <item><b>Engine-Slug</b> (v1.28.1) — der einzige Weg fuer Manual-Kacheln aus
///   dem Wizard „🎮 Ordner mit Spielen scannen". Die legt pro Container-Ordner
///   ein Manual-Game mit <c>Engine="renpy"</c> und OHNE SteamAppId an; ohne
///   Engine-Match fand der Host kein Index-Plugin und zeigte statt der
///   Install-Karte „Fuer ... ist kein Plugin verfuegbar".</item>
/// </list>
///
/// <para>Das Index-Gegenstueck ist <see cref="PluginIndexEntry.Engines"/> — es
/// spiegelt <c>PluginManifest.Targets[].Engine</c>, das der
/// <see cref="PluginActivationPlanner"/> schon immer ausgewertet hat.</para>
/// </summary>
public static class PluginIndexMatcher
{
    /// <summary>Alle Index-Plugins die <paramref name="game"/> bedienen —
    /// leere Sequenz wenn keins passt oder kein Index geladen ist.</summary>
    public static IEnumerable<PluginIndexEntry> EntriesFor(
        PluginIndex? index, DiscoveredGame game)
    {
        if (index is null) return Array.Empty<PluginIndexEntry>();
        var appId = game.SteamAppId;
        var engine = game.Engine;
        return index.Plugins.Where(p =>
            (appId is int id && p.SteamAppIds.Contains(id))
            || MatchesEngine(p, engine));
    }

    /// <summary>Der erste passende Eintrag (Install-Karte zeigt genau einen).</summary>
    public static PluginIndexEntry? FirstFor(PluginIndex? index, DiscoveredGame game)
        => EntriesFor(index, game).FirstOrDefault();

    /// <summary>Das Plugin, das dem User als „⬇ Installieren"-Karte angeboten
    /// werden darf — oder null.
    ///
    /// <para>Der Unterschied zu <see cref="FirstFor"/>: ein Plugin das bereits
    /// GELADEN ist, wird nie angeboten. Steht der Content-Bereich trotz
    /// geladenem Plugin ohne Tabs da, fehlt dem Plugin nur dieses eine Spiel in
    /// seiner DetectedGames-Liste — dann ist ein zweiter Download die falsche
    /// Antwort, das Reconcile-Sicherheitsnetz die richtige (v1.28.2).</para></summary>
    public static PluginIndexEntry? InstallOfferFor(
        PluginIndex? index, DiscoveredGame game, IEnumerable<string> loadedPluginIds)
    {
        var entry = FirstFor(index, game);
        if (entry is null) return null;
        return loadedPluginIds.Any(id =>
            string.Equals(id, entry.Id, StringComparison.OrdinalIgnoreCase))
            ? null
            : entry;
    }

    /// <summary>Alle Engine-Slugs fuer die ueberhaupt ein Plugin im Index steht.
    /// Vorberechnet fuer den Sidebar-Sweep in <c>RefreshPluginStates</c>.</summary>
    public static HashSet<string> AvailableEngines(PluginIndex? index)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (index is null) return set;
        foreach (var p in index.Plugins)
            foreach (var e in p.Engines)
                if (!string.IsNullOrWhiteSpace(e)) set.Add(e);
        return set;
    }

    private static bool MatchesEngine(PluginIndexEntry entry, string? engine)
        => !string.IsNullOrWhiteSpace(engine)
           && entry.Engines.Any(e => string.Equals(e, engine, StringComparison.OrdinalIgnoreCase));
}
