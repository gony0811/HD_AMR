# 헬륨 가스 디텍터(INFICON XL3000flex) 연동 사양

가스 검출 AMR 은 비전 S/W 대신 헬륨 스니퍼 리크 디텍터 **INFICON XL3000flex** 를 검사 센서로 쓴다.
검사 시퀀스의 경유점마다 비전 `CAPTURE_REQ` 대신 리크레이트를 측정해 OK/NG 판정하고 UI 에 표시한다.
구현 기준은 `HD.AMR.App/Communication/GasDetector/`, `HD.AMR.App/Service/GasDetectorService.cs`.

근거 문서: INFICON *Interface Description LDS3000* (XL3000flex 는 LDS3000 플랫폼), *XL3000flex Operating Instructions*.

## 1. 검사 센서 선택

`appsettings.json`

```json
"InspectionSensor": { "Mode": "GasDetector" }   // 또는 "Vision"
```

| 항목 | Vision | GasDetector |
|---|---|---|
| 기동 통신 서비스 | `VisionInterfaceService` (TCP 자동 접속) | `GasDetectorService` (시리얼/TCP 자동 접속) |
| ⑱ 검사 수행 경유점 동작 | CAPTURE_REQ / CAPTURE_RES | 대기 → 리크레이트 샘플링 → 판정 |
| SETTINGS 메뉴 | Vision Interface | Gas Detector |
| 상단 배지 | 비전 | 가스 |
| 장치 HEAVY 알람 | `visionLinkLost` | `gasDetectorLinkLost` |
| ACS 검사 액션 링크 확인 | 비전 S/W | 가스 디텍터 |

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

### 미확정 사항 (현장 확인 필요)
- **RS485 결선 경로** — LDS3000 인터페이스 사양서상 IO1000 직렬 포트는 RS232 다. RS485 로 쓰려면
  RS232↔RS485 변환기를 두거나, 장착된 모듈이 RS485 를 지원하는지 확인한다. 프로토콜은 동일하다.
- **이더넷 방식** — 현재 구현은 디바이스 서버(투명 TCP) 방식이다. BM1000 버스모듈(Modbus TCP /
  EtherNet/IP / PROFINET)을 쓰게 되면 프로세스 데이터 매핑이 달라 별도 전송 계층을 추가해야 한다.
- **제어 위치** — `*STArt`/`*STOp`/`*ZERO` 가 `E06`(RS232 제어 권한 없음)으로 거부되면 장비의
  Control location 을 RS232(IO) 로 설정한다. 리크레이트 조회만은 제어 위치와 무관하다.
- **판정 기준** — `LeakRateLimitMbarLs` 기본 1.0E-5 mbar·l/s 는 임시값이다.

## 3. 사용 명령

| 명령 | 용도 | LD 번호 |
|---|---|---|
| `*READ?` | 리크레이트(장비 설정 단위) — 화면 표시 | 128 |
| `*READ:MBAR*l/s?` | 리크레이트 [mbar·l/s] — **판정 기준값** | 129 |
| `*STATus?` | 운전 상태 STBY / ACCL / MEAS / CAL / EMI OFF / ERROR | — |
| `*STATus:TRIGger?` | 트리거 1~4 초과 여부 (ON/OFF ×4) | 385 |
| `*STATus:ERRor?` | 현재 에러/경고 (`NO ERROR/WARNING` 또는 번호) | 290 |
| `*CONFig:UNIT:LRSniff?` | 스니프 모드 단위 | 432 |
| `*STArt` / `*STOp` | 측정 시작 / 정지 | — |
| `*ZERO` | 배경 보정 | 6 |
| `*CLS` | 에러 클리어 | — |

## 4. 경유점 측정·판정

1. 경유점 MoveL 도착, 진동 흡수(SettleDelaySec)
2. `MeasureDwellMs`(기본 2000 ms) 대기 — 가스가 스니퍼 프로브까지 오는 지연
3. `*READ:MBAR*l/s?` 를 `MeasureSampleCount`(기본 5)회, `MeasureSampleIntervalMs`(기본 200 ms) 간격으로 읽음
4. 판정: 유효 샘플 0개 → **실패**, 최대값 ≥ 기준 → **NG**, 그 외 **OK**

NG(리크 검출)는 정상적인 검사 결과라 시퀀스를 멈추지 않는다. 측정 **실패**(통신)만 비전 실패와 같이
레시피의 `VisionFailRatioMax`(실패율 상한) 판정에 들어간다. 결과는 Gas Detector 화면의 측정 결과 목록과
운영 로그(`[가스 측정]`)에 남는다.
