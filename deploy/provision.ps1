<#
.SYNOPSIS
    One-time Azure provisioning for BrokerAi. Run once, then iterate with:
      dotnet ef database update --project src/BrokerAi.Core --startup-project src/BrokerAi.Functions
      func azure functionapp publish brokerai-func

.NOTES
    Requires: az CLI logged in (az login), Azure Functions Core Tools (func).
#>

param(
    [string]$ResourceGroup = "brokerai-rg",
    [string]$Location = "eastus2",
    [string]$FunctionApp = "brokerai-func",
    [string]$StorageAccount = "brokeraistorage",
    [string]$SqlServer = "brokerai-sql",
    [string]$SqlDatabase = "brokerai-db",
    [string]$KeyVault = "brokerai-kv",
    [string]$AppInsights = "brokerai-insights",
    [Parameter(Mandatory = $true)][string]$SqlAdminUser,
    [Parameter(Mandatory = $true)][securestring]$SqlAdminPassword
)

$ErrorActionPreference = "Stop"

Write-Host "Creating resource group $ResourceGroup in $Location..."
az group create --name $ResourceGroup --location $Location | Out-Null

Write-Host "Creating storage account $StorageAccount (Functions runtime storage + queues)..."
az storage account create --name $StorageAccount --resource-group $ResourceGroup `
    --location $Location --sku Standard_LRS | Out-Null

Write-Host "Creating Application Insights $AppInsights..."
az monitor app-insights component create --app $AppInsights --location $Location `
    --resource-group $ResourceGroup | Out-Null

Write-Host "Creating Function App $FunctionApp (Consumption, dotnet-isolated)..."
az functionapp create --name $FunctionApp --resource-group $ResourceGroup `
    --storage-account $StorageAccount --consumption-plan-location $Location `
    --runtime dotnet-isolated --runtime-version 8 --functions-version 4 `
    --app-insights $AppInsights | Out-Null

Write-Host "Enabling system-assigned managed identity on the Function App..."
az functionapp identity assign --name $FunctionApp --resource-group $ResourceGroup | Out-Null

Write-Host "Creating SQL Server $SqlServer..."
$plainPassword = [System.Runtime.InteropServices.Marshal]::PtrToStringAuto(
    [System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($SqlAdminPassword))
az sql server create --name $SqlServer --resource-group $ResourceGroup `
    --location $Location --admin-user $SqlAdminUser --admin-password $plainPassword | Out-Null

Write-Host "Allowing Azure services to reach the SQL server..."
az sql server firewall-rule create --resource-group $ResourceGroup --server $SqlServer `
    --name AllowAzureServices --start-ip-address 0.0.0.0 --end-ip-address 0.0.0.0 | Out-Null

Write-Host "Creating free-tier SQL Database $SqlDatabase..."
az sql db create --resource-group $ResourceGroup --server $SqlServer --name $SqlDatabase `
    --edition GeneralPurpose --compute-model Serverless --family Gen5 --capacity 2 `
    --use-free-limit --free-limit-exhaustion-behavior AutoPause | Out-Null

Write-Host "Creating Key Vault $KeyVault..."
az keyvault create --name $KeyVault --resource-group $ResourceGroup --location $Location | Out-Null

Write-Host ""
Write-Host "Provisioning complete. Next steps:" -ForegroundColor Green
Write-Host "  1. Grant the Function App's managed identity 'Key Vault Secrets User' on $KeyVault"
Write-Host "  2. Add secrets: az keyvault secret set --vault-name $KeyVault --name Meta--AccessToken --value ..."
Write-Host "  3. Set app settings to reference Key Vault (see README.md)"
Write-Host "  4. dotnet ef database update --project src/BrokerAi.Core --startup-project src/BrokerAi.Functions"
Write-Host "  5. func azure functionapp publish $FunctionApp"
