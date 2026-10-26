# Process icon-master.jpg -> transparent-corner master + sized PNGs + FastTranslation.ico
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName PresentationCore
$dir = 'D:\FTranslation\assets\icon'

# --- stage 1: load 1280 jpg, apply antialiased rounded-rect alpha mask (r=252) ---
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
public static class IconMask {
    public static void Apply(string src, string dst, int radius) {
        using (Image img = Image.FromFile(src))
        using (Bitmap b = new Bitmap(img.Width, img.Height, PixelFormat.Format32bppArgb)) {
            using (Graphics g = Graphics.FromImage(b)) g.DrawImage(img, 0, 0, img.Width, img.Height);
            int w = b.Width, h = b.Height;
            var rect = new Rectangle(0, 0, w, h);
            var data = b.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
            int stride = data.Stride;
            byte[] px = new byte[stride * h];
            Marshal.Copy(data.Scan0, px, 0, px.Length);
            double cx = w / 2.0, cy = h / 2.0;
            double hx = w / 2.0 - radius, hy = h / 2.0 - radius;
            for (int y = 0; y < h; y++) {
                for (int x = 0; x < w; x++) {
                    double dx = Math.Abs(x + 0.5 - cx) - hx;
                    double dy = Math.Abs(y + 0.5 - cy) - hy;
                    double qx = Math.Max(dx, 0), qy = Math.Max(dy, 0);
                    double dist = Math.Sqrt(qx * qx + qy * qy) + Math.Min(Math.Max(dx, dy), 0) - radius;
                    // dist < 0 inside; 1px linear ramp for AA
                    double cov = Math.Max(0.0, Math.Min(1.0, -dist + 0.5));
                    byte a = (byte)(255 * cov);
                    int off = y * stride + x * 4;
                    px[off + 3] = a;
                }
            }
            Marshal.Copy(px, 0, data.Scan0, px.Length);
            b.UnlockBits(data);
            b.Save(dst, ImageFormat.Png);
        }
    }
}
'@
[IconMask]::Apply((Join-Path $dir 'icon-master.jpg'), (Join-Path $dir 'master-alpha.png'), 252)
Write-Host 'master-alpha.png done'

# --- stage 2: high-quality downscale to target sizes (System.Drawing bicubic) ---
function Save-Png([int]$size, [string]$out) {
    $src = [System.Drawing.Image]::FromFile((Join-Path $dir 'master-alpha.png'))
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.DrawImage($src, (New-Object System.Drawing.Rectangle(0, 0, $size, $size)))
    $g.Dispose(); $src.Dispose()
    $bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
}
foreach ($s in 256, 64, 48, 32, 24, 16) { Save-Png $s (Join-Path $dir "icon-$s.png") }
Write-Host 'sized pngs done'

# --- stage 3: assemble ICO (PNG-in-ICO) ---
$sizes = @(16, 24, 32, 48, 64, 256)
$blobs = @()
foreach ($s in $sizes) { $blobs += ,[System.IO.File]::ReadAllBytes((Join-Path $dir "icon-$s.png")) }
$out = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter($out)
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $bw.Write([byte]$dim); $bw.Write([byte]$dim)
    $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$blobs[$i].Length); $bw.Write([uint32]$offset)
    $offset += $blobs[$i].Length
}
foreach ($b2 in $blobs) { $bw.Write($b2) }
$bw.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $dir 'FastTranslation.ico'), $out.ToArray())
Write-Host ('FastTranslation.ico: ' + (Get-Item (Join-Path $dir 'FastTranslation.ico')).Length + ' bytes')
