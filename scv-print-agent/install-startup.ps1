param(
    [string]$PublishPath = "$PSScriptRoot\publish"
)

$exe = Join-Path $PublishPath "SCV.PrintAgent.exe"
if (-not (Test-Path $exe)) { throw "No se encontro $exe. Publique primero el agente." }

$startup = [Environment]::GetFolderPath("Startup")
$shortcutPath = Join-Path $startup "SCV Print Agent.lnk"
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = $exe
$shortcut.WorkingDirectory = $PublishPath
$shortcut.Save()
Write-Host "SCV Print Agent se iniciara automaticamente con Windows: $shortcutPath"
