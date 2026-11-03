// ---------------------------------------------------------------
//  Quest Case - Server-Teil des Aufgaben-Gepaecks
//
//  Ein neuer Behaelter: 2x2 gross, passt in jeden
//  Sicherheitsbehaelter und hat innen ein Fach mit (Standard)
//  10 x 30 = 300 Feldern. Das Aufgaben-Gepaeck-Plugin packt die
//  Aufgaben-Gegenstaende zuerst hier hinein, und das Fenster im
//  Spiel rollt, weil 30 Zeilen nicht auf den Bildschirm passen.
//
//  Die Idee (Koffer im Sicherheitsbehaelter, rollbares Fenster)
//  stammt aus Keymaster von LennoxP90 (MIT) - der Code hier ist
//  eigenstaendig geschrieben.
//
//  Wie das geht:
//  1. Beim Start wird der Munitionskoffer (2x2, eigenes Modell)
//     geklont. Das Fach wird ersetzt: nimmt alles AUSSER Dingen,
//     die selbst Faecher haben (Rucksaecke, Westen, Koffer,
//     Sicherheitsbehaelter ...) - keine Behaelter im Behaelter.
//  2. Die neue Kennung wird in die Filter ALLER
//     Sicherheitsbehaelter eingetragen - beim Start und noch einmal
//     bei der ersten Client-Anfrage, damit auch Behaelter anderer
//     Mods (und Filter, die SVM & Co. spaeter umschreiben) erfasst
//     werden.
//  3. Die Therapeutin verkauft ihn (Stufe/Preis in questcase.json).
//
//  Die Datenbank im Spielordner bleibt unangetastet - alles
//  passiert zur Laufzeit im Speicher.
// ---------------------------------------------------------------
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Mod;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Servers.Http;
using SPTarkov.Server.Core.Services.Modding.Custom;
using SPTarkov.Common.Models.Logging;

using Pfad = System.IO.Path;

namespace AufgabenGepaeckServer;

public record ModMetadata : IModMetadata
{
    public string ModGuid { get; init; } = "com.kaan.aufgaben-gepaeck.server";
    public string Name { get; init; } = "Aufgaben-Gepaeck (Quest Case)";
    public string Author { get; init; } = "Kaan";
    public List<string>? Contributors { get; init; } = new();
    public SemanticVersioning.Version Version { get; init; } = new(1, 3, 0);
    public SemanticVersioning.Range SptVersion { get; init; } = new("~4.1");
    public bool HasPrepatcher { get; init; } = false;
    public List<string>? Incompatibilities { get; init; } = new();
    public Dictionary<string, SemanticVersioning.Range>? ModDependencies { get; init; } = new();
    public string? Url { get; init; } = "";
    public string License { get; init; } = "MIT";
}

/// <summary>Einstellungen aus questcase.json neben der DLL.</summary>
public sealed class Einstellungen
{
    public int Breite { get; set; } = 10;
    public int Hoehe { get; set; } = 30;
    public string Haendler { get; set; } = Koffer.Therapeutin;
    public int Stufe { get; set; } = 1;
    public double Preis { get; set; } = 50000;
    public bool BeimHaendler { get; set; } = true;
}

public static class Koffer
{
    // Feste Kennungen - duerfen sich NIE aendern, sonst verschwinden
    // gekaufte Koffer (samt Inhalt) aus dem Profil. Der Client
    // (AufgabenGepaeck.dll) kennt dieselbe Kennung.
    public const string KofferId = "61afb620726b3782be0c8801";
    public const string AngebotId = "61afb620726b3782be0c8802";
    public const string FachId = "61afb620726b3782be0c8803";

    // Spender: Munitionskoffer - von Haus aus 2x2, eigenes 3D-Modell.
    public const string Spender = "5aafbde786f774389d0cbc0f";

    public const string Therapeutin = "54cb57776803fa99248b456e";
    public const string Rubel = "5449016a4bdc2d6f028b456f";
    public const string HandbuchBehaelter = "5b5f6fa186f77409407a7eb7";
    public const string FachVorlage = "55d329c24bdc2d892f8b4567";

    // Vorlagen-Klassen (Elternknoten)
    public const string Wurzel = "54009119af1c881c07000029";          // Item
    public const string Sicherheitsbehaelter = "5448bf274bdc2dfc2f8b456a"; // MobContainer
    public const string EinfacherBehaelter = "5795f317245977243854e041";   // SimpleContainer (Koffer, Etuis)

    /// <summary>Klassen, deren Mitglieder Faecher haben. Sie stehen als
    /// ganze Klasse im Ausschlussfilter - so greift der Ausschluss auch
    /// fuer Behaelter, die ein Mod erst nach uns anlegt.</summary>
    public static readonly string[] BehaelterKlassen =
    {
        "5448e53e4bdc2d60728b4567", // Backpack
        "5448e5284bdc2dcb718b4567", // Vest (auch Plattentraeger mit Taschen)
        EinfacherBehaelter,         // SimpleContainer - auch der Quest Case selbst
        Sicherheitsbehaelter,       // MobContainer
        "5671435f4bdc2d96058b4569", // LockableContainer
        "566abbb64bdc2d144c8b457d", // Stash
        "557596e64bdc2dc2118b4571", // Pockets
        "566965d44bdc2d814c8b4571", // LootContainer
        "62f109593b54472778797866", // RandomLootContainer
        "63da6da4784a55176c018dba", // HideoutAreaContainer
        "567583764bdc2d98058b456e", // StationaryContainer
    };

    private static readonly JsonSerializerOptions Leseweise = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    public static Einstellungen EinstellungenLesen(out string quelle)
    {
        var pfad = Pfad.Combine(
            Pfad.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? ".",
            "questcase.json");
        Einstellungen cfg;
        if (!File.Exists(pfad))
        {
            quelle = "Standardwerte (keine questcase.json)";
            cfg = new Einstellungen();
        }
        else
        {
            try
            {
                quelle = "questcase.json";
                cfg = JsonSerializer.Deserialize<Einstellungen>(File.ReadAllText(pfad), Leseweise)
                      ?? new Einstellungen();
            }
            catch (Exception ex)
            {
                quelle = "questcase.json unlesbar (" + ex.Message + ") - Standardwerte";
                cfg = new Einstellungen();
            }
        }
        // Unsinnige Werte abfangen statt einen kaputten Koffer zu bauen.
        cfg.Breite = Math.Clamp(cfg.Breite, 1, 20);
        cfg.Hoehe = Math.Clamp(cfg.Hoehe, 1, 100);
        cfg.Stufe = Math.Clamp(cfg.Stufe, 1, 4);
        if (cfg.Preis < 1) cfg.Preis = 1;
        return cfg;
    }

    /// <summary>Alle Klassen ueber einer Vorlage, die Vorlage selbst zuerst.</summary>
    public static List<MongoId> Kette(TemplateTable vorlagen, MongoId id)
    {
        var kette = new List<MongoId>();
        var aktuell = id;
        // Schutz gegen kaputte Mod-Vorlagen, die auf sich selbst zeigen
        for (int i = 0; i < 32 && vorlagen.Items.TryGetValue(aktuell, out var v); i++)
        {
            kette.Add(aktuell);
            if (v.Parent == aktuell) break;
            aktuell = v.Parent;
        }
        return kette;
    }

    public static bool IstSicherheitsbehaelter(TemplateTable vorlagen, MongoId id)
    {
        var sb = new MongoId(Sicherheitsbehaelter);
        var kette = Kette(vorlagen, id);
        // Die Klasse selbst ist kein Behaelter, nur ihre Mitglieder
        return kette.Count > 1 && kette.Skip(1).Contains(sb);
    }

    /// <summary>Traegt den Koffer in die Fachfilter aller
    /// Sicherheitsbehaelter ein. Gibt zurueck, wie viele Filter neu
    /// dazukamen, und sammelt Behaelter, bei denen ein Ausschluss die
    /// ganze Koffer-Klasse sperrt (das loesen wir bewusst nicht auf).</summary>
    public static (int neu, List<string> gesperrt) InSicherheitsbehaelterEintragen(TemplateTable vorlagen)
    {
        var koffer = new MongoId(KofferId);
        var kette = Kette(vorlagen, koffer);
        var gesperrt = new List<string>();
        int neu = 0;

        foreach (var (id, vorlage) in vorlagen.Items)
        {
            if (vorlage.Properties?.Grids is null || !IstSicherheitsbehaelter(vorlagen, id))
                continue;

            foreach (var fach in vorlage.Properties.Grids)
            {
                if (fach.Properties?.Filters is null) continue;
                foreach (var filter in fach.Properties.Filters)
                {
                    // Eigene Kennung aus einem Ausschluss nehmen (nur genau
                    // diese - eine ganze Klasse freizugeben, wuerde auch
                    // fremde Koffer durchlassen).
                    if (filter.ExcludedFilter is not null && filter.ExcludedFilter.Remove(koffer))
                        neu++;
                    if (filter.ExcludedFilter is not null && kette.Skip(1).Any(filter.ExcludedFilter.Contains))
                        gesperrt.Add($"{vorlage.Name} ({id})");

                    // Leerer Filter = keine Einschraenkung, nichts zu tun.
                    if (filter.Filter is null || filter.Filter.Count == 0) continue;
                    // Schon erlaubt (direkt oder ueber eine Klasse, z. B. "Item")?
                    if (kette.Any(filter.Filter.Contains)) continue;
                    if (filter.Filter.Add(koffer)) neu++;
                }
            }
        }
        return (neu, gesperrt);
    }

    /// <summary>Haelt den Ausschlussfilter des Koffers aktuell: jede
    /// Vorlage mit eigenen Faechern, die nicht schon ueber ihre Klasse
    /// ausgeschlossen ist, kommt einzeln dazu (Mod-Behaelter mit
    /// ungewoehnlicher Klasse).</summary>
    public static int AusschlussNachziehen(TemplateTable vorlagen)
    {
        if (!vorlagen.Items.TryGetValue(new MongoId(KofferId), out var koffer))
            return 0;
        var ausschluss = koffer.Properties?.Grids?.FirstOrDefault()?.Properties?.Filters?
            .FirstOrDefault()?.ExcludedFilter;
        if (ausschluss is null) return 0;

        var klassen = BehaelterKlassen.Select(k => new MongoId(k)).ToHashSet();
        int neu = 0;
        foreach (var (id, vorlage) in vorlagen.Items)
        {
            if (vorlage.Properties?.Grids is null || !vorlage.Properties.Grids.Any()) continue;
            if (Kette(vorlagen, id).Any(klassen.Contains)) continue;
            if (ausschluss.Add(id)) neu++;
        }
        return neu;
    }

    public static Grid FachBauen(int breite, int hoehe) => new Grid
    {
        Name = "main",
        Id = FachId,
        Parent = KofferId,
        Prototype = FachVorlage,
        Properties = new GridProperties
        {
            CellsH = breite,
            CellsV = hoehe,
            MinCount = 0,
            MaxCount = 0,
            MaxWeight = 0,
            IsSortingTable = false,
            Filters = new List<GridFilter>
            {
                new GridFilter
                {
                    // Alles erlaubt ...
                    Filter = new HashSet<MongoId> { new(Wurzel) },
                    // ... ausser Dingen mit eigenen Faechern
                    ExcludedFilter = BehaelterKlassen.Select(k => new MongoId(k)).ToHashSet()
                }
            }
        }
    };
}

/// <summary>Beim Serverstart: Koffer anlegen, Haendlerangebot, Filter.
/// Laeuft vor der Haendlerregistrierung und weit vor dem Laden der
/// Profile (danach nimmt CustomItemService keine neuen Vorlagen mehr).</summary>
[Injectable(TypePriority = OnLoadOrder.GameCallbacks + 1)]
public class BeimStart(
    ISptLogger<BeimStart> logger,
    TemplateTable vorlagen,
    TradersTable haendler,
    CustomItemService itemDienst) : IOnLoad
{
    public Task OnLoadAsync(CancellationToken abbruch)
    {
        try
        {
            var cfg = Koffer.EinstellungenLesen(out var quelle);

            const string name = "Quest Case";
            const string kurz = "Quest";
            var ergebnis = itemDienst.CreateItemFromClone(new NewItemFromCloneDetails
            {
                ItemTplToClone = new MongoId(Koffer.Spender),
                ParentId = new MongoId(Koffer.EinfacherBehaelter),
                NewId = new MongoId(Koffer.KofferId),
                NewItemName = "item_container_questcase",
                OverrideProperties = new TemplateItemProperties
                {
                    Name = name,
                    ShortName = kurz,
                    Description = "questcase",
                    Width = 2,
                    Height = 2,
                    Weight = 0.5,
                    BackgroundColor = "green",
                    CanSellOnRagfair = false,
                    CanRequireOnRagfair = false,
                    ExaminedByDefault = true,
                    Grids = new List<Grid> { Koffer.FachBauen(cfg.Breite, cfg.Hoehe) }
                },
                AddToHandbook = true,
                HandbookParentId = Koffer.HandbuchBehaelter,
                HandbookPriceRoubles = cfg.Preis,
                // false = nicht in die Flohmarkt-Preisliste, dafuer auf die
                // globale Beute-Sperrliste: der Koffer taucht nicht als Beute
                // oder bei Bots auf, nur beim Haendler.
                AddToFleaPriceDb = false,
                AddToWeaponShelf = false,
                Locales = new Dictionary<string, LocaleDetails>
                {
                    // "en" zuerst: Sprachen ohne eigenen Eintrag bekommen den ersten.
                    ["en"] = new LocaleDetails
                    {
                        Name = name,
                        ShortName = kurz,
                        Description = "A compact organiser that fits into any secure container. "
                                      + $"Inside: {cfg.Breite} x {cfg.Hoehe} cells for everything "
                                      + "you have to bring into a raid - but no other containers. "
                                      + "Aufgaben-Gepaeck packs your quest items here first."
                    },
                    ["ge"] = new LocaleDetails
                    {
                        Name = "Aufgaben-Koffer",
                        ShortName = kurz,
                        Description = "Ein kompakter Organizer, der in jeden Sicherheitsbehälter passt. "
                                      + $"Innen {cfg.Breite} x {cfg.Hoehe} Felder für alles, was in den "
                                      + "Einsatz mitmuss - aber keine weiteren Behälter. "
                                      + "Das Aufgaben-Gepäck packt Aufgaben-Gegenstände zuerst hier hinein."
                    }
                }
            }, Assembly.GetExecutingAssembly());

            if (!ergebnis.Success)
            {
                logger.Error("[Quest Case] Nicht angelegt: "
                             + string.Join("; ", ergebnis.Errors ?? new List<string>()));
                return Task.CompletedTask;
            }

            var angebot = cfg.BeimHaendler ? Anbieten(cfg) : "kein Haendler";
            var einzeln = Koffer.AusschlussNachziehen(vorlagen);
            var (neu, gesperrt) = Koffer.InSicherheitsbehaelterEintragen(vorlagen);

            logger.Success($"[Quest Case] Angelegt ({Koffer.KofferId}), Fach {cfg.Breite}x{cfg.Hoehe}. "
                           + $"In {neu} Sicherheitsbehaelter-Filter eingetragen, "
                           + $"{einzeln} Einzel-Behaelter ausgeschlossen. Angebot: {angebot}. Quelle: {quelle}");
            if (gesperrt.Count > 0)
                logger.Warning("[Quest Case] Diese Sicherheitsbehaelter sperren alle Koffer per Ausschluss "
                               + "und nehmen ihn darum nicht: " + string.Join(", ", gesperrt.Distinct()));
        }
        catch (Exception ex)
        {
            logger.Error("[Quest Case] " + ex);
        }

        return Task.CompletedTask;
    }

    private string Anbieten(Einstellungen cfg)
    {
        var trader = haendler.GetTrader(new MongoId(cfg.Haendler));
        if (trader?.Assort is null)
            return $"Haendler {cfg.Haendler} nicht gefunden";

        var angebotId = new MongoId(Koffer.AngebotId);
        trader.Assort.Items.Add(new Item
        {
            Id = angebotId,
            Template = new MongoId(Koffer.KofferId),
            ParentId = "hideout",
            SlotId = "hideout",
            Upd = new Upd { UnlimitedCount = true, StackObjectsCount = 9999999 }
        });
        trader.Assort.BarterScheme[angebotId] = new List<List<BarterScheme>>
        {
            new() { new BarterScheme { Template = new MongoId(Koffer.Rubel), Count = cfg.Preis } }
        };
        trader.Assort.LoyalLevelItems[angebotId] = cfg.Stufe;

        return $"{trader.Base?.Nickname ?? cfg.Haendler} Stufe {cfg.Stufe}, {cfg.Preis:N0} Rubel";
    }
}

/// <summary>Einmalig bei der ersten Client-Anfrage: Sicherheitsbehaelter
/// und Behaelter nachziehen, die andere Mods erst nach uns eingehaengt
/// oder umgeschrieben haben. Beantwortet selbst nie eine Anfrage.</summary>
[Injectable(InjectionType.Singleton)]
public class Nachziehen(
    ISptLogger<Nachziehen> logger,
    TemplateTable vorlagen) : IHttpListener
{
    private int _erledigt;

    public bool CanHandle(HttpContext context)
    {
        if (Interlocked.Exchange(ref _erledigt, 1) == 0)
        {
            try
            {
                if (vorlagen.Items.ContainsKey(new MongoId(Koffer.KofferId)))
                {
                    var einzeln = Koffer.AusschlussNachziehen(vorlagen);
                    var (neu, gesperrt) = Koffer.InSicherheitsbehaelterEintragen(vorlagen);
                    if (neu + einzeln > 0)
                        logger.Success($"[Quest Case] Nachgezogen: {neu} weitere Sicherheitsbehaelter-Filter, "
                                       + $"{einzeln} weitere Behaelter ausgeschlossen (spaeter geladene Mods).");
                    if (gesperrt.Count > 0)
                        logger.Warning("[Quest Case] Sperren alle Koffer per Ausschluss: "
                                       + string.Join(", ", gesperrt.Distinct()));
                }
            }
            catch (Exception ex)
            {
                logger.Error("[Quest Case] Nachziehen fehlgeschlagen: " + ex.Message);
            }
        }

        return false;   // immer: dieser Mod beantwortet keine Anfragen
    }

    public Task HandleAsync(MongoId sessionId, HttpContext context,
                            CancellationToken abbruch = default)
        => Task.CompletedTask;
}
