# 构建 FNF 模组启动器
# 依赖：Windows 自带的 .NET Framework 4.x（csc.exe），不需要装 SDK
# 用法：powershell -ExecutionPolicy Bypass -File build.ps1

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
Push-Location $here
try {
    # 1. 找 csc.exe（优先 64 位 Framework）
    $candidates = @(
        "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe",
        "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\csc.exe"
    )
    $csc = $null
    foreach ($c in $candidates) { if (Test-Path $c) { $csc = $c; break } }
    if (-not $csc) { throw "找不到 .NET Framework 4.x 的 csc.exe，请先启用 .NET Framework 4.x。" }
    Write-Host "使用编译器：$csc"

    # 2. 收集源码（tools 里的开发工具不参与编译）
    $src = @(Get-ChildItem -Path (Join-Path $here 'src') -Filter *.cs | ForEach-Object { $_.FullName })
    if ($src.Count -eq 0) { throw "src 目录下没有 .cs 文件。" }
    Write-Host ("源码文件：{0} 个" -f $src.Count)

    New-Item -ItemType Directory -Path (Join-Path $here 'dist') -Force | Out-Null
    $out = Join-Path $here 'dist\FNF启动器.exe'
    $ico = Join-Path $here 'assets\launcher.ico'

    # 3. 编译（/codepage:65001 必须保留：源码是无 BOM 的 UTF-8）
    & $csc /nologo /target:winexe /optimize+ /codepage:65001 "/out:$out" "/win32icon:$ico" /r:System.IO.Compression.FileSystem.dll /r:System.IO.Compression.dll $src
    if ($LASTEXITCODE -ne 0) { throw "编译失败，退出码 $LASTEXITCODE" }

    $f = Get-Item $out
    Write-Host ("构建成功：{0}（{1:N0} 字节）" -f $f.FullName, $f.Length)
}
finally {
    Pop-Location
}