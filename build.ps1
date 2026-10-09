[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

try {
    $dotnetCommand = Get-Command dotnet -CommandType Application -ErrorAction SilentlyContinue
    if ($null -eq $dotnetCommand) {
        throw '未找到 dotnet，请先安装 .NET SDK。'
    }

    $projectPath = Join-Path $PSScriptRoot 'gcviewer.csproj'
    $outputPath = Join-Path $PSScriptRoot 'bin\Release\net8.0-windows'
    $executablePath = Join-Path $outputPath 'gcviewer.exe'

    if (-not (Test-Path -LiteralPath $projectPath -PathType Leaf)) {
        throw "未找到项目文件：$projectPath"
    }

    Write-Host '正在构建 Release 版本...'

    Push-Location -LiteralPath $PSScriptRoot
    try {
        & $dotnetCommand.Source build $projectPath --configuration Release --framework net8.0-windows --output $outputPath --nologo
        $buildExitCode = $LASTEXITCODE
    }
    finally {
        Pop-Location
    }

    if ($buildExitCode -ne 0) {
        [Console]::Error.WriteLine("构建失败，退出码：$buildExitCode")
        exit $buildExitCode
    }

    if (-not (Test-Path -LiteralPath $executablePath -PathType Leaf)) {
        throw "构建结束，但未找到程序：$executablePath"
    }

    Write-Host "构建成功，输出目录：$outputPath"
    Write-Host "程序路径：$executablePath"
    exit 0
}
catch {
    [Console]::Error.WriteLine("构建失败：$($_.Exception.Message)")
    exit 1
}
