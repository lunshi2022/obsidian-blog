# 数字花园 DEPLOYER —— Windows 一键部署器（界面）
# 界面只负责显示与收集输入，真正的部署由 worker.ps1 在独立进程里完成，
# 通过日志文件回传进度，所以界面卡死或崩溃都不会影响部署。

param(
  [switch]$NoElevate,
  [switch]$NoWindow
)

$ErrorActionPreference = 'Stop'
$Script:Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$Script:WorkerPath = Join-Path $Script:Root 'worker.ps1'
$Script:XamlPath = Join-Path $Script:Root 'ui.xaml'
$Script:ErrLog = Join-Path $env:TEMP 'garden-deployer-error.log'

# 模板目录：部署器放在仓库的 deployer/ 里，模板在上一级；
# 如果被打包成独立目录，则期望同级的 payload/
function Resolve-PayloadDir {
  $candidates = @(
    (Split-Path -Parent $Script:Root)
    (Join-Path $Script:Root 'payload')
    $Script:Root
  )
  foreach ($c in $candidates) {
    if ($c -and (Test-Path (Join-Path $c 'src\build.js'))) { return $c }
  }
  return $null
}
$Script:PayloadDir = Resolve-PayloadDir

function Write-Fatal {
  param([string]$Msg)
  try {
    "[$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')] $Msg" | Add-Content -Path $Script:ErrLog -Encoding UTF8
  } catch { }
  try {
    [void][System.Reflection.Assembly]::LoadWithPartialName('System.Windows.Forms')
    [System.Windows.Forms.MessageBox]::Show(
      "$Msg`n`n详细信息已写入：`n$Script:ErrLog", '数字花园部署器 - 出错了',
      'OK', 'Error') | Out-Null
  } catch { Write-Host $Msg }
}

# ---------------------------------------------------------------- 提升权限

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $isAdmin -and -not $NoElevate -and -not $env:GARDEN_NO_ELEVATE) {
  try {
    $argList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-WindowStyle', 'Hidden',
      '-File', "`"$($MyInvocation.MyCommand.Path)`"")
    Start-Process -FilePath (Get-Command powershell.exe).Source -Verb RunAs -ArgumentList $argList
    exit
  } catch {
    # 用户拒绝了 UAC —— 继续以普通权限运行，安装系统组件时可能失败
  }
}

# ---------------------------------------------------------------- 载入界面

try {
  [void][System.Reflection.Assembly]::LoadWithPartialName('PresentationFramework')
  [void][System.Reflection.Assembly]::LoadWithPartialName('PresentationCore')
  [void][System.Reflection.Assembly]::LoadWithPartialName('WindowsBase')
  [void][System.Reflection.Assembly]::LoadWithPartialName('System.Windows.Forms')

  $xaml = Get-Content -Raw -Encoding UTF8 $Script:XamlPath
  $reader = New-Object System.Xml.XmlNodeReader ([xml]$xaml)
  $window = [Windows.Markup.XamlReader]::Load($reader)
} catch {
  Write-Fatal "界面加载失败：$($_.Exception.Message)"
  exit 1
}

# 取出所有控件
$ui = @{}
foreach ($n in @(
    'HeaderBar', 'StatusDot', 'StatusText', 'StatusPill', 'BtnMin', 'BtnClose',
    'TxtPath', 'BtnBrowse', 'TxtUser', 'TxtRepo', 'TxtToken', 'BtnToken',
    'EnvNodeDot', 'EnvNodeVer', 'EnvNodePath',
    'EnvGitDot', 'EnvGitVer', 'EnvGitPath',
    'EnvObsDot', 'EnvObsVer', 'EnvObsPath', 'BtnRefresh',
    'LogHost', 'LogScroll', 'BtnClearLog',
    'BarFill', 'BarGlow', 'BarTrack', 'PctText', 'PhaseText',
    'BtnStart', 'BtnOpen', 'BtnQuit', 'HintText', 'GaugeMode', 'GaugeTarget'
  )) {
  $ui[$n] = $window.FindName($n)
}
foreach ($i in 0..5) {
  $ui["S${i}Dot"] = $window.FindName("S${i}Dot")
  $ui["S${i}Name"] = $window.FindName("S${i}Name")
  $ui["S${i}Sub"] = $window.FindName("S${i}Sub")
  $ui["S${i}Rail"] = $window.FindName("S${i}Rail")
}

# ---------------------------------------------------------------- 颜色与绘制

function Brush([string]$hex) {
  return New-Object System.Windows.Media.SolidColorBrush ([System.Windows.Media.ColorConverter]::ConvertFromString($hex))
}
$C = @{
  cyan  = Brush '#00e5ff'
  ok    = Brush '#00ffa3'
  warn  = Brush '#ffb020'
  err   = Brush '#ff4d6d'
  txt   = Brush '#c6d6ec'
  dim   = Brush '#4d5f80'
  dim2  = Brush '#2c3a54'
  white = Brush '#ffffff'
  cyanD = Brush '#0a7d92'
  okD   = Brush '#3d6a5c'
  rail  = Brush '#16283c'
  railD = Brush '#1f4d3f'
  bgDot = Brush '#060a13'
}
$LogColor = @{
  ''     = $C['dim']
  'ok'   = $C['ok']
  'warn' = $C['warn']
  'err'  = $C['err']
  'cmd'  = $C['cyan']
  'hi'   = $C['white']
}

function Add-Log {
  param([string]$Text, [string]$Cls = '')
  $tb = New-Object System.Windows.Controls.TextBlock
  $tb.FontFamily = New-Object System.Windows.Media.FontFamily('Cascadia Mono, Consolas, Microsoft YaHei UI')
  $tb.FontSize = 11
  $tb.TextWrapping = 'Wrap'
  $tb.Margin = New-Object System.Windows.Thickness(0, 1, 0, 1)
  $r1 = New-Object System.Windows.Documents.Run -ArgumentList ("[{0}] " -f (Get-Date -Format 'HH:mm:ss'))
  $r1.Foreground = $C['dim2']
  $r2 = New-Object System.Windows.Documents.Run -ArgumentList $Text
  if ($LogColor.ContainsKey($Cls)) { $r2.Foreground = $LogColor[$Cls] } else { $r2.Foreground = $C['dim'] }
  [void]$tb.Inlines.Add($r1)
  [void]$tb.Inlines.Add($r2)
  [void]$ui.LogHost.Children.Add($tb)
  while ($ui.LogHost.Children.Count -gt 600) { $ui.LogHost.Children.RemoveAt(0) }
  $ui.LogScroll.ScrollToEnd()
}

function Set-StepState {
  param([int]$Index, [string]$State, [string]$Sub)
  $dot = $ui["S${Index}Dot"]
  $name = $ui["S${Index}Name"]
  $subTb = $ui["S${Index}Sub"]
  $rail = $ui["S${Index}Rail"]
  if (-not $dot) { return }

  switch ($State) {
    'pending' {
      $dot.Fill = $C['bgDot']; $dot.Stroke = $C['dim2']; $name.Foreground = $C['dim']
      if ($subTb) { $subTb.Foreground = $C['dim2'] }
      if ($rail) { $rail.Fill = $C['rail'] }
    }
    'active' {
      $dot.Fill = $C['cyan']; $dot.Stroke = $C['cyan']; $name.Foreground = $C['white']
      if ($subTb) { $subTb.Foreground = $C['cyanD'] }
    }
    'done' {
      $dot.Fill = $C['ok']; $dot.Stroke = $C['ok']; $name.Foreground = $C['txt']
      if ($subTb) { $subTb.Foreground = $C['okD'] }
      if ($rail) { $rail.Fill = $C['railD'] }
    }
    'fail' {
      $dot.Fill = $C['err']; $dot.Stroke = $C['err']; $name.Foreground = Brush '#ff8ea0'
      if ($subTb) { $subTb.Foreground = $C['err'] }
    }
  }
  if ($Sub -and $subTb) { $subTb.Text = $Sub }
}

function Set-Bar {
  param([double]$Pct, [string]$Label)
  $w = 0
  if ($ui.BarTrack -and $ui.BarTrack.ActualWidth -gt 0) {
    $w = $ui.BarTrack.ActualWidth * ([Math]::Max(0, [Math]::Min(100, $Pct)) / 100.0)
  }
  $ui.BarFill.Width = [Math]::Max(0, $w)
  if ($ui.BarGlow) {
    $ui.BarGlow.Margin = New-Object System.Windows.Thickness ([Math]::Max(0, $w - 35)), 0, 0, 0
  }
  $ui.PctText.Text = '{0}%' -f [Math]::Round($Pct)
  if ($Label) { $ui.PhaseText.Text = $Label }
}

function Set-Status {
  param([string]$Text, [string]$Color)
  $ui.StatusText.Text = $Text
  $ui.StatusDot.Fill = (Brush $Color)
  $ui.StatusText.Foreground = (Brush $Color)
}

function Set-EnvRow {
  param([string]$Name, [string]$State, [string]$Version, [string]$Path)
  $map = @{ node = 'Node'; git = 'Git'; obsidian = 'Obs' }
  $k = $map[$Name]
  if (-not $k) { return }
  $dot = $ui["Env${k}Dot"]; $ver = $ui["Env${k}Ver"]; $p = $ui["Env${k}Path"]
  if ($State -eq 'ok') {
    $dot.Fill = $C['ok']
    $ver.Text = $Version; $ver.Foreground = $C['txt']
    $p.Text = $Path
    $p.ToolTip = $Path
  } else {
    $dot.Fill = $C['warn']
    $ver.Text = '未安装'; $ver.Foreground = $C['warn']
    $p.Text = ''
  }
}

# 百分比文本单独处理（避开 Set-Bar 里的语法风险）
function Set-PctText([double]$Pct) {
  $ui.PctText.Text = '{0}%' -f [Math]::Round($Pct)
}

# ---------------------------------------------------------------- 运行控制

$Script:Proc = $null
$Script:Cursor = 0
$Script:LogPath = $null
$Script:Mode = 'idle'
$Script:Done = $false
$Script:PagesUrl = $null
$Script:RepoUrl = $null
$Script:TempDir = Join-Path $env:TEMP 'garden-deployer'

if (-not (Test-Path $Script:TempDir)) { New-Item -ItemType Directory -Force -Path $Script:TempDir | Out-Null }

$timer = New-Object System.Windows.Threading.DispatcherTimer
$timer.Interval = [TimeSpan]::FromMilliseconds(120)

function Write-Utf8NoBom {
  param([string]$Path, [string]$Text)
  [IO.File]::WriteAllText($Path, $Text, (New-Object Text.UTF8Encoding($false)))
}

function Start-Worker {
  param([string]$Mode, [hashtable]$Config)
  $Script:Mode = $Mode
  $Script:Done = $false
  $Script:Cursor = 0
  $Script:LogPath = Join-Path $Script:TempDir ("run-" + $Mode + "-" + (Get-Date -Format 'HHmmss') + ".log")
  if (Test-Path $Script:LogPath) { Remove-Item $Script:LogPath -Force }

  $cfg = @{}
  foreach ($k in $Config.Keys) { $cfg[$k] = $Config[$k] }
  $cfg['LogFile'] = $Script:LogPath
  $cfg['DryRun'] = $false

  $cfgPath = Join-Path $Script:TempDir 'config.json'
  Write-Utf8NoBom $cfgPath ($cfg | ConvertTo-Json -Depth 4)

  $argLine = '-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "' + $Script:WorkerPath + '" -Config "' + $cfgPath + '"'
  if ($Mode -eq 'detect') { $argLine += ' -DetectOnly' }

  $psi = New-Object System.Diagnostics.ProcessStartInfo
  $psi.FileName = (Get-Command powershell.exe).Source
  $psi.Arguments = $argLine
  $psi.UseShellExecute = $false
  $psi.CreateNoWindow = $true
  $Script:Proc = [System.Diagnostics.Process]::Start($psi)
  $timer.Start()
}

function Stop-Worker {
  $timer.Stop()
  if ($Script:Proc -and -not $Script:Proc.HasExited) {
    try { $Script:Proc.Kill() } catch { }
  }
  $Script:Proc = $null
  $Script:Mode = 'idle'
}

$tick = {
  # 读取新增日志
  if ($Script:LogPath -and (Test-Path $Script:LogPath)) {
    $all = $null
    try { $all = [IO.File]::ReadAllLines($Script:LogPath, [Text.Encoding]::UTF8) } catch { $all = $null }
    if ($all) {
      for ($i = $Script:Cursor; $i -lt $all.Count; $i++) {
        $line = $all[$i]
        if (-not $line) { continue }
        # 字段之间用 Tab 分隔，引擎已把内容里的 Tab 替换掉
        $p = $line -split "`t"
        $type = $p[0]
        switch ($type) {
          'LOG' {
            if ($Script:Mode -eq 'deploy' -and $p.Count -ge 3) { Add-Log $p[2] $p[1] }
          }
          'STEP' {
            if ($Script:Mode -eq 'deploy' -and $p.Count -ge 3) {
              $subText = if ($p.Count -ge 4) { $p[3] } else { '' }
              Set-StepState ([int]$p[1]) $p[2] $subText
            }
          }
          'PROG' {
            if ($Script:Mode -eq 'deploy' -and $p.Count -ge 2) {
              $pct = [double]$p[1]
              $label = if ($p.Count -ge 3) { $p[2] } else { '' }
              Set-Bar $pct $label
            }
          }
          'ENV' {
            if ($p.Count -ge 4) {
              $envPath = if ($p.Count -ge 5) { $p[4] } else { '' }
              Set-EnvRow $p[1] $p[2] $p[3] $envPath
            }
          }
          'DONE' {
            $Script:Done = $true
            $ok = ($p.Count -ge 2 -and $p[1] -eq '1')
            if ($p.Count -ge 3) { $Script:PagesUrl = $p[2] }

            if ($Script:Mode -eq 'deploy') {
              if ($ok) {
                Set-Status '部署完成' '#00ffa3'
                Set-Bar 100 '全部完成'
                Add-Log '—— 部署完成 ——' 'ok'
                if ($Script:PagesUrl) {
                  Add-Log "站点地址：$($Script:PagesUrl)" 'hi'
                  $ui.BtnOpen.IsEnabled = $true
                  $ui.BtnOpen.Content = '打开站点'
                }
                $ui.BtnStart.Content = '重新部署'
                $ui.HintText.Text = 'GitHub Actions 正在发布，通常 1 分钟内可访问'
              } else {
                Set-Status '部署失败' '#ff4d6d'
                $why = if ($p.Count -ge 3) { $p[2] } else { '未知错误' }
                Add-Log "—— 部署失败：$why ——" 'err'
                $ui.BtnStart.Content = '重试'
                $ui.HintText.Text = '可修正上面的问题后点「重试」'
              }
              $ui.BtnStart.IsEnabled = $true
              $ui.BtnBrowse.IsEnabled = $true
              $ui.TxtToken.IsEnabled = $true
              $ui.BtnRefresh.IsEnabled = $true
            } else {
              Set-Status '就绪' '#2c3a54'
              $ui.BtnRefresh.IsEnabled = $true
            }
          }
        }
      }
      $Script:Cursor = $all.Count
    }
  }

  # 进程退出兜底
  if ($Script:Proc -and $Script:Proc.HasExited) {
    if (-not $Script:Done) {
      if ($Script:Mode -eq 'deploy') {
        Set-Status '异常退出' '#ff4d6d'
        Add-Log '部署进程意外退出，未收到完成信号。常见原因：被杀毒软件拦截或权限不足。' 'err'
        $ui.BtnStart.IsEnabled = $true
        $ui.BtnBrowse.IsEnabled = $true
        $ui.TxtToken.IsEnabled = $true
        $ui.BtnRefresh.IsEnabled = $true
      } else {
        $ui.BtnRefresh.IsEnabled = $true
      }
      Set-Bar 0 '已停止'
    }
    $timer.Stop()
    $Script:Proc = $null
    $Script:Mode = 'idle'
  }
}
$timer.Add_Tick($tick)

# ---------------------------------------------------------------- 事件

$ui.HeaderBar.Add_MouseLeftButtonDown({ try { $window.DragMove() } catch { } })
$ui.BtnMin.Add_Click({ $window.WindowState = 'Minimized' })
$ui.BtnClose.Add_Click({
    if ($Script:Mode -eq 'deploy' -and -not $Script:Done) {
      $r = [System.Windows.MessageBox]::Show('部署正在进行，确定要退出吗？', '确认', 'YesNo', 'Warning')
      if ($r -ne 'Yes') { return }
    }
    Stop-Worker
    $window.Close()
  })

$ui.BtnBrowse.Add_Click({
    $dlg = New-Object System.Windows.Forms.FolderBrowserDialog
    $dlg.Description = '选择博客部署目录（会在这里生成站点、笔记和 Git 仓库）'
    $dlg.ShowNewFolderButton = $true
    if ($ui.TxtPath.Text -and (Test-Path $ui.TxtPath.Text)) { $dlg.SelectedPath = $ui.TxtPath.Text }
    if ($dlg.ShowDialog() -eq 'OK') {
      $ui.TxtPath.Text = (Join-Path $dlg.SelectedPath 'GardenBlog')
      Add-Log "目标目录已设置：$($ui.TxtPath.Text)" 'cmd'
    }
  })

$ui.BtnToken.Add_Click({
    Start-Process 'https://github.com/settings/tokens/new?scopes=repo,workflow&description=Garden%20Deployer'
    Add-Log '已在浏览器打开令牌创建页，勾选 repo 与 workflow 后复制生成的令牌粘贴回来' 'cmd'
  })

$ui.BtnClearLog.Add_Click({ $ui.LogHost.Children.Clear() })

$ui.BtnRefresh.Add_Click({
    if ($Script:Mode -ne 'idle') { return }
    $ui.BtnRefresh.IsEnabled = $false
    Add-Log '重新检测环境…' 'cmd'
    Start-Worker 'detect' @{ Target = $ui.TxtPath.Text; PayloadDir = $Script:PayloadDir; Owner = ''; Repo = ''; Token = '' }
  })

$ui.BtnStart.Add_Click({
    if ($Script:Mode -ne 'idle') { return }

    $target = $ui.TxtPath.Text.Trim()
    $token = $ui.TxtToken.Password.Trim()
    $repo = $ui.TxtRepo.Text.Trim()
    $owner = $ui.TxtUser.Text.Trim()

    if (-not $target) { Add-Log '请先选择部署目录' 'err'; return }
    if (-not $token) { Add-Log '请填写 GitHub 访问令牌（点右侧「去创建令牌」）' 'err'; return }
    if (-not $repo) { $repo = 'my-garden'; $ui.TxtRepo.Text = $repo }
    if ($repo -notmatch '^[A-Za-z0-9._-]+$') { Add-Log '仓库名只能包含字母、数字、点、下划线和连字符' 'err'; return }

    # 重置界面
    $ui.LogHost.Children.Clear()
    foreach ($i in 0..5) { Set-StepState $i 'pending' '' }
    Set-Bar 0 '准备中…'
    Set-PctText 0
    Set-Status '部署中' '#00e5ff'
    $ui.BtnStart.IsEnabled = $false
    $ui.BtnBrowse.IsEnabled = $false
    $ui.TxtToken.IsEnabled = $false
    $ui.BtnOpen.IsEnabled = $false
    $ui.BtnRefresh.IsEnabled = $false
    $Script:PagesUrl = $null
    $ui.HintText.Text = '正在部署，请不要关闭窗口'

    Add-Log "开始部署到 $target" 'hi'
    Start-Worker 'deploy' @{
      Target     = $target
      PayloadDir = $Script:PayloadDir
      Owner      = $owner
      Repo       = $repo
      Token      = $token
    }
  })

$ui.BtnOpen.Add_Click({
    $url = $Script:PagesUrl
    if (-not $url) { $url = (Join-Path $ui.TxtPath.Text 'site\index.html') }
    if ($url -and (Test-Path $url)) { Start-Process $url; return }
    if ($url) { Start-Process $url }
    else { Add-Log '还没有可打开的地址' 'warn' }
  })

$ui.BtnQuit.Add_Click({ $window.Close() })

$window.Add_Closed({ Stop-Worker })

# ---------------------------------------------------------------- 初始化

$defaultTarget = Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'GardenBlog'
$ui.TxtPath.Text = $defaultTarget
$ui.GaugeMode.Text = 'GUI'
$ui.GaugeTarget.Text = if ($isAdmin) { 'ADMIN' } else { 'USER' }
Set-StepState 0 'pending' ''
Set-Bar 0 '等待开始'
Set-Status '就绪' '#2c3a54'
$ui.BtnOpen.IsEnabled = $false
Add-Log '数字花园部署器已就绪' 'hi'
Add-Log '三个步骤：填好目录与令牌 → 点「开始部署」→ 完成后点「打开站点」'
if (-not $isAdmin) { Add-Log '当前不是管理员权限，安装 Node/Git/Obsidian 时可能失败' 'warn' }
if (-not $Script:PayloadDir) {
  Add-Log '找不到站点模板（src\build.js）。请把 deployer 目录放在博客仓库内，或提供 payload 目录。' 'err'
  $ui.BtnStart.IsEnabled = $false
} else {
  Add-Log "模板目录：$($Script:PayloadDir)"
}

# 启动时自动检测一次环境
$ui.BtnRefresh.IsEnabled = $false
Start-Worker 'detect' @{ Target = $defaultTarget; PayloadDir = $Script:PayloadDir; Owner = ''; Repo = ''; Token = '' }

try {
  [void]$window.ShowDialog()
} catch {
  Write-Fatal "界面运行异常：$($_.Exception.Message)"
}
