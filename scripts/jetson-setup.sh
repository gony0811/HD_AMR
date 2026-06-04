#!/usr/bin/env bash
#
# Jetson Nano Orin 일회성 셋업.
# .NET 8 SDK 설치 → 첫 dotnet build 트리거(Orbbec SDK 자동 다운로드) → udev 규칙 설치.
#
# 실행 위치: Jetson 의 ~/HD_AMR (또는 deploy-jetson.sh 가 동기화한 곳).
# 사용 예:
#   ssh user@jetson "cd ~/HD_AMR && bash scripts/jetson-setup.sh"
#
# 멱등(idempotent): 두 번째 실행 시 이미 설치된 단계는 건너뜀.

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SOLN_DIR="$REPO_ROOT/HD_AMR"

log() { printf '[setup] %s\n' "$*"; }
warn() { printf '[setup] WARN: %s\n' "$*" >&2; }
die() { printf '[setup] ERROR: %s\n' "$*" >&2; exit 1; }

# ── 1) OS 확인 ──────────────────────────────────────────────────────────
if [ ! -r /etc/os-release ]; then
    die "/etc/os-release 가 없습니다 — Ubuntu/Debian 기반 OS 가 아닌 것 같습니다."
fi
# shellcheck disable=SC1091
. /etc/os-release
UBUNTU_VER="${VERSION_ID:-}"
case "$UBUNTU_VER" in
    20.04|22.04|24.04) log "Ubuntu $UBUNTU_VER 감지" ;;
    18.04) die "Ubuntu 18.04 은 .NET 8 미지원입니다. JetPack 5+ 권장." ;;
    *) warn "검증되지 않은 Ubuntu 버전: ${UBUNTU_VER:-unknown}. 계속 진행하지만 실패 시 OS 확인 필요." ;;
esac

# ── 1.5) sudo 권한 사전 확보 ────────────────────────────────────────────
# 본 스크립트는 dpkg/apt/udev 등 여러 sudo 호출을 한다. 명령 모드 SSH(`ssh user@host "cmd"`)
# 는 기본적으로 TTY 가 없어 sudo 가 비밀번호 프롬프트를 띄울 수 없다. 시작하자마자 한 번
# 받아두면 이후 호출은 캐시 사용. TTY 가 없으면 친절한 메시지 후 종료.
if ! sudo -n true 2>/dev/null; then
    if [ ! -t 0 ]; then
        die "sudo 비밀번호가 필요한데 TTY 가 없습니다. SSH 호출에 -t 옵션을 추가하세요:
  ssh -t $USER@<host> \"cd ~/HD_AMR && bash scripts/jetson-setup.sh\""
    fi
    log "sudo 권한이 필요합니다 (한 번만 입력)…"
    sudo -v || die "sudo 인증 실패"
fi

# sudo 캐시 타임스탬프를 50초마다 갱신 — 빌드/다운로드가 길어져도 캐시 만료 방지.
# 스크립트 종료 시 cleanup() 가 keepalive 와 임시 파일을 모두 정리.
( while true; do sudo -nv 2>/dev/null || exit; sleep 50; done ) &
SUDO_KEEPALIVE_PID=$!
TMP_DEB=""
cleanup() {
    [ -n "${SUDO_KEEPALIVE_PID:-}" ] && kill "$SUDO_KEEPALIVE_PID" 2>/dev/null || true
    [ -n "${TMP_DEB:-}" ] && [ -f "$TMP_DEB" ] && rm -f "$TMP_DEB"
}
trap cleanup EXIT

# ── 2) .NET 8 SDK 설치 ──────────────────────────────────────────────────
if command -v dotnet >/dev/null 2>&1; then
    INSTALLED_VER="$(dotnet --version 2>/dev/null || echo unknown)"
    log "이미 설치된 dotnet: $INSTALLED_VER"
else
    log ".NET 8 SDK 설치 (Microsoft apt repo 등록)"
    DEB_URL="https://packages.microsoft.com/config/ubuntu/${UBUNTU_VER}/packages-microsoft-prod.deb"
    TMP_DEB="$(mktemp -t ms-prod-XXXXXX.deb)"
    wget -q "$DEB_URL" -O "$TMP_DEB" || die "Microsoft apt repo deb 다운로드 실패: $DEB_URL"
    sudo dpkg -i "$TMP_DEB"
    sudo apt-get update -y
    sudo apt-get install -y dotnet-sdk-8.0
    rm -f "$TMP_DEB"
    TMP_DEB=""
fi
dotnet --info | sed -n '1,15p'

# ── 3) 첫 빌드 → Orbbec SDK zip 자동 다운로드 ──────────────────────────
if [ ! -d "$SOLN_DIR" ]; then
    die "솔루션 디렉터리가 없습니다: $SOLN_DIR (Mac 에서 deploy-jetson.sh 를 먼저 실행하셨나요?)"
fi
log "초기 빌드 — Orbbec SDK v1.10.16 (linux-arm64) 자동 다운로드"
( cd "$SOLN_DIR" && dotnet build HD_AMR.sln -v minimal )

# ── 4) Orbbec udev 규칙 설치 ────────────────────────────────────────────
INSTALL_RULES_SH="$(find "$SOLN_DIR/HD_AMR/libs/orbbec/downloads" \
    -type f -name 'install_udev_rules.sh' 2>/dev/null | head -1)"

if [ -n "$INSTALL_RULES_SH" ]; then
    log "Orbbec 제공 udev 설치 스크립트 실행: $INSTALL_RULES_SH"
    sudo bash "$INSTALL_RULES_SH"
    sudo udevadm control --reload-rules
    sudo udevadm trigger
else
    warn "install_udev_rules.sh 미발견 — 수동으로 99-obsensor-libusb.rules 를 /etc/udev/rules.d/ 에 두세요."
fi

# ── 5) USB 권한 그룹 가입 ───────────────────────────────────────────────
if id -nG "$USER" | grep -qw plugdev; then
    log "$USER 는 이미 plugdev 그룹"
else
    log "$USER 를 plugdev 그룹에 추가 (로그아웃/재로그인 필요)"
    sudo usermod -aG plugdev "$USER"
fi

# ── 6) 카메라 확인 ──────────────────────────────────────────────────────
echo
log "── 디바이스 확인 ──"
if lsusb | grep -qi 'orbbec\|2bc5'; then
    lsusb | grep -i 'orbbec\|2bc5'
    log "Gemini 2 인식 OK"
else
    warn "Gemini 2 가 USB 3.0 포트에 보이지 않습니다. 직결(허브 없이) 권장."
fi

echo
log "셋업 완료."
log "다음 단계:"
log "  1) plugdev 그룹 적용을 위해 한 번 로그아웃 후 재로그인 (또는 \`newgrp plugdev\`)."
log "  2) Mac 에서: ./scripts/deploy-jetson.sh — Blazor UI 가 http://localhost:5253 에 뜹니다."
