# Pack the blog template into payload.zip, which build.bat then embeds into the exe.
# Keep this file pure ASCII (PowerShell 5.1 reads non-BOM files as ANSI).
param(
  [Parameter(Mandatory = $true)][string]$Root,
  [Parameter(Mandatory = $true)][string]$Out
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

# Resolve -Out to a full path: Split-Path -Parent returns "" for a bare filename
if ([System.IO.Path]::IsPathRooted($Out)) { $zipPath = $Out }
else { $zipPath = Join-Path (Get-Location).Path $Out }

function Fail($msg) {
  Write-Output ("[ERROR] " + $msg)
  exit 1
}

if (-not (Test-Path (Join-Path $Root 'src\build.js'))) {
  Fail ("blog template not found under " + $Root)
}

$stage = Join-Path $env:TEMP 'garden-payload-stage'
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Path $stage | Out-Null

# directories of the template
foreach ($d in @('src', 'assets', 'content', 'blog-uploader', '.github')) {
  $src = Join-Path $Root $d
  if (Test-Path $src) { Copy-Item -LiteralPath $src -Destination (Join-Path $stage $d) -Recurse -Force }
}

# files at repo root（README 单独处理：用户仓库里放「你的博客仓库」说明，而不是项目介绍）
foreach ($f in @('site.config.json', 'package.json', '.gitignore')) {
  $p = Join-Path $Root $f
  if (Test-Path $p) { Copy-Item -LiteralPath $p -Destination (Join-Path $stage $f) -Force }
}

# 用户博客仓库 / 笔记库用的 README（docs/vault-README.md），缺失时退回项目 README
$vaultReadme = Join-Path $Root 'docs\vault-README.md'
$readmeSrc = if (Test-Path $vaultReadme) { $vaultReadme } else { Join-Path $Root 'README.md' }
if (Test-Path $readmeSrc) { Copy-Item -LiteralPath $readmeSrc -Destination (Join-Path $stage 'README.md') -Force }

# user blog repos don't need the deployer's own release workflow
$rel = Join-Path (Join-Path $stage '.github') 'workflows\release.yml'
if (Test-Path $rel) { Remove-Item $rel -Force }

if (-not (Test-Path (Join-Path $stage 'src\build.js'))) { Fail 'staging failed: src\build.js missing' }

if (Test-Path $zipPath) { Remove-Item $zipPath -Force }

# Preferred: ZipFile with UTF-8 entry names, so Chinese note filenames survive.
$done = $false
try {
  [void][System.Reflection.Assembly]::LoadWithPartialName('System.IO.Compression.FileSystem')
  $utf8 = New-Object System.Text.UTF8Encoding($false)
  [System.IO.Compression.ZipFile]::CreateFromDirectory(
    $stage, $zipPath,
    [System.IO.Compression.CompressionLevel]::Optimal, $false, $utf8)
  $done = $true
} catch {
  Write-Output ("[warn] ZipFile failed: " + $_.Exception.Message)
}

# Fallback: Compress-Archive (also available on Windows 10 / 11)
if (-not $done) {
  try {
    Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zipPath -Force
    $done = $true
  } catch {
    Fail ("Compress-Archive failed: " + $_.Exception.Message)
  }
}

Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue

if (-not (Test-Path $zipPath)) { Fail 'payload.zip was not created' }
$len = (Get-Item -LiteralPath $zipPath).Length
if ($len -lt 1000) { Fail ("payload.zip looks too small: " + $len + " bytes") }
Write-Output ("Payload: " + $len + " bytes")
exit 0
