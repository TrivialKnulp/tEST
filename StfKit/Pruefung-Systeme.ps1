<#
 Pruefung-Systeme.ps1 - prüft die Systeme, die nur bei Ihnen erreichbar sind (Firebird, Qdrant, Ollama, Mailserver, n8n, Aufgabenplanung, Laufwerk T:)
 und den Ablauf von der Datei bis zum fertigen Formular. Jeder Schritt ist einzeln, schreibt nichts Dauerhaftes
 (Tests legen nur temporäre Dateien/Sammlungen an und löschen sie wieder) und endet mit PASS / WARN / FAIL / SKIP / INFO.
 Gruppen (-Nur): Basis, Pfade, Api, Ollama, Qdrant, Abgleich, Firebird, EndeZuEnde, Mail, N8n, Aufgabe, Import

 Beispiele (PowerShell 5.1 oder 7):
   # alles, was ohne Zusatzangaben geht
   .\Pruefung-Systeme.ps1
   # mit Beispieldatei und Mailtest
   .\Pruefung-Systeme.ps1 -BeispielPdf "T:\...\Muster.pdf" -BeispielDocx "T:\...\Muster.docx" -Smtp mail.firma.de -Von ki@firma.de -An ich@firma.de
   # Firebird (Passwort nur über die Umgebung, wird nirgends gespeichert)
   $env:STFKIT_FB_PASSWORD = "geheim"; .\Pruefung-Systeme.ps1 -Nur Firebird -DbInspect "T:\...\StfKit.DbInspect"
   # nur Qdrant inkl. Schreibtest in einer Wegwerf-Sammlung
   .\Pruefung-Systeme.ps1 -Nur Qdrant -QdrantSchreibtest
#>
param(
  [string]$Api = "http://localhost:5077",
  [string]$QdrantUrl = "http://192.168.1.241:6333", [string]$Collection = "Formularerstellung", [switch]$QdrantSchreibtest,
  [string]$Ollama = "http://kiserver:11434", [string]$EmbedModel = "bge-m3:latest", [string]$ChatModel = "qwen3.8:27b",
  [string]$FormularOrdner = "T:\Kseniia Umrykhina\ki\Formular\Aeneas", [string]$AusgabeOrdner = "T:\Kseniia Umrykhina\ki\Formular\test\ausgabe",
  [string]$StfKit = "$PSScriptRoot\StfKit.exe", [int]$AbgleichLimit = 50, [int]$AbgleichTimeoutSek = 300, [switch]$AbgleichMitOffice,
  [string]$BeispielPdf = "", [string]$BeispielDocx = "", [switch]$MitQdrant, [ValidateSet("auto", "scratch", "inherit", "bescheid")][string]$Modus = "auto",
  [string]$DbInspect = "", [string]$FbHost = "main", [string]$FbDatei = "/data/db/DatenFormulare_3926.fdb5",
  [string]$Smtp = "", [int]$SmtpPort = 25, [switch]$SmtpSsl, [string]$Von = "", [string]$An = "",
  [string]$N8nUrl = "",
  [switch]$Aufgabe, [string[]]$Nur = @(), [string]$Bericht = ""
)
$ErrorActionPreference = "Stop"
$Nur = @($Nur | ForEach-Object { $_ -split "[,; ]+" } | Where-Object { $_ })     # -Nur "Api,Qdrant" funktioniert auch mit powershell -File
$script:Ergebnisse = New-Object System.Collections.ArrayList
$script:Stamp = Get-Date -Format "yyyyMMdd_HHmmss"
$script:TestOrdner = [IO.Path]::Combine($AusgabeOrdner, "_systemtest", $script:Stamp)     # kein Join-Path: das bricht ab, wenn Laufwerk T: nicht verbunden ist
$script:Merk = @{}

function Rest([string]$Method, [string]$Url, $Body = $null, [int]$Timeout = 60) {
  $p = @{ Method = $Method; Uri = $Url; TimeoutSec = $Timeout }
  if ($null -ne $Body) { $p.Body = [Text.Encoding]::UTF8.GetBytes(($Body | ConvertTo-Json -Depth 12 -Compress)); $p.ContentType = "application/json; charset=utf-8" }
  Invoke-RestMethod @p
}
function Pruef([string]$Gruppe, [string]$Name, [scriptblock]$Code) {
  if ($Nur.Count -gt 0 -and ($Nur -notcontains $Gruppe)) { return }
  $t0 = Get-Date; $status = "PASS"; $detail = ""
  try {
    $r = & $Code
    $detail = ($r | Out-String).Trim()
    if ($detail -like "WARN:*") { $status = "WARN"; $detail = $detail.Substring(5).Trim() }
    elseif ($detail -like "INFO:*") { $status = "INFO"; $detail = $detail.Substring(5).Trim() }
  } catch {
    $m = $_.Exception.Message
    if ($m -like "SKIP:*") { $status = "SKIP"; $detail = $m.Substring(5).Trim() } else { $status = "FAIL"; $detail = $m }
  }
  $dt = [math]::Round(((Get-Date) - $t0).TotalSeconds, 1)
  $farbe = @{ PASS = "Green"; WARN = "Yellow"; FAIL = "Red"; SKIP = "DarkGray"; INFO = "Cyan" }[$status]
  Write-Host ("[{0,-4}] {1,-11} {2}  ({3}s)" -f $status, $Gruppe, $Name, $dt) -ForegroundColor $farbe
  if ($detail) { ($detail -split "`r?`n") | ForEach-Object { Write-Host ("         " + $_) -ForegroundColor $farbe } }
  [void]$script:Ergebnisse.Add([pscustomobject]@{ Gruppe = $Gruppe; Pruefung = $Name; Status = $status; Sekunden = $dt; Detail = $detail })
}
function Skip([string]$Grund) { throw ("SKIP:" + $Grund) }
function Haupt([string]$Url) { $Url.TrimEnd("/") }
function JsonZahl([string]$Json, [string]$Name) {
  $m = [regex]::Match($Json, '"' + $Name + '"\s*:\s*(-?\d+)', 'IgnoreCase')
  if ($m.Success) { [int]$m.Groups[1].Value } else { $null }
}
function CliAufruf([string[]]$Argumente, [int]$TimeoutSek = 300) {
  if (-not (Test-Path $StfKit)) { Skip "StfKit-Programm nicht gefunden: $StfKit (Parameter -StfKit)" }
  $psi = New-Object System.Diagnostics.ProcessStartInfo
  $psi.FileName = (Resolve-Path $StfKit).Path
  $psi.Arguments = (($Argumente | ForEach-Object { if ("$_" -match '[\s"]') { '"' + ("$_" -replace '"', '\"') + '"' } else { "$_" } }) -join " ")
  $psi.UseShellExecute = $false; $psi.CreateNoWindow = $true
  $psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true
  $psi.StandardOutputEncoding = [Text.Encoding]::UTF8; $psi.StandardErrorEncoding = [Text.Encoding]::UTF8
  $p = New-Object System.Diagnostics.Process; $p.StartInfo = $psi
  [void]$p.Start()
  $outTask = $p.StandardOutput.ReadToEndAsync(); $errTask = $p.StandardError.ReadLineAsync()
  $script:CliFehler = New-Object System.Collections.ArrayList
  $t0 = Get-Date; $letzte = Get-Date
  while (-not ($p.HasExited -and $errTask.IsCompleted)) {
    if ($errTask.IsCompleted) {
      $z = $errTask.Result
      if ($null -ne $z) { Write-Host ("         | " + $z) -ForegroundColor DarkGray; [void]$script:CliFehler.Add($z); $letzte = Get-Date; $errTask = $p.StandardError.ReadLineAsync() } else { break }
    } else { Start-Sleep -Milliseconds 200 }
    $sek = ((Get-Date) - $t0).TotalSeconds
    if ($sek -gt $TimeoutSek) { try { $p.Kill() } catch { }; throw "Zeitüberschreitung nach $TimeoutSek s - abgebrochen. Ordner zu groß/zu langsam (Netzlaufwerk?): -AbgleichLimit verkleinern, einen Unterordner mit nur .stf-Dateien angeben oder -AbgleichTimeoutSek erhöhen." }
    if (((Get-Date) - $letzte).TotalSeconds -ge 15) { Write-Host ("         ... läuft seit {0:N0} s (Abbruch nach {1} s)" -f $sek, $TimeoutSek) -ForegroundColor DarkGray; $letzte = Get-Date }
  }
  # Rest der Fehlerausgabe (nach dem Ende des Programms) noch lesen - dort steht meist die eigentliche Ursache
  while ($true) {
    $z = $null; try { $z = $errTask.Result } catch { }
    if ($null -eq $z) { break }
    Write-Host ("         | " + $z) -ForegroundColor DarkGray; [void]$script:CliFehler.Add($z); $errTask = $p.StandardError.ReadLineAsync()
  }
  $p.WaitForExit()
  $outTask.Result -split "`r?`n"
}

Write-Host "`nStfKit Systemprüfung $($script:Stamp)  (PowerShell $($PSVersionTable.PSVersion))`n"

# ---------------------------------------------------------------- Basis
Pruef "Basis" "PowerShell-Version >= 5.1" {
  if ($PSVersionTable.PSVersion -lt [version]"5.1") { throw "Version $($PSVersionTable.PSVersion) ist zu alt" }
  "PowerShell $($PSVersionTable.PSVersion)"
}
Pruef "Basis" "StfKit-Programm vorhanden" {
  if (-not (Test-Path $StfKit)) { throw "nicht gefunden: $StfKit - Parameter -StfKit angeben (StfKit.exe aus dem Kit)" }
  "gefunden: $StfKit"
}

# ---------------------------------------------------------------- Pfade
Pruef "Pfade" "Formularordner lesbar ($FormularOrdner)" {
  if (-not (Test-Path -LiteralPath $FormularOrdner)) { throw "Ordner nicht erreichbar. Läuft der Dienst/das Konto ohne verbundenes Laufwerk T:? Dann den UNC-Pfad (\\server\freigabe\...) verwenden." }
  $n = @(Get-ChildItem -LiteralPath $FormularOrdner -Recurse -Filter *.stf -ErrorAction Stop | Select-Object -First 5000).Count
  if ($n -eq 0) { "WARN: Ordner gefunden, aber keine .stf-Dateien" } else { "$n .stf-Dateien gefunden (höchstens 5000 gezählt)" }
}
Pruef "Pfade" "Ausgabeordner beschreibbar ($AusgabeOrdner)" {
  New-Item -ItemType Directory -Force $script:TestOrdner | Out-Null
  $f = [IO.Path]::Combine($script:TestOrdner, "schreibtest.txt"); "ok" | Set-Content $f -Encoding UTF8
  $ok = (Get-Content $f -Raw).Trim() -eq "ok"; Remove-Item $f -Force
  if (-not $ok) { throw "Datei konnte nicht gelesen werden" }
  "Schreiben/Lesen/Löschen ok in $script:TestOrdner"
}

# ---------------------------------------------------------------- Api
Pruef "Api" "StfKit-API erreichbar ($Api)" {
  $h = Rest GET ((Haupt $Api) + "/health") $null 15
  if (-not $h.ok) { throw "health meldet nicht ok" }
  $script:Merk.apiOk = $true
  $s = "Version $($h.version), PDF-Auslese: $($h.pdf), LibreOffice: $($h.libreOffice)"
  if (-not $h.libreOffice) { "WARN: $s - ohne LibreOffice können .doc/.docx ohne PDF nicht in PDF umgewandelt werden (Verzeichnis-Modus)." } else { $s }
}

# ---------------------------------------------------------------- Ollama
Pruef "Ollama" "Ollama erreichbar, Modelle vorhanden ($Ollama)" {
  $t = Rest GET ((Haupt $Ollama) + "/api/tags") $null 20
  $namen = @($t.models | ForEach-Object { $_.name })
  $fehl = @()
  foreach ($m in @($EmbedModel, $ChatModel)) { $b = ($m -split ":")[0]; if (-not ($namen | Where-Object { $_ -eq $m -or $_ -like "$b*" })) { $fehl += $m } }
  if ($fehl.Count -gt 0) { throw ("Modell(e) nicht installiert: " + ($fehl -join ", ") + "  (vorhanden: " + ($namen -join ", ") + ")") }
  $script:Merk.ollamaOk = $true
  "$($namen.Count) Modelle, benötigte vorhanden"
}
Pruef "Ollama" "Embedding ($EmbedModel)" {
  if (-not $script:Merk.ollamaOk) { Skip "Ollama-Prüfung davor nicht bestanden" }
  $r = Rest POST ((Haupt $Ollama) + "/api/embeddings") @{ model = $EmbedModel; prompt = "Gebührenbescheid für die Benutzung des Übergangswohnheims" } 180
  $n = @($r.embedding).Count; if ($n -lt 8) { throw "kein Vektor erhalten" }
  "Vektor mit $n Dimensionen"
}
Pruef "Ollama" "Antwort als JSON nach Schema ($ChatModel)" {
  if (-not $script:Merk.ollamaOk) { Skip "Ollama-Prüfung davor nicht bestanden" }
  $schema = @{ type = "object"; properties = @{ ok = @{ type = "boolean" }; feld = @{ type = "string" } }; required = @("ok", "feld") }
  $body = @{ model = $ChatModel; stream = $false; format = $schema; messages = @(@{ role = "user"; content = 'Gib ausschließlich JSON zurück: {"ok": true, "feld": "Datum"}' }) }
  $r = Rest POST ((Haupt $Ollama) + "/api/chat") $body 300
  $j = $r.message.content | ConvertFrom-Json
  if ($null -eq $j.ok) { throw "Antwort entspricht nicht dem Schema: $($r.message.content)" }
  "gültiges JSON: $($r.message.content)"
}

# ---------------------------------------------------------------- Qdrant
Pruef "Qdrant" "Qdrant erreichbar, Sammlung vorhanden ($Collection)" {
  try { $c = Rest GET ((Haupt $QdrantUrl) + "/collections/" + $Collection) $null 20 }
  catch { if ("$($_.Exception.Message)" -match "404|Not Found") { "WARN: Qdrant erreichbar, aber die Sammlung '$Collection' existiert noch nicht (wird beim ersten qdrant-load angelegt)" ; return } ; throw }
  $script:Merk.qdrantOk = $true
  "Status $($c.result.status), Punkte: $($c.result.points_count)"
}
Pruef "Qdrant" "Schreib-/Lesetest in Wegwerf-Sammlung" {
  if (-not $QdrantSchreibtest) { Skip "nur mit -QdrantSchreibtest (legt eine temporäre Sammlung an und löscht sie wieder)" }
  $nm = "stfkit_selftest_" + ([guid]::NewGuid().ToString("N").Substring(0, 8)); $u = (Haupt $QdrantUrl) + "/collections/" + $nm
  try {
    [void](Rest PUT $u @{ vectors = @{ size = 4; distance = "Cosine" } } 30)
    [void](Rest PUT ($u + "/points?wait=true") @{ points = @(@{ id = 1; vector = @(0.1, 0.2, 0.3, 0.4); payload = @{ norm_name = "systemtest" } }) } 30)
    $s = Rest POST ($u + "/points/scroll") @{ limit = 10; with_payload = $true; with_vector = $false } 30
    if (@($s.result.points).Count -ne 1) { throw "Punkt nach dem Schreiben nicht lesbar" }
  } finally { try { [void](Rest DELETE $u $null 30) } catch { } }
  "Sammlung $nm angelegt, 1 Punkt geschrieben/gelesen, Sammlung gelöscht"
}

# ---------------------------------------------------------------- Abgleich Qdrant <-> Ordner (nur anzeigen)
Pruef "Abgleich" "Was fehlt in Qdrant? (Probelauf, schreibt nichts)" {
  if (-not (Test-Path -LiteralPath $FormularOrdner)) { Skip "Formularordner nicht erreichbar" }
  $a = @("qdrant-load", $FormularOrdner, "--dry-run", "--only-missing", "--limit", "$AbgleichLimit", "--url", (Haupt $QdrantUrl), "--collection", $Collection, "--ollama", (Haupt $Ollama))
  if (-not $AbgleichMitOffice) { $a += "--no-office" }     # LibreOffice für Word/RTF/doc dauert pro Datei Sekunden - für die Prüfung nicht nötig
  Write-Host ("         (höchstens $AbgleichLimit Einträge, " + $(if ($AbgleichMitOffice) { "mit" } else { "ohne" }) + " LibreOffice, Abbruch nach $AbgleichTimeoutSek s)") -ForegroundColor DarkGray
  $out = CliAufruf $a $AbgleichTimeoutSek
  $json = ($out | Where-Object { $_ -match '^\s*\{' } | Select-Object -Last 1)
  if (-not $json) { throw ("keine Auswertung erhalten. Letzte Meldungen des Programms:`n" + (@($script:CliFehler | Select-Object -Last 6) -join "`n")) }
  $geprueft = JsonZahl $json "checkedEntries"; if ($null -eq $geprueft) { $geprueft = JsonZahl $json "source" }
  $fehlen = JsonZahl $json "missing"; $da = JsonZahl $json "present"; $gesamt = JsonZahl $json "inQdrant"
  if ($null -eq $fehlen -or $null -eq $da) { throw "Auswertung nicht lesbar: $json" }
  "Geprüft (höchstens $AbgleichLimit): $geprueft   schon in Qdrant: $da   fehlen: $fehlen   Qdrant gesamt: $gesamt"
}

# ---------------------------------------------------------------- Firebird (nur lesen)
Pruef "Firebird" "Export der zentralen Formulare (nur Lesen)" {
  if (-not $DbInspect) { Skip "Parameter -DbInspect (Ordner von StfKit.DbInspect) fehlt" }
  if (-not $env:STFKIT_FB_PASSWORD) { Skip "`$env:STFKIT_FB_PASSWORD nicht gesetzt" }
  $tmp = [IO.Path]::Combine($env:TEMP, ("stfkit_fbtest_" + $script:Stamp))
  Push-Location $DbInspect
  try { $o = & dotnet run -c Release -- $FbHost $FbDatei export $tmp 2>&1 | ForEach-Object { "$_" } } finally { Pop-Location }
  if (-not (Test-Path $tmp)) { throw ("Export hat keinen Ordner erzeugt:`n" + (($o | Select-Object -Last 6) -join "`n")) }
  $n = @(Get-ChildItem $tmp -Recurse -Filter *.stf).Count; $ix = [IO.Path]::Combine($tmp, "index.csv")
  $z = if (Test-Path $ix) { @(Get-Content $ix).Count - 1 } else { 0 }
  $script:Merk.fbExport = $tmp
  if ($n -eq 0) { throw "Export ohne .stf-Dateien" }
  "$n Formulare exportiert, index.csv: $z Zeilen ($tmp)"
}
Pruef "Firebird" "Vergleich Firebird-Export mit Qdrant (Probelauf)" {
  if (-not $script:Merk.fbExport) { Skip "kein Firebird-Export aus dem Schritt davor" }
  $out = CliAufruf @("qdrant-load", $script:Merk.fbExport, "--dry-run", "--only-missing", "--no-office", "--url", (Haupt $QdrantUrl), "--collection", $Collection, "--ollama", (Haupt $Ollama)) 900
  $json = ($out | Where-Object { $_ -match '^\s*\{' } | Select-Object -Last 1); if (-not $json) { throw ("keine Auswertung erhalten. Letzte Meldungen des Programms:`n" + (@($script:CliFehler | Select-Object -Last 6) -join "`n")) }
  $geprueft = JsonZahl $json "checkedEntries"; if ($null -eq $geprueft) { $geprueft = JsonZahl $json "source" }
  $fehlen = JsonZahl $json "missing"; $da = JsonZahl $json "present"
  if ($null -eq $fehlen -or $null -eq $da) { throw "Auswertung nicht lesbar: $json" }
  "In Firebird: $geprueft   davon fehlen in Qdrant: $fehlen   schon vorhanden: $da"
}

# ---------------------------------------------------------------- Ende zu Ende
Pruef "EndeZuEnde" "Beispiel: analysieren und Formular erzeugen" {
  if (-not $BeispielPdf -and -not $BeispielDocx) { Skip "-BeispielPdf und/oder -BeispielDocx angeben" }
  try { $h = Rest GET ((Haupt $Api) + "/health") $null 15; if (-not $h.ok) { Skip "API meldet nicht ok" } } catch { if ("$($_.Exception.Message)" -like "SKIP:*") { throw } ; Skip "API nicht erreichbar ($Api)" }
  $ana = @{ pdf = $(if ($BeispielPdf) { $BeispielPdf } else { $null }); docx = $(if ($BeispielDocx) { $BeispielDocx } else { $null }); other = $null }
  $a = Rest POST ((Haupt $Api) + "/analyze") $ana 600
  if (-not $a.ok) { throw "analyze: $($a.error)" }
  $out = [IO.Path]::Combine($script:TestOrdner, "formular"); New-Item -ItemType Directory -Force $out | Out-Null
  $req = @{ irId = $a.irId; name = "Systemtest"; outDir = $out; version = "DB2026"; preview = $true; useQdrant = [bool]$MitQdrant }
  if ($Modus -ne "auto") { $req.mode = $Modus }     # scratch = Layout aus dem PDF, bescheid/inherit = Texte der ähnlichsten Vorlage mit deren Geometrie
  $g = Rest POST ((Haupt $Api) + "/generate") $req 900
  if (-not $g.ok) { if ("$($g.error)" -like "*außerhalb der erlaubten*") { throw "generate: $($g.error)`nTipp: -AusgabeOrdner ($AusgabeOrdner) muss unter StfKit:AllowedRoots der API liegen (appsettings.json) - und die API muss unter einem Konto laufen, das dort schreiben darf." } ; throw "generate: $($g.error)" }
  $fehl = @(); foreach ($p in @($g.stf, $g.previewPdf, $g.filledPdf)) { if ($p -and -not (Test-Path -LiteralPath $p)) { $fehl += $p } }
  if (-not $g.stf) { throw "keine .stf-Datei im Ergebnis" }
  if ($fehl.Count -gt 0) { "WARN: erzeugt, aber nicht vom Rechner des Skripts erreichbar (Pfad nur auf dem Server gültig?): " + ($fehl -join "; ") }
  else {
    $script:Merk.stf = $g.stf
    $info = "Modus: $($g.mode), Vorlage: $($g.template) (Ähnlichkeit $($g.similarity)), Version $($g.version), Import als: $($g.importMode)"
    $lay = ""; $warn = $false
    if ($g.layout -and $g.layout.matched -gt 0) {
      $lay = "`nLayout gegenüber dem PDF: $($g.layout.within15) von $($g.layout.matched) Zeilen innerhalb 1,5 mm; Median dx $($g.layout.medianDx) mm, dy $($g.layout.medianDy) mm; größte vertikale Abweichung $($g.layout.maxAbsDy) mm"
      if ($g.layout.poor) { $warn = $true; $lay += "`n  -> WEICHT AB: das Formular würde anders drucken als das gesendete PDF. Mit -Modus scratch (Layout aus dem PDF) vergleichen."; }
    }
    $nl = @($g.notes | Where-Object { "$_" -match "PRÜFEN|Version|Layout|Personenname|überlagerte" } | Select-Object -First 5); $nt = if ($nl.Count -gt 0) { "`nHinweise: " + ($nl -join "`n          ") } else { "" }
    $txt = $info + "`nSTF: $($g.stf)`nVorschau: $($g.previewPdf)`n" + $(if ($g.filledPdf) { "Testausdruck: " + $g.filledPdf + "`n" } else { "" }) + "Markierungen: $(@($a.notes.highlights).Count), Kommentare: $(@($a.notes.comments).Count), automatisch zugeordnet: $(@($g.autoPlaceholders).Count), offen für Menschen: $(@($g.unassignedHighlights).Count)" + $lay + $nt
    if (@($g.autoPlaceholders).Count -gt 0) { $txt += "`n  automatisch zugeordnet:`n    " + ((@($g.autoPlaceholders) | Select-Object -First 12) -join "`n    ") }
    if (@($g.unassignedHighlights).Count -gt 0) { $txt += "`n  offen (Mensch/Modell nötig, mit Vorschlag falls vorhanden):`n    " + ((@($g.unassignedHighlights) | Select-Object -First 12) -join "`n    ") }
    if ($warn) { "WARN: Layout weicht vom PDF ab`n" + $txt } else { $txt }
  }
}

# ---------------------------------------------------------------- Mail
Pruef "Mail" "Testmail über SMTP ($Smtp)" {
  if (-not $Smtp -or -not $Von -or -not $An) { Skip "-Smtp, -Von und -An angeben" }
  $m = New-Object System.Net.Mail.MailMessage($Von, $An)
  $m.Subject = "StfKit Systemtest $($script:Stamp)"; $m.IsBodyHtml = $true; $m.SubjectEncoding = [Text.Encoding]::UTF8; $m.BodyEncoding = [Text.Encoding]::UTF8
  $m.Body = "<p>Dies ist ein Test der Benachrichtigung 'KI ist fertig' (Umlaute: äöüß).</p><p>Ausgabeordner: <code>$script:TestOrdner</code></p>"
  $c = New-Object System.Net.Mail.SmtpClient($Smtp, $SmtpPort); $c.EnableSsl = [bool]$SmtpSsl; $c.Timeout = 30000
  if ($env:STFKIT_SMTP_USER) { $c.Credentials = New-Object System.Net.NetworkCredential($env:STFKIT_SMTP_USER, $env:STFKIT_SMTP_PASSWORD) }
  try { $c.Send($m) } finally { $m.Dispose(); $c.Dispose() }
  "Testmail an $An übergeben - bitte im Postfach prüfen (Umlaute im Text und im Betreff)"
}
Pruef "N8n" "n8n-Formular erreichbar ($N8nUrl)" {
  if (-not $N8nUrl) { Skip "-N8nUrl (Adresse des Formulars 'Formular-Import') angeben" }
  $r = Invoke-WebRequest -Uri $N8nUrl -TimeoutSec 20 -UseBasicParsing
  if ($r.StatusCode -ne 200) { throw "HTTP $($r.StatusCode)" }
  if ($r.Content -notmatch "Verzeichnis|Formulare|Kunde") { "WARN: Seite erreichbar, enthält aber nicht die erwarteten Felder (richtiger Workflow aktiv?)" } else { "HTTP 200, Formularfelder gefunden" }
}

# ---------------------------------------------------------------- Aufgabenplanung
Pruef "Aufgabe" "Geplante Aufgabe anlegen/prüfen/löschen (Test)" {
  if (-not $Aufgabe) { Skip "nur mit -Aufgabe" }
  if ($env:OS -ne "Windows_NT") { Skip "nur unter Windows" }
  $nm = "StfKit Systemtest " + $script:Stamp
  try {
    $a = New-ScheduledTaskAction -Execute "powershell.exe" -Argument "-NoProfile -Command exit 0"
    Register-ScheduledTask -TaskName $nm -Action $a -Trigger (New-ScheduledTaskTrigger -Daily -At "03:30") -Description "Systemtest (wird sofort gelöscht)" -Force | Out-Null
    $t = Get-ScheduledTask -TaskName $nm; if (-not $t) { throw "Aufgabe nicht auffindbar" }
    "Aufgabe angelegt (Status $($t.State)); Rechte reichen für katalog-aktualisieren.ps1 -TaskAnlegen"
  } finally { try { Unregister-ScheduledTask -TaskName $nm -Confirm:$false } catch { } }
}

# ---------------------------------------------------------------- Import in AENEAS vorbereiten
Pruef "Import" "Importpaket für FormulareImport vorbereiten (Test)" {
  $quelle = $script:Merk.stf
  if (-not $quelle -and (Test-Path -LiteralPath $FormularOrdner)) { $q = Get-ChildItem -LiteralPath $FormularOrdner -Recurse -Filter *.stf | Select-Object -First 1; if ($q) { $quelle = $q.FullName } }
  if (-not $quelle) { Skip "keine .stf-Datei verfügbar (zuerst EndeZuEnde oder erreichbaren Formularordner)" }
  $imp = [IO.Path]::Combine($script:TestOrdner, "import"); New-Item -ItemType Directory -Force $imp | Out-Null
  Copy-Item -LiteralPath $quelle -Destination ([IO.Path]::Combine($imp, "Systemtest_KI.stf")) -Force
  $csv = "Datei;Name;Art;Kategorie;Modus;Ziel;Vorlage;Aehnlichkeit;Hinweise;Datenbank`r`n" + '"Systemtest_KI.stf";"Systemtest KI";"Formular";"";"Neu";"";"";;"Systemtest, bitte nicht übernehmen";"TEST"' + "`r`n"
  [IO.File]::WriteAllText(([IO.Path]::Combine($imp, "manifest.csv")), $csv, (New-Object Text.UTF8Encoding($true)))
  "INFO: Paket bereit: $imp`nDer eigentliche Import läuft NUR innerhalb von AENEAS (benötigt dessen Datenbankverbindung). Dort zuerst als Probelauf:`n  FormulareImport.ImportManifest(fm, @`"$imp`", true, @`"$script:TestOrdner\backup`")   // dryRun = true: schreibt nichts, meldet nur PLAN/ÜBERSPRUNGEN`nErwartet: 1 Eintrag 'Systemtest KI' mit Ergebnis PLAN (bzw. ÜBERSPRUNGEN, falls es den Namen schon gibt). Erst danach mit dryRun = false in einer TESTdatenbank."
}

# ---------------------------------------------------------------- Zusammenfassung
$z = $script:Ergebnisse | Group-Object Status | ForEach-Object { "$($_.Name): $($_.Count)" }
Write-Host ("`nErgebnis: " + ($z -join "   "))
if ($Bericht) { $script:Ergebnisse | ConvertTo-Json -Depth 4 | Set-Content -Path $Bericht -Encoding UTF8; Write-Host "Bericht: $Bericht" }
if (Test-Path -LiteralPath $script:TestOrdner -ErrorAction SilentlyContinue) { Write-Host "Testdateien (können gelöscht werden): $script:TestOrdner" }
if (@($script:Ergebnisse | Where-Object { $_.Status -eq "FAIL" }).Count -gt 0) { exit 1 } else { exit 0 }