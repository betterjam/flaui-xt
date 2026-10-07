<#
.SYNOPSIS
  Instala un monitor virtual 1024x768 para correr tests FlaUI headless (sin tu pantalla).

.DESCRIPTION
  Driver: IddSampleDriver (Indirect Display Driver de Microsoft, open source).
  CORRER COMO ADMINISTRADOR. Hace los chequeos previos, te guia, y deja la maquina lista
  para que Exatouch abra en una pantalla invisible 1024x768.

  IMPORTANTE: requiere habilitar testsigning + reiniciar UNA vez. Si Secure Boot esta activo,
  hay que desactivarlo en BIOS primero (el script te avisa).

.PARAMETER Step
  check   : solo diagnostica (testsigning, secure boot, monitores). Por defecto.
  enable  : habilita testsigning (requiere reinicio despues).
  install : instala el driver (correr DESPUES del reinicio).
#>
[CmdletBinding()]
param([ValidateSet('check','enable','install')] [string]$Step = 'check')

function IsAdmin { ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator) }
if (-not (IsAdmin)) { Write-Host "ESTE SCRIPT NECESITA ADMIN. Abri PowerShell como administrador y volve a correrlo." -ForegroundColor Red; return }

if ($Step -eq 'check') {
    Write-Host "=== DIAGNOSTICO MONITOR VIRTUAL ===" -ForegroundColor Cyan
    $ts = (bcdedit /enum '{current}' | Select-String 'testsigning')
    Write-Host ("testsigning : " + ($(if ($ts) { $ts.Line.Trim() } else { 'OFF (por defecto)' })))
    try {
        $sb = Confirm-SecureBootUEFI
        Write-Host "Secure Boot : $sb"
        if ($sb) { Write-Host "  -> Secure Boot ACTIVO bloquea testsigning. Hay que DESACTIVARLO en BIOS antes de seguir." -ForegroundColor Yellow }
    } catch { Write-Host "Secure Boot : no determinable (BIOS legacy o sin UEFI) - probablemente OK" }
    Write-Host "`nProximos pasos:"
    Write-Host "  1. (si Secure Boot ON) desactivar en BIOS."
    Write-Host "  2. .\install-virtual-monitor.ps1 -Step enable   (habilita testsigning) + REINICIAR"
    Write-Host "  3. descargar IddSampleDriver (ver setup-virtual-display.md) y descomprimir en C:\IddSampleDriver"
    Write-Host "  4. .\install-virtual-monitor.ps1 -Step install"
    return
}

if ($Step -eq 'enable') {
    Write-Host "Habilitando testsigning..." -ForegroundColor Cyan
    bcdedit /set testsigning on
    Write-Host "LISTO. REINICIA la maquina ahora. Despues del reinicio corre: -Step install" -ForegroundColor Green
    return
}

if ($Step -eq 'install') {
    $drv = 'C:\IddSampleDriver'
    if (-not (Test-Path "$drv\IddSampleDriver.inf")) {
        Write-Host "No encontre el driver en $drv. Descargalo (setup-virtual-display.md) y descomprimilo ahi." -ForegroundColor Red
        return
    }
    # config 1024x768
    @("1","1024, 768, 60") | Set-Content "$drv\option.txt" -Encoding ascii
    Write-Host "option.txt -> 1024x768 escrito." -ForegroundColor Green
    # instalar cert + driver
    if (Test-Path "$drv\installCert.bat") { & "$drv\installCert.bat" }
    Write-Host "`nAhora: Device Manager -> Action -> Add legacy hardware -> Display adapters ->" -ForegroundColor Yellow
    Write-Host "  Have Disk -> $drv\IddSampleDriver.inf -> instalar." -ForegroundColor Yellow
    Write-Host "Luego verifica:" -ForegroundColor Yellow
    Write-Host '  [System.Windows.Forms.Screen]::AllScreens'
    Write-Host "Deberias ver un 1024x768 nuevo. Ahi corremos los tests headless."
}
