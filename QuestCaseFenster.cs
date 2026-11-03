// ---------------------------------------------------------------
//  Quest Case - rollbares Fenster
//
//  Der Quest Case hat ein Fach mit 10 x 30 Feldern (Server-Teil).
//  Das Spiel baut fuer Behaelter ein Fenster in voller Rasterhoehe:
//  30 Zeilen a 63 px = 1890 px, das passt auf keinen Bildschirm.
//
//  Loesung: Das ECHTE Raster des Spiels bleibt, wie es ist - mit
//  allen Tetris-Plaetzen, Ziehen & Ablegen, Markierungen und dem
//  Sortierknopf. Es wird nur in einen Rahmen gehaengt, der lediglich
//  einen Ausschnitt zeigt (RectMask2D) und den Inhalt beim Rollen
//  verschiebt. So rollt EFT auch das Lager: dessen Raster liegt
//  ebenfalls verschoben hinter einer Maske, und GridView rechnet die
//  Ablegestelle ueber InverseTransformPoint - eine Verschiebung des
//  Elternteils stoert es also nicht.
//
//  Bedienung: Mausrad (ganze Zeilen), Rollbalken rechts, und beim
//  Ziehen eines Gegenstands rollt das Fenster von selbst, wenn man
//  an den oberen oder unteren Rand faehrt.
//
//  Idee (Koffer mit rollbarem Fenster) aus Keymaster von LennoxP90
//  (MIT). Keymaster ersetzt das Raster durch eigene Zeilen, das geht
//  nur bei 1x1-Schluesseln - hier bleibt das Spiel-Raster erhalten.
//
//  Sicherheitsnetz: Geht beim Einhaengen etwas schief, wird alles
//  zurueckgebaut und das normale Spiel-Fenster bleibt stehen. Ganz
//  abschaltbar im F12-Menue ("5. Quest Case" -> "Fenster rollen").
// ---------------------------------------------------------------
using System;
using System.Reflection;
using EFT.InventoryLogic;
using EFT.Settings.Game;
using EFT.UI;
using EFT.UI.DragAndDrop;
using HarmonyLib;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace AufgabenGepaeck
{
	internal static class QuestCaseFenster
	{
		// Muss zur Kennung im Server-Teil passen (Server/src/QuestCase.cs).
		internal const string KofferId = "61afb620726b3782be0c8801";

		// Private Felder ueber Harmony statt ueber den Publicizer - so
		// haengt nichts davon ab, ob Mono die Zugriffspruefung auslaesst.
		private static readonly AccessTools.FieldRef<GridWindow, ContainedGridsView> FensterRaster =
			AccessTools.FieldRefAccess<GridWindow, ContainedGridsView>("_containedGrids");

		public static void Anmelden(Harmony harmony)
		{
			MethodInfo ziel = AccessTools.Method(typeof(GridWindow), "Show", new Type[5]
			{
				typeof(CompoundItem),
				typeof(ItemContext),
				typeof(ItemController),
				typeof(ItemUiContext),
				typeof(GameSettingsGroup.EPriorityWindowMode)
			});
			if (ziel == null)
			{
				throw new MissingMethodException("GridWindow.Show(CompoundItem, ItemContext, ItemController, ItemUiContext, EPriorityWindowMode) gibt es nicht mehr");
			}
			harmony.Patch(ziel, null, new HarmonyMethod(typeof(QuestCaseFenster), "NachFensterAuf"));
			MainPlugin.Log.LogInfo("[Aufgaben-Gepaeck] Quest-Case-Fenster bereit.");
		}

		private static void NachFensterAuf(GridWindow __instance, CompoundItem compoundItem)
		{
			try
			{
				if (!MainPlugin.QuestCaseRollen.Value || compoundItem == null || compoundItem.StringTemplateId != KofferId)
				{
					return;
				}
				ContainedGridsView raster = FensterRaster(__instance);
				if (raster == null)
				{
					return;
				}
				QuestCaseRollbereich.Einhaengen(raster, compoundItem);
			}
			catch (Exception ex)
			{
				MainPlugin.Log.LogError("[Aufgaben-Gepaeck] Quest-Case-Fenster: " + ex.Message + " - normales Fenster bleibt.");
			}
		}
	}

	/// <summary>
	/// Rahmen um das Spiel-Raster: zeigt einen Ausschnitt und verschiebt
	/// das Raster darin. Liegt an der Stelle, an der vorher das Raster im
	/// Fenster lag; das Raster selbst haengt darunter.
	/// </summary>
	internal sealed class QuestCaseRollbereich : MonoBehaviour, IScrollHandler, IEventSystemHandler
	{
		private const float Zelle = 63f;

		private const float BalkenBreite = 10f;

		private const float BalkenAbstand = 2f;

		// Platz, der ueber dem Raster fuer Kopfzeile und Rahmen des Fensters bleibt
		private const float FensterRand = 160f;

		// Beim Ziehen: so nah am Rand (px) beginnt das Fenster zu rollen ...
		private const float KantenBand = 40f;

		// ... und zwar eine Zeile je so viele Sekunden.
		private const float KantenTakt = 0.15f;

		private static readonly AccessTools.FieldRef<ItemUiContext, DragItemContext> GezogenFeld =
			AccessTools.FieldRefAccess<ItemUiContext, DragItemContext>("_dragItemContext");

		private RectTransform _rahmen;

		private RectTransform _inhalt;

		private ContainedGridsView _raster;

		private CompoundItem _koffer;

		private LayoutElement _layout;

		private bool _inhaltHatFitter;

		private Canvas _leinwand;

		private Scrollbar _balken;

		private RectTransform _balkenRt;

		private bool _balkenWirdGesetzt;

		private float _oben;

		private float _inhaltB;

		private float _inhaltH;

		private float _sichtH;

		private float _naechsteKante;

		private Vector2 _zuletztGesetzt = new Vector2(-1f, -1f);

		private readonly Vector3[] _ecken = new Vector3[4];

		public static void Einhaengen(ContainedGridsView raster, CompoundItem koffer)
		{
			RectTransform inhalt = raster.transform as RectTransform;
			Transform platz = (inhalt != null) ? inhalt.parent : null;
			if (inhalt == null || platz == null)
			{
				return;
			}
			if (platz.GetComponent<QuestCaseRollbereich>() != null)
			{
				return; // schon eingehaengt
			}

			// Lage des Rasters merken - fuer den Rahmen und fuers Zurueckbauen
			int stelle = inhalt.GetSiblingIndex();
			Vector2 ankerMin = inhalt.anchorMin;
			Vector2 ankerMax = inhalt.anchorMax;
			Vector2 drehpunkt = inhalt.pivot;
			Vector2 lage = inhalt.anchoredPosition;
			Vector2 groesse = inhalt.sizeDelta;

			GameObject neu = new GameObject("QuestCaseRollbereich", typeof(RectTransform));
			try
			{
				RectTransform rahmen = (RectTransform)neu.transform;
				rahmen.SetParent(platz, false);
				rahmen.SetSiblingIndex(stelle);
				rahmen.anchorMin = ankerMin;
				rahmen.anchorMax = ankerMax;
				rahmen.pivot = drehpunkt;
				rahmen.anchoredPosition = lage;
				rahmen.localScale = Vector3.one;
				// Maske: zeigt nur den Ausschnitt und laesst Klicks
				// ausserhalb davon gar nicht erst bis zum Raster durch.
				neu.AddComponent<RectMask2D>();
				LayoutElement layout = neu.AddComponent<LayoutElement>();

				inhalt.SetParent(rahmen, false);
				inhalt.anchorMin = new Vector2(0f, 1f);
				inhalt.anchorMax = new Vector2(0f, 1f);
				inhalt.pivot = new Vector2(0f, 1f);
				inhalt.anchoredPosition = Vector2.zero;

				QuestCaseRollbereich bereich = neu.AddComponent<QuestCaseRollbereich>();
				bereich.Starten(rahmen, inhalt, raster, koffer, layout);
			}
			catch
			{
				// Zurueckbauen: Raster wieder an seinen alten Platz
				if (inhalt != null && inhalt.parent != platz)
				{
					inhalt.SetParent(platz, false);
					inhalt.SetSiblingIndex(stelle);
					inhalt.anchorMin = ankerMin;
					inhalt.anchorMax = ankerMax;
					inhalt.pivot = drehpunkt;
					inhalt.anchoredPosition = lage;
					inhalt.sizeDelta = groesse;
				}
				UnityEngine.Object.Destroy(neu);
				throw;
			}
		}

		private void Starten(RectTransform rahmen, RectTransform inhalt, ContainedGridsView raster, CompoundItem koffer, LayoutElement layout)
		{
			_rahmen = rahmen;
			_inhalt = inhalt;
			_raster = raster;
			_koffer = koffer;
			_layout = layout;
			_layout.flexibleWidth = 0f;
			_layout.flexibleHeight = 0f;
			// Bringt das Raster selbst eine Groessenregel mit, lassen wir
			// es seine Groesse selbst bestimmen und lesen sie nur ab.
			_inhaltHatFitter = inhalt.GetComponent<ContentSizeFitter>() != null;
			Canvas c = GetComponentInParent<Canvas>();
			_leinwand = (c != null) ? c.rootCanvas : null;
			_balken = BalkenBauen();
			Messen();
			Anwenden();
		}

		private void LateUpdate()
		{
			if (_inhalt == null)
			{
				// Fenster wird gerade abgebaut
				UnityEngine.Object.Destroy(base.gameObject);
				return;
			}
			Messen();
			KanteBeimZiehen();
			Anwenden();
		}

		/// <summary>Wie gross ist das Raster wirklich? Drei Quellen, die
		/// groesste gewinnt: das Layout des Spiels, die tatsaechliche Lage
		/// der Raster-Ansichten und die Rastergroesse des Koffers selbst
		/// (die stimmt schon, bevor das Layout gerechnet hat).</summary>
		private void Messen()
		{
			float b = LayoutUtility.GetPreferredWidth(_inhalt);
			float h = LayoutUtility.GetPreferredHeight(_inhalt);
			if (_inhaltHatFitter)
			{
				b = Mathf.Max(b, _inhalt.rect.width);
				h = Mathf.Max(h, _inhalt.rect.height);
			}
			GridView[] ansichten = _raster.GridViews;
			if (ansichten != null)
			{
				foreach (GridView ansicht in ansichten)
				{
					if (ansicht == null || !ansicht.gameObject.activeSelf)
					{
						continue;
					}
					((RectTransform)ansicht.transform).GetWorldCorners(_ecken);
					for (int i = 0; i < 4; i++)
					{
						// Ursprung des Inhalts = linke obere Ecke (Drehpunkt 0/1)
						Vector3 p = _inhalt.InverseTransformPoint(_ecken[i]);
						b = Mathf.Max(b, p.x);
						h = Mathf.Max(h, 0f - p.y);
					}
				}
			}
			Grid[] faecher = _koffer.Grids;
			if (faecher != null && faecher.Length == 1 && faecher[0] != null)
			{
				b = Mathf.Max(b, faecher[0].GridWidth * Zelle);
				h = Mathf.Max(h, faecher[0].GridHeight * Zelle);
			}
			_inhaltB = Mathf.Max(b, Zelle);
			_inhaltH = Mathf.Max(h, Zelle);
		}

		private float HoechsteSichtHoehe()
		{
			float max = MainPlugin.QuestCaseZeilen.Value * Zelle;
			if (_leinwand != null)
			{
				// Bildschirmhoehe in den Einheiten des Rahmens
				RectTransform l = (RectTransform)_leinwand.transform;
				float massstab = (_rahmen.lossyScale.y > 0.0001f) ? (l.lossyScale.y / _rahmen.lossyScale.y) : 1f;
				float bildschirm = l.rect.height * massstab;
				if (bildschirm > 0f)
				{
					max = Mathf.Min(max, bildschirm - FensterRand);
				}
			}
			return Mathf.Max(max, 3f * Zelle);
		}

		private float Spielraum => Mathf.Max(0f, _inhaltH - _sichtH);

		private void Anwenden()
		{
			_sichtH = Mathf.Min(_inhaltH, HoechsteSichtHoehe());
			bool mitBalken = _inhaltH > _sichtH + 0.5f;
			float breite = _inhaltB + (mitBalken ? (BalkenAbstand + BalkenBreite) : 0f);
			Vector2 soll = new Vector2(breite, _sichtH);
			if (soll != _zuletztGesetzt)
			{
				// Nur bei Aenderung anfassen - sonst baut das Layout jedes Bild neu.
				_zuletztGesetzt = soll;
				_rahmen.sizeDelta = soll;
				_layout.minWidth = breite;
				_layout.preferredWidth = breite;
				_layout.minHeight = _sichtH;
				_layout.preferredHeight = _sichtH;
			}
			if (!_inhaltHatFitter)
			{
				Vector2 inhaltSoll = new Vector2(_inhaltB, _inhaltH);
				if (_inhalt.sizeDelta != inhaltSoll)
				{
					_inhalt.sizeDelta = inhaltSoll;
				}
			}
			_oben = Mathf.Clamp(_oben, 0f, Spielraum);
			Vector2 lage = new Vector2(0f, _oben);
			if (_inhalt.anchoredPosition != lage)
			{
				_inhalt.anchoredPosition = lage;
			}
			if (_balken != null)
			{
				if (_balken.gameObject.activeSelf != mitBalken)
				{
					_balken.gameObject.SetActive(mitBalken);
				}
				if (mitBalken)
				{
					_balkenRt.anchoredPosition = new Vector2(_inhaltB + BalkenAbstand, 0f);
					_balkenRt.sizeDelta = new Vector2(BalkenBreite, _sichtH);
					_balkenWirdGesetzt = true;
					_balken.size = Mathf.Clamp01(_sichtH / _inhaltH);
					_balken.SetValueWithoutNotify((Spielraum > 0f) ? (_oben / Spielraum) : 0f);
					_balkenWirdGesetzt = false;
				}
			}
		}

		/// <summary>Um ganze Zeilen rollen; landet immer auf einer Zeilengrenze.</summary>
		private void Rollen(int richtung, int zeilen)
		{
			float ziel = Mathf.Round(_oben / Zelle) * Zelle + richtung * zeilen * Zelle;
			_oben = Mathf.Clamp(ziel, 0f, Spielraum);
			Anwenden();
		}

		public void OnScroll(PointerEventData eventData)
		{
			float d = eventData.scrollDelta.y;
			if (Mathf.Approximately(d, 0f))
			{
				return;
			}
			Rollen((d > 0f) ? (-1) : 1, MainPlugin.QuestCaseRaste.Value);
		}

		/// <summary>Zieht man einen Gegenstand an den oberen oder unteren
		/// Rand, rollt das Fenster nach - sonst kaeme man beim Ablegen nicht
		/// an die Zeilen ausserhalb des Ausschnitts.</summary>
		private void KanteBeimZiehen()
		{
			if (Time.unscaledTime < _naechsteKante || Spielraum <= 0f)
			{
				return;
			}
			ItemUiContext ui = ItemUiContext.Instance;
			if (ui == null || GezogenFeld(ui) == null)
			{
				return;
			}
			Camera kamera = (_leinwand == null || _leinwand.renderMode == RenderMode.ScreenSpaceOverlay) ? null : _leinwand.worldCamera;
			if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(_rahmen, Input.mousePosition, kamera, out Vector2 p))
			{
				return;
			}
			Rect r = _rahmen.rect;
			if (p.x < r.xMin || p.x > r.xMax)
			{
				return;
			}
			int richtung = 0;
			if (p.y > r.yMax - KantenBand && p.y < r.yMax + KantenBand)
			{
				richtung = -1;
			}
			else if (p.y < r.yMin + KantenBand && p.y > r.yMin - KantenBand)
			{
				richtung = 1;
			}
			if (richtung != 0)
			{
				_naechsteKante = Time.unscaledTime + KantenTakt;
				Rollen(richtung, 1);
			}
		}

		/// <summary>Schlichter senkrechter Rollbalken aus Unity-Bausteinen.
		/// Bewusst selbst gebaut statt aus einem Spiel-Balken kopiert: eine
		/// Kopie traegt den Zustand ihres Vorbilds mit (ausgeblendet,
		/// Ereignisse), das hier hat keine Ueberraschungen.</summary>
		private Scrollbar BalkenBauen()
		{
			GameObject balken = new GameObject("Rollbalken", typeof(RectTransform));
			_balkenRt = (RectTransform)balken.transform;
			_balkenRt.SetParent(_rahmen, false);
			_balkenRt.anchorMin = new Vector2(0f, 1f);
			_balkenRt.anchorMax = new Vector2(0f, 1f);
			_balkenRt.pivot = new Vector2(0f, 1f);
			Image grund = balken.AddComponent<Image>();
			grund.color = new Color(1f, 1f, 1f, 0.06f);

			GameObject bahn = new GameObject("Bahn", typeof(RectTransform));
			RectTransform bahnRt = (RectTransform)bahn.transform;
			bahnRt.SetParent(_balkenRt, false);
			bahnRt.anchorMin = Vector2.zero;
			bahnRt.anchorMax = Vector2.one;
			bahnRt.offsetMin = Vector2.zero;
			bahnRt.offsetMax = Vector2.zero;

			GameObject griff = new GameObject("Griff", typeof(RectTransform));
			RectTransform griffRt = (RectTransform)griff.transform;
			griffRt.SetParent(bahnRt, false);
			griffRt.offsetMin = Vector2.zero;
			griffRt.offsetMax = Vector2.zero;
			Image griffBild = griff.AddComponent<Image>();
			griffBild.color = new Color(0.78f, 0.78f, 0.72f, 0.55f);

			Scrollbar sb = balken.AddComponent<Scrollbar>();
			sb.handleRect = griffRt;
			sb.targetGraphic = griffBild;
			sb.direction = Scrollbar.Direction.TopToBottom;
			sb.numberOfSteps = 0;
			Navigation keine = default(Navigation);
			keine.mode = Navigation.Mode.None;
			sb.navigation = keine;
			sb.onValueChanged.AddListener(BalkenBewegt);
			balken.SetActive(false);
			return sb;
		}

		private void BalkenBewegt(float wert)
		{
			if (_balkenWirdGesetzt)
			{
				return;
			}
			_oben = Mathf.Clamp01(wert) * Spielraum;
			Anwenden();
		}
	}
}
