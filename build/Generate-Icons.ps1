#Requires -Version 7.4
<#
.SYNOPSIS
Regenerates the original cloud/printer mark and Windows icon sizes using WPF.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, WindowsBase
$assetDirectory = Join-Path (Split-Path $PSScriptRoot -Parent) 'src/Qqq.PrintAgent/Assets/Brand'
$glyph = 'M 190 196 H 326 C 350 196 366 180 366 160 C 366 138 350 122 328 122 C 314 91 275 85 251 111 C 219 103 195 122 194 149 C 169 144 151 163 158 182 C 163 192 176 196 190 196 Z M 184 248 V 226 H 328 V 248 M 176 344 H 148 V 272 Q 148 248 172 248 H 340 Q 364 248 364 272 V 344 H 336 M 184 304 H 328 V 402 H 184 Z M 216 342 H 296 M 216 372 H 278'
$svg = @"
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 512 512" width="512" height="512">
  <title>QQQ Print Agent - cloud and printer</title>
  <defs><linearGradient id="tile" x1="0" y1="0" x2="1" y2="1"><stop stop-color="#0F766E"/><stop offset="1" stop-color="#134E4A"/></linearGradient></defs>
  <rect x="48" y="48" width="416" height="416" rx="96" fill="url(#tile)"/>
  <path d="$glyph" fill="none" stroke="#FFFFFF" stroke-width="28" stroke-linecap="round" stroke-linejoin="round"/>
</svg>
"@
[IO.File]::WriteAllText((Join-Path $assetDirectory 'app-icon.svg'), $svg)

<#
.SYNOPSIS
Renders the cloud-printer mark as one PNG byte array, with an optional health badge.
#>
function Get-MarkPng
{
   [CmdletBinding()]
   [OutputType([byte[]])]
   param([int] $Size, [string] $Status)
   $visual = [Windows.Media.DrawingVisual]::new()
   $drawing = $visual.RenderOpen()
   $drawing.PushTransform([Windows.Media.ScaleTransform]::new($Size / 512.0, $Size / 512.0))
   $gradient = [Windows.Media.LinearGradientBrush]::new([Windows.Media.ColorConverter]::ConvertFromString('#0F766E'), [Windows.Media.ColorConverter]::ConvertFromString('#134E4A'), [Windows.Point]::new(0, 0), [Windows.Point]::new(1, 1))
   $drawing.DrawRoundedRectangle($gradient, $null, [Windows.Rect]::new(48, 48, 416, 416), 96, 96)
   $pen = [Windows.Media.Pen]::new([Windows.Media.Brushes]::White, 28)
   $pen.StartLineCap = $pen.EndLineCap = [Windows.Media.PenLineCap]::Round
   $pen.LineJoin = [Windows.Media.PenLineJoin]::Round
   $drawing.DrawGeometry($null, $pen, [Windows.Media.Geometry]::Parse($glyph))
   if($Status)
   {
      $color = switch($Status) { 'green' { '#16A34A' } 'orange' { '#F59E0B' } default { '#DC2626' } }
      $badge = [Windows.Media.SolidColorBrush]::new([Windows.Media.ColorConverter]::ConvertFromString($color))
      $drawing.DrawEllipse($badge, [Windows.Media.Pen]::new([Windows.Media.Brushes]::White, 18), [Windows.Point]::new(408, 408), 88, 88)
      $symbol = switch($Status) { 'green' { 'M 367 409 L 397 439 L 449 380' } 'orange' { 'M 408 363 V 414 M 408 445 V 449' } default { 'M 377 377 L 439 439 M 439 377 L 377 439' } }
      $drawing.DrawGeometry($null, $pen, [Windows.Media.Geometry]::Parse($symbol))
   }
   $drawing.Close()
   $bitmap = [Windows.Media.Imaging.RenderTargetBitmap]::new($Size, $Size, 96, 96, [Windows.Media.PixelFormats]::Pbgra32)
   $bitmap.Render($visual)
   $encoder = [Windows.Media.Imaging.PngBitmapEncoder]::new()
   $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
   $stream = [IO.MemoryStream]::new()
   try { $encoder.Save($stream); Write-Output -NoEnumerate -InputObject ($stream.ToArray()) } finally { $stream.Dispose() }
}

$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$frames = @($sizes | ForEach-Object { , (Get-MarkPng -Size $_) })
$writer = [IO.BinaryWriter]::new([IO.File]::Create((Join-Path $assetDirectory 'app.ico')))
try
{
   $writer.Write([UInt16]0); $writer.Write([UInt16]1); $writer.Write([UInt16]$sizes.Count)
   $offset = 6 + 16 * $sizes.Count
   for($index = 0; $index -lt $sizes.Count; $index++)
   {
      $sizeByte = if($sizes[$index] -eq 256) { 0 } else { $sizes[$index] }
      $writer.Write([byte]$sizeByte); $writer.Write([byte]$sizeByte)
      $writer.Write([byte]0); $writer.Write([byte]0); $writer.Write([UInt16]1); $writer.Write([UInt16]32)
      $writer.Write([int]$frames[$index].Length); $writer.Write([int]$offset)
      $offset += $frames[$index].Length
   }
   foreach($frame in $frames) { $writer.Write([byte[]]$frame) }
}
finally { $writer.Dispose() }
[IO.File]::WriteAllBytes((Join-Path $assetDirectory 'app-preview.png'), (Get-MarkPng -Size 256))
foreach($status in @('green', 'orange', 'red'))
{
   [IO.File]::WriteAllBytes((Join-Path $assetDirectory "tray-$status.png"), (Get-MarkPng -Size 64 -Status $status))
}
Write-Output 'Generated cloud-printer SVG, multi-resolution ICO, preview and tray status PNGs.'
