<#
.SYNOPSIS
    Builds the release artefacts: a single-file installer and a source-plus-binaries archive.

.DESCRIPTION
    Produces in dist\:
      IFC2RVT-Setup.exe        installer with the add-in binaries embedded, install + uninstall
      IFC2RVT-<version>.zip    the same binaries loose, plus LICENSE and README

    The archive exists so the binaries can be inspected and installed by hand, which matters for
    a GPL-licensed release: whoever receives the binary is entitled to see exactly what it is.

.PARAMETER Version
    Version stamped on the artefacts. Defaults to 0.1.0.

.EXAMPLE
    .\pack.ps1
    .\pack.ps1 -Version 0.2.0
#>
[CmdletBinding()]
param(
    [string] $Version = '0.1.0'
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$dist = Join-Path $root 'dist'
$payload = Join-Path $root 'tools\Installer\payload.zip'

function Step($text) { Write-Host "==> $text" -ForegroundColor Cyan }

# --- 1. add-in, both Revit families ------------------------------------------------------------

Step 'Сборка надстройки (net48 + net8.0-windows)'
& dotnet build (Join-Path $root 'IFC2RVT.sln') -c Release --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw "Сборка надстройки завершилась с кодом $LASTEXITCODE" }

foreach ($build in @('2022', '2025')) {
    $folder = Join-Path $root "bin\$build"
    if (-not (Test-Path (Join-Path $folder 'IFC2RVT.dll'))) { throw "Нет сборки bin\$build" }
    if (-not (Test-Path (Join-Path $folder 'IFC2RVT.Core.dll'))) { throw "Нет ядра в bin\$build" }
}

# --- 2. payload the installer carries -----------------------------------------------------------

Step 'Упаковка payload'
$staging = Join-Path ([IO.Path]::GetTempPath()) ("IFC2RVT_pack_" + [Guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Force -Path $staging | Out-Null

try {
    foreach ($build in @('2022', '2025')) {
        Copy-Item (Join-Path $root "bin\$build") -Destination (Join-Path $staging $build) -Recurse -Force
    }

    # The licence travels with the binaries. GPLv3 section 4: every copy carries the licence.
    Copy-Item (Join-Path $root 'LICENSE')   -Destination $staging -Force
    Copy-Item (Join-Path $root 'README.md') -Destination $staging -Force

    if (Test-Path $payload) { Remove-Item $payload -Force }
    New-Item -ItemType Directory -Force -Path (Split-Path $payload) | Out-Null
    Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $payload -CompressionLevel Optimal

    $payloadMb = [math]::Round((Get-Item $payload).Length / 1MB, 1)
    Write-Host "    payload.zip — $payloadMb МБ" -ForegroundColor DarkGray

    # --- 3. installer -------------------------------------------------------------------------

    Step 'Сборка установщика'
    & dotnet build (Join-Path $root 'tools\Installer\Installer.csproj') -c Release --nologo -v quiet `
        -p:Version=$Version -p:AssemblyVersion="$Version.0" -p:FileVersion="$Version.0"
    if ($LASTEXITCODE -ne 0) { throw "Сборка установщика завершилась с кодом $LASTEXITCODE" }

    $setup = Join-Path $dist 'IFC2RVT-Setup.exe'
    if (-not (Test-Path $setup)) { throw 'Установщик не собрался' }

    # dotnet build drops its own companions next to the exe; the installer is meant to be one file.
    Get-ChildItem $dist -File |
        Where-Object { $_.Name -ne 'IFC2RVT-Setup.exe' -and $_.Extension -in '.pdb', '.config', '.json' } |
        Remove-Item -Force

    # --- 4. archive ---------------------------------------------------------------------------

    Step 'Сборка архива'
    $archiveStaging = Join-Path $staging '_archive'
    New-Item -ItemType Directory -Force -Path $archiveStaging | Out-Null

    foreach ($build in @('2022', '2025')) {
        Copy-Item (Join-Path $root "bin\$build") -Destination (Join-Path $archiveStaging $build) -Recurse -Force
    }
    Copy-Item $setup                            -Destination $archiveStaging -Force
    Copy-Item (Join-Path $root 'LICENSE')       -Destination $archiveStaging -Force
    Copy-Item (Join-Path $root 'README.md')     -Destination $archiveStaging -Force
    Copy-Item (Join-Path $root 'install.ps1')   -Destination $archiveStaging -Force

    $commit = (& git -C $root rev-parse HEAD 2>$null)
    $notice = @"
IFC2RVT $Version — конвертер IFC в нативные элементы Revit
baidurovlabs.ru | https://github.com/AnT1pal/IFC2RVT

УСТАНОВКА
  IFC2RVT-Setup.exe          — двойной клик, дальше по меню
  IFC2RVT-Setup.exe --install --versions 2026
  IFC2RVT-Setup.exe --uninstall

  Либо вручную: содержимое папки 2022 (Revit 2022-2024) или 2025 (Revit 2025-2026)
  положить в %APPDATA%\Autodesk\Revit\Addins\<версия>\IFC2RVT\ и туда же, уровнем
  выше, манифест IFC2RVT.addin с путём до IFC2RVT.dll.

ЛИЦЕНЗИЯ
  GNU General Public License v3.0. Полный текст — в файле LICENSE.
  Программа распространяется БЕЗ КАКИХ-ЛИБО ГАРАНТИЙ.

  Исходный код этой сборки:
    $SourceUrlPlaceholder
    коммит $commit

  Распространяя эти двоичные файлы дальше, вы обязаны предоставить получателю
  и исходный код на тех же условиях — ссылки выше для этого достаточно.

ЗАВИСИМОСТИ
  xBIM Toolkit (CDDL-1.0) — https://github.com/xBimTeam
"@
    $notice = $notice.Replace('$SourceUrlPlaceholder', 'https://github.com/AnT1pal/IFC2RVT')
    Set-Content -Path (Join-Path $archiveStaging 'ПРОЧТИ МЕНЯ.txt') -Value $notice -Encoding UTF8

    $archive = Join-Path $dist "IFC2RVT-$Version.zip"
    if (Test-Path $archive) { Remove-Item $archive -Force }
    Compress-Archive -Path (Join-Path $archiveStaging '*') -DestinationPath $archive -CompressionLevel Optimal

    # --- 5. checksums -------------------------------------------------------------------------

    Step 'Контрольные суммы'
    $lines = Get-ChildItem $dist -File | Where-Object { $_.Extension -in '.exe', '.zip' } | ForEach-Object {
        $hash = (Get-FileHash $_.FullName -Algorithm SHA256).Hash
        "{0}  {1}" -f $hash, $_.Name
    }
    Set-Content -Path (Join-Path $dist 'SHA256SUMS.txt') -Value $lines -Encoding ASCII

    Write-Host ''
    Write-Host 'Готово:' -ForegroundColor Green
    Get-ChildItem $dist -File | ForEach-Object {
        "    {0,-28} {1,8:N1} МБ" -f $_.Name, ($_.Length / 1MB)
    }
    Write-Host ''
    Write-Host "    коммит: $commit" -ForegroundColor DarkGray
}
finally {
    if (Test-Path $staging) { Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue }
}
