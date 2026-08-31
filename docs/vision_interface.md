# 비전 검사 S/W 인터페이스 사양

자동화 S/W(HD_AMR) ↔ 비전 검사 S/W 간 TCP 통신 프로토콜 사양이다.

원 사양서는 `docs/비전 인터페이스_v2.xlsx`(저장소 미포함)이며, 이 문서는 실제 구현
(`HD_AMR/HD_AMR/Communication/Vision/`)을 기준으로 정리한 것이다.

## 1. 통신 개요

| 항목 | 값 |
|---|---|
| 프로토콜 | TCP/IP (바이너리 프레임) |
| 자동화 S/W | TCP **Client**, 장치 ID `0x01` |
| 비전 S/W | TCP **Server**, 장치 ID `0x02` |
| 서버 주소(기본) | `10.10.100.201 : 8000` (`appsettings.json` → `Vision` 섹션) |
| 접속 정책 | 기동 시 상시 자동 접속, 실패 시 5초 주기 재시도 |
| 접속 타임아웃 | 5000 ms |
| Heartbeat 주기 | 5000 ms |
| 재연결(전송계층) | 3초 간격 × 최대 10회 |

설정 키는 `VisionInterfaceSettings`(`ServerHost`, `Port`, `HeartbeatPeriodMs`,
`ReconnectDelayMs`, `MaxReconnectAttempts`)로 주입된다. 이 인터페이스는 서비스
(`VisionInterfaceService`)로 싱글톤·호스티드 등록되어 페이지 이동과 무관하게 연결·로그
상태가 유지된다.

## 2. 프레임 구조

```
STX(1) │ LENGTH(2, LE) │ SEQ(1) │ COMMAND(1) │ FROM(1) │ TO(1) │ DATA(n) │ CHECKSUM(1) │ ETX(1)
```

| 필드 | 크기 | 설명 |
|---|---|---|
| STX | 1 | 프레임 시작 `0x02` |
| LENGTH | 2 | `COMMAND + FROM + TO + DATA` 바이트 수 (= 3 + DATA 길이), Little-Endian |
| SEQ | 1 | 송신마다 0~255 순환 증가 |
| COMMAND | 1 | 명령 코드 (§3) |
| FROM | 1 | 송신자 장치 ID |
| TO | 1 | 수신자 장치 ID |
| DATA | n | 명령별 페이로드 (최대 256바이트) |
| CHECKSUM | 1 | LENGTH(2바이트) ~ DATA 전체의 XOR (SEQ·COMMAND·FROM·TO 포함) |
| ETX | 1 | 프레임 끝 `0x03` |

- 고정 오버헤드 9바이트, 전체 프레임 = 9 + DATA 길이.
- 체크섬은 전송 바이트(LE) 그대로 XOR 한다.

### 수신 파서 동작

- 바이트 스트림을 버퍼링하며 STX(`0x02`)를 탐색한다.
- LENGTH 범위 이탈, ETX 불일치, 체크섬 불일치 시 프레임을 폐기하고 STX 재탐색으로
  **재동기화**한다. 체크섬 오류 프레임은 폐기하며 **재전송을 요구하지 않는다**.

## 3. 명령 코드 (COMMAND)

| 코드 | 명령 | 방향 | 설명 |
|---|---|---|---|
| `0x01` | HEARTBEAT | 양방향 | 5초 주기 생존 신호 |
| `0x02` | CAPTURE_REQ | 자동화 → 비전 | 촬영/검사 요청 |
| `0x03` | CAPTURE_RES | 비전 → 자동화 | 검사 결과 응답 |
| `0x04` | ERROR_NOTI | 비전 → 자동화 | 오류 통지 |

### 3.1 HEARTBEAT (`0x01`) DATA (20바이트)

| 오프셋 | 필드 | 형식 |
|---|---|---|
| [0-13] | 타임스탬프 | ASCII 14자 (`yyyyMMddHHmmss`) |
| [14-19] | 예약 | ASCII `"000000"` |

### 3.2 CAPTURE_REQ (`0x02`) DATA (15바이트)

| 오프셋 | 필드 | 형식 |
|---|---|---|
| [0] | Surface Type | 1B (§4) |
| [1-2] | Surface ID | UInt16 LE (§5) |
| [3-6] | PosX (mm) | Int32 LE |
| [7-10] | PosY (mm) | Int32 LE |
| [11-14] | 예약 | `0x00` |

### 3.3 CAPTURE_RES (`0x03`) DATA

| 오프셋 | 필드 | 형식 |
|---|---|---|
| [0-1] | 결과 코드 | UInt16 LE (§6) |

## 4. Surface Type

| 값 | 이름 |
|---|---|
| `0x00` | Flat |
| `0x01` | Corner |
| `0x02` | Corrugation |

## 5. Surface ID 정의

ID `0x01`~`0x0A`, 모두 Flat 면이다.

| ID | 면 | U / V 축 방향 |
|---|---|---|
| `0x01` | 바닥 (Bottom) | U: 선수→선미, V: 좌현→우현 |
| `0x02` | 천장 (Top) | U: 선수→선미, V: 좌현→우현 |
| `0x03` | 좌현벽 (Port) | U: 선수→선미, V: 바닥→천장 |
| `0x04` | 우현벽 (Starboard) | U: 선수→선미, V: 바닥→천장 |
| `0x05` | 전벽 (Forward) | U: 좌현→우현, V: 바닥→천장 |
| `0x06` | 후벽 (Aft) | U: 좌현→우현, V: 바닥→천장 |
| `0x07` | 하부 좌현 챔퍼 | U: 선수→선미, V: 바닥→좌현벽 |
| `0x08` | 하부 우현 챔퍼 | U: 선수→선미, V: 바닥→우현벽 |
| `0x09` | 상부 좌현 챔퍼 | U: 선수→선미, V: 천장→좌현벽 |
| `0x0A` | 상부 우현 챔퍼 | U: 선수→선미, V: 천장→우현벽 |

## 6. 결과 코드

| 코드 | 이름 | 의미 |
|---|---|---|
| `0x0000` | SUCCESS | 성공 |
| `0x0001` | ERR_TIMEOUT | 타임아웃 |
| `0x0002` | ERR_CAMERA | 카메라 오류 |
| `0x0003` | ERR_POSITION | 위치 오류 |
| `0x0004` | ERR_SURFACE | 면 오류 |
| `0x0005` | ERR_BUSY | 사용 중 |
| `0x00FF` | ERR_UNKNOWN | 알 수 없는 오류 |

## 7. 검사 시퀀스에서의 사용 흐름

검사 실행(`InspectionRunStep`)은 티칭된 경유점을 코봇으로 순회하며 각 점에서 다음을
수행한다.

1. 경유점으로 코봇 이동 후 안정화 지연(`SettleDelaySec`) 대기.
2. Surface Type 판정: `|θ| ≥ 코로게이션 판정각`이면 `Corrugation`, 아니면 `Flat`.
3. `CaptureReqPayload.Build(type, surfaceId, X, Z)`로 **CAPTURE_REQ** 전송.
4. 프로필의 `DelaySec`를 타임아웃으로 **CAPTURE_RES / ERROR_NOTI** 수신 대기.
5. 결과 코드가 `SUCCESS`이면 OK 카운트, 아니면 실패로 기록(전송 여부·응답 여부·코드 로그).

응답 상관(correlation)은 SEQ 에코를 가정하지 않고, "직전에 보낸 요청 1건"과 매칭하는
단일 보류 슬롯 방식이다(검사는 한 점씩 순차로 대기하므로 단일 슬롯으로 충분).

## 8. 관련 소스

| 파일 | 역할 |
|---|---|
| `Communication/Vision/VisionProtocol.cs` | 명령/장치/면/결과 코드, CAPTURE_REQ 페이로드 |
| `Communication/Vision/VisionFrame.cs` | 프레임 인코딩·체크섬 |
| `Communication/Vision/VisionFrameParser.cs` | 바이트 스트림 파서·재동기화 |
| `Communication/Vision/VisionEngine.cs` | 세션 엔진(전송·Heartbeat·캡처 상관·로그) |
| `Communication/Vision/VisionTcpClientTransport.cs` | TCP 클라이언트 전송·재연결 |
| `Communication/Vision/VisionInterfaceSettings.cs` | 설정 바인딩 |
| `Service/VisionInterfaceService.cs` | 호스티드 서비스(상시 접속) |
| `Service/Sequence/Steps/InspectionRunStep.cs` | 검사 시퀀스에서의 캡처 요청 |
| `HD_AMR.Web/Components/Pages/VisionInterface.razor` | 수동 연결·프레임 송수신 UI |
