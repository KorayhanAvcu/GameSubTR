<#
  GameSubTR derleme betiği (Windows).
  Kullanım (PowerShell, proje klasöründe):
      .\build.ps1                 # test + yayınla → dist\GameSubTR\GameSubTR.exe
      .\build.ps1 -SelfContained  # .NET kurulu olmayan bilgisayarlar için tek klasör
      .\build.ps1 -Native         # hook DLL'lerini MSVC/CMake ile yeniden derle (isteğe bağlı)
#>
param(
    [switch]$SelfContained,
    [switch]$Native,
    [switch]$SkipTests
)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

function Step($m) { Write-Host "`n==> $m" -ForegroundColor Cyan }

Step ".NET SDK kontrolü"
$sdks = (& dotnet --list-sdks) 2>$null
if (-not ($sdks -match '^(8|9|10)\.')) {
    Write-Host ".NET 8 SDK bulunamadı. Kurulum: winget install Microsoft.DotNet.SDK.8" -ForegroundColor Red
    exit 1
}

if ($Native) {
    Step "Hook DLL'leri derleniyor (CMake + Visual Studio)"
    foreach ($a in @(@{A='x64';B='64'}, @{A='Win32';B='32'})) {
        $dir = "native\build-msvc-$($a.B)"
        cmake -S native -B $dir -A $a.A | Out-Host
        cmake --build $dir --config Release | Out-Host
        New-Item -ItemType Directory -Force native\bin | Out-Null
        Copy-Item "$dir\Release\GameSubTR.Hook$($a.B).dll", "$dir\Release\GameSubTR.Injector$($a.B).exe" native\bin -Force
    }
}

if (-not $SkipTests) {
    Step "Testler"
    dotnet test tests\GameSubTR.Core.Tests -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw "Testler başarısız" }
}

Step "Uygulama yayınlanıyor"
$out = "dist\GameSubTR"
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
$sc = if ($SelfContained) { 'true' } else { 'false' }
dotnet publish src\GameSubTR.App -c Release -r win-x64 --self-contained $sc -o $out --nologo
if ($LASTEXITCODE -ne 0) { throw "Yayınlama başarısız" }

Copy-Item native\bin\* $out -Force
foreach ($f in 'GameSubTR.exe','GameSubTR.Hook64.dll','GameSubTR.Hook32.dll','GameSubTR.Injector64.exe','GameSubTR.Injector32.exe','models\silero_vad.onnx') {
    if (-not (Test-Path (Join-Path $out $f))) { throw "Eksik dosya: $f" }
}

Step "Zip"
$zip = "dist\GameSubTR-win-x64.zip"
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path "$out\*" -DestinationPath $zip
Write-Host "`nHazır: $out\GameSubTR.exe" -ForegroundColor Green
Write-Host "Zip:   $zip" -ForegroundColor Green
