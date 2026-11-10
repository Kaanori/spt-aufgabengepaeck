// ---------------------------------------------------------------
//  Quest Case sortiert wie das Lager
//
//  Beide Sortierknoepfe (der des Spiel-Fensters und der vom Mod
//  Sortierknopf) laufen ueber dieselbe Spiel-Routine
//  ItemManipulator.Sort. Das "Lager-Sortieren" kommt NICHT vom Spiel,
//  sondern vom Mod StashManagementHelper (Markosz). Der haengt sich an
//  Sort, ItemSorter.Sort und Grid.FindFreeSpace und schaltet seine
//  eigene Ordnung (Behaeltergroesse, Feldgroesse, Art), Stapeln,
//  Zusammenklappen und das Drehen fuer die beste Lage nur ein, wenn
//  zwei Pruefungen in StashManagementHelper.Helpers.ItemManager ja
//  sagen:
//
//    IsItemInStash(sortedItem)  - der Behaelter liegt im Lager
//                                 (sonst nur mit "Sort other
//                                 containers", das ist aus)
//    IsStashGrid(grid)          - grid.ID == "hideout", also nur das
//                                 Lagerraster selbst
//
//  Der Quest Case steckt im Sicherheitsbehaelter (Ausruestung), sein
//  Fach heisst "main" - beide Pruefungen sagen nein, er bekam die
//  nackte Spiel-Sortierung. Hier sagen sie fuer den Quest Case ja.
//  Fehlt StashManagementHelper, passiert nichts.
// ---------------------------------------------------------------
using System;
using System.Reflection;
using EFT.InventoryLogic;
using HarmonyLib;

namespace AufgabenGepaeck
{
	internal static class QuestCaseSortieren
	{
		internal const string HelferGuid = "com.markosz.stashmanagementhelper";

		public static void Anmelden(Harmony harmony)
		{
			Type helfer = AccessTools.TypeByName("StashManagementHelper.Helpers.ItemManager");
			if (helfer == null)
			{
				MainPlugin.Log.LogInfo("[Aufgaben-Gepaeck] StashManagementHelper nicht geladen - der Quest Case sortiert mit der Spiel-Sortierung.");
				return;
			}
			MethodInfo imLager = AccessTools.Method(helfer, "IsItemInStash", new Type[1] { typeof(Item) });
			MethodInfo lagerRaster = AccessTools.Method(helfer, "IsStashGrid", new Type[1] { typeof(EFT.InventoryLogic.Grid) });
			if (imLager == null || lagerRaster == null)
			{
				MainPlugin.Log.LogWarning("[Aufgaben-Gepaeck] StashManagementHelper hat sich geaendert (IsItemInStash/IsStashGrid fehlen) - der Quest Case sortiert mit der Spiel-Sortierung.");
				return;
			}
			harmony.Patch(imLager, null, new HarmonyMethod(typeof(QuestCaseSortieren), nameof(NachImLager)));
			harmony.Patch(lagerRaster, null, new HarmonyMethod(typeof(QuestCaseSortieren), nameof(NachLagerRaster)));
			MainPlugin.Log.LogInfo("[Aufgaben-Gepaeck] Quest Case sortiert wie das Lager (StashManagementHelper).");
		}

		private static void NachImLager(Item item, ref bool __result)
		{
			if (!__result && MainPlugin.QuestCaseWieLager.Value && item != null && item.StringTemplateId == QuestCaseFenster.KofferId)
			{
				__result = true;
			}
		}

		private static void NachLagerRaster(EFT.InventoryLogic.Grid grid, ref bool __result)
		{
			if (!__result && MainPlugin.QuestCaseWieLager.Value && grid?.ParentItem != null && grid.ParentItem.StringTemplateId == QuestCaseFenster.KofferId)
			{
				__result = true;
			}
		}
	}
}
