param(
    [string]$Version,
    [switch]$SelfContained,
    [switch]$SkipZip
)

$ErrorActionPreference = "Stop"

function Get-ProjectVersion {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ProjectFile
    )

    [xml]$projectXml = Get-Content -LiteralPath $ProjectFile
    foreach ($propertyGroup in $projectXml.Project.PropertyGroup) {
        if ($propertyGroup.Version) {
            $value = $propertyGroup.Version.Trim()
            if ($value) {
                return $value
            }
        }
    }

    throw "Project version not found in $ProjectFile."
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$projectFile = Join-Path $repoRoot "FolderPeek.App\FolderPeek.App.csproj"
$resolvedVersion = if ($Version) { $Version } else { Get-ProjectVersion -ProjectFile $projectFile }
$runtimeIdentifier = "win-x64"
$publishModeSuffix = if ($SelfContained) { "-self-contained" } else { "" }
$packageName = "FolderPeek-v$resolvedVersion-click-folder-expand-$runtimeIdentifier$publishModeSuffix"
$stagingRoot = Join-Path $repoRoot "output\staging"
$stagingPath = Join-Path $stagingRoot $packageName
$releaseRoot = Join-Path $repoRoot "output\release"
$zipPath = Join-Path $releaseRoot "$packageName.zip"
$selfContainedValue = if ($SelfContained) { "true" } else { "false" }

if (Test-Path -LiteralPath $stagingPath) {
    Remove-Item -LiteralPath $stagingPath -Recurse -Force
}

New-Item -ItemType Directory -Force -Path $stagingRoot | Out-Null
New-Item -ItemType Directory -Force -Path $releaseRoot | Out-Null

dotnet publish $projectFile `
    -c Release `
    -r $runtimeIdentifier `
    --self-contained:$selfContainedValue `
    -o $stagingPath

Get-ChildItem -LiteralPath $stagingPath -Filter *.pdb -File -ErrorAction SilentlyContinue |
    Remove-Item -Force -ErrorAction SilentlyContinue

if (-not $SkipZip) {
    if (Test-Path -LiteralPath $zipPath) {
        Remove-Item -LiteralPath $zipPath -Force
    }

    Compress-Archive -Path (Join-Path $stagingPath "*") -DestinationPath $zipPath -Force
}

Write-Host "Published to: $stagingPath"
if (-not $SkipZip) {
    Write-Host "Archive: $zipPath"
}
