# Laser Displacement Sensor (레이저 변위 센서)

> **문서 상태: 기본 기능 구현됨.** EtherNet/IP Class1 implicit I/O(ForwardOpen)로 **CH1~3 변위 측정**과
> **채널별 영점 설정/해제**를 구현했습니다(매뉴얼 Z496 §4·Appendix A-2 기준). 아래 §3 표는 매뉴얼에서
> 확인한 실제 값으로 채웠습니다. 남은 하드웨어 튜닝 항목은 `MeasurementScale`(측정 스케일/단위)과
> `ConfigAssemblyInstanceId`(ForwardOpen Config 인스턴스) 두 가지이며, 실장비에서 확정합니다.

---

## 1. 개요 / 목적

- **무엇을 측정하나**: 대상 표면 3점에 대해 변위/거리(mm)를 측정하여 평면의 기울기/높이 보정에 활용. 
- **워크플로 상 위치**: 용접선/코로게이션 위치를 검출하기 전에 코봇 TCP 높이와 평면의 기울기를 보정하는 용도로 사용. (코봇 → 레이저 센서 → 비전 카메라 순서)
- **측정 주기 / 실시간성**: 폴링은 100ms 간격.

## 2. 장치 (Device)

| 항목 | 값                                             |
|---|-----------------------------------------------|
| 제조사 / 모델 | OMRON / (Head) ZP-LS300S / (Controller) ZP-EIP 
| 펌웨어 버전 | -                                             |
| EtherNet/IP 역할 | Adapter(타깃)                                   |
| 측정 범위 / 분해능 | -150mm ~ 150mm                                   |
| 물리 인터페이스 | RJ45 (EtherNet/IP)                            |

## 3. 통신 (EtherNet/IP)

- **IP / 포트**: 기본 `192.168.0.1` : `44818`(0xAF12, EtherNet/IP 표준 TCP 포트).
  `appsettings.json` 의 `LaserDisplacementSensor` 섹션에서 변경 가능하며, 페이지에서 접속 전 편집 가능.
- **세션 수명**: `RegisterSession` 으로 TCP 세션 등록 → `UnRegisterSession` 으로 해제.
  (구현: `HD_AMR/Communication/LaserDisplacementSensorClient.cs`, 라이브러리 `EEIP.NetStandard` 1.0.2,
  네임스페이스 `Sres.Net.EEIP`, 클래스 `EEIPClient`.)
- **메시징 방식**: **Implicit (Class1) I/O — 구현 채택** (측정값은 이 방식으로만 읽을 수 있음. Assembly
  오브젝트는 explicit 메시지 오브젝트 표에 없어 explicit 폴링 불가.)
  - ZP-EIP는 EtherNet/IP Adapter. PC(Scanner)가 `ForwardOpen`으로 "Full"(Exclusive Owner)·Point-to-Point 연결 생성.
  - 측정값/상태는 **T→O Input Assembly(인스턴스 110, 276B)**를 RPI 주기로 수신 → 파싱.
  - 영점 등 명령은 **O→T Output Assembly(인스턴스 132, 24B)**로 송신(EEIP `O_T_IOData`, RPI마다 재송신).
  - RPI 기본 50ms(장치 사양 1~10000ms). 구현: `HD_AMR/Communication/LaserDisplacementSensorClient.cs`.

  - O = Originator = Scanner = PLC/PC
  - T = Target     = Adapter = ZP-EIP
  - T→O = ZP-EIP → PLC/PC : 측정값, 상태, 알람 
  - O→T = PLC/PC → ZP-EIP : 명령, 제어 출력
  - 
- **CIP 오브젝트 매핑** (매뉴얼 Z496 §4 확인값):

| 용도 | Class ID | Instance | 크기 | 방향 | 비고 |
|---|---:|---:|---:|---|---|
| 측정값/상태 Input Assembly | `0x04` | `110` | 276B | T→O (장치→PC) | ForwardOpen 로 수신. 아래 I/O 데이터 맵 참조 |
| 명령/영점 Output Assembly | `0x04` | `132` | 24B | O→T (PC→장치) | ForwardOpen 로 송신 |
| 장비 정보 확인(선택) | `0x01` | `0x01` | — | Explicit | Identity Object |

- **Input Assembly(110) I/O 데이터 맵** (채널 N = 1-based, little-endian):

| 항목 | 바이트 오프셋 | 형식 |
|---|---|---|
| 측정값(변위) | `48 + (N-1)*4` (CH1=48, CH2=52, CH3=56) | 32bit signed int |
| Sensor Enable(측정범위 내/유효) | byte 10, bit (N-1) | 채널별 비트 (byte 8-9는 Reserved; 라이브 hex 확인 권장) |
| Sensor Error / Warning | byte 2 / byte 4, bit (N-1) | 채널별 비트 |
| 판정 HIGH / LOW / PASS | byte 18 / 20 / 22, bit (N-1) | 채널별 비트 (Sensor Output 1/2/3; 라이브 확인 필요) |

- **Output Assembly(132) — 영점 제어**: External Input Request 2(=Zero Reset) = **byte 2**, 채널별 비트
  (CH1=bit0 … CH8=bit7, CH9~16=byte 3). 비트 ON=현재값을 0으로(영점 설정), OFF=실제값 복원(영점 해제).

- **Implicit 파라미터** (Class1 사용 시):
  - 통신 역할:
    - Scanner / Originator: PLC 또는 PC 프로그램
    - Adapter / Target: ZP-EIP
  - 연결 방식: EtherNet/IP Class1 Implicit I/O
  - 연결 생성: `ForwardOpen`
  - 데이터 방향:
    - `T→O`: ZP-EIP → Scanner, 측정값/상태/알람 수신
    - `O→T`: Scanner → ZP-EIP, 제어/리셋/명령 송신
  - Assembly:

  | 항목 | 값 | 비고 |
    |---|---:|---|
  | Input Assembly, `T→O` | EDS 확인 필요 | ZP-EIP가 Scanner로 보내는 측정값/상태 데이터 |
  | Input 길이 | EDS 확인 필요 | byte 단위 |
  | Output Assembly, `O→T` | EDS 확인 필요 | Scanner가 ZP-EIP로 보내는 명령 데이터 |
  | Output 길이 | EDS 확인 필요 | byte 단위. 명령이 없으면 0 byte 또는 heartbeat만 존재할 수 있음 |
  | Configuration Assembly | EDS 확인 필요 | 장치에 따라 `0`, `1`, 또는 별도 instance 사용 |
  | Configuration 길이 | EDS 확인 필요 | 보통 0 byte인 경우도 있음 |
  | RPI | 10,000 ~ 100,000 µs 권장 | 예: 10 ms = `10000`, 50 ms = `50000`. 센서 응답속도와 네트워크 부하에 맞춰 조정 |
  | Real-Time Format | EDS 확인 필요 | 일반적으로 `32-bit Run/Idle Header` 또는 `Modeless` 중 장치 지원값 사용 |
  | Priority | Scheduled 또는 Low | PLC/Scanner 설정 가능 범위에 따름. 일반 측정값이면 Low/Default도 가능 |
  | Transport Type | Point-to-Point 또는 Multicast | 단일 Scanner면 Point-to-Point 권장 |
> docs/z496_ethernet_ip™_communication_unit_users_manual_en.pdf 파일 확인 요망

## 4. 설정 (Settings)

`HD_AMR/Communication/LaserDisplacementSensorSettings.cs` ↔ `appsettings.json` `"LaserDisplacementSensor"` 섹션:

| 키 | 기본값 | 설명 |
|---|---|---|
| `Name` | `LaserDisplacementSensor` | 표시용 이름 |
| `IpAddress` | `192.168.0.1` | 대상 장치 IP |
| `Port` | `44818` | EtherNet/IP TCP 포트 |
| `AutoConnect` | `false` | 기동 시 자동 접속 여부(재접속 루프 초기 활성 상태) |
| `ConnectTimeoutMs` | `3000` | 세션 등록 타임아웃 |
| `ReconnectDelayMs` | `5000` | 활성 상태에서 재접속 시도 간격 |

> 추가로 필요한 설정(예: CIP 오브젝트 ID, RPI, 스케일 계수)은 확정 후 이 POCO 와 섹션에 추가.

## 5. 서비스 API

`HD_AMR/Service/LaserDisplacementSensorService.cs` (`BackgroundService`, 싱글톤 + 호스티드):

- `Task ConnectAsync(CancellationToken)` — 재접속 루프 활성화 + 즉시 1회 접속 시도.
- `Task DisconnectAsync(CancellationToken)` — 재접속 루프 비활성화 + 즉시 세션 해제.
- `bool IsConnected` — 현재 세션 등록 여부.
- `bool IsEnabled` — 재접속 루프 활성 여부.
- `string? LastError` — 마지막 접속 실패 메시지.
- `LaserDisplacementSensorSettings Settings` — 편집 가능한 접속 설정.
- **TODO** `Task<double> ReadMeasurementAsync(CancellationToken)` — 클라이언트에 스텁만 존재
  (`NotImplementedException`). 3장 CIP 매핑 확정 후 구현.

**동작 규약**: 다른 장치(AMR/Cobot/Camera)는 상시 자동 접속이지만, 레이저 센서는 페이지의 [연결]/[해제]
버튼으로 켜고 끄는 `_enabled` 플래그로 재접속 루프를 게이팅한다(요청 사양). 활성 상태에서 연결이 끊기면
`ReconnectDelayMs` 간격으로 자동 재접속한다.

## 6. UI

- 라우트: `/laser` (내비게이션 "Laser Sensor" 버튼 → `HD_AMR.Web/Components/Pages/LaserDisplacementSensor.razor`).
- 통신 연결 카드: IP/포트 입력(접속 중엔 비활성), 상태 배지(연결됨/끊김), [연결]/[연결 해제] 버튼, 오류 표시.
- "측정값" 카드: 현재 플레이스홀더. 사양 확정 후 실시간 값·그래프 등 구현.
- channel 1, 2, 3 3개 센서에 대해서 변위 표시, zero set/reset 기능

## 7. 미해결 항목 / TODO (구현 전 확정 필요)

- [ ] 센서 제조사·모델·펌웨어, EtherNet/IP 역할(Adapter/Scanner).
- [ ] 메시징 방식: Explicit 폴링 vs Implicit(Class1) I/O(또는 병행).
- [ ] CIP 클래스/인스턴스/어트리뷰트 ID, 데이터 타입, 바이트 순서.
- [ ] Implicit 사용 시 어셈블리 인스턴스/길이/RPI/포맷.
- [ ] 값 스케일링(계수·오프셋·단위), 무효값/알람 처리 규칙.
- [ ] 측정 주기(폴링 간격) 또는 스트리밍 표시 요구사항.
- [ ] 측정값 UI(수치/그래프/로그/임계값 경보 등) 요구사항.
