# Renders the Android UI mockups (docs\android\mockups\<name>.html) to docs\android\screens\<name>.png at 2x scale:
# headless Microsoft Edge in a 1000x1000 window, then the centred 390x844 phone frame (844x390 for the landscape
# screens) is cropped out. Pass names to render only some screens: .\build\render-android-mockups.ps1 jogo sistema
# The mockups load Sora/Inter from Google Fonts, so render with network access.
param([Parameter(ValueFromRemainingArguments = $true)][string[]]$Names)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$root = Join-Path (Resolve-Path (Join-Path $PSScriptRoot '..')) 'docs\android'
$edge = @("${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe", "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $edge) { throw 'Microsoft Edge not found' }
$work = Join-Path ([System.IO.Path]::GetTempPath()) 'vitasharp-mockups'
New-Item -ItemType Directory -Force $work | Out-Null
$landscape = @('jogo', 'sistema')
if (-not $Names) { $Names = Get-ChildItem (Join-Path $root 'mockups\*.html') | ForEach-Object { $_.BaseName } }
try {
    foreach ($n in $Names) {
        $raw = Join-Path $work "$n.png"
        if (Test-Path $raw) { Remove-Item $raw }
        $url = 'file:///' + ((Join-Path $root "mockups\$n.html") -replace '\\', '/')
        Start-Process -FilePath $edge -Wait -WindowStyle Hidden -ArgumentList @('--headless=new', '--disable-gpu', '--hide-scrollbars',
            "--user-data-dir=$work\profile", '--force-device-scale-factor=2', '--window-size=1000,1000', '--virtual-time-budget=8000',
            "--screenshot=$raw", $url)
        $src = [System.Drawing.Bitmap]::FromFile($raw)
        try {
            $rect = if ($landscape -contains $n) { New-Object System.Drawing.Rectangle 156, 610, 1688, 780 } else { New-Object System.Drawing.Rectangle 610, 156, 780, 1688 }
            $dst = $src.Clone($rect, [System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
            $dst.Save((Join-Path $root "screens\$n.png"), [System.Drawing.Imaging.ImageFormat]::Png)
            $dst.Dispose()
        } finally { $src.Dispose() }
        "screens\$n.png"
    }
} finally {
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
}
