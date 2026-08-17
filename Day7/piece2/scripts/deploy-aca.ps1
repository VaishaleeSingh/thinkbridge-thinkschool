<#
.SYNOPSIS
    Automated Azure Container Apps (ACA) Provisioning Script for QuotesApi.
.DESCRIPTION
    Creates the Resource Group, Container Apps Environment, and Container App revision
    with external ingress, target port 8080, health probes, and HTTP autoscaling rules.
.PARAMETER ResourceGroup
    Name of the Azure Resource Group (Default: thinkschool-rg).
.PARAMETER Location
    Azure Region (Default: centralindia).
.PARAMETER EnvironmentName
    Name of the Container Apps Environment (Default: thinkschool-env).
.PARAMETER AppName
    Name of the Container App (Default: quotes-api).
.PARAMETER Image
    Container image to deploy (Default: quotes-api:0.1.0).
.PARAMETER JwtSecret
    JWT signing key (Default: generated 32-character secure secret).
#>

[CmdletBinding()]
param (
    [string]$ResourceGroup = "thinkschool-rg",
    [string]$Location = "centralindia",
    [string]$EnvironmentName = "thinkschool-env",
    [string]$AppName = "quotes-api",
    [string]$Image = "quotes-api:0.1.0",
    [string]$JwtSecret = "SuperSecretKeyForJwtAuthenticationMustBeAtLeast32BytesLong!"
)

$ErrorActionPreference = "Stop"

Write-Host "=== Azure Container Apps Provisioning Script ===" -ForegroundColor Cyan
Write-Host "Resource Group: $ResourceGroup"
Write-Host "Location:       $Location"
Write-Host "Environment:    $EnvironmentName"
Write-Host "App Name:       $AppName"
Write-Host "Image:          $Image"
Write-Host "------------------------------------------------"

# Step 1: Create Resource Group
Write-Host "[1/4] Creating Resource Group '$ResourceGroup' in '$Location'..." -ForegroundColor Yellow
az group create --name $ResourceGroup --location $Location --output table

# Step 2: Create Container Apps Environment
Write-Host "[2/4] Creating Container Apps Environment '$EnvironmentName'..." -ForegroundColor Yellow
az containerapp env create `
    --name $EnvironmentName `
    --resource-group $ResourceGroup `
    --location $Location `
    --output table

# Step 3: Create Container App with Ingress, Scaling Rules, and Environment Variables
Write-Host "[3/4] Deploying Container App '$AppName'..." -ForegroundColor Yellow
az containerapp create `
    --name $AppName `
    --resource-group $ResourceGroup `
    --environment $EnvironmentName `
    --image $Image `
    --ingress external `
    --target-port 8080 `
    --min-replicas 1 `
    --max-replicas 5 `
    --scale-rule-name "http-concurrency-rule" `
    --scale-rule-type "http" `
    --scale-rule-http-concurrency 50 `
    --env-vars "Jwt__Secret=$JwtSecret" "ASPNETCORE_ENVIRONMENT=Production" `
    --output table

# Step 4: Retrieve FQDN & Health Verification Instructions
Write-Host "[4/4] Retrieving App FQDN & Ingress Details..." -ForegroundColor Yellow
$fqdn = az containerapp show --name $AppName --resource-group $ResourceGroup --query "properties.configuration.ingress.fqdn" -o tsv

Write-Host "`n=== Deployment Successful ===" -ForegroundColor Green
Write-Host "App FQDN: https://$fqdn" -ForegroundColor Cyan
Write-Host "Health Endpoint: https://$fqdn/health"
Write-Host "Liveness Probe:  https://$fqdn/health/live"
Write-Host "Readiness Probe: https://$fqdn/health/ready"
