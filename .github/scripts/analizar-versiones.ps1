# Analiza con Microsoft Defender TODAS las versiones de dist/ que ha habido en el repositorio,
# con Defender configurado como en un PC de casa: tiempo real, comportamiento, nube con envío
# de muestras a Microsoft y bloqueo a primera vista. A cada .exe se le pone la marca de
# "descargado de internet" (como al bajarlo con el navegador) y se ejecuta unos segundos.
# Sirve para ver con qué versión empezó a saltar el antivirus y con qué nombre.
param([string] $Informe = 'informe-versiones.txt', [int] $Segundos = 10)
$ErrorActionPreference = 'Continue'
$mpcmd = Join-Path $env:ProgramFiles 'Windows Defender\MpCmdRun.exe'
$L = [System.Collections.Generic.List[string]]::new()
function Linea([string] $s) { $L.Add($s); Write-Output $s }

# --- Defender como en un PC de casa ---
try {
    $pref = Get-MpPreference -ErrorAction Stop
    foreach ($x in @($pref.ExclusionPath))      { if ($x) { Remove-MpPreference -ExclusionPath $x } }
    foreach ($x in @($pref.ExclusionProcess))   { if ($x) { Remove-MpPreference -ExclusionProcess $x } }
    foreach ($x in @($pref.ExclusionExtension)) { if ($x) { Remove-MpPreference -ExclusionExtension $x } }
    Set-MpPreference -MAPSReporting Advanced -SubmitSamplesConsent SendAllSamples -DisableBlockAtFirstSeen $false `
        -CloudExtendedTimeout 50 -PUAProtection Enabled -DisableArchiveScanning $false
    Set-MpPreference -DisableIOAVProtection $false -DisableBehaviorMonitoring $false -DisableScriptScanning $false
    Set-MpPreference -DisableRealtimeMonitoring $false
} catch { Linea "Aviso al configurar Defender: $($_.Exception.Message)" }
& $mpcmd -SignatureUpdate | Out-Null
Start-Sleep -Seconds 5
$st = Get-MpComputerStatus
$p2 = Get-MpPreference
Linea "Defender: modo $($st.AMRunningMode), tiempo real $($st.RealTimeProtectionEnabled), comportamiento $($st.BehaviorMonitorEnabled), descargas (IOAV) $($st.IoavProtectionEnabled), firmas $($st.AntivirusSignatureVersion)"
Linea "Nube $($p2.MAPSReporting), envío de muestras $($p2.SubmitSamplesConsent), bloqueo a primera vista desactivado: $($p2.DisableBlockAtFirstSeen)"

$base = Join-Path $env:RUNNER_TEMP 'versiones'
New-Item -ItemType Directory -Force $base | Out-Null

function Detecciones([datetime] $desde, [string] $dir) {
    $ev = Get-WinEvent -FilterHashtable @{ LogName = 'Microsoft-Windows-Windows Defender/Operational'; Id = 1006, 1015, 1116, 1117; StartTime = $desde } -ErrorAction SilentlyContinue |
          Where-Object { $_.Message -like "*$dir*" }
    @($ev | ForEach-Object { if ($_.Message -match '(?m)^\s*Name:\s*(.+?)\s*$') { $Matches[1] } } | Select-Object -Unique)
}

function Probar([string] $nombre, [string] $origen) {
    $dir = Join-Path $base "t-$nombre"
    New-Item -ItemType Directory -Force $dir | Out-Null
    $exe = Join-Path $dir 'TraductorNWN.exe'
    $desde = (Get-Date).AddSeconds(-2)
    try {
        Copy-Item $origen $exe -ErrorAction Stop
        Set-Content -Path $exe -Stream Zone.Identifier -ErrorAction Stop -Value @(
            '[ZoneTransfer]', 'ZoneId=3',
            'ReferrerUrl=https://github.com/froixrd/Traductor-NW/blob/master/dist/TraductorNWN.exe',
            'HostUrl=https://raw.githubusercontent.com/froixrd/Traductor-NW/master/dist/TraductorNWN.exe')
    } catch { }
    Start-Sleep -Seconds 3

    $scan = if (Test-Path $exe) { & $mpcmd -Scan -ScanType 3 -File $exe -DisableRemediation 2>&1 | Out-String } else { '' }
    $codigo = $LASTEXITCODE
    $enScan = @([regex]::Matches($scan, '(?m)^\s*Threat\s*:\s*(.+?)\s*$') | ForEach-Object { $_.Groups[1].Value } | Select-Object -Unique)

    $arranca = 'no probado'
    if (Test-Path $exe) {
        try {
            $psi = [System.Diagnostics.ProcessStartInfo]::new($exe)
            $psi.UseShellExecute = $false
            $psi.WorkingDirectory = $dir
            $proc = [System.Diagnostics.Process]::Start($psi)
            Start-Sleep -Seconds $Segundos
            if ($proc.HasExited) { $arranca = "se cerró (código $($proc.ExitCode))" }
            else { $arranca = 'sí'; $proc.Kill(); $proc.WaitForExit(10000) | Out-Null }
        } catch { $arranca = "bloqueado: $($_.Exception.Message)" }
    }
    Start-Sleep -Seconds 4
    $ev = Detecciones $desde $dir
    $sigue = Test-Path $exe
    $todas = @($enScan + $ev | Select-Object -Unique)
    $veredicto = if ($todas.Count) { 'DETECTADO: ' + ($todas -join ', ') } elseif ($codigo -eq 0) { 'limpio' } else { "análisis con código $codigo" }
    if (-not $sigue) { $veredicto += ' (Defender lo borró)' }
    [pscustomobject]@{ Veredicto = $veredicto; Arranca = $arranca }
}

# --- Control: archivo de prueba EICAR (inofensivo; guardado al revés y en números) ---
$c = 42,72,43,72,36,33,69,76,73,70,45,84,83,69,84,45,83,85,82,73,86,73,84,78,65,45,68,82,65,68,78,65,84,83,45,82,65,67,73,69,36,125,55,41,67,67,55,41,94,80,40,52,53,88,90,80,92,52,91,80,65,64,37,80,33,79,53,88
[array]::Reverse($c)
$ctrlDir = Join-Path $base 'control'
New-Item -ItemType Directory -Force $ctrlDir | Out-Null
$desde = (Get-Date).AddSeconds(-2)
try { [System.IO.File]::WriteAllBytes((Join-Path $ctrlDir 'control.com'), [byte[]]$c) } catch { }
Start-Sleep -Seconds 5
$rt = if (Test-Path (Join-Path $ctrlDir 'control.com')) { 'el tiempo real NO lo borró' } else { 'el tiempo real lo borró al guardarlo' }
Linea "Control EICAR: $rt; detecciones: $((Detecciones $desde $ctrlDir) -join ', ')"
Linea ''

# --- Todas las versiones de dist/ ---
$hist = git log --format='%H|%h|%ad|%s' --date=format:'%m-%d %H:%M' -- dist/TraductorNWN.exe dist/TraductorNWN-ligero.exe
[array]::Reverse($hist)
foreach ($h in $hist) {
    $sha, $corto, $fecha, $asunto = $h -split '\|', 4
    foreach ($ruta in 'dist/TraductorNWN.exe', 'dist/TraductorNWN-ligero.exe') {
        git cat-file -e "${sha}:$ruta" 2>$null
        if ($LASTEXITCODE -ne 0) { continue }
        $etiqueta = if ($ruta -like '*ligero*') { "$corto-ligero" } else { $corto }
        $archivo = Join-Path $base "$etiqueta.exe"
        cmd /c "git show ${sha}:$ruta > `"$archivo`""
        $mb = '{0:N1} MB' -f ((Get-Item $archivo).Length / 1MB)
        $r = Probar $etiqueta $archivo
        Linea ("{0,-16} {1}  {2,8}  arranca: {3,-8} {4}   [{5}]" -f $etiqueta, $fecha, $mb, $r.Arranca, $r.Veredicto, $asunto)
    }
}

Set-Content -Path $Informe -Value $L -Encoding utf8
if ($env:GITHUB_STEP_SUMMARY) { Add-Content $env:GITHUB_STEP_SUMMARY ("``````text`n" + ($L -join "`n") + "`n``````") }
# El informe también como anotación, para poder leerlo desde fuera sin descargar el registro.
$msg = ($L -join "`n").Replace('%', '%25').Replace("`r", '%0D').Replace("`n", '%0A')
Write-Output "::notice title=Defender por versiones::$msg"
exit 0
