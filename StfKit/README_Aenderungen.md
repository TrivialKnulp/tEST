# Änderungen (Stand 14.11.2026)

* **PdfExtractor.cs** war im Archiv beschädigt (Zeilen wie `baseline=0,79 * size ... ));)`) und hätte nicht kompiliert: wiederhergestellt, JPEG-Logo-Umwandlung (System.Drawing) bleibt.
* **Operations.AppendManifest**: `if (File.Exists)` -> `if (!File.Exists(mf))` (Kopfzeile von manifest.csv).
* **Scans** (Seite = nur ein Bild, kein Text, z. B. WIRTHAND_…, unterschriebene 2. Änderungssatzung): das Bild wird als Hintergrund erkannt (`IrBox.Background`), auf die Seite begrenzt
  und NICHT mehr als 7-MB-@Picture ins .stf geschrieben. Der Text wird per Ollama-Bildmodell gelesen (`Qdrant.OcrModel`, Standard `qwen3.8:27b`, leer = aus);
  Positionen sind dabei künstlich (Fließtext). Im Bericht steht "Scan ... OCR" bzw. "Scan ohne Text". Examples: `--ollama http://kiserver:11434 [--ocr-model ...]`.
* **Vorschau-PDF** zeichnet jetzt echte @Picture-Bilder (PNG: Graustufen/RGB/Palette/RGBA); vorher nur eine leere Rahmen-Box.
* **Word**: Text in Textfeldern/Formen steht in `mc:AlternateContent` doppelt (Choice + Fallback) - das führte zu doppelten Tabellen/Absätzen. Es wird nur noch die Choice-Fassung gelesen (DocxLayout, DocxNotes, CorpusLoader).

## Qdrant - Anforderungen von Herrn Haneke und wo sie umgesetzt sind
| Anforderung | Umsetzung |
|---|---|
| Tabelle (Sammlung) ändern | `appsettings.json` -> `StfKit:Qdrant:Collection` (Standard `Formularerstellung`) |
| ID: wenn keine ID übergeben wird, UUID anders erzeugen | `CorpusLoader.Uuid(root, relPath)` - UUID aus Ordner + relativem Pfad (stabil: erneutes Laden überschreibt statt zu doppeln) |
| embedData = Text der Vorlage (Suche), data = Text der .stf | Payload `embed_data` und `data` (CorpusLoader, Upsert) |
| PDF: kein "split chunks", ganzer Text zur Suche | ein Punkt je Dokument, voller Text im BM25-Vektor; dichter Vektor bis `MaxEmbedChars` (bei Fehler wird halbiert) |
| `avg_len` je Item | `bm25.options.avg_len` = durchschnittliche Länge aller `embed_data` (einheitlich für alle Einträge) |
| Löschen von Einträgen | `POST /corpus/delete` (ohne `confirm=true` nur zählen) |
| Frage-Datenbank: Tabelle + "Frage" = kompletter PDF-Text | `POST /corpus/search`: kompletter Text des Kundenformulars -> Embedding + BM25 -> hybride Suche (RRF) in der gewählten Sammlung |

## Zentrale Datenbank (DatenFormulare_3926.fdb5) und "neueste Fassungen"
Die Firebird-Datei wird NICHT direkt per SQL beschrieben (Aeneas-eigenes Speicherformat, Logik/Versionen würden umgangen). Der Weg ist der von Aeneas selbst:
1. `FormulareExport` (aeneas-integration) exportiert den aktuellen Stand der zentralen DB in einen Ordner (nur lesend).
2. `StfKit sync-plan "T:\...\A - im Echtbetrieb;T:\...\Formular" <db-export> <paket>` vergleicht Ordner mit DB-Stand (je Name die neueste Datei):
   * `manifest.csv` + .stf = nur NEUE Formulare (KI-Ergebnisse, von Hand erstellte) -> `FormulareImport` (erst dryRun), Modus Neu.
   * `manifest_geaendert.csv` = gleicher Name, anderer Inhalt: nur zur Prüfung (Mensch entscheidet, was neuer ist), wird nicht automatisch importiert.
3. `StfKit qdrant-load` / `/corpus/add` hält die Qdrant-Suche auf dem neuesten Stand.

## Typen nicht fest (Bestand mit tausenden verschiedenen Formularen)
Die Regelliste (Gebührenbescheid, Anordnung, Antrag, …) ist nur noch ein Startwert. `FormClassifier.Refine` bestimmt den Typ aus den ähnlichsten Formularen des Bestands
(gewichtete Mehrheit der Treffer ab Ähnlichkeit 0,5): bei unsicherer Regel (< 0,60 oder "Sonstiges") oder fast identischem Vorbild (>= 0,80) mit anderem Typ gewinnen die Nachbarn,
und der Typname ist der des Bestands - also beliebig, nicht auf die Regelliste beschränkt. Neue Typen für die Regeln: `FormClassifier.LoadExtraKinds(<datei>)`.
Der Bestand kann der DB-Export (`DbInspect export`) mit allen Formularen der zentralen Datenbank sein (`CorpusDirs`).


## v8 - Katalog nach Lastenheft (Quellen, Versionen, Aktualität)

**Entscheidung zum Original-Archiv:** Originale bleiben in ihren Quellen (STF in der zentralen Firebird-DB, Dateien in den AENEAS-Ordnern). Es wird kein zweites Archiv aufgebaut; der Katalog verweist nur mit Pfad + Hash. Nicht-STF-Formate werden vorerst nicht kopiert/migriert.

### 1. Katalog bauen
```
StfKit.Cli catalog catalog.config.json <ausgabeordner>
```
Erzeugt `catalog.json` (maschinenlesbar), `katalog.csv` (Excel) und `katalog-bericht.md` (Migrationsbericht: Anzahl, neu/geändert/unverändert/entfernt, Duplikate, Konflikte, nicht gelesene Dateiarten).
Erneuter Lauf mit demselben Ausgabeordner ist **inkrementell** (Größe + Datum bzw. Hash; Firebird-Export nur nach Größe).
* **Quellen** stehen in `catalog.config.json` (Firebird-Export von `DbInspect export`, "A - im Echtbetrieb", älterer Ordner). Rolle/Priorität sind Konfiguration. **Die Rollen der beiden Ordner bitte bestätigen lassen** (im Lastenheft widersprüchlich).
* Gelesen werden nur die konfigurierten Endungen (`Extensions`, je Quelle überschreibbar). PDF, MSG, Bilder usw. (Kundenunterlagen, personenbezogene Daten) werden nur gezählt, nie geöffnet.
* **Name_Nr_Mandant** (so heißen die Formulare in der Datenbank) liefert Basisname, Version und Mandant; gleiche Basis + Mandant = ein Formular (`FormularId`), egal in welchem Format/welcher Quelle.
* **Status** je Formular: `Aktuell`, `Ersetzt` (niedrigere Versionsnummer), `Historisch`, `Ungeprüft`, `Unklar` (z. B. Echtbetrieb widerspricht höchster DB-Version, oder gleiche Versionsnummer mit unterschiedlichem Inhalt) - jeweils mit Begründung. Nie nur nach Dateidatum; Status ist eine Einstufung, keine Freigabe.
* **Duplikate** (gleicher Inhalt, auch bzip2 vs. entpackt): eine Kopie wird bevorzugt (kleinste Priority), die anderen verweisen darauf (`DuplikatVon`).

### 2. Suche mit Katalog
```
StfKit.Examples <ordner> <arbeitsordner> --catalog <ausgabeordner>\catalog.json [--high 0.75] [--min 0.45]
```
`bericht.csv` bekommt: `Sicherheitsstufe` (hoch/mittel/niedrig, Lastenheft 7.3; **Schwellen unkalibriert**), `Status` (immer `Review Required`), `Kandidaten` (Top 5 mit Quelle, Version, Status, Wert), `Vergleich` (Zusammenfassung), `IRFehler`.
Ist die beste Vorlage `Historisch/Ersetzt`/`Unklar`, steht in PRÜFEN die aktuelle Fassung bzw. der Konflikt.

### 3. Weitere Dateien je Dokument
* `<name>.elemente.json`: Elemente mit document_id, page, element_type, text, position, confidence, source_region, field_type, placeholder_mapping, validation_errors (Lastenheft 12.2). `ir.schema.json` im Arbeitsordner = JSON Schema dazu. **Die Prüfung läuft im Programm (gleiche Regeln); ein externer Schema-Validator wurde nicht eingebunden.**
* `<name>.vergleich.csv`: Element | Existing | Original | Aktion (Übernehmen/Ändern/Ersetzen/Hinzufügen/Entfernen/Prüfen) | Begründung (Lastenheft 11), auf Absatzebene; Tabellen werden nur über Linienanzahl verglichen.

### 4. Firebird
`DbInspect export` entpackt bzip2 jetzt selbst und überschreibt gleichnamige Formulare nicht mehr (`_ID<n>`-Suffix). Nur Tabelle FORMULARE wird gelesen.
Corpus/Index lesen bzip2-komprimierte .stf ebenfalls direkt.

### Nicht enthalten / ungeprüft
Direkter inkrementeller Firebird-Zugriff (aktuell Umweg über `DbInspect export`), Qdrant-Migration, Dokumentenarchiv, DOC-Konvertierung, visueller Bildvergleich, Testbefüllung, Review-Oberfläche, Retry/Verlauf. FormulareImport, n8n-Mail, OCR sind weiterhin nicht live getestet.


## v8.1 - Korrekturen nach Abgleich mit der Mail von Herrn Haneke
* **Markierungen im PDF** (neu: `StfKit.Core/PdfHighlights.cs`, `PdfExtractor`: Füllfarbe der Rechtecke `IrBox.Fill`): dunkel/gelb/rot gefüllte Rechtecke mit Text darin werden wie Word-Markierungen behandelt (Datum -> `<DATUM>`, Betrag -> `<BETRAG>`, Jahr -> `<JAHR>`, Namensmuster). Nicht erkennbare Werte stehen im Bericht (PRÜFEN), nichts wird geraten. Flächen > 25 % der Seite (Hintergrund) und helle Flächen werden ignoriert. Aufruf in `/analyze` direkt nach der PDF-Extraktion. Core-Logik mit Testseite geprüft; die Änderung in `PdfExtractor` (PdfPig-API `IsFilled`/`FillColor.ToRGBValues()`) ist NICHT kompiliert, weil NuGet fehlte - bitte einmal bauen und mit einem echten PDF testen.
* **n8n**: Feld "E-Mail für Benachrichtigung" ist jetzt Pflicht (die Mail "KI ist fertig" geht damit immer raus).
* **catalog.config.json**: Rollen nach der Mail: `AENEAS` = aktuell (Priority 0, CurrentHint), `A - im Echtbetrieb` = älter (Priority 2). Bitte von Herrn Haneke bestätigen lassen.

### Weiterhin offen (nicht durch Code lösbar oder nicht umgesetzt)
Auswahl Verzeichnis/Ziel-DB im n8n-Formular, Testbefüllung/Review-Oberfläche, Kalibrierung der Sicherheitsstufen, echte `apiUrl` und Pfade (`T:`/`\\NAS\` vs. `Z:`), LibreOffice-Installation für .doc, Nachweis des Lerneffekts, Katalog/sync-plan als n8n-Schritte.


## v9 - Firebird -> Qdrant: nur Fehlendes laden
* `StfKit.Cli qdrant-load <ordner> --dry-run [--list]` liest die vorhandenen Punkte der Collection (nur document_id, name, .stf-Inhalt; keine Vektoren) und zeigt, welche Formulare fehlen. Es wird nichts geschrieben.
* `... --only-missing` lädt nur diese. "Vorhanden" = gleiche ID (gleicher relativer Pfad) **oder** gleicher .stf-Inhalt (BOM und Zeilenende egal, d. h. dieselbe Datei aus Ordner und aus Firebird zählt als gleich). Gleicher Name mit anderem Inhalt = neue Fassung und wird geladen (alte bleibt; Aufräumen bewusst manuell via `qdrant-delete`).
* Nicht geladen werden Duplikate (gleicher Inhalt), leere Hüllen/Alias-Verweise und Formulare ohne Text (in der Ausgabe unter stats).
* bzip2-komprimierte .stf (Rohinhalt aus FORMULARE) werden direkt gelesen.
* `firebird-nach-qdrant.ps1` macht beides in einem Zug (Standard = Probelauf; geladen wird erst mit `-Ausfuehren`).
* Qdrant bleibt reiner Suchindex; Firebird wird nur gelesen (Tabelle FORMULARE).
* Nicht getestet: echter Qdrant-Server und Ollama-Embedding (bge-m3). Die Fehlend-Prüfung wurde gegen einen Mock-Server geprüft.


## v10 - Lücken zu den Anforderungen (Haneke-Mails, Lastenheft) geschlossen
Alle Punkte, die vorher "teilweise/offen" waren, sind jetzt im Code (offline mit Testdaten und Mock-Servern geprüft; echte Systeme siehe unten).
* **Ablauf/Status** (`Workflow.cs`): Prozess-ID, `status.json` (Processing / Review Required / Approved / Rejected / Imported / Import Failed / Failed), `verlauf.jsonl` (Protokoll, nur anhängen), `review.html` (Prüfseite), `mail.txt` (Text für die Mail "KI ist fertig"; n8n versendet).
* **Eingaben des Menschen**: `--mandant <name>` (Ziel-Mandant) und `--ziel Dateien|Importdatei|ZentraleDB` (Ergebnis a/b) beim Aufruf von `StfKit.Examples`; stehen in status.json, Protokoll und Importliste.
* **Wiederaufnahme**: `--resume` überspringt fertige Formulare und wiederholt Failed/Processing/Import Failed.
* **Prüfung**: `StfKit review list|approve|reject|correct|html|mail <arbeitsordner> [<formular>] [--by Name] [--note Text] [--file korrigiert.stf] [--kb <ordner>]`. `correct` gibt die korrigierte Fassung frei UND legt Original/Korrektur unter `lernen\` ab; mit `--kb` lernt die Wissensbasis daraus.
* **Übergabe**: `StfKit export <arbeitsordner> [--exec "<Befehl> {datei} {modus}"] [--dry-run]` schreibt `freigegeben\` + `importliste.csv`. Ziel ZentraleDB schreibt NUR über den angegebenen FormulareImport-Aufruf (`--exec`), nie per SQL; ohne `--exec` entsteht nur die Liste. Nur freigegebene Formulare werden übergeben.
* **Layout-Prüfung** (`LayoutCheck.cs`): Koordinatenvergleich Original (PDF) gegen erzeugte .stf (mm-Abweichung je Zeile, Linien/Rahmen, Seitenzahl) im Bericht. Ehrlich: kein Pixelvergleich; dafür gibt es die Vorschau mit rotem Original-Overlay.
* **Testbefüllung**: `<name>.testdruck.pdf` = Formular mit künstlichen Beispieldaten in allen Platzhaltern (nach Namensmuster, nie echte Personendaten) - der "ausgefüllte Ausdruck" zur Prüfung.
* **Felder per Sprachmodell** (`LlmFields.cs`): `--llm-felder http://kiserver:11434 [--llm-model qwen3.8:27b]` bzw. `StfKit llm-fields <ir.json> --ollama <url>`; qwen liefert JSON (Feldname, Typ, Platzhalter, confidence), wird gegen das Schema geprüft, bei Fehlern ein Wiederholungsversuch. Ergebnis sind nur Vorschläge (`*.llmfelder.json`, Hinweis im Prüfbericht).
* **Auswertung/Kalibrierung**: `--erwartet <csv Name;Erwartet>` -> `auswertung.txt` (Precision@1, Recall@5, No-Match, Falschtreffer, Schwellentabelle).
* **Inhaltsähnlichkeit im Katalog**: dasselbe Formular unter anderem Namen in anderer Quelle wird verknüpft (Schwelle `ContentLinkThreshold`, Standard 0.95, 0 = aus). Bei gleichem Briefkopf können niedrigere Werte falsch verknüpfen - an echten Daten prüfen.
* **Automatische Aktualisierung**: `katalog-aktualisieren.ps1` (inkrementell, optional Qdrant nur Fehlendes); `-TaskAnlegen -Uhrzeit 02:00` legt die tägliche Aufgabe in der Windows-Aufgabenplanung an.
* **Word/RTF/ODT/DOC ohne PDF**: werden per LibreOffice in PDF/DOCX umgewandelt (wenn installiert).

### Nur mit echten Systemen prüfbar
Echte Firebird-Verbindung und `StfKit.DbInspect` (nicht kompiliert), FormulareImport samt Backup/Rollback, n8n-Mailversand, echtes Qdrant/Ollama (bge-m3, qwen), PDF-Extraktion und Markierungen auf echten PDFs. Schwellen 0.75/0.45 sind nicht kalibriert (50-100 echte Dokumente + `--erwartet`).

## v11 - Abweichungen von den Anforderungen (Mails Haneke) behoben
Geprüft mit den Beispieldateien (q.zip) und Testdokumenten; Testbuild .NET 8 + PdfPig 0.1.9 (siehe tests/).
* **Markierungen im PDF** (`PdfHighlights.cs`, jetzt im `PdfExtractor`, also in CLI **und** API): gefüllte Flächen (dunkel/gelb/rot) mit Text darin, **wortgenau** (vorher ganze Zeile, 6 von 9 Stellen, CLI 0). Ihr Word-Muster als PDF: alle 9 Stellen enthalten (8 Treffer, zwei benachbarte zusammengefasst). 6 echte Kunden-PDFs: 0 falsche Treffer.
* **Kommentare/Markierungen im PDF** (Anmerkungen): Haftnotiz, Textfeld, Textmarker/Unterstreichung mit Text werden als Kommentar bzw. Markierung gelesen. Eine Haftnotiz hat keinen sicheren Anker und ordnet deshalb nie automatisch einen Platzhalter zu (nur Hinweis für den Menschen).
* **Zerrissene Platzhalter** (`PlaceholderRepair.cs`): `<B_` / `Pers` / `onen>` in schmalen Zellen wird wieder `<B_Personen>`; unplausible Verbindungen werden verworfen.
* **Word: dunkle Zellen/Absatzschattierung** (`DocxNotes`): kurze Werte in gelb/rot/dunkel schattierten Zellen gelten als Markierung; Tabellenköpfe werden übersprungen (Test: 5 von 5 Stellen, vorher 4).
* **Katalog**: mehrere Versionen im Hinweis-Ordner -> höchste Versionsnummer = Aktuell, die anderen Ersetzt (vorher "Unklar"/Scheinkonflikt). Echte Konflikte (Hinweis-Ordner älter als zentrale DB) bleiben "Unklar" (geprüft).
* **/scan** (Verzeichnis-Modus): fehlende PDF/DOCX zu Word/.doc werden per LibreOffice in einem Arbeitsordner erzeugt, Original bleibt unverändert (22/22 Beispiele laufen).
* **Pfade** wie in der Mail: `T:\Kseniia Umrykhina\ki\Formular\Aeneas` (aktuell) und `T:\Kseniia Umrykhina\ki\Formular\A - im Echtbetrieb` (älter) in appsettings.json, catalog.config.json und den Vorgaben von Pruefung-Systeme.ps1 (korrigiert: nicht die Z:-Pfade aus der Mail) (ohne verbundenes Laufwerk Z: den UNC-Pfad eintragen).

### Bekannt / nicht gelöst
* PDF-Markierungen, die selbst `<Platzhalter in Klartext>` enthalten (z. B. `<Datum letzter Gebührenbescheid>`), bekommen ohne Katalog-/Modellzuordnung (`LlmFields`, Word-Kommentare) keinen AENEAS-Platzhalter: sie stehen im Bericht unter "PRÜFEN (Mensch)". Datum/Betrag/Jahr als Klartext werden automatisch zugeordnet.
* Rest-Zerreißung `<B_Mona` + `tGesamt` (das `>` steht an anderer Stelle im PDF).
* Nicht prüfbar ohne Ihre Systeme: echte Firebird/FormulareImport, n8n-Mail, Qdrant/Ollama, Windows-Aufgabenplanung (`katalog-aktualisieren.ps1`).

## v11.1 - Platzhalter in Worten, zerrissene Platzhalter, Systemprüfung
* **Markierung mit Platzhalter in Worten** (`PhraseMatcher.cs`, `KnowledgeBase.Phrases`, `Operations.Generate`): drei Stufen, nichts wird geraten.
  1. **Gedächtnis** (`<kb>/phrases.json`): jede Word-Markierung mit Kommentar (`<UVOR_BescheidDatum>`) wird beim Erzeugen gemerkt; dieselbe Formulierung in einem PDF ohne Kommentar bekommt den Platzhalter automatisch. Von Hand: `StfKit phrase-add <kb> "Datum letzter Gebührenbescheid" UVOR_BescheidDatum`, `phrase-import <kb> <csv>` (Zeilen `Text;Platzhalter`), `phrase-list <kb>`.
  2. **Katalog** (Platzhalter-Namen aus dem Korpus, `--catalog <ordner>` im CLI, in der API automatisch aus `CorpusDirs`): nur bei Ähnlichkeit >= 0,85 und deutlichem Abstand zum Zweitbesten automatisch (z. B. "Betrag letzte Gebühr" -> `B_BetragLetzteGebuehr`); Namen mit Leerzeichen zählen nicht als Platzhalter.
  3. **Vorschlag im Bericht** ("PRÜFEN (Mensch)": `... → Vorschlag: UVOR_BescheidDatum (0,53), ...`). Mehrere `<...>` in einer Markierung werden einzeln behandelt.
  Messung (Wohnungsgeber-Muster als PDF, 8 Markierungen): ohne Gedächtnis/Katalog 0 automatisch; mit Katalog 2; mit Gedächtnis aus dem Word-Muster 5; Rest mit Vorschlägen.
* **Zerrissener Platzhalter** `<B_Mona` + `tGesamt`: `PlaceholderRepair.ApplyKnown` setzt ihn zusammen, wenn der Name im Word-Text, in Kommentaren oder im Katalog bekannt ist, und entfernt das verwaiste `>` (Test: `<B_MonatGesamt>`, 0 offene Platzhalter). Ohne Word/Katalog bleibt die vorsichtige Variante (nur wenn das `>` direkt folgt).
* **Pruefung-Systeme.ps1** - Systemprüfung für Ihre Umgebung (PowerShell 5.1/7), siehe unten.

### Systemprüfung starten (Pruefung-Systeme.ps1)
Ziel: Firebird, Qdrant, Ollama, Mail, n8n, Aufgabenplanung, Laufwerk Z: und den Ablauf Datei -> Formular prüfen. Jeder Schritt endet mit PASS / WARN / FAIL / SKIP / INFO; Ende-Code 1, wenn etwas FAIL ist. Es werden nur temporäre Dateien/Sammlungen angelegt und gelöscht (Testdateien unter `<Ausgabeordner>\_systemtest\<Zeitstempel>`).

Voraussetzungen: StfKit-API läuft (`http://localhost:5077`), `StfKit.exe` (CLI) liegt neben dem Skript oder `-StfKit` angeben; Firebird-Prüfung braucht den Ordner `StfKit.DbInspect` und `$env:STFKIT_FB_PASSWORD`.

```powershell
cd <Kit-Ordner>
# 1) alles, was ohne Zusatzangaben geht (Laufwerk Z:, API, Ollama, Qdrant, Abgleich Qdrant <-> Z:)
.\Pruefung-Systeme.ps1
# 2) mit Beispielformular (Ende-zu-Ende) und Mailtest
.\Pruefung-Systeme.ps1 -BeispielPdf "T:\...\Muster.pdf" -BeispielDocx "T:\...\Muster.docx" `
   -Smtp mail.firma.de -Von ki@firma.de -An ich@firma.de        # SMTP-Anmeldung optional: $env:STFKIT_SMTP_USER / STFKIT_SMTP_PASSWORD
# 3) Qdrant mit Schreibtest (temporäre Sammlung)
.\Pruefung-Systeme.ps1 -Nur Qdrant -QdrantSchreibtest
# 4) Firebird (nur Lesen)
$env:STFKIT_FB_PASSWORD = "geheim"
.\Pruefung-Systeme.ps1 -Nur Firebird -DbInspect "T:\...\StfKit.DbInspect" -QdrantUrl http://192.168.1.241:6333
# 5) Geplante Aufgabe (Windows, Test wird sofort gelöscht) und n8n-Formular
.\Pruefung-Systeme.ps1 -Nur Aufgabe,N8n -Aufgabe -N8nUrl "http://<n8n>/form/<id>"
# Bericht als JSON:  -Bericht .\systemtest.json     Gruppen (-Nur): Basis,Pfade,Api,Ollama,Qdrant,Abgleich,Firebird,EndeZuEnde,Mail,N8n,Aufgabe,Import
```
Der Import in AENEAS selbst (`FormulareImport`) läuft nur innerhalb von AENEAS: der Schritt "Import" erzeugt ein Testpaket (`manifest.csv` + `Systemtest_KI.stf`) und druckt den Aufruf für den **Probelauf** (`dryRun = true`) mit der erwarteten Meldung.
Typische FAIL-Gründe: Laufwerk Z: im Dienstkonto nicht verbunden (UNC-Pfad nehmen), Ausgabeordner nicht unter `StfKit:AllowedRoots`, Ollama-Modell nicht geladen, Qdrant-Sammlung fehlt (erst `qdrant-load`).
Mit Mock-Diensten (Qdrant/Ollama/SMTP) und der echten API getestet: 13 PASS, 4 SKIP (Firebird, n8n, Aufgabe, ohne Angaben), 1 INFO; Negativtest ohne Dienste und ohne Laufwerk Z: ergibt FAIL mit Ende-Code 1.

### Weiterhin nicht prüfbar ohne Ihre Systeme
Echte Firebird/DbInspect, `FormulareImport` im AENEAS-Prozess, n8n-E-Mail-Knoten, echte Qdrant/Ollama-Qualität, Windows-Aufgabenplanung (das Skript prüft sie bei Ihnen mit `-Aufgabe`).

### v11.2 - Pruefung-Systeme.ps1: Abgleich hing 20 Minuten (aus der ersten echten Ausführung)
Ursache: der Schritt "Abgleich" las den ganzen Ordner `...\Aeneas` rekursiv (Netzlaufwerk, viele Kundendateien) und startete für jedes Word/RTF/doc LibreOffice; die Ausgabe wurde erst am Ende gezeigt, deshalb sah man nichts.
Jetzt: Live-Ausgabe des Programms, Zeitlimit `-AbgleichTimeoutSek` (Standard 300 s, danach Abbruch mit Hinweis), Standard `-AbgleichLimit 50` und **ohne** LibreOffice (nur mit `-AbgleichMitOffice`), bei Fehlern erscheinen die letzten Meldungen des Programms in der Fehlerzeile. Schritt ganz überspringen: `-Nur` mit allen Gruppen außer `Abgleich`.

### v11.3 - Pruefung-Systeme.ps1 unter Windows PowerShell 5.1 (zweite echte Ausführung)
Der Abgleich lief (aus 50 Formularen fehlten 31 in Qdrant), brach aber bei der Auswertung ab: `ConvertFrom-Json` in 5.1 verträgt keine leeren Schlüsselnamen (die Statistik zählt Dateien ohne Endung unter dem Schlüssel ""). Die Zahlen werden jetzt direkt aus dem JSON gelesen (`JsonZahl`).

### v11.4 - Pruefung-Systeme.ps1: Schritt "EndeZuEnde" zeigt jetzt die Zuordnungen
Zusätzlich zu den Zahlen (Markierungen / Kommentare / offen) werden die automatisch zugeordneten Platzhalter und die offenen Markierungen (mit Vorschlägen) aufgelistet (bis 12). Erste echte Ausführung mit "Vordruck Gebührenbescheid.pdf": 10 Markierungen, 0 Kommentare, 8 offen, 74 s.

### v11.5 - Auswertung der ersten echten Läufe (Monschau "Vordruck Gebührenbescheid.pdf")
Befund aus der Vorschau/den Dateien des Anwenders (Modus bescheid, Vorlage "Gebührenbescheid", Ähnlichkeit 0,74):
* **Layout stimmt nicht mit dem PDF überein**: von 78 zuordenbaren Zeilen lagen nur 8 innerhalb 1,5 mm, Median dy +8,1 mm, größte Abweichung 20,8 mm, wachsend mit der Seitenhöhe (+2,8 bis +6 mm je 100 mm). Ursache: im Modus "bescheid" (und "inherit") werden nur die TEXTE der ähnlichsten Vorlage angepasst, die Geometrie bleibt die der Vorlage (`FormAdapter.EditTemplate`).
* **Neu: Layout-Prüfung** (`LayoutCheck.cs`): `/generate` liefert `layout` (Zeilen innerhalb 1,5 mm, Median dx/dy, größte Abweichung, Steigung) und den Hinweis "PRÜFEN: Layout weicht vom PDF ab ... mode=scratch versuchen", wenn weniger als 60 % der Zeilen treffen. CLI: `StfKit layout-check <x.stf> <ir.json>`. Geprüft: Erzeugung "scratch" 62 von 62 Zeilen; künstlich um 6 % gestreckte STF 0 von 62 (Steigung 6,0 mm/100 mm erkannt); die echte Monschau-STF (Original-Layout aus dem roten Vorschau-Layer rekonstruiert) 8 von 78.
* **Pruefung-Systeme.ps1**: neuer Parameter `-Modus auto|scratch|inherit|bescheid`; Schritt "EndeZuEnde" zeigt Modus/Vorlage/Version/Import-Art, die Layout-Messung (WARN bei Abweichung) und wichtige Hinweise. Vergleich: `-Modus scratch` baut das Layout aus dem PDF.
* **Matcher**: reine Zahlen ("1" aus "Anzahl 1") erzeugen keine Vorschläge mehr (vorher `K_1_Name 0,85`); zusammengesetzte Beschriftungen werden teilweise vorgeschlagen ("Vorname / Nachname" -> Teile: Vorname→F_Vorname | Nachname→F_Name; "PLZ/ Ort" -> F_U_PLZ | F_U_Ort); Synonyme (nachname=name) vor dem Kürzen.

### Weitere Befunde aus demselben Lauf (nicht automatisch behoben)
* In der erzeugten STF steht noch statischer Text `Sachbearbeiter/in Cindy Radermacher` (Name aus dem Kundenmuster, sollte `<SB_Vorname> <SB_Name>` sein).
* Die Ergebnis-STF behält die Version der Vorlage (DB2014), angefordert war DB2026 (steht im Hinweis).
* Modus "Ersetzen" mit Ziel "Gebührenbescheid" überschreibt beim Import das vorhandene Formular gleichen Namens in der Ziel-DB: zuerst `FormulareImport` als Probelauf (`dryRun = true`), Backup-Ordner angeben.

### v11.6 - Layout an das PDF ausrichten (`LayoutSnap.cs`)
Zweiter echter Lauf (Monschau): Modus bescheid 8 von 79 Zeilen innerhalb 1,5 mm (WARN), Modus scratch 91 von 91 (aber "Import als: Neu", Plattform-Platzhalter der Vorlage gehen verloren, Gebührenbescheide lassen sich in Aeneas nicht über "Neu" anlegen).
Neu: Meldet die Layout-Prüfung "schlecht" (< 60 % der Zeilen), richtet `/generate` die **Geometrie der Vorlage am PDF aus** und behält Texte/Platzhalter der Vorlage (`align=false` schaltet das ab; CLI: `StfKit layout-snap <x.stf> <ir.json> --out <y.stf>`).
Verfahren: Textboxen, deren Wortlaut im PDF steht, rücken exakt auf die PDF-Zeile (Anker, Ausreißer > 8 mm vom Umfeld werden verworfen); alle anderen Elemente (Platzhalter-Boxen, Linien, Bilder, Kästen) erhalten die zwischen den Ankern linear interpolierte vertikale Verschiebung. Boxgrößen bleiben.
Messung: echte Monschau-STF (Original aus dem roten Vorschau-Layer rekonstruiert) 8 -> 59 von 78 Zeilen, Median dy +8,1 -> 0,0 mm; künstlich gestreckte STF 0 -> 56 von 62 (max. Abweichung 3,8 mm). Offen bleiben Bereiche, in denen die Vorlage anders aufgebaut ist als das PDF (Gebührentabelle, lange Absätze mit anderem Zeilenumbruch): dort reicht Verschieben nicht, die Boxen müssten umgebaut werden.
