<#
.SYNOPSIS
    Builds IFC2RVT and deploys it into the Revit add-in folders.

.DESCRIPTION
    Two builds cover Revit 2022-2026:
      net48          -> compiled against the Revit 2022 API, deployed to 2022/2023/2024
      net8.0-windows -> compiled against the Revit 2025 API, deployed to 2025/2026

    Only versions actually installed on the machine are deployed.

.PARAMETER Versions
    Explicit list of Revit versions to deploy to. Defaults to every supported version found.

.PARAMETER SkipBuild
    Deploy the existing bin output without rebuilding.

.EXAMPLE
    .\install.ps1
    .\install.ps1 -Versions 2026
    .\install.ps1 -Versions 2024,2026 -SkipBuild
#>
[CmdletBinding()]
param(
    [ValidateSet('2022', '2023', '2024', '2025', '2026')]
    [string[]] $Versions,

    [switch] $SkipBuild
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

# Revit version -> which build output feeds it.
$frameworkFor = @{
    '2022' = '2022'; '2023' = '2022'; '2024' = '2022'
    '2025' = '2025'; '2026' = '2025'
}

function Get-InstalledRevitVersions {
    $found = @()
    foreach ($v in $frameworkFor.Keys) {
        if (Test-Path "$env:ProgramFiles\Autodesk\Revit $v\Revit.exe") { $found += $v }
    }
    $found | Sort-Object
}

if (-not $Versions) {
    $Versions = Get-InstalledRevitVersions
    if (-not $Versions) {
        Write-Warning 'Не найдено ни одной установленной версии Revit 2022-2026.'
        Write-Warning 'Укажите версии явно: .\install.ps1 -Versions 2026'
        exit 1
    }
    Write-Host "Найдены установленные версии Revit: $($Versions -join ', ')" -ForegroundColor Cyan
}

# Revit holds its add-in DLLs open. Copying over them mid-session leaves a half-updated folder,
# so refuse up front rather than fail somewhere in the middle of the copy.
$running = Get-Process Revit -ErrorAction SilentlyContinue
if ($running) {
    Write-Warning "Revit запущен (PID $($running.Id -join ', ')). Надстройка заблокирована."
    Write-Warning 'Закройте Revit и повторите: файлы DLL нельзя заменить на лету.'
    exit 2
}

if (-not $SkipBuild) {
    Write-Host 'Сборка...' -ForegroundColor Cyan
    & dotnet build "$root\src\IFC2RVT\IFC2RVT.csproj" -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw "Сборка завершилась с кодом $LASTEXITCODE" }
}

$deployed = 0
foreach ($v in $Versions) {
    $buildFolder = $frameworkFor[$v]
    $source = Join-Path $root "bin\$buildFolder"

    if (-not (Test-Path $source)) {
        Write-Warning "Нет сборки для Revit $v (ожидалась папка $source). Пропуск."
        continue
    }

    $addinRoot = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$v"
    $target = Join-Path $addinRoot 'IFC2RVT'

    New-Item -ItemType Directory -Force -Path $target | Out-Null

    # The DLLs live in a subfolder; the manifest must sit directly in the Addins folder.
    # A stray copy inside the subfolder would risk a second registration of the same AddInId.
    Copy-Item "$source\*" -Destination $target -Recurse -Force
    Remove-Item (Join-Path $target 'IFC2RVT.addin') -Force -ErrorAction SilentlyContinue

    $content = Get-Content (Join-Path $root 'IFC2RVT.addin') -Raw
    $content = $content -replace '<Assembly>.*</Assembly>', "<Assembly>$target\IFC2RVT.dll</Assembly>"
    Set-Content -Path (Join-Path $addinRoot 'IFC2RVT.addin') -Value $content -Encoding UTF8

    Write-Host "  Revit $v  <-  bin\$buildFolder  ->  $target" -ForegroundColor Green
    $deployed++
}

if ($deployed -eq 0) {
    Write-Warning 'Ничего не установлено.'
    exit 1
}

Write-Host ''
Write-Host "Готово: установлено для $deployed версий. Перезапустите Revit." -ForegroundColor Green
Write-Host 'Вкладка ленты: IFC2RVT -> Конвертация -> "IFC → нативные"' -ForegroundColor Green
