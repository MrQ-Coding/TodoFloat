param(
    [string]$Version = "",
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$PackId = "MrQ.TodoFloat",
    [string]$PackTitle = "TodoFloat",
    [string]$RepoUrl = "https://github.com/MrQ-Coding/TodoFloat",
    [string]$OutputDir = "",
    [switch]$SkipLauncher,
    [switch]$UploadToGitHub,
    [switch]$Publish,
    [string]$GitHubToken = $env:GITHUB_TOKEN
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$projectPath = Join-Path $repoRoot "TodoFloat.csproj"
$launcherProjectPath = Join-Path $repoRoot "InstallerLauncher\TodoFloat.InstallerLauncher.csproj"

if ([string]::IsNullOrWhiteSpace($Version)) {
    [xml]$project = Get-Content -LiteralPath $projectPath
    $versionGroup = $project.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1
    $Version = $versionGroup.Version
}

if ([string]::IsNullOrWhiteSpace($Version)) {
    throw "Version was not supplied and could not be read from TodoFloat.csproj."
}

if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = Join-Path $repoRoot "Releases"
}

$publishDir = Join-Path $repoRoot "publish\velopack-$Runtime"

Write-Host "Restoring local tools..."
& dotnet tool restore

Write-Host "Publishing TodoFloat $Version for $Runtime..."
if (Test-Path -LiteralPath $publishDir) {
    Remove-Item -LiteralPath $publishDir -Recurse -Force
}

& dotnet publish $projectPath `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -o $publishDir `
    /p:PublishSingleFile=false `
    /p:Version=$Version

Write-Host "Packing Velopack release..."
& dotnet tool run vpk -- pack `
    --packId $PackId `
    --packTitle $PackTitle `
    --packVersion $Version `
    --packDir $publishDir `
    --mainExe "TodoFloat.exe" `
    --runtime $Runtime `
    --outputDir $OutputDir

if (-not $SkipLauncher) {
    $setupExe = Get-ChildItem -LiteralPath $OutputDir -Filter "*-Setup.exe" |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -First 1

    if ($setupExe -eq $null) {
        throw "Velopack Setup.exe was not found in $OutputDir."
    }

    $launcherPublishDir = Join-Path $repoRoot "publish\setup-launcher-$Runtime"
    if (Test-Path -LiteralPath $launcherPublishDir) {
        Remove-Item -LiteralPath $launcherPublishDir -Recurse -Force
    }

    Write-Host "Publishing custom setup launcher..."
    & dotnet publish $launcherProjectPath `
        -c $Configuration `
        -r $Runtime `
        --self-contained true `
        -o $launcherPublishDir `
        /p:Version=$Version `
        /p:PublishSingleFile=true `
        /p:EmbeddedSetupPath="$($setupExe.FullName)"

    $launcherExe = Join-Path $launcherPublishDir "TodoFloatSetup.exe"
    if (-not (Test-Path -LiteralPath $launcherExe)) {
        throw "Custom setup launcher was not created: $launcherExe"
    }

    Copy-Item -LiteralPath $launcherExe -Destination (Join-Path $OutputDir "TodoFloatSetup.exe") -Force
}

if ($UploadToGitHub) {
    if ([string]::IsNullOrWhiteSpace($GitHubToken)) {
        throw "Set GITHUB_TOKEN or pass -GitHubToken before uploading to GitHub Releases."
    }

    Write-Host "Uploading release assets to GitHub..."
    $env:VPK_TOKEN = $GitHubToken

    $uploadArgs = @(
        "tool", "run", "vpk", "--",
        "upload", "github",
        "--repoUrl", $RepoUrl,
        "--outputDir", $OutputDir,
        "--tag", "v$Version",
        "--releaseName", "$PackTitle $Version"
    )

    if ($Publish) {
        $uploadArgs += "--publish"
    }

    & dotnet @uploadArgs
}

Write-Host "Done. Release files are in: $OutputDir"
