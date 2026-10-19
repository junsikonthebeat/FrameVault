[CmdletBinding()]
param(
    [string]$Version = "1.0.0"
)

$ErrorActionPreference = "Stop"

$projectRoot = $PSScriptRoot
$msbuild = "C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe"
if (-not (Test-Path -LiteralPath $msbuild)) {
    throw "Visual Studio MSBuild를 찾을 수 없습니다: $msbuild"
}

$tempRoot = Join-Path ([IO.Path]::GetTempPath()) ("FrameVault-build-" + [guid]::NewGuid().ToString("N"))
$stageName = "FrameVault-v$Version-win-x64"
$stageRoot = Join-Path $projectRoot "dist\releases\$stageName"
$zipPath = "$stageRoot.zip"

if ((Test-Path -LiteralPath $stageRoot) -or (Test-Path -LiteralPath $zipPath)) {
    throw "같은 버전의 배포 결과가 이미 있습니다: $stageName"
}

function Copy-SourceTree([string]$Source, [string]$Destination) {
    Get-ChildItem -LiteralPath $Source -Recurse -File | Where-Object {
        $_.FullName -notmatch '[\\/](bin|obj)[\\/]'
    } | ForEach-Object {
        $relative = [IO.Path]::GetRelativePath($Source, $_.FullName)
        $target = Join-Path $Destination $relative
        [IO.Directory]::CreateDirectory((Split-Path -Parent $target)) | Out-Null
        Copy-Item -LiteralPath $_.FullName -Destination $target
    }
}

try {
    [IO.Directory]::CreateDirectory($tempRoot) | Out-Null
    Copy-Item -LiteralPath (Join-Path $projectRoot "FrameVault.sln") -Destination $tempRoot
    Copy-SourceTree (Join-Path $projectRoot "src\FrameVault") (Join-Path $tempRoot "src\FrameVault")
    Copy-SourceTree (Join-Path $projectRoot "src\PortableLauncher") (Join-Path $tempRoot "src\PortableLauncher")

    $solution = Join-Path $tempRoot "FrameVault.sln"
    & $msbuild $solution /restore /t:Build /p:Configuration=Release "/p:Platform=Any CPU" `
        /p:RuntimeIdentifier=win-x64 /p:SelfContained=true /p:UseSharedCompilation=false `
        /p:AssemblyName=FrameVault `
        "/p:Version=$Version" /m:1 /verbosity:minimal
    if ($LASTEXITCODE -ne 0) { throw "FrameVault 빌드에 실패했습니다." }

    $launcherProject = Join-Path $tempRoot "src\PortableLauncher\FrameVaultLauncher.vcxproj"
    $launcherOut = Join-Path $tempRoot "launcher"
    & $msbuild $launcherProject /t:Build /p:Configuration=Release /p:Platform=x64 `
        /p:TargetName=FrameVault "/p:OutDir=$launcherOut\" /m:1 /verbosity:minimal
    if ($LASTEXITCODE -ne 0) { throw "포터블 실행기 빌드에 실패했습니다." }

    $appOutput = Join-Path $tempRoot "src\FrameVault\bin\Release\net10.0-windows10.0.19041.0\win-x64"
    foreach ($required in @("FrameVault.exe", "FrameVault.dll", "App.xbf", "MainWindow.xbf", "FrameVault.pri")) {
        if (-not (Test-Path -LiteralPath (Join-Path $appOutput $required))) {
            throw "필수 WinUI 배포 파일이 생성되지 않았습니다: $required"
        }
    }

    [IO.Directory]::CreateDirectory((Join-Path $stageRoot "app")) | Out-Null
    [IO.Directory]::CreateDirectory((Join-Path $stageRoot "docs")) | Out-Null
    Copy-Item -LiteralPath (Join-Path $launcherOut "FrameVault.exe") -Destination (Join-Path $stageRoot "FrameVault.exe")
    Copy-Item -Path (Join-Path $appOutput "*") -Destination (Join-Path $stageRoot "app") -Recurse
    # Unpackaged WinUI resolves the application PRI through the well-known file name.
    Copy-Item -LiteralPath (Join-Path $appOutput "FrameVault.pri") `
        -Destination (Join-Path $stageRoot "app\resources.pri")
    Copy-Item -LiteralPath (Join-Path $projectRoot "README.md") -Destination (Join-Path $stageRoot "README.md")
    Copy-Item -LiteralPath (Join-Path $projectRoot "docs\FrameVault_사용설명서.md") `
        -Destination (Join-Path $stageRoot "docs\FrameVault_사용설명서.md")

    Compress-Archive -LiteralPath $stageRoot -DestinationPath $zipPath -CompressionLevel Optimal
    $hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash
    Write-Host "배포 패키지: $zipPath"
    Write-Host "SHA-256: $hash"
}
finally {
    if (Test-Path -LiteralPath $tempRoot) {
        Remove-Item -LiteralPath $tempRoot -Recurse -Force
    }
}
