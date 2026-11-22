<#
 Firebird (FORMULARE) -> Qdrant: lädt NUR die Formulare, die in Qdrant noch fehlen.
 Schritte: 1) DbInspect export (liest nur Tabelle FORMULARE, entpackt bzip2)  2) StfKit.Cli qdrant-load --dry-run / --only-missing.
 Standard = Probelauf (Dry-Run): zeigt nur, was fehlen würde. Erst mit -Ausfuehren wird wirklich geladen.

 Beispiel:
   $env:STFKIT_FB_PASSWORD = "geheim"
   .\firebird-nach-qdrant.ps1 -DbInspect "T:\...\StfKit.DbInspect" -Export "T:\...\db-export-qdrant"
   .\firebird-nach-qdrant.ps1 -DbInspect "T:\...\StfKit.DbInspect" -Export "T:\...\db-export-qdrant" -Ausfuehren
#>
param(
  [Parameter(Mandatory = $true)][string]$DbInspect,                 # Ordner von StfKit.DbInspect
  [Parameter(Mandatory = $true)][string]$Export,                    # Zielordner des Exports (wird angelegt)
  [string]$FbHost = "main",
  [string]$FbDatei = "/data/db/DatenFormulare_3926.fdb5",
  [string]$QdrantUrl = "http://192.168.1.241:6333",
  [string]$Collection = "Formularerstellung",
  [string]$Ollama = "",                                              # leer = Wert aus der Konfiguration des Kits
  [switch]$Ausfuehren                                                 # ohne diesen Schalter wird nichts in Qdrant geschrieben
)
$ErrorActionPreference = "Stop"
if (-not $env:STFKIT_FB_PASSWORD) { throw "Bitte zuerst `$env:STFKIT_FB_PASSWORD setzen (Passwort wird nirgends gespeichert)." }
$kit = $PSScriptRoot

Write-Host "1/2 Export aus Firebird (nur Tabelle FORMULARE) -> $Export"
Push-Location $DbInspect
try { dotnet run -c Release -- $FbHost $FbDatei export $Export } finally { Pop-Location }

$mode = if ($Ausfuehren) { "--only-missing" } else { "--dry-run" }
Write-Host "2/2 Qdrant: $mode ($QdrantUrl / $Collection)"
$extra = @("--url", $QdrantUrl, "--collection", $Collection)
if ($Ollama) { $extra += @("--ollama", $Ollama) }
Push-Location $kit
try { dotnet run --project src\StfKit.Cli -c Release -- qdrant-load $Export $mode --list @extra } finally { Pop-Location }
if (-not $Ausfuehren) { Write-Host "`nProbelauf beendet - nichts wurde in Qdrant geschrieben. Zum Laden dasselbe mit -Ausfuehren." }
