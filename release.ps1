# Publishes a new version: builds and signs the installer, tags the commit, pushes, and creates the GitHub Release
# that users' copies of LiteMixer will offer as an update.
#
#   ./release.ps1 -Version 1.1.0 -Notes "Added X. Fixed Y."
#   ./release.ps1 -Version 1.1.0 -NotesFile notes.md
#
# Needs a clean working tree on main, "az login" (signing) and "gh auth login" (publishing).
param(
    [Parameter(Mandatory)][string]$Version,
    [string]$Notes,
    [string]$NotesFile
)
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Version must look like 1.2.0" }
if (-not $Notes -and -not $NotesFile) { throw "Give -Notes or -NotesFile; they are shown to users in the update prompt." }
if ((git branch --show-current) -ne "main") { throw "Release from the main branch." }
if (git status --porcelain) { throw "Commit or discard local changes first." }
$tag = "v$Version"
if (git tag --list $tag) { throw "$tag already exists. Releases need a new version number." }

# The app only offers versions newer than itself, so a release must never go backwards.
$latest = (gh release list --limit 1 --exclude-drafts --exclude-pre-releases --json tagName --jq ".[0].tagName")
if ($latest -and $latest -ne "null" -and [version]$Version -le [version]($latest.TrimStart('v'))) { throw "$Version is not newer than the latest release, $latest." }

$csproj = "LiteMixer.csproj"
$text = [IO.File]::ReadAllText((Resolve-Path $csproj))
$updated = [regex]::Replace($text, '<Version>[^<]+</Version>', "<Version>$Version</Version>")
if ($updated -ne $text) {
    [IO.File]::WriteAllText((Resolve-Path $csproj), $updated)
    git add $csproj
    git commit -q -m "chore: release $Version"
}

& "$PSScriptRoot\build-installer.ps1"

$parts = $Version -split '\.'
$short = if ($parts[2] -ne '0') { $Version } else { $parts[0..1] -join '.' }
$setup = Join-Path $PSScriptRoot "dist\LiteMixer_$short.exe"
$signature = Get-AuthenticodeSignature $setup
if ($signature.Status -ne "Valid") { throw "Refusing to publish: $setup signature is $($signature.Status). Updates only accept signed builds." }

git tag $tag
git push origin main $tag
if ($LASTEXITCODE -ne 0) { throw "git push failed" }

# The same file under a fixed name gives the README one link that always downloads the newest version:
# releases/latest/download/LiteMixer-Setup.exe. The updater only takes names starting with "LiteMixer_", so it ignores this.
$stable = Join-Path $PSScriptRoot "dist\LiteMixer-Setup.exe"
Copy-Item $setup $stable -Force

$notesArgs = if ($NotesFile) { @("--notes-file", $NotesFile) } else { @("--notes", $Notes) }
gh release create $tag $setup $stable --title "LiteMixer $short" --latest @notesArgs
if ($LASTEXITCODE -ne 0) { throw "gh release create failed" }
Write-Output "Released LiteMixer $short"
