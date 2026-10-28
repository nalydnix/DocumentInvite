#!/usr/bin/env bash
# Provisions the Azure resources for DocumentInvite. Idempotent: safe to run again.
# Run by the repository owner only.
#
# Usage:
#   infra/provision.sh <name-prefix> <location> <web-origin> [resource-group]
# Environment:
#   SQL_ADMIN_PASSWORD  SQL administrator password (prompted for when unset)
#   SQL_ADMIN_LOGIN     SQL administrator login (default: diadmin)
set -euo pipefail

if [ "$#" -lt 3 ]; then
  echo "Usage: $0 <name-prefix> <location> <web-origin> [resource-group]" >&2
  exit 2
fi

PREFIX="$1"
LOCATION="$2"
WEB_ORIGIN="${3%/}"
GROUP="${4:-${PREFIX}-rg}"
SQL_ADMIN_LOGIN="${SQL_ADMIN_LOGIN:-diadmin}"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DEPLOYMENT="documentinvite-${PREFIX}"

if ! command -v az >/dev/null 2>&1; then
  echo "Azure CLI (az) is not installed. See docs/deployment.md." >&2
  exit 1
fi
if ! az account show >/dev/null 2>&1; then
  echo "Not logged in to Azure. Run 'az login' first." >&2
  exit 1
fi

if [ -z "${SQL_ADMIN_PASSWORD:-}" ]; then
  read -r -s -p "SQL administrator password: " SQL_ADMIN_PASSWORD
  echo
fi

echo "Creating resource group ${GROUP} in ${LOCATION}..."
az group create --name "$GROUP" --location "$LOCATION" --output none

echo "Deploying infra/main.bicep..."
az deployment group create \
  --resource-group "$GROUP" \
  --name "$DEPLOYMENT" \
  --template-file "${SCRIPT_DIR}/main.bicep" \
  --parameters namePrefix="$PREFIX" location="$LOCATION" webOrigin="$WEB_ORIGIN" \
               sqlAdminLogin="$SQL_ADMIN_LOGIN" sqlAdminPassword="$SQL_ADMIN_PASSWORD" \
  --output none

deployment_output() {
  az deployment group show --resource-group "$GROUP" --name "$DEPLOYMENT" \
    --query "properties.outputs.$1.value" --output tsv
}

API_URL="$(deployment_output apiUrl)"
WEB_APP="$(deployment_output webAppName)"
SQL_FQDN="$(deployment_output sqlServerFqdn)"
SQL_SERVER="$(deployment_output sqlServerName)"
SQL_DB="$(deployment_output sqlDatabaseName)"
STORAGE="$(deployment_output storageAccountName)"
CONTAINER="$(deployment_output blobContainerName)"

STORAGE_CONN="$(az storage account show-connection-string --resource-group "$GROUP" --name "$STORAGE" \
  --query connectionString --output tsv)"
MIGRATIONS_CONN="Server=tcp:${SQL_FQDN},1433;Database=${SQL_DB};User ID=${SQL_ADMIN_LOGIN};Password=${SQL_ADMIN_PASSWORD};Encrypt=True;TrustServerCertificate=False;"
DEFAULT_CONN_TEMPLATE="Server=tcp:${SQL_FQDN},1433;Database=${SQL_DB};User ID=di_app;Password=<di_app password>;Encrypt=True;TrustServerCertificate=False;"

echo "Setting App Service settings on ${WEB_APP}..."
# Secrets only the owner knows (ConnectionStrings__Default, Jobs__ApiKey, the portal signing key, ...)
# are not set here; docs/deployment.md lists them.
az webapp config appsettings set --resource-group "$GROUP" --name "$WEB_APP" --output none --settings \
  ASPNETCORE_ENVIRONMENT=Production \
  ASPNETCORE_FORWARDEDHEADERS_ENABLED=true \
  "Cors__Origins__0=${WEB_ORIGIN}" \
  "Portal__BaseUrl=${WEB_ORIGIN}" \
  "Blob__ConnectionString=${STORAGE_CONN}" \
  "Blob__ContainerName=${CONTAINER}" \
  Scalar__Enabled=false \
  Demo__Enabled=false

cat <<SUMMARY

Provisioning finished.

GitHub repository secrets (Settings > Secrets and variables > Actions):
  AZURE_WEBAPP_NAME                        ${WEB_APP}
  AZURE_RESOURCE_GROUP                     ${GROUP}
  AZURE_SQL_SERVER_NAME                    ${SQL_SERVER}
  AZURE_CREDENTIALS                        output of: az ad sp create-for-rbac --name ${PREFIX}-deploy --role Contributor --scopes /subscriptions/<subscription id>/resourceGroups/${GROUP} --sdk-auth
  AZURE_SQL_MIGRATIONS_CONNECTION_STRING   ${MIGRATIONS_CONN}

Still to set on the web app (App Service > Environment variables):
  ConnectionStrings__Default   ${DEFAULT_CONN_TEMPLATE}
  Jobs__ApiKey, Portal__Session__SigningKey, Bootstrap__AdminEmail, Bootstrap__AdminPassword,
  Email__From (and Email__Provider / Resend__ApiKey for real e-mail)

Vercel project environment variable:
  VITE_API_BASE_URL            ${API_URL}

Next: create the least-privilege SQL user (docs/deployment.md, "Runtime database user"), and
add your IP to the SQL server firewall if you run migrations from your machine.
SUMMARY
