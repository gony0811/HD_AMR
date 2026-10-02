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

FILE_COUNT=$(find "$PUBLISH_DIR" -type f | wc -l | tr -d ' ')
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
scp -r -q "$PUBLISH_DIR/"* "$REMOTE_USER@$REMOTE_HOST:$REMOTE_DIR/"
echo "  ✓ 전송 완료"

# ── 4. 원격 앱 시작 ──────────────────────────────────────────
if [ "$NO_RESTART" = false ]; then
    echo "▶ 원격 앱 시작 중…"
    $SSH "cd $REMOTE_DIR && start $REMOTE_EXE" 2>/dev/null || true
    echo "  ✓ 앱 시작"
fi

echo ""
echo "✅ 배포 완료"
