# Build NativeWidget (Release) into the folder the Start Menu "Widgets" shortcut launches.
# Usage:  powershell -ExecutionPolicy Bypass -File .\deploy-app.ps1
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$proj = Join-Path $root "NativeWidget\NativeWidget.csproj"
$src  = Join-Path $root "NativeWidget\bin\Release\net8.0-windows10.0.19041.0"
$dest = Join-Path $root "app"
$exe  = Join-Path $dest "NativeWidget.exe"
$lnk  = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\Widgets.lnk"

Write-Host "Stopping running NativeWidget..."
Get-Process -Name "NativeWidget" -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 600

Write-Host "Building Release..."
dotnet build $proj -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw "Build failed" }

New-Item -ItemType Directory -Force -Path $dest | Out-Null
$files = @(
  "NativeWidget.exe", "NativeWidget.dll", "NativeWidget.pdb",
  "NativeWidget.deps.json", "NativeWidget.runtimeconfig.json",
  "icon.ico", "Microsoft.Windows.SDK.NET.dll", "WinRT.Runtime.dll"
)
foreach ($f in $files) {
  $from = Join-Path $src $f
  if (Test-Path $from) {
    Copy-Item $from (Join-Path $dest $f) -Force
  }
}

# Keep Start Menu entry pointed at this deploy folder.
$shell = New-Object -ComObject WScript.Shell
$sc = $shell.CreateShortcut($lnk)
$sc.TargetPath = $exe
$sc.WorkingDirectory = $dest
$sc.IconLocation = "$exe,0"
$sc.Description = "Native Widget (dev app folder)"
$sc.Save()
Write-Host "Start Menu shortcut: $lnk"
Write-Host "  -> $exe"
Write-Host "DLL: $((Get-Item (Join-Path $dest 'NativeWidget.dll')).LastWriteTime)"
Write-Host "Done. Open 'Widgets' from Start Menu (or run app\NativeWidget.exe)."
