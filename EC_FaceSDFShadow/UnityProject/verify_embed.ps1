# 验证 DLL 里嵌的 bundle 就是 Resources/ 里那份。
# 手法:把两者都转成 hex 串,取 bundle 三处 48 字节切片作为指纹在 DLL hex 里找。
# 为什么不用反射 GetManifestResourceStream:加载程序集需要 Unity/BepInEx 依赖,
# 在裸 PowerShell 里必然失败;字节指纹与依赖无关,且能同时证明"没被压缩/改写"。
chcp 65001 > $null
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)

$pluginDir = Split-Path -Parent $PSScriptRoot
$repoRoot  = Split-Path -Parent (Split-Path -Parent $pluginDir)
$Dll    = Join-Path $repoRoot 'bin/BepInEx/plugins/EC_FaceSDFShadow/EC_FaceSDFShadow.dll'
$Bundle = Join-Path $pluginDir 'Resources/ec_facesdf.unity3d'

if (-not (Test-Path $Dll))    { Write-Host "FAIL: DLL 不存在 $Dll";    exit 1 }
if (-not (Test-Path $Bundle)) { Write-Host "FAIL: bundle 不存在 $Bundle"; exit 1 }

$dllBytes    = [System.IO.File]::ReadAllBytes($Dll)
$bundleBytes = [System.IO.File]::ReadAllBytes($Bundle)

Write-Host ("DLL    : {0} bytes" -f $dllBytes.Length)
Write-Host ("bundle : {0} bytes" -f $bundleBytes.Length)

$dllHex = [System.BitConverter]::ToString($dllBytes) -replace '-', ''

# 取三处指纹:头部(跳过 UnityFS 魔数附近)、中部、尾部。全部命中才算嵌对。
$probes = @(
    @{ Name = 'head'; Off = 256 },
    @{ Name = 'mid';  Off = [int]($bundleBytes.Length / 2) },
    @{ Name = 'tail'; Off = $bundleBytes.Length - 48 }
)

$allHit = $true
foreach ($p in $probes) {
    $slice = New-Object byte[] 48
    [System.Array]::Copy($bundleBytes, $p.Off, $slice, 0, 48)
    $sliceHex = [System.BitConverter]::ToString($slice) -replace '-', ''
    $hit = $dllHex.IndexOf($sliceHex) -ge 0
    if (-not $hit) { $allHit = $false }
    Write-Host ("  probe {0,-5} @off {1,-6} -> {2}" -f $p.Name, $p.Off, $(if ($hit) { 'HIT' } else { 'MISS' }))
}

if ($allHit) {
    Write-Host "PASS: DLL 内嵌的 bundle 与 Resources/ 逐字节一致"
    exit 0
} else {
    Write-Host "FAIL: DLL 里找不到该 bundle 的字节 —— 嵌的是旧版本"
    exit 1
}
