param(
    [string]$Version = "",
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$PackId = "MrQ.TodoFloat",
    [string]$PackTitle = "TodoFloat",
    [string]$RepoUrl = "https://github.com/MrQ-Coding/TodoFloat",
    [string]$OutputDir = "",
    [switch]$UploadToGitHub,
    [switch]$Publish,
    [string]$GitHubToken = $env:GITHUB_TOKEN
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$projectPath = Join-Path $repoRoot "TodoFloat.csproj"

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
