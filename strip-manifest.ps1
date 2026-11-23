param(
    [Parameter(Mandatory = $true)]
    [Alias('ManifestPath')]
    [string]$Path
)

# 剥离 mt.exe 为「内嵌 interop 类型」生成的 clrClass / clrSurrogate。
#
# 原因：多个 .NET Add-in 各自内嵌同一套 interop 类型库时，mt.exe 会为这些类型生成
# 不同 CLSID 但相同 progid（完全限定类型名）。多个 Add-in 共用同一个 SxS 激活上下文时
# 合并失败 —— 「应用程序并行配置不正确」→ CAXA 直接起不来。
# 免注册 COM 只需要本插件自己的 clrClass（Addin 类），interop 的委托/枚举不会被 COM 激活，
# 剥离是安全的。
#
# 关键：mt.exe 产出的清单带 xmlns="urn:schemas-microsoft-com:asm.v1"，
# 必须用 local-name() 忽略命名空间，否则子节点查询为空、interop 条目留在原地 → CAXA 起不来。
#
# 编译后四步的第 2 步，由 csproj 的 EmbedSxsManifestAndResign 目标自动调用。
# 本文件必须存成「单一 UTF-8 BOM」。

if (-not (Test-Path $Path)) {
    Write-Host "strip-manifest: file not found: $Path" -ForegroundColor Red
    exit 1
}

[xml]$m = Get-Content $Path -Raw -Encoding UTF8
$removed = 0

foreach ($tag in @('clrClass', 'clrSurrogate')) {
    $xpath = "//*[local-name()='$tag']"
    $nodes = $m.SelectNodes($xpath)
    foreach ($n in $nodes) {
        $nameAttr = $n.GetAttribute('name')
        if ($nameAttr -and $nameAttr.StartsWith('interop.', [System.StringComparison]::Ordinal)) {
            [void]$n.ParentNode.RemoveChild($n)
            $removed++
        }
    }
}

# 去掉默认 xmlns，避免后续 mt.exe / sn.exe 步骤处理命名空间时出错
$ns = New-Object System.Xml.XmlNamespaceManager($m.NameTable)
$ns.AddNamespace('asm', 'urn:schemas-microsoft-com:asm.v1')
$root = $m.SelectSingleNode('//asm:assembly', $ns)
if ($root) { $root.RemoveAttribute('xmlns') }

$m.Save($Path)

# 自检：必须仍保留恰好 1 条自身 clrClass，否则等于把插件自己剥掉了
[xml]$check = Get-Content $Path -Raw -Encoding UTF8
$left = @($check.SelectNodes("//*[local-name()='clrClass']"))
Write-Host "strip-manifest: removed $removed interop entries; clrClass left = $($left.Count)"
if ($left.Count -ne 1) {
    Write-Host "strip-manifest: [WARN] expected exactly 1 clrClass (the Addin class), got $($left.Count)." -ForegroundColor Yellow
    Write-Host "                 Check that Addin.cs is the only [ComVisible(true)] type." -ForegroundColor Yellow
}
