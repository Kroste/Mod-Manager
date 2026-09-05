using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using KroModIx.Services.Games;
using KroModIx.Services.Plugins;
using Xunit;

namespace KroModIx.Tests;

public class PluginIndexMatcherTests
{
    private static PluginIndexEntry Entry(string id, int[]? appIds = null, string[]? engines = null)
        => new()
        {
            Id = id,
            DisplayName = id,
            SteamAppIds = (appIds ?? System.Array.Empty<int>()).ToList(),
            Engines = (engines ?? System.Array.Empty<string>()).ToList(),
        };

    private static PluginIndex Index(params PluginIndexEntry[] entries)
        => new() { Plugins = entries.ToList() };

    private static DiscoveredGame SteamGame(int appId)
        => new($"steam:{appId}", "Steam-Spiel", "/games/x", appId, null, null,
            DiscoveredGameSource.Steam);

    private static DiscoveredGame EngineGame(string engine, string name = "Happy Summer")
        => new($"manual:{name}", name, $"/games/{name}", null, "id-1", null,
            DiscoveredGameSource.Manual, Engine: engine);

    [Fact]
    public void SteamAppId_matcht_wie_bisher()
    {
        var idx = Index(Entry("kroste.icarus", appIds: new[] { 1149460 }));
        PluginIndexMatcher.FirstFor(idx, SteamGame(1149460))!.Id.Should().Be("kroste.icarus");
        PluginIndexMatcher.FirstFor(idx, SteamGame(999)).Should().BeNull();
    }

    [Fact]
    public void EngineGame_ohne_SteamAppId_findet_das_Engine_Plugin()
    {
        // Der Regressionsfall: Ordner-Scan legt Ren'Py-Kacheln ohne SteamAppId
        // an. Vor v1.28.1 lief das Matching nur ueber SteamAppId → jede Kachel
        // meldete "kein Plugin verfuegbar".
        var idx = Index(
            Entry("kroste.icarus", appIds: new[] { 1149460 }),
            Entry("kroste.renpyassist", engines: new[] { "renpy" }));

        PluginIndexMatcher.FirstFor(idx, EngineGame("renpy"))!.Id
            .Should().Be("kroste.renpyassist");
    }

    [Fact]
    public void Engine_Match_ist_case_insensitive()
    {
        var idx = Index(Entry("kroste.renpyassist", engines: new[] { "renpy" }));
        PluginIndexMatcher.FirstFor(idx, EngineGame("RenPy")).Should().NotBeNull();
    }

    [Fact]
    public void Unbekannte_Engine_matcht_nichts()
    {
        var idx = Index(Entry("kroste.renpyassist", engines: new[] { "renpy" }));
        PluginIndexMatcher.FirstFor(idx, EngineGame("unity")).Should().BeNull();
    }

    [Fact]
    public void Game_ohne_Engine_und_ohne_AppId_matcht_nichts()
    {
        var idx = Index(Entry("kroste.renpyassist", engines: new[] { "renpy" }));
        var game = new DiscoveredGame("manual:x", "X", "/games/x", null, "id", null,
            DiscoveredGameSource.Manual);
        PluginIndexMatcher.EntriesFor(idx, game).Should().BeEmpty();
    }

    [Fact]
    public void AvailableEngines_sammelt_alle_Slugs_und_ignoriert_Leerwerte()
    {
        var idx = Index(
            Entry("a", engines: new[] { "renpy", "  " }),
            Entry("b", engines: new[] { "rpgmaker" }),
            Entry("c", appIds: new[] { 42 }));

        PluginIndexMatcher.AvailableEngines(idx)
            .Should().BeEquivalentTo(new[] { "renpy", "rpgmaker" });
        PluginIndexMatcher.AvailableEngines(idx).Contains("RENPY").Should().BeTrue();
    }

    [Fact]
    public void InstallOffer_bietet_ein_fehlendes_Plugin_an()
    {
        var idx = Index(Entry("kroste.renpyassist", engines: new[] { "renpy" }));
        PluginIndexMatcher.InstallOfferFor(idx, EngineGame("renpy"), Array.Empty<string>())!
            .Id.Should().Be("kroste.renpyassist");
    }

    [Fact]
    public void InstallOffer_bietet_ein_GELADENES_Plugin_nicht_nochmal_an()
    {
        // Regression v1.28.2: die Kachel stand ohne Tabs da, weil das geladene
        // Plugin dieses eine Spiel noch nicht in DetectedGames hatte. Eine
        // Install-Karte waere hier die falsche Antwort — ein Klick wuerde
        // dasselbe Plugin ein zweites Mal von GitHub holen. Zustaendig ist der
        // Reconcile-Pfad, den die Karte mit ihrem fruehen return uebersprang.
        var idx = Index(Entry("kroste.renpyassist", engines: new[] { "renpy" }));

        PluginIndexMatcher.InstallOfferFor(idx, EngineGame("renpy"),
            new[] { "kroste.renpyassist" }).Should().BeNull();
    }

    [Fact]
    public void InstallOffer_ignoriert_Gross_Kleinschreibung_der_Plugin_Id()
    {
        var idx = Index(Entry("kroste.renpyassist", engines: new[] { "renpy" }));
        PluginIndexMatcher.InstallOfferFor(idx, EngineGame("renpy"),
            new[] { "KROSTE.RENPYASSIST" }).Should().BeNull();
    }

    [Fact]
    public void InstallOffer_laesst_sich_von_anderen_geladenen_Plugins_nicht_beirren()
    {
        var idx = Index(Entry("kroste.renpyassist", engines: new[] { "renpy" }));
        PluginIndexMatcher.InstallOfferFor(idx, EngineGame("renpy"),
            new[] { "kroste.icarus", "kroste.ls25" })!.Id.Should().Be("kroste.renpyassist");
    }

    [Fact]
    public void Kein_Index_geladen_liefert_leer()
    {
        PluginIndexMatcher.EntriesFor(null, EngineGame("renpy")).Should().BeEmpty();
        PluginIndexMatcher.AvailableEngines(null).Should().BeEmpty();
    }
}
