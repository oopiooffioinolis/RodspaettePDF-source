# Copies the built app to %LOCALAPPDATA%\Programs\Rødspætte PDF, adds a Start menu shortcut and starts it.
# No admin rights needed. Run build.cmd instead of calling this directly.
$ErrorActionPreference = 'Stop'
$src = Join-Path $PSScriptRoot 'publish'
$dest = Join-Path $env:LOCALAPPDATA 'Programs\Rødspætte PDF'

# close a running copy so its files can be replaced
Get-Process RodspaettePDF -ErrorAction SilentlyContinue | ForEach-Object { $_.CloseMainWindow() | Out-Null; Start-Sleep -Milliseconds 800; if (!$_.HasExited) { $_.Kill() } }

New-Item -ItemType Directory -Force -Path $dest | Out-Null
Copy-Item -Path (Join-Path $src '*') -Destination $dest -Recurse -Force
# lets the app's "Update app" find this source folder
Set-Content -LiteralPath (Join-Path $dest 'source-path.txt') -Value $PSScriptRoot -Encoding UTF8

$exe = Join-Path $dest 'RodspaettePDF.exe'
$startMenu = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'
$shell = New-Object -ComObject WScript.Shell
$lnk = $shell.CreateShortcut((Join-Path $startMenu 'Rødspætte PDF.lnk'))
$lnk.TargetPath = $exe
$lnk.WorkingDirectory = $dest
$lnk.IconLocation = "$exe,0"
$lnk.Description = 'Rødspætte PDF'
$lnk.Save()

Start-Process $exe
