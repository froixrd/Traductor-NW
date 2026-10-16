# Se ejecuta en el Windows de GitHub después de compilar (ver .github/workflows/compilar.yml).
#
# Analiza cada .exe con Microsoft Defender (el antivirus de Windows) con las firmas del día y la
# protección en la nube activada, como en un PC normal, y añade el resultado al informe
# dist/analisis-antivirus.txt. Nunca hace fallar la compilación: es solo informativo.
param(
    [Parameter(Mandatory)] [string]   $Informe,
    # "nombre=ruta"
    [Parameter(Mandatory)] [string[]] $Exe
)
$ErrorActionPreference = 'Continue'
$lineas = [System.Collections.Generic.List[string]]::new()
$lineas.Add('')
$lineas.Add('== Microsoft Defender ==')

function Escribir {
    Add-Content -Path $Informe -Value $lineas -Encoding utf8
    $lineas | ForEach-Object { Write-Output $_ }
    if ($env:GITHUB_STEP_SUMMARY) {
        Add-Content -Path $env:GITHUB_STEP_SUMMARY -Value ("``````text`n" + ((Get-Content $Informe) -join "`n") + "`n``````") -Encoding utf8
    }
}

$mpcmd = Join-Path $env:ProgramFiles 'Windows Defender\MpCmdRun.exe'
if (-not (Test-Path $mpcmd)) {
    $lineas.Add('Defender no está instalado en este Windows: no se pudo analizar.')
    Escribir; exit 0
}

# Los Windows de GitHub traen Defender recortado: sin nube y con los discos C:\ y D:\ excluidos.
# Se deja como viene en un PC normal (y con detección de aplicaciones no deseadas) para que el
# análisis sirva de algo. No se envía ningún archivo a Microsoft.
try {
    $pref = Get-MpPreference -ErrorAction Stop
    foreach ($x in @($pref.ExclusionPath))      { if ($x) { Remove-MpPreference -ExclusionPath $x -ErrorAction SilentlyContinue } }
    foreach ($x in @($pref.ExclusionProcess))   { if ($x) { Remove-MpPreference -ExclusionProcess $x -ErrorAction SilentlyContinue } }
    foreach ($x in @($pref.ExclusionExtension)) { if ($x) { Remove-MpPreference -ExclusionExtension $x -ErrorAction SilentlyContinue } }
    Set-MpPreference -MAPSReporting Advanced -SubmitSamplesConsent NeverSend -PUAProtection Enabled `
        -DisableArchiveScanning $false -ErrorAction SilentlyContinue
} catch {
    $lineas.Add("Defender no responde en este Windows ($($_.Exception.Message)): no se pudo analizar.")
    Escribir; exit 0
}

$antes = (Get-MpComputerStatus).AntivirusSignatureVersion
& $mpcmd -SignatureUpdate | Out-Null
if ($LASTEXITCODE -ne 0) { Update-MpSignature -ErrorAction SilentlyContinue }
$st = Get-MpComputerStatus
$pref = Get-MpPreference
$lineas.Add("Motor $($st.AMEngineVersion), firmas $($st.AntivirusSignatureVersion) del $($st.AntivirusSignatureLastUpdated.ToString('yyyy-MM-dd HH:mm')) (antes de actualizar: $antes)")
$lineas.Add("Protección en la nube: $(if ($pref.MAPSReporting -ge 1) { 'activada' } else { 'desactivada' }). Exclusiones: $(if (@($pref.ExclusionPath | Where-Object { $_ }).Count) { $pref.ExclusionPath -join ', ' } else { 'ninguna' })")

function Analizar([string] $ruta) {
    $salida = & $mpcmd -Scan -ScanType 3 -File $ruta -DisableRemediation 2>&1 | Out-String
    $codigo = $LASTEXITCODE
    $amenazas = @([regex]::Matches($salida, '(?m)^\s*Threat\s*:\s*(.+?)\s*$') | ForEach-Object { $_.Groups[1].Value } | Select-Object -Unique)
    [pscustomobject]@{ Codigo = $codigo; Amenazas = $amenazas; Salida = $salida.Trim() }
}

$base = Join-Path $env:RUNNER_TEMP 'analisis'
Remove-Item $base -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $base | Out-Null

# Control: el archivo de prueba estándar EICAR (inofensivo, todos los antivirus lo detectan a propósito).
# Si Defender no lo detecta, es que aquí no está analizando de verdad y el resto no vale.
# Se guarda al revés y en números para que este script no salte en tu antivirus.
$codigos = 42,72,43,72,36,33,69,76,73,70,45,84,83,69,84,45,83,85,82,73,86,73,84,78,65,45,68,82,65,68,78,65,84,83,45,82,65,67,73,69,36,125,55,41,67,67,55,41,94,80,40,52,53,88,90,80,92,52,91,80,65,64,37,80,33,79,53,88
[array]::Reverse($codigos)
$control = Join-Path $base 'control.com'
[System.IO.File]::WriteAllBytes($control, [byte[]]$codigos)
$r = Analizar $control
$controlOk = $r.Codigo -eq 2 -and $r.Amenazas.Count -gt 0
if ($controlOk) {
    $lineas.Add("Control (archivo de prueba EICAR): detectado como $($r.Amenazas -join ', ') -> Defender analiza bien aquí.")
} else {
    $lineas.Add("Control (archivo de prueba EICAR): NO detectado (código $($r.Codigo)) -> Defender no está analizando de verdad aquí; lo de abajo no vale.")
    $lineas.Add(($r.Salida -split "`r?`n" | Select-Object -Last 5) -join ' | ')
}
Remove-Item $control -Force -ErrorAction SilentlyContinue

foreach ($par in $Exe) {
    $nombre, $ruta = $par -split '=', 2
    if (-not (Test-Path $ruta)) { continue }
    $dir = Join-Path $base $nombre
    New-Item -ItemType Directory -Force $dir | Out-Null
    $copia = Join-Path $dir 'TraductorNWN.exe'
    Copy-Item $ruta $copia
    $r = Analizar $copia
    $texto = switch ($r.Codigo) {
        0       { 'LIMPIO (no detecta nada)' }
        2       { 'DETECTADO: ' + ($r.Amenazas -join ', ') }
        default { "no se pudo analizar (código $($r.Codigo)): " + (($r.Salida -split "`r?`n" | Select-Object -Last 3) -join ' | ') }
    }
    $lineas.Add(("{0,-12} {1}" -f $nombre, $texto))
}

$lineas.Add('')
$lineas.Add('Nota: es Microsoft Defender en un Windows de GitHub. Otros antivirus (Avast, AVG, Norton,')
$lineas.Add('Kaspersky...) pueden opinar distinto, y SmartScreen avisa de todo programa nuevo sin firma.')
Escribir
exit 0
