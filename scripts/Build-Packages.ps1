[CmdletBinding()]
param(
    [string] $IsccPath,
    [string] $RepositoryRoot
)

$ErrorActionPreference = 'Stop'
$repoRoot = if ($RepositoryRoot) { [IO.Path]::GetFullPath($RepositoryRoot) } else { Split-Path -Parent $PSScriptRoot }
if (-not (Test-Path -LiteralPath (Join-Path $repoRoot 'Corelight.slnx'))) { throw 'Не найдена папка исходников Corelight.' }

if (-not $IsccPath) {
    $compiler = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($compiler) { $IsccPath = $compiler.Source }
    foreach ($candidate in @("${env:ProgramFiles}\Inno Setup 7\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 7\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe")) {
        if (-not $IsccPath -and (Test-Path -LiteralPath $candidate -PathType Leaf)) { $IsccPath = $candidate }
    }
}
if (-not $IsccPath -or -not (Test-Path -LiteralPath $IsccPath -PathType Leaf)) {
    throw 'Нужен Inno Setup 6.3+ или 7. Установите его с https://jrsoftware.org/isdl.php либо передайте -IsccPath.'
}

Push-Location $repoRoot
try {
    [xml] $project = [IO.File]::ReadAllText((Join-Path $repoRoot 'src\Corelight.App\Corelight.App.csproj'))
    $version = @($project.Project.PropertyGroup.Version | Where-Object { $_ })[0]
    if ($version -notmatch '^\d+\.\d+\.\d+(?:-[A-Za-z0-9.-]+)?$') { throw 'Неподдерживаемая версия проекта.' }
    # Inno Setup ограничивает текстовую версию продукта 50 символами.
    $gitDirectory = Join-Path $repoRoot '.git'
    if (-not (Test-Path -LiteralPath $gitDirectory) -or -not (Get-Command git -ErrorAction SilentlyContinue)) {
        throw 'Для сборки пакетов нужен Git и рабочая копия репозитория Corelight. Из ZIP можно выполнить обычную сборку приложения.'
    }
    $revision = & git rev-parse --short=16 HEAD
    if ($LASTEXITCODE -ne 0 -or $revision -notmatch '^[0-9a-f]+$') { throw 'Не удалось прочитать ревизию Git.' }
    $changes = & git status --porcelain
    if ($LASTEXITCODE -ne 0) { throw 'Не удалось прочитать состояние Git.' }
    $suffix = ''
    if ($changes) {
        $revision += '-dirty'
        $suffix = '-local'
        Write-Warning 'Есть незакоммиченные изменения: пакеты отмечены local, версия в журнале — dirty.'
    }
    if ("$version+$revision".Length -gt 50) { throw 'Версия сборки слишком длинная для метаданных установщика.' }
    $packageRoot = Join-Path $repoRoot "publish\packages\$version$suffix"
    $appDir = Join-Path $packageRoot 'app'
    New-Item -ItemType Directory -Path $appDir -Force | Out-Null
    & dotnet publish src/Corelight.App -c Release -o $appDir -warnaserror "-p:SourceRevisionId=$revision"
    if ($LASTEXITCODE -ne 0) { throw 'Публикация Corelight не выполнена.' }
    $exe = Join-Path $appDir 'Corelight.exe'
    $appVersionInfo = (Get-Item -LiteralPath $exe).VersionInfo
    if ($appVersionInfo.ProductVersion -ne "$version+$revision") { throw 'Версия exe не соответствует сборке.' }
    $stem = "Corelight-$version$suffix"
    $readme = @"
Corelight $version$suffix — Windows 11 x64

Установщик: запустите $stem-Setup-win-x64.exe и следуйте подсказкам.
Переносной вариант: запустите $stem-Portable-win-x64.exe.
.NET включён. Для запуска нужны права администратора.

Сканирование не применяет изменения. Перед применением прочитайте предупреждения.
Удаление программы не отменяет настройки Windows. История и резервные копии
остаются в %ProgramData%\Corelight; для отката установите программу снова.

Инструкция и исходники: https://github.com/Shtarkerr/Corelight
Журналы: %ProgramData%\Corelight\logs
Версия для журнала: $version+$revision
"@
    [IO.File]::WriteAllText((Join-Path $appDir 'README.txt'), $readme, [Text.UTF8Encoding]::new($false))
    $installerArgs = @(
        "/DAppVersion=$version$suffix"
        "/DBinaryVersion=$($appVersionInfo.FileVersion)"
        "/DProductTextVersion=$($appVersionInfo.ProductVersion)"
        "/DSourceDir=$appDir"
        "/DPackageDir=$packageRoot"
        "/DPackageName=$stem-Setup-win-x64"
        (Join-Path $repoRoot 'installer\Corelight.iss')
    )
    & $IsccPath @installerArgs
    if ($LASTEXITCODE -ne 0) { throw 'Сборка установщика не выполнена.' }
    $setup = Join-Path $packageRoot "$stem-Setup-win-x64.exe"
    $setupVersionInfo = (Get-Item -LiteralPath $setup).VersionInfo
    if ($setupVersionInfo.ProductName.TrimEnd() -ne $appVersionInfo.ProductName -or
        $setupVersionInfo.ProductVersion.TrimEnd() -ne $appVersionInfo.ProductVersion) {
        throw 'Метаданные установщика не соответствуют сборке Corelight.'
    }
    $portable = Join-Path $packageRoot "$stem-Portable-win-x64.exe"
    Copy-Item -LiteralPath $exe -Destination $portable -Force
    $releaseFiles = @($setup, $portable)
    $checksums = foreach ($file in $releaseFiles) {
        $hash = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()
        "$hash  $([IO.Path]::GetFileName($file))"
    }
    [IO.File]::WriteAllLines((Join-Path $packageRoot 'SHA256SUMS.txt'), [string[]] $checksums, [Text.UTF8Encoding]::new($false))
    Write-Output $releaseFiles
}
finally { Pop-Location }
