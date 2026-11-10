// ---------------------------------------------------------------
//  Quest Case nach dem Raid aufraeumen
//
//  Zurueck im Menue nach einem echten Raid (nicht Versteck, nicht
//  Transit) wird der Inhalt des Quest Case durchgesehen:
//
//   - Aufgaben-Gegenstaende des Spiels (Quest-Markierung) bleiben,
//     ebenso angepinnte/gesperrte Gegenstaende.
//   - Was laufende Aufgaben noch verlangen (Abgeben, Ablegen, Marker -
//     auf jeder Karte, nur der noch offene Rest), bleibt in genau
//     dieser Menge. Ueberzaehlige Stuecke eines Stapels werden
//     abgeteilt. "Nur im Raid gefunden" zaehlt nur gefundene Stuecke.
//   - Schluessel gehoeren nicht in den Quest Case: sie kommen in den
//     KM Case (Keymaster), wenn er im Sicherheitsbehaelter liegt und
//     Platz hat, sonst ins Versteck.
//   - Alles andere kommt ins Versteck.
//
//  Ausloeser wie bei Keymaster und EinsatzortAufgaben:
//  TarkovApplication.OnGameEnd merkt sich den Raid,
//  ComebackToMainMenu raeumt danach auf. Die Plaetze sucht das Spiel
//  (TryFindLocationForItem), die Vorgaenge baut ItemManipulator und
//  fuehrt TryRunNetworkTransaction aus - wie jedes Ziehen per Hand.
// ---------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Comfort.Common;
using EFT;
using EFT.Communications;
using EFT.InventoryLogic;
using HarmonyLib;

namespace AufgabenGepaeck
{
	internal static class Aufraeumer
	{
		private static string _endeAuf;

		public static void Anmelden(Harmony harmony)
		{
			MethodInfo ende = AccessTools.Method(typeof(TarkovApplication), nameof(TarkovApplication.OnGameEnd));
			MethodInfo menue = AccessTools.Method(typeof(TarkovApplication), nameof(TarkovApplication.ComebackToMainMenu));
			if (ende == null || menue == null)
			{
				throw new MissingMethodException("TarkovApplication.OnGameEnd/ComebackToMainMenu gibt es nicht mehr");
			}
			harmony.Patch(ende, new HarmonyMethod(typeof(Aufraeumer), nameof(VorRaidende)));
			harmony.Patch(menue, null, new HarmonyMethod(typeof(Aufraeumer), nameof(NachMenue)));
		}

		private static void VorRaidende(TarkovApplication __instance, JsonType.LocationSettings.Location location)
		{
			try
			{
				string id = location?.Id;
				bool transit = __instance.transitionStatus.InTransition;
				_endeAuf = (string.IsNullOrEmpty(id) || id == "hideout" || transit) ? null : id;
			}
			catch (Exception ex)
			{
				_endeAuf = null;
				MainPlugin.Log.LogError("[Aufgaben-Gepaeck] Raidende: " + ex.Message);
			}
		}

		private static void NachMenue(Task __result)
		{
			string ende = _endeAuf;
			_endeAuf = null;
			if (ende == null || __result == null || !MainPlugin.QuestCaseAufraeumen.Value)
			{
				return;
			}
			__result.ContinueWith(t =>
			{
				if (!t.IsFaulted && !t.IsCanceled)
				{
					_ = Aufraeumen(ende);
				}
			}, TaskScheduler.FromCurrentSynchronizationContext());
		}

		private static async Task Aufraeumen(string ende)
		{
			try
			{
				if (!TarkovApplication.Exist(out TarkovApplication app))
				{
					return;
				}
				// Gleich nach dem Raid hat das Menue-Inventar noch kein Lager
				// (wie bei Keymaster: bis zu 10 s warten).
				InventoryController steuerung = app._menuOperation?.InventoryController;
				for (int i = 0; i < 50 && steuerung?.Inventory?.Stash == null; i++)
				{
					await Task.Delay(200);
					steuerung = app._menuOperation?.InventoryController;
				}
				if (steuerung?.Inventory?.Stash == null)
				{
					MainPlugin.Log.LogWarning("[Aufgaben-Gepaeck] Aufraeumen: kein Versteck im Menue - nichts geraeumt.");
					return;
				}
				CompoundItem koffer = Einpacker.QuestCaseSuchen(steuerung.Inventory.Equipment);
				if (koffer?.Grids == null)
				{
					return;
				}
				// Ohne Aufgabenliste wissen wir nicht, was gebraucht wird - dann
				// lieber gar nichts anfassen.
				if (!Aufgabendaten.Laden(BepInEx.Paths.GameRootPath, MainPlugin.DatenbankPfad.Value))
				{
					MainPlugin.Log.LogWarning("[Aufgaben-Gepaeck] Aufraeumen uebersprungen: " + Aufgabendaten.Meldung);
					return;
				}
				Profile profil = app.Session?.Profile;
				if (profil?.QuestsData == null)
				{
					MainPlugin.Log.LogWarning("[Aufgaben-Gepaeck] Aufraeumen uebersprungen: kein Profil mit Aufgaben.");
					return;
				}
				Bericht b = await Raeumen(steuerung, koffer, Aufgabendaten.OffeneAnforderungen(profil));
				if (b.Leer)
				{
					return;
				}
				string zeile = b.Text();
				MainPlugin.Log.LogInfo("[Aufgaben-Gepaeck] Quest Case nach " + ende + ": " + zeile);
				if (MainPlugin.MeldungAnzeigen.Value)
				{
					if (b.KeinPlatz > 0)
					{
						NotificationManager.DisplayWarningNotification("Quest Case: " + zeile, ENotificationDurationType.Long);
					}
					else
					{
						NotificationManager.DisplayMessageNotification("Quest Case: " + zeile, ENotificationDurationType.Long, ENotificationIconType.Default, null);
					}
				}
				var gesendet = await app.Session.FlushOperationQueue();
				if (!gesendet.Succeed)
				{
					MainPlugin.Log.LogWarning("[Aufgaben-Gepaeck] Aufraeumen: der Server hat nicht bestaetigt: " + gesendet.Error);
				}
			}
			catch (Exception ex)
			{
				MainPlugin.Log.LogError("[Aufgaben-Gepaeck] Aufraeumen fehlgeschlagen: " + ex);
			}
		}

		private sealed class Bericht
		{
			public int InsVersteck;

			public int InDenKmCase;

			public int Behalten;

			public int KeinPlatz;

			public bool Leer => InsVersteck + InDenKmCase + KeinPlatz == 0;

			public string Text()
			{
				string t = InsVersteck + " Gegenstände ins Versteck geräumt, " + Behalten + " behalten";
				if (InDenKmCase > 0)
				{
					t += ", " + InDenKmCase + " Schlüssel in den KM Case";
				}
				if (KeinPlatz > 0)
				{
					t += ", " + KeinPlatz + " blieben (kein Platz im Versteck)";
				}
				return t + ".";
			}
		}

		/// <summary>Wie viele Stuecke dieses Gegenstands die offenen
		/// Anforderungen noch brauchen; zieht sie dabei von den
		/// Anforderungen ab (jedes Stueck deckt nur eine).</summary>
		private static int Gebraucht(Item gegenstand, List<Anforderung> offen)
		{
			int stueck = Math.Max(1, gegenstand.StackObjectsCount);
			string tpl = gegenstand.StringTemplateId;
			bool gefunden = gegenstand.MarkedAsSpawnedInSession;
			int behalten = 0;
			foreach (Anforderung a in offen)
			{
				if (behalten >= stueck)
				{
					break;
				}
				if (a.Anzahl <= 0 || !a.Ziele.Contains(tpl) || (a.NurGefunden && !gefunden))
				{
					continue;
				}
				int nimm = Math.Min(a.Anzahl, stueck - behalten);
				a.Anzahl -= nimm;
				behalten += nimm;
			}
			return behalten;
		}

		private static async Task<Bericht> Raeumen(InventoryController steuerung, CompoundItem koffer, List<Anforderung> offen)
		{
			Bericht b = new Bericht();
			Inventory inv = steuerung.Inventory;
			CompoundItem km = Einpacker.ImSicherheitsbehaelterSuchen(inv.Equipment, Einpacker.KmCaseId);
			// "Nur gefunden"-Anforderungen zuerst bedienen, und dafuer die
			// nicht gefundenen Stuecke zuerst verteilen: die koennen nur
			// Anforderungen ohne diese Bedingung decken.
			offen = offen.OrderByDescending(a => a.NurGefunden).ToList();
			// Feste Liste ziehen, bevor verschoben wird - sonst aendert sich
			// die Sammlung waehrend der Aufzaehlung.
			List<Item> inhalt = koffer.Grids.Where(g => g != null).SelectMany(g => g.Items).Where(i => i != null)
				.OrderBy(i => i.MarkedAsSpawnedInSession).ToList();
			foreach (Item it in inhalt)
			{
				// Zwischen zwei Vorgaengen laufen andere Helfer (EinsatzortAufgaben
				// legt seine Sachen zurueck, Keymaster seine Schluessel): was
				// nicht mehr im Quest Case liegt, fassen wir nicht an.
				if ((object)it.Parent?.Container?.ParentItem != (object)koffer)
				{
					continue;
				}
				int stueck = Math.Max(1, it.StackObjectsCount);
				if (it.QuestItem || it.PinLockState != EItemPinLockState.Free)
				{
					b.Behalten++;
					continue;
				}
				bool schluessel = Einpacker.IstSchluessel(it);
				int behalten = Gebraucht(it, offen);
				if (schluessel && km != null)
				{
					// Schluessel gehoeren nie in den Quest Case, auch wenn eine
					// Aufgabe sie braucht. Sie stapeln nicht - es geht immer um
					// das ganze Stueck. Nimmt der KM Case ihn nicht, bleibt ein
					// gebrauchter Schluessel hier, ein ueberzaehliger geht heim.
					if (await Verschieben(steuerung, it, 0, km))
					{
						b.InDenKmCase++;
						continue;
					}
				}
				if (behalten >= stueck)
				{
					b.Behalten++;
					continue;
				}
				int weg = stueck - behalten;
				if (await Verschieben(steuerung, it, (behalten > 0) ? weg : 0, inv.Stash))
				{
					b.InsVersteck++;
					if (behalten > 0)
					{
						b.Behalten++;
					}
				}
				else
				{
					b.KeinPlatz++;
				}
			}
			return b;
		}

		/// <summary>anzahl 0 = ganzer Gegenstand, sonst so viele Stuecke
		/// abteilen. Platz sucht das Spiel, die Filter der Faecher (und
		/// Keymasters Grenze fuer fremde Schluessel) prueft es dabei mit.</summary>
		private static async Task<bool> Verschieben(InventoryController steuerung, Item was, int anzahl, CompoundItem ziel)
		{
			if (ziel?.Grids == null)
			{
				return false;
			}
			foreach (EFT.InventoryLogic.Grid raster in ziel.Grids)
			{
				if (raster == null || !raster.TryFindLocationForItem(was, out ItemAddress platz) || platz == null)
				{
					continue;
				}
				Diz.LanguageExtensions.OperationResult vorgang;
				if (anzahl > 0 && anzahl < Math.Max(1, was.StackObjectsCount))
				{
					vorgang = ItemManipulator.SplitExact(was, anzahl, platz, steuerung, steuerung, true);
				}
				else
				{
					vorgang = ItemManipulator.Move(was, platz, steuerung, true);
				}
				if (vorgang.Failed)
				{
					continue;
				}
				try
				{
					IResult r = await steuerung.TryRunNetworkTransaction(vorgang);
					if (r != null && r.Succeed)
					{
						return true;
					}
					MainPlugin.Log.LogWarning("[Aufgaben-Gepaeck] Aufraeumen: " + Einpacker.Anzeigename(was) + " abgelehnt - " + r?.Error);
				}
				catch (Exception ex)
				{
					MainPlugin.Log.LogWarning("[Aufgaben-Gepaeck] Aufraeumen: " + Einpacker.Anzeigename(was) + " - " + ex.Message);
				}
				return false;
			}
			return false;
		}
	}
}
