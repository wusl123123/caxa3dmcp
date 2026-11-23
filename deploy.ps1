param(
    [string]$WorkRoot = ''
)

# ============================================================
# caxa3dmcp 一键安装脚本（模板）
#   1. 拷贝 DLL/PDB 到 <CAXA>\<年份>\bin
#   2. 在 Config\IronCad.Addin.config 登记 <AddIn> 条目
#   3. 在 bin\IronCad.exe.manifest 登记 dependency（仅当 DLL 内嵌 RT_MANIFEST）
#   4. 写 HKCU\SOFTWARE\CAXA\CAXA 3D <N.0>\Applications\<键名>
# 用法：双击 安装.bat（自动 UAC），或
#       powershell -ExecutionPolicy Bypass -File deploy.ps1
# 铁律：本文件必须存成「单一 UTF-8 BOM」，否则 PS5.1 按 GBK 解码中文报错并静默退出。
# 占位符 caxa3dmcp / {5B82F332-9F34-456D-ACB6-8CC02F616340} / CAXA3D MCP桥接 / CAXA 3D MCP 服务进程内桥接插件（命名管道） 由 scripts/new-addin.ps1 替换。
# ============================================================

$ScriptVersion = '2026-11-12.1719'
$ErrorActionPreference = 'Stop'

# ---- v003.056 日志重置铁律（规则第 19 节 15 条）：每轮脚本执行都从干净日志起点开始 ----
# 清两个输出日志；_crx.marker 是探针不清；CAXA 运行中文件被占用时静默跳过（插件加载时仍会截断重建）。
$__logs = @((Join-Path $env:TEMP 'caxa3dmcp_trace.log'), (Join-Path $env:TEMP 'caxa3dmcp_crx.log'))
foreach ($__f in $__logs) { if (Test-Path -LiteralPath $__f) { Remove-Item -LiteralPath $__f -Force -ErrorAction SilentlyContinue } }
# 任何未捕获错误都不要静默退出（提权窗口会瞬间关闭，用户看不到原因）
trap {
    Write-Host ""
    Write-Host "[FATAL] $($_.Exception.Message)" -ForegroundColor Red
    Write-Host $_.ScriptStackTrace -ForegroundColor DarkGray
    Write-Host "Full log: $env:TEMP\caxa3dmcp_install.log" -ForegroundColor Yellow
    Read-Host 'Install failed, press Enter to exit'
    exit 1
}

$scriptDir = if ($WorkRoot) { $WorkRoot } else { $PSScriptRoot }
if (-not $scriptDir) { $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path }

# ---------- 自动提权（先落一份真实路径的副本，提权后执行副本、业务目录靠 -WorkRoot 传回） ----------
$identity  = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    $elevDir = Join-Path $env:ProgramData 'caxa3dmcp'
    New-Item -ItemType Directory -Path $elevDir -Force | Out-Null
    $elevScript = Join-Path $elevDir ('deploy-' + $ScriptVersion + '.ps1')
    Copy-Item $PSCommandPath $elevScript -Force

    Write-Host "Restarting as administrator: $elevScript" -ForegroundColor Yellow
    Write-Host "Business directory: $scriptDir"
    $argLine = "-NoProfile -ExecutionPolicy Bypass -File `"$elevScript`" -WorkRoot `"$scriptDir`""
    Start-Process powershell.exe -Verb RunAs -ArgumentList $argLine
    exit
}

Write-Host "================ caxa3dmcp 安装 ================"
Write-Host "Script version : $ScriptVersion"
Write-Host ""
try { Start-Transcript -Path (Join-Path $env:TEMP 'caxa3dmcp_install.log') -Append | Out-Null } catch { }

# ---------- 常量（与 Addin.cs 的 [Guid] 必须一致） ----------
$addinName  = 'caxa3dmcp'
$clsid      = '{5B82F332-9F34-456D-ACB6-8CC02F616340}'
$displayZh  = 'CAXA3D MCP桥接'
$descZh     = 'CAXA 3D MCP 服务进程内桥接插件（命名管道）'

# ---------- 第五登记点：图纸环境唯一桥（26-11-13）----------
# 桥接型 CRX 必须同时满足：① .crx 落 <bin>\CAXADraft\bin\；
# ② <bin>\CAXADraft\Modules\CxAutorun.cx 在 </Modules> **之前**有自己那一行。
# 缺一即「装上但静默不可用」（投递后 NOCRX/TIMEOUT）；卸载残留登记会让宿主启动期原生 AV。
$crxName      = 'caxa3dmcp_bridge.crx'
$warmupExb    = 'caxa3d图纸环境预热.exb'
$cmdIndexName = 'draft_commands.txt'
$qrRuleName   = 'caxa3dmcp二维码定位规则.txt'
$qrCoderName  = 'QRCoder.dll'

function Test-Utf8Bom([string]$path) {
    $b = [IO.File]::ReadAllBytes($path)
    return ($b.Length -ge 3 -and $b[0] -eq 0xEF -and $b[1] -eq 0xBB -and $b[2] -eq 0xBF)
}

function Write-TextKeepBom([string]$path, [string]$text, [bool]$hadBom) {
    $enc = New-Object System.Text.UTF8Encoding($hadBom)
    [IO.File]::WriteAllText($path, $text, $enc)
}

# 随包资源查找：DLL 输出目录 → 脚本目录 → Bridge\bin（CRX 直接编译产物）
function Find-BridgeResource([string]$name) {
    $c = @(
        (Join-Path (Split-Path -Parent $dllPath) $name),
        (Join-Path $scriptDir $name),
        (Join-Path (Join-Path $scriptDir 'Bridge\bin') $name)
    )
    foreach ($p in $c) { if ($p -and (Test-Path $p)) { return $p } }
    return $null
}

function Register-CrxBridge([string]$caxaBin) {
    $draftBin = Join-Path (Join-Path $caxaBin 'CAXADraft') 'bin'
    $autorun  = Join-Path (Join-Path $caxaBin 'CAXADraft') (Join-Path 'Modules' 'CxAutorun.cx')
    $dstCrx   = Join-Path $draftBin $crxName

    if (-not (Test-Path $draftBin)) {
        Write-Host "[5/5] 该版本没有 CAXADraft\bin（图纸环境未安装？）——跳过 CRX 登记。" -ForegroundColor Yellow
        return $false
    }
    $srcCrx = Find-BridgeResource $crxName
    if (-not $srcCrx) {
        Write-Host "[5/5] 缺 $crxName（先跑 Bridge\build.ps1 编译）——图纸环境通讯不可用，其余 MCP 工具不受影响。" -ForegroundColor Yellow
        return $false
    }
    try {
        Copy-Item $srcCrx $dstCrx -Force -ErrorAction Stop
        Write-Host "[5/5] CRX 已就位：$dstCrx"
    } catch {
        Write-Host "[5/5] CRX 拷贝失败（CAXA/图纸进程正在运行？）：$($_.Exception.Message)" -ForegroundColor Red
        return $false
    }

    # 预热空图纸、离线命令索引、二维码定位规则与 QRCoder 随包落到程序目录（add-in AssemblyDir 从这里找）
    # 坑（26-11-15）：QRCoder.dll 是通用第三方库，本机 <bin> 可能已被 caxa3dBatchMaster 等插件放了同名文件。
    #   若目标已存在且哈希不同，绝不覆盖（否则破坏别人的插件）；只警告，让共用者继续用已装版本。
    foreach ($n in @($warmupExb, $cmdIndexName, $qrRuleName, $qrCoderName)) {
        $s = Find-BridgeResource $n
        if ($s) {
            $dstRes = Join-Path $caxaBin $n
            if ($n -eq $qrCoderName -and (Test-Path $dstRes)) {
                try {
                    $hSrc = (Get-FileHash -LiteralPath $s -Algorithm SHA256).Hash
                    $hDst = (Get-FileHash -LiteralPath $dstRes -Algorithm SHA256).Hash
                    if ($hSrc -ne $hDst) {
                        Write-Host "[5/5] [shared] $n 已由其它插件安装且版本不同（$hSrc vs $hDst）——保留不覆盖" -ForegroundColor Yellow
                        continue
                    }
                    Write-Host "[5/5] [shared] $n 已存在且哈希一致，跳过拷贝"
                    continue
                } catch { Write-Host "[5/5] [shared] $n 哈希比对失败，保守跳过覆盖：$($_.Exception.Message)" -ForegroundColor Yellow; continue }
            }
            try { Copy-Item $s $dstRes -Force -ErrorAction Stop; Write-Host "[5/5] 随包资源已就位：$n" }
            catch { Write-Host "[5/5] 随包资源 $n 拷贝失败：$($_.Exception.Message)" -ForegroundColor Yellow }
        } else {
            Write-Host "[5/5] 缺随包资源 $n（预热/命令索引将走第三态，不假成功）" -ForegroundColor Yellow
        }
    }

    if (-not (Test-Path $autorun)) {
        Write-Host "[5/5] 没有 $autorun ——不凭空新建宿主模块文件，请手工确认图纸环境自动加载机制。" -ForegroundColor Yellow
        return $false
    }
    $hadBom   = Test-Utf8Bom $autorun
    $original = [IO.File]::ReadAllText($autorun)
    $line     = '  <File Template="ID_DFT_TMPL">' + $dstCrx + '</File>'

    # 幂等：先摘掉自己所有旧行（含换安装根后指向已删路径的残留），再插回唯一一行。
    # 判据不能写 $stripped -eq $want（那样短路永远不成立）。
    $rxOwn    = [regex]'(?m)^[ \t]*<File[^>]*>[^<]*\\caxa3dmcp_bridge\.crx</File>[ \t]*\r?\n'
    $stripped = $rxOwn.Replace($original, '')
    $want     = $stripped.Replace('</Modules>', $line + [Environment]::NewLine + '</Modules>')
    if ($want -eq $original) {
        Write-Host "[5/5] CxAutorun.cx 已登记本桥 CRX（跳过）"
        return $true
    }
    if ($want.Length -le $stripped.Length) { Write-Host "[5/5] 登记文本异常（没有变长），放弃写入。" -ForegroundColor Red; return $false }
    if (([regex]::Matches($want, '</Modules>')).Count -ne 1) { Write-Host "[5/5] </Modules> 锚点不唯一，放弃写入（不动宿主文件）。" -ForegroundColor Red; return $false }
    if ($want.IndexOf($line) -lt 0) { Write-Host "[5/5] 待写入行不在结果里，放弃写入。" -ForegroundColor Red; return $false }
    try {
        [xml]$check = $want
    } catch {
        Write-Host "[5/5] 待写入内容不是合法 XML，放弃写入：$($_.Exception.Message)" -ForegroundColor Red
        return $false
    }
    Write-Host ("[5/5] CxAutorun.cx 计划改动：{0} → {1} 字符（BOM={2}）" -f $original.Length, $want.Length, $hadBom)
    try {
        Write-TextKeepBom $autorun $want $hadBom
        $back = [IO.File]::ReadAllText($autorun)
        if ($back -ne $want) { throw '回读与写入内容不一致' }
        if ((Test-Utf8Bom $autorun) -ne $hadBom) { throw '回读 BOM 状态与原文件不同' }
        Write-Host "[5/5] CxAutorun.cx 登记完成并回读校验通过"
        return $true
    } catch {
        Write-Host "[5/5] 登记失败（$($_.Exception.Message)），回滚原文。" -ForegroundColor Red
        try { Write-TextKeepBom $autorun $original $hadBom } catch { Write-Host "[5/5] 回滚也失败，文件可能需手工修复：$autorun" -ForegroundColor Red }
        return $false
    }
}
# ---------- 安装根发现（绝不假设在 C 盘；规则第 6 节第 12 条 / 坑 163） ----------
# 固定盘必须用 [IO.DriveInfo]：PS 5.1 的 Get-PSDrive 没有可用的 DriveType 属性（恒空），
# Where-Object { $_.DriveType -eq 'Fixed' } 会把整张盘表清空，换机（非 C 盘安装）直接查无。
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

# 候选可能是安装根、版本目录本身、甚至 ...\bin —— 一律上溯到「装着 4 位年份子目录」那一层
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

# ================================================================ helpers

function Write-TextWithBom([string]$path, [string]$text) {
    [IO.File]::WriteAllText($path, $text, (New-Object System.Text.UTF8Encoding($true)))
}

function Set-NotReadOnly([string]$file) {
    try {
        $item = Get-Item $file -Force -ErrorAction Stop
        if ($item.Attributes -band [IO.FileAttributes]::ReadOnly) {
            $item.Attributes = $item.Attributes -bxor [IO.FileAttributes]::ReadOnly
        }
    } catch {
        Write-Host "      warning: clear readonly failed $file -> $($_.Exception.Message)" -ForegroundColor Yellow
    }
}

function New-XmlElement([System.Xml.XmlDocument]$doc, [string]$name) {
    $uri = $doc.DocumentElement.NamespaceURI
    if ([string]::IsNullOrEmpty($uri)) { return $doc.CreateElement($name) }
    return $doc.CreateElement($name, $uri)
}

# 手术式删除自己的 config 条目（按 inprocserver / siteclsid 精确匹配）
function Remove-AddinEntries([string]$configFile, [string]$dllName, [string]$siteClsid) {
    $removed = 0
    try {
        [xml]$doc = Get-Content $configFile -Raw -Encoding UTF8
        foreach ($n in @($doc.SelectNodes("//*[local-name()='AddIn']"))) {
            $inproc = $n.SelectSingleNode("*[local-name()='inprocserver']")
            $site   = $n.SelectSingleNode("*[local-name()='siteclsid']")
            $hit = $false
            if ($inproc -and [string]::Equals($inproc.InnerText.Trim(), $dllName, [StringComparison]::OrdinalIgnoreCase)) { $hit = $true }
            if (-not $hit -and $site -and [string]::Equals($site.InnerText.Trim(), $siteClsid, [StringComparison]::OrdinalIgnoreCase)) { $hit = $true }
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

# 块级正则兜底：XML 解析失败时也能清干净，可重复执行
function Remove-ConfigBlocksByRegex([string]$configFile, [string[]]$needles) {
    $text = [IO.File]::ReadAllText($configFile)
    $rx = [regex]'(?is)<AddIn\b[^>]*>.*?</AddIn>'
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
    if ($removed -gt 0) { Write-TextWithBom $configFile $sb.ToString() }
    return $removed
}

function Remove-ManifestDependencies([string]$manifestFile, [string]$asmName) {
    $removed = 0
    try {
        [xml]$doc = Get-Content $manifestFile -Raw -Encoding UTF8
        foreach ($n in @($doc.SelectNodes("//*[local-name()='dependency']"))) {
            $id = $n.SelectSingleNode(".//*[local-name()='assemblyIdentity']")
            if ($id -and [string]::Equals($id.GetAttribute('name'), $asmName, [StringComparison]::OrdinalIgnoreCase)) {
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

function Remove-ManifestBlocksByRegex([string]$manifestFile, [string[]]$needles) {
    $text = [IO.File]::ReadAllText($manifestFile)
    $rx = [regex]'(?is)<dependency\b[^>]*>.*?</dependency>'
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
    if ($removed -gt 0) { Write-TextWithBom $manifestFile $sb.ToString() }
    return $removed
}

# 探测 DLL 是否内嵌 SxS 清单：mt 导出优先，回退扫描 PE 里的 clrClass 字样
function Test-EmbeddedManifest([string]$dllPath) {
    $mt = 'C:\Program Files (x86)\Windows Kits\10\bin\10.0.19041.0\x64\mt.exe'
    if (Test-Path $mt) {
        $probe = Join-Path $env:TEMP ("caxa3dmcp_mf_{0}.xml" -f [Guid]::NewGuid().ToString('N'))
        try {
            & $mt "-inputresource:$dllPath;#2" "-out:$probe" -nologo 2>$null | Out-Null
            if ((Test-Path $probe) -and (Get-Item $probe).Length -gt 0) { return $true }
        } catch {
        } finally {
            if (Test-Path $probe) { Remove-Item $probe -Force -ErrorAction SilentlyContinue }
        }
    }
    try {
        $text = [Text.Encoding]::ASCII.GetString([IO.File]::ReadAllBytes($dllPath))
        return $text.Contains('clrClass')
    } catch {
        return $false
    }
}

function Add-AddinEntry([string]$configFile, [string]$dllName, [string]$displayName, [string]$description) {
    [xml]$doc = Get-Content $configFile -Raw -Encoding UTF8

    $addin = New-XmlElement $doc 'AddIn'
    foreach ($key in @('identify', 'inprocserver', 'siteclsid', 'modulever', 'platformver', 'autoload', 'system')) {
        $e = New-XmlElement $doc $key
        switch ($key) {
            'inprocserver' { $e.InnerText = $dllName }
            'siteclsid'    { $e.InnerText = $clsid }
            'system'       { $e.InnerText = 'true' }
            default        { $e.InnerText = '' }
        }
        [void]$addin.AppendChild($e)
    }

    # displayname/default 必须严格等于注册表 Applications 下的键名，否则插件不加载且无 trace
    $display = New-XmlElement $doc 'displayname'
    $d1 = New-XmlElement $doc 'default'; $d1.InnerText = $addinName; [void]$display.AppendChild($d1)
    $d2 = New-XmlElement $doc 'zh-cn';   $d2.InnerText = $displayName; [void]$display.AppendChild($d2)
    [void]$addin.AppendChild($display)

    $desc = New-XmlElement $doc 'description'
    $x1 = New-XmlElement $doc 'default'; $x1.InnerText = $description; [void]$desc.AppendChild($x1)
    $x2 = New-XmlElement $doc 'zh-cn';   $x2.InnerText = $description; [void]$desc.AppendChild($x2)
    [void]$addin.AppendChild($desc)

    $addIns = $doc.SelectSingleNode("//*[local-name()='AddIns']")
    if (-not $addIns) { $addIns = $doc.DocumentElement }
    [void]$addIns.AppendChild($addin)
    $doc.Save($configFile)
}

function Add-ManifestDependency([string]$manifestFile, [string]$version, [string]$publicToken) {
    [xml]$doc = Get-Content $manifestFile -Raw -Encoding UTF8
    $dep = New-XmlElement $doc 'dependency'
    $da  = New-XmlElement $doc 'dependentAssembly'
    $ai  = New-XmlElement $doc 'assemblyIdentity'
    $ai.SetAttribute('name', $addinName)
    $ai.SetAttribute('version', $version)
    $ai.SetAttribute('publicKeyToken', $publicToken)
    $ai.SetAttribute('processorArchitecture', 'amd64')
    [void]$da.AppendChild($ai)
    [void]$dep.AppendChild($da)
    [void]$doc.DocumentElement.AppendChild($dep)
    $doc.Save($manifestFile)
}

function Add-RegistryEntry([string]$verTag) {
    $path = "HKCU:\SOFTWARE\CAXA\CAXA 3D $verTag\Applications\$addinName"
    New-Item -Path $path -Force | Out-Null
    Set-ItemProperty -Path $path -Name '(default)'     -Value $clsid
    Set-ItemProperty -Path $path -Name 'LoadOnStartup' -Value 1 -Type DWord
    Set-ItemProperty -Path $path -Name 'ShowInList'    -Value 1 -Type DWord
}

# ================================================================ 定位 DLL

function Find-AddinDll {
    $candidates = @(
        $scriptDir,
        (Join-Path $scriptDir 'bin\x64\Release\net48'),
        (Join-Path $scriptDir 'bin\Release\net48')
    )
    $d = [IO.DirectoryInfo]$scriptDir
    for ($i = 0; $i -lt 4 -and $d; $i++) {
        $candidates += (Join-Path $d.FullName 'bin\x64\Release\net48')
        $candidates += (Join-Path $d.FullName 'bin\Release\net48')
        $d = $d.Parent
    }
    foreach ($c in $candidates) {
        $p = Join-Path $c "$addinName.dll"
        if (Test-Path $p) { return $p }
    }
    return $null
}

$dllPath = Find-AddinDll
if (-not $dllPath) {
    Write-Host "[ERROR] cannot find $addinName.dll" -ForegroundColor Red
    Write-Host "        Build first: dotnet build -c Release -p:Platform=x64" -ForegroundColor Yellow
    Read-Host 'Press Enter to exit'
    exit 1
}
$pdbPath = [IO.Path]::ChangeExtension($dllPath, '.pdb')

$asmName     = [Reflection.AssemblyName]::GetAssemblyName($dllPath)
$version     = $asmName.Version.ToString()
$publicToken = (($asmName.GetPublicKeyToken() | ForEach-Object { $_.ToString('x2') }) -join '')
$hasManifest = Test-EmbeddedManifest $dllPath

Write-Host "DLL        : $dllPath"
Write-Host "Version    : $version  publicKeyToken=$publicToken"
Write-Host "SxS manifest: $hasManifest"
Write-Host ""

if ($publicToken -eq '') {
    Write-Host "[ERROR] DLL is not strong-named; CAXA will refuse to load it." -ForegroundColor Red
    Read-Host 'Press Enter to exit'
    exit 1
}
if ($publicToken -ne '56647e1eee90f6d4') {
    Write-Host "[WARN] publicKeyToken != 56647e1eee90f6d4 (Lib\ICApiAddin.snk) -> CAXA will refuse to load." -ForegroundColor Yellow
}
if (-not $hasManifest) {
    Write-Host "[WARN] DLL has no embedded SxS manifest; manifest registration will be skipped." -ForegroundColor Yellow
    Write-Host "       Rebuild with Release to embed the manifest (mt + strip + sn)." -ForegroundColor Yellow
}

# ---------- CAXA 进程检查（DLL 被锁即拷贝必失败） ----------
$running = Get-Process -Name 'IRONCAD', 'IronCad', 'CDRAFT_M_IC' -ErrorAction SilentlyContinue
if ($running) {
    Write-Host "[WARN] CAXA processes detected (files may be locked)." -ForegroundColor Yellow
    $ans = Read-Host 'Already fully exited? type y to continue'
    if ($ans -ne 'y') { exit 1 }
}

function Get-VerTag([string]$year, [string]$caxaBin) {
    # 优先读 IronCad.exe 的 ProductVersion（如 29.0），失败回退年份推算：N.0 = 年份 - 1998
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

# ---------- 发现已安装版本 ----------
# 只认「4 位年份目录 + bin\IronCad.exe」，排除 CAXA 3D Mechanical 等非本产品目录
$versions = @()
if ($caxaRoot) {
    $versions = @(Get-ChildItem -LiteralPath $caxaRoot -Directory -ErrorAction SilentlyContinue |
                  Where-Object { $_.Name -match '^\d{4}$' -and
                                 (Test-Path (Join-Path $_.FullName 'bin')) -and
                                 (Test-Path (Join-Path $_.FullName 'bin\IronCad.exe')) })
}
if ($versions.Count -eq 0) {
    Write-Host "[ERROR] no CAXA 3D installation found on this machine." -ForegroundColor Red
    Write-Host "        Probed: env CAXA3D_ROOT, then <fixed drive>:\Program Files\CAXA\CAXA 3D," -ForegroundColor Red
    Write-Host "        <fixed drive>:\Program Files (x86)\CAXA\CAXA 3D, <fixed drive>:\CAXA\CAXA 3D, <fixed drive>:\CAXA." -ForegroundColor Red
    Write-Host "        If installed elsewhere, set env CAXA3D_ROOT to the install root, the year folder or its bin." -ForegroundColor Yellow
    Read-Host 'Press Enter to exit'
    exit 1
}
Write-Host ("CAXA 3D install root : {0}" -f $caxaRoot) -ForegroundColor Cyan

$hadError = $false

foreach ($v in $versions) {
    $year         = $v.Name
    $caxaBin      = Join-Path $v.FullName 'bin'
    $configFile   = Join-Path $v.FullName 'Config\IronCad.Addin.config'
    $manifestFile = Join-Path $caxaBin 'IronCad.exe.manifest'
    $verTag       = Get-VerTag $year $caxaBin
    if (-not $verTag) {
        Write-Host "[SKIP] $year -> cannot determine version tag" -ForegroundColor Yellow
        continue
    }

    Write-Host "================ CAXA 3D $year (registry $verTag) ================"

    if (-not (Test-Path $configFile))   { Write-Host "[ERROR] missing $configFile" -ForegroundColor Red; $hadError = $true; continue }
    if (-not (Test-Path $manifestFile)) { Write-Host "[ERROR] missing $manifestFile" -ForegroundColor Red; $hadError = $true; continue }

    Set-NotReadOnly $configFile
    Set-NotReadOnly $manifestFile

    # ---------- 1. 只在「干净状态」下备份；重装绝不覆盖旧备份 ----------
    $configText   = [IO.File]::ReadAllText($configFile)
    $manifestText = [IO.File]::ReadAllText($manifestFile)
    if ($configText -notlike "*$addinName*" -and $manifestText -notlike "*$addinName*") {
        try {
            Copy-Item $configFile   "$configFile.bak" -Force
            Copy-Item $manifestFile "$manifestFile.bak" -Force
            Write-Host "[1/5] clean config backed up (*.bak)"
        } catch {
            Write-Host "[1/5] backup failed: $($_.Exception.Message)" -ForegroundColor Yellow
        }
    } else {
        Write-Host "[1/5] entries already present; skip backup (avoid dirty backup)"
    }

    # ---------- 2. 拷贝 DLL/PDB ----------
    $copyOk = $true
    try {
        Copy-Item $dllPath (Join-Path $caxaBin "$addinName.dll") -Force -ErrorAction Stop
        if (Test-Path $pdbPath) { Copy-Item $pdbPath (Join-Path $caxaBin "$addinName.pdb") -Force -ErrorAction Stop }
    } catch {
        Write-Host "[2/5] copy failed: $($_.Exception.Message)" -ForegroundColor Red
        Write-Host "      Exit CAXA completely (including background processes) and retry." -ForegroundColor Yellow
        $copyOk = $false
        $hadError = $true
    }
    if (-not $copyOk) { continue }
    Write-Host "[2/5] DLL/PDB copied to $caxaBin"

    # ---------- 3. 先清自己的旧条目，再登记（幂等） ----------
    try {
        $needles = @("$addinName.dll", $clsid)
        $null = Remove-AddinEntries $configFile "$addinName.dll" $clsid
        $null = Remove-ConfigBlocksByRegex $configFile $needles
        $null = Remove-ManifestDependencies $manifestFile $addinName
        $null = Remove-ManifestBlocksByRegex $manifestFile @($addinName)

        Add-AddinEntry $configFile "$addinName.dll" $displayZh $descZh
        if ($hasManifest) {
            Add-ManifestDependency $manifestFile $version $publicToken
            Write-Host "[3/5] IronCad.Addin.config + IronCad.exe.manifest registered"
        } else {
            Write-Host "[3/5] IronCad.Addin.config registered (manifest skipped)" -ForegroundColor Yellow
        }

        # ---------- 4. 注册表 Applications 键 ----------
        Add-RegistryEntry $verTag
        Write-Host "[4/5] HKCU Applications key registered ($verTag)"
    } catch {
        Write-Host "[3/5] registration failed: $($_.Exception.Message)" -ForegroundColor Red
        $hadError = $true
    }

    # ---------- 5. 第五登记点：唯一 CRX 桥 ----------
    if (-not (Register-CrxBridge $caxaBin)) { $hadError = $true }

    Write-Host ""
}

# ---------- 回读校验（不看脚本自己说成功） ----------
Write-Host "================ verification ================"
foreach ($v in $versions) {
    $caxaBin      = Join-Path $v.FullName 'bin'
    $configFile   = Join-Path $v.FullName 'Config\IronCad.Addin.config'
    $manifestFile = Join-Path $caxaBin 'IronCad.exe.manifest'
    $verTag       = Get-VerTag $v.Name $caxaBin
    if (-not $verTag) { Write-Host "[SKIP] $($v.Name) -> cannot determine version tag" -ForegroundColor Yellow; continue }

    $okFile = Test-Path (Join-Path $caxaBin "$addinName.dll")
    $okCfg  = ((Select-String -Path $configFile -Pattern "$addinName.dll" -SimpleMatch -ErrorAction SilentlyContinue) -ne $null)
    $okDep  = (-not $hasManifest) -or ((Select-String -Path $manifestFile -Pattern "`"$addinName`"" -SimpleMatch -ErrorAction SilentlyContinue) -ne $null)
    $okReg  = Test-Path "HKCU:\SOFTWARE\CAXA\CAXA 3D $verTag\Applications\$addinName"

    $autorunFile = Join-Path (Join-Path $caxaBin 'CAXADraft') (Join-Path 'Modules' 'CxAutorun.cx')
    $okCrx = Test-Path (Join-Path (Join-Path $caxaBin 'CAXADraft') (Join-Path 'bin' $crxName))
    $okAutorun = (Test-Path $autorunFile) -and ((Select-String -Path $autorunFile -Pattern $crxName -SimpleMatch -ErrorAction SilentlyContinue) -ne $null)
    $okExb = Test-Path (Join-Path $caxaBin $warmupExb)

    Write-Host ("CAXA 3D {0}: DLL={1} config={2} manifest={3} registry={4} crx={5} autorun={6} warmupExb={7}" -f $v.Name, $okFile, $okCfg, $okDep, $okReg, $okCrx, $okAutorun, $okExb)
    if (-not ($okFile -and $okCfg -and $okDep -and $okReg -and $okCrx -and $okAutorun)) { $hadError = $true }
}

Write-Host ""
Write-Host "Install finished. Trace log: %TEMP%\caxa3dmcp_trace.log" -ForegroundColor Green
Write-Host "启动 CAXA 3D 并重启后：日志应有「已注册 74 个工具」与 auto-warm | START/OK/FAIL/SKIP；进图纸环境后调 draft_ping 验桥。" -ForegroundColor Green
if ($hadError) { Write-Host "Some checks failed; fix the items above and run again." -ForegroundColor Yellow }
if ($Host.Name -eq 'ConsoleHost') { Read-Host 'Press Enter to exit' }
