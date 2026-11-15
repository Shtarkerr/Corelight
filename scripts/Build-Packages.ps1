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
    $revision = (& git rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'Не удалось прочитать ревизию Git.' }
    $changes = & git status --porcelain
    if ($LASTEXITCODE -ne 0) { throw 'Не удалось прочитать состояние Git.' }
    $suffix = ''
    if ($changes) {
        $revision += '-dirty'
        $suffix = '-local'
        Write-Warning 'Есть незакоммиченные изменения: пакеты отмечены local, версия в журнале — dirty.'
    }
    $packageRoot = Join-Path $repoRoot "publish\packages\$version$suffix"
    $appDir = Join-Path $packageRoot 'app'
    New-Item -ItemType Directory -Path $appDir -Force | Out-Null
    & dotnet publish src/Corelight.App -c Release -o $appDir -warnaserror "-p:SourceRevisionId=$revision"
    if ($LASTEXITCODE -ne 0) { throw 'Публикация Corelight не выполнена.' }
    $exe = Join-Path $appDir 'Corelight.exe'
    if ((Get-Item -LiteralPath $exe).VersionInfo.ProductVersion -ne "$version+$revision") { throw 'Версия exe не соответствует сборке.' }
    $stem = "Corelight-$version$suffix"
    $readme = @"
Corelight $version$suffix — Windows 11 x64

Установщик: запустите $stem-Setup-win-x64.exe и следуйте подсказкам.
Переносной вариант: распакуйте ZIP целиком и запустите Corelight.exe.
.NET включён. Для запуска нужны права администратора.

Сканирование не применяет изменения. Перед применением прочитайте предупреждения.
Удаление программы не отменяет настройки Windows. История и резервные копии
остаются в %ProgramData%\Corelight; для отката установите программу снова.

Инструкция и исходники: https://github.com/Shtarkerr/Corelight
Журналы: %ProgramData%\Corelight\logs
Версия для журнала: $version+$revision
"@
    [IO.File]::WriteAllText((Join-Path $appDir 'README.txt'), $readme, [Text.UTF8Encoding]::new($false))
    & $IsccPath "/DAppVersion=$version$suffix" "/DSourceDir=$appDir" "/DPackageDir=$packageRoot" "/DPackageName=$stem-Setup-win-x64" (Join-Path $repoRoot 'installer\Corelight.iss')
    if ($LASTEXITCODE -ne 0) { throw 'Сборка установщика не выполнена.' }
    $setup = Join-Path $packageRoot "$stem-Setup-win-x64.exe"
    $zip = Join-Path $packageRoot "$stem-Portable-win-x64.zip"
    # Перезаписываем только конкретный выходной архив; каталоги и пользовательские данные не удаляем.
    Compress-Archive -LiteralPath @($exe, (Join-Path $appDir 'LICENSE'), (Join-Path $appDir 'THIRD-PARTY-NOTICES.txt'), (Join-Path $appDir 'README.txt')) -DestinationPath $zip -Force
    $checksums = foreach ($file in @($setup, $zip)) {
        $hash = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()
        "$hash  $([IO.Path]::GetFileName($file))"
    }
    [IO.File]::WriteAllLines((Join-Path $packageRoot 'SHA256SUMS.txt'), [string[]] $checksums, [Text.UTF8Encoding]::new($false))
    Write-Output "Готовые пакеты: $packageRoot"
}
finally { Pop-Location }
