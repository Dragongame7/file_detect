param([switch]$SkipTests)
$ErrorActionPreference = 'Stop'
$workspace = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$dotnet = Join-Path $workspace '.tools\dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet)) { $dotnet = (Get-Command dotnet -ErrorAction Stop).Source }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$output = Join-Path $workspace 'artifacts'
$appOutput = Join-Path $output 'FolderSentinel-win-x64'
$setupOutput = Join-Path $output 'setup'
$version = ([xml](Get-Content -LiteralPath (Join-Path $workspace 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version
$zipOutput = Join-Path $output "FolderSentinel-$version-win-x64.zip"
$installerOutput = Join-Path $output "FolderSentinel-$version-Setup.exe"
New-Item -ItemType Directory -Force -Path $output | Out-Null
& (Join-Path $PSScriptRoot 'New-AppIcon.ps1')
if (-not $SkipTests) {
    & $dotnet test (Join-Path $workspace 'tests\FolderSentinel.Tests\FolderSentinel.Tests.csproj') -c Release --nologo --logger 'trx;LogFileName=results.trx' --results-directory (Join-Path $output 'test-results')
    if ($LASTEXITCODE -ne 0) { throw 'テストが失敗しました。' }
}
& $dotnet publish (Join-Path $workspace 'src\FolderSentinel.Desktop\FolderSentinel.Desktop.csproj') -c Release -r win-x64 --self-contained true -o $appOutput --nologo -p:DebugType=None -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) { throw 'アプリの発行に失敗しました。' }
Copy-Item -LiteralPath (Join-Path $workspace 'README.md') -Destination (Join-Path $appOutput 'README.md')
Copy-Item -LiteralPath (Join-Path $workspace 'THIRD-PARTY-NOTICES.md') -Destination (Join-Path $appOutput 'THIRD-PARTY-NOTICES.md')
& (Join-Path $PSScriptRoot 'Collect-Licenses.ps1') -Destination $appOutput
# Runtime license files shipped by the SDK/NuGet remain part of the distribution.
Add-Type -AssemblyName System.IO.Compression.FileSystem
$payload = Join-Path $output 'payload.zip'
if (Test-Path -LiteralPath $payload) { Remove-Item -LiteralPath $payload }
[System.IO.Compression.ZipFile]::CreateFromDirectory($appOutput, $payload, [System.IO.Compression.CompressionLevel]::Optimal, $false)
Copy-Item -LiteralPath $payload -Destination $zipOutput -Force
& $dotnet publish (Join-Path $workspace 'src\FolderSentinel.Setup\FolderSentinel.Setup.csproj') -c Release -r win-x64 --self-contained true -o $setupOutput --nologo -p:DebugType=None -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) { throw 'セットアップの発行に失敗しました。' }
Copy-Item -LiteralPath (Join-Path $setupOutput 'FolderSentinel.Setup.exe') -Destination (Join-Path $output 'FolderSentinel.Setup.exe') -Force
Copy-Item -LiteralPath (Join-Path $setupOutput 'FolderSentinel.Setup.exe') -Destination $installerOutput -Force
Get-FileHash -Algorithm SHA256 -LiteralPath $installerOutput,$zipOutput | ForEach-Object { $_.Hash + '  ' + [System.IO.Path]::GetFileName($_.Path) } | Set-Content -LiteralPath (Join-Path $output 'SHA256SUMS.txt') -Encoding ascii
Write-Output ('配布物: ' + $output)
