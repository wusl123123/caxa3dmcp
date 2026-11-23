param(
    [string]$WorkRoot = ''
)

# ============================================================
# caxa3dmcp 一键卸载脚本（模板）
#   1. 删除 <CAXA>\<年份>\bin\caxa3dmcp.dll / .pdb
#   2. 从 IronCad.Addin.config 手术式移除本插件 <AddIn> 条目
#   3. 从 IronCad.exe.manifest 手术式移除本插件 <dependency>
#   4. 删除 HKCU Applications 键
# 铁律：绝不整体还原 *.bak —— config/manifest 是所有插件共享文件，
#       整体还原会抹掉备份之后登记的其他插件（已有 cudeaddin 被冲掉的实例）。
# 可重复执行。
# 本文件必须存成「单一 UTF-8 BOM」。
# ============================================================

$ScriptVersion = '2026-11-12.1719'
$ErrorActionPreference = 'Stop'

# ---- v003.056 日志重置铁律（规则第 19 节 15 条）：每轮脚本执行都从干净日志起点开始 ----
# 清两个输出日志；_crx.marker 是探针不清；CAXA 运行中文件被占用时静默跳过（插件加载时仍会截断重建）。
$__logs = @((Join-Path $env:TEMP 'caxa3dmcp_trace.log'), (Join-Path $env:TEMP 'caxa3dmcp_crx.log'))
foreach ($__f in $__logs) { if (Test-Path -LiteralPath $__f) { Remove-Item -LiteralPath $__f -Force -ErrorAction SilentlyContinue } }
$scriptDir = if ($WorkRoot) { $WorkRoot } else { $PSScriptRoot }
if (-not $scriptDir) { $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path }

# ---------- 自动提权 ----------
$identity  = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    $elevDir = Join-Path $env:ProgramData 'caxa3dmcp'
    New-Item -ItemType Directory -Path $elevDir -Force | Out-Null
    $elevScript = Join-Path $elevDir ('restore-' + $ScriptVersion + '.ps1')
    Copy-Item $PSCommandPath $elevScript -Force

    Write-Host "Restarting as administrator: $elevScript" -ForegroundColor Yellow
    $argLine = "-NoProfile -ExecutionPolicy Bypass -File `"$elevScript`" -WorkRoot `"$scriptDir`""
    Start-Process powershell.exe -Verb RunAs -ArgumentList $argLine
    exit
}

Write-Host "================ caxa3dmcp 卸载 ================"
try { Start-Transcript -Path (Join-Path $env:TEMP 'caxa3dmcp_uninstall.log') -Append | Out-Null } catch { }

# ---------- 常量（与 Addin.cs 的 [Guid] 必须一致） ----------
$addinName = 'caxa3dmcp'
$clsid     = '{5B82F332-9F34-456D-ACB6-8CC02F616340}'

# ---------- 第五登记点卸载（顺序铁律：先摘登记并回读，之后才删 .crx）----------
$crxName      = 'caxa3dmcp_bridge.crx'
$cmdIndexName = 'draft_commands.txt'
$qrRuleName   = 'caxa3dmcp二维码定位规则.txt'   # 本插件独占 ⇒ 卸载时删
$qrCoderName  = 'QRCoder.dll'                   # 通用第三方库、<bin> 内多插件共用 ⇒ 卸载时**不删**（26-11-15 已在 CAXA 2027 bin 见到 caxa3dBatchMaster 装的同名文件）
$warmupExb    = 'caxa3d图纸环境预热.exb'   # 全仓唯一基准、多插件共用 ⇒ 卸载时**不删**（不破坏别人的插件）

function Test-Utf8Bom([string]$path) {
    $b = [IO.File]::ReadAllBytes($path)
    return ($b.Length -ge 3 -and $b[0] -eq 0xEF -and $b[1] -eq 0xBB -and $b[2] -eq 0xBF)
}

function Write-TextKeepBom([string]$path, [string]$text, [bool]$hadBom) {
    $enc = New-Object System.Text.UTF8Encoding($hadBom)
    [IO.File]::WriteAllText($path, $text, $enc)
}

function Unregister-CrxBridge([string]$caxaBin) {
    $autorun = Join-Path (Join-Path $caxaBin 'CAXADraft') (Join-Path 'Modules' 'CxAutorun.cx')
    $crxDst  = Join-Path (Join-Path $caxaBin 'CAXADraft') (Join-Path 'bin' $crxName)

    if (-not (Test-Path $autorun)) {
        Write-Host "[5/5] 没有 $autorun（无登记可摘）"
    } else {
        $hadBom   = Test-Utf8Bom $autorun
        $original = [IO.File]::ReadAllText($autorun)
        $rxOwn    = [regex]'(?m)^[ \t]*<File[^>]*>[^<]*\\caxa3dmcp_bridge\.crx</File>[ \t]*\r?\n'
        $want     = $rxOwn.Replace($original, '')
        if ($want -eq $original) {
            Write-Host "[5/5] CxAutorun.cx 里没有本桥登记（跳过）"
        } else {
            if ($want.Length -ge $original.Length) { Write-Host "[5/5] 摘除结果异常（没有变短），放弃写入。" -ForegroundColor Red; return $false }
            if (([regex]::Matches($want, '</Modules>')).Count -ne 1) { Write-Host "[5/5] 摘除后 </Modules> 不唯一，放弃写入。" -ForegroundColor Red; return $false }
            if ($want -like "*$crxName*") { Write-Host "[5/5] 摘除后仍残留本桥 crx 字样，放弃写入。" -ForegroundColor Red; return $false }
            try { [xml]$check = $want }
            catch { Write-Host "[5/5] 摘除后不是合法 XML，放弃写入：$($_.Exception.Message)" -ForegroundColor Red; return $false }
            try {
                Write-TextKeepBom $autorun $want $hadBom
                $back = [IO.File]::ReadAllText($autorun)
                if ($back -ne $want) { throw '回读与写入内容不一致' }
                if ((Test-Utf8Bom $autorun) -ne $hadBom) { throw '回读 BOM 状态与原文件不同' }
                Write-Host "[5/5] CxAutorun.cx 本桥登记已摘除并回读校验通过"
            } catch {
                Write-Host "[5/5] 摘除失败（$($_.Exception.Message)），回滚原文；.crx 文件保留不删。" -ForegroundColor Red
                try { Write-TextKeepBom $autorun $original $hadBom } catch { Write-Host "[5/5] 回滚也失败，需手工修复：$autorun" -ForegroundColor Red }
                return $false
            }
        }
    }

    # 登记确认摘干净，才允许删 .crx 与本桥独占的命令索引 / 二维码定位规则；删不掉如实报告
    # QRCoder.dll 与预热 exb 都是多插件共用资源，卸载一律保留（删了会破坏 caxa3dBatchMaster 等插件）。
    $ok = $true
    foreach ($f in @($crxDst, (Join-Path $caxaBin $cmdIndexName), (Join-Path $caxaBin $qrRuleName))) {
        if (Test-Path $f) {
            try { Remove-Item $f -Force -ErrorAction Stop; Write-Host "[5/5] deleted $f" }
            catch { Write-Host "[5/5] [locked] $f 删不掉（CAXA/图纸进程正在运行？）：$($_.Exception.Message)" -ForegroundColor Yellow; $ok = $false }
        } else { Write-Host "[5/5] not present (skip) $f" }
    }
    Write-Host "[5/5] 共享资源 $qrCoderName / $warmupExb 保留不删（多插件共用，删了会破坏其它插件）"
    return $ok
}
# ---------- 安装根发现（与 deploy.ps1 同一口径，绝不假设在 C 盘；坑 163） ----------
# 固定盘必须用 [IO.DriveInfo]：PS 5.1 的 Get-PSDrive 没有可用的 DriveType 属性（恒空）。
function Get-CaxaRootCandidates {
    $out = @()
    if ($env:CAXA3D_ROOT) { $out += ([string]$env:CAXA3D_ROOT).TrimEnd('\') }
    foreach ($d in [System.IO.DriveInfo]::GetDrives()) {
        if ($d.DriveType -ne 'Fixed') { continue }
        if (-not $d.IsReady) { continue }
        foreach ($sub in @('Program Files\CAXA\CAXA 3D', 'Program Files (x86)\CAXA\CAXA 3D', 'CAXA\CAXA 3D', 'CAXA')) {
            $out += (Join-Path $d.Name $sub)   # .NET Framework 的 DriveInfo 没有 Root 属性，盘符在 Name（已带结尾反斜杠）
        }
    }
    return $out
}

function Resolve-CaxaRoot {
    foreach ($c in @(Get-CaxaRootCandidates)) {
        if ([string]::IsNullOrWhiteSpace($c)) { continue }
        $p = $c.TrimEnd('\')
        if ($p -match '(?i)\\bin$') { $p = Split-Path -Parent $p }
        if ($p -and (Split-Path -Leaf $p) -match '^\d{4}$') { $p = Split-Path -Parent $p }
        if (-not $p -or -not (Test-Path -LiteralPath $p)) { continue }
        $hit = @(Get-ChildItem -LiteralPath $p -Directory -ErrorAction SilentlyContinue |
                 Where-Object { $_.Name -match '^\d{4}$' -and
                                (Test-Path (Join-Path $_.FullName 'bin\IronCad.exe')) })
        if ($hit.Count -gt 0) { return $p }
    }
    return $null
}

$caxaRoot = Resolve-CaxaRoot

function Write-TextWithBom([string]$path, [string]$text) {
    [IO.File]::WriteAllText($path, $text, (New-Object System.Text.UTF8Encoding($true)))
}

function Remove-AddinEntries([string]$configFile) {
    $removed = 0
    try {
        [xml]$doc = Get-Content $configFile -Raw -Encoding UTF8
        foreach ($n in @($doc.SelectNodes("//*[local-name()='AddIn']"))) {
            $inproc = $n.SelectSingleNode("*[local-name()='inprocserver']")
            $site   = $n.SelectSingleNode("*[local-name()='siteclsid']")
            $hit = $false
            if ($inproc -and [string]::Equals($inproc.InnerText.Trim(), "$addinName.dll", [StringComparison]::OrdinalIgnoreCase)) { $hit = $true }
            if (-not $hit -and $site -and [string]::Equals($site.InnerText.Trim(), $clsid, [StringComparison]::OrdinalIgnoreCase)) { $hit = $true }
            if ($hit) {
                [void]$n.ParentNode.RemoveChild($n)
                $removed++
            }
        }
        if ($removed -gt 0) { $doc.Save($configFile) }
    } catch {
        Write-Host "      warning: config XML parse failed -> $($_.Exception.Message)" -ForegroundColor Yellow
    }
    return $removed
}

function Remove-BlocksByRegex([string]$file, [string]$tagName, [string[]]$needles) {
    if (-not (Test-Path $file)) { return 0 }
    $text = [IO.File]::ReadAllText($file)
    $rx = [regex]('(?is)<' + $tagName + '\b[^>]*>.*?</' + $tagName + '>')
    $sb = New-Object System.Text.StringBuilder
    $pos = 0
    $removed = 0
    foreach ($m in $rx.Matches($text)) {
        [void]$sb.Append($text.Substring($pos, $m.Index - $pos))
        $keep = $true
        foreach ($needle in $needles) {
            if ($m.Value -like "*$needle*") { $keep = $false; break }
        }
        if ($keep) { [void]$sb.Append($m.Value) } else { $removed++ }
        $pos = $m.Index + $m.Length
    }
    [void]$sb.Append($text.Substring($pos))
    if ($removed -gt 0) {
        Write-TextWithBom $file $sb.ToString()
        # 保存后必须重新解析，损坏立即回滚
        try { [void][xml](Get-Content $file -Raw -Encoding UTF8) }
        catch {
            Write-Host "      [FATAL] $file damaged after edit, restoring from .bak" -ForegroundColor Red
            if (Test-Path "$file.bak") { Copy-Item "$file.bak" $file -Force }
            throw
        }
    }
    return $removed
}

function Remove-ManifestDependencies([string]$manifestFile) {
    $removed = 0
    try {
        [xml]$doc = Get-Content $manifestFile -Raw -Encoding UTF8
        foreach ($n in @($doc.SelectNodes("//*[local-name()='dependency']"))) {
            $id = $n.SelectSingleNode(".//*[local-name()='assemblyIdentity']")
            if ($id -and [string]::Equals($id.GetAttribute('name'), $addinName, [StringComparison]::OrdinalIgnoreCase)) {
                [void]$n.ParentNode.RemoveChild($n)
                $removed++
            }
        }
        if ($removed -gt 0) { $doc.Save($manifestFile) }
    } catch {
        Write-Host "      warning: manifest XML parse failed -> $($_.Exception.Message)" -ForegroundColor Yellow
    }
    return $removed
}

function Get-VerTag([string]$year, [string]$caxaBin) {
    try {
        $exe = Join-Path $caxaBin 'IronCad.exe'
        if (Test-Path $exe) {
            $pv = (Get-Item $exe).VersionInfo.ProductVersion
            if ($pv -match '^(\d+)\.(\d+)') { return ('{0}.{1}' -f $matches[1], $matches[2]) }
        }
    } catch { }
    if ($year -match '^\d{4}$') { return ('{0}.0' -f ([int]$year - 1998)) }
    return $null
}

# ---------- 发现已安装版本（$caxaRoot 已由 Resolve-CaxaRoot 在每台固定盘上定位） ----------
# 只认「4 位年份目录 + bin\IronCad.exe」，排除 CAXA 3D Mechanical 等非本产品目录
$versions = @()
if ($caxaRoot) {
    $versions = @(Get-ChildItem -LiteralPath $caxaRoot -Directory -ErrorAction SilentlyContinue |
                  Where-Object { $_.Name -match '^\d{4}$' -and
                                 (Test-Path (Join-Path $_.FullName 'bin')) -and
                                 (Test-Path (Join-Path $_.FullName 'bin\IronCad.exe')) })
}
if ($versions.Count -eq 0) {
    Write-Host "[ERROR] no CAXA 3D version folder found on this machine." -ForegroundColor Red
    Write-Host "        Probed CAXA3D_ROOT (if set) then <every fixed drive>:\Program Files\CAXA\CAXA 3D" -ForegroundColor Red
    Write-Host "        and \Program Files (x86)\..., \CAXA\CAXA 3D, \CAXA." -ForegroundColor Red
    Write-Host "        If installed elsewhere, set env CAXA3D_ROOT to the install root / year folder / its bin." -ForegroundColor Yellow
    Read-Host 'Press Enter to exit'
    exit 1
}
Write-Host ("CAXA 3D root  : {0}" -f $caxaRoot) -ForegroundColor Cyan
Write-Host ("Versions      : {0}" -f (($versions | ForEach-Object { $_.Name }) -join ', '))

$hadError = $false

foreach ($v in $versions) {
    $caxaBin      = Join-Path $v.FullName 'bin'
    $configFile   = Join-Path $v.FullName 'Config\IronCad.Addin.config'
    $manifestFile = Join-Path $caxaBin 'IronCad.exe.manifest'
    $verTag       = Get-VerTag $v.Name $caxaBin
    if (-not $verTag) { continue }

    Write-Host "================ CAXA 3D $($v.Name) ================"

    # ---------- 1. 删文件（被锁只提示，不中断流程） ----------
    foreach ($f in @((Join-Path $caxaBin "$addinName.dll"), (Join-Path $caxaBin "$addinName.pdb"))) {
        if (Test-Path $f) {
            try { Remove-Item $f -Force -ErrorAction Stop; Write-Host "[1/4] deleted $f" }
            catch { Write-Host "[1/4] [locked] $f : $($_.Exception.Message) (exit CAXA and retry)" -ForegroundColor Yellow; $hadError = $true }
        } else {
            Write-Host "[1/4] not present (skip) $f"
        }
    }

    # ---------- 2/3. 手术式移除共享文件里的自己的条目 ----------
    if (Test-Path $configFile) {
        $n1 = Remove-AddinEntries $configFile
        $n1 += Remove-BlocksByRegex $configFile 'AddIn' @("$addinName.dll", $clsid, $addinName)
        Write-Host "[2/4] IronCad.Addin.config entries removed: $n1"
    } else {
        Write-Host "[2/4] config missing (skip) $configFile" -ForegroundColor Yellow
    }

    if (Test-Path $manifestFile) {
        $n2 = Remove-ManifestDependencies $manifestFile
        $n2 += Remove-BlocksByRegex $manifestFile 'dependency' @($addinName)
        Write-Host "[3/4] IronCad.exe.manifest dependencies removed: $n2"
    } else {
        Write-Host "[3/4] manifest missing (skip) $manifestFile" -ForegroundColor Yellow
    }

    # ---------- 4. 删注册表键（只删自己的） ----------
    $regPath = "HKCU:\SOFTWARE\CAXA\CAXA 3D $verTag\Applications\$addinName"
    if (Test-Path $regPath) {
        try { Remove-Item $regPath -Recurse -Force -ErrorAction Stop; Write-Host "[4/4] registry key deleted: $regPath" }
        catch { Write-Host "[4/4] registry delete failed: $($_.Exception.Message)" -ForegroundColor Yellow; $hadError = $true }
    } else {
        Write-Host "[4/4] registry key not present (skip)"
    }

    # ---------- 5. 第五登记点：先摘 CxAutorun.cx 那一行，再删 .crx ----------
    if (-not (Unregister-CrxBridge $caxaBin)) { $hadError = $true }
}

# ---------- 残留校验 ----------
Write-Host "================ residual check ================"
foreach ($v in $versions) {
    $caxaBin      = Join-Path $v.FullName 'bin'
    $configFile   = Join-Path $v.FullName 'Config\IronCad.Addin.config'
    $manifestFile = Join-Path $caxaBin 'IronCad.exe.manifest'
    $verTag       = Get-VerTag $v.Name $caxaBin
    if (-not $verTag) { continue }

    $left = @()
    $left += Select-String -Path $configFile -Pattern "$addinName" -SimpleMatch -ErrorAction SilentlyContinue
    $left += Select-String -Path $manifestFile -Pattern "$addinName" -SimpleMatch -ErrorAction SilentlyContinue
    if (Test-Path (Join-Path $caxaBin "$addinName.dll")) { $left += "bin still contains $addinName.dll" }
    # 第 5 登记点残留：登记行与 .crx 任一残留都要报（登记行指向已删文件＝下次启动原生 AV）
    $autorunChk = Join-Path (Join-Path $caxaBin 'CAXADraft') (Join-Path 'Modules' 'CxAutorun.cx')
    if ((Test-Path $autorunChk) -and ((Select-String -Path $autorunChk -Pattern $crxName -SimpleMatch -ErrorAction SilentlyContinue) -ne $null)) {
        $left += "CxAutorun.cx 仍登记 $crxName"
    }
    if (Test-Path (Join-Path (Join-Path $caxaBin 'CAXADraft') (Join-Path 'bin' $crxName))) {
        $left += "CAXADraft\bin 仍有 $crxName"
    }
    if (Test-Path (Join-Path $caxaBin $qrRuleName)) { $left += "bin 仍有 $qrRuleName" }
    if (Test-Path (Join-Path $caxaBin $cmdIndexName)) { $left += "bin 仍有 $cmdIndexName" }
    if (Test-Path "HKCU:\SOFTWARE\CAXA\CAXA 3D $verTag\Applications\$addinName") { $left += 'registry key still present' }

    if ($left.Count -gt 0) {
        Write-Host "CAXA 3D $($v.Name): still has residue:" -ForegroundColor Yellow
        $left | ForEach-Object { Write-Host "    $_" }
        $hadError = $true
    } else {
        Write-Host "CAXA 3D $($v.Name): clean"
    }
}

Write-Host ""
Write-Host "Uninstall finished." -ForegroundColor Green
if ($hadError) { Write-Host "Some residual items remain; close CAXA and run again." -ForegroundColor Yellow }
if ($Host.Name -eq 'ConsoleHost') { Read-Host 'Press Enter to exit' }
