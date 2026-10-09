#!/usr/bin/env bash
set -euo pipefail

# ── 설정 ──────────────────────────────────────────────────────
REMOTE_HOST="100.116.213.68"
REMOTE_USER="eggplant"
REMOTE_DIR="C:/Deploy/HD_AMR"
REMOTE_EXE="HD.AMR.Desktop.exe"
PROJECT="HD.AMR.Desktop"
SLN_DIR="$(cd "$(dirname "$0")/src" && pwd)"
PUBLISH_DIR="$SLN_DIR/$PROJECT/bin/Release/net8.0/win-x64"
SSH="ssh -o ConnectTimeout=5 ${REMOTE_USER}@${REMOTE_HOST}"

# ── 옵션 ──────────────────────────────────────────────────────
SKIP_BUILD=false
DRY_RUN=false
NO_RESTART=false

for arg in "$@"; do
    case "$arg" in
        --skip-build) SKIP_BUILD=true ;;
        --dry-run)    DRY_RUN=true ;;
        --no-restart) NO_RESTART=true ;;
        --help|-h)
            echo "Usage: ./deploy.sh [--skip-build] [--dry-run] [--no-restart]"
            echo "  --skip-build   publish 생략, 기존 빌드 결과물만 전송"
            echo "  --dry-run      전송 대상 파일만 표시, 실제 전송 안 함"
            echo "  --no-restart   전송 후 앱 재시작 안 함"
            exit 0 ;;
    esac
done

# ── 1. Publish ────────────────────────────────────────────────
if [ "$SKIP_BUILD" = false ]; then
    echo "▶ dotnet publish ($PROJECT, Release, win-x64)…"
    dotnet publish "$SLN_DIR/$PROJECT/$PROJECT.csproj" \
        -c Release \
        --no-self-contained \
        -v quiet
    echo "  ✓ publish 완료"
else
    echo "▶ publish 생략 (--skip-build)"
fi

if [ ! -d "$PUBLISH_DIR" ]; then
    echo "✗ 빌드 출력 디렉터리 없음: $PUBLISH_DIR" >&2
    exit 1
fi

FILE_COUNT=$(find "$PUBLISH_DIR" -type f ! -name 'appsettings*.json' | wc -l | tr -d ' ')
echo "▶ 전송 대상: $FILE_COUNT 개 파일 → $REMOTE_USER@$REMOTE_HOST:$REMOTE_DIR"

if [ "$DRY_RUN" = true ]; then
    echo "  (dry-run: 실제 전송 안 함)"
    exit 0
fi

# ── 2. 원격 앱 종료 ──────────────────────────────────────────
echo "▶ 원격 앱 종료 중…"
$SSH "taskkill /IM $REMOTE_EXE /F 2>NUL & exit 0" 2>/dev/null || true
sleep 2
echo "  ✓ 앱 종료"

# ── 3. 전송 ───────────────────────────────────────────────────
echo "▶ scp 전송 중…"
# appsettings*.json 은 보내지 않는다 — 원격 현장 설정(장비 Enabled·COM 포트 등)을 로컬 값으로 덮어쓰지 않기 위해.
# 새 설정 키는 코드 기본값으로 동작하고, 원격 설정을 바꿔야 하면 원격 파일을 직접 고친다.
# mapfile 은 bash 4+ 전용 — macOS 기본 bash(3.2)에서도 돌도록 read 루프로 모은다.
SEND_FILES=()
while IFS= read -r -d '' f; do SEND_FILES+=("$f"); done \
    < <(find "$PUBLISH_DIR" -mindepth 1 -maxdepth 1 ! -name 'appsettings*.json' -print0)
scp -r -q "${SEND_FILES[@]}" "$REMOTE_USER@$REMOTE_HOST:$REMOTE_DIR/"
echo "  ✓ 전송 완료"

# ── 4. 원격 앱 시작 ──────────────────────────────────────────
if [ "$NO_RESTART" = false ]; then
    echo "▶ 원격 앱 시작 중…"
    # ssh 세션에서 띄운 프로세스는 ssh 종료 시 함께 죽는다 — 로그인된 데스크톱 세션에서 실행되도록
    # 예약 작업(Interactive)으로 시작한다(작업은 매번 덮어써 등록).
    $SSH "powershell -NoProfile -Command \"\$a=New-ScheduledTaskAction -Execute '$REMOTE_DIR/$REMOTE_EXE' -WorkingDirectory '$REMOTE_DIR'; \$p=New-ScheduledTaskPrincipal -UserId '$REMOTE_USER' -LogonType Interactive; Register-ScheduledTask -TaskName HDAMR_Start -Action \$a -Principal \$p -Force | Out-Null; Start-ScheduledTask -TaskName HDAMR_Start\"" 2>/dev/null || true
    echo "  ✓ 앱 시작"
fi

echo ""
echo "✅ 배포 완료"
