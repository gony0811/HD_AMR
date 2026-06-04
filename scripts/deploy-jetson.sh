#!/usr/bin/env bash
#
# Mac → Jetson 배포·실행 사이클.
# (1) rsync 로 소스 동기화, (2) SSH 로 원격 dotnet run, (3) -L 로 5253 포트 포워딩.
# Mac 에서 Ctrl+C 한 번이면 SSH 종료 → SIGHUP → 원격 dotnet 도 같이 종료.
#
# 환경변수 (모두 override 가능):
#   JETSON_HOST=jetson.local        대상 호스트
#   JETSON_USER=jetson              대상 사용자
#   JETSON_DEST=~/HD_AMR            대상 디렉터리 (홈 기준)
#   BROWSER_PORT=5253               Mac 측 포워딩 + Jetson 측 바인딩 포트
#   SSH_OPTS=                       추가 ssh 옵션 (예: "-i ~/.ssh/jetson_id_ed25519")
#
# 옵션 플래그:
#   --watch       dotnet run 대신 dotnet watch (저장 즉시 재빌드)
#   --no-rsync    소스 동기화 건너뛰고 ssh 만 (Jetson 의 마지막 빌드 그대로 사용)
#   --build       빌드만 수행, run 안 함
#   --no-tunnel   포트 포워딩 안 함 (다른 mac 인스턴스가 5253 점유 중일 때)

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

: "${JETSON_HOST:=jetson.local}"
: "${JETSON_USER:=jetson}"
: "${JETSON_DEST:=~/HD_AMR}"
: "${BROWSER_PORT:=5253}"
: "${SSH_OPTS:=}"

DO_RSYNC=1
DO_RUN=1
USE_WATCH=0
USE_TUNNEL=1

for arg in "$@"; do
    case "$arg" in
        --watch)      USE_WATCH=1 ;;
        --no-rsync)   DO_RSYNC=0 ;;
        --build)      DO_RUN=0 ;;
        --no-tunnel)  USE_TUNNEL=0 ;;
        -h|--help)
            awk '/^# Mac/,/^# *--no-tunnel/ { sub(/^# ?/, ""); print }' "${BASH_SOURCE[0]}"
            exit 0 ;;
        *) printf '[deploy] 알 수 없는 옵션: %s\n' "$arg" >&2; exit 1 ;;
    esac
done

log() { printf '[deploy] %s\n' "$*"; }

REMOTE="$JETSON_USER@$JETSON_HOST"

# ── 0) 사전 점검 ────────────────────────────────────────────────────────
if [ "$USE_TUNNEL" = "1" ] && command -v lsof >/dev/null 2>&1; then
    if lsof -i ":$BROWSER_PORT" -sTCP:LISTEN >/dev/null 2>&1; then
        log "경고: Mac 의 $BROWSER_PORT 포트가 이미 사용 중 — 터널이 실패할 수 있습니다."
        log "  → 다른 dotnet 인스턴스를 종료하거나 BROWSER_PORT=5254 등으로 변경."
    fi
fi

# ── 1) rsync ───────────────────────────────────────────────────────────
if [ "$DO_RSYNC" = "1" ]; then
    log "rsync → $REMOTE:$JETSON_DEST (--delete 로 Jetson 의 비 sync 파일 제거)"
    rsync -az --delete \
        ${SSH_OPTS:+-e "ssh $SSH_OPTS"} \
        --exclude='/HD_AMR/HD_AMR/libs/orbbec/runtimes' \
        --exclude='/HD_AMR/HD_AMR/libs/orbbec/downloads' \
        --exclude='/HD_AMR/*/bin' \
        --exclude='/HD_AMR/*/obj' \
        --exclude='.git' \
        --exclude='.idea' \
        --exclude='.DS_Store' \
        --exclude='*.user' \
        --exclude='*.bak_*' \
        "$REPO_ROOT/" "$REMOTE:$JETSON_DEST/"
else
    log "rsync 건너뜀 (--no-rsync)"
fi

# ── 2) 원격 명령 결정 ──────────────────────────────────────────────────
if [ "$DO_RUN" = "0" ]; then
    REMOTE_CMD="cd $JETSON_DEST/HD_AMR && dotnet build HD_AMR.sln"
elif [ "$USE_WATCH" = "1" ]; then
    REMOTE_CMD="cd $JETSON_DEST/HD_AMR && dotnet watch --project HD_AMR.Web run --urls http://localhost:$BROWSER_PORT"
else
    REMOTE_CMD="cd $JETSON_DEST/HD_AMR && dotnet run --project HD_AMR.Web --urls http://localhost:$BROWSER_PORT"
fi

# ── 3) SSH (선택적 포트 포워딩) ────────────────────────────────────────
SSH_TUNNEL_OPT=()
if [ "$USE_TUNNEL" = "1" ] && [ "$DO_RUN" = "1" ]; then
    SSH_TUNNEL_OPT=(-L "$BROWSER_PORT:localhost:$BROWSER_PORT")
    log "원격 실행 + 포트 포워딩 ($BROWSER_PORT) — Mac 에서 http://localhost:$BROWSER_PORT 접속"
else
    log "원격 실행 (터널 없음)"
fi
log "Ctrl+C 한 번이면 SSH/dotnet 모두 종료"
echo

# shellcheck disable=SC2086
exec ssh -t $SSH_OPTS ${SSH_TUNNEL_OPT[@]+"${SSH_TUNNEL_OPT[@]}"} "$REMOTE" "$REMOTE_CMD"
