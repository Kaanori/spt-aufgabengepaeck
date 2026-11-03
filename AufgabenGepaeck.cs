using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using Comfort.Common;
using EFT;
using EFT.Communications;
using EFT.InventoryLogic;
using EFT.UI;
using EFT.UI.Matchmaker;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine;

[assembly: CompilationRelaxations(8)]
[assembly: RuntimeCompatibility(WrapNonExceptionThrows = true)]
[assembly: Debuggable(DebuggableAttribute.DebuggingModes.IgnoreSymbolStoreSequencePoints)]
[assembly: TargetFramework(".NETFramework,Version=v4.7.1", FrameworkDisplayName = ".NET Framework 4.7.1")]
[assembly: AssemblyCompany("AufgabenGepaeck")]
[assembly: AssemblyConfiguration("Release")]
[assembly: AssemblyDescription("Packt vor dem Raid die Aufgaben-Gegenstaende der gewaehlten Karte ein")]
[assembly: AssemblyFileVersion("1.2.0.0")]
[assembly: AssemblyInformationalVersion("1.2.0")]
[assembly: AssemblyProduct("AufgabenGepaeck")]
[assembly: AssemblyTitle("Aufgaben-Gepaeck")]
[assembly: AssemblyVersion("1.2.0.0")]
namespace AufgabenGepaeck
{
	internal class Platzierung
	{
		public string Vorlage;

		public int Anzahl;
	}
	internal class Aufgabe
	{
		public string Id;

		public string Name;

		public string Karte;

		public readonly List<Platzierung> Platzierungen = new List<Platzierung>();
	}
	internal class Kartenangabe
	{
		public string Art;

		public readonly List<string> Karten = new List<string>();
	}
	internal class Bedarf
	{
		public string Vorlage;

		public int Anzahl;

		public readonly List<string> Aufgaben = new List<string>();
	}
	// Loest Vorlagen-IDs in lesbare Gegenstandsnamen auf. Quelle ist die
	// Sprachdatei des Servers - unabhaengig davon, ob der Gegenstand im Spiel
	// gerade als Objekt vorliegt. Genau das fehlte in den "fehlt:"-Meldungen.
	internal static class Gegenstandsnamen
	{
		private static Dictionary<string, string> _namen;

		public static int Anzahl => (_namen != null) ? _namen.Count : 0;

		public static string OrdnerSuchen(string spielordner)
		{
			// Seit SPT 4.1 liegt der Server unter SPT_Runtime/ - derselbe
			// Umzug wie bei der quests.json (DateiSuchen). Ohne diesen Pfad
			// wurde keine Sprachdatei gefunden, und die "fehlt:"-Meldungen
			// zeigten wieder die nackte Vorlagen-ID.
			return new string[3]
			{
				Path.Combine(spielordner, "SPT_Runtime/SPT_Data/database/locales/global"),
				Path.Combine(spielordner, "SPT/SPT_Data/database/locales/global"),
				Path.Combine(spielordner, "SPT_Data/Server/database/locales/global")
			}.FirstOrDefault(Directory.Exists);
		}

		public static void Laden(string spielordner)
		{
			if (_namen != null)
			{
				return;
			}
			_namen = new Dictionary<string, string>();
			try
			{
				string ordner = OrdnerSuchen(spielordner);
				if (ordner == null)
				{
					return;
				}
				string datei = new string[2] { "ge.json", "en.json" }.Select((string n) => Path.Combine(ordner, n)).FirstOrDefault(File.Exists);
				if (datei == null)
				{
					return;
				}
				JObject jObject = JObject.Parse(File.ReadAllText(datei));
				foreach (JProperty item in jObject.Properties())
				{
					if (item.Name.EndsWith(" Name"))
					{
						string key = item.Name.Substring(0, item.Name.Length - 5);
						string text = (string)item.Value;
						if (!string.IsNullOrEmpty(text))
						{
							_namen[key] = text;
						}
					}
				}
			}
			catch
			{
			}
		}

		public static string Name(string vorlage)
		{
			string value;
			if (_namen != null && !string.IsNullOrEmpty(vorlage) && _namen.TryGetValue(vorlage, out value))
			{
				return value;
			}
			return vorlage;
		}
	}

	internal static class Aufgabendaten
	{
		private static readonly string[][] Kartenpaare = new string[2][]
		{
			new string[2] { "55f2d3fd4bdc2d5f408b4567", "59fc81d786f774390775787e" },
			new string[2] { "653e6760052c01c1c805532f", "65b8d6f5cdde2479cb2a3125" }
		};

		private static Dictionary<string, Aufgabe> _aufgaben;

		private static Dictionary<string, Kartenangabe> _kartenangaben;

		public static bool Geladen => _aufgaben != null;

		public static string Meldung { get; private set; }

		public static string DateiSuchen(string spielordner, string wunschpfad)
		{
			if (!string.IsNullOrEmpty(wunschpfad))
			{
				if (!File.Exists(wunschpfad))
				{
					return null;
				}
				return wunschpfad;
			}
			// Reihenfolge = Alter der SPT-Ordnerstruktur, neueste zuerst.
			// Seit SPT 4.1 liegt der Server unter SPT_Runtime/ - die beiden
			// alten Pfade gehen dort ins Leere, und genau daran ist das
			// Einpacken ab 4.1.6 gescheitert (Warnung "Aufgaben-Datenbank
			// nicht gefunden").
			return new string[3]
			{
				Path.Combine(spielordner, "SPT_Runtime/SPT_Data/database/templates/quests.json"),
				Path.Combine(spielordner, "SPT/SPT_Data/database/templates/quests.json"),
				Path.Combine(spielordner, "SPT_Data/Server/database/templates/quests.json")
			}.FirstOrDefault(File.Exists);
		}

		public static bool Laden(string spielordner, string wunschpfad)
		{
			if (_aufgaben != null)
			{
				return true;
			}
			string text = DateiSuchen(spielordner, wunschpfad);
			if (text == null)
			{
				Meldung = "Die Aufgaben-Datenbank des Servers wurde nicht gefunden. Erwartet unter <Spielordner>/SPT_Runtime/SPT_Data/database/templates/quests.json (aeltere SPT-Staende: SPT/ bzw. SPT_Data/Server/) - sonst den Pfad im F12-Menue eintragen.";
				return false;
			}
			Dictionary<string, Aufgabe> dictionary = new Dictionary<string, Aufgabe>();
			foreach (JProperty item in JObject.Parse(File.ReadAllText(text)).Properties())
			{
				JToken value = item.Value;
				JObject val = (JObject)(object)((value is JObject) ? value : null);
				if (val == null)
				{
					continue;
				}
				JArray val2 = val["conditions"] as JArray;
				if (val2 == null)
				{
					continue;
				}
				Aufgabe aufgabe = null;
				foreach (JObject item2 in ((IEnumerable)val2).OfType<JObject>())
				{
					string text2 = (string)item2["conditionType"];
					if (text2 != "LeaveItemAtLocation" && text2 != "PlaceBeacon")
					{
						continue;
					}
					JToken obj = item2["target"];
					JArray val3 = (JArray)(object)((obj is JArray) ? obj : null);
					if (val3 == null)
					{
						continue;
					}
					int num = ((item2["value"] == null) ? 1 : ((int?)item2["value"]).GetValueOrDefault(1));
					if (num < 1)
					{
						num = 1;
					}
					foreach (string item3 in from t in (IEnumerable<JToken>)val3
						select (string)t into t
						where !string.IsNullOrEmpty(t)
						select t)
					{
						if (aufgabe == null)
						{
							aufgabe = new Aufgabe
							{
								Id = item.Name,
								Name = (((string)val["QuestName"]) ?? item.Name),
								Karte = (((string)val["location"]) ?? "any")
							};
						}
						aufgabe.Platzierungen.Add(new Platzierung
						{
							Vorlage = item3,
							Anzahl = num
						});
					}
				}
				if (aufgabe != null)
				{
					dictionary[aufgabe.Id] = aufgabe;
				}
			}
			_aufgaben = dictionary;
			_kartenangaben = KartenangabenLaden(spielordner);
			Meldung = string.Format("{0} Aufgaben mit Mitbring-Gegenstaenden gelesen{1}.", _aufgaben.Count, (_kartenangaben == null) ? " (ohne TaskListLocations - Aufgaben mit Karte \"any\" bleiben ungenau)" : (", dazu " + _kartenangaben.Count + " Kartenangaben von TaskListLocations"));
			return true;
		}

		private static Dictionary<string, Kartenangabe> KartenangabenLaden(string spielordner)
		{
			string path = Path.Combine(spielordner, "BepInEx/plugins/TaskListLocations/quests.json");
			if (!File.Exists(path))
			{
				return null;
			}
			try
			{
				Dictionary<string, Kartenangabe> dictionary = new Dictionary<string, Kartenangabe>();
				foreach (JProperty item in JObject.Parse(File.ReadAllText(path)).Properties())
				{
					JToken value = item.Value;
					JObject val = (JObject)(object)((value is JObject) ? value : null);
					if (val == null)
					{
						continue;
					}
					Kartenangabe kartenangabe = new Kartenangabe
					{
						Art = (string)val["type"]
					};
					JToken obj = val["locations"];
					JArray val2 = (JArray)(object)((obj is JArray) ? obj : null);
					if (val2 != null)
					{
						kartenangabe.Karten.AddRange(from k in (IEnumerable<JToken>)val2
							select (string)k into k
							where !string.IsNullOrEmpty(k)
							select k);
					}
					dictionary[item.Name] = kartenangabe;
				}
				return dictionary;
			}
			catch
			{
				return null;
			}
		}

		private static bool GleicheKarte(string a, string b)
		{
			if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
			{
				return false;
			}
			if (a.Equals(b, StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}
			return Kartenpaare.Any((string[] paar) => paar.Any((string x) => x.Equals(a, StringComparison.OrdinalIgnoreCase)) && paar.Any((string x) => x.Equals(b, StringComparison.OrdinalIgnoreCase)));
		}

		private static double AnteilFuerKarte(Aufgabe aufgabe, string karte, bool ueberallMitnehmen)
		{
			if (string.IsNullOrEmpty(aufgabe.Karte) || !aufgabe.Karte.Equals("any", StringComparison.OrdinalIgnoreCase))
			{
				if (!GleicheKarte(aufgabe.Karte, karte))
				{
					return 0.0;
				}
				return 1.0;
			}
			Kartenangabe value = null;
			if (_kartenangaben != null)
			{
				_kartenangaben.TryGetValue(aufgabe.Id, out value);
			}
			if (value == null || value.Karten.Count == 0)
			{
				if (!ueberallMitnehmen)
				{
					return 0.0;
				}
				return 1.0;
			}
			switch (value.Art)
			{
			case "stash":
			case "find_in_raid":
				return 0.0;
			case "any_location":
				if (!ueberallMitnehmen)
				{
					return 0.0;
				}
				return 1.0;
			case "location_excluding":
				if (value.Karten.Any((string k) => GleicheKarte(k, karte)))
				{
					return 0.0;
				}
				if (!ueberallMitnehmen)
				{
					return 0.0;
				}
				return 1.0;
			case "many_location":
			{
				int num = value.Karten.Count((string k) => GleicheKarte(k, karte));
				if (num == 0)
				{
					return 0.0;
				}
				return (double)num / (double)value.Karten.Count;
			}
			default:
				if (!value.Karten.Any((string k) => GleicheKarte(k, karte)))
				{
					return 0.0;
				}
				return 1.0 / (double)value.Karten.Count;
			}
		}

		public static List<Bedarf> Berechnen(string karte, IEnumerable<string> laufendeAufgaben, bool ueberallMitnehmen, int deckel)
		{
			Dictionary<string, Bedarf> dictionary = new Dictionary<string, Bedarf>();
			if (_aufgaben == null || string.IsNullOrEmpty(karte))
			{
				return new List<Bedarf>();
			}
			foreach (string item in laufendeAufgaben)
			{
				if (!_aufgaben.TryGetValue(item, out var value))
				{
					continue;
				}
				double num = AnteilFuerKarte(value, karte, ueberallMitnehmen);
				if (num <= 0.0)
				{
					continue;
				}
				foreach (IGrouping<string, Platzierung> item2 in from p in value.Platzierungen
					group p by p.Vorlage)
				{
					int num2 = (int)Math.Ceiling((double)item2.Sum((Platzierung p) => p.Anzahl) * num);
					if (num2 >= 1)
					{
						if (deckel > 0)
						{
							num2 = Math.Min(num2, deckel);
						}
						if (!dictionary.TryGetValue(item2.Key, out var value2))
						{
							value2 = new Bedarf
							{
								Vorlage = item2.Key
							};
							dictionary[item2.Key] = value2;
						}
						value2.Anzahl += num2;
						if (!value2.Aufgaben.Contains(value.Name))
						{
							value2.Aufgaben.Add(value.Name);
						}
					}
				}
			}
			if (deckel > 0)
			{
				foreach (Bedarf value3 in dictionary.Values)
				{
					value3.Anzahl = Math.Min(value3.Anzahl, deckel);
				}
			}
			return dictionary.Values.ToList();
		}
	}
	internal class Packbericht
	{
		public readonly List<string> Eingepackt = new List<string>();

		public readonly List<string> Fehlt = new List<string>();

		public readonly List<string> KeinPlatz = new List<string>();

		public bool NichtsZuTun
		{
			get
			{
				if (Eingepackt.Count == 0 && Fehlt.Count == 0)
				{
					return KeinPlatz.Count == 0;
				}
				return false;
			}
		}
	}
	internal static class Einpacker
	{
		private static FieldInfo _feldSteuerung;

		private static MethodInfo _methodeName;

		private static bool _nameGesucht;

		public static InventoryController SteuererHolen()
		{
			ItemUiContext instance = ItemUiContext.Instance;
			if ((UnityEngine.Object)(object)instance == (UnityEngine.Object)null)
			{
				return null;
			}
			if (_feldSteuerung == null)
			{
				_feldSteuerung = AccessTools.GetDeclaredFields(typeof(ItemUiContext)).FirstOrDefault((FieldInfo f) => f.FieldType == typeof(InventoryController));
			}
			if (_feldSteuerung == null)
			{
				return null;
			}
			object? value = _feldSteuerung.GetValue(instance);
			return (InventoryController)((value is InventoryController) ? value : null);
		}

		public static string Anzeigename(Item gegenstand)
		{
			if (gegenstand == null)
			{
				return "?";
			}
			if (!_nameGesucht)
			{
				_nameGesucht = true;
				try
				{
					_methodeName = typeof(Item).Assembly.GetTypes().SelectMany((Type t) => t.GetMethods(BindingFlags.Static | BindingFlags.Public)).FirstOrDefault((MethodInfo m) => m.Name == "LocalizedName" && m.ReturnType == typeof(string) && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(Item));
				}
				catch
				{
					_methodeName = null;
				}
			}
			if (_methodeName != null)
			{
				try
				{
					string text = _methodeName.Invoke(null, new object[1] { gegenstand }) as string;
					if (!string.IsNullOrEmpty(text))
					{
						return text;
					}
				}
				catch
				{
				}
			}
			return gegenstand.StringTemplateId;
		}

		// Laeuft den Gegenstand und alles darin rekursiv ab. Bewusst ueber die
		// Schnittstelle IItemContainer statt ueber den obfuskierten Klassennamen -
		// der aendert sich mit jedem EFT-Update.
		private static IEnumerable<Item> AlleGegenstaende(Item wurzel)
		{
			if (wurzel == null)
			{
				yield break;
			}
			yield return wurzel;
			CompoundItem halter = wurzel as CompoundItem;
			if (halter == null)
			{
				yield break;
			}
			foreach (EFT.InventoryLogic.IContainer behaelter in halter.Containers)
			{
				foreach (Item kind in behaelter.Items)
				{
					if (kind == null || kind == wurzel)
					{
						continue;
					}
					foreach (Item enkel in AlleGegenstaende(kind))
					{
						yield return enkel;
					}
				}
			}
		}

		private static int Bestand(Item wurzel, string vorlage)
		{
			return (from i in AlleGegenstaende(wurzel)
				where i.StringTemplateId == vorlage
				select i).Sum((Item i) => Math.Max(1, i.StackObjectsCount));
		}

		private static List<CompoundItem> Zielbehaelter(InventoryEquipment ausruestung)
		{
			//IL_0063: Unknown result type (might be due to invalid IL or missing references)
			//IL_0068: Unknown result type (might be due to invalid IL or missing references)
			//IL_006a: Unknown result type (might be due to invalid IL or missing references)
			List<EquipmentSlot> list = new List<EquipmentSlot>();
			bool tresor = MainPlugin.ZielSicherheitsbehaelter.Value;
			if (tresor && MainPlugin.ZielTresorZuerst.Value)
			{
				list.Add((EquipmentSlot)5);
			}
			if (MainPlugin.ZielRucksack.Value)
			{
				list.Add((EquipmentSlot)4);
			}
			if (MainPlugin.ZielWeste.Value)
			{
				list.Add((EquipmentSlot)6);
			}
			if (MainPlugin.ZielTaschen.Value)
			{
				list.Add((EquipmentSlot)8);
			}
			if (tresor && !MainPlugin.ZielTresorZuerst.Value)
			{
				list.Add((EquipmentSlot)5);
			}
			List<CompoundItem> list2 = new List<CompoundItem>();
			foreach (EquipmentSlot item in list)
			{
				Slot slot = ausruestung.GetSlot(item);
				CompoundItem val = (slot != null) ? (slot.ContainedItem as CompoundItem) : null;
				if (val != null)
				{
					list2.Add(val);
				}
			}
			return list2;
		}

		private static ItemAddress FreienPlatzSuchen(List<CompoundItem> ziele, Item gegenstand)
		{
			ItemAddress val = default(ItemAddress);
			foreach (CompoundItem item in ziele)
			{
				if (item.Grids == null)
				{
					continue;
				}
				EFT.InventoryLogic.Grid[] grids = item.Grids;
				for (int i = 0; i < grids.Length; i++)
				{
					if (grids[i].TryFindLocationForItem(gegenstand, out val) && val != null)
					{
						return val;
					}
				}
			}
			return null;
		}

		public static Packbericht Packen(InventoryController steuerung, List<Bedarf> bedarfsliste)
		{
			//IL_01de: Unknown result type (might be due to invalid IL or missing references)
			//IL_01e3: Unknown result type (might be due to invalid IL or missing references)
			//IL_01bd: Unknown result type (might be due to invalid IL or missing references)
			//IL_01c2: Unknown result type (might be due to invalid IL or missing references)
			//IL_01ee: Unknown result type (might be due to invalid IL or missing references)
			//IL_01cd: Unknown result type (might be due to invalid IL or missing references)
			Packbericht packbericht = new Packbericht();
			InventoryEquipment equipment = steuerung.Inventory.Equipment;
			EFT.InventoryLogic.Stash stash = steuerung.Inventory.Stash;
			if (equipment == null || stash == null)
			{
				return packbericht;
			}
			foreach (Bedarf bedarf in bedarfsliste.OrderByDescending((Bedarf b) => b.Anzahl))
			{
				int num = Bestand((Item)(object)equipment, bedarf.Vorlage);
				int num2 = bedarf.Anzahl - num;
				if (num2 <= 0)
				{
					continue;
				}
				List<Item> list = (from i in AlleGegenstaende((Item)(object)stash)
					where i.StringTemplateId == bedarf.Vorlage
					orderby Math.Max(1, i.StackObjectsCount)
					select i).ToList();
				if (list.Count == 0)
				{
					packbericht.Fehlt.Add(string.Format("{0}x {1} ({2})", num2, Aufgabenname(bedarf), string.Join(", ", bedarf.Aufgaben.ToArray())));
					continue;
				}
				string arg = Anzeigename(list[0]);
				int num3 = 0;
				bool flag = false;
				foreach (Item item2 in list)
				{
					if (num2 <= 0)
					{
						break;
					}
					List<CompoundItem> list2 = Zielbehaelter(equipment);
					if (list2.Count == 0)
					{
						flag = true;
						break;
					}
					ItemAddress val = FreienPlatzSuchen(list2, item2);
					if (val == null)
					{
						flag = true;
						break;
					}
					int num4 = Math.Max(1, item2.StackObjectsCount);
					int num5 = Math.Min(num2, num4);
					EFT.InventoryLogic.IOperationResult val2 = null;
					if (num5 < num4)
					{
						var val3 = EFT.InventoryLogic.ItemManipulator.SplitExact(item2, num5, val, (EFT.InventoryLogic.ItemController)(object)steuerung, (EFT.InventoryLogic.IDatabaseIdGenerator)(object)steuerung, true);
						if (val3.Succeeded)
						{
							val2 = (EFT.InventoryLogic.IOperationResult)(object)val3.Value;
						}
					}
					else
					{
						var val4 = EFT.InventoryLogic.ItemManipulator.Move(item2, val, (EFT.InventoryLogic.ItemController)(object)steuerung, true);
						if (val4.Succeeded)
						{
							val2 = (EFT.InventoryLogic.IOperationResult)(object)val4.Value;
						}
					}
					if (val2 == null)
					{
						flag = true;
						break;
					}
					if (!((EFT.InventoryLogic.ItemController)steuerung).CanExecute(val2))
					{
						flag = true;
						break;
					}
					((EFT.InventoryLogic.ItemController)steuerung).RunNetworkTransaction(val2, (Callback)null);
					num3 += num5;
					num2 -= num5;
				}
				if (num3 > 0)
				{
					packbericht.Eingepackt.Add($"{num3}x {arg}");
				}
				if (num2 > 0)
				{
					string item = string.Format("{0}x {1} ({2})", num2, arg, string.Join(", ", bedarf.Aufgaben.ToArray()));
					if (flag)
					{
						packbericht.KeinPlatz.Add(item);
					}
					else
					{
						packbericht.Fehlt.Add(item);
					}
				}
			}
			return packbericht;
		}

		private static string Aufgabenname(Bedarf bedarf)
		{
			return Gegenstandsnamen.Name(bedarf.Vorlage);
		}
	}
	[BepInPlugin("com.kaan.aufgaben-gepaeck", "Aufgaben-Gepaeck", "1.2.0")]
	[BepInDependency("com.SPT.core")]
	public class MainPlugin : BaseUnityPlugin
	{
		internal static MainPlugin Selbst;

		internal static ConfigEntry<bool> Eingeschaltet;

		internal static ConfigEntry<bool> UeberallAufgaben;

		internal static ConfigEntry<int> HoechstzahlProGegenstand;

		internal static ConfigEntry<bool> ZielRucksack;

		internal static ConfigEntry<bool> ZielWeste;

		internal static ConfigEntry<bool> ZielTaschen;

		internal static ConfigEntry<bool> ZielSicherheitsbehaelter;

		internal static ConfigEntry<bool> ZielTresorZuerst;

		internal static ConfigEntry<bool> MeldungAnzeigen;

		internal static ConfigEntry<string> DatenbankPfad;

		private void Awake()
		{
			//IL_006a: Unknown result type (might be due to invalid IL or missing references)
			//IL_0074: Expected O, but got Unknown
			//IL_0191: Unknown result type (might be due to invalid IL or missing references)
			//IL_01a8: Unknown result type (might be due to invalid IL or missing references)
			//IL_01b5: Expected O, but got Unknown
			Selbst = this;
			Eingeschaltet = ((BaseUnityPlugin)this).Config.Bind<bool>("1. Allgemein", "Eingeschaltet", true, "Packt beim Raid-Start die Aufgaben-Gegenstaende der gewaehlten Karte ein.");
			UeberallAufgaben = ((BaseUnityPlugin)this).Config.Bind<bool>("1. Allgemein", "Ueberall-Aufgaben mitnehmen", true, "Auch Gegenstaende fuer Aufgaben einpacken, die auf jeder Karte zaehlen. Aus, wenn nur karten-eigene Aufgaben zaehlen sollen.");
			HoechstzahlProGegenstand = ((BaseUnityPlugin)this).Config.Bind<int>("1. Allgemein", "Hoechstzahl je Gegenstand", 10, new ConfigDescription("Sicherheitsnetz: nie mehr als so viele Stueck eines Gegenstands einpacken. Greift vor allem, wenn TaskListLocations fehlt - dann waeren fuer \"Is This a Reference\" sonst 24 WI-FI-Kameras faellig. 0 schaltet die Grenze ab.", (AcceptableValueBase)(object)new AcceptableValueRange<int>(0, 50), Array.Empty<object>()));
			ZielRucksack = ((BaseUnityPlugin)this).Config.Bind<bool>("2. Wohin", "Rucksack", true, "Zuerst in den Rucksack legen.");
			ZielWeste = ((BaseUnityPlugin)this).Config.Bind<bool>("2. Wohin", "Taktische Weste", true, "Danach in die Weste.");
			ZielTaschen = ((BaseUnityPlugin)this).Config.Bind<bool>("2. Wohin", "Taschen", true, "Danach in die Hosentaschen.");
			ZielSicherheitsbehaelter = ((BaseUnityPlugin)this).Config.Bind<bool>("2. Wohin", "Sicherheitsbehaelter", false, "In den Sicherheitsbehaelter legen. Aus, weil der Inhalt den Tod ueberlebt - das nimmt dem Mitbringen jedes Risiko.");
			ZielTresorZuerst = ((BaseUnityPlugin)this).Config.Bind<bool>("2. Wohin", "Zuerst in den Sicherheitsbehaelter", true, "Wenn der Sicherheitsbehaelter erlaubt ist: ihn zuerst fuellen statt zuletzt. Sonst landen Aufgabengegenstaende im Rucksack, solange dort Platz ist.");
			MeldungAnzeigen = ((BaseUnityPlugin)this).Config.Bind<bool>("3. Anzeige", "Meldung im Spiel", true, "Blendet nach dem Einpacken kurz ein, was eingepackt wurde und was fehlt.");
			DatenbankPfad = ((BaseUnityPlugin)this).Config.Bind<string>("4. Fortgeschritten", "Pfad zur quests.json", "", "Leer lassen - dann wird die Aufgaben-Datenbank des Servers automatisch gesucht. Nur ausfuellen, wenn der SPT-Server woanders liegt.");
			MethodInfo methodInfo = AccessTools.GetDeclaredMethods(typeof(MatchMakerAcceptScreen)).FirstOrDefault((MethodInfo m) => m.Name == "Show" && m.GetParameters().Length == 3);
			if (methodInfo == null)
			{
				base.Logger.LogError((object)"[Aufgaben-Gepaeck] Der Raid-Bildschirm hat sich geaendert - die Stelle MatchMakerAcceptScreen.Show(...) gibt es nicht mehr. Der Mod tut nichts.");
				return;
			}
			try
			{
				new Harmony("com.kaan.aufgaben-gepaeck").Patch((MethodBase)methodInfo, (HarmonyMethod)null, new HarmonyMethod(typeof(MainPlugin), "NachRaidBildschirm", (Type[])null), (HarmonyMethod)null, (HarmonyMethod)null, (HarmonyMethod)null);
				base.Logger.LogInfo((object)"[Aufgaben-Gepaeck] Aktiv.");
			}
			catch (Exception ex)
			{
				base.Logger.LogError((object)("[Aufgaben-Gepaeck] Konnte nicht ansetzen: " + ex.Message));
			}
		}

		private static void NachRaidBildschirm(EFT.IEftSession session, RaidSettings raidSettings, RaidSettings offlineRaidSettings)
		{
			try
			{
				if (Eingeschaltet.Value)
				{
					Erledigen(session, raidSettings, offlineRaidSettings);
				}
			}
			catch (Exception ex)
			{
				Selbst.Logger.LogError((object)("[Aufgaben-Gepaeck] Fehler beim Einpacken: " + ex));
			}
		}

		private static void Erledigen(EFT.IEftSession session, RaidSettings raidSettings, RaidSettings offlineRaidSettings)
		{
			ManualLogSource logger = Selbst.Logger;
			string text = KarteBestimmen(raidSettings) ?? KarteBestimmen(offlineRaidSettings);
			if (string.IsNullOrEmpty(text))
			{
				logger.LogWarning((object)"[Aufgaben-Gepaeck] Keine Karte erkannt - nichts zu tun.");
				return;
			}
			Gegenstandsnamen.Laden(BepInEx.Paths.GameRootPath);
			if (!Aufgabendaten.Laden(BepInEx.Paths.GameRootPath, DatenbankPfad.Value))
			{
				logger.LogWarning((object)("[Aufgaben-Gepaeck] " + Aufgabendaten.Meldung));
				return;
			}
			logger.LogInfo((object)("[Aufgaben-Gepaeck] " + Aufgabendaten.Meldung));
			Profile val = ((session != null) ? ((EFT.IProfileSession)session).Profile : null);
			if (val == null || val.QuestsData == null)
			{
				logger.LogWarning((object)"[Aufgaben-Gepaeck] Kein Profil mit Aufgaben gefunden.");
				return;
			}
			List<string> list = (from q in val.QuestsData
				where q != null && (int)q.Status == 2 && !string.IsNullOrEmpty(q.Id)
				select q.Id).ToList();
			List<Bedarf> list2 = Aufgabendaten.Berechnen(text, list, UeberallAufgaben.Value, HoechstzahlProGegenstand.Value);
			if (list2.Count == 0)
			{
				logger.LogInfo((object)$"[Aufgaben-Gepaeck] Karte {text}: keine der {list.Count} laufenden Aufgaben braucht etwas von zuhause.");
				return;
			}
			InventoryController val2 = Einpacker.SteuererHolen();
			if (val2 == null)
			{
				logger.LogWarning((object)"[Aufgaben-Gepaeck] Der Inventar-Steuerer ist noch nicht bereit. Einmal das Inventar oeffnen und erneut auf READY gehen.");
				return;
			}
			Packbericht packbericht = Einpacker.Packen(val2, list2);
			foreach (string item in packbericht.Eingepackt)
			{
				logger.LogInfo((object)("[Aufgaben-Gepaeck] eingepackt: " + item));
			}
			foreach (string item2 in packbericht.Fehlt)
			{
				logger.LogInfo((object)("[Aufgaben-Gepaeck] fehlt im Lager: " + item2));
			}
			foreach (string item3 in packbericht.KeinPlatz)
			{
				logger.LogInfo((object)("[Aufgaben-Gepaeck] kein Platz: " + item3));
			}
			if (MeldungAnzeigen.Value)
			{
				MeldungZeigen(packbericht);
			}
		}

		private static void MeldungZeigen(Packbericht bericht)
		{
			if (!bericht.NichtsZuTun)
			{
				if (bericht.Eingepackt.Count > 0)
				{
					EFT.Communications.NotificationManager.DisplayMessageNotification("Aufgaben-Gepaeck: " + string.Join(", ", bericht.Eingepackt.ToArray()), (ENotificationDurationType)1, (ENotificationIconType)5, (Color?)null);
				}
				List<string> list = new List<string>();
				list.AddRange(bericht.Fehlt.Select((string z) => "fehlt: " + z));
				list.AddRange(bericht.KeinPlatz.Select((string z) => "kein Platz: " + z));
				if (list.Count > 0)
				{
					EFT.Communications.NotificationManager.DisplayWarningNotification("Aufgaben-Gepaeck - " + string.Join(" | ", list.ToArray()), (ENotificationDurationType)1);
				}
			}
		}

		private static string KarteBestimmen(RaidSettings einstellungen)
		{
			if (einstellungen == null)
			{
				return null;
			}
			var selectedLocation = einstellungen.SelectedLocation;
			if (selectedLocation == null)
			{
				return null;
			}
			if (!string.IsNullOrEmpty(selectedLocation._Id))
			{
				return selectedLocation._Id;
			}
			return null;
		}
	}
}
