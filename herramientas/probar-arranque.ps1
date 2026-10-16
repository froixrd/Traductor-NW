# Se ejecuta en el Windows de GitHub después de compilar (ver .github/workflows/compilar.yml).
#
# Copia cada .exe SOLO en una carpeta vacía, lo abre, espera unos segundos y apunta:
#   - si sigue abierto y sin errores (en errors.log),
#   - el título de su ventana y con qué .NET se ejecuta,
#   - cuántos archivos sacó a la carpeta temporal al arrancar (lo que vigilan los antivirus).
# Guarda una captura de pantalla de cada uno y empieza el informe dist/analisis-antivirus.txt.
#
# Termina con error (y entonces no se guarda el .exe nuevo en dist/) si la versión anterior
# arrancaba bien y alguna de las que se comprueban (-Obligatorios) no.
param(
    [Parameter(Mandatory)] [string]   $Informe,
    # "nombre=ruta"; el primero se toma como referencia (la versión anterior).
    [Parameter(Mandatory)] [string[]] $Exe,
    [string[]] $Obligatorios = @(),
    # Nombres a los que se obliga a usar el .NET más nuevo instalado (DOTNET_ROLL_FORWARD=LatestMajor).
    [string[]] $NetMasNuevo = @(),
    [string]   $Capturas = (Join-Path $env:RUNNER_TEMP 'capturas'),
    [int]      $Segundos = 12
)
$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName System.Windows.Forms, System.Drawing

$appData = Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'TraductorNWN'
$errLog = Join-Path $appData 'errors.log'
New-Item -ItemType Directory -Force $Capturas | Out-Null

$lineas = [System.Collections.Generic.List[string]]::new()
$lineas.Add('Análisis automático de TraductorNWN.exe')
$lineas.Add("Fecha (UTC): $([DateTime]::UtcNow.ToString('yyyy-MM-dd HH:mm'))   Commit: $env:GITHUB_SHA")
$lineas.Add('')
$lineas.Add('== Archivos ==')
foreach ($par in $Exe) {
    $nombre, $ruta = $par -split '=', 2
    if (Test-Path $ruta) {
        $f = Get-Item $ruta
        $hash = (Get-FileHash $ruta -Algorithm SHA256).Hash
        $lineas.Add(("{0,-12} {1,7:N1} MB  SHA256 {2}" -f $nombre, ($f.Length / 1MB), $hash))
    }
}
$lineas.Add('')
$lineas.Add("== Arranque (cada .exe solo en una carpeta vacía, $Segundos s abierto) ==")

$resultado = @{}
foreach ($par in $Exe) {
    $nombre, $ruta = $par -split '=', 2
    if (-not (Test-Path $ruta)) { $lineas.Add("${nombre}: no hay archivo"); continue }

    $dir = Join-Path $env:RUNNER_TEMP "arranque-$nombre"
    $extraer = Join-Path $env:RUNNER_TEMP "extraccion-$nombre"
    Remove-Item $dir, $extraer -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force $dir, $extraer | Out-Null
    $copia = Join-Path $dir 'TraductorNWN.exe'
    Copy-Item $ruta $copia
    Remove-Item $errLog -Force -ErrorAction SilentlyContinue

    # Dónde sacaría un .exe "de un solo archivo" sus DLL al arrancar (por defecto, %TEMP%\.net).
    $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR = $extraer
    if ($NetMasNuevo -contains $nombre) { $env:DOTNET_ROLL_FORWARD = 'LatestMajor' }

    $vivo = $false; $titulo = ''; $net = 'incluido en el .exe'; $netInstalado = $false; $codigo = $null
    # Un .exe pequeño no lleva .NET dentro: tiene que usar el instalado en Windows.
    $necesitaNet = (Get-Item $ruta).Length -lt 20MB
    if ($necesitaNet) { $net = 'no lo encontró' }
    try {
        $p = Start-Process -FilePath $copia -WorkingDirectory $dir -PassThru
        Start-Sleep -Seconds $Segundos
        $p.Refresh()
        $vivo = -not $p.HasExited
        if ($vivo) {
            $titulo = $p.MainWindowTitle
            try {
                foreach ($m in $p.Modules) {
                    if ($m.FileName -match '\\shared\\Microsoft\.NETCore\.App\\([^\\]+)\\') {
                        $net = ".NET $($Matches[1]) instalado en Windows"; $netInstalado = $true; break
                    }
                }
            } catch { }
            try {
                $b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
                $bmp = [System.Drawing.Bitmap]::new($b.Width, $b.Height)
                $g = [System.Drawing.Graphics]::FromImage($bmp)
                $g.CopyFromScreen($b.Location, [System.Drawing.Point]::Empty, $b.Size)
                $bmp.Save((Join-Path $Capturas "$nombre.png"), [System.Drawing.Imaging.ImageFormat]::Png)
                $g.Dispose(); $bmp.Dispose()
            } catch { }
            Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
            $p.WaitForExit(10000) | Out-Null
        } else {
            $codigo = $p.ExitCode
        }
    } catch {
        $titulo = "no se pudo abrir: $($_.Exception.Message)"
    } finally {
        Remove-Item Env:DOTNET_BUNDLE_EXTRACT_BASE_DIR -ErrorAction SilentlyContinue
        Remove-Item Env:DOTNET_ROLL_FORWARD -ErrorAction SilentlyContinue
    }

    $extraidos = @(Get-ChildItem $extraer -Recurse -File -ErrorAction SilentlyContinue)
    $fallo = if (Test-Path $errLog) { (Get-Content $errLog -Raw).Trim() } else { '' }
    $ok = $vivo -and -not $fallo -and (-not $necesitaNet -or $netInstalado)
    $resultado[$nombre] = $ok

    $estado = if ($ok) { 'ARRANCA' }
              elseif (-not $vivo) { "SE CIERRA (código $codigo)" }
              elseif ($fallo) { 'ARRANCA CON ERRORES' }
              else { 'NO ENCUENTRA .NET' }
    $lineas.Add(("{0,-12} {1}. Ventana: «{2}». .NET: {3}. Archivos sacados a la carpeta temporal: {4}" -f
        $nombre, $estado, $titulo, $net, $extraidos.Count))
    foreach ($e in $extraidos | Select-Object -First 8) { $lineas.Add("               - $($e.Name)") }
    if ($fallo) {
        $plano = $fallo -replace '\s+', ' '
        $lineas.Add("               errors.log: " + $plano.Substring(0, [Math]::Min(400, $plano.Length)))
    }
}

New-Item -ItemType Directory -Force (Split-Path $Informe) | Out-Null
Set-Content -Path $Informe -Value $lineas -Encoding utf8
$lineas | ForEach-Object { Write-Output $_ }

$referencia = ($Exe[0] -split '=', 2)[0]
if ($resultado[$referencia]) {
    $malos = @($Obligatorios | Where-Object { -not $resultado[$_] })
    if ($malos.Count -gt 0) {
        Write-Output "La versión anterior arranca y estas no: $($malos -join ', '). No se guardan en dist/."
        exit 1
    }
} else {
    Write-Output "La versión anterior tampoco arrancó aquí (o no había): la prueba de arranque no es concluyente."
}
exit 0
