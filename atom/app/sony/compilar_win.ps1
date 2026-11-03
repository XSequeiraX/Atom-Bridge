# Compila o sony_ctl para Windows contra o Camera Remote SDK da Sony (Visual C++) e instala-o em <Dest> com as DLL do SDK ao lado.
#   powershell -File compilar_win.ps1 -Sdk <pasta do SDK para Windows> -Dest <destino>
# <pasta do SDK>: onde se extraiu o SDK (procura-se la dentro o CameraRemote_SDK.h e o Cr_Core.lib). O SDK nao vem no
# repositorio publico: tem licenca propria. Precisa do Visual Studio 2022 (ou as Build Tools) com o C++ e do Python 3.
# O mesmo do compilar.sh (Linux): so o sistema muda. O fluxo "Sony Windows" do GitHub corre isto.
param(
    [Parameter(Mandatory = $true)][string]$Sdk,
    [Parameter(Mandatory = $true)][string]$Dest
)
$ErrorActionPreference = "Stop"
$Aqui = Split-Path -Parent $MyInvocation.MyCommand.Path

# Os cabecalhos e as bibliotecas do SDK
$hdr = Get-ChildItem -Path $Sdk -Recurse -Filter CameraRemote_SDK.h | Where-Object { $_.FullName -match "app[\\/]CRSDK" } | Select-Object -First 1
if (-not $hdr) { throw "CameraRemote_SDK.h not found under $Sdk" }
$Inc = $hdr.DirectoryName
$Lib = Join-Path (Split-Path -Parent (Split-Path -Parent $Inc)) "external\crsdk"
foreach ($f in @("Cr_Core.lib", "Cr_Core.dll", "CrAdapter\Cr_PTP_IP.dll", "CrAdapter\Cr_PTP_USB.dll")) {
    if (-not (Test-Path (Join-Path $Lib $f))) { throw "$f not found in $Lib" }
}
$Tmp = Join-Path ([System.IO.Path]::GetTempPath()) ("sony_ctl_" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $Tmp | Out-Null

# Os modelos que este SDK conhece (do CrDefines.h)
python (Join-Path $Aqui "gerar_modelos.py") (Join-Path $Inc "CrDefines.h") (Join-Path $Tmp "modelos.inc")
if ($LASTEXITCODE -ne 0) { throw "gerar_modelos.py failed" }

# O Visual C++: o SDK e C++ com a ABI da Microsoft, so compila com o cl.exe. /MT e UNICODE como no exemplo da Sony.
$vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
$vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vs) { throw "Visual Studio with the C++ tools not found" }
$vcvars = Join-Path $vs "VC\Auxiliary\Build\vcvars64.bat"
$exe = Join-Path $Tmp "sony_ctl.exe"
# A linha do cl vai para um .bat: as aspas e as barras do PowerShell para o cmd davam erros dificeis de ver. Compila-se na
# pasta temporaria (o sony_ctl.obj fica la, sem /Fo).
$bat = Join-Path $Tmp "compilar.bat"
@(
    "@echo off",
    "call `"$vcvars`" >nul",
    "if errorlevel 1 exit /b 1",
    "cl /nologo /std:c++17 /EHsc /O2 /MT /W3 /DUNICODE /D_UNICODE /D_CRT_SECURE_NO_WARNINGS /I`"$Inc`" /I`"$Tmp`" `"$Aqui\sony_ctl.cpp`" /Fe`"$exe`" /link /LIBPATH:`"$Lib`" Cr_Core.lib",
    "exit /b %errorlevel%"
) | Set-Content -Path $bat -Encoding ascii
Push-Location $Tmp
try { cmd /c $bat } finally { Pop-Location }
if ($LASTEXITCODE -ne 0 -or -not (Test-Path $exe)) { throw "cl.exe failed ($LASTEXITCODE)" }

# O programa e as DLL do SDK ao lado (como no Linux: sony_ctl, libCr_Core.so, CrAdapter/*.so)
New-Item -ItemType Directory -Force -Path (Join-Path $Dest "CrAdapter") | Out-Null
Copy-Item $exe $Dest -Force
Copy-Item (Join-Path $Lib "Cr_Core.dll") $Dest -Force
Copy-Item (Join-Path $Lib "CrAdapter\*.dll") (Join-Path $Dest "CrAdapter") -Force
# De que codigo saiu este sony_ctl (o ficheiro inteiro, com os blocos do Windows)
$h = python (Join-Path $Aqui "fonte_hash.py") windows (Join-Path $Aqui "sony_ctl.cpp")
Set-Content -Path (Join-Path $Dest "sony_ctl.fonte") -Value "$h  sony_ctl.cpp" -Encoding ascii
Remove-Item -Recurse -Force $Tmp
Write-Host "sony_ctl.exe installed in $Dest (SDK from $Sdk)"
