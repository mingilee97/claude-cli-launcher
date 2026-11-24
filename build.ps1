# Claude 런처 빌드 스크립트
# Windows에 기본으로 들어 있는 .NET Framework 4 컴파일러(csc.exe)로 빌드한다. 따로 설치할 SDK는 없다.
# WebView2 SDK DLL은 NuGet에서 받아 온다(처음 한 번).
param(
    [string]$OutDir = $PSScriptRoot,
    [switch]$Admin,  # 켤 때마다 관리자 권한으로 실행되게 만든다
    [switch]$Test    # 시험용 test.exe(관리자 권한 없이 실행)도 함께 만든다
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$fw = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$csc = Join-Path $fw 'csc.exe'
if (-not (Test-Path $csc)) { throw ".NET Framework 4 컴파일러가 없습니다: $csc" }

# 1) WebView2 SDK
$wvVer = '1.0.4370.26'
$need = 'Microsoft.Web.WebView2.Core.dll', 'Microsoft.Web.WebView2.Wpf.dll', 'WebView2Loader.dll'
$missing = $need | Where-Object { -not (Test-Path (Join-Path $OutDir $_)) }
if ($missing) {
    $pkgDir = Join-Path $root ".deps\webview2-$wvVer"
    if (-not (Test-Path (Join-Path $pkgDir 'lib'))) {
        Write-Host "WebView2 SDK $wvVer 받는 중..."
        New-Item -ItemType Directory -Force $pkgDir | Out-Null
        $zip = Join-Path $pkgDir 'pkg.zip'
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        Invoke-WebRequest "https://api.nuget.org/v3-flatcontainer/microsoft.web.webview2/$wvVer/microsoft.web.webview2.$wvVer.nupkg" -OutFile $zip -UseBasicParsing
        Expand-Archive $zip $pkgDir -Force
        Remove-Item $zip
    }
    New-Item -ItemType Directory -Force $OutDir | Out-Null
    Copy-Item (Join-Path $pkgDir 'lib\net462\Microsoft.Web.WebView2.Core.dll') $OutDir -Force
    Copy-Item (Join-Path $pkgDir 'lib\net462\Microsoft.Web.WebView2.Wpf.dll') $OutDir -Force
    Copy-Item (Join-Path $pkgDir 'runtimes\win-x64\native\WebView2Loader.dll') $OutDir -Force
}

# 2) 아이콘: 내 PC에 claude.ico 가 있으면 그것을, 없으면 저장소의 assets\app.ico 를 쓴다.
$ico = Join-Path $root 'claude.ico'
if (-not (Test-Path $ico)) { $ico = Join-Path $root 'assets\app.ico' }

# 3) 컴파일
$refs = @(
    (Join-Path $fw 'WPF\PresentationCore.dll'), (Join-Path $fw 'WPF\PresentationFramework.dll'), (Join-Path $fw 'WPF\WindowsBase.dll'),
    (Join-Path $fw 'System.Xaml.dll'), (Join-Path $fw 'System.Web.Extensions.dll'),
    (Join-Path $OutDir 'Microsoft.Web.WebView2.Core.dll'), (Join-Path $OutDir 'Microsoft.Web.WebView2.Wpf.dll')
) | ForEach-Object { "/r:$_" }
$b = Join-Path $root 'build'
function Invoke-Csc($exe, $manifest) {
    $cscArgs = @('/nologo', '/target:winexe', '/platform:x64', '/optimize+', '/codepage:65001',
        "/out:$(Join-Path $OutDir $exe)", "/win32icon:$ico", "/win32manifest:$(Join-Path $b $manifest)",
        "/resource:$(Join-Path $b 'main.xaml'),main.xaml", "/resource:$(Join-Path $b 'res.xaml'),res.xaml", "/resource:$ico,claude.ico") + $refs + @((Join-Path $b 'Program.cs'))
    & $csc @cscArgs
    if ($LASTEXITCODE -ne 0) { throw "$exe 빌드 실패" }
    Write-Host "만듦: $(Join-Path $OutDir $exe)"
}
Invoke-Csc 'ClaudeLauncher.exe' $(if ($Admin) { 'app-admin.manifest' } else { 'app.manifest' })
if ($Test) { Invoke-Csc 'test.exe' 'app.manifest' }
