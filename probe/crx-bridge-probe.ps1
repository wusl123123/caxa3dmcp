param(
    [string]$TempDir = $env:TEMP,
    [string]$CommandIndex = "C:\code\ICAPI\_probe-tools\out\cmd_index_20261111_124939.csv",
    [switch]$SelfTest
)

# caxa3dmcp —— 图纸环境 CRX 桥「只读取证」探针（26-11-13 08:55 探索轮）
#
# 口径（总纲第 26 条三件）：
#   ① 信号 = %TEMP% 下 *_crx.log 推项目前缀，再拼 *_crx.marker / *_request.txt / *_response.txt；
#      命令面 = _probe-tools 离线命令索引 CSV。范围不含 _scratch / bin / obj。
#   ② 反向自检 = -SelfTest 把 TempDir 指向不存在的目录，脚本必须报 MISS 而不是「0 座桥」。
#   ③ 第三态 = 读不到一律 MISS / UNKNOWN，绝不折算成 0 或「通过」。
#
# ★ 本脚本零写入 CRX 桥文件：不发 PING、不改 request/response、不动 seq。
#   理由：文件桥是单写者协议（seq <= g_lastSeq 直接丢弃），旁听式写入会与本机其它插件互吃请求。

$ErrorActionPreference = "SilentlyContinue"
$out = New-Object System.Collections.Generic.List[string]
function Say([string]$s) { $out.Add($s) | Out-Null }

if ($SelfTest) { $TempDir = Join-Path $TempDir "__caxa3dmcp_probe_missing__" }

Say "=== caxa3dmcp CRX 桥只读取证（零写入 / 不发 PING / 不改 seq）==="
Say "TempDir      = $TempDir"
Say "CommandIndex = $CommandIndex"
Say "口径         = *_crx.log 推前缀；marker/request/response 按前缀拼；读不到=MISS 不当 0"

if (-not (Test-Path -LiteralPath $TempDir)) {
    Say "RESULT       = MISS（TempDir 不存在）"
    if ($SelfTest) { Say "SELFTEST     = PASS（坏样本被判 MISS，不是静默 0）" }
    $out | ForEach-Object { Write-Output $_ }
    return
}

# ---------- ① 逐座桥：进程归属 / 冷热态 / 残留请求 ----------
$logs = @(Get-ChildItem -LiteralPath $TempDir -Filter "*_crx.log" -File -ErrorAction SilentlyContinue)
Say "CRX 日志数   = $($logs.Count)"

$hot = 0; $cold = 0; $unknown = 0; $byToken = @{}; $tokenCount = @{}

foreach ($log in $logs) {
    $prefix = $log.Name -replace '_crx\.log$', ''
    $marker = Join-Path $TempDir ($prefix + "_crx.marker")
    $req    = Join-Path $TempDir ($prefix + "_request.txt")
    $resp   = Join-Path $TempDir ($prefix + "_response.txt")

    $head = @(Get-Content -LiteralPath $log.FullName -TotalCount 8 -Encoding UTF8 -ErrorAction SilentlyContinue)
    $tail = @(Get-Content -LiteralPath $log.FullName -Tail 1 -Encoding UTF8 -ErrorAction SilentlyContinue)
    $joined = $head -join "`n"
    $lastLine = ($tail -join '').Trim()

    $exePid = if ($joined -match 'exe pid=(\d+)') { $Matches[1] } else { "MISS" }
    $token  = if ($joined -match 'token=([0-9]+-[0-9]+)') { $Matches[1] } else { "MISS" }
    if ($token -ne "MISS") {
        if (-not $byToken.ContainsKey($token)) { $byToken[$token] = @{}; $tokenCount[$token] = 0 }
        $byToken[$token][$exePid] = 1
        $tokenCount[$token] = $tokenCount[$token] + 1
    }

    $hasMarker = Test-Path -LiteralPath $marker
    $unloaded  = $lastLine -match 'On_kUnloadAppMsg'
    if     ($hasMarker -and -not $unloaded) { $state = "HOT";     $hot++ }
    elseif (-not $hasMarker -and $unloaded) { $state = "COLD";    $cold++ }
    else                                    { $state = "UNKNOWN"; $unknown++ }

    $staleReq  = Test-Path -LiteralPath $req
    $staleResp = Test-Path -LiteralPath $resp
    $risk = if ($staleReq -and -not $hasMarker) { "STALE_REQUEST(坑146)" } else { "-" }

    Say ("  {0,-30} state={1,-7} pid={2,-6} token={3,-16} marker={4,-5} staleReq={5,-5} staleResp={6,-5} {7}" -f `
        $prefix, $state, $exePid, $token, $hasMarker, $staleReq, $staleResp, $risk)
}

if ($logs.Count -eq 0) { Say "  （无 CRX 日志 = 本会话从未进过图纸环境，或日志已被脚本重置）" }
Say "汇总           = HOT $hot / COLD $cold / UNKNOWN $unknown；不同会话令牌数 = $($byToken.Count)"
foreach ($k in @($byToken.Keys | Sort-Object)) {
    $pids = @($byToken[$k].Keys) -join ','
    Say ("  会话 {0,-18} 桥数={1,-3} pid数={2,-3} pid={3}" -f $k, $tokenCount[$k], $byToken[$k].Count, $pids)
    if ($tokenCount[$k] -gt 1 -and $byToken[$k].Count -eq 1) {
        Say "    => 同一加载批次内多座桥同进程（已证）"
    }
}
if ($byToken.Count -eq 0) {
    Say "进程拓扑       = MISS（日志里无 pid/token，样本不足，不下结论）"
} else {
    Say "进程拓扑       = 日志跨会话混合，只能证「同批次桥同进程」；与 C# add-in 是否同进程、图纸环境内嵌/独立"
    Say "                 一律 UNKNOWN —— 实机把 exe pid 与 IRONCAD.exe / CAXADraft.exe 比对才可定案。"
    Say "                 ★ 本结论不影响文件桥可行性：%TEMP% 文本 IPC 与进程拓扑无关。"
}
if ($hot -eq 0) {
    Say "冷态结论       = 无 CRX 消费者：禁止投递会动手的命令，只允许无副作用 PING（坑 146）"
}

# ---------- ② 离线命令面：图纸环境能识别什么 ----------
Say ""
Say "=== 图纸命令表（离线索引）==="
if (Test-Path -LiteralPath $CommandIndex) {
    $rows = @(Import-Csv -LiteralPath $CommandIndex -Encoding UTF8 -ErrorAction SilentlyContinue)
    if ($rows.Count -eq 0) {
        Say "  命令索引 = MISS（CSV 解析出 0 行，不折算成「无命令」）"
    } else {
        $modal = @($rows | Where-Object { $_.Priority -eq 'CMDPRIORITY_MODAL' }).Count
        $dft   = @($rows | Where-Object { $_.Name -like 'CX_DFT_*' }).Count
        Say ("  总命令 {0} / CMDPRIORITY_MODAL {1} / CX_DFT_ 前缀 {2}" -f $rows.Count, $modal, $dft)
        foreach ($k in @('CX_DFT_ViewUpdate','CX_DFT_ChangeLinks','CX_DFT_TITLEBLOCK_3DSET',
                         'CX_DFT_Update3DBom','CX_save','CX_DFT_ViewClose')) {
            $hit = @($rows | Where-Object { $_.Name -eq $k })
            $v = if ($hit.Count -gt 0) { $hit[0].Priority } else { "MISS(不在命令表)" }
            Say ("    {0,-28} -> {1}" -f $k, $v)
        }
        Say "  边界：名字取 Mdl*Cmd.xml 的 <Identify>；EXEC_NATIVE 仅放行 CX_DFT_ / CX_ 前缀；"
        Say "        表外名字 crxedCommand 恒 runs=0 / sendStringToExecute 回 unknownCommand。"
    }
} else {
    Say "  命令索引 = MISS（文件不存在：$CommandIndex）"
}

# ---------- ③ 落点结论 ----------
Say ""
Say "=== 结论（26-11-13 取证）==="
Say "  ① 可行：MCP 服务作为「文件桥请求侧」读写 %TEMP% 文本 → 传 CRX 命令表内具名命令。"
Say "     零 COM / 零反射 / 零消息循环依赖，正合 CRX 桥铁律「桥只通讯、决策在外」。"
Say "  ② 禁止：把 CRX 桥改造成命名管道 + 工作线程直连 MCP（否决 85）。"
Say "  ③ 不成立：把任意界面命令名当万能通道；必须先查命令表与 CMDPRIORITY_MODAL。"
Say "  ★ 单写者：seq <= g_lastSeq 直接丢弃；本机 $($logs.Count) 座桥前缀各异、各自独立 seq。"
Say "     MCP 想接入 ⇒ 新建 caxa3dmcp_ 唯一前缀 + 自带 CRX（第五登记点），或把某座桥接管为唯一请求者。"

if ($SelfTest) {
    if ($logs.Count -eq 0) { Say "SELFTEST     = PASS（坏样本被判 MISS）" }
    else                   { Say "SELFTEST     = FAIL（坏样本竟读到 $($logs.Count) 个日志）" }
}

$out | ForEach-Object { Write-Output $_ }
