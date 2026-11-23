# caxa3dmcp_bridge 编译脚本（Release x64 -> .crx）
# 用法：powershell -NoProfile -ExecutionPolicy Bypass -File "C:\code\ICAPI\caxa3dmcp\Bridge\build.ps1"
param(
    [switch]$NoPause
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$proj = Join-Path $root 'caxa3dmcp_bridge.vcxproj'

function Find-MSBuild {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path $vswhere) {
        $p = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' 2>$null
        if ($p) { return @($p)[0] }
    }
    foreach ($c in @(
        'C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe',
        'C:\Program Files\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe',
        'C:\Program Files\Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin\MSBuild.exe',
        'C:\Program Files (x86)\Microsoft Visual Studio\2019\Community\MSBuild\Current\Bin\MSBuild.exe')) {
        if (Test-Path $c) { return $c }
    }
    return $null
}

$msb = Find-MSBuild
if (-not $msb) { Write-Host '[ERROR] MSBuild not found (VS C++ workload required)' -ForegroundColor Red; exit 1 }

Write-Host "MSBuild: $msb"

# 26-11-13：本机 WorkBuddy 会话会同时注入 HTTP_PROXY / http_proxy（HTTPS/ALL 同族）。
# MSBuild 启动 CL.exe 时用 ProcessStartInfo.EnvironmentVariables（大小写不敏感字典）
# 枚举到两个同名键即抛 MSB6001「已添加了项」，与 C++ 代码无关。这里显式构造一份
# 去重后的环境字典再启动 MSBuild，CL.exe 继承干净环境；换机器/无代理也不受影响。
$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName  = $msb
$psi.Arguments = '"' + $proj + '" /p:Configuration=Release /p:Platform=x64 /t:Rebuild /v:minimal /nologo'
$psi.UseShellExecute = $false
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError  = $true
$psi.WorkingDirectory = $root
try   { $dstEnv = $psi.EnvironmentVariables }   # .NET Framework / PS5.1
catch { $dstEnv = $psi.Environment }            # .NET Core / PS7
$dstEnv.Clear()
$srcEnv = [Environment]::GetEnvironmentVariables('Process')
foreach ($k in $srcEnv.Keys) {
    $name = [string]$k
    if ($name -match '(?i)^(http|https|all)_proxy$') { continue }   # 代理变量一律不带进编译
    if (-not $dstEnv.ContainsKey($name)) { $dstEnv.Add($name, [string]$srcEnv[$k]) }  # 大小写重复键只留一个
}
$proc = New-Object System.Diagnostics.Process
$proc.StartInfo = $psi
$proc.Start() | Out-Null
$stdout = $proc.StandardOutput.ReadToEnd()
$stderr = $proc.StandardError.ReadToEnd()
$proc.WaitForExit()
if ($stdout) { Write-Host $stdout }
if ($stderr) { Write-Host $stderr }
$buildExit = $proc.ExitCode
if ($buildExit -ne 0) { Write-Host '[ERROR] CRX build failed' -ForegroundColor Red; exit $buildExit }

$out = Join-Path $root 'bin\caxa3dmcp_bridge.crx'
if (-not (Test-Path $out)) { Write-Host '[ERROR] bin\caxa3dmcp_bridge.crx not produced' -ForegroundColor Red; exit 1 }
$item = Get-Item $out
Write-Host ("[OK] {0}  {1} B" -f $out, $item.Length) -ForegroundColor Green
$sha = (Get-FileHash -Algorithm SHA256 $out).Hash
Write-Host ("[SHA256] {0}" -f $sha) -ForegroundColor Green
if (-not $NoPause -and $Host.Name -eq 'ConsoleHost') { }
