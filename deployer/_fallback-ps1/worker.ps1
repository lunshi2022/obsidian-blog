# 数字花园部署引擎（无界面，可单独运行）
# 用法: powershell -NoProfile -ExecutionPolicy Bypass -File worker.ps1 -Config <config.json>
param(
  [Parameter(Mandatory = $true)][string]$Config,
  [switch]$DetectOnly,
  # 只跑指定步骤（调试用），例如 -OnlySteps 0,1,3
  [string]$OnlySteps = ''
)

$ErrorActionPreference = 'Continue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

# PowerShell 5.1 默认按系统代码页（中文是 GBK）解码外部程序输出，
# Node 的输出是 UTF-8，不统一就会变成乱码
try { [Console]::OutputEncoding = [Text.Encoding]::UTF8 } catch { }
try { $OutputEncoding = [Text.Encoding]::UTF8 } catch { }

if (-not (Test-Path $Config)) { Write-Host "找不到配置文件: $Config"; exit 2 }
$Cfg = Get-Content -Raw -Encoding UTF8 $Config | ConvertFrom-Json

$Script:LogFile = $Cfg.LogFile
$Script:DryRun = [bool]$Cfg.DryRun
$Script:Tmp = Join-Path $env:TEMP ('garden-deploy-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Force -Path $Script:Tmp | Out-Null

$Script:Steps = @(
  '环境检测'
  '准备部署目录'
  '安装 Node 与 Git'
  '构建站点'
  '创建仓库并推送'
  '安装 Obsidian 与插件'
)

# ---------------------------------------------------------------- 输出协议

function Out-Line {
  param([string]$Type, [string[]]$Fields)
  $line = $Type + "`t" + ($Fields -join "`t")
  [IO.File]::AppendAllText($Script:LogFile, $line + "`n", (New-Object Text.UTF8Encoding($false)))
}

function Log {
  param([string]$Msg, [string]$Cls = '')
  Out-Line 'LOG' @($Cls, ($Msg -replace "[`t`r`n]+", ' '))
}

function Set-Step {
  param([int]$Index, [string]$State, [string]$Sub = '')
  Out-Line 'STEP' @($Index, $State, ($Sub -replace "[`t`r`n]+", ' '))
}

function Set-Progress {
  param([double]$Pct, [string]$Label = '')
  Out-Line 'PROG' @([Math]::Round($Pct, 1), ($Label -replace "[`t`r`n]+", ' '))
}

function Set-Env {
  param([string]$Name, [string]$State, [string]$Version, [string]$Path)
  Out-Line 'ENV' @($Name, $State, ($Version -replace "[`t`r`n]+", ' '), ($Path -replace "[`t`r`n]+", ' '))
}

# PS 5.1 的 Set-Content -Encoding UTF8 会写 BOM，Node 的 JSON.parse 会因此报错
function Write-Json {
  param([string]$Path, $Object, [int]$Depth = 6)
  # 必须用 -InputObject：走管道会把单元素数组展平成标量
  $text = ConvertTo-Json -InputObject $Object -Depth $Depth
  [IO.File]::WriteAllText($Path, $text, (New-Object Text.UTF8Encoding($false)))
}

function Write-TextFile {
  param([string]$Path, [string]$Text)
  $dir = Split-Path $Path -Parent
  if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
  [IO.File]::WriteAllText($Path, $Text, (New-Object Text.UTF8Encoding($false)))
}

function Finish {
  param([bool]$Ok, [string]$Message)
  Out-Line 'DONE' @($(if ($Ok) { '1' } else { '0' }), ($Message -replace "[`t`r`n]+", ' '))
}

# 步内进度：把 step 内的 0-100 映射到全局区间
function Step-Progress {
  param([int]$Index, [double]$Inner, [string]$Label)
  $base = $Index * 100.0 / $Script:Steps.Count
  Set-Progress ($base + ($Inner / 100.0) * (100.0 / $Script:Steps.Count)) $Label
}

# ---------------------------------------------------------------- 基础工具

function Update-SessionPath {
  # 合并（而不是覆盖）当前 PATH 与注册表里的 Machine/User PATH，
  # 否则会丢失调用方进程额外注入的目录。装完 Node/Git 后需要重新调用才生效。
  $parts = @()
  $parts += ($env:Path -split ';')
  $parts += ([Environment]::GetEnvironmentVariable('Path', 'Machine') -split ';')
  $parts += ([Environment]::GetEnvironmentVariable('Path', 'User') -split ';')
  $seen = @{}
  $out = @()
  foreach ($p in $parts) {
    if (-not $p) { continue }
    $key = $p.Trim().TrimEnd('\').ToLower()
    if (-not $key -or $seen.ContainsKey($key)) { continue }
    $seen[$key] = $true
    $out += $p.Trim()
  }
  $env:Path = ($out -join ';')
}

# 从注册表读安装目录（winget / MSI 装完后即使 PATH 未刷新也能找到）
function Get-RegInstallPath {
  param([string[]]$Keys)
  foreach ($k in $Keys) {
    try {
      if (-not (Test-Path $k)) { continue }
      $v = (Get-ItemProperty $k -ErrorAction SilentlyContinue).InstallPath
      if ($v -and (Test-Path $v)) { return $v }
    } catch { }
  }
  return $null
}

function Join-Safe {
  param([string]$Base, [string]$Child)
  if (-not $Base) { return $null }
  return (Join-Path $Base $Child)
}

function Find-Exe {
  param([string]$Name, [string[]]$Candidates = @())
  $cmd = Get-Command $Name -ErrorAction SilentlyContinue
  if ($cmd -and $cmd.Source) { return $cmd.Source }
  foreach ($c in $Candidates) {
    if ($c -and (Test-Path $c)) { return $c }
  }
  return $null
}

function Get-NodeInfo {
  $reg = Get-RegInstallPath @('HKLM:\SOFTWARE\Node.js', 'HKLM:\SOFTWARE\WOW6432Node\Node.js', 'HKCU:\SOFTWARE\Node.js')
  $exe = Find-Exe 'node' @(
    (Join-Safe $env:ProgramFiles 'nodejs\node.exe')
    (Join-Safe ${env:ProgramFiles(x86)} 'nodejs\node.exe')
    (Join-Safe $env:LOCALAPPDATA 'Programs\nodejs\node.exe')
    (Join-Safe $reg 'node.exe')
  )
  if (-not $exe) { return @{ ok = $false; ver = ''; path = '' } }
  $v = ''
  try { $v = (& $exe --version 2>$null | Select-Object -First 1) } catch { }
  return @{ ok = $true; ver = ("$v").Trim(); path = $exe }
}

function Get-GitInfo {
  $reg = Get-RegInstallPath @('HKLM:\SOFTWARE\GitForWindows', 'HKLM:\SOFTWARE\WOW6432Node\GitForWindows', 'HKCU:\SOFTWARE\GitForWindows')
  $exe = Find-Exe 'git' @(
    (Join-Safe $env:ProgramFiles 'Git\cmd\git.exe')
    (Join-Safe ${env:ProgramFiles(x86)} 'Git\cmd\git.exe')
    (Join-Safe $env:LOCALAPPDATA 'Programs\Git\cmd\git.exe')
    (Join-Safe $reg 'cmd\git.exe')
  )
  if (-not $exe) { return @{ ok = $false; ver = ''; path = '' } }
  $v = ''
  try { $v = (& $exe --version 2>$null | Select-Object -First 1) } catch { }
  return @{ ok = $true; ver = ("$v").Trim(); path = $exe }
}

function Get-ObsidianInfo {
  $exe = $null
  foreach ($c in @(
      (Join-Safe $env:LOCALAPPDATA 'Obsidian\Obsidian.exe')
      (Join-Safe $env:ProgramFiles 'Obsidian\Obsidian.exe')
      (Join-Safe ${env:ProgramFiles(x86)} 'Obsidian\Obsidian.exe')
    )) {
    if ($c -and (Test-Path $c)) { $exe = $c; break }
  }
  if (-not $exe) {
    $keys = @(
      'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*'
      'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*'
      'HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*'
    )
    foreach ($k in $keys) {
      $hit = Get-ItemProperty $k -ErrorAction SilentlyContinue |
        Where-Object { $_.DisplayName -like 'Obsidian*' -and $_.DisplayIcon } |
        Select-Object -First 1
      if ($hit) {
        $exe = ($hit.DisplayIcon -split ',')[0].Trim('"')
        if (-not (Test-Path $exe)) { $exe = $null }
        if ($exe) { break }
      }
    }
  }
  if (-not $exe) { return @{ ok = $false; ver = ''; path = '' } }
  $ver = ''
  try { $ver = (Get-Item $exe).VersionInfo.ProductVersion } catch { }
  return @{ ok = $true; ver = "v$ver"; path = $exe }
}

function Invoke-Silent {
  param([string]$File, [string[]]$Args, [string]$Label)
  Log ("  > " + (Split-Path $File -Leaf) + " " + ($Args -join ' ')) 'cmd'
  $p = Start-Process -FilePath $File -ArgumentList $Args -Wait -PassThru -NoNewWindow `
    -RedirectStandardOutput (Join-Path $Script:Tmp 'out.txt') `
    -RedirectStandardError (Join-Path $Script:Tmp 'err.txt')
  return $p.ExitCode
}

function Get-FileWithProgress {
  param([string]$Url, [string]$Dest, [string]$Label, [int]$StepIndex, [double]$From, [double]$To)
  Log "  下载 $Label" 'cmd'
  $req = [Net.HttpWebRequest]::Create($Url)
  $req.UserAgent = 'garden-deployer/1.0'
  $req.Timeout = 30000
  $req.ReadWriteTimeout = 300000
  $resp = $req.GetResponse()
  $total = $resp.ContentLength
  $in = $resp.GetResponseStream()
  $out = [IO.File]::Create($Dest)
  try {
    $buf = New-Object byte[] 131072
    $done = [long]0
    $last = [DateTime]::MinValue
    while (($read = $in.Read($buf, 0, $buf.Length)) -gt 0) {
      $out.Write($buf, 0, $read)
      $done += $read
      if ($total -gt 0) {
        $now = [DateTime]::Now
        if (($now - $last).TotalMilliseconds -gt 180) {
          $last = $now
          $ratio = $done / $total
          $mb = [Math]::Round($done / 1MB, 1)
          $mbAll = [Math]::Round($total / 1MB, 1)
          Step-Progress $StepIndex ($From + ($To - $From) * $ratio) "$Label  $mb/$mbAll MB"
        }
      }
    }
  } finally {
    $out.Close(); $in.Close(); $resp.Close()
  }
  Step-Progress $StepIndex $To "$Label 完成"
  return $true
}

# ---------------------------------------------------------------- 安装器

function Test-Winget {
  return [bool](Get-Command winget -ErrorAction SilentlyContinue)
}

function Install-ViaWinget {
  param([string]$Id, [string]$Label, [int]$StepIndex)
  if (-not (Test-Winget)) { return $false }
  Log "  尝试 winget 安装 $Label ($Id)" 'cmd'
  Step-Progress $StepIndex 20 "winget 安装 $Label…"
  $out = Join-Path $Script:Tmp 'winget.out'
  $err = Join-Path $Script:Tmp 'winget.err'
  $args = @('install', '--id', $Id, '-e', '--silent', '--scope', 'machine',
    '--accept-package-agreements', '--accept-source-agreements', '--disable-interactivity')
  $p = Start-Process -FilePath 'winget' -ArgumentList $args -Wait -PassThru -NoNewWindow `
    -RedirectStandardOutput $out -RedirectStandardError $err
  $detail = ''
  if (Test-Path $out) { $detail += (Get-Content $out -Raw -Encoding UTF8) }
  if (Test-Path $err) { $detail += (Get-Content $err -Raw -Encoding UTF8) }
  if ($p.ExitCode -eq 0) { Log "  winget 安装成功" 'ok'; return $true }
  # 0x8A150061 = 已安装
  if ($p.ExitCode -eq -1978335199 -or $p.ExitCode -eq -1978335189) {
    Log "  winget 报告已安装" 'warn'
    return $true
  }
  Log "  winget 失败 (exit $($p.ExitCode))，改用离线安装包" 'warn'
  return $false
}

function Install-NodePackage {
  param([int]$StepIndex)
  Log "查询 Node.js LTS 版本…" 'cmd'
  $idx = Invoke-RestMethod 'https://nodejs.org/dist/index.json' -TimeoutSec 30
  $lts = $idx | Where-Object { $_.lts } | Select-Object -First 1
  if (-not $lts) { throw '无法获取 Node.js 版本列表' }
  $ver = $lts.version
  $msi = "node-$ver-x64.msi"
  $url = "https://nodejs.org/dist/$ver/$msi"
  $dest = Join-Path $Script:Tmp $msi
  Get-FileWithProgress $url $dest "Node.js $ver" $StepIndex 30 70 | Out-Null
  if ($Script:DryRun) { Log "  [模拟] 跳过安装 $msi" 'warn'; return }
  $code = Invoke-Silent 'msiexec.exe' @('/i', "`"$dest`"", '/qn', '/norestart') 'Node.js'
  if ($code -ne 0 -and $code -ne 3010) { throw "Node.js 安装失败 (msiexec exit $code)" }
  Log "Node.js $ver 安装完成" 'ok'
}

function Install-GitPackage {
  param([int]$StepIndex)
  Log "查询 Git for Windows 最新版…" 'cmd'
  $rel = Invoke-RestMethod 'https://api.github.com/repos/git-for-windows/git/releases/latest' `
    -Headers @{ 'User-Agent' = 'garden-deployer' } -TimeoutSec 30
  $asset = $rel.assets | Where-Object { $_.name -match '64-bit\.exe$' -and $_.name -notmatch 'Portable|Admin' } |
    Select-Object -First 1
  if (-not $asset) { throw '无法定位 Git 安装包' }
  $dest = Join-Path $Script:Tmp $asset.name
  Get-FileWithProgress $asset.browser_download_url $dest "Git $($rel.tag_name)" $StepIndex 72 96 | Out-Null
  if ($Script:DryRun) { Log "  [模拟] 跳过安装 $($asset.name)" 'warn'; return }
  $code = Invoke-Silent $dest @('/VERYSILENT', '/NORESTART', '/SUPPRESSMSGBOXES', '/SP-', '/CLOSEAPPLICATIONS') 'Git'
  if ($code -ne 0) { throw "Git 安装失败 (exit $code)" }
  Log "Git $($rel.tag_name) 安装完成" 'ok'
}

function Install-ObsidianPackage {
  param([int]$StepIndex)
  Log "查询 Obsidian 最新版…" 'cmd'
  $rel = Invoke-RestMethod 'https://api.github.com/repos/obsidianmd/obsidian-releases/releases/latest' `
    -Headers @{ 'User-Agent' = 'garden-deployer' } -TimeoutSec 30
  $asset = $rel.assets | Where-Object { $_.name -match '^Obsidian-.*\.exe$' } | Select-Object -First 1
  if (-not $asset) { throw '无法定位 Obsidian 安装包' }
  $dest = Join-Path $Script:Tmp $asset.name
  Get-FileWithProgress $asset.browser_download_url $dest "Obsidian $($rel.tag_name)" $StepIndex 40 90 | Out-Null
  if ($Script:DryRun) { Log "  [模拟] 跳过安装 $($asset.name)" 'warn'; return }
  $code = Invoke-Silent $dest @('/S') 'Obsidian'
  if ($code -ne 0) { throw "Obsidian 安装失败 (exit $code)" }
  Log "Obsidian 安装完成" 'ok'
}

# ---------------------------------------------------------------- 步骤实现

function Step-Environment {
  Set-Step 0 'active' '读取系统信息…'
  Log '检查运行环境' 'hi'
  $os = Get-CimInstance Win32_OperatingSystem -ErrorAction SilentlyContinue
  $osName = if ($os) { "$($os.Caption) $($os.Version)" } else { [Environment]::OSVersion.VersionString }
  Log "  系统：$osName"
  Log "  PowerShell：$($PSVersionTable.PSVersion)"

  $admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
  ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
  if ($admin) { Log '  管理员权限：是' 'ok' } else { Log '  管理员权限：否（安装系统组件可能失败）' 'warn' }

  $target = $Cfg.Target
  if ($target) {
    $drive = (Split-Path $target -Qualifier)
    if ($drive) {
      try {
        $free = (Get-PSDrive -Name $drive.TrimEnd(':') -ErrorAction Stop).Free / 1GB
        Log ("  磁盘剩余：" + [Math]::Round($free, 1) + " GB")
        if ($free -lt 1) { Log '  磁盘空间不足 1GB' 'warn' }
      } catch { }
    }
  }

  try {
    Invoke-RestMethod 'https://api.github.com/zen' -TimeoutSec 15 -Headers @{ 'User-Agent' = 'garden-deployer' } | Out-Null
    Log '  网络：可访问 GitHub' 'ok'
  } catch {
    Log '  网络：无法访问 GitHub API，后续推送可能失败' 'warn'
  }

  Update-SessionPath
  $n = Get-NodeInfo; $g = Get-GitInfo; $o = Get-ObsidianInfo
  Set-Env 'node' $(if ($n.ok) { 'ok' } else { 'miss' }) $n.ver $n.path
  Set-Env 'git' $(if ($g.ok) { 'ok' } else { 'miss' }) $g.ver $g.path
  Set-Env 'obsidian' $(if ($o.ok) { 'ok' } else { 'miss' }) $o.ver $o.path
  Log ("  Node：" + $(if ($n.ok) { "$($n.ver)" } else { '未安装' })) $(if ($n.ok) { 'ok' } else { 'warn' })
  Log ("  Git：" + $(if ($g.ok) { "$($g.ver)" } else { '未安装' })) $(if ($g.ok) { 'ok' } else { 'warn' })
  Log ("  Obsidian：" + $(if ($o.ok) { "$($o.ver)" } else { '未安装' })) $(if ($o.ok) { 'ok' } else { 'warn' })

  # 立刻校验令牌，拿到真实用户名，后面的站点地址和建仓都依赖它
  if ($Cfg.Token) {
    try {
      $me = Invoke-Gh 'GET' '/user' $null $Cfg.Token
      $Script:Owner = $me.login
      Log "  GitHub 身份：$($me.login)" 'ok'
    } catch {
      throw "访问令牌无效或无法连接 GitHub：$($_.Exception.Message)"
    }
  } else {
    $Script:Owner = $Cfg.Owner
  }

  Set-Step 0 'done' '环境检测完成'
  Step-Progress 0 100 '环境检测完成'
}

function Step-PrepareTarget {
  Set-Step 1 'active' '复制站点模板…'
  Log '准备部署目录' 'hi'
  $target = $Cfg.Target
  if (-not (Test-Path $target)) {
    New-Item -ItemType Directory -Force -Path $target | Out-Null
    Log "  已创建目录：$target" 'ok'
  } else {
    Log "  目录已存在：$target"
  }

  $src = $Cfg.PayloadDir
  if (-not (Test-Path (Join-Path $src 'src\build.js'))) {
    throw "模板目录无效（缺少 src\build.js）：$src"
  }
  if ([IO.Path]::GetFullPath($src).TrimEnd('\') -eq [IO.Path]::GetFullPath($target).TrimEnd('\')) {
    Log '  模板与目标相同，跳过复制（就地部署）' 'warn'
  } else {
    $items = @('src', 'assets', 'content', 'blog-uploader', '.github', 'site.config.json', 'package.json', 'README.md', '.gitignore')
    $i = 0
    foreach ($it in $items) {
      $s = Join-Path $src $it
      $d = Join-Path $target $it
      $i++
      Step-Progress 1 (10 + 70 * $i / $items.Count) "复制 $it"
      if (-not (Test-Path $s)) { Log "  跳过缺失项：$it" 'warn'; continue }
      if (Test-Path $s -PathType Container) {
        New-Item -ItemType Directory -Force -Path $d | Out-Null
        Copy-Item (Join-Path $s '*') $d -Recurse -Force
      } else {
        Copy-Item $s $d -Force
      }
    }
    Log '  站点模板复制完成' 'ok'
  }

  # 写站点配置
  $cfgFile = Join-Path $target 'site.config.json'
  if (Test-Path $cfgFile) {
    try {
      $site = Get-Content -Raw -Encoding UTF8 $cfgFile | ConvertFrom-Json
      $owner = if ($Script:Owner) { $Script:Owner } else { 'github-user' }
      $pagesUrl = "https://$owner.github.io/$($Cfg.Repo)/"
      if (-not $site.title) { $site.title = '我的数字花园' }
      $site.siteUrl = $pagesUrl
      if ($site.author -eq '你的名字' -or -not $site.author) { $site.author = $owner }
      Write-Json $cfgFile $site 5
      Log "  已写入站点地址：$pagesUrl" 'ok'
    } catch { Log "  站点配置更新失败：$($_.Exception.Message)" 'warn' }
  }

  Set-Step 1 'done' '目录就绪'
  Step-Progress 1 100 '目录就绪'
}

function Step-Dependencies {
  Set-Step 2 'active' '检测 Node 与 Git…'
  Log '安装依赖' 'hi'
  Update-SessionPath
  $needNode = -not (Get-NodeInfo).ok
  $needGit = -not (Get-GitInfo).ok

  if (-not $needNode -and -not $needGit) {
    Log '  Node 与 Git 均已安装，跳过' 'ok'
    Set-Step 2 'done' '依赖齐全'
    Step-Progress 2 100 '依赖齐全'
    return
  }

  if ($Script:DryRun) {
    $miss = @()
    if ($needNode) { $miss += 'Node.js' }
    if ($needGit) { $miss += 'Git' }
    Log ("  [模拟] 本应安装：" + ($miss -join '、')) 'warn'
    Set-Step 2 'done' '模拟跳过'
    Step-Progress 2 100 '模拟跳过'
    return
  }

  if ($needNode) {
    Step-Progress 2 10 '安装 Node.js…'
    if (-not (Install-ViaWinget 'OpenJS.NodeJS.LTS' 'Node.js LTS' 2)) {
      Install-NodePackage 2
    }
    Update-SessionPath
    $n = Get-NodeInfo
    if (-not $n.ok) { throw 'Node.js 安装后仍未检测到，请手动安装后重试' }
    Log "  Node 就绪：$($n.ver)" 'ok'
    Set-Env 'node' 'ok' $n.ver $n.path
  }

  if ($needGit) {
    Step-Progress 2 60 '安装 Git…'
    if (-not (Install-ViaWinget 'Git.Git' 'Git' 2)) {
      Install-GitPackage 2
    }
    Update-SessionPath
    $g = Get-GitInfo
    if (-not $g.ok) { throw 'Git 安装后仍未检测到，请手动安装后重试' }
    Log "  Git 就绪：$($g.ver)" 'ok'
    Set-Env 'git' 'ok' $g.ver $g.path
  }

  Set-Step 2 'done' '依赖安装完成'
  Step-Progress 2 100 '依赖安装完成'
}

function Step-Build {
  Set-Step 3 'active' '构建站点…'
  Log '构建站点' 'hi'
  $node = (Get-NodeInfo).path
  if (-not $node) { throw '找不到 node，无法构建' }
  if ($Script:DryRun) { Log '  [模拟] 跳过构建' 'warn'; Set-Step 3 'done' '模拟跳过'; return }

  Push-Location $Cfg.Target
  try {
    $output = & $node 'src/build.js' 2>&1 | Out-String
    $code = $LASTEXITCODE
  } finally { Pop-Location }

  foreach ($ln in ($output -split "`r?`n")) {
    if ($ln.Trim()) { Log "  $($ln.TrimEnd())" }
  }
  if ($code -ne 0) { throw "构建失败（exit $code）" }
  if (-not (Test-Path (Join-Path $Cfg.Target 'site\index.html'))) { throw '构建完成但没有生成 site\index.html' }
  Log '  站点构建成功' 'ok'
  Set-Step 3 'done' '构建完成'
  Step-Progress 3 100 '构建完成'
}

function Invoke-Gh {
  param([string]$Method, [string]$Path, $Body, [string]$Token)
  $headers = @{
    Authorization          = "Bearer $Token"
    'User-Agent'           = 'garden-deployer'
    Accept                 = 'application/vnd.github+json'
    'X-GitHub-Api-Version' = '2022-11-28'
  }
  $params = @{ Method = $Method; Uri = "https://api.github.com$Path"; Headers = $headers; TimeoutSec = 40 }
  if ($null -ne $Body) {
    $params.Body = ($Body | ConvertTo-Json -Depth 6 -Compress)
    $params.ContentType = 'application/json; charset=utf-8'
  }
  try {
    return Invoke-RestMethod @params
  } catch {
    $detail = ''
    if ($_.ErrorDetails -and $_.ErrorDetails.Message) { $detail = $_.ErrorDetails.Message }
    throw "GitHub API $Method $Path 失败: $($_.Exception.Message) $detail"
  }
}

function Step-GitHub {
  Set-Step 4 'active' '校验令牌…'
  Log '创建 GitHub 仓库' 'hi'

  if (-not $Cfg.Token) { throw '未提供访问令牌，无法创建仓库' }

  $owner = $Script:Owner
  if (-not $owner) {
    $me = Invoke-Gh 'GET' '/user' $null $Cfg.Token
    $owner = $me.login
    $Script:Owner = $owner
  }
  Log "  GitHub 账号：$owner" 'ok'
  if ($Cfg.Owner -and $Cfg.Owner -ne $owner) {
    Log "  注意：填写的用户名是 $($Cfg.Owner)，与令牌归属（$owner）不一致，以令牌为准" 'warn'
  }

  Step-Progress 4 25 '创建仓库…'
  $exists = $true
  try {
    Invoke-Gh 'GET' "/repos/$owner/$($Cfg.Repo)" $null $Cfg.Token | Out-Null
    Log "  仓库已存在：$owner/$($Cfg.Repo)" 'warn'
  } catch {
    $exists = $false
  }
  if (-not $exists) {
    Invoke-Gh 'POST' '/user/repos' @{
      name        = $Cfg.Repo
      description = '一个用 Obsidian 写作、自动发布到 GitHub Pages 的数字花园'
      private     = $false
      auto_init   = $false
      has_issues  = $true
    } $Cfg.Token | Out-Null
    Log "  仓库已创建：$owner/$($Cfg.Repo)" 'ok'
  }

  $remote = "https://github.com/$owner/$($Cfg.Repo).git"
  $git = (Get-GitInfo).path
  if (-not $git) { throw '找不到 git' }

  Step-Progress 4 45 '初始化本地仓库…'
  Push-Location $Cfg.Target
  try {
    if (-not (Test-Path '.git')) {
      & $git init -b main 2>&1 | Out-Null
      Log '  git init 完成'
    }
    & $git config user.name $owner
    & $git config user.email "$owner@users.noreply.github.com"
    & $git config core.autocrlf true

    $remotes = & $git remote
    if ($remotes -contains 'origin') { & $git remote set-url origin $remote } else { & $git remote add origin $remote }

    Step-Progress 4 60 '提交文件…'
    & $git add -A 2>&1 | Out-Null
    $st = & $git status --porcelain
    if ($st) {
      & $git commit -m 'deploy: 初始化数字花园' 2>&1 | Out-Null
      Log '  已提交初始版本'
    } else { Log '  没有需要提交的变更' 'warn' }

    if ($Script:DryRun) {
      Log '  [模拟] 跳过推送' 'warn'
    } else {
      Step-Progress 4 75 '推送到 GitHub…'
      $pushUrl = "https://x-access-token:$($Cfg.Token)@github.com/$owner/$($Cfg.Repo).git"
      $pushOut = & $git push $pushUrl 'HEAD:refs/heads/main' --force 2>&1 | Out-String
      if ($LASTEXITCODE -ne 0) {
        Log ($pushOut.Trim() -replace $Cfg.Token, '***') 'err'
        throw 'git push 失败'
      }
      Log '  推送成功' 'ok'

      # 把令牌存进 Windows 凭据管理器，之后插件推送不用再输
      try {
        & $git config --local credential.helper manager
        "protocol=https`nhost=github.com`nusername=$owner`npassword=$($Cfg.Token)`n" |
          & $git credential approve
        Log '  已保存凭据到 Windows 凭据管理器' 'ok'
      } catch { Log "  凭据保存失败（不影响本次部署）：$($_.Exception.Message)" 'warn' }
    }
  } finally { Pop-Location }

  # 开启 Pages
  Step-Progress 4 90 '开启 GitHub Pages…'
  if (-not $Script:DryRun) {
    $pagesOk = $false
    try {
      Invoke-Gh 'POST' "/repos/$owner/$($Cfg.Repo)/pages" @{ build_type = 'workflow' } $Cfg.Token | Out-Null
      $pagesOk = $true
    } catch {
      try {
        Invoke-Gh 'PUT' "/repos/$owner/$($Cfg.Repo)/pages" @{ build_type = 'workflow' } $Cfg.Token | Out-Null
        $pagesOk = $true
      } catch { Log "  Pages 自动开启失败：$($_.Exception.Message)" 'warn' }
    }
    if ($pagesOk) { Log '  GitHub Pages 已设为 GitHub Actions 发布' 'ok' }
  }

  $Script:PagesUrl = "https://$owner.github.io/$($Cfg.Repo)/"
  $Script:RepoUrl = "https://github.com/$owner/$($Cfg.Repo)"
  Set-Step 4 'done' '仓库已就绪'
  Step-Progress 4 100 '仓库已就绪'
}

function Step-Obsidian {
  Set-Step 5 'active' '检测 Obsidian…'
  Log '安装 Obsidian 与插件' 'hi'
  Update-SessionPath
  $o = Get-ObsidianInfo
  if (-not $o.ok) {
    Step-Progress 5 20 '安装 Obsidian…'
    if (-not (Install-ViaWinget 'Obsidian.Obsidian' 'Obsidian' 5)) {
      Install-ObsidianPackage 5
    }
    Update-SessionPath
    $o = Get-ObsidianInfo
    if (-not $o.ok) { Log '  Obsidian 未检测到，请手动安装后把插件复制进库' 'warn' }
    else { Log "  Obsidian 就绪：$($o.ver)" 'ok'; Set-Env 'obsidian' 'ok' $o.ver $o.path }
  } else { Log "  Obsidian 已安装：$($o.ver)" 'ok' }

  # 笔记库 = 部署目录（content/ 就是笔记所在的子目录）
  $vault = $Cfg.Target
  $pluginSrc = Join-Path $Cfg.Target 'blog-uploader'
  $pluginDst = Join-Path $vault '.obsidian\plugins\blog-uploader'

  Step-Progress 5 60 '安装上传插件…'
  if (-not (Test-Path (Join-Path $pluginSrc 'main.js'))) {
    Log '  找不到 blog-uploader 插件文件' 'warn'
  } else {
    New-Item -ItemType Directory -Force -Path $pluginDst | Out-Null
    Copy-Item (Join-Path $pluginSrc '*') $pluginDst -Recurse -Force

    # 预填插件设置：博客路径指向部署目录
    Write-Json (Join-Path $pluginDst 'data.json') @{
      blogPath      = $vault
      contentDir    = 'content'
      keepStructure = $true
      autoBuild     = $true
      autoPush      = $true
    }

    # 启用插件（写 community-plugins.json + 关闭安全模式）
    $cpFile = Join-Path $vault '.obsidian\community-plugins.json'
    $list = @()
    if (Test-Path $cpFile) {
      try {
        $parsed = Get-Content -Raw -Encoding UTF8 $cpFile | ConvertFrom-Json
        $list = @($parsed | Where-Object { $_ })
      } catch { $list = @() }
    }
    if ($list -notcontains 'blog-uploader') { $list += 'blog-uploader' }
    Write-Json $cpFile $list 3

    # app.json 保留原有设置，只关掉安全模式（PS 5.1 没有 -AsHashtable，手动转）
    $appFile = Join-Path $vault '.obsidian\app.json'
    $appCfg = [ordered]@{}
    if (Test-Path $appFile) {
      try {
        $old = Get-Content -Raw -Encoding UTF8 $appFile | ConvertFrom-Json
        if ($old) { foreach ($p in $old.PSObject.Properties) { $appCfg[$p.Name] = $p.Value } }
      } catch { }
    }
    $appCfg['safeMode'] = $false
    $appCfg['communityPluginsEnabled'] = $true
    Write-Json $appFile $appCfg 6

    Log "  插件已安装并预配置：$pluginDst" 'ok'
  }

  # 给库写一个欢迎笔记
  $readme = Join-Path $vault 'content\开始写作.md'
  if (-not (Test-Path $readme)) {
    $txt = @"
---
title: 开始写作
date: $(Get-Date -Format 'yyyy-MM-dd')
tags:
  - 指南
---

这是你的第一篇笔记。左侧边栏点 ↑ 图标（或右键笔记选「上传到博客」），就会把它发布到
$($Script:PagesUrl)

写完想更新？再点一次上传即可，同名会覆盖。

支持的写法：[[开始使用]]、![[图片.png]]、Callout、表格、任务列表。
"@
    Write-TextFile $readme $txt
    Log '  已生成示例笔记：content\开始写作.md'
  }

  Set-Step 5 'done' 'Obsidian 与插件就绪'
  Step-Progress 5 100 '全部完成'
}

# ---------------------------------------------------------------- 主流程

function Invoke-Main {
  $Script:Owner = $Cfg.Owner
  $Script:PagesUrl = $null
  $Script:RepoUrl = $null
  $Script:CurrentStep = 0
  try {
    Log "数字花园部署器 v1.0.0 启动" 'hi'
    Log "目标目录：$($Cfg.Target)"
    if ($Script:DryRun) { Log '当前为模拟模式，不会真的安装或推送' 'warn' }

    for ($i = 0; $i -lt $Script:Steps.Count; $i++) { Set-Step $i 'pending' '' }

    $only = @()
    if ($OnlySteps) { $only = @($OnlySteps -split ',' | ForEach-Object { [int]$_.Trim() }) }
    $shouldRun = { param($n) ($only.Count -eq 0) -or ($only -contains $n) }

    if (& $shouldRun 0) { $Script:CurrentStep = 0; Step-Environment }
    if (& $shouldRun 1) { $Script:CurrentStep = 1; Step-PrepareTarget }
    if (& $shouldRun 2) { $Script:CurrentStep = 2; Step-Dependencies }
    if (& $shouldRun 3) { $Script:CurrentStep = 3; Step-Build }
    if (& $shouldRun 4) { $Script:CurrentStep = 4; Step-GitHub }
    if (& $shouldRun 5) { $Script:CurrentStep = 5; Step-Obsidian }

    Set-Progress 100 '部署完成'
    Log ''
    Log '全部完成 ✓' 'ok'
    if ($Script:PagesUrl) { Log "站点地址：$($Script:PagesUrl)" 'hi' }
    Log 'GitHub Actions 正在发布，通常 1 分钟内可访问。' 'warn'
    Finish $true $Script:PagesUrl
  } catch {
    $msg = $_.Exception.Message
    Set-Step $Script:CurrentStep 'fail' $msg
    Log "失败：$msg" 'err'
    if ($_.InvocationInfo) { Log ("  位置：" + ($_.InvocationInfo.PositionMessage -replace "`r?`n", ' ')) 'err' }
    Finish $false $msg
  } finally {
    try { if (Test-Path $Script:Tmp) { Remove-Item $Script:Tmp -Recurse -Force -ErrorAction SilentlyContinue } } catch { }
  }
}

if ($DetectOnly) {
  # 只检测环境，供界面刷新「环境」面板使用
  try {
    for ($i = 0; $i -lt $Script:Steps.Count; $i++) { Set-Step $i 'pending' '' }
    Step-Environment
    Finish $true 'detect'
  } catch {
    Finish $false $_.Exception.Message
  }
  exit
}

Invoke-Main
