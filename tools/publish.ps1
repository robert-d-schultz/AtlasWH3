# Builds the AtlasWH3 alpha download: AtlasWH3.exe (editor) + AtlasWH3.Cli.exe (headless builder), self-contained
# win-x64 (no .NET install needed), plus the read-me files, in dist\AtlasWH3-<version>\ and a zip of it.
#   powershell -ExecutionPolicy Bypass -File tools\publish.ps1 [-NoZip]
param([switch]$NoZip)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
# the version carries the commit (Directory.Build.props runs git); Git for Windows is often not on PowerShell's PATH
if (-not (Get-Command git -ErrorAction SilentlyContinue) -and (Test-Path "$env:ProgramFiles\Git\cmd\git.exe")) {
    $env:PATH = "$env:ProgramFiles\Git\cmd;$env:PATH"
}
[xml]$props = Get-Content (Join-Path $root "Directory.Build.props")
$group = $props.Project.PropertyGroup | Select-Object -First 1
$version = "$($group.VersionPrefix)-$($group.VersionSuffix)"
$out = Join-Path $root "dist\AtlasWH3-$version"
if (Test-Path $out) { Remove-Item $out -Recurse -Force }

foreach ($project in "src\AtlasWH3.App\AtlasWH3.App.csproj", "src\AtlasWH3.Cli\AtlasWH3.Cli.csproj") {
    Write-Host "publishing $project"
    dotnet publish (Join-Path $root $project) -c Release -r win-x64 --self-contained true -o $out -p:DebugType=none -p:GenerateDocumentationFile=false -nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $project" }
}
foreach ($doc in "LICENSE", "README.md", "CHANGELOG.md", "KNOWN_ISSUES.md", "THIRD_PARTY_NOTICES.md") {
    Copy-Item (Join-Path $root $doc) $out
}
# nothing machine-specific may ship
Get-ChildItem $out -Include *.pdb, *.xml -Recurse | Where-Object { $_.Name -notlike "*.deps.json" } | Remove-Item -Force -ErrorAction SilentlyContinue

$size = (Get-ChildItem $out -Recurse | Measure-Object Length -Sum).Sum / 1MB
Write-Host ("{0}: {1:N0} MB" -f $out, $size)
if (-not $NoZip) {
    $zip = "$out.zip"
    if (Test-Path $zip) { Remove-Item $zip -Force }
    Compress-Archive -Path "$out\*" -DestinationPath $zip -CompressionLevel Optimal
    Write-Host ("{0}: {1:N0} MB" -f $zip, ((Get-Item $zip).Length / 1MB))
}
