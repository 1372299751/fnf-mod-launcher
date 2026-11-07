# 界面多语言工具：提取文案 / 机翻 / 生成 LangData.cs
#
# 用法（在仓库根目录执行）：
#   powershell -ExecutionPolicy Bypass -File tools/i18n.ps1 -Extract      # 扫描 src 里的中文文案 -> i18n/strings.tsv
#   powershell -ExecutionPolicy Bypass -File tools/i18n.ps1 -Translate    # 用 MyMemory 机翻（带缓存、限速）
#   powershell -ExecutionPolicy Bypass -File tools/i18n.ps1 -Generate     # 合并 i18n/core.tsv + 机翻缓存 -> src/LangData.cs
#
# i18n/core.tsv 是手工校对的表（zh <TAB> en <TAB> tw <TAB> es），优先级高于机翻。
# 缺译文的条目会原样显示中文，不会出现空白。

param(
    [switch]$Extract,
    [switch]$Translate,
    [switch]$Generate,
    [int]$Limit = 0,
    [int]$DelayMs = 260
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$srcDir = Join-Path $root 'src'
$i18nDir = Join-Path $root 'i18n'
if (-not (Test-Path (Join-Path $root 'src'))) { $srcDir = $root }   # src if missing, use root
$stringsTsv = Join-Path $i18nDir 'strings.tsv'
$cacheTsv = Join-Path $i18nDir 'machine.tsv'
$coreTsv = Join-Path $i18nDir 'core.tsv'
$langData = Join-Path $srcDir 'LangData.cs'
$utf8 = New-Object System.Text.UTF8Encoding($false)

function Ensure-Dir($p) { if (-not (Test-Path $p)) { New-Item -ItemType Directory -Path $p -Force | Out-Null } }
Ensure-Dir $i18nDir

function Has-Cjk($s) { return ($s -match '[\u4e00-\u9fa5]') }

# ---------------------------------------------------------------- 提取
if ($Extract) {
    $seen = New-Object System.Collections.Generic.List[string]
    $set = New-Object 'System.Collections.Generic.HashSet[string]'
    foreach ($f in (Get-ChildItem $srcDir -Filter *.cs | Where-Object { $_.Name -ne 'LangData.cs' })) {
        $lines = [System.IO.File]::ReadAllLines($f.FullName, [System.Text.Encoding]::UTF8)
        $ln = 0
        foreach ($line in $lines) {
            $ln++
            $trim = $line.Trim()
            if ($trim.StartsWith('//') -or $trim.StartsWith('///')) { continue }
            # 去掉行尾注释再取字面量，避免把注释里的中文也抓进来
            $code = $line
            if ($code -match '^(.*?)//') { $code = $Matches[1] }
            foreach ($m in [regex]::Matches($code, '"([^"\\]*(?:\\.[^"\\]*)*)"')) {
                $s = $m.Groups[1].Value
                if (-not (Has-Cjk($s))) { continue }
                if ($s.Length -gt 400) { continue }
                if ($set.Add($s)) { $seen.Add($s) }
            }
        }
    }
    $sb = New-Object System.Text.StringBuilder
    [void]$sb.AppendLine("# 自动生成：src 里所有含中文的字符串字面量（去重，按出现顺序）")
    $i = 0
    foreach ($s in $seen) { $i++; [void]$sb.AppendLine(("{0}`t{1}" -f $i, $s)) }
    [System.IO.File]::WriteAllText($stringsTsv, $sb.ToString(), $utf8)
    Write-Host ("提取完成：{0} 条 -> {1}" -f $seen.Count, $stringsTsv)
}

# ---------------------------------------------------------------- 机翻（MyMemory，免费额度有限）
if ($Translate) {
    if (-not (Test-Path $stringsTsv)) { throw "先运行 -Extract" }
    $cache = @{}
    if (Test-Path $cacheTsv) {
        foreach ($line in [System.IO.File]::ReadAllLines($cacheTsv, [System.Text.Encoding]::UTF8)) {
            if ($line -match '^#') { continue }
            $p = $line -split "`t"
            if ($p.Count -ge 4) { $cache[$p[0] + '|' + $p[1]] = $p[2], $p[3] }
        }
    }
    $rows = @()
    foreach ($line in [System.IO.File]::ReadAllLines($stringsTsv, [System.Text.Encoding]::UTF8)) {
        if ($line -match '^#') { continue }
        $p = $line -split "`t", 2
        if ($p.Count -eq 2) { $rows += , @($p[0], $p[1]) }
    }
    Write-Host ("待翻译 {0} 条，已有缓存 {1} 条" -f $rows.Count, $cache.Count)

    $todo = @()
    foreach ($r in $rows) {
        $zh = $r[1]
        if (-not $cache.ContainsKey($zh + '|en') -or -not $cache.ContainsKey($zh + '|es')) { $todo += , $r }
    }
    if ($Limit -gt 0 -and $todo.Count -gt $Limit) { $todo = $todo[0..($Limit - 1)] }
    Write-Host ("本次翻译 {0} 条（每条 2 个语言）" -f $todo.Count)

    $fail = 0
    foreach ($r in $todo) {
        $zh = $r[1]
        foreach ($pair in @(@('en', 'zh-CN|en'))) {
            $lang = $pair[0]
            if ($cache.ContainsKey($zh + '|' + $lang)) { continue }
            $url = 'https://api.mymemory.translated.net/get?q=' + [uri]::EscapeDataString($zh) + '&langpair=' + $pair[1]
            $txt = ''
            try {
                $resp = Invoke-RestMethod -Uri $url -TimeoutSec 15
                $txt = [string]$resp.responseData.translatedText
                if ($txt -match '^MYMEMORY WARNING' -or $txt -match 'QUERY LENGTH LIMIT') { $txt = '' }
            } catch { $fail++ }
            if ($txt.Length -gt 0) { $cache[$zh + '|' + $lang] = @($lang, $txt) }
            Start-Sleep -Milliseconds $DelayMs
        }
        Write-Host ("  已处理：" + $zh) -NoNewline
        Write-Host "`r" -NoNewline
    }

    $sb = New-Object System.Text.StringBuilder
    [void]$sb.AppendLine("# 机翻缓存（zh<TAB>lang<TAB>translation），可手工修改后重新 -Generate")
    foreach ($k in $cache.Keys) {
        $zh, $lang2 = $k -split '\|', 2
        [void]$sb.AppendLine(("{0}`t{1}`t{2}" -f $zh, $lang2, $cache[$k][1]))
    }
    [System.IO.File]::WriteAllText($cacheTsv, $sb.ToString(), $utf8)
    Write-Host ("机翻完成：缓存 {0} 条，失败 {1} 次 -> {2}" -f $cache.Count, $fail, $cacheTsv)
}

# ---------------------------------------------------------------- 生成 LangData.cs
if ($Generate) {
    if (-not (Test-Path $stringsTsv)) { throw "先运行 -Extract" }

    $core = @{}
    if (Test-Path $coreTsv) {
        foreach ($line in [System.IO.File]::ReadAllLines($coreTsv, [System.Text.Encoding]::UTF8)) {
            if ($line -match '^#' -or [string]::IsNullOrWhiteSpace($line)) { continue }
            $p = $line -split "`t"
            if ($p.Count -ge 4) { $core[$p[0]] = @($p[1], $p[2], $p[3]) }
        }
    }
    $machine = @{}
    if (Test-Path $cacheTsv) {
        foreach ($line in [System.IO.File]::ReadAllLines($cacheTsv, [System.Text.Encoding]::UTF8)) {
            if ($line -match '^#' -or [string]::IsNullOrWhiteSpace($line)) { continue }
            $p = $line -split "`t"
            if ($p.Count -ge 3) {
                if (-not $machine.ContainsKey($p[0])) { $machine[$p[0]] = @{} }
                $machine[$p[0]][$p[1]] = $p[2]
            }
        }
    }

    # 繁体：内置一份常用简繁对照（只覆盖界面里出现的字，缺的字保持原样）
    $twMap = @{}
    $twFile = Join-Path $i18nDir 's2t.tsv'
    if (Test-Path $twFile) {
        foreach ($line in [System.IO.File]::ReadAllLines($twFile, [System.Text.Encoding]::UTF8)) {
            if ($line.Length -ge 3 -and $line[1] -eq "`t") { $twMap[[string]$line[0]] = [string]$line[2] }
        }
    }
    function To-Tw($s) {
        if ($twMap.Count -eq 0) { return $s }
        $sb = New-Object System.Text.StringBuilder
        foreach ($ch in $s.ToCharArray()) {
            $k = [string]$ch
            if ($twMap.ContainsKey($k)) { [void]$sb.Append($twMap[$k]) } else { [void]$sb.Append($ch) }
        }
        return $sb.ToString()
    }

    $keys = New-Object System.Collections.Generic.List[string]
    $en = New-Object System.Collections.Generic.List[string]
    $tw = New-Object System.Collections.Generic.List[string]
    $es = New-Object System.Collections.Generic.List[string]

    foreach ($line in [System.IO.File]::ReadAllLines($stringsTsv, [System.Text.Encoding]::UTF8)) {
        if ($line -match '^#' -or [string]::IsNullOrWhiteSpace($line)) { continue }
        $p = $line -split "`t", 2
        if ($p.Count -ne 2) { continue }
        $zhRaw = $p[1]
        $zh = $zhRaw
        $zh = $zh.Replace("\r\n", "`r`n").Replace("\n", "`n").Replace("\t", "`t")
        $e = ''; $t = ''; $s = ''
        if ($core.ContainsKey($zh)) { $e = $core[$zh][0]; $t = $core[$zh][1]; $s = $core[$zh][2] }
        if ($e.Length -eq 0 -and $machine.ContainsKey($zhRaw) -and $machine[$zhRaw].ContainsKey('en')) { $e = $machine[$zh]['en'] }
        if ($s.Length -eq 0 -and $machine.ContainsKey($zhRaw) -and $machine[$zhRaw].ContainsKey('es')) { $s = $machine[$zh]['es'] }
        if ($t.Length -eq 0) { $t = To-Tw $zh }
        if ($e.Length -eq 0 -and $s.Length -eq 0 -and $t -eq $zh) { continue }   # 什么都没翻出来就跳过
        $keys.Add($zh); $en.Add($e); $tw.Add($t); $es.Add($s)
    }

    function Arr($name, $list) {
        $sb = New-Object System.Text.StringBuilder
        [void]$sb.AppendLine("        public static readonly string[] $name = new string[] {")
        for ($i = 0; $i -lt $list.Count; $i++) {
            $v = $list[$i].Replace('\', '\\').Replace('"', '\"').Replace("`r", '\r').Replace("`n", '\n').Replace("`t", '\t')
            [void]$sb.Append('            "').Append($v).Append('"')
            if ($i -lt $list.Count - 1) { [void]$sb.Append(',') }
            [void]$sb.AppendLine()
        }
        [void]$sb.AppendLine("        };")
        return $sb.ToString()
    }

    $out = New-Object System.Text.StringBuilder
    [void]$out.AppendLine("// 由 tools/i18n.ps1 自动生成，请勿手改（改 i18n/core.tsv 或 i18n/machine.tsv 后重新 -Generate）")
    [void]$out.AppendLine("namespace FnfLauncher")
    [void]$out.AppendLine("{")
    [void]$out.AppendLine("    internal static class LangData")
    [void]$out.AppendLine("    {")
    [void]$out.Append((Arr 'Key' $keys))
    [void]$out.Append((Arr 'En' $en))
    [void]$out.Append((Arr 'Tw' $tw))
    [void]$out.Append((Arr 'Es' $es))
    [void]$out.AppendLine("    }")
    [void]$out.AppendLine("}")
    [System.IO.File]::WriteAllText($langData, $out.ToString(), $utf8)
    Write-Host ("生成完成：{0} 条 -> {1}" -f $keys.Count, $langData)
}
