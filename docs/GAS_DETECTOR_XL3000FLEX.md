# 가스 디텍터(INFICON XL3000flex) 연동 사양

가스 검출 AMR 은 비전 S/W 대신 스니퍼 리크 디텍터 **INFICON XL3000flex** 를 검사 센서로 쓴다(포밍가스 기밀 검사).
검사 시퀀스의 경유점마다 비전 `CAPTURE_REQ` 대신 측정값을 SAIGE 임계값과 비교해 OK/NG 판정하고 UI 에 표시하며,
Task 결과는 SAIGE 결과 본문 형식으로 ACS 에 보고한다.
구현 기준은 `HD.AMR.App/Communication/GasDetector/`, `HD.AMR.App/Service/GasDetectorService.cs`.

근거 문서: INFICON *Interface Description LDS3000* (XL3000flex 는 LDS3000 플랫폼), *XL3000flex Operating Instructions*,
SAIGE *HD_ACS ↔ SAIGE 포밍가스 기밀 검사 연동 규격 초안 v0.1* (2026-11-03).

## 1. 검사 센서 선택

`appsettings.json`

```json
"InspectionSensor": { "Mode": "GasDetector" }   // 또는 "Vision"
```

| 항목 | Vision | GasDetector |
|---|---|---|
| 기동 통신 서비스 | `VisionInterfaceService` (TCP 자동 접속) | `GasDetectorService` (시리얼/TCP 자동 접속) |
| ⑱ 검사 수행 경유점 동작 | CAPTURE_REQ / CAPTURE_RES | 대기 → 샘플링 → 임계값 판정 → NG 면 재측정 |
| SETTINGS 메뉴 | Vision Interface | Gas Detector |
| 상단 배지 | 비전 | 가스 |
| 장치 HEAVY 알람 | `visionLinkLost` | `gasDetectorLinkLost` |
| ACS 검사 액션 링크 확인 | 비전 S/W | 가스 디텍터 |
| `startWeldInspection` resultDescription | 요약 문자열 | **SAIGE 결과 본문 JSON** (§5) |

모드는 기동 시 한 번 읽는다 — 바꾸면 프로그램을 다시 시작한다.

## 2. 통신

| 항목 | 값 |
|---|---|
| 프로토콜 | ASCII (IO1000 기본값). 명령 `*…` + CR, 응답 한 줄(데이터 / `OK` / `Exx`) |
| 시리얼 | 19200 bps, 8N1, 핸드셰이크 없음 (`GasDetector:BaudRate` 등) |
| 이더넷 | 시리얼-이더넷 디바이스 서버의 TCP Server(Raw) 모드에 TCP Client 로 접속 |
| 응답 타임아웃 | 1500 ms (사양 권장) |
| 폴링 | 500 ms (사양: 100 ms 이상, 응답 수신 후 다음 명령) |
| 재연결 | 연속 무응답 3회 또는 소켓 오류 시 포트를 닫고 5초 주기 재시도 |

통신 경로(`Transport`: `Serial` / `Tcp`)·포트·IP 는 화면 **SETTINGS > Gas Detector** 에서 바꿀 수 있고,
`적용 · 저장` 하면 DB 파라미터(`GasDetector.*`)에 저장되어 appsettings 기본값보다 우선한다.

## 3. 장비 설정 (포밍가스)

- **가스 종류 H2** — 장비 Settings 에서 가스 종류(mass)를 H2(포밍가스 95% N₂ / 5% H₂)로 바꾸고, 포밍가스 교정 리크로
  교정한다. 교정·스니프 계수는 설정한 질량별로 저장된다.
- **인터페이스 단위 = 임계값 단위** — SAIGE 임계값 단위가 `ppm` 이면 장비의 *Interfaces > Units (interface) >
  Leak rate unit* (ASCII `*CONFig:UNIT:LRSniff`)를 ppm 으로 둔다. AMR 은 측정 전 이 단위를 조회해 임계값 단위와
  다르면 **판정하지 않고 측정 실패**로 처리한다(화면에 "단위 불일치" 경고). 단위 조회를 지원하지 않으면 경고 한 번만
  남기고 장비 단위가 임계값 단위라고 본다.

## 4. 경유점 측정·판정

1. 경유점 MoveL 도착, 진동 흡수(SettleDelaySec)
2. `MeasureDwellMs`(기본 2000 ms) 대기 — 가스가 스니퍼 프로브까지 오는 지연
3. `*READ?`(장비 설정 단위)를 `MeasureSampleCount`(기본 5)회, `MeasureSampleIntervalMs`(기본 200 ms) 간격으로 읽음
4. 측정 1건(scan)의 값 = 샘플 최대값. **임계값 이하 OK, 초과 NG** (같으면 OK — SAIGE 규격 §2)
5. OK 가 아니면(NG·측정 실패) 같은 자리에서 `MaxRetries`(기본 2, 최대 2)회까지 2~4를 반복 — `retry` 1·2
6. 포인트 판정 = 마지막 유효 측정의 판정(재측정에서 OK 가 나오면 OK). 유효 측정이 없으면 측정 실패
7. Task 판정 = 포인트 중 하나라도 NG 면 NG

- **임계값**은 Task 시작 시 한 번 잡아 끝까지 쓴다. 검사 도중 SAIGE 가 값을 바꾸면 측정 중인 Task 는 이전 값으로 끝내고
  다음 Task 부터 새 값을 쓴다. 결과의 `threshold` 에는 그 Task 판정에 실제로 쓴 값이 실린다.
- **u·v** 는 경유점의 코봇 wobj 좌표를 면-로컬 mm 정수로 바꾼 값이다(`FaceLocalMapper` — 비전 CAPTURE_REQ 와 동일 규약).
- **pointSeq** 는 티칭 경유점 순번(1부터)이다. θ 초과로 제외된 경유점이 있으면 번호가 건너뛴다.
- NG(리크 검출)는 정상적인 검사 결과라 시퀀스를 멈추지 않는다. 측정 **실패**만 레시피의 `VisionFailRatioMax`(실패율 상한)
  판정에 들어간다. 측정 1건씩 Gas Detector 화면의 결과 목록과 운영 로그(`[가스 측정]`)에 남는다.

## 5. ACS 인터페이스 (VDA5050) — 이노로보틱스 협의안

SAIGE 규격은 ACS ↔ SAIGE 구간만 정한다. AMR ↔ ACS 구간은 아래처럼 구현했고, **ACS 와 확정이 필요하다.**

### 5.1 회차 `round` — `startWeldInspection` params

`params.round` (정수 1 또는 2, 선택). 없으면 결과 본문에서 생략. 1·2 가 아니면 `orderValidationError` 로 거부한다.
기존 `params.taskId`(GUID) 는 SAIGE `taskId`, `position.drawingPos.tank` 는 `tankId` 로 쓴다.

### 5.2 측정 결과 — `startWeldInspection` actionState

가스 모드에서 액션이 끝나면(FINISHED) `actionStates[].resultDescription` 에 SAIGE §2 본문 JSON 을 그대로 싣는다.
`robotId` 는 VDA5050 `serialNumber`. ACS 는 이 문자열을 `POST /admin/v3/external/gas/results` 본문으로 보낸다.

```json
{"tankId":"CT1","taskId":"3f2b8c1e-…","round":1,"robotId":"AMR-01",
 "startedAt":1762151520000,"finishedAt":1762151785000,
 "threshold":{"value":5.0,"unit":"ppm"},"result":"NG",
 "points":[{"pointSeq":1,"result":"OK","scans":[{"retry":0,"u":1200,"v":340,"value":0.8,"result":"OK","measuredAt":1762151521000}]},
           {"pointSeq":2,"result":"NG","scans":[{"retry":0,…},{"retry":1,…},{"retry":2,…}]}]}
```

- 시퀀스가 실패(FAILED)하면 결과 본문을 싣지 않는다 — 기존처럼 에러 사유 문자열.
- 통신 실패로 값이 없는 scan 은 `value` 가 필수라 싣지 않고, 그런 포인트만 남으면 포인트·Task `result` 가 `"ERROR"` 다.
  **SAIGE 규격에 없는 값** — 처리 방식 협의 필요.

### 5.3 임계값 — instantAction `setGasThreshold`

ACS 가 SAIGE 의 `PUT /api/gas/threshold` 를 받으면 같은 필드를 instantAction 으로 내려준다.

```json
{"actionType":"setGasThreshold","actionId":"…","blockingType":"NONE",
 "actionParameters":[{"key":"tankId","value":"CT1"},{"key":"value","value":4.5},{"key":"unit","value":"ppm"},
                     {"key":"requestedBy","value":"operator01"},{"key":"requestedAt","value":1762152000000}]}
```

- 거부 조건: 가스 모드 아님 / 값 ≤ 0 / 단위 없음 / 단위가 장비 단위와 다름. 거부되면 이전 값을 유지한다.
- 적용하면 DB 에 저장해 재시작 후에도 마지막 값을 쓴다. 화면·운영 로그에 요청자·요청 시각·tankId 를 남긴다.
- 적용 여부는 `state.information` 의 `gasThreshold` 항목(`lastActionId`, `lastAccepted`, `lastError`)으로 알린다 —
  ACS 는 이를 보고 SAIGE 에 200 / 400 / 503 을 돌려준다. (응답 방식 협의 필요)

### 5.4 센서 상태 — `state.information`

| infoType | infoReferences | SAIGE §4 |
|---|---|---|
| `gasSensor` | `state` = MEASURING / STANDBY / CALIBRATING / WARMUP / ERROR / EMISSION_OFF / DISCONNECTED, `errorCode`(정수, 없으면 0) | `sensor.state`, `sensor.errorCode` |
| `gasThreshold` | `value`, `unit`, `source`(SAIGE/LOCAL), `tankId`, `lastActionId`, `lastAccepted`, `lastError` | — (임계값 적용 확인) |

`sensor.calibrated` 는 교정 상태를 직접 주는 ASCII 조회가 확인되지 않아 싣지 않는다(SAIGE 규격상 선택 항목).

## 6. 미확정 사항 (현장·협의 확인 필요)

- **RS485 결선 경로** — LDS3000 인터페이스 사양서상 IO1000 직렬 포트는 RS232 다. RS485 로 쓰려면
  RS232↔RS485 변환기를 두거나, 장착된 모듈이 RS485 를 지원하는지 확인한다. 프로토콜은 동일하다.
- **이더넷 방식** — 현재 구현은 디바이스 서버(투명 TCP) 방식이다. BM1000 버스모듈(Modbus TCP /
  EtherNet/IP / PROFINET)을 쓰게 되면 프로세스 데이터 매핑이 달라 별도 전송 계층을 추가해야 한다.
- **제어 위치** — `*STArt`/`*STOp`/`*ZERO` 가 `E06`(RS232 제어 권한 없음)으로 거부되면 장비의
  Control location 을 RS232(IO) 로 설정한다. 리크레이트 조회만은 제어 위치와 무관하다.
- **ppm 단위 응답 형식** — 장비를 ppm 으로 바꾼 뒤 `*CONFig:UNIT:LRSniff?` 응답 문자열과 `*READ?` 값 형식을 실기 확인.
- **에러 응답 형식** — `*STATus:ERRor?` 의 번호 표기(W/E 접두 등)를 실기 확인. 현재는 첫 숫자를 `errorCode` 로 쓴다.
- **calibrated** — 교정 상태 조회 방법(ASCII 명령 또는 경고 번호) 확인.
- **ACS 인터페이스(§5)** — `round`, 결과 JSON 전달, `setGasThreshold`, information 항목 모두 이노로보틱스와 확정.
- **측정 실패 포인트 표기** — §5.2 `"ERROR"` 처리를 SAIGE 와 협의.
- **임계값 기본값** — 로컬 기본 5.0 ppm 은 SAIGE 예시값(임시)이다. 운영은 SAIGE 수신값을 쓴다.

## 7. 사용 명령

| 명령 | 용도 | LD 번호 |
|---|---|---|
| `*READ?` | 리크레이트(장비 설정 단위) — **판정 기준값** | 128 |
| `*READ:MBAR*l/s?` | 리크레이트 [mbar·l/s] — 화면 참고 표시 | 129 |
| `*STATus?` | 운전 상태 STBY / ACCL / MEAS / CAL / EMI OFF / ERROR | — |
| `*STATus:TRIGger?` | 트리거 1~4 초과 여부 (ON/OFF ×4) | 385 |
| `*STATus:ERRor?` | 현재 에러/경고 (`NO ERROR/WARNING` 또는 번호) | 290 |
| `*CONFig:UNIT:LRSniff?` | 스니프 모드 단위 — 측정 전 임계값 단위와 대조 | 432 |
| `*STArt` / `*STOp` | 측정 시작 / 정지 | — |
| `*ZERO` | 배경 보정 | 6 |
| `*CLS` | 에러 클리어 | — |
