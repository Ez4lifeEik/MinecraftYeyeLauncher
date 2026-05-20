#Requires -Version 5.1
<#
.SYNOPSIS
  给整合包 manifest.json 生成 RSA-SHA256 分离签名（manifest.json.sig），
  供启动器的 Launcher:ManifestPublicKey 校验使用（H2 修复）。

.DESCRIPTION
  工作流：
    1) 首次运行带 -GenerateKey：生成 RSA 私钥(arclight-manifest-private.pem) +
       打印 base64 公钥（粘到启动器 appsettings.json 的 Launcher:ManifestPublicKey）。
       私钥务必保密、加入 .gitignore，绝不要提交到任何仓库。
    2) 每次更新 manifest 后运行（不带 -GenerateKey）：在 manifest.json 同目录生成
       manifest.json.sig，然后把 manifest.json + manifest.json.sig 一起推到 arclight-modpack。

  依赖 openssl（Git for Windows 自带，路径通常 C:\Program Files\Git\usr\bin\openssl.exe）。

.EXAMPLE
  .\sign-manifest.ps1 -GenerateKey
  .\sign-manifest.ps1 -ManifestPath .\manifest.json
#>
param(
    [string]$ManifestPath = ".\manifest.json",
    [string]$PrivateKey   = ".\arclight-manifest-private.pem",
    [switch]$GenerateKey
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Resolve-OpenSsl {
    $cmd = Get-Command openssl -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    foreach ($p in @(
        "C:\Program Files\Git\usr\bin\openssl.exe",
        "C:\Program Files\Git\mingw64\bin\openssl.exe")) {
        if (Test-Path $p) { return $p }
    }
    throw "未找到 openssl。请安装 Git for Windows 或 OpenSSL 后重试。"
}

$ssl = Resolve-OpenSsl
Write-Host "openssl: $ssl"

if ($GenerateKey) {
    if (Test-Path $PrivateKey) { throw "私钥已存在：$PrivateKey（不会覆盖）。" }
    & $ssl genpkey -algorithm RSA -pkeyopt rsa_keygen_bits:3072 -out $PrivateKey
    if ($LASTEXITCODE -ne 0) { throw "生成私钥失败" }

    # 导出 SubjectPublicKeyInfo (DER) 并转 base64，单行，供 appsettings 使用
    $derPath = [System.IO.Path]::GetTempFileName()
    & $ssl rsa -in $PrivateKey -pubout -outform DER -out $derPath
    if ($LASTEXITCODE -ne 0) { throw "导出公钥失败" }
    $pubB64 = [Convert]::ToBase64String([System.IO.File]::ReadAllBytes($derPath))
    Remove-Item $derPath -Force

    Write-Host ""
    Write-Host "私钥已生成（请保密，加入 .gitignore）：$PrivateKey" -ForegroundColor Yellow
    Write-Host ""
    Write-Host "把下面这行 base64 公钥填入启动器 appsettings.json 的 Launcher:ManifestPublicKey：" -ForegroundColor Cyan
    Write-Host $pubB64 -ForegroundColor Green
    Write-Host ""
    return
}

if (-not (Test-Path $ManifestPath)) { throw "找不到 manifest：$ManifestPath" }
if (-not (Test-Path $PrivateKey))   { throw "找不到私钥：$PrivateKey（先用 -GenerateKey 生成）。" }

$sigBin = [System.IO.Path]::GetTempFileName()
& $ssl dgst -sha256 -sign $PrivateKey -out $sigBin $ManifestPath
if ($LASTEXITCODE -ne 0) { throw "签名失败" }

$sigB64  = [Convert]::ToBase64String([System.IO.File]::ReadAllBytes($sigBin))
Remove-Item $sigBin -Force

$sigPath = "$ManifestPath.sig"
Set-Content -Path $sigPath -Value $sigB64 -Encoding ascii -NoNewline

Write-Host "已生成签名：$sigPath" -ForegroundColor Green
Write-Host "请把 manifest.json 和 manifest.json.sig 一起推送到 arclight-modpack。" -ForegroundColor Cyan
