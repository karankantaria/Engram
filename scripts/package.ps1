# Builds a self-contained, redistributable engram for Windows x64.
# Produces dist\engram-<version>-win-x64.zip — upload that to a GitHub Release.
#
#   powershell -File scripts\package.ps1
#
# Requires the .NET 8 SDK. The embedding model is fetched if missing.

$ErrorActionPreference = "Stop"
$root  = Split-Path $PSScriptRoot -Parent
$src   = Join-Path $root "src"
$pub   = Join-Path $src "bin\Release\net8.0-windows\win-x64\publish"
$stage = Join-Path $root "dist\engram"

# version from the csproj
$ver = ([xml](Get-Content (Join-Path $src "Engram.csproj"))).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $ver) { $ver = "0.0.0" }
Write-Host "Packaging engram $ver"

# 1. ensure the model is present (publish bundles it next to the exe)
if (-not (Test-Path (Join-Path $src "models\model.onnx"))) {
    & (Join-Path $PSScriptRoot "fetch-model.ps1")
}

# 2. build the single-file, self-contained exe
Push-Location $src
dotnet publish -c Release -p:PublishSingleFile=true --nologo
Pop-Location

# 3. stage: exe (from publish) + model & branding (from source, always correct)
New-Item -ItemType Directory -Force -Path "$stage\models", "$stage\assets" | Out-Null
Copy-Item "$pub\engram.exe" $stage -Force
Copy-Item "$src\models\model.onnx", "$src\models\vocab.txt" "$stage\models\" -Force
Copy-Item "$src\assets\engram.ico", "$src\assets\engram-tray.ico", "$src\assets\logo.png" "$stage\assets\" -Force

@"
engram $ver - a terminal-themed second brain.

Run engram.exe. Requires the Microsoft Edge WebView2 Runtime
(preinstalled on Windows 11; Windows 10:
https://developer.microsoft.com/microsoft-edge/webview2/).

Keep engram.exe, the models\ folder and assets\ folder together.
Your notes live in %APPDATA%\engram.
"@ | Set-Content "$stage\README.txt" -Encoding utf8

# 4. zip (top-level 'engram' folder inside the archive)
$zip = Join-Path $root "dist\engram-$ver-win-x64.zip"
Compress-Archive -Path $stage -DestinationPath $zip -Force
Write-Host ("Done -> {0} ({1:N1} MB)" -f $zip, ((Get-Item $zip).Length / 1MB))
