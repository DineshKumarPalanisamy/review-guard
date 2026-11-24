# Deploys the Azure side of Review Guard: infra (Bicep), webhook secret, Function code.
# Needs: az login (Azure), .NET 10 SDK, Python 3 (to build a zip with forward slashes).
# The Azure DevOps PAT is NOT set here. A person stores it in Key Vault as "AdoPat".
[CmdletBinding()]
param(
    [string]$ResourceGroup = 'rg-review-guard',
    [string]$Location = 'centralindia'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent

$me = az ad signed-in-user show --query id -o tsv
az group create -n $ResourceGroup -l $Location --tags app=review-guard -o none
$out = az deployment group create -g $ResourceGroup -n main `
    --template-file "$root/infra/main.bicep" -p deployerObjectId=$me `
    --query properties.outputs -o json | ConvertFrom-Json

$app = $out.functionAppName.value
$kv = $out.keyVaultName.value

# Webhook secret: create once, keep on later runs (the service hooks use it).
az keyvault secret show --vault-name $kv -n WebhookSecret -o none 2>$null
if ($LASTEXITCODE -ne 0) {
    $bytes = [byte[]]::new(32); [Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
    az keyvault secret set --vault-name $kv -n WebhookSecret --value ([Convert]::ToHexString($bytes)) -o none
}

# Build and deploy the code.
$tmp = Join-Path ([IO.Path]::GetTempPath()) "review-guard-$([guid]::NewGuid().ToString('N'))"
dotnet publish "$root/src/ReviewGuard.Functions" -c Release -o "$tmp/out"
python -I -c @"
import os,zipfile,sys
src,dst=sys.argv[1],sys.argv[2]
with zipfile.ZipFile(dst,'w',zipfile.ZIP_DEFLATED) as z:
  for r,_,fs in os.walk(src):
    for f in fs:
      p=os.path.join(r,f); z.write(p,os.path.relpath(p,src).replace(os.sep,'/'))
"@ "$tmp/out" "$tmp/app.zip"
az functionapp deployment source config-zip -g $ResourceGroup -n $app --src "$tmp/app.zip" -o none
Remove-Item $tmp -Recurse -Force

Write-Host "Function app : $app"
Write-Host "Base URL     : $($out.functionBaseUrl.value)"
Write-Host "Key Vault    : $kv"
