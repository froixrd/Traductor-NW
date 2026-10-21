# Se ejecuta en el Windows de GitHub después de compilar (ver .github/workflows/compilar-tauri.yml).
# Copia el .exe solo en una carpeta vacía, lo abre con un log de NWN de prueba, le escribe dos
# líneas de chat y comprueba que sigue abierto, sin errores y con su ventana. Guarda una captura.
param(
    [Parameter(Mandatory)] [string] $Exe,
    [Parameter(Mandatory)] [string] $Informe,
    [string] $Captura = (Join-Path $env:RUNNER_TEMP 'arranque.png'),
    [int]    $Segundos = 15
)
$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName System.Windows.Forms, System.Drawing

$errLog = Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'TraductorNWN\errores.log'
Remove-Item $errLog -Force -ErrorAction SilentlyContinue

# Un log del juego como el de verdad, para que el traductor lo encuentre y lo siga.
$juego = Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'Neverwinter Nights'
New-Item -ItemType Directory -Force (Join-Path $juego 'logs') | Out-Null
$log = Join-Path $juego 'logs\nwclientLog1.txt'
Set-Content -Path $log -Value '' -NoNewline
Set-Content -Path (Join-Path $juego 'settings.tml') -Value "[game.log.chat.all]`nenabled = true`n"

$dir = Join-Path $env:RUNNER_TEMP 'arranque-tauri'
Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $dir | Out-Null
$copia = Join-Path $dir 'TraductorNWN.exe'
Copy-Item $Exe $copia

$f = Get-Item $Exe
$lineas = [System.Collections.Generic.List[string]]::new()
$lineas.Add('Prueba automática de TraductorNWN (versión Tauri)')
$lineas.Add("Fecha (UTC): $([DateTime]::UtcNow.ToString('yyyy-MM-dd HH:mm'))   Commit: $env:GITHUB_SHA")
$lineas.Add(("Tamaño: {0:N1} MB   SHA256 {1}" -f ($f.Length / 1MB), (Get-FileHash $Exe -Algorithm SHA256).Hash))

$p = Start-Process -FilePath $copia -WorkingDirectory $dir -PassThru
Start-Sleep -Seconds 6
$enc = [System.Text.Encoding]::GetEncoding(1252)
[System.IO.File]::AppendAllText($log, "[CHAT WINDOW TEXT] [Sun Oct 11 14:35:00] Kael Ironfist: [Party] brb, gonna grab food`r`n", $enc)
[System.IO.File]::AppendAllText($log, "[CHAT WINDOW TEXT] [Sun Oct 11 14:35:02] Lyra Vex: [Tell] *waves* are you joining tonight?`r`n", $enc)
Start-Sleep -Seconds ($Segundos - 6)
$p.Refresh()
$vivo = -not $p.HasExited
$titulo = ''
$memoria = 0
if ($vivo) {
    $titulo = $p.MainWindowTitle
    # Memoria de todo el programa: el .exe y los procesos de WebView2 que abre.
    $memoria = $p.WorkingSet64
    Get-CimInstance Win32_Process -Filter "Name='msedgewebview2.exe'" | ForEach-Object {
        $memoria += $_.WorkingSetSize
    }
    try {
        $b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
        $bmp = [System.Drawing.Bitmap]::new($b.Width, $b.Height)
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.CopyFromScreen($b.Location, [System.Drawing.Point]::Empty, $b.Size)
        $bmp.Save($Captura, [System.Drawing.Imaging.ImageFormat]::Png)
        $g.Dispose(); $bmp.Dispose()
    } catch { }
    Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
    $p.WaitForExit(10000) | Out-Null
}
$fallo = if (Test-Path $errLog) { (Get-Content $errLog -Raw).Trim() } else { '' }
$ok = $vivo -and -not $fallo -and $titulo -eq 'Traductor NWN'

$estado = if ($ok) { 'ARRANCA' } elseif (-not $vivo) { "SE CIERRA (código $($p.ExitCode))" } elseif ($fallo) { 'ARRANCA CON ERRORES' } else { "VENTANA INESPERADA («$titulo»)" }
$lineas.Add("Arranque: $estado. Memoria total (con WebView2): {0:N0} MB" -f ($memoria / 1MB))
if ($fallo) { $lineas.Add("errores.log: " + ($fallo -replace '\s+', ' ')) }

New-Item -ItemType Directory -Force (Split-Path $Informe) | Out-Null
Set-Content -Path $Informe -Value $lineas -Encoding utf8
$lineas | ForEach-Object { Write-Output $_ }
if (-not $ok) { exit 1 }
exit 0
