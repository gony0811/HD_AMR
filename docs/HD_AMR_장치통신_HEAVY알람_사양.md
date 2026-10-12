# HD_AMR 장치 통신 HEAVY 알람 사양 (rev.0, 2026-10-12)

AMR 탑재 장치 6종 가운데 하나라도 통신이 끊기거나 상태 확인이 안 되면 HEAVY 알람을 낸다. 알람 중에는 진행 중이던 임무를 멈추고 ACS order를 받지 않는다. 이 문서는 HD_AMR 온보드 동작과, 관제(HD_ACS)가 맞춰야 할 VDA 5050 계약을 정의한다.

## 1. 목적 / 범위 / 전제

* 검사·주행에 필요한 장치가 끊긴 상태로 임무를 계속하면 다음 위험이 있다.
  * 검사 결과 누락
  * 코봇·AMR 오동작
  * 원격 정지 불가
* 그래서 장치 이상을 **HEAVY(작업 불가)** 알람으로 보고 즉시 작업을 멈춘다.
* 판정과 정지는 **AMR 온보드 로컬 기능**이다. 관제와의 통신 상태와 무관하게 동작한다.
* ACS와 AMR 사이 통신 단절은 이 문서의 대상이 아니다. 별도 문서 `HD_AMR_통신단절_FailSafe_사양.md`가 다룬다.
* 대상 장치는 다음 6종이다.

|장치|통신 방식|비고|
|-|-|-|
|검사 비전카메라(비전 서버)|TCP 10.10.100.201:8000, HEARTBEAT 5 s 양방향|`docs/vision_interface.md`|
|Depth 카메라 (RealSense D435)|USB 3.0|정렬·평탄면 검출|
|3점 레이저 거리 센서|EtherNet/IP (TCP 44818 세션 + Class1 implicit)|평행·거리 보정|
|I/O 모듈 (LS산전)|Modbus TCP 10.10.100.202|EMO·RESET·타워램프·부저|
|코봇 (Fairino)|XML-RPC 20003 + 상태 소켓 20004|검사 이동|
|AMR (TARS-M)|Modbus TCP 10.10.100.200:502 (+ REST)|주행·위치|

## 2. 이상 판정

### 2.1 장치별 판정 조건

하나라도 해당하는 상태가 **3 s 이상 지속되면** 알람이다.

|장치|비정상 조건|사유 표기(예)|
|-|-|-|
|검사 비전카메라|TCP 미연결, 또는 비전 서버 수신(HEARTBEAT 포함)이 15 s 이상 없음|`TCP 미연결` / `HEARTBEAT 무응답`|
|Depth 카메라|미연결, 스트림 중지, 또는 프레임 수신이 2 s 이상 없음|`미연결` / `스트림 중지` / `프레임 수신 중단`|
|3점 레이저|EtherNet/IP 미연결, 또는 생존 확인(2 s 주기 TCP 도달성)이 6 s 이상 실패|`EtherNet/IP 미연결` / `생존 확인 실패`|
|I/O 모듈|Modbus 미연결, 또는 입력 읽기 성공이 2 s 이상 없음|`Modbus 미연결` / `입력 읽기 실패`|
|코봇|XML-RPC 미연결, 상태 소켓 끊김, 또는 상태 패킷이 2 s 이상 없음|`XML-RPC 미연결` / `상태 소켓(20004) 끊김` / `상태 패킷 중단`|
|AMR|Modbus 상태 읽기 실패, 또는 상태 갱신이 3 s 이상 없음|`Modbus 미연결/상태 읽기 실패` / `상태 갱신 중단`|

* 비전 서버와 레이저 센서는 "연결 플래그"만으로 판정하지 않는다. 케이블이 빠져도 연결 플래그가 유지되는 경우가 있어서, 수신 시각 또는 생존 확인으로 본다.
* 레이저는 생존 확인이 연속 2회 실패하면 세션을 닫고 재접속한다.

### 2.2 기동 유예

* 앱 기동 후 **30 s** 동안은 알람을 내지 않는다. 장치 최초 접속과 첫 프레임을 기다리는 시간이다.
* 유예가 끝난 뒤에도 끝내 정상이 되지 않은 장치는 알람이다. 기동 시점부터 연결이 안 되는 "상태 확인 불가"도 여기에 포함된다.

## 3. 알람 상태와 거동

### 3.1 상태 전이

```
Normal ──(이상 3 s 지속)──▶ Active(통신 두절)
Active ──(통신 복구)──────▶ Latched(복구됨, RESET 대기)
Latched ──(RESET)─────────▶ Normal
Latched ──(이상 재발 3 s)─▶ Active
Active ──(RESET)──────────▶ Active (유지 — 아직 끊김)
```

* HEAVY 알람은 **래치**한다. 통신이 복구돼도 자동으로 해제되지 않는다.
* "알람 중"은 Active 또는 Latched 장치가 하나라도 있는 상태다.

### 3.2 알람 발생 시 거동

|항목|동작|
|-|-|
|수행 중 임무|**즉시 중단**. AMR 감속정지(REST stop), 코봇 검사 시퀀스 중단, 남은 action `FAILED`(`resultDescription = "aborted: {장치} 통신 두절({사유}) (HEAVY)"`), 일시정지 상태 해소|
|신규 order|거부. §4.2 `orderRejectedDeviceFault`|
|instantActions|그대로 처리(`cancelOrder`, `emergencyStop`, `stateRequest` 등)|
|타워램프|적색|
|화면|상단 적색 배너(장치·상태·발생 시각) + "알람 리셋" 버튼|
|운영 로그|출처 `DEVICE`, 분류 `ALARM`: "HEAVY 알람 발생 — {장치}", "통신 복구 — {장치}", "알람 RESET"|

* 임무를 중단할 때 `emergencyStopActive`는 세팅하지 않는다. 비상정지와는 별개다.

### 3.3 해제 (RESET)

* 해제 수단:
  * 조작반 RESET 버튼(IN01) — 상승 에지
  * 화면 "알람 리셋" 버튼 — I/O 모듈이 끊겨 조작반 RESET을 읽을 수 없을 때를 대비한 수단
* RESET은 **Latched(복구된) 알람만** 해제한다. 아직 끊긴 장치는 Active로 남는다.
* RESET 버튼은 EMO 해제와 같은 버튼이다. 한 번 누르면 둘 다 처리된다.
* 모든 알람이 해제되면 state의 장치 오류가 사라지고 order를 다시 받는다.

## 4. VDA 5050 매핑

### 4.1 장치 알람 — `state.errors[]` (FATAL)

알람 중인 장치마다 errors에 1건씩 넣는다. 해제(RESET) 전까지 매 state 발행에 포함된다.

|장치|errorType|errorLevel|
|-|-|-|
|검사 비전카메라|`visionLinkLost`|FATAL|
|Depth 카메라|`depthCameraLinkLost`|FATAL|
|3점 레이저|`laserSensorLinkLost`|FATAL|
|I/O 모듈|`ioModuleLinkLost`|FATAL|
|코봇|`cobotLinkLost`|FATAL|
|AMR|`amrLinkLost`|FATAL|

* `errorReferences`: `[{ "referenceKey": "device", "referenceValue": <vision|depthCamera|laserSensor|ioModule|cobot|amr> }]`
* `errorDescription`:
  * 끊김(Active): `"{장치} 통신 두절 t0=HH:mm:ss — {사유}"`
  * 복구 후 RESET 대기(Latched): `"{장치} 통신 두절 t0=HH:mm:ss — 복구됨, RESET 대기"`
* 알람이 발생·복구·해제될 때마다 state를 즉시 발행한다. 2 s 주기 발행을 기다리지 않는다.
* 예시:

```json
{
  "errorType": "cobotLinkLost",
  "errorLevel": "FATAL",
  "errorDescription": "코봇 통신 두절 t0=14:03:21 — 상태 소켓(20004) 끊김",
  "errorReferences": [ { "referenceKey": "device", "referenceValue": "cobot" } ]
}
```

### 4.2 order 거부 — `orderRejectedDeviceFault` (WARNING)

알람 중에 order를 받으면 다음 규칙으로 거부한다.
* order를 폐기한다. 부분 실행도 하지 않는다.
* **기존 orderId는 유지**한다. 배터리 저전력 거부(`orderRejectedBatteryLow`)와 같은 계약이다.

|필드|값|
|-|-|
|errorType|`orderRejectedDeviceFault`|
|errorLevel|WARNING|
|errorDescription|`"장치 HEAVY 알람 — {장치}(끊김|복구됨·RESET 대기), … 복구 후 RESET 필요"`|
|errorReferences|`orderId` 1건, `actionId` 1건(order 첫 action, 없으면 생략), 알람 장치마다 `device` 1건씩|

* 판정 순서: 기존 order 검증(`orderValidationError`) → **장치 알람 게이트** → 배터리 게이트 → 수락.
* 장치 알람이 배터리 거부보다 우선한다.
* 다음 order가 수락되면 `orderRejectedDeviceFault`를 지운다.

### 4.3 errorReferences 전달 (변경 사항)

* 이전 버전은 state를 발행할 때 `errorReferences`를 누락했다. 이번 버전부터 모든 errors의 `errorReferences`를 그대로 발행한다.
* `orderRejectedBatteryLow`의 `orderId`·`actionId` 참조도 이제 실제로 전달된다.

## 5. 관제(HD_ACS) 측 요구사항

1. **errorType 등록:** 신규 7종을 등록한다 — §4.1의 6종과 `orderRejectedDeviceFault`.
2. **장치 알람(`*LinkLost`, FATAL) 수신 시:**
   * 알람 `DEVICE_LINK_LOST`(HEAVY)를 기록한다. 장치는 `errorReferences.device`로 구분한다.
   * 화면에 장치명, `errorDescription`, "현장 RESET 필요"를 표시한다.
   * 해당 로봇을 **배차 불가** 상태로 둔다. order를 보내지 않는다. errors에서 사라지면 배차를 재개한다.
   * 진행 중이던 run은 로봇이 이미 중단했다(남은 action `FAILED`). 관제는 이를 실패로 집계하고, 자동 재시도는 알람이 해제된 뒤에만 한다.
   * 에지로 감지한다(신규 출현 시 1회 알람, 사라지면 해제 기록). 매 state마다 중복 알람을 내지 않는다.
3. **`orderRejectedDeviceFault`(WARNING) 수신 시:**
   * `errorReferences.orderId`의 order를 거부된 것으로 처리한다(§4.5.2 거부와 같은 흐름).
   * 로봇의 기존 orderId가 유지되므로 FATAL로 해석하지 않는다.
   * 장치 알람이 남아 있는 동안 같은 order를 반복 재전송하지 않는다.
4. **errorReferences 파싱:** §4.3 변경에 따라 `errorReferences`가 채워져 온다. 기존 errorType의 파싱이 깨지지 않는지 확인한다.

## 6. 우선순위

비상정지 > 배터리 CRITICAL > **장치 HEAVY 알람** > 통신 단절 PAUSE > 정상 운전

* 장치 알람과 비상정지가 동시에 있으면 두 오류를 모두 보고한다. 해제는 각각 따로 해야 한다(EMO 해제 + 장치 알람 RESET). RESET 버튼 한 번으로 둘 다 처리된다.

## 7. 파라미터 (`appsettings.json` → `DeviceHealth`)

|파라미터|기본값|설정 키|비고|
|-|-|-|-|
|기능 사용|true|`DeviceHealth:Enabled`|현장 운영은 항상 true|
|기동 유예|30 s|`DeviceHealth:StartupGraceSec`||
|판정 지속 시간|3 s|`DeviceHealth:LostAfterSec`||
|장치별 감시 여부|모두 true|`DeviceHealth:Devices:{Vision,DepthCamera,Laser,IoModule,Cobot,Amr}`|미설치 장치는 false|
|AMR 상태 한계|3 s|`DeviceHealth:AmrStaleSec`|Modbus 1 s 주기|
|코봇 상태 한계|2 s|`DeviceHealth:CobotStaleSec`||
|I/O 입력 한계|2 s|`DeviceHealth:IoStaleSec`|폴링 200 ms|
|Depth 프레임 한계|2 s|`DeviceHealth:DepthCameraStaleSec`||
|비전 수신 한계|15 s|`DeviceHealth:VisionRxStaleSec`|HEARTBEAT 5 s × 3, 0이면 TCP 연결 여부만 판정|
|레이저 생존 한계|6 s|`DeviceHealth:LaserStaleSec`|생존 확인 주기 `LaserDisplacementSensor:AliveProbeMs`(2 s)|

## 8. 시험 기준

|#|조건|Pass 기준|
|-|-|-|
|1|장치별 케이블 분리(6종 각각)|약 3 s 내 알람(비전은 15 s 내), 타워램프 적색, state.errors에 해당 `*LinkLost` FATAL|
|2|2 s 분리 후 재연결|알람 없음|
|3|코봇 검사 중 코봇 LAN 분리|임무 중단, 남은 action `FAILED`, AMR 정지 유지|
|4|주행 중 AMR Modbus 분리|임무 중단, REST stop 시도, `amrLinkLost`|
|5|알람 중 order 발행|`orderRejectedDeviceFault`(orderId·device 참조 포함), 기존 orderId 유지|
|6|복구 후 RESET 전|errors에 `복구됨, RESET 대기`, order 계속 거부|
|7|복구 후 조작반 RESET|errors에서 제거, 다음 order 수락|
|8|I/O 모듈 분리 후 복구 → 화면 "알람 리셋"|해제|
|9|두 장치 중 하나만 복구 후 RESET|복구된 장치만 해제, 나머지 유지, order 계속 거부|
|10|장치 미연결 상태로 앱 기동|30 s 유예 후 알람|

## 9. 미결 / 검토 사항

* **알람 코드 체계:** 관제 알람 코드(`DEVICE_LINK_LOST`)와 등급(HEAVY/LIGHT) 표기를 HD_ACS 알람 표준에 맞춘다.
* **비전 HEARTBEAT:** 비전 서버가 실제로 5 s 주기 HEARTBEAT를 보내는지 현장에서 확인한다. 보내지 않으면 15 s마다 오탐이 나므로 `VisionRxStaleSec = 0`으로 바꾼다. 2026-10-12 배포 후 확인한 범위에서는 오탐이 없었다.
* **부저:** 현재 장치 알람에는 부저를 울리지 않는다(타워램프 적색만). 필요하면 FT-16 표시 규칙과 함께 정한다.
* **LIGHT 알람:** 이번 범위는 HEAVY(통신 두절)만 다룬다. 장치 경고(예: USB 2.x 폴백, 프레임 지연)를 LIGHT로 보고할지는 추후 정한다.

## 10. 구현 위치 (HD_AMR)

|구성|파일|
|-|-|
|알람 상태기(순수 로직)|`src/HD.AMR.App/Service/DeviceAlarmTracker.cs`|
|공유 상태(order 게이트·state·IO·UI 공용)|`src/HD.AMR.App/Service/DeviceAlarmState.cs`|
|감시 서비스(500 ms)|`src/HD.AMR.App/Service/DeviceHealthMonitorService.cs`|
|설정|`src/HD.AMR.App/Service/DeviceHealthSettings.cs`|
|order 게이트|`OrderAdmissionGate.cs`, `Vda5050OrderExecutor.HandleOrderAsync`|
|임무 중단|`Vda5050OrderExecutor.AbortForDeviceFaultAsync`|
|state.errors 보고|`Vda5050AdapterService.BuildState`|
|RESET·타워램프|`IoCommLossControl.cs`(`IoAlarmResetControl`), `IoTowerLampPolicy.cs`, `IoModuleService.cs`|
|시험|`src/HD.AMR.Tests/DeviceAlarmTests.cs`|
