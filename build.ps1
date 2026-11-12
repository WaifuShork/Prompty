<#
.SYNOPSIS
    Builds and publishes Prompty as a single self-contained executable.

.DESCRIPTION
    1. Restores and builds Release (fails on any error).
    2. Publishes artifacts\publish\Prompty.exe with the SingleFile profile: .NET runtime, WPF,
       ASP.NET Core, all packages, native libraries and symbols bundled into one file.
    3. Checks that the output really is a single file.
    4. Optionally launches the exe for a smoke test (-Smoke).
    5. Packages artifacts\Prompty-<version>-win-x64.zip and a .sha256 file next to it.

.EXAMPLE
    .\build.ps1
    .\build.ps1 -Version 0.2.0 -Smoke
#>
[CmdletBinding()]
param(
    [string]$Version,
    [switch]$Smoke
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$project = Join-Path $root 'src\Prompty\Prompty.csproj'
$artifacts = Join-Path $root 'artifacts'
$publishDir = Join-Path $artifacts 'publish'

function Step($text) { Write-Host "`n==> $text" -ForegroundColor Cyan }
function Invoke-Checked {
    & $args[0] $args[1..($args.Count - 1)]
    if ($LASTEXITCODE -ne 0) { throw "Command failed ($LASTEXITCODE): $($args -join ' ')" }
}

if (-not $Version) {
    $Version = ([xml](Get-Content $project)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
}
$versionArgs = @("-p:Version=$Version")
Write-Host "Prompty $Version" -ForegroundColor Green

Step 'Cleaning artifacts'
if (Test-Path $artifacts) { Remove-Item $artifacts -Recurse -Force }

Step 'Restoring'
Invoke-Checked dotnet restore $project -r win-x64 -p:PublishReadyToRun=true

Step 'Building (Release)'
Invoke-Checked dotnet build $project -c Release --no-restore -r win-x64 -p:PublishReadyToRun=true @versionArgs -nologo

Step 'Publishing single-file executable'
Invoke-Checked dotnet publish $project --no-restore -p:PublishProfile=SingleFile @versionArgs -nologo

$exe = Join-Path $publishDir 'Prompty.exe'
if (-not (Test-Path $exe)) { throw "Publish did not produce $exe" }
$extra = Get-ChildItem $publishDir -File | Where-Object Name -ne 'Prompty.exe'
if ($extra) { throw "Expected a single file, but publish also produced: $($extra.Name -join ', ')" }
$sizeMb = [math]::Round((Get-Item $exe).Length / 1MB, 1)
Write-Host "Prompty.exe: $sizeMb MB, no other files" -ForegroundColor Green

if ($Smoke) {
    Step 'Smoke test (launch, wait 8 s, close)'
    $p = Start-Process $exe -PassThru
    Start-Sleep -Seconds 8
    $p.Refresh()
    if ($p.HasExited) { throw "Prompty.exe exited during startup (exit code $($p.ExitCode))" }
    if ($p.MainWindowHandle -eq 0) { Write-Warning 'Process is running but no main window was found yet' }
    else { Write-Host "Window up: '$($p.MainWindowTitle)'" -ForegroundColor Green }
    $null = $p.CloseMainWindow()
    if (-not $p.WaitForExit(5000)) { $p.Kill() }
}

Step 'Packaging'
$zip = Join-Path $artifacts "Prompty-$Version-win-x64.zip"
Compress-Archive -Path $exe -DestinationPath $zip
foreach ($file in @($exe, $zip)) {
    $hash = (Get-FileHash $file -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $(Split-Path $file -Leaf)" | Set-Content "$file.sha256" -Encoding ascii
}

Write-Host "`nDone:" -ForegroundColor Green
Get-ChildItem $artifacts -File | ForEach-Object { '  {0,-40} {1,8:N1} MB' -f $_.Name, ($_.Length / 1MB) }
Write-Host "  publish\Prompty.exe"
