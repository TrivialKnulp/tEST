# Katalog (und optional Qdrant) regelmäßig aktualisieren - ohne Handarbeit.
#   .\katalog-aktualisieren.ps1 -Config .\catalog.config.json -Ausgabe D:\StfKit\katalog [-Qdrant -QdrantUrl http://192.168.1.241:6333 -Ollama http://kiserver:11434 -Ordner <Firebird-Export>] [-TaskAnlegen -Uhrzeit 02:00]
# Mit -TaskAnlegen wird einmalig eine geplante Aufgabe (täglich) in der Windows-Aufgabenplanung erzeugt (benötigt Rechte des Kontos, das auf \\NAS zugreifen darf).
param(
  [string]$Config = "$PSScriptRoot\catalog.config.json", [string]$Ausgabe = "$PSScriptRoot\katalog",
  [string]$StfKit = "$PSScriptRoot\StfKit.exe", [switch]$Qdrant, [string]$QdrantUrl = "http://192.168.1.241:6333",
  [string]$Collection = "Formularerstellung", [string]$Ollama = "", [string]$Ordner = "",
  [switch]$TaskAnlegen, [string]$Uhrzeit = "02:00")
$ErrorActionPreference = "Stop"
if ($TaskAnlegen) {
  $arg = "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`" -Config `"$Config`" -Ausgabe `"$Ausgabe`" -StfKit `"$StfKit`""
  if ($Qdrant) { $arg += " -Qdrant -QdrantUrl $QdrantUrl -Collection $Collection -Ollama $Ollama -Ordner `"$Ordner`"" }
  $a = New-ScheduledTaskAction -Execute "powershell.exe" -Argument $arg
  $t = New-ScheduledTaskTrigger -Daily -At $Uhrzeit
  Register-ScheduledTask -TaskName "StfKit Katalog aktualisieren" -Action $a -Trigger $t -Description "Formular-Katalog (und Qdrant: nur Fehlendes) aktualisieren" -Force | Out-Null
  Write-Host "Aufgabe 'StfKit Katalog aktualisieren' angelegt (täglich $Uhrzeit)."; return
}
New-Item -ItemType Directory -Force $Ausgabe | Out-Null
$log = Join-Path $Ausgabe "aktualisierung.log"
"$(Get-Date -Format s) Start" | Add-Content $log
& $StfKit catalog $Config $Ausgabe 2>&1 | Tee-Object -Append $log     # inkrementell: nur neue/geänderte Dateien werden neu gelesen
if ($Qdrant -and $Ordner) {
  $o = @("qdrant-load", $Ordner, "--only-missing", "--url", $QdrantUrl, "--collection", $Collection); if ($Ollama) { $o += @("--ollama", $Ollama) }
  & $StfKit @o 2>&1 | Tee-Object -Append $log
}
"$(Get-Date -Format s) Ende" | Add-Content $log
