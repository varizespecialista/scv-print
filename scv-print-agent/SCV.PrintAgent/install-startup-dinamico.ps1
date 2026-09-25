param(
    [switch]$Remove
)

$ErrorActionPreference = "Stop"

# La carpeta donde se encuentra este script sera la carpeta instalada del agente.
$installPath = $PSScriptRoot
$exePath = Join-Path $installPath "SCV.PrintAgent.exe"

$startupFolder = [Environment]::GetFolderPath("Startup")
$shortcutPath = Join-Path $startupFolder "SCV Print Agent.lnk"

if ($Remove) {
    if (Test-Path $shortcutPath) {
        Remove-Item $shortcutPath -Force
        Write-Host "Inicio automatico de SCV Print Agent eliminado." -ForegroundColor Green
    }
    else {
        Write-Host "No existe un acceso directo de inicio automatico." -ForegroundColor Yellow
    }
    exit 0
}

if (-not (Test-Path $exePath)) {
    throw "No se encontro SCV.PrintAgent.exe en: $installPath. Coloque este script en la misma carpeta que SCV.PrintAgent.exe."
}

$wshShell = New-Object -ComObject WScript.Shell
$shortcut = $wshShell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = $exePath
$shortcut.WorkingDirectory = $installPath
$shortcut.Description = "SCV Print Agent - Sistema Clinica VARIZ"
$shortcut.WindowStyle = 7
$shortcut.Save()

Write-Host ""
Write-Host "SCV Print Agent configurado para iniciar automaticamente." -ForegroundColor Green
Write-Host "Carpeta del agente: $installPath" -ForegroundColor Cyan
Write-Host "Ejecutable: $exePath" -ForegroundColor Cyan
Write-Host "Acceso directo: $shortcutPath" -ForegroundColor Cyan
Write-Host ""
Write-Host "Puede mover/copiar esta carpeta a otra PC y ejecutar nuevamente este script." -ForegroundColor Green
