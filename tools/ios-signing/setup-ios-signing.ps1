param(
    [string]$Repo,
    [Parameter(Mandatory = $true)][string]$ApiKey,
    [string]$ApiKeyId,
    [Parameter(Mandatory = $true)][string]$IssuerId,
    [Parameter(Mandatory = $true)][string]$Certificate,
    [string]$CertificatePassword,
    [Parameter(Mandatory = $true)][string]$Profile,
    [string]$SigningIdentity,
    [ValidateSet("app-store", "ad-hoc", "development", "enterprise")]
    [string]$ExportMethod = "app-store",
    [ValidateSet("true", "false")]
    [string]$UploadTestFlight = "false",
    [switch]$Trigger,
    [switch]$DryRun
)

$ErrorActionPreference = "Stop"

function Fail($Message) {
    Write-Error $Message
    exit 1
}

function Require-File($Label, $Path) {
    if ([string]::IsNullOrWhiteSpace($Path)) {
        Fail "$Label is required."
    }
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        Fail "$Label does not exist: $Path"
    }
}

function Convert-FileToBase64($Path) {
    [Convert]::ToBase64String([IO.File]::ReadAllBytes((Resolve-Path -LiteralPath $Path).ProviderPath))
}

function Infer-Repo {
    $remote = (& git config --get remote.origin.url 2>$null)
    if ([string]::IsNullOrWhiteSpace($remote)) {
        return $null
    }

    $remote = $remote.Trim()
    $remote = $remote -replace '\.git$', ''
    if ($remote -match 'github\.com[:/](.+/.+)$') {
        return $Matches[1]
    }

    return $null
}

function Set-GitHubSecret($Name, $Value) {
    if ($DryRun) {
        Write-Host "would set GitHub secret: $Name"
        return
    }

    $Value | gh secret set $Name --repo $Repo
}

function Try-ReadProfileDetails($Path) {
    $details = [ordered]@{
        Name = "unknown"
        UUID = "unknown"
        TeamId = "unknown"
        BundleId = "unknown"
    }

    if (-not $IsMacOS) {
        return $details
    }

    $tmp = [IO.Path]::GetTempFileName()
    try {
        & security cms -D -i $Path > $tmp 2>$null
        if ($LASTEXITCODE -ne 0) {
            return $details
        }

        $name = & /usr/libexec/PlistBuddy -c 'Print :Name' $tmp 2>$null
        $uuid = & /usr/libexec/PlistBuddy -c 'Print :UUID' $tmp 2>$null
        $teamId = & /usr/libexec/PlistBuddy -c 'Print :TeamIdentifier:0' $tmp 2>$null
        $applicationIdentifier = & /usr/libexec/PlistBuddy -c 'Print :Entitlements:application-identifier' $tmp 2>$null

        if (-not [string]::IsNullOrWhiteSpace($name)) { $details.Name = $name.Trim() }
        if (-not [string]::IsNullOrWhiteSpace($uuid)) { $details.UUID = $uuid.Trim() }
        if (-not [string]::IsNullOrWhiteSpace($teamId)) { $details.TeamId = $teamId.Trim() }
        if (-not [string]::IsNullOrWhiteSpace($applicationIdentifier) -and $details.TeamId -ne "unknown") {
            $prefix = "$($details.TeamId)."
            if ($applicationIdentifier.StartsWith($prefix)) {
                $details.BundleId = $applicationIdentifier.Substring($prefix.Length).Trim()
            }
        }
    }
    finally {
        Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue
    }

    return $details
}

Require-File "-ApiKey" $ApiKey
Require-File "-Certificate" $Certificate
Require-File "-Profile" $Profile

if ([string]::IsNullOrWhiteSpace($ApiKeyId)) {
    $apiKeyFile = Split-Path -Leaf $ApiKey
    if ($apiKeyFile -match '^AuthKey_([^./]+)\.p8$') {
        $ApiKeyId = $Matches[1]
    }
}
if ([string]::IsNullOrWhiteSpace($ApiKeyId)) {
    Fail "-ApiKeyId is required when the key file is not named AuthKey_<KEY_ID>.p8."
}

if ([string]::IsNullOrWhiteSpace($Repo)) {
    $Repo = Infer-Repo
}
if ([string]::IsNullOrWhiteSpace($Repo)) {
    Fail "-Repo is required when git origin is not a GitHub repository."
}

if ([string]::IsNullOrWhiteSpace($CertificatePassword)) {
    $secure = Read-Host "Password for $Certificate" -AsSecureString
    $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    try {
        $CertificatePassword = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr)
    }
    finally {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr)
    }
}

if (-not $DryRun) {
    if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
        Fail "GitHub CLI is required: https://cli.github.com/"
    }
    & gh auth status --hostname github.com | Out-Null
}

$profileDetails = Try-ReadProfileDetails $Profile

Write-Host "Repository: $Repo"
Write-Host "API key ID: $ApiKeyId"
Write-Host "Provisioning profile: $($profileDetails.Name)"
Write-Host "Profile UUID: $($profileDetails.UUID)"
Write-Host "Team ID: $($profileDetails.TeamId)"
Write-Host "Bundle ID: $($profileDetails.BundleId)"
Write-Host "Export method: $ExportMethod"
Write-Host ""

Set-GitHubSecret "APP_STORE_CONNECT_KEY_ID" $ApiKeyId
Set-GitHubSecret "APP_STORE_CONNECT_ISSUER_ID" $IssuerId
Set-GitHubSecret "APP_STORE_CONNECT_API_KEY_BASE64" (Convert-FileToBase64 $ApiKey)
Set-GitHubSecret "IOS_DISTRIBUTION_CERTIFICATE_BASE64" (Convert-FileToBase64 $Certificate)
Set-GitHubSecret "IOS_DISTRIBUTION_CERTIFICATE_PASSWORD" $CertificatePassword
Set-GitHubSecret "IOS_PROVISIONING_PROFILE_BASE64" (Convert-FileToBase64 $Profile)

if (-not [string]::IsNullOrWhiteSpace($SigningIdentity)) {
    Set-GitHubSecret "IOS_SIGNING_CERTIFICATE_NAME" $SigningIdentity
}

if ($Trigger) {
    if ($DryRun) {
        Write-Host "would trigger workflow: ios-signed-build.yml"
    }
    else {
        & gh workflow run ios-signed-build.yml `
            --repo $Repo `
            --field "export_method=$ExportMethod" `
            --field "upload_testflight=$UploadTestFlight"
    }
}

Write-Host ""
Write-Host "Done. Run the GitHub Actions workflow named 'iOS Signed Build' to produce the IPA."
