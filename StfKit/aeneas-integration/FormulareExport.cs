// FormulareExport.cs - exportiert ALLE Formulare/Vorlagen der aktuellen Aeneas-Datenbank als Dateien
// (Korpus fuer die KI-Formularerstellung). Wird in das Projekt FlueMa (oder einen DV-Job) eingebunden.
//
// Ausgabe im Zielordner:
//   <Art>/<Kategorie>/<Name>[ (<Nationalitaet>)].stf   (bzw. .txt fuer Mail-/Export-/Code-Vorlagen, wie "Vorlage speichern")
//   <...>.cs                                             Code-Teil (cBearbeitet/cOriginal), falls vorhanden
//   index.csv                                            Name;Nationalitaet;Art;Kategorie;DokKategorie;Sichtbar;Datei;Platzhalter
// Es wird immer die aktuell gueltige Fassung gespeichert (AktDef() = Bearbeitet, sonst Original), genau wie
// "Formulare bearbeiten > Speichern unter". Nur lesend, aendert nichts in der Datenbank.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using flInterface;

namespace FlueMa
{
	public static class FormulareExport
	{
		static string San(string s)
		{
			if (string.IsNullOrWhiteSpace(s)) return "_";
			foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
			return s.Trim().TrimEnd('.');
		}

		static string Csv(string s) => "\"" + (s ?? "").Replace("\"", "\"\"") + "\"";

		/// <returns>Anzahl exportierter Formulare</returns>
		public static int ExportAlle(IFlueMa fm, string zielOrdner)
		{
			Directory.CreateDirectory(zielOrdner);
			var utf8 = new UTF8Encoding(false);
			var idx = new StringBuilder("Name;Nationalitaet;Art;Kategorie;DokKategorie;Sichtbar;Datei;Platzhalter\r\n");
			int n = 0;
			foreach (var f in fm.GetFormulare(FormularArt.Alle))
			{
				string def = f.AktDef();
				if (string.IsNullOrWhiteSpace(def)) continue;
				bool txt = f.Art == FormularArt.Mailvorlage || f.Art == FormularArt.ExportVorlage ||
				           f.Art == FormularArt.KassenExport || f.Art == FormularArt.DVJob || f.Art == FormularArt.SonstigerCode;
				string ext = txt ? "txt" : "stf";
				string dir = Path.Combine(zielOrdner, San(f.Art.ToString()), San(f.Kategorie));
				Directory.CreateDirectory(dir);
				string name = San(f.Name) + (string.IsNullOrWhiteSpace(f.Nationalitaet) ? "" : " (" + San(f.Nationalitaet) + ")");
				string path = Path.Combine(dir, name + "." + ext);
				for (int i = 2; File.Exists(path); i++) path = Path.Combine(dir, name + "_" + i + "." + ext);   // Namenskollisionen
				File.WriteAllText(path, def, utf8);                                                              // wie "Vorlage speichern"
				string code = f.AktCode();
				if (!string.IsNullOrWhiteSpace(code)) File.WriteAllText(path + ".cs", code, utf8);
				string ph = "";
				try { ph = string.Join(",", f.VerwendetePlatzhalter()); } catch { }
				idx.Append(string.Join(";", Csv(f.Name), Csv(f.Nationalitaet), Csv(f.Art.ToString()), Csv(f.Kategorie),
					Csv(f.DokKategorie), f.Sichtbar ? "1" : "0", Csv(path.Substring(zielOrdner.Length).TrimStart('\\', '/')), Csv(ph))).Append("\r\n");
				n++;
			}
			File.WriteAllText(Path.Combine(zielOrdner, "index.csv"), idx.ToString(), utf8);
			return n;
		}
	}
}
