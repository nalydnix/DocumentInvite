<#
Provisions the Azure resources for DocumentInvite. Idempotent: safe to run again.
Run by the repository owner only.

Usage: ./infra/provision.ps1 -NamePrefix <prefix> -Location <region> -WebOrigin <https://app.example.com> [-ResourceGroup <name>] [-SkipDatabase]
-SkipDatabase deploys everything except the SQL database (create a database named DocumentInvite by hand).
The SQL administrator password is read from $env:SQL_ADMIN_PASSWORD or prompted for.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$NamePrefix,
    [Parameter(Mandatory)][string]$Location,
    [Parameter(Mandatory)][string]$WebOrigin,
    [string]$ResourceGroup = "$NamePrefix-rg",
    [string]$SqlAdminLogin = $(if ($env:SQL_ADMIN_LOGIN) { $env:SQL_ADMIN_LOGIN } else { 'diadmin' })
)

$ErrorActionPreference = 'Stop'
$WebOrigin = $WebOrigin.TrimEnd('/')

if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
    throw 'Azure CLI (az) is not installed. See docs/deployment.md.'
}
az account show *> $null
if ($LASTEXITCODE -ne 0) {
    throw "Not logged in to Azure. Run 'az login' first."
}

$sqlPassword = $env:SQL_ADMIN_PASSWORD
if (-not $sqlPassword) {
    $secure = Read-Host -AsSecureString 'SQL administrator password'
    $sqlPassword = [System.Net.NetworkCredential]::new('', $secure).Password
}

# Reject passwords that cmd.exe (az.cmd on Windows), connection strings or Azure SQL would mishandle.
function Test-SqlAdminPassword([string]$Password, [string]$Login) {
    if ($Password.Length -lt 12 -or $Password.Length -gt 128) { return 'must be 12 to 128 characters long' }
    if ($Password -match '[&|^<>%";\s]') { return 'must not contain & | ^ < > % " ; or whitespace (cmd.exe and connection strings would mangle them)' }
    if ($Login -and $Password.IndexOf($Login, [StringComparison]::OrdinalIgnoreCase) -ge 0) { return 'must not contain the SQL administrator login' }
    $classes = 0
    foreach ($pattern in '[a-z]', '[A-Z]', '[0-9]', '[^a-zA-Z0-9]') {
        if ($Password -cmatch $pattern) { $classes++ }
    }
    if ($classes -lt 3) { return 'must use at least three of: lowercase, uppercase, digits, other characters' }
    return $null
}

$passwordProblem = Test-SqlAdminPassword $sqlPassword $SqlAdminLogin
if ($passwordProblem) { throw "Invalid SQL administrator password: it $passwordProblem." }

$deployment = "documentinvite-$NamePrefix"

Write-Host "Creating resource group $ResourceGroup in $Location..."
az group create --name $ResourceGroup --location $Location --output none
if ($LASTEXITCODE -ne 0) { throw 'Resource group creation failed.' }

Write-Host 'Deploying infra/main.bicep...'
az deployment group create `
    --resource-group $ResourceGroup `
    --name $deployment `
    --template-file (Join-Path $PSScriptRoot 'main.bicep') `
    --parameters namePrefix=$NamePrefix location=$Location webOrigin=$WebOrigin `
                 sqlAdminLogin=$SqlAdminLogin sqlAdminPassword=$sqlPassword `
                 createSqlDatabase=$(if ($SkipDatabase) { 'false' } else { 'true' }) `
    --output none
if ($LASTEXITCODE -ne 0) { throw 'Bicep deployment failed.' }

function Get-DeploymentOutput([string]$Name) {
    az deployment group show --resource-group $ResourceGroup --name $deployment `
        --query "properties.outputs.$Name.value" --output tsv
}

$apiUrl = Get-DeploymentOutput 'apiUrl'
$webApp = Get-DeploymentOutput 'webAppName'
$sqlFqdn = Get-DeploymentOutput 'sqlServerFqdn'
$sqlServer = Get-DeploymentOutput 'sqlServerName'
$sqlDb = Get-DeploymentOutput 'sqlDatabaseName'
$storage = Get-DeploymentOutput 'storageAccountName'
$container = Get-DeploymentOutput 'blobContainerName'

$storageConn = az storage account show-connection-string --resource-group $ResourceGroup --name $storage `
    --query connectionString --output tsv
$migrationsConn = "Server=tcp:$sqlFqdn,1433;Database=$sqlDb;User ID=$SqlAdminLogin;Password=$sqlPassword;Encrypt=True;TrustServerCertificate=False;"
$defaultConnTemplate = "Server=tcp:$sqlFqdn,1433;Database=$sqlDb;User ID=di_app;Password=<di_app password>;Encrypt=True;TrustServerCertificate=False;"

Write-Host "Setting App Service settings on $webApp..."
az webapp config appsettings set --resource-group $ResourceGroup --name $webApp --output none --settings `
    ASPNETCORE_ENVIRONMENT=Production `
    ASPNETCORE_FORWARDEDHEADERS_ENABLED=true `
    "Cors__Origins__0=$WebOrigin" `
    "Portal__BaseUrl=$WebOrigin" `
    "Blob__ConnectionString=$storageConn" `
    "Blob__ContainerName=$container" `
    Scalar__Enabled=false `
    Demo__Enabled=false
if ($LASTEXITCODE -ne 0) { throw 'Setting App Service settings failed.' }

@"

Provisioning finished.

GitHub repository secrets (Settings > Secrets and variables > Actions):
  AZURE_WEBAPP_NAME                        $webApp
  AZURE_RESOURCE_GROUP                     $ResourceGroup
  AZURE_SQL_SERVER_NAME                    $sqlServer
  AZURE_CREDENTIALS                        output of: az ad sp create-for-rbac --name $NamePrefix-deploy --role Contributor --scopes /subscriptions/<subscription id>/resourceGroups/$ResourceGroup --sdk-auth
  AZURE_SQL_MIGRATIONS_CONNECTION_STRING   $migrationsConn

Still to set on the web app (App Service > Environment variables):
  ConnectionStrings__Default   $defaultConnTemplate
  Jobs__ApiKey, Portal__Session__SigningKey, Bootstrap__AdminEmail, Bootstrap__AdminPassword,
  Email__From (and Email__Provider / Resend__ApiKey for real e-mail)

Vercel project environment variable:
  VITE_API_BASE_URL            $apiUrl

$(if ($SkipDatabase) { "Database skipped: create a database named $sqlDb on server $sqlServer by hand (docs/deployment.md), then continue.`n" })Next: create the least-privilege SQL user (docs/deployment.md, "Runtime database user"), and
add your IP to the SQL server firewall if you run migrations from your machine.
"@
