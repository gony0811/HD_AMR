# Jetson Orin Nano(linux-arm64) 배포용 퍼블리시.
#
# self-contained 로 묶으므로 젯슨에 .NET 런타임을 설치할 필요가 없다.
# 사용: .\deploy\publish.ps1 -JetsonHost eggplant@192.168.0.10

param(
    [string]$JetsonHost = "",
    [string]$RemotePath = "/opt/nsl-lidar",
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$projectDir = Split-Path -Parent $PSScriptRoot
$outDir = Join-Path $projectDir "bin/publish/linux-arm64"

Write-Host "퍼블리시: $Configuration / linux-arm64" -ForegroundColor Cyan

dotnet publish $projectDir `
    -c $Configuration `
    -r linux-arm64 `
    --self-contained true `
    -o $outDir

if ($LASTEXITCODE -ne 0) { throw "퍼블리시 실패" }

Write-Host "출력: $outDir" -ForegroundColor Green

if ([string]::IsNullOrWhiteSpace($JetsonHost)) {
    Write-Host "JetsonHost 를 주지 않아 전송은 건너뛴다." -ForegroundColor Yellow
    Write-Host "수동 전송: scp -r `"$outDir/*`" <user>@<jetson>:$RemotePath/"
    exit 0
}

# 실행 중이면 먼저 멈춰야 파일이 잠기지 않는다.
Write-Host "서비스 중지 후 전송: $JetsonHost`:$RemotePath" -ForegroundColor Cyan
ssh $JetsonHost "sudo systemctl stop nsl-lidar 2>/dev/null; mkdir -p $RemotePath"
scp -r "$outDir/*" "${JetsonHost}:${RemotePath}/"
ssh $JetsonHost "chmod +x $RemotePath/HD_AMR.LidarService; sudo systemctl start nsl-lidar 2>/dev/null || true"

Write-Host "완료. 상태 확인: ssh $JetsonHost 'systemctl status nsl-lidar'" -ForegroundColor Green
