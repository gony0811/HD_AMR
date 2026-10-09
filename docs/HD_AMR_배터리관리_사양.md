# HD\_AMR 배터리 관리 사양 (rev.1, 2026-10-09)

AMR 온보드 로컬 전력 관리 사양. 저전력 시 Order 수락 제어, 배터리 교체 유도, 알람 체계를 정의한다.

## 1\. 목적 / 범위 / 전제

* 배터리 잔량 판정·LOW 진입·Order 거부는 **AMR 온보드 로컬 기능**이다 (통신두절 환경에서도 독립 동작 필수).
* ACS↔AMR 인터페이스는 VDA 5050 단일 채널. 본 문서의 "AUTO 상태" = VDA 5050 `operatingMode == AUTOMATIC`.
* 잔량 판정 입력 = `state.batteryState.batteryCharge` (%).
* 전원 공급 방식 = **배터리 팩 교체(swap)**. 로봇은 자체 충전하지 않는다 (→ §6, §7).

## 2\. 임계값 체계 (히스테리시스 + 디바운스)

단일 20% 한 점은 모터 부하 시 전압/SoC 새그로 경계 플래핑을 유발하므로 다단 + 디바운스를 적용한다.

|레벨|진입 조건|거동|알람|
|-|-|-|-|
|NORMAL|—|정상 Order 수락|—|
|LOW (LIGHT)|≤ 20%|신규 Order REJECT, 진행 TASK 마무리, (AUTO) 교체 이동 Order만 ACCEPT|WARNING|
|CRITICAL|≤ 10%|진행 TASK도 안전지점 중단, 그 자리 안전정지|FATAL|
|RESUME (해제)|새 팩 SoC ≥ 80% **AND** 교체완료 신호|LOW 해제, 정상 수락 복귀|해제|

* **디바운스**: 임계 미만이 연속 2 state 주기(≈4s) 지속 시 천이. 순간 부하 새그에 의한 오천이 방지.
* RESUME은 교체 방식이므로 "충전 곡선 도달"이 아니라 **팩 교체로 SoC가 80% 이상으로 계단 점프 + 교체완료 신호**로 판정한다.

## 3\. 전력 상태 모델 (PowerMode — 미션 상태머신과 직교)

전력 상태를 미션 상태머신(Stateless)에 혼입하지 않고 **직교 상태 PowerMode**로 두어 Order 수락 경로에만 게이트로 건다. 미션 로직이 전력 상태로 오염되지 않는다.

```
PowerMode ∈ { NORMAL, LOW, CRITICAL, SWAP }
```

* `batteryState.charging` 플래그는 교체 방식이므로 **항상 false** 유지. 교체 상태는 `charging`이 아니라 PowerMode=SWAP + actionStates로 표현한다.

## 4\. Order 수락 게이트 (admission filter)

수신 Order를 미션 상태머신에 넘기기 전 통과시키는 필터.

```
입력: powerMode, operatingMode, actionType

if powerMode == NORMAL:
    ACCEPT
elif powerMode in { LOW, CRITICAL }:
    if operatingMode == AUTOMATIC and actionType in ALLOWED\_IN\_LOW:
        ACCEPT
    else:
        REJECT(errorType = "orderRejectedBatteryLow")

ALLOWED\_IN\_LOW = { BatterySwapMoveAction }
상시허용(instantActions) = { cancelOrder, startPause, stopPause,
                             stateRequest, factsheetRequest, E-STOP 관련 }
```

즉 LOW/CRITICAL + AUTO에서는 **배터리 교체 이동 Order만** 화이트리스트로 통과, 나머지 작업 Order는 전부 거부한다.

## 5\. "진행 중이던 TASK 마무리" 규칙 (확정)

TASK 불변식(TASK 1 = actionId 1 = 결과 1행 = 재시도 단위 1)에 따라:

* **현재 RUNNING인 actionId 1개는 끝까지 수행** (검사 중단 시 부분작업 폐기·재수행 낭비 방지).
* 현재 actionId → `FINISHED` 보고.
* **같은 Order의 남은 후속 노드/TASK는 이어가지 않고 정지**한다 (잔여 주행이 복귀 여유 잠식).
* 잔여 처리: AMR은 paused 유지 → ACS가 `cancelOrder`로 잔여 회수 → 교체 완료 후 `orderUpdateId + 1`로 잔여 재발행 (기존 "FAILED → orderUpdateId+1 재발행" 패턴과 동일).

## 6\. 배터리 교체 장소 \& 교체 유도

* 교체 지점은 고정 충전 도크가 아니라 **층(mapId)별로 지정된 "배터리 교체 장소(swap location)"** 다.
* `BatterySwapMoveAction` = 현재 층 교체 장소로 이동 → 구동 인터락 → 교체 대기 → 교체 완료.
* 층별 배치이므로 **최악 복귀거리가 현재 층 내로 한정**된다 (엘리베이터 수동 이동 불필요). 따라서 20% 임계의 도달성 검증은 "층 내 최원거리"만 보면 된다.
* 예비 배터리 팩은 교체 장소에서 상시 **SoC ≥ 80%(권장 \~95% 이상)** 로 충전 유지한다 — 교체 직후 자동 RESUME 및 유의미한 가동시간 확보 조건.

## 7\. 교체 방식 상세 (확정)

|항목|결정|내용|
|-|-|-|
|A. 교체 완료 감지|**자동 인식**|BMS 새 팩 인식 + SoC 계단 상승을 1차 자동 판정. 작업자 HMI/ACS 확인은 백업 경로.|
|B. 스왑 유형|**핫스왑**|브릿지 전원(슈퍼캡 등)으로 전원 무중단. VDA 5050 세션·Order·미션 상태가 유지되어 재부팅·재동기화 불필요.|
|C. 교체 중 인터락|**구동 인터락**|교체 장소 도착 즉시 드라이브 전원 차단 + 브레이크 체결. 교체 완료까지 구동 금지.|

## 8\. VDA 5050 매핑

VDA 5050에는 별도 REJECT ACK가 없으므로 `state.errors\[]`로 표현한다.

* **Order 거부**: `errors\[]`에 `errorType = "orderRejectedBatteryLow"`, `errorLevel = WARNING`(로봇 가동 중), `errorReferences = \[{orderId}, {actionId}]`. AMR은 거부한 Order의 orderId로 전환하지 않고 기존 orderId를 유지 → ACS는 이를 FATAL(실패)로 오해하지 않도록 규약화.
* **LOW(LIGHT) 알람**: `errors\[]`에 `errorType = "batteryLow"`, `errorLevel = WARNING`을 SoC 회복 전까지 **상주** 발행(information은 1회성이라 부적합).
* **CRITICAL 알람**: `errorType = "batteryCritical"`, `errorLevel = FATAL`.
* **교체 상태**: `batteryState.charging = false` 유지. 교체 진행은 `actionStates`로 표현 — INITIALIZING → RUNNING("ready for swap") → FINISHED. PowerMode=SWAP.
* **RESUME**: 새 팩 SoC ≥ 80% + 교체완료 신호 → `batteryLow` 에러 클리어 → PowerMode=NORMAL.

## 9\. 동작 시퀀스 (상태 천이)

1. NORMAL 운행 중 `batteryCharge < 20%`가 2주기 지속 → **PowerMode = LOW**, `batteryLow` WARNING 상주 발행.
2. 신규 작업 Order 수신 → 게이트에서 REJECT + `orderRejectedBatteryLow` 보고. 진행 중 actionId는 `FINISHED`까지 수행 후 정지(paused).
3. (AUTO) ACS가 `BatterySwapMoveAction` Order 발행 → 게이트 ACCEPT → **PowerMode = SWAP** → 현재 층 교체 장소로 이동 → 도착 시 **구동 인터락** → 교체 대기.
4. 작업자 **핫스왑** → BMS 자동 인식 + 새 팩 SoC ≥ 80% → `batteryLow` 클리어 → 인터락 해제 → **PowerMode = NORMAL** → 정상 수락 복귀 (핫스왑이므로 Order/미션 상태 그대로 재개 가능).
5. **CRITICAL (≤ 10%)**: 교체 장소 도달이 불확실하면 무리하게 이동하지 않고 **그 자리 안전정지** + `batteryCritical` FATAL → 작업자가 현장에서 교체.
6. **폴백 없음 (확정)**: AUTO+LOW에서 ACS가 교체 Order를 미발행하거나 통신두절이어도 **자율 이동하지 않고 그 자리 대기**. SoC 지속 하락 시 LOW → CRITICAL 알람 에스컬레이션이 작업자 호출 경로 역할을 한다 (교체는 본래 사람이 수행하므로 일관).
7. **MANUAL 분기**: LOW/CRITICAL 알람은 동일하게 발행하되 ACS는 교체 Order를 자동 발행하지 않는다(작업자 제어). 작업자가 수동으로 교체 장소 이동·교체.

## 10\. 확정 결정 요약

|#|항목|확정|
|-|-|-|
|1|CRITICAL / RESUME 임계값|10% / 80%·교체완료|
|2|저전력 시 잔여 Order 처리|현재 TASK만 마무리 후 정지, 잔여는 ACS cancelOrder → 교체 후 재발행|
|3|전원 보충 지점|고정 도크 아님 — 층별 지정 배터리 교체 장소|
|A|교체 완료 감지|자동 인식 (BMS + SoC 계단)|
|B|스왑 유형|핫스왑 (전원 무중단, 상태 유지)|
|C|교체 중 인터락|구동 인터락 (드라이브 차단 + 브레이크)|
|4|자율 충전/이동 폴백|없음 — 그 자리 대기, 알람 에스컬레이션으로 작업자 호출|

## 11\. 후속 구현 과제

* 온보드 PowerMode 상태기 + Order 수락 게이트 구현 (디바운스 포함).
* BMS 핫스왑 인식 연동 (새 팩 인식·SoC 점프 이벤트 → RESUME 트리거).
* 브릿지 전원 HW 사양 확정 (핫스왑 전원 무중단 보장 용량).
* 층별 교체 장소를 VDA 5050 노드(ref.node)로 등록 + mapId 바인딩.
* 20% 임계 도달성: 각 층 최원거리 → 교체 장소 주행 소요 SoC 실측 검증.
* ACS측: `orderRejectedBatteryLow`(WARNING) / `batteryLow` / `batteryCritical` 알람 해석 및 교체 Order 디스패치 로직.

