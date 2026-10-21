# Se ejecuta en el Windows de GitHub después de compilar (ver .github/workflows/compilar-tauri.yml).
#
# Abre cada .exe solo en una carpeta vacía, con un log de NWN de prueba, y apunta:
#   - cuánto tarda en salir su ventana;
#   - si sigue abierto y sin errores (errores.log);
#   - la memoria que usa de verdad (memoria privada: la que no comparte con otros programas),
#     sumando los procesos de WebView2 que abre la versión nueva.
# A la versión nueva, además, le escribe dos líneas de chat en el log y guarda una captura.
# Termina con error si la versión nueva no arranca.
param(
    [Parameter(Mandatory)] [string] $Exe,
    [Parameter(Mandatory)] [string] $Informe,
    # La versión de C#, para comparar en la misma máquina (opcional).
    [string] $Anterior,
    [string] $Captura = (Join-Path $env:RUNNER_TEMP 'arranque.png')
)
$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName System.Windows.Forms, System.Drawing

$errLog = Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'TraductorNWN\errores.log'

# Un log del juego como el de verdad, para que el traductor lo encuentre y lo siga.
$juego = Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'Neverwinter Nights'
New-Item -ItemType Directory -Force (Join-Path $juego 'logs') | Out-Null
$log = Join-Path $juego 'logs\nwclientLog1.txt'
Set-Content -Path (Join-Path $juego 'settings.tml') -Value "[game.log.chat.all]`nenabled = true`n"
$cp1252 = [System.Text.Encoding]::GetEncoding(1252)

# El proceso y todos los que cuelgan de él (WebView2 abre varios).
function Arbol([int] $raiz) {
    $todos = @(Get-CimInstance Win32_Process)
    $ids = [System.Collections.Generic.List[int]]::new()
    $ids.Add($raiz)
    $añadido = $true
    while ($añadido) {
        $añadido = $false
        foreach ($p in $todos) {
            if ($ids.Contains([int]$p.ParentProcessId) -and -not $ids.Contains([int]$p.ProcessId)) {
                $ids.Add([int]$p.ProcessId); $añadido = $true
            }
        }
    }
    $todos | Where-Object { $ids.Contains([int]$_.ProcessId) }
}

function Probar([string] $nombre, [string] $ruta, [bool] $conChat) {
    $dir = Join-Path $env:RUNNER_TEMP "arranque-$nombre"
    Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force $dir | Out-Null
    $copia = Join-Path $dir 'TraductorNWN.exe'
    Copy-Item $ruta $copia
    Remove-Item $errLog -Force -ErrorAction SilentlyContinue
    Set-Content -Path $log -Value '' -NoNewline

    $r = [ordered]@{ Nombre = $nombre; Vivo = $false; Titulo = ''; Ventana = $null; Privada = 0; Procesos = 0; Fallo = '' }
    $reloj = [System.Diagnostics.Stopwatch]::StartNew()
    $p = Start-Process -FilePath $copia -WorkingDirectory $dir -PassThru
    while ($reloj.Elapsed.TotalSeconds -lt 30) {
        Start-Sleep -Milliseconds 100
        $p.Refresh()
        if ($p.HasExited) { break }
        if ($p.MainWindowTitle) { $r.Ventana = $reloj.Elapsed.TotalSeconds; break }
    }
    if ($r.Ventana) {
        # Que termine de arrancar (y encuentre el log) antes de escribirle.
        Start-Sleep -Seconds 5
        if ($conChat) {
            [System.IO.File]::AppendAllText($log, "[CHAT WINDOW TEXT] [Sun Oct 11 14:35:00] Kael Ironfist: [Party] brb, gonna grab food`r`n", $cp1252)
            [System.IO.File]::AppendAllText($log, "[CHAT WINDOW TEXT] [Sun Oct 11 14:35:02] Lyra Vex: [Tell] *waves* are you joining tonight?`r`n", $cp1252)
        }
        Start-Sleep -Seconds 6
    }
    $p.Refresh()
    $r.Vivo = -not $p.HasExited
    if ($r.Vivo) {
        $r.Titulo = $p.MainWindowTitle
        $arbol = @(Arbol $p.Id)
        $r.Procesos = $arbol.Count
        foreach ($x in $arbol) {
            try { $r.Privada += (Get-Process -Id $x.ProcessId -ErrorAction Stop).PrivateMemorySize64 } catch { }
        }
        if ($conChat) {
            try {
                $b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
                $bmp = [System.Drawing.Bitmap]::new($b.Width, $b.Height)
                $g = [System.Drawing.Graphics]::FromImage($bmp)
                $g.CopyFromScreen($b.Location, [System.Drawing.Point]::Empty, $b.Size)
                $bmp.Save($Captura, [System.Drawing.Imaging.ImageFormat]::Png)
                $g.Dispose(); $bmp.Dispose()
            } catch { }
        }
        foreach ($x in $arbol) { Stop-Process -Id $x.ProcessId -Force -ErrorAction SilentlyContinue }
        $p.WaitForExit(10000) | Out-Null
    }
    if (Test-Path $errLog) { $r.Fallo = (Get-Content $errLog -Raw).Trim() }
    [pscustomobject]$r
}

$lineas = [System.Collections.Generic.List[string]]::new()
$lineas.Add('Prueba automática de TraductorNWN (versión Tauri)')
$lineas.Add("Fecha (UTC): $([DateTime]::UtcNow.ToString('yyyy-MM-dd HH:mm'))   Commit: $env:GITHUB_SHA")
$lineas.Add('')
$lineas.Add('== Archivos ==')
$probar = @(@('nueva', $Exe, $true))
if ($Anterior -and (Test-Path $Anterior)) { $probar += ,@('anterior', $Anterior, $false) }
foreach ($x in $probar) {
    $f = Get-Item $x[1]
    $lineas.Add(("{0,-9} {1,6:N1} MB  SHA256 {2}" -f $x[0], ($f.Length / 1MB), (Get-FileHash $x[1] -Algorithm SHA256).Hash))
}
$lineas.Add('')
$lineas.Add('== Arranque (memoria privada: la que usa de verdad, sin contar la compartida) ==')
$resultados = @{}
foreach ($x in $probar) {
    $r = Probar $x[0] $x[1] $x[2]
    $resultados[$r.Nombre] = $r
    $ok = $r.Vivo -and -not $r.Fallo -and $r.Titulo -eq 'Traductor NWN'
    $estado = if ($ok) { 'ARRANCA' } elseif (-not $r.Vivo) { 'SE CIERRA' } elseif ($r.Fallo) { 'ARRANCA CON ERRORES' } else { "VENTANA INESPERADA («$($r.Titulo)»)" }
    $ventana = if ($r.Ventana) { '{0:N1} s' -f $r.Ventana } else { 'no salió' }
    $lineas.Add(("{0,-9} {1}. Ventana en {2}. Memoria: {3:N0} MB en {4} proceso(s)" -f $r.Nombre, $estado, $ventana, ($r.Privada / 1MB), $r.Procesos))
    if ($r.Fallo) { $lineas.Add("          errores.log: " + ($r.Fallo -replace '\s+', ' ')) }
    $r | Add-Member -NotePropertyName Ok -NotePropertyValue $ok
}

New-Item -ItemType Directory -Force (Split-Path $Informe) | Out-Null
Set-Content -Path $Informe -Value $lineas -Encoding utf8
$lineas | ForEach-Object { Write-Output $_ }
if (-not $resultados['nueva'].Ok) { exit 1 }
exit 0
