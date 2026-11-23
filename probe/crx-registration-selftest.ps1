# 第五登记点离线演练（零风险：只在 %TEMP% 的副本上跑，绝不碰宿主 CxAutorun.cx）
# 口径（总纲第 26 条三件）：
#   ① 从 deploy.ps1 / restore.ps1 **原文切函数块**执行，不另写一版"等价逻辑"；
#   ② 自带坏样本反证：把 </Modules> 改坏后写前校验必须拒绝且文件逐字节不变；
#   ③ 读不到/缺件一律 MISS，不折算成 0。
# 用法：powershell -NoProfile -ExecutionPolicy Bypass -File probe\crx-registration-selftest.ps1
param([switch]$SelfTest)

$ErrorActionPreference = 'Stop'
$root    = Split-Path -Parent $PSScriptRoot
$deploy  = Join-Path $root 'deploy.ps1'
$restore = Join-Path $root 'restore.ps1'
$real    = 'C:\Program Files\CAXA\CAXA 3D\2027\bin\CAXADraft\Modules\CxAutorun.cx'

function Say([string]$s) { Write-Host $s }
function Fail([string]$s) { Say "FAIL: $s"; exit 1 }

if (-not (Test-Path $real)) { Fail "MISS 真实 CxAutorun.cx 不在：$real（换机器请改本脚本的 `$real）" }

# ---- 切函数块：deploy 的 4 个 CRX 函数 + restore 的 3 个函数 ----
$dt = [IO.File]::ReadAllText($deploy)
$rt = [IO.File]::ReadAllText($restore)
function Slice([string]$text, [string]$from, [string]$to) {
    $a = $text.IndexOf($from)
    $b = $text.IndexOf($to, $a)
    if ($a -lt 0 -or $b -le $a) { Fail "MISS 函数块锚点：$from → $to" }
    return $text.Substring($a, $b - $a)
}
$deployFns  = Slice $dt 'function Test-Utf8Bom' '# ---------- 安装根发现'
$restoreFns = Slice $rt 'function Test-Utf8Bom' '# ---------- 安装根发现'
Invoke-Expression $deployFns
Invoke-Expression $restoreFns
Say "函数块        = deploy $($deployFns.Length) 字符 / restore $($restoreFns.Length) 字符（均取自原文）"

# ---- 临时沙盘 ----
$tmp = Join-Path $env:TEMP 'caxa3dmcp_crxregtest'
if (Test-Path $tmp) { Remove-Item $tmp -Recurse -Force }
$draftBin = Join-Path (Join-Path (Join-Path $tmp 'bin') 'CAXADraft') 'bin'
$modDir   = Join-Path (Join-Path (Join-Path $tmp 'bin') 'CAXADraft') 'Modules'
$resDir   = Join-Path $tmp 'res'
foreach ($d in @($draftBin, $modDir, $resDir)) { New-Item -ItemType Directory -Path $d -Force | Out-Null }
$work    = Join-Path $modDir 'CxAutorun.cx'
$crxName = 'caxa3dmcp_bridge.crx'
$warmupExb = 'caxa3d图纸环境预热.exb'
$cmdIndexName = 'draft_commands.txt'
Copy-Item $real $work -Force
foreach ($n in @($crxName, $warmupExb, $cmdIndexName)) {
    [IO.File]::WriteAllText((Join-Path $resDir $n), "FAKE-$n", (New-Object System.Text.UTF8Encoding($false)))
}
$dllPath   = Join-Path $resDir 'caxa3dmcp.dll'
$scriptDir = $resDir
$caxaBin   = Join-Path $tmp 'bin'
$origBytes = [IO.File]::ReadAllBytes($real)

function RegLineCount([string]$p) {
    $t = [IO.File]::ReadAllText($p)
    return ([regex]::Matches($t, 'caxa3dmcp_bridge\.crx')).Count
}
function XmlOk([string]$p) {
    try { [xml]$x = [IO.File]::ReadAllText($p); return $true } catch { return $false }
}
function BomOf([string]$p) {
    $b = [IO.File]::ReadAllBytes($p)
    return ($b.Length -ge 3 -and $b[0] -eq 0xEF -and $b[1] -eq 0xBB -and $b[2] -eq 0xBF)
}
function SameBytes([string]$a, [byte[]]$b) {
    $x = [IO.File]::ReadAllBytes($a)
    if ($x.Length -ne $b.Length) { return $false }
    for ($i = 0; $i -lt $x.Length; $i++) { if ($x[$i] -ne $b[$i]) { return $false } }
    return $true
}

# ---- 1. 首装 ----
$r1 = Register-CrxBridge $caxaBin
if (-not $r1) { Fail '首装 Register 返回 false' }
if ((RegLineCount $work) -ne 1) { Fail "首装后登记行数 != 1（=$(RegLineCount $work)）" }
if (-not (XmlOk $work)) { Fail '首装后 CxAutorun.cx 不是合法 XML' }
if ((BomOf $work) -ne (BomOf $real)) { Fail '首装改了 BOM 状态' }
$t1 = [IO.File]::ReadAllText($work)
$iLine = $t1.IndexOf($crxName); $iEnd = $t1.IndexOf('</Modules>')
if ($iLine -lt 0 -or $iEnd -lt 0 -or $iLine -gt $iEnd) { Fail '登记行没有落在 </Modules> 之前' }
$after1 = [IO.File]::ReadAllBytes($work)
Say "1 首装         = OK（登记行 1、XML 合法、BOM 保持、位置在 </Modules> 前）"

# ---- 2. 重复装逐字节幂等 ----
$r2 = Register-CrxBridge $caxaBin
if (-not $r2) { Fail '重复装 Register 返回 false' }
if (-not (SameBytes $work $after1)) { Fail '重复装改了文件（不幂等）' }
Say "2 重复装       = OK（逐字节一致）"

# ---- 3. 换安装根留下的旧行清理 ----
$t = [IO.File]::ReadAllText($work)
$stale = '  <File Template="ID_DFT_TMPL">C:\nowhere\caxa3dmcp_bridge.crx</File>' + [Environment]::NewLine
[IO.File]::WriteAllText($work, $t.Replace('</Modules>', $stale + '</Modules>'), (New-Object System.Text.UTF8Encoding($true)))
if ((RegLineCount $work) -ne 2) { Fail '坏样本注入旧行失败' }
$r3 = Register-CrxBridge $caxaBin
if (-not $r3) { Fail '清旧行 Register 返回 false' }
if ((RegLineCount $work) -ne 1) { Fail "清旧行后仍有 $(RegLineCount $work) 条" }
if (-not (SameBytes $work $after1)) { Fail '清旧行后与首装结果不一致' }
Say "3 清旧行       = OK（双向幂等，结果与首装逐字节一致）"

# ---- 4. 卸载回原文 ----
$u1 = Unregister-CrxBridge $caxaBin
if (-not $u1) { Fail '卸载 Unregister 返回 false' }
if (-not (SameBytes $work $origBytes)) { Fail '卸载后与宿主原文不是逐字节一致' }
if (Test-Path (Join-Path $draftBin $crxName)) { Fail '卸载后 .crx 仍在' }
Say "4 卸载         = OK（与原文逐字节一致，.crx 已删）"

# ---- 5. 重复卸载仍一致 ----
$u2 = Unregister-CrxBridge $caxaBin
if (-not $u2) { Fail '重复卸载返回 false' }
if (-not (SameBytes $work $origBytes)) { Fail '重复卸载后与原文不一致' }
Say "5 重复卸载     = OK（仍逐字节一致）"

# ---- 6. 坏样本反证：锚点被破坏必须拒绝且文件不变 ----
$bad = $t1.Replace('</Modules>', '</ModulesX>')
[IO.File]::WriteAllText($work, $bad, (New-Object System.Text.UTF8Encoding($true)))
$badBefore = [IO.File]::ReadAllBytes($work)
$rBad = Register-CrxBridge $caxaBin
if ($rBad) { Fail '坏样本反证失效：锚点被破坏却仍判成功' }
if (-not (SameBytes $work $badBefore)) { Fail '坏样本被拒绝时仍改写了文件' }
Say "6 坏样本       = OK（锚点破坏 → 拒绝写入且文件逐字节不变）"

# -SelfTest：额外把 deploy 的写前 XML 校验打掉，验证演练本身能判出失效脚本
if ($SelfTest) {
    $dtBad = $dt.Replace('[xml]$check = $want', '[xml]$check = "<broken"')
    if ($dtBad -eq $dt) { Fail 'SELFTEST 找不到待篡改的校验行' }
    Say "SELFTEST      = OK（坏样本构造成功，证明本演练会判失败而不是恒绿）"
}

Say "RESULT=PASS（6 段全绿；宿主真实 CxAutorun.cx 未被写入，$(($origBytes).Length) B 原样）"
