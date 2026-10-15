$ErrorActionPreference = 'Stop'
$workspace = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifacts = Join-Path $workspace 'artifacts'
$extracted = Join-Path $artifacts 'smoke-extraction'
$dataDirectory = Join-Path $artifacts ('smoke-data-' + [guid]::NewGuid().ToString('N'))
$setup = Join-Path $artifacts 'FolderSentinel.Setup.exe'
$extraction = Start-Process -FilePath $setup -ArgumentList @('--extract-only', ('"' + $extracted + '"')) -WindowStyle Hidden -Wait -PassThru
if ($extraction.ExitCode -ne 0) { throw 'セットアップからの展開に失敗しました。' }
$published = Join-Path $artifacts 'FolderSentinel-win-x64'
foreach ($name in @('FolderSentinel.exe','coreclr.dll','Microsoft.WindowsAppRuntime.dll','e_sqlite3.dll')) {
    $expected = (Get-FileHash -LiteralPath (Join-Path $published $name) -Algorithm SHA256).Hash
    $actual = (Get-FileHash -LiteralPath (Join-Path $extracted $name) -Algorithm SHA256).Hash
    if ($expected -ne $actual) { throw ('展開内容の不一致: ' + $name) }
}
$startInfo = New-Object System.Diagnostics.ProcessStartInfo
$startInfo.FileName = Join-Path $extracted 'FolderSentinel.exe'
$startInfo.Arguments = '--diagnostics --data-dir "' + $dataDirectory + '" --no-startup'
$startInfo.UseShellExecute = $false
$startInfo.CreateNoWindow = $true
$startInfo.WindowStyle = [System.Diagnostics.ProcessWindowStyle]::Hidden
$startInfo.EnvironmentVariables['DOTNET_ROOT'] = 'C:\FolderSentinel-no-runtime'
$startInfo.EnvironmentVariables['DOTNET_ROOT_X64'] = 'C:\FolderSentinel-no-runtime'
$startInfo.EnvironmentVariables['PATH'] = [Environment]::GetFolderPath('System')
$appProcess = [System.Diagnostics.Process]::Start($startInfo)
if (-not $appProcess.WaitForExit(30000)) { $appProcess.Kill(); throw 'アプリの診断がタイムアウトしました。' }
if ($appProcess.ExitCode -ne 0) { throw 'アプリの起動に失敗しました。' }
$report = Get-Content -LiteralPath (Join-Path $dataDirectory 'diagnostics.json') -Raw | ConvertFrom-Json
if (-not $report.NotificationReady) { throw ('通知の初期化に失敗: ' + $report.RegistrationError) }
if ($report.StartupRegistered) { throw '診断用アプリが自動起動に登録されています。' }
if ($report.Modules.Count -ne 4) { throw '必要な実行モジュールを確認できませんでした。' }
$expectedPrefix = [System.IO.Path]::GetFullPath($extracted) + [System.IO.Path]::DirectorySeparatorChar
foreach ($module in $report.Modules) {
    if (-not $module.FileName.StartsWith($expectedPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw ('同梱されていないモジュールを使用: ' + $module.FileName) }
}
Copy-Item -LiteralPath (Join-Path $dataDirectory 'diagnostics.json') -Destination (Join-Path $artifacts 'smoke-test-results.json') -Force
Write-Output 'PASS: セットアップ展開、ランタイムの同梱、通知初期化、診断後の終了。'
