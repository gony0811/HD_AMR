# 비전 검사 S/W 인터페이스 사양

자동화 S/W(HD_AMR) ↔ 비전 검사 S/W 간 TCP 통신 프로토콜 사양이다.

**적용 버전: v3.2** (개정분 `vision_interface_v3.2_revision_20260915`, = HD_ACS↔SAIGE 연동
사양서 v2.6 부록 B). CAPTURE_REQ DATA 가 15 → 34바이트로 확장되어 `taskId`(GUID 16B 바이너리)·
`PosZ`(면-로컬 h)·`attempt`·`captureSeq` 가 추가되고, Surface ID 는 Wall ID 로 개명되었다.
프레임 구조·체크섬·기존 필드 오프셋([0]~[10])은 불변. 구현 기준은
`HD.AMR.App/Communication/Vision/`.

### 구현 메모 (연동 시험 확인 대상)
- **taskId·attempt 는 ACS 발급.** VDA5050 `startWeldInspection` 액션의 `taskId`(GUID 문자열)·
  `attempt` 를 `WeldInspectionActionParser` 가 읽어 `SequenceContext.AcsTaskId`·`AcsAttempt` 로
  주입하고, ⑱ 검사 수행이 CAPTURE_REQ 에 그대로 실어 보낸다(배선 완료 2026-09-22,
  [VDA5050 §8.1](VDA5050_INTERFACE_SPEC.md#81-startweldinspection)).
  수신 위치는 최상위 `actionParameters` key 우선, `params.taskId`/`params.attempt` 도 수용한다.
  미연동/수동 실행/GUID 아닌 값 폴백 = `taskId`=`Guid.Empty`, `attempt`=1 (액션은 실패시키지 않고
  경고 로그만 남긴다 — 이 경우 비전이 검사 이력을 누적할 키가 없다).
- **captureSeq 는 로봇 발번.** 검사 실행(=TASK) 내 촬영마다 1부터 증가.
- **(u,v,h) 는 코봇 wobj pose 직접.** `FaceLocalMapper` 가 코봇 (X,Y,Z)→(u,v,h) 매핑(현재 항등).
  축·부호는 wobj 티칭 규약(§5)에 흡수, h=0=벽 표면 정합은 물리 캘리브레이션 확인 대상.
- **taskId 바이트 순서**: RFC 4122 빅엔디안(`GuidBinary`). 비전 S/W 와 최종 확인.

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

### 3.2 CAPTURE_REQ (`0x02`) DATA (34바이트, v3.2)

| 오프셋 | 필드 | 형식 |
|---|---|---|
| [0] | Surface Type | 1B (§4) |
| [1-2] | Wall ID | UInt16 LE (§5, 구 Surface ID) |
| [3-6] | PosX = u (mm) | Int32 LE (면-로컬) |
| [7-10] | PosY = v (mm) | Int32 LE (면-로컬) |
| [11-14] | PosZ = h (mm) | Int32 LE (표면 높이) |
| [15-30] | taskId | GUID 16B 바이너리 (RFC 4122 빅엔디안) |
| [31] | attempt | UInt8 (1부터, ACS 발급) |
| [32-33] | captureSeq | UInt16 LE (시도 내 1부터, 로봇 발번) |

전체 프레임 = 9 + 34 = 43바이트. 수신 시 DATA 길이로 버전 판별(34=v3.2, 15=v2 레거시,
그 외=폐기·재동기화).

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

## 5. Wall ID 정의 (v3.2 축 정본)

구 Surface ID. Wall ID `0x01`~`0x0A`. 축 방향은 v3.2 §5(=SAIGE v2.6 부록 A, HD_ACS 내부
원점·축 기준)이며, v3.1 대비 U축 9면·V축 4면(바닥·천장·상부챔퍼2)이 반전되었다. 값 체계(1~10)·
바이트 위치는 불변.

| ID | Code | 면 | U / V 축 방향 |
|---|---|---|---|
| `0x01` | B | 바닥 (Bottom) | U: 선미→선수, V: 우현→좌현 |
| `0x02` | T | 천장 (Top) | U: 선미→선수, V: 우현→좌현 |
| `0x03` | PM | 좌현벽 (Port) | U: 선미→선수, V: 하단→상단 |
| `0x04` | SM | 우현벽 (Starboard) | U: 선미→선수, V: 하단→상단 |
| `0x05` | F | 전벽 (Forward) | U: 좌현→우현, V: 하단→상단 |
| `0x06` | A | 후벽 (Aft) | U: 우현→좌현, V: 하단→상단 |
| `0x07` | PL | 하부 좌현 챔퍼 | U: 선미→선수, V: 바닥→좌현 수직벽 |
| `0x08` | SL | 하부 우현 챔퍼 | U: 선미→선수, V: 바닥→우현 수직벽 |
| `0x09` | PU | 상부 좌현 챔퍼 | U: 선미→선수, V: 수직벽→천장 |
| `0x0A` | SU | 상부 우현 챔퍼 | U: 선미→선수, V: 수직벽→천장 |

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
3. `FaceLocalMapper.ToFaceLocal(wallId, X, Y, Z)`로 (u,v,h) 산출 후
   `CaptureReqPayload.Build(type, wallId, u, v, h, taskId, attempt, captureSeq)`로 **CAPTURE_REQ** 전송
   (captureSeq 는 촬영마다 증가).
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
