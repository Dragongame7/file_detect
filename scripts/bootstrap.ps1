$ErrorActionPreference = 'Stop'
$workspace = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$toolDirectory = Join-Path $workspace '.tools'
New-Item -ItemType Directory -Force -Path $toolDirectory | Out-Null
$installer = Join-Path $toolDirectory 'dotnet-install.ps1'
Invoke-WebRequest -Uri 'https://dot.net/v1/dotnet-install.ps1' -OutFile $installer
& $installer -Version '10.0.402' -InstallDir (Join-Path $toolDirectory 'dotnet') -NoPath
if ($LASTEXITCODE -ne 0) { throw 'SDKの準備に失敗しました。' }
