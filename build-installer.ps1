# Builds dist\LiteMixer_<major.minor>.exe, the custom installer in installer\Setup: a native exe embedding a
# self-contained app, so the target PC needs nothing installed. Signed with Azure Artifact Signing when
# signing\metadata.json exists. This script only builds; it never installs anything.
#
#   ./build-installer.ps1            build, sign if configured
#   ./build-installer.ps1 -NoSign    build unsigned
#
# Signing needs: signing\metadata.json (copy metadata.example.json), "az login" with an account that has the
# "Artifact Signing Certificate Profile Signer" role, and the Microsoft.Azure.ArtifactSigningClientTools package.
param([switch]$NoSign)
$ErrorActionPreference = "Stop"

# The native (AOT) linker step finds Visual Studio through vswhere.exe.
$env:PATH = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer;$env:PATH"

$root = $PSScriptRoot
$dist = Join-Path $root "dist"
$appOut = Join-Path $dist "app"
$setupProject = Join-Path $root "installer\Setup\Setup.csproj"
$setupBuild = Join-Path $root "installer\Setup"
[xml]$proj = Get-Content (Join-Path $root "LiteMixer.csproj")
$version = $proj.Project.PropertyGroup.Version | Select-Object -First 1
# 1.0.0 builds LiteMixer_1.0.exe; a patch like 1.0.1 keeps its third number so file names never collide.
$parts = $version -split '\.'
$short = if ($parts.Count -gt 2 -and $parts[2] -ne '0') { $parts[0..2] -join '.' } else { $parts[0..1] -join '.' }

# ---- Signing setup ----
$metadata = Join-Path $root "signing\metadata.json"
$sign = -not $NoSign -and (Test-Path $metadata)
if ($sign) {
    $signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" |
        Sort-Object { [version]($_.Directory.Parent.Name) } | Select-Object -Last 1 -Expand FullName
    $dlib = @("$env:LOCALAPPDATA\Microsoft\MicrosoftArtifactSigningClientTools\Azure.CodeSigning.Dlib.dll",
              "$env:LOCALAPPDATA\Microsoft\MicrosoftTrustedSigningClientTools\Azure.CodeSigning.Dlib.dll") |
        Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $signtool) { throw "signtool.exe not found (install the Windows SDK)" }
    if (-not $dlib) { throw "Azure.CodeSigning.Dlib.dll not found (winget install Microsoft.Azure.ArtifactSigningClientTools)" }
    $signArgs = @("sign", "/v", "/fd", "SHA256", "/tr", "http://timestamp.acs.microsoft.com", "/td", "SHA256",
                  "/dlib", $dlib, "/dmdf", $metadata)
    Write-Output "Signing with $signtool"
} elseif (-not $NoSign) {
    Write-Warning "signing\metadata.json not found: building UNSIGNED. See signing\metadata.example.json."
}

function Invoke-Signing([string[]]$files) {
    if (-not $sign) { return }
    & $signtool @signArgs @files
    if ($LASTEXITCODE -ne 0) { throw "signtool failed ($LASTEXITCODE)" }
}

function Publish-Setup([string]$output, [string]$payload) {
    # The two builds differ only by the embedded payload, so neither may reuse the other's outputs.
    foreach ($folder in "bin", "obj") {
        $path = Join-Path $setupBuild $folder
        if (Test-Path $path) { Remove-Item $path -Recurse -Force }
    }
    $publishArgs = @("publish", $setupProject, "-c", "Release", "-o", $output, "-p:Version=$version")
    if ($payload) { $publishArgs += "-p:PayloadPath=$payload" }
    & dotnet @publishArgs
    if ($LASTEXITCODE -ne 0) { throw "Setup publish failed" }
}

# ---- App ----
# Clears the contents rather than the folder, which an open Explorer window may be holding.
if (Test-Path $dist) { Get-ChildItem $dist -Force | Remove-Item -Recurse -Force }
dotnet publish (Join-Path $root "LiteMixer.csproj") -c Release -o $appOut `
    -p:SelfContained=true -p:PublishSingleFile=false -p:PublishReadyToRun=true -p:DebugType=none `
    -p:RuntimeFrameworkVersion=10.0.14
if ($LASTEXITCODE -ne 0) { throw "publish failed" }
Copy-Item (Join-Path $root "LICENSE") (Join-Path $appOut "LICENSE.txt")
Invoke-Signing @((Join-Path $appOut "LiteMixer.exe"), (Join-Path $appOut "LiteMixer.dll"))

# ---- Uninstaller: the setup exe without a payload, shipped inside the app folder ----
$uninstallerOut = Join-Path $dist "uninstaller"
Publish-Setup $uninstallerOut ""
$uninstaller = Join-Path $appOut "Uninstall LiteMixer.exe"
Copy-Item (Join-Path $uninstallerOut "LiteMixerSetup.exe") $uninstaller
Invoke-Signing @($uninstaller)

# ---- Payload ----
$payload = Join-Path $dist "payload.bin"
dotnet run --project (Join-Path $root "installer\Pack\Pack.csproj") -c Release -- $appOut $payload
if ($LASTEXITCODE -ne 0) { throw "pack failed" }

# ---- Installer ----
$setupOut = Join-Path $dist "setup"
Publish-Setup $setupOut $payload
$setup = Join-Path $dist "LiteMixer_$short.exe"
Copy-Item (Join-Path $setupOut "LiteMixerSetup.exe") $setup
Invoke-Signing @($setup)

$size = [math]::Round((Get-Item $setup).Length / 1MB, 1)
$status = (Get-AuthenticodeSignature $setup).Status
Write-Output "Built $setup ($size MB, signature: $status)"
