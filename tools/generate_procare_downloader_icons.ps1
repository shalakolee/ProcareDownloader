param(
    [string]$ProjectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing

function New-Color {
    param(
        [int]$A,
        [int]$R,
        [int]$G,
        [int]$B
    )

    [System.Drawing.Color]::FromArgb($A, $R, $G, $B)
}

function New-RoundedRectPath {
    param(
        [float]$X,
        [float]$Y,
        [float]$W,
        [float]$H,
        [float]$Radius
    )

    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $d = $Radius * 2
    $path.AddArc($X, $Y, $d, $d, 180, 90)
    $path.AddArc($X + $W - $d, $Y, $d, $d, 270, 90)
    $path.AddArc($X + $W - $d, $Y + $H - $d, $d, $d, 0, 90)
    $path.AddArc($X, $Y + $H - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    return $path
}

function Fill-RoundedRect {
    param(
        [System.Drawing.Graphics]$Graphics,
        [System.Drawing.Brush]$Brush,
        [float]$X,
        [float]$Y,
        [float]$W,
        [float]$H,
        [float]$Radius
    )

    $path = New-RoundedRectPath $X $Y $W $H $Radius
    try {
        $Graphics.FillPath($Brush, $path)
    } finally {
        $path.Dispose()
    }
}

function Stroke-RoundedRect {
    param(
        [System.Drawing.Graphics]$Graphics,
        [System.Drawing.Pen]$Pen,
        [float]$X,
        [float]$Y,
        [float]$W,
        [float]$H,
        [float]$Radius
    )

    $path = New-RoundedRectPath $X $Y $W $H $Radius
    try {
        $Graphics.DrawPath($Pen, $path)
    } finally {
        $path.Dispose()
    }
}

function Draw-Icon {
    param([string]$OutputPath)

    $size = 1024
    $bmp = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality

    try {
        $bgTop = New-Color 255 248 252 255
        $bgBottom = New-Color 255 229 246 244
        $bgBrush = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
            [System.Drawing.RectangleF]::new(0, 0, $size, $size),
            $bgTop,
            $bgBottom,
            90
        )
        try {
            $g.FillRectangle($bgBrush, 0, 0, $size, $size)
        } finally {
            $bgBrush.Dispose()
        }

        $softTeal = [System.Drawing.SolidBrush]::new((New-Color 42 15 118 110))
        try {
            $g.FillEllipse($softTeal, -170, 710, 470, 470)
            $g.FillEllipse($softTeal, 760, -150, 430, 430)
        } finally {
            $softTeal.Dispose()
        }

        $ringSpecs = @(
            @{ X = 248; Y = 160; Color = (New-Color 232 30 184 209) },
            @{ X = 410; Y = 160; Color = (New-Color 232 97 209 125) },
            @{ X = 330; Y = 302; Color = (New-Color 232 244 211 94) },
            @{ X = 492; Y = 302; Color = (New-Color 232 229 82 135) }
        )

        foreach ($ring in $ringSpecs) {
            $pen = [System.Drawing.Pen]::new($ring.Color, 72)
            $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
            $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
            try {
                $g.DrawEllipse($pen, [float]$ring.X, [float]$ring.Y, 285, 285)
            } finally {
                $pen.Dispose()
            }
        }

        $shadow = [System.Drawing.SolidBrush]::new((New-Color 48 16 24 40))
        $card = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::White)
        $photoBg = [System.Drawing.SolidBrush]::new((New-Color 255 232 242 252))
        $teal = New-Color 255 15 118 110
        $tealBrush = [System.Drawing.SolidBrush]::new($teal)
        $cyanBrush = [System.Drawing.SolidBrush]::new((New-Color 255 29 184 209))
        $greenBrush = [System.Drawing.SolidBrush]::new((New-Color 255 97 209 125))
        $yellowBrush = [System.Drawing.SolidBrush]::new((New-Color 255 244 211 94))
        $inkBrush = [System.Drawing.SolidBrush]::new((New-Color 255 16 24 40))
        $borderPen = [System.Drawing.Pen]::new((New-Color 255 15 118 110), 28)
        $innerPen = [System.Drawing.Pen]::new((New-Color 255 211 224 238), 16)
        $arrowPen = [System.Drawing.Pen]::new($teal, 44)
        $arrowPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
        $arrowPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round

        try {
            Fill-RoundedRect $g $shadow 218 344 588 484 94
            Fill-RoundedRect $g $card 202 326 588 484 94
            Stroke-RoundedRect $g $borderPen 202 326 588 484 94

            Fill-RoundedRect $g $photoBg 290 414 412 250 48
            Stroke-RoundedRect $g $innerPen 290 414 412 250 48

            $mountain1 = [System.Drawing.PointF[]]@(
                [System.Drawing.PointF]::new(314, 626),
                [System.Drawing.PointF]::new(430, 500),
                [System.Drawing.PointF]::new(540, 626)
            )
            $mountain2 = [System.Drawing.PointF[]]@(
                [System.Drawing.PointF]::new(454, 626),
                [System.Drawing.PointF]::new(584, 468),
                [System.Drawing.PointF]::new(680, 626)
            )
            $g.FillPolygon($greenBrush, $mountain1)
            $g.FillPolygon($cyanBrush, $mountain2)
            $g.FillEllipse($yellowBrush, 606, 446, 58, 58)

            $g.DrawLine($arrowPen, 496, 660, 496, 752)
            $arrowLeft = [System.Drawing.PointF[]]@(
                [System.Drawing.PointF]::new(424, 706),
                [System.Drawing.PointF]::new(496, 782),
                [System.Drawing.PointF]::new(568, 706)
            )
            $arrowHeadPen = [System.Drawing.Pen]::new($teal, 42)
            $arrowHeadPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
            $arrowHeadPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
            try {
                $g.DrawLines($arrowHeadPen, $arrowLeft)
            } finally {
                $arrowHeadPen.Dispose()
            }

            $trayPen = [System.Drawing.Pen]::new((New-Color 255 16 24 40), 34)
            $trayPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
            $trayPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
            try {
                $g.DrawLine($trayPen, 392, 806, 600, 806)
            } finally {
                $trayPen.Dispose()
            }
        } finally {
            $shadow.Dispose()
            $card.Dispose()
            $photoBg.Dispose()
            $tealBrush.Dispose()
            $cyanBrush.Dispose()
            $greenBrush.Dispose()
            $yellowBrush.Dispose()
            $inkBrush.Dispose()
            $borderPen.Dispose()
            $innerPen.Dispose()
            $arrowPen.Dispose()
        }

        $directory = Split-Path $OutputPath -Parent
        New-Item -ItemType Directory -Force -Path $directory | Out-Null
        $bmp.Save($OutputPath, [System.Drawing.Imaging.ImageFormat]::Png)
    } finally {
        $g.Dispose()
        $bmp.Dispose()
    }
}

function Save-ResizedIcon {
    param(
        [string]$SourcePath,
        [string]$OutputPath,
        [int]$Size
    )

    $source = [System.Drawing.Bitmap]::FromFile($SourcePath)
    $target = [System.Drawing.Bitmap]::new($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($target)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality

    try {
        $g.DrawImage($source, 0, 0, $Size, $Size)
        New-Item -ItemType Directory -Force -Path (Split-Path $OutputPath -Parent) | Out-Null
        $target.Save($OutputPath, [System.Drawing.Imaging.ImageFormat]::Png)
    } finally {
        $g.Dispose()
        $target.Dispose()
        $source.Dispose()
    }
}

$flutterRoot = Join-Path $ProjectRoot 'ProcareDownloader.Flutter'
$sourceIcon = Join-Path $flutterRoot 'assets\brand\procare_downloader_icon_1024.png'
Draw-Icon $sourceIcon

$androidIcons = @(
    @{ Path = 'android\app\src\main\res\mipmap-mdpi\ic_launcher.png'; Size = 48 },
    @{ Path = 'android\app\src\main\res\mipmap-hdpi\ic_launcher.png'; Size = 72 },
    @{ Path = 'android\app\src\main\res\mipmap-xhdpi\ic_launcher.png'; Size = 96 },
    @{ Path = 'android\app\src\main\res\mipmap-xxhdpi\ic_launcher.png'; Size = 144 },
    @{ Path = 'android\app\src\main\res\mipmap-xxxhdpi\ic_launcher.png'; Size = 192 }
)

foreach ($icon in $androidIcons) {
    Save-ResizedIcon $sourceIcon (Join-Path $flutterRoot $icon.Path) $icon.Size
}

$iosIconRoot = Join-Path $flutterRoot 'ios\Runner\Assets.xcassets\AppIcon.appiconset'
$iosIcons = @(
    @{ Name = 'Icon-App-20x20@1x.png'; Size = 20 },
    @{ Name = 'Icon-App-20x20@2x.png'; Size = 40 },
    @{ Name = 'Icon-App-20x20@3x.png'; Size = 60 },
    @{ Name = 'Icon-App-29x29@1x.png'; Size = 29 },
    @{ Name = 'Icon-App-29x29@2x.png'; Size = 58 },
    @{ Name = 'Icon-App-29x29@3x.png'; Size = 87 },
    @{ Name = 'Icon-App-40x40@1x.png'; Size = 40 },
    @{ Name = 'Icon-App-40x40@2x.png'; Size = 80 },
    @{ Name = 'Icon-App-40x40@3x.png'; Size = 120 },
    @{ Name = 'Icon-App-60x60@2x.png'; Size = 120 },
    @{ Name = 'Icon-App-60x60@3x.png'; Size = 180 },
    @{ Name = 'Icon-App-76x76@1x.png'; Size = 76 },
    @{ Name = 'Icon-App-76x76@2x.png'; Size = 152 },
    @{ Name = 'Icon-App-83.5x83.5@2x.png'; Size = 167 },
    @{ Name = 'Icon-App-1024x1024@1x.png'; Size = 1024 }
)

foreach ($icon in $iosIcons) {
    Save-ResizedIcon $sourceIcon (Join-Path $iosIconRoot $icon.Name) $icon.Size
}

$preview = Join-Path $ProjectRoot 'docs\design\mobile-ux-concepts\procare-downloader-app-icon.png'
Save-ResizedIcon $sourceIcon $preview 512

Write-Host "Generated launcher icons from $sourceIcon"
