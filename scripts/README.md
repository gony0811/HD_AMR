# Remote deploy scripts (Mac → Jetson Nano Orin)

Mac 에서 Jetson 에 소스를 보내고, 거기서 빌드·실행해 Gemini 2 카메라 라이브 뷰를
Mac 브라우저로 받아보는 데 쓰는 셸 스크립트 두 개.

| 스크립트 | 실행 위치 | 빈도 | 역할 |
| --- | --- | --- | --- |
| `jetson-setup.sh` | **Jetson** | 1회 | .NET 8 SDK + Orbbec udev 규칙 설치 |
| `deploy-jetson.sh` | **Mac** | 매 반복 | rsync 소스 → 원격 build & run → SSH 터널 |

## 초기 셋업 (한 번만)

### 1. SSH 키 등록 (Mac)

```sh
# 키가 없다면 생성
ssh-keygen -t ed25519 -C "mac → jetson"

# Jetson 에 공개키 등록 (한 번만 비밀번호 입력)
ssh-copy-id jetson@<jetson-ip>
```

### 2. 환경변수 (Mac, `~/.zshrc` 등에)

```sh
export JETSON_HOST="192.168.x.x"   # 또는 jetson.local
export JETSON_USER="jetson"
# 옵션: export BROWSER_PORT=5253
# 옵션: export SSH_OPTS="-i ~/.ssh/jetson_id_ed25519"
```

### 3. Jetson 셋업 (Mac 에서 1회 트리거)

```sh
# 먼저 소스 동기화만 (dotnet 단계는 실패해도 OK — 아직 .NET 미설치)
./scripts/deploy-jetson.sh --build || true

# Jetson 에서 셋업 스크립트 실행 — `-t` 는 sudo 비밀번호 프롬프트를 위한 TTY 할당
ssh -t "$JETSON_USER@$JETSON_HOST" "cd ~/HD_AMR && bash scripts/jetson-setup.sh"

# plugdev 그룹 적용 위해 한 번 로그아웃/재로그인 (또는 newgrp plugdev)
```

스크립트 마지막에 `lsusb | grep -i orbbec` 가 Gemini 2 를 보여야 합니다. 안 보이면 USB 3.0
포트(허브 없이 직결)와 케이블을 확인하세요.

## 매번 사이클 (Mac)

```sh
./scripts/deploy-jetson.sh
# → rsync 소스 → 원격 dotnet run → 포트 포워딩 5253
# → Mac 브라우저: http://localhost:5253/camera

# Ctrl+C 한 번이면 SSH/dotnet 모두 종료
```

## 옵션 플래그

| 플래그 | 동작 |
| --- | --- |
| `--watch` | `dotnet watch` — 코드 저장 즉시 원격 재빌드 |
| `--no-rsync` | 동기화 건너뛰고 ssh 만 (이전 빌드 그대로 재실행) |
| `--build` | 빌드만, run 안 함 |
| `--no-tunnel` | 포트 포워딩 안 함 (다른 mac 인스턴스가 5253 점유 시) |

## 트러블슈팅

| 증상 | 원인 / 조치 |
| --- | --- |
| `Permission denied (publickey)` | `ssh-copy-id` 미실행 — 위 1단계 다시 |
| `sudo: a terminal is required` | `ssh` 에 `-t` 누락 — `ssh -t "$JETSON_USER@$JETSON_HOST" "..."` 로 재실행 |
| `bind: Address already in use` | Mac 의 5253 포트 점유 — `BROWSER_PORT=5254 ./scripts/deploy-jetson.sh` |
| Jetson 빌드에서 `error NETSDK1045` | .NET 8 SDK 미설치 — `jetson-setup.sh` 재실행 |
| `Camera 연결 실패 — uvc_open already opened` | udev 규칙 미적용 또는 plugdev 그룹 미적용 — `jetson-setup.sh` 다시 + 로그아웃/재로그인 |
| `Camera 연결 실패 — No device found` | USB 미인식 — `lsusb` 로 확인, 케이블/포트 변경, USB 3.0 직결 |
| 첫 빌드가 매우 느림 (1~2분) | NuGet restore + Orbbec zip (~17MB) 다운로드 1회분 — 정상 |
| `dotnet` 명령은 있는데 8.0 아님 | `apt list --installed | grep dotnet` 확인 후 `apt remove dotnet*` → setup 재실행 |

## 트래픽이 안 가는 경우 (네트워크)

- 같은 LAN 인지 확인: Mac 에서 `ping $JETSON_HOST`
- mDNS 안 풀리면 IP 직접 사용: `JETSON_HOST=192.168.1.50 ./scripts/deploy-jetson.sh`
- VPN 으로 sub-net 이 갈리면 LAN bridge 필요
