// FormulareImport.cs - legt die von StfKit erzeugten Formulare direkt in der geöffneten Aeneas-Datenbank ab.
// Entspricht "Formulare bearbeiten > Vorlage laden" (Neu / Ersetzen), nur automatisiert für einen ganzen Ordner.
//
// Liest <ordner>\manifest.csv (von StfKit erzeugt):
//   Datei;Name;Art;Kategorie;Modus;Ziel;Vorlage;Aehnlichkeit;Hinweise
//   Modus "Neu"      -> neues Formular (Name, Art); existiert bereits eines mit diesem Namen, wird NICHT überschrieben (Meldung)
//   Modus "Ersetzen" -> ersetzt die bearbeitete Fassung des Formulars "Ziel" (z. B. Gebührenbescheid); die bisherige Fassung
//                       wird vorher in <backupOrdner> gesichert. Die Werksfassung ("Original") bleibt unberührt.
// dryRun=true schreibt nichts in die Datenbank und meldet nur, was passieren würde.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using flInterface;

namespace FlueMa
{
	public static class FormulareImport
	{
		public sealed class Eintrag
		{
			public string Datei, Name, Art, Kategorie, Modus, Ziel;
			public string Ergebnis;      // OK / ÜBERSPRUNGEN / FEHLER / PLAN
			public string Meldung;
		}

		static string[] SplitCsv(string line)
		{
			var res = new List<string>(); var sb = new StringBuilder(); bool q = false;
			for (int i = 0; i < line.Length; i++)
			{
				char c = line[i];
				if (q) { if (c == '"') { if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; } else q = false; } else sb.Append(c); }
				else if (c == '"') q = true;
				else if (c == ';') { res.Add(sb.ToString()); sb.Clear(); }
				else sb.Append(c);
			}
			res.Add(sb.ToString()); return res.ToArray();
		}

		static IFormular Finde(IFlueMa fm, FormularArt art, string name)
		{
			return fm.GetFormulare(art).FirstOrDefault(f =>
				string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(f.Nationalitaet));
		}

		/// <returns>je Zeile des Manifests ein Ergebnis</returns>
		public static List<Eintrag> ImportManifest(IFlueMa fm, string ordner, bool dryRun, string backupOrdner)
		{
			var res = new List<Eintrag>();
			var mf = Path.Combine(ordner, "manifest.csv");
			if (!File.Exists(mf)) throw new FileNotFoundException("manifest.csv nicht gefunden", mf);
			var lines = File.ReadAllLines(mf, Encoding.UTF8);
			for (int i = 1; i < lines.Length; i++)
			{
				if (string.IsNullOrWhiteSpace(lines[i])) continue;
				var c = SplitCsv(lines[i]);
				var e = new Eintrag { Datei = c.ElementAtOrDefault(0), Name = c.ElementAtOrDefault(1), Art = c.ElementAtOrDefault(2), Kategorie = c.ElementAtOrDefault(3), Modus = c.ElementAtOrDefault(4), Ziel = c.ElementAtOrDefault(5) };
				res.Add(e);
				try { Verarbeite(fm, ordner, e, dryRun, backupOrdner); }
				catch (Exception ex) { e.Ergebnis = "FEHLER"; e.Meldung = ex.Message; }
			}
			return res;
		}

		static void Verarbeite(IFlueMa fm, string ordner, Eintrag e, bool dryRun, string backupOrdner)
		{
			FormularArt art;
			if (!Enum.TryParse(e.Art, true, out art) || art == FormularArt.Alle) { e.Ergebnis = "FEHLER"; e.Meldung = "Unbekannte Art: " + e.Art; return; }
			string pfad = Path.Combine(ordner, e.Datei ?? "");
			if (!File.Exists(pfad)) { e.Ergebnis = "FEHLER"; e.Meldung = "Datei fehlt: " + e.Datei; return; }
			string txt = File.ReadAllText(pfad);
			if (string.IsNullOrWhiteSpace(txt)) { e.Ergebnis = "FEHLER"; e.Meldung = "Datei ist leer"; return; }
			string code = File.Exists(pfad + ".cs") ? File.ReadAllText(pfad + ".cs") : null;

			if (string.Equals(e.Modus, "Ersetzen", StringComparison.OrdinalIgnoreCase))
			{
				var alt = Finde(fm, art, e.Ziel);
				if (alt == null) { e.Ergebnis = "FEHLER"; e.Meldung = "Zu ersetzendes Formular '" + e.Ziel + "' (" + art + ") nicht gefunden - bitte zuerst anlegen oder Modus 'Neu' verwenden"; return; }
				if (dryRun) { e.Ergebnis = "PLAN"; e.Meldung = "würde '" + alt.Name + "' ersetzen (Sicherung der alten Fassung vorher)"; return; }
				Directory.CreateDirectory(backupOrdner);
				string bk = Path.Combine(backupOrdner, Sicher(alt.Name) + "_" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".stf");
				File.WriteAllText(bk, alt.AktDef() ?? "", new UTF8Encoding(false));
				alt.Bearbeitet = txt;
				if (code != null) alt.cBearbeitet = code;
				alt.DatenSpeichern(fm);
				e.Ergebnis = "OK"; e.Meldung = "'" + alt.Name + "' ersetzt, alte Fassung gesichert: " + bk;
			}
			else
			{
				if (Finde(fm, art, e.Name) != null) { e.Ergebnis = "ÜBERSPRUNGEN"; e.Meldung = "Formular '" + e.Name + "' existiert bereits - nicht überschrieben (Modus 'Ersetzen' mit Ziel verwenden)"; return; }
				if (dryRun) { e.Ergebnis = "PLAN"; e.Meldung = "würde neues Formular '" + e.Name + "' (" + art + ") anlegen"; return; }
				var nNeu = fm.NewItem<IFormular>();
				nNeu.Art = art;
				nNeu.Name = e.Name;
				nNeu.Bearbeitet = txt;
				nNeu.cBearbeitet = code;
				nNeu.Sichtbar = true;
				if (!string.IsNullOrWhiteSpace(e.Kategorie)) nNeu.Kategorie = e.Kategorie;
				nNeu.DatenSpeichern(fm);
				e.Ergebnis = "OK"; e.Meldung = "neu angelegt";
			}
		}

		static string Sicher(string s)
		{
			foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
			return s;
		}
	}
}
