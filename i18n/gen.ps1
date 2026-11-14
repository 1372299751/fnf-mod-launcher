# Build LangData.cs straight from core.tsv + machine.tsv
# Extra keys are harmless (never matched)
$ErrorActionPreference = 'Stop'
$i18n = 'D:\fnf\launcher\i18n'
$outFile = 'D:\fnf\launcher\LangData.cs'
$utf8 = New-Object System.Text.UTF8Encoding($false)

function LoadMap($path, $trusted) {
    $m = @{}
    if (-not (Test-Path $path)) { return $m }
    foreach ($line in [System.IO.File]::ReadAllLines($path, [System.Text.Encoding]::UTF8)) {
        if ($line -match '^\s*#' -or [string]::IsNullOrWhiteSpace($line)) { continue }
        $p = $line -split "`t"
        if ($p.Count -lt 2) { continue }
        $zh = $p[0]
        if ($p.Count -ge 3 -and ($p[1] -eq 'en' -or $p[1] -eq 'es')) { $en = $p[2] }
        elseif ($p.Count -ge 2) { $en = $p[1] }
        else { continue }
        if ([string]::IsNullOrWhiteSpace($en) -or $en -eq $zh) { continue }
        if ($zh -match '^\s*$') { continue }
        # 下面是"过滤机翻垃圾"的启发式，只对 machine.tsv 生效：
        # 手工表（core.tsv）里出现中文是正常的（例如英文译文里引用「FNF 汉化」），不能被误杀。
        if (-not $trusted) {
            if ($en -match '^[A-Za-z]$') { continue }                 # 光一个 E / Z 这种
            if ($en -match '^[\s\W]+$') { continue }                  # 纯标点
            if ($zh.Length -ge 3 -and $en.Length -le 2) { continue }  # 长原文配一个字母
            if ($en -match '[\u4e00-\u9fa5]') { continue }            # 残留中文的机翻
        }
        $m[$zh] = $en
    }
    return $m
}

$m = @{}
$machine = LoadMap (Join-Path $i18n 'machine.tsv') $false
foreach ($k in $machine.Keys) { $m[$k] = $machine[$k] }
$core = LoadMap (Join-Path $i18n 'core.tsv') $true
foreach ($k in $core.Keys) { $m[$k] = $core[$k] }   # hand-checked entries win

function Unescape($s) { return $s.Replace('\r\n', "`r`n").Replace('\n', "`n").Replace('\t', "`t") }
function Escape($s) {
    return $s.Replace('\', '\\').Replace('"', '\"').Replace("`r", '\r').Replace("`n", '\n').Replace("`t", '\t')
}

# 按长度降序 + 序数排序输出：顺序稳定（换台机器跑出来一样），也方便看 diff
$keys = @($m.Keys | Sort-Object -Property @{Expression = { $_.Length }; Descending = $true }, @{Expression = { $_ } })
$sb = New-Object System.Text.StringBuilder
[void]$sb.AppendLine('// 由 i18n/gen.ps1 生成，请勿手改（改 i18n/core.tsv 或 machine.tsv 后重跑）')
[void]$sb.AppendLine('namespace FnfLauncher')
[void]$sb.AppendLine('{')
[void]$sb.AppendLine('    internal static class LangData')
[void]$sb.AppendLine('    {')
[void]$sb.AppendLine('        public static readonly string[] Key = new string[] {')
for ($i = 0; $i -lt $keys.Count; $i++) {
    $v = Escape (Unescape $keys[$i])
    [void]$sb.Append('            "').Append($v).Append('"')
    if ($i -lt $keys.Count - 1) { [void]$sb.Append(',') }
    [void]$sb.AppendLine()
}
[void]$sb.AppendLine('        };')
[void]$sb.AppendLine('        public static readonly string[] En = new string[] {')
for ($i = 0; $i -lt $keys.Count; $i++) {
    $v = Escape (Unescape $m[$keys[$i]])   # 译文里的 \r\n / \n / \t 也要还原成真字符
    [void]$sb.Append('            "').Append($v).Append('"')
    if ($i -lt $keys.Count - 1) { [void]$sb.Append(',') }
    [void]$sb.AppendLine()
}
[void]$sb.AppendLine('        };')
[void]$sb.AppendLine('    }')
[void]$sb.AppendLine('}')
[System.IO.File]::WriteAllText($outFile, $sb.ToString(), $utf8)
Write-Host ("LangData generated: {0} entries (machine {1} + core {2})" -f $keys.Count, $machine.Count, $core.Count)
