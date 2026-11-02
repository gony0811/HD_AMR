<#
.SYNOPSIS
    HD.AMR.Desktop 을 원격 AMR PC 에 배포한다 (deploy.sh 의 Windows 판).

.DESCRIPTION
    Git Bash 의 ssh 는 한글 사용자 폴더 경로를 깨뜨려 ~/.ssh 키를 못 찾으므로,
    Windows 기본 OpenSSH(ssh.exe / scp.exe)를 쓴다. 원격 기본 셸이 cmd 라서
    원격 PowerShell 명령은 -EncodedCommand 로 보낸다(파이프 '|' 를 cmd 가 가로채지 않도록).

.EXAMPLE
    .\deploy.ps1
    .\deploy.ps1 -SkipBuild
    .\deploy.ps1 -DryRun
    .\deploy.ps1 -NoRestart
#>
[CmdletBinding()]
param(
    [switch]$SkipBuild,   # publish 생략, 기존 publish 결과물만 전송
    [switch]$DryRun,      # 전송 대상만 표시, 실제 전송 안 함
    [switch]$NoRestart    # 전송 후 앱을 다시 시작하지 않음
)

$ErrorActionPreference = 'Stop'

# ── 설정 ──────────────────────────────────────────────────────
$RemoteHost = '100.116.213.68'
$RemoteUser = 'eggplant'
$RemoteDir  = 'C:\Deploy\HD_AMR'
$RemoteExe  = 'HD.AMR.Desktop.exe'
$Project    = 'HD.AMR.Desktop'
$ProjectDir = Join-Path $PSScriptRoot "src\$Project"
$PublishDir = Join-Path $ProjectDir 'bin\Release\net8.0\win-x64\publish'
$Target     = "$RemoteUser@$RemoteHost"
$SshOpts    = @('-o', 'ConnectTimeout=5', '-o', 'BatchMode=yes')

# 원격 PowerShell 스크립트를 실행하고 출력 문자열을 돌려준다.
function Invoke-Remote([string]$Script) {
    # 진행률 레코드가 CLIXML 로 stderr 에 섞여 나오지 않게 끈다.
    $Script = "`$ProgressPreference='SilentlyContinue'`n" + $Script
    $enc = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($Script))
    $out = & ssh @SshOpts $Target "powershell -NoProfile -NonInteractive -EncodedCommand $enc"
    if ($LASTEXITCODE -ne 0) { throw "원격 명령 실패 (exit $LASTEXITCODE)" }
    return $out
}

# ── 0. 연결 확인 ──────────────────────────────────────────────
Write-Host "▶ 원격 연결 확인 ($Target)…"
$remoteName = & ssh @SshOpts $Target hostname
if ($LASTEXITCODE -ne 0) { throw "SSH 접속 실패: $Target (키 등록·Tailscale 연결 확인)" }
Write-Host "  ✔ $remoteName"

# ── 1. Publish ────────────────────────────────────────────────
if (-not $SkipBuild) {
    Write-Host "▶ dotnet publish ($Project, Release, win-x64)…"
    dotnet publish (Join-Path $ProjectDir "$Project.csproj") -c Release --no-self-contained -v quiet
    if ($LASTEXITCODE -ne 0) { throw "publish 실패 (exit $LASTEXITCODE)" }
    Write-Host '  ✔ publish 완료'
} else {
    Write-Host '▶ publish 생략 (-SkipBuild)'
}

if (-not (Test-Path $PublishDir)) { throw "publish 출력 디렉터리 없음: $PublishDir" }

# appsettings*.json 은 보내지 않는다 — 원격 현장 설정(장비 Enabled·COM 포트 등)을
# 로컬 값으로 덮어쓰지 않기 위해. 원격 설정을 바꿔야 하면 원격 파일을 직접 고친다.
$items = Get-ChildItem $PublishDir | Where-Object Name -notlike 'appsettings*.json'
$files = Get-ChildItem $PublishDir -Recurse -File | Where-Object Name -notlike 'appsettings*.json'
$sizeMb = ($files | Measure-Object Length -Sum).Sum / 1MB
Write-Host ("▶ 전송 대상: {0}개 파일 ({1:N1} MB) → {2}:{3}" -f $files.Count, $sizeMb, $Target, $RemoteDir)

if ($DryRun) {
    Write-Host '  (dry-run: 실제 전송 안 함)'
    return
}

# ── 2. 원격 앱 종료 ───────────────────────────────────────────
Write-Host '▶ 원격 앱 종료 중…'
$exeName = [IO.Path]::GetFileNameWithoutExtension($RemoteExe)
Invoke-Remote @"
`$p = Get-Process $exeName -ErrorAction SilentlyContinue
if (`$p) { `$p | Stop-Process -Force; `$p | Wait-Process -Timeout 10 -ErrorAction SilentlyContinue; 'stopped' } else { 'not running' }
"@ | ForEach-Object { Write-Host "  ✔ $_" }

# ── 3. 전송 ───────────────────────────────────────────────────
Write-Host '▶ scp 전송 중…'
$remoteScpDir = $RemoteDir -replace '\\', '/'
& scp -r -q @SshOpts @($items.FullName) "${Target}:$remoteScpDir/"
if ($LASTEXITCODE -ne 0) { throw "scp 전송 실패 (exit $LASTEXITCODE)" }
Write-Host '  ✔ 전송 완료'

# ── 4. 원격 앱 시작 ───────────────────────────────────────────
if (-not $NoRestart) {
    Write-Host '▶ 원격 앱 시작 중…'
    # ssh 세션에서 띄운 프로세스는 ssh 종료 시 함께 죽으므로, 로그인된 데스크톱 세션에서 실행되도록
    # 예약 작업(Interactive)으로 시작한다(작업은 매번 덮어써 등록).
    $status = Invoke-Remote @"
`$a = New-ScheduledTaskAction -Execute '$RemoteDir\$RemoteExe' -WorkingDirectory '$RemoteDir'
`$pr = New-ScheduledTaskPrincipal -UserId '$RemoteUser' -LogonType Interactive
Register-ScheduledTask -TaskName HDAMR_Start -Action `$a -Principal `$pr -Force | Out-Null
Start-ScheduledTask -TaskName HDAMR_Start
Start-Sleep -Seconds 8
`$p = Get-Process $exeName -ErrorAction SilentlyContinue
if (`$p) { "running pid=`$(`$p.Id) session=`$(`$p.SessionId) responding=`$(`$p.Responding)" } else { 'NOT RUNNING' }
"@
    if ($status -match 'NOT RUNNING') { throw '원격 앱이 시작되지 않았다 (원격 PC 에 eggplant 로 로그인돼 있는지 확인)' }
    Write-Host "  ✔ $status"
}

Write-Host ''
Write-Host '✔ 배포 완료'
