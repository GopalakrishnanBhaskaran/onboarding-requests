<#
Phase 2 auto-assign: one-off Entra setup for ONE role mapping (run by a Global Administrator in Cloud Shell, pwsh).
Idempotent: reuses what already exists. Graph v1.0, delegated sign-in (device code).

  Creates or reuses:
    catalog              "PMO Pilot SAMPLE"
    catalog resource     the security group (e.g. SAMPLE-GRP-ALPHA-DEV-ENG)
    access package       e.g. "SAMPLE Data Engineer – Dev", with the group's Member role
    assignment policy    "PMO Review auto-assign": direct (admin) assignments only, no self-requests, no approval stage
                         (the second Reviewer's acceptance in the PMO Review app is the decision), max 180 days.
  Writes .local/entra-package.json: catalogId, accessPackageId, policyId, myAccessLink.

Called by: bash tools/setup_entra.sh package <tenant-domain> ...
#>
param(
  [Parameter(Mandatory)] [string] $TenantDomain,
  [Parameter(Mandatory)] [string] $GroupName,
  [Parameter(Mandatory)] [string] $PackageName,
  [string] $CatalogName = 'PMO Pilot SAMPLE',
  [string] $PolicyName = 'PMO Review auto-assign',
  [string] $OutFile = '.local/entra-package.json'
)
$ErrorActionPreference = 'Stop'

if (-not (Get-Module -ListAvailable Microsoft.Graph.Authentication)) {
  Write-Host 'Installing Microsoft.Graph.Authentication for this user...'
  Install-Module Microsoft.Graph.Authentication -Scope CurrentUser -Force
}
Import-Module Microsoft.Graph.Authentication
Connect-MgGraph -TenantId $TenantDomain -UseDeviceCode -NoWelcome `
  -Scopes 'EntitlementManagement.ReadWrite.All', 'Group.Read.All'

$em = 'https://graph.microsoft.com/v1.0/identityGovernance/entitlementManagement'
function Get-Graph([string] $Url) { Invoke-MgGraphRequest -Method GET -Uri $Url -OutputType PSObject }
function Post-Graph([string] $Url, $Body) {
  Invoke-MgGraphRequest -Method POST -Uri $Url -Body ($Body | ConvertTo-Json -Depth 10) -ContentType 'application/json' -OutputType PSObject
}
function Q([string] $s) { [uri]::EscapeDataString($s.Replace("'", "''")) }

# 1. Catalog
$catalog = (Get-Graph "$em/catalogs?`$filter=displayName eq '$(Q $CatalogName)'").value | Select-Object -First 1
if (-not $catalog) {
  $catalog = Post-Graph "$em/catalogs" @{ displayName = $CatalogName; description = 'SAMPLE pilot: PMO Review access packages.'; isExternallyVisible = $false }
  Write-Host "Created catalog $CatalogName"
} else { Write-Host "Catalog exists: $CatalogName" }

# 2. Group as a catalog resource
$group = (Get-Graph "https://graph.microsoft.com/v1.0/groups?`$filter=displayName eq '$(Q $GroupName)'&`$select=id,displayName").value | Select-Object -First 1
if (-not $group) { throw "Group '$GroupName' not found. Run: bash tools/setup_entra.sh identities <tenant-domain> first." }
$resource = (Get-Graph "$em/catalogs/$($catalog.id)/resources?`$filter=originId eq '$($group.id)'").value | Select-Object -First 1
if (-not $resource) {
  Post-Graph "$em/resourceRequests" @{
    requestType = 'adminAdd'
    resource    = @{ originId = $group.id; originSystem = 'AadGroup' }
    catalog     = @{ id = $catalog.id }
  } | Out-Null
  for ($i = 0; $i -lt 12 -and -not $resource; $i++) {
    Start-Sleep -Seconds 5
    $resource = (Get-Graph "$em/catalogs/$($catalog.id)/resources?`$filter=originId eq '$($group.id)'").value | Select-Object -First 1
  }
  if (-not $resource) { throw 'The group did not appear in the catalog within 60 s. Re-run the script.' }
  Write-Host "Added group $GroupName to the catalog"
} else { Write-Host "Group already in catalog: $GroupName" }

$member = (Get-Graph "$em/catalogs/$($catalog.id)/resourceRoles?`$filter=(originSystem eq 'AadGroup' and resource/id eq '$($resource.id)')&`$expand=resource").value |
  Where-Object { $_.displayName -eq 'Member' } | Select-Object -First 1
if (-not $member) { throw "No 'Member' role found for $GroupName in the catalog." }

# 3. Access package with the group's Member role
$package = (Get-Graph "$em/catalogs/$($catalog.id)/accessPackages").value |
  Where-Object { $_.displayName -eq $PackageName } | Select-Object -First 1
if (-not $package) {
  $package = Post-Graph "$em/accessPackages" @{ displayName = $PackageName; description = "SAMPLE: $GroupName (Member)."; catalog = @{ id = $catalog.id } }
  Write-Host "Created access package $PackageName"
} else { Write-Host "Access package exists: $PackageName" }

$scopes = (Get-Graph "$em/accessPackages/$($package.id)?`$expand=resourceRoleScopes(`$expand=role,scope)").resourceRoleScopes
if (-not ($scopes | Where-Object { $_.scope.originId -eq $group.id })) {
  Post-Graph "$em/accessPackages/$($package.id)/resourceRoleScopes" @{
    role  = @{ originId = $member.originId; originSystem = 'AadGroup'; resource = @{ id = $resource.id; originId = $group.id; originSystem = 'AadGroup' } }
    scope = @{ originId = $group.id; originSystem = 'AadGroup' }
  } | Out-Null
  Write-Host "Added $GroupName (Member) to the access package"
} else { Write-Host "Access package already grants $GroupName" }

# 4. The app's policy: direct assignments only (no self-requests), no approval stage, at most 180 days.
$policy = (Get-Graph "$em/assignmentPolicies?`$filter=accessPackage/id eq '$($package.id)'&`$expand=accessPackage").value |
  Where-Object { $_.displayName -eq $PolicyName } | Select-Object -First 1
if (-not $policy) {
  $policy = Post-Graph "$em/assignmentPolicies" @{
    displayName            = $PolicyName
    description            = 'Assignments filed by the PMO Review worker after a second Reviewer accepts a ready review. No self-requests.'
    allowedTargetScope     = 'notSpecified'
    specificAllowedTargets = @()
    expiration             = @{ type = 'afterDuration'; duration = 'P180D' }
    requestorSettings      = @{
      enableTargetsToSelfAddAccess = $false; enableTargetsToSelfUpdateAccess = $false; enableTargetsToSelfRemoveAccess = $false
      allowCustomAssignmentSchedule = $true
      enableOnBehalfRequestorsToAddAccess = $false; enableOnBehalfRequestorsToUpdateAccess = $false; enableOnBehalfRequestorsToRemoveAccess = $false
      onBehalfRequestors = @()
    }
    requestApprovalSettings = @{ isApprovalRequiredForAdd = $false; isApprovalRequiredForUpdate = $false; stages = @() }
    accessPackage          = @{ id = $package.id }
  }
  Write-Host "Created policy $PolicyName"
} else { Write-Host "Policy exists: $PolicyName" }

$link = "https://myaccess.microsoft.com/@$TenantDomain#/access-packages/$($package.id)"
$out = [ordered]@{ catalogId = $catalog.id; accessPackageId = $package.id; policyId = $policy.id; myAccessLink = $link }
New-Item -ItemType Directory -Force -Path (Split-Path $OutFile) | Out-Null
$out | ConvertTo-Json | Set-Content -Path $OutFile -Encoding utf8
Write-Host "catalogId=$($catalog.id)"
Write-Host "accessPackageId=$($package.id)"
Write-Host "myAccessLink=$link"
Disconnect-MgGraph | Out-Null
