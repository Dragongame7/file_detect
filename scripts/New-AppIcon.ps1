param([string]$Destination = (Join-Path $PSScriptRoot '..\assets\icon.ico'))
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$bitmap = New-Object System.Drawing.Bitmap 64,64
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$background = New-Object System.Drawing.SolidBrush ([System.Drawing.ColorTranslator]::FromHtml('#1c3046'))
$folderBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.ColorTranslator]::FromHtml('#a9c9ff'))
$dotBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.ColorTranslator]::FromHtml('#63d6b3'))
$graphics.FillRectangle($background,0,0,64,64)
$polygon = [System.Drawing.Point[]]@([System.Drawing.Point]::new(10,21),[System.Drawing.Point]::new(27,21),[System.Drawing.Point]::new(33,28),[System.Drawing.Point]::new(54,28),[System.Drawing.Point]::new(54,53),[System.Drawing.Point]::new(10,53))
$graphics.FillPolygon($folderBrush,$polygon)
$graphics.FillEllipse($dotBrush,42,10,16,16)
$icon = [System.Drawing.Icon]::FromHandle($bitmap.GetHicon())
$stream = [System.IO.File]::Create([System.IO.Path]::GetFullPath($Destination))
try { $icon.Save($stream) } finally { $stream.Dispose(); $icon.Dispose(); $graphics.Dispose(); $bitmap.Dispose(); $background.Dispose(); $folderBrush.Dispose(); $dotBrush.Dispose() }
