<#
  GameSubTR derleme betiği (Windows). CI/CD de bu betiği kullanır.
  Kullanım (PowerShell, proje klasöründe):
      .\build.ps1                       # test + yayınla → dist\GameSubTR\GameSubTR.exe
      .\build.ps1 -SelfContained        # .NET kurulu olmayan bilgisayarlar için
      .\build.ps1 -Native               # önce hook DLL'lerini MSVC/CMake ile yeniden derle
      .\build.ps1 -NativeOnly           # sadece hook DLL'lerini derle
      .\build.ps1 -Version 1.2.0        # sürüm numarası ver
#>
param(
    [switch]$SelfContained,
    [switch]$Native,
    [switch]$NativeOnly,
    [switch]$SkipTests,
    [string]$Version = "1.0.0"
)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

function Step($m) { Write-Host "`n==> $m" -ForegroundColor Cyan }
function Run($exe, [string[]]$argList) {
    & $exe @argList | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "$exe başarısız (çıkış kodu $LASTEXITCODE)" }
}

if ($Native -or $NativeOnly) {
    Step "Hook DLL'leri derleniyor (CMake + Visual Studio)"
    New-Item -ItemType Directory -Force native\bin | Out-Null
    foreach ($a in @(@{A='x64';B='64'}, @{A='Win32';B='32'})) {
        $dir = "native\build-msvc-$($a.B)"
        Run cmake @('-S', 'native', '-B', $dir, '-A', $a.A)
        Run cmake @('--build', $dir, '--config', 'Release')
        Copy-Item "$dir\Release\GameSubTR.Hook$($a.B).dll", "$dir\Release\GameSubTR.Injector$($a.B).exe" native\bin -Force
    }
    if ($NativeOnly) { Write-Host "Hook DLL'leri hazır: native\bin" -ForegroundColor Green; exit 0 }
}

Step ".NET SDK kontrolü"
$sdks = (& dotnet --list-sdks) 2>$null
if (-not ($sdks -match '^(8|9|10)\.')) {
    Write-Host ".NET 8 SDK bulunamadı. Kurulum: winget install Microsoft.DotNet.SDK.8" -ForegroundColor Red
    exit 1
}

if (-not $SkipTests) {
    Step "Testler"
    Run dotnet @('test', 'tests\GameSubTR.Core.Tests', '-c', 'Release', '--nologo')
    Run dotnet @('test', 'tests\GameSubTR.Patching.Tests', '-c', 'Release', '--nologo')
}

Step "Uygulama yayınlanıyor (v$Version)"
$out = "dist\GameSubTR"
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
$sc = if ($SelfContained) { 'true' } else { 'false' }
Run dotnet @('publish', 'src\GameSubTR.App', '-c', 'Release', '-r', 'win-x64', '--self-contained', $sc, '-o', $out, '--nologo', "-p:Version=$Version")
# Kalıcı Türkçe yama aracı aynı klasöre yayınlanır (ortak DLL'ler paylaşılır).
Run dotnet @('publish', 'src\GameSubTR.Patcher', '-c', 'Release', '-r', 'win-x64', '--self-contained', $sc, '-o', $out, '--nologo', "-p:Version=$Version")

Copy-Item native\bin\* $out -Force
foreach ($f in 'GameSubTR.exe','GameSubTR.Patcher.exe','GameSubTR.Hook64.dll','GameSubTR.Hook32.dll','GameSubTR.Injector64.exe','GameSubTR.Injector32.exe','models\silero_vad.onnx') {
    if (-not (Test-Path (Join-Path $out $f))) { throw "Eksik dosya: $f" }
}

Step "Zip"
$zip = "dist\GameSubTR-win-x64.zip"
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path "$out\*" -DestinationPath $zip
Write-Host "`nHazır: $out\GameSubTR.exe + GameSubTR.Patcher.exe (v$Version)" -ForegroundColor Green
Write-Host "Zip:   $zip" -ForegroundColor Green
