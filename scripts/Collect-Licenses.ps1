param([Parameter(Mandatory=$true)][string]$Destination)
$ErrorActionPreference = 'Stop'
$workspace = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$licenseRoot = Join-Path $Destination 'licenses'
New-Item -ItemType Directory -Force -Path $licenseRoot | Out-Null
Copy-Item -Path (Join-Path $workspace 'licenses\*') -Destination $licenseRoot -Force
$sdkRoot = Join-Path $workspace '.tools\dotnet'
foreach ($name in @('LICENSE.txt','ThirdPartyNotices.txt')) {
    $source = Join-Path $sdkRoot $name
    if (Test-Path -LiteralPath $source) { Copy-Item -LiteralPath $source -Destination (Join-Path $licenseRoot ('dotnet-' + $name)) -Force }
}
$assets = Get-Content -LiteralPath (Join-Path $workspace 'src\FolderSentinel.Desktop\obj\project.assets.json') -Raw | ConvertFrom-Json
$packageRoots = @($assets.packageFolders.PSObject.Properties.Name)
foreach ($library in $assets.libraries.PSObject.Properties) {
    if ($library.Value.type -ne 'package') { continue }
    $packagePath = $null
    foreach ($packageRoot in $packageRoots) {
        $candidate = Join-Path $packageRoot $library.Value.path
        if (Test-Path -LiteralPath $candidate) { $packagePath = $candidate; break }
    }
    if (-not $packagePath) { throw ('Package missing: ' + $library.Name) }
    $target = Join-Path $licenseRoot ($library.Name.Replace('/','-'))
    New-Item -ItemType Directory -Force -Path $target | Out-Null
    foreach ($file in $library.Value.files) {
        if ($file -match '(^|/)(license|thirdparty|third-party|notice|sdk_license)[^/]*$') {
            Copy-Item -LiteralPath (Join-Path $packagePath $file) -Destination (Join-Path $target ($file.Replace('/','-'))) -Force
        }
    }
    $specFile = Get-ChildItem -LiteralPath $packagePath -Filter '*.nuspec' | Select-Object -First 1
    if ($specFile) {
        [xml]$spec = Get-Content -LiteralPath $specFile.FullName -Raw
        $license = $spec.SelectSingleNode("//*[local-name()='license']")
        $url = $spec.SelectSingleNode("//*[local-name()='licenseUrl']")
        @(('Package: ' + $library.Name), ('License: ' + $license.InnerText), ('License URL: ' + $url.InnerText)) | Set-Content -LiteralPath (Join-Path $target 'package-info.txt') -Encoding UTF8
    }
}
