param(
    [string]$CompilerPath = 'C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\Roslyn\csc.exe'
)

$ErrorActionPreference = 'Stop'
$projectRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
if (-not (Test-Path -LiteralPath $CompilerPath -PathType Leaf)) {
    throw "C# compiler not found: $CompilerPath. Pass -CompilerPath with an installed csc.exe."
}

# 构建产物与 Assets 隔离，并在 finally 中清理。
$artifactRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
$artifactDirectory = Join-Path $artifactRoot ('unity-weapon-tests-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $artifactDirectory | Out-Null
$testExecutable = Join-Path $artifactDirectory 'WeaponControlRegression.exe'
$exitStatus = 0
try {
    $sources = @(
        (Join-Path $projectRoot 'Assets\Scripts\WeaponControl.cs'),
        (Join-Path $projectRoot 'Assets\Scripts\NetworkProtocol.cs'),
        (Join-Path $PSScriptRoot 'UnityStubs.cs'),
        (Join-Path $PSScriptRoot 'WeaponControlRegression.cs')
    )
    Write-Host 'Compiling production WeaponControl.cs and NetworkProtocol.cs with boundary stubs...'
    & $CompilerPath /nologo /warn:4 /target:exe /main:WeaponControlRegression "/out:$testExecutable" @sources
    if ($LASTEXITCODE -ne 0) { throw "C# regression compilation failed (exit $LASTEXITCODE)." }
    & $testExecutable
    $exitStatus = $LASTEXITCODE
} finally {
    # 删除前验证实际临时目录，避免误删。
    $resolvedArtifacts = [System.IO.Path]::GetFullPath($artifactDirectory)
    $expectedParent = $artifactRoot.TrimEnd('\')
    if ([System.IO.Path]::GetDirectoryName($resolvedArtifacts) -ne $expectedParent -or
        [System.IO.Path]::GetFileName($resolvedArtifacts) -notmatch '^unity-weapon-tests-[0-9a-f]{32}$') {
        throw "Unsafe temporary cleanup path: $resolvedArtifacts"
    }
    if (Test-Path -LiteralPath $resolvedArtifacts) {
        Remove-Item -LiteralPath $resolvedArtifacts -Recurse -Force
    }
}
exit $exitStatus
