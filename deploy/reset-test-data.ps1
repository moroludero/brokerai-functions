<#
.SYNOPSIS
    Wipes ALL data from the BrokerAi database (leads, sessions, properties,
    photos, campaigns, processed messages, brokers) and re-seeds the pilot
    broker, leaving the system ready to test from zero.

.DESCRIPTION
    Reads the SQL password from deploy/sql-admin-password.txt and the pilot
    broker values from deploy/pilot-broker.json (both gitignored, next to this
    script). Retries the connection while the free-tier DB wakes from auto-pause.

.EXAMPLE
    .\deploy\reset-test-data.ps1                # full wipe + re-seed pilot broker
    .\deploy\reset-test-data.ps1 -SkipReseed    # full wipe, leave DB empty
    .\deploy\reset-test-data.ps1 -ClearPhotos   # also delete uploaded photos from Blob storage

.NOTES
    ⚠️ Destructive by design — intended for the TEST/pilot environment only.
    Asks for confirmation before touching anything.
#>

param(
    [switch]$SkipReseed,
    [switch]$ClearPhotos,
    [string]$SqlServer = "brokerai-sql-cu-4fbm",
    [string]$Database = "brokerai-db",
    [string]$SqlUser = "brokeradmin",
    [string]$StorageAccount = "brokeraistoreml",
    [string]$ResourceGroup = "brokerai-rg",
    [string]$BlobContainer = "property-images"
)

$ErrorActionPreference = "Stop"
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path

$confirmation = Read-Host "Esto BORRA todos los leads, sesiones, propiedades y brokers de '$Database'. Escribe SI para continuar"
if ($confirmation -ne "SI") { Write-Host "Cancelado."; exit 0 }

$password = (Get-Content (Join-Path $scriptDir "sql-admin-password.txt") -Raw).Trim()
$connStr = "Server=tcp:$SqlServer.database.windows.net,1433;Database=$Database;User ID=$SqlUser;Password=$password;Encrypt=True;Connection Timeout=60;"

Write-Host "Conectando (la base puede tardar ~30s en despertar del auto-pause)..."
$conn = New-Object System.Data.SqlClient.SqlConnection($connStr)
$opened = $false
for ($i = 1; $i -le 8; $i++) {
    try { $conn.Open(); $opened = $true; break }
    catch { Write-Host "  intento $i - despertando..."; Start-Sleep -Seconds 20 }
}
if (-not $opened) { Write-Error "No se pudo conectar a la base."; exit 1 }

# Delete order matters: Sessions and AdCampaigns have NoAction FKs to
# Leads/Properties, so children go first.
$wipeSql = @"
DELETE FROM Sessions;
DELETE FROM AdCampaigns;
DELETE FROM PropertyImages;
DELETE FROM Properties;
DELETE FROM Leads;
DELETE FROM ProcessedMessages;
DELETE FROM Brokers;
"@
$cmd = $conn.CreateCommand()
$cmd.CommandText = $wipeSql
$cmd.ExecuteNonQuery() | Out-Null
Write-Host "✅ Datos borrados."

if (-not $SkipReseed) {
    $broker = Get-Content (Join-Path $scriptDir "pilot-broker.json") -Raw | ConvertFrom-Json
    $cmd = $conn.CreateCommand()
    $cmd.CommandText = @"
INSERT INTO Brokers (Id, Name, WhatsappNumber, AlertNumber, PhoneNumberId, [Language], [Plan], LeadsLimit, PropertiesLimit, MonthlyAdBudget, CreatedAt)
VALUES (NEWID(), @name, @wa, @alert, @pni, @lang, @plan, @leadsLimit, @propsLimit, @adBudget, SYSDATETIMEOFFSET());
"@
    $cmd.Parameters.AddWithValue("@name", $broker.name) | Out-Null
    $cmd.Parameters.AddWithValue("@wa", $broker.whatsappNumber) | Out-Null
    $cmd.Parameters.AddWithValue("@alert", $broker.alertNumber) | Out-Null
    $cmd.Parameters.AddWithValue("@pni", $broker.phoneNumberId) | Out-Null
    $cmd.Parameters.AddWithValue("@lang", $broker.language) | Out-Null
    $cmd.Parameters.AddWithValue("@plan", $broker.plan) | Out-Null
    $cmd.Parameters.AddWithValue("@leadsLimit", $broker.leadsLimit) | Out-Null
    $cmd.Parameters.AddWithValue("@propsLimit", $broker.propertiesLimit) | Out-Null
    $cmd.Parameters.AddWithValue("@adBudget", $broker.monthlyAdBudget) | Out-Null
    $cmd.ExecuteNonQuery() | Out-Null
    Write-Host "✅ Broker piloto re-sembrado: $($broker.name) (alertas → $($broker.alertNumber))"
}

$cmd = $conn.CreateCommand()
$cmd.CommandText = "SELECT (SELECT COUNT(*) FROM Brokers) AS B, (SELECT COUNT(*) FROM Leads) AS L, (SELECT COUNT(*) FROM Properties) AS P"
$reader = $cmd.ExecuteReader()
while ($reader.Read()) { Write-Host ("Estado final: brokers=" + $reader["B"] + " leads=" + $reader["L"] + " propiedades=" + $reader["P"]) }
$conn.Close()

if ($ClearPhotos) {
    Write-Host "Borrando fotos del contenedor '$BlobContainer'..."
    $key = az storage account keys list --account-name $StorageAccount --resource-group $ResourceGroup --query "[0].value" -o tsv
    az storage blob delete-batch --source $BlobContainer --account-name $StorageAccount --account-key $key | Out-Null
    Write-Host "✅ Fotos borradas."
}

Write-Host ""
Write-Host "Listo para probar desde cero 🚀 (el bot responderá con el broker piloto re-sembrado)"
