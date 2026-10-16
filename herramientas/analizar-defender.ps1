# Se ejecuta en el Windows de GitHub después de compilar (ver .github/workflows/compilar.yml).
#
# Pasa cada .exe por Microsoft Defender configurado como en un PC de casa: protección en tiempo
# real y de comportamiento, nube con bloqueo a primera vista y envío automático de muestras a
# Microsoft. Cada .exe se "descarga" igual que lo hacen Edge y Chrome al terminar una descarga
# (Attachment Services: el antivirus lo analiza y se le pone la marca de "descargado de internet"),
# luego se analiza a mano y se mira el registro de Defender. El resultado se añade al informe
# dist/analisis-antivirus.txt. Nunca hace fallar la compilación: es solo informativo.
param(
    [Parameter(Mandatory)] [string]   $Informe,
    # "nombre=ruta"
    [Parameter(Mandatory)] [string[]] $Exe
)
$ErrorActionPreference = 'Continue'
$lineas = [System.Collections.Generic.List[string]]::new()
$lineas.Add('')
$lineas.Add('== Microsoft Defender (configurado como en un PC de casa) ==')

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

# Los Windows de GitHub traen Defender recortado por directivas (sin tiempo real, sin nube y con
# C:\ y D:\ excluidos). Se quita todo eso para que el análisis sea como en tu PC.
$pol = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows Defender'
if (Test-Path $pol) { Remove-Item $pol -Recurse -Force -ErrorAction SilentlyContinue; New-Item $pol -Force | Out-Null }
$atp = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows Advanced Threat Protection'
if (Test-Path $atp) { Remove-ItemProperty $atp -Name ForceDefenderPassiveMode -ErrorAction SilentlyContinue }
try {
    $pref = Get-MpPreference -ErrorAction Stop
    foreach ($x in @($pref.ExclusionPath))      { if ($x) { Remove-MpPreference -ExclusionPath $x -ErrorAction SilentlyContinue } }
    foreach ($x in @($pref.ExclusionProcess))   { if ($x) { Remove-MpPreference -ExclusionProcess $x -ErrorAction SilentlyContinue } }
    foreach ($x in @($pref.ExclusionExtension)) { if ($x) { Remove-MpPreference -ExclusionExtension $x -ErrorAction SilentlyContinue } }
    Set-MpPreference -MAPSReporting Advanced -SubmitSamplesConsent SendAllSamples -DisableBlockAtFirstSeen $false `
        -CloudExtendedTimeout 50 -PUAProtection Enabled -DisableArchiveScanning $false -ErrorAction SilentlyContinue
} catch {
    $lineas.Add("Defender no responde en este Windows ($($_.Exception.Message)): no se pudo analizar.")
    Escribir; exit 0
}

$antes = (Get-MpComputerStatus).AntivirusSignatureVersion
& $mpcmd -SignatureUpdate | Out-Null
if ($LASTEXITCODE -ne 0) { Update-MpSignature -ErrorAction SilentlyContinue }
Set-MpPreference -DisableRealtimeMonitoring $false -DisableIOAVProtection $false -DisableBehaviorMonitoring $false -ErrorAction SilentlyContinue
Start-Sleep -Seconds 15
$st = Get-MpComputerStatus
$pref = Get-MpPreference
function SiNo($b) { if ($b) { 'sí' } else { 'NO' } }
$lineas.Add("Motor $($st.AMEngineVersion), firmas $($st.AntivirusSignatureVersion) del $($st.AntivirusSignatureLastUpdated.ToString('yyyy-MM-dd HH:mm')) (antes de actualizar: $antes)")
$lineas.Add("Tiempo real: $(SiNo $st.RealTimeProtectionEnabled). Comportamiento: $(SiNo $st.BehaviorMonitorEnabled). Descargas: $(SiNo $st.IoavProtectionEnabled). " +
            "Nube: $(SiNo ($pref.MAPSReporting -ge 1)). Bloqueo a primera vista: $(SiNo (-not $pref.DisableBlockAtFirstSeen)). " +
            "Envío de muestras: $(SiNo ($pref.SubmitSamplesConsent -eq 3)). Exclusiones: $(if (@($pref.ExclusionPath | Where-Object { $_ }).Count) { $pref.ExclusionPath -join ', ' } else { 'ninguna' })")

# "Descargar" un archivo como lo hace el navegador al terminar la descarga.
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Threading;
[ComImport, Guid("73db1241-1e85-4581-8e4f-a81e1d0f8c57"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAttachmentExecute {
    [PreserveSig] int SetClientTitle([MarshalAs(UnmanagedType.LPWStr)] string t);
    [PreserveSig] int SetClientGuid(ref Guid g);
    [PreserveSig] int SetLocalPath([MarshalAs(UnmanagedType.LPWStr)] string p);
    [PreserveSig] int SetFileName([MarshalAs(UnmanagedType.LPWStr)] string n);
    [PreserveSig] int SetSource([MarshalAs(UnmanagedType.LPWStr)] string s);
    [PreserveSig] int SetReferrer([MarshalAs(UnmanagedType.LPWStr)] string r);
    [PreserveSig] int CheckPolicy();
    [PreserveSig] int Prompt(IntPtr hwnd, int prompt, out int action);
    [PreserveSig] int Save();
    [PreserveSig] int Execute(IntPtr hwnd, [MarshalAs(UnmanagedType.LPWStr)] string verb, out IntPtr process);
    [PreserveSig] int SaveWithUI(IntPtr hwnd);
    [PreserveSig] int ClearClientState();
}
[ComImport, Guid("4125dd96-e03a-4103-8f70-e0597d803b9c")] class AttachmentServices { }
public static class Descarga {
    // HRESULT de Save(): 0 = el antivirus lo deja pasar.
    public static int Guardar(string ruta, string origen, string referente) {
        int hr = -1;
        var t = new Thread(() => {
            var ae = (IAttachmentExecute)new AttachmentServices();
            var g = new Guid("8b5a2f4e-6c1d-4f7e-9a3b-2d4c6e8f0a1b");
            ae.SetClientGuid(ref g);
            ae.SetLocalPath(ruta);
            ae.SetSource(origen);
            ae.SetReferrer(referente);
            hr = ae.Save();
            Marshal.ReleaseComObject(ae);
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start(); t.Join();
        return hr;
    }
}
'@

function Detecciones([datetime] $desde, [string] $dir) {
    $ev = Get-WinEvent -FilterHashtable @{ LogName = 'Microsoft-Windows-Windows Defender/Operational'; Id = 1006, 1015, 1116, 1117; StartTime = $desde } -ErrorAction SilentlyContinue |
          Where-Object { $_.Message -like "*$dir*" }
    @($ev | ForEach-Object { if ($_.Message -match '(?m)^\s*Name:\s*(.+?)\s*$') { $Matches[1] } } | Select-Object -Unique)
}

$base = Join-Path $env:RUNNER_TEMP 'analisis'
Remove-Item $base -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $base | Out-Null

# Control: el archivo de prueba estándar EICAR (inofensivo, todos los antivirus lo detectan a propósito).
# Si Defender no lo para, es que aquí no está analizando de verdad y el resto no vale.
# Se guarda al revés y en números para que este script no salte en tu antivirus.
$codigos = 42,72,43,72,36,33,69,76,73,70,45,84,83,69,84,45,83,85,82,73,86,73,84,78,65,45,68,82,65,68,78,65,84,83,45,82,65,67,73,69,36,125,55,41,67,67,55,41,94,80,40,52,53,88,90,80,92,52,91,80,65,64,37,80,33,79,53,88
[array]::Reverse($codigos)
$ctrlDir = Join-Path $base 'control'
New-Item -ItemType Directory -Force $ctrlDir | Out-Null
$control = Join-Path $ctrlDir 'control.com'
$desde = (Get-Date).AddSeconds(-2)
$hr = $null
try {
    [System.IO.File]::WriteAllBytes($control, [byte[]]$codigos)
    $hr = [Descarga]::Guardar($control, 'https://example.com/control.com', 'https://example.com/')
} catch { }
Start-Sleep -Seconds 5
$det = Detecciones $desde $ctrlDir
if ($det.Count -gt 0 -or -not (Test-Path $control)) {
    $lineas.Add("Control (archivo de prueba EICAR): bloqueado al descargarlo como $($det -join ', ') -> Defender analiza bien aquí.")
} else {
    $lineas.Add("Control (archivo de prueba EICAR): NO bloqueado (descarga 0x{0:X8}) -> Defender no está analizando de verdad aquí; lo de abajo no vale." -f $hr)
}
Remove-Item $control -Force -ErrorAction SilentlyContinue

foreach ($par in $Exe) {
    $nombre, $ruta = $par -split '=', 2
    if (-not (Test-Path $ruta)) { continue }
    $dir = Join-Path $base $nombre
    New-Item -ItemType Directory -Force $dir | Out-Null
    $copia = Join-Path $dir 'TraductorNWN.exe'
    $desde = (Get-Date).AddSeconds(-2)
    Copy-Item $ruta $copia
    $hr = [Descarga]::Guardar($copia, 'https://raw.githubusercontent.com/froixrd/Traductor-NW/master/dist/TraductorNWN.exe',
                              'https://github.com/froixrd/Traductor-NW/blob/master/dist/TraductorNWN.exe')
    Start-Sleep -Seconds 3
    $scan = if (Test-Path $copia) { & $mpcmd -Scan -ScanType 3 -File $copia -DisableRemediation 2>&1 | Out-String } else { '' }
    $codigo = $LASTEXITCODE
    Start-Sleep -Seconds 2
    $det = @(@([regex]::Matches($scan, '(?m)^\s*Threat\s*:\s*(.+?)\s*$') | ForEach-Object { $_.Groups[1].Value }) + (Detecciones $desde $dir) | Select-Object -Unique)
    $texto = if ($det.Count) { 'DETECTADO: ' + ($det -join ', ') }
             elseif ($hr -ne 0) { 'descarga bloqueada (0x{0:X8})' -f $hr }
             elseif ($codigo -eq 0) { 'LIMPIO (lo deja descargar y no detecta nada)' }
             else { "no se pudo analizar (código $codigo)" }
    $lineas.Add(("{0,-12} {1}" -f $nombre, $texto))
}

$lineas.Add('')
$lineas.Add('Nota: es Microsoft Defender en un Windows de GitHub. Otros antivirus (Avast, AVG, Norton,')
$lineas.Add('Kaspersky...) pueden opinar distinto. Aparte, SmartScreen ("Windows protegió su PC") y el')
$lineas.Add('navegador avisan de cualquier programa nuevo sin firma digital, aunque esté limpio.')
Escribir
exit 0
