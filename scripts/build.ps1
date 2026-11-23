[CmdletBinding()]
param(
    [string] $Dotnet = 'dotnet',
    [switch] $Installer
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$root = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $root 'artifacts'
$portable = Join-Path $artifacts ('portable-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $portable -Force | Out-Null

& $Dotnet run --project (Join-Path $root 'tests/DiskSpace.Tests.csproj') -c Release
& $Dotnet publish (Join-Path $root 'DiskSpace/DiskSpace.csproj') -c Release -r win-x64 --self-contained true -p:DebugType=None -o $portable

Copy-Item -LiteralPath (Join-Path $root 'README.md'), (Join-Path $root 'LICENSE') -Destination $portable
$archive = Join-Path $artifacts 'KudaDelasPamyat-windows-x64.zip'
Compress-Archive -Path (Join-Path $portable '*') -DestinationPath $archive -CompressionLevel Optimal -Force
$outputs = @($archive)

if ($Installer) {
    $iscc = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    $compiler = if ($iscc) { $iscc.Source } else { Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6/ISCC.exe' }
    if (!(Test-Path -LiteralPath $compiler)) { throw 'Inno Setup 6 is required for the installer.' }
    & $compiler "/DSourceDir=$portable" "/O$artifacts" (Join-Path $PSScriptRoot 'installer.iss')
    $outputs += Join-Path $artifacts 'KudaDelasPamyat-Setup-x64.exe'
}
foreach ($file in $outputs) {
    $hash = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $(Split-Path -Leaf $file)" | Set-Content -LiteralPath "$file.sha256" -Encoding ascii
    Write-Host "$file"
}
