# Claude 런처 설치 스크립트
#  - 저장소를 받은 폴더에서:   .\install.ps1   (또는 setup.cmd 더블클릭)
#  - 받지 않고 한 줄로:         irm https://raw.githubusercontent.com/mingilee97/claude-cli-launcher/main/install.ps1 | iex
param(
    [string]$Root,                                              # 프로젝트를 모아 둘 폴더(기본: 바탕화면\Claude)
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA 'ClaudeLauncher'),  # 한 줄 설치 때 받을 곳
    [switch]$NoShortcut
)
$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

# 0) 저장소 밖에서 실행됐으면(한 줄 설치) 먼저 소스를 받아 그 안의 install.ps1 을 돌린다.
$here = $PSScriptRoot
if (-not $here -or -not (Test-Path (Join-Path $here 'build\Program.cs'))) {
    Write-Host "소스를 받는 중: $InstallDir"
    $tmp = Join-Path $env:TEMP ("claude-launcher-" + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force $tmp | Out-Null
    $zip = Join-Path $tmp 'src.zip'
    Invoke-WebRequest 'https://github.com/mingilee97/claude-cli-launcher/archive/refs/heads/main.zip' -OutFile $zip -UseBasicParsing
    Expand-Archive $zip $tmp -Force
    $src = Get-ChildItem $tmp -Directory | Select-Object -First 1
    New-Item -ItemType Directory -Force $InstallDir | Out-Null
    Copy-Item (Join-Path $src.FullName '*') $InstallDir -Recurse -Force
    Remove-Item $tmp -Recurse -Force
    $a = @{}; if ($Root) { $a.Root = $Root }; if ($NoShortcut) { $a.NoShortcut = $true }
    & (Join-Path $InstallDir 'install.ps1') @a
    return
}

function Test-Cmd($name) { [bool](Get-Command $name -ErrorAction SilentlyContinue) }
$warn = @()

# 1) 필요한 것 확인
if (-not (Test-Cmd 'claude')) { $warn += "Claude Code(claude)가 없습니다. 설치: irm https://claude.ai/install.ps1 | iex" }
if (-not (Test-Cmd 'node'))   { $warn += "Node.js가 없습니다(상태줄·한도 표시에 씀). 설치: winget install OpenJS.NodeJS.LTS" }
$wvKey = '{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}'
$wv = @("HKLM:\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\$wvKey", "HKCU:\Software\Microsoft\EdgeUpdate\Clients\$wvKey") |
      Where-Object { (Get-ItemProperty $_ -ErrorAction SilentlyContinue).pv }
if (-not $wv) { $warn += "WebView2 런타임이 없습니다. 설치: winget install Microsoft.EdgeWebView2Runtime" }
if (-not (Test-Cmd 'wt'))     { $warn += "(선택) Windows Terminal이 없으면 '바깥 창에서 열기'가 기본 콘솔로 열립니다." }

# 2) 빌드
& (Join-Path $here 'build.ps1')

# 3) 프로젝트 폴더 정하기
$cfg = Join-Path $here 'launcher-config.json'
if ($Root -or -not (Test-Path $cfg)) {
    if (-not $Root) {
        $def = Join-Path ([Environment]::GetFolderPath('Desktop')) 'Claude'
        $ans = Read-Host "프로젝트를 모아 둘 폴더 [엔터 = $def]"
        $Root = if ($ans) { $ans.Trim('"') } else { $def }
    }
    New-Item -ItemType Directory -Force $Root | Out-Null
    $utf8 = New-Object System.Text.UTF8Encoding($false)
    [IO.File]::WriteAllText($cfg, (@{ root = $Root } | ConvertTo-Json), $utf8)
    Write-Host "프로젝트 폴더: $Root"
}

# 4) 바로가기(바탕화면, 시작 메뉴)
if (-not $NoShortcut) {
    $exe = Join-Path $here 'ClaudeLauncher.exe'
    $sh = New-Object -ComObject WScript.Shell
    foreach ($dir in @([Environment]::GetFolderPath('Desktop'), (Join-Path ([Environment]::GetFolderPath('StartMenu')) 'Programs'))) {
        $lnk = $sh.CreateShortcut((Join-Path $dir 'Claude 런처.lnk'))
        $lnk.TargetPath = $exe; $lnk.WorkingDirectory = $here; $lnk.IconLocation = "$exe,0"
        $lnk.Save()
    }
    Write-Host "바로가기를 만들었습니다: 바탕화면, 시작 메뉴"
}

Write-Host ""
Write-Host "설치 끝: $here\ClaudeLauncher.exe" -ForegroundColor Green
if ($warn) { Write-Host "확인할 것:" -ForegroundColor Yellow; $warn | ForEach-Object { Write-Host " - $_" -ForegroundColor Yellow } }
