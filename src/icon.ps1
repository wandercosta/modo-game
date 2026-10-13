# Gera o ícone (PNG dentro de .ico) com o raio verde
param([string]$Out)
Add-Type -AssemblyName System.Drawing
$s = 256
$bmp = New-Object System.Drawing.Bitmap $s, $s
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = 'AntiAlias'; $g.TextRenderingHint = 'AntiAliasGridFit'
$path = New-Object System.Drawing.Drawing2D.GraphicsPath
$r = 56; $path.AddArc(0,0,$r,$r,180,90); $path.AddArc($s-$r,0,$r,$r,270,90); $path.AddArc($s-$r,$s-$r,$r,$r,0,90); $path.AddArc(0,$s-$r,$r,$r,90,90); $path.CloseFigure()
$g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(11,13,17))), $path)
$g.DrawEllipse((New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(61,220,151)), 10), 38, 38, 180, 180)
$font = New-Object System.Drawing.Font 'Segoe Fluent Icons', 92
$sf = New-Object System.Drawing.StringFormat; $sf.Alignment = 'Center'; $sf.LineAlignment = 'Center'
$g.DrawString([string][char]0xE945, $font, (New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(61,220,151))), (New-Object System.Drawing.RectangleF 0, 6, $s, $s), $sf)
$ms = New-Object System.IO.MemoryStream; $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); $png = $ms.ToArray()
$fs = [System.IO.File]::Create($Out); $w = New-Object System.IO.BinaryWriter $fs
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]1)
$w.Write([byte]0); $w.Write([byte]0); $w.Write([byte]0); $w.Write([byte]0)
$w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$png.Length); $w.Write([uint32]22)
$w.Write($png); $w.Close()
