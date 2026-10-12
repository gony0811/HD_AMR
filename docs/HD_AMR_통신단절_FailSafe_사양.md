# HD_AMR 통신 단절 Fail-Safe 사양 (rev.0 초안, 2026-10-10)

작업 Order 수행 중 WiFi 또는 관제 통신이 끊겼을 때 로봇이 스스로 작업을 멈추고, 통신이 돌아와도 작업자 확인 없이는 재개하지 않도록 하는 온보드 로컬 기능을 정의한다. FT-11 시험 기준의 근거 문서다. (위키: `20 HD-AMR/11 통신 단절 Fail-Safe`)

## 1. 목적 / 범위 / 전제

* 통신이 끊기면 관제 화면의 비상정지와 상태 감시가 사라진다. 원격 정지 경로를 잃은 상태로 로봇이 작업을 계속하지 않게 한다.
* 판정과 정지는 **AMR 온보드 로컬 기능**이다. 통신이 끊긴 상태에서 동작해야 하므로 관제에 의존하지 않는다.
* 짧은 끊김에는 작업을 계속한다. 선창 WiFi 특성상 순간 끊김마다 정지하면 검사 시간이 크게 늘어난다.
* 적용 대상: `operatingMode == AUTOMATIC` 이고 **수행 중인 Order가 있을 때**(주행 중 또는 action 실행 중).
  * MANUAL(작업자 현장 조작) 또는 대기(Order 없음) 상태에서는 기록과 표시만 하고 정지 동작은 하지 않는다.
* 이 기능은 운영상 Fail-Safe다. 안전 등급 기능은 통신과 무관한 로컬 장치가 담당한다 — 차체 EMO, 라이다 세이프티 존, 코봇 충돌 감지.

## 2. 통신 단절 판정

### 2.1 판정 조건 (하나라도 해당하면 단절)

|#|조건|감지 대상 장애|
|-|-|-|
|A|MQTT 브로커 연결 두절 (클라이언트 `Disconnected`)|로봇 쪽 WiFi 단절, 네트워크 장애, 브로커가 있는 서버 다운|
|B|ACS connection 토픽 = `CONNECTIONBROKEN` 또는 `OFFLINE`|관제 쪽 WiFi 단절, 관제 프로세스 비정상 종료·정상 종료|

* ACS connection 상태가 `Unknown`(수신 이력 없음)인 경우는 단절로 보지 않는다. 브로커 재접속 직후 retained 메시지 수신 전의 일시 상태이기 때문이다.
* 브로커가 로봇 PC에 있든 관제 서버에 있든, 위 두 조건으로 양쪽 모두 감지된다.
  * 브로커가 관제 서버에 있으면 로봇 WiFi 단절은 조건 A로 감지된다.
  * 브로커가 로봇 PC에 있으면 관제 쪽 단절은 관제 Last Will(조건 B)로 감지된다.

### 2.2 감지 지연

* MQTT 단절 감지는 keep-alive 주기에 따라 늦어진다. 기본값(15 s)이면 브로커가 상대 단절을 알아차리는 데 최대 22.5 s(1.5 × keep-alive)가 걸린다.
* 따라서 로봇과 관제 MQTT 클라이언트 모두 **keep-alive를 5 s로 명시**한다. 이때 감지 지연은 최대 7.5 s다.
* 단절 경과 시간은 **단절을 판정한 시각(t0)부터** 잰다. 실제 정지 시점은 T1 + 감지 지연(최대 약 37.5 s)이다.

### 2.3 복구 판정

브로커 연결 정상 **AND** ACS connection = `ONLINE` 일 때 복구로 본다.

## 3. 단계별 거동

|단계|경과 시간 (t0 기준)|주행 중|코봇 검사 중|표시|
|-|-|-|-|-|
|GRACE|0 ~ T1 (30 s)|계속|계속|로컬 로그만|
|PAUSE|T1 (30 s) 도달|감속 정지 (REST stop). 남은 경로는 유지|현재 웨이포인트 이동까지 마치고 정지. 다음 촬영으로 넘어가지 않음|타워 램프 **황색**|
|HOLD|T2 (60 s) 도달|정지 유지|정지 유지 (홈 복귀 등 추가 동작 없음)|황색 + **부저 경보**|

* GRACE 중에 복구되면 정지하지 않고 그대로 진행한다.
* PAUSE에서 진행 중 action은 `FAILED`가 아니라 **`PAUSED`** 로 둔다. Order도 폐기하지 않는다.
* HOLD는 선창 안 작업자가 로봇 정지를 알아차리게 하는 현장 경보 단계다. 코봇을 홈 자세로 옮기면 벽면·보강재와 충돌할 수 있어 추가 동작은 하지 않는다.
* 코봇을 웨이포인트 중간에 급정지하지 않는 이유: 벽면 근접 자세에서 궤적 중간 정지는 자세 복원 시 간섭 위험이 있다. 웨이포인트 1개 이동은 수 초 이내다.

## 4. 복구와 재개

### 4.1 원칙

* **통신이 복구돼도 자동 재개하지 않는다** (급발진 방지).
* 재개 경로는 두 가지다.
  1. 관제 화면 **이어하기(재개)** → instantAction `stopPause`
  2. 로봇 차체 **START 버튼** (작업자가 로봇 옆에 있을 때)
* 작업을 그만둘 때는 관제에서 `cancelOrder` 또는 run 중단.

### 4.2 재개 전 확인 (AMR)

다음을 모두 만족할 때만 재개한다. 하나라도 어긋나면 재개를 거부하고 사유를 `errors[]`로 보고한다.

* 비상정지 해제 상태
* 배터리 PowerMode = NORMAL (`docs/HD_AMR_배터리관리_사양.md`)
* 위치 추정 정상, 현재 mapId가 Order의 mapId와 일치
* operatingMode = AUTOMATIC

### 4.3 재개 동작

* 주행 중이었으면 같은 목표 노드로 REST 이동을 다시 지령한다.
* 검사 중이었으면 **정지 직전 웨이포인트부터 촬영을 다시 수행**한다(정지 시 촬영 성공 여부가 불확실하므로).
* action 상태 `PAUSED` → `RUNNING`, 타워 램프 녹색, 부저 해제.

## 5. VDA 5050 매핑

|항목|내용|
|-|-|
|state.paused|PAUSE 진입 시 `true`, 재개 시 `false`|
|actionStates|진행 중 action → `PAUSED` (재개 시 `RUNNING`)|
|errors[] (PAUSE)|`errorType = "communicationLostPause"`, `errorLevel = WARNING`, 설명에 단절 시각·경과 시간·조건(A/B)|
|errors[] (HOLD)|같은 errorType, 설명에 HOLD 단계 표기 (level은 WARNING 유지)|
|instantActions|`startPause` / `stopPause` 지원 — 현재 미지원(로그 후 무시) 상태라 구현 필요. `stopPause`는 4.2 확인 후 이행|
|재개 거부|`errorType = "resumeRejected"`, `errorLevel = WARNING`, 사유 포함|

* 복구 즉시 state를 발행해 관제가 PAUSE 상태를 바로 알게 한다.
* `communicationLostPause` 에러는 재개 또는 Order 취소 시 클리어한다.

## 6. 관제(HD_ACS) 측 요구사항

* MQTT keep-alive 5 s 명시.
* 로봇 state의 `paused = true` + `communicationLostPause` 수신 시:
  * 알람 `COMM_LOSS_PAUSED`(WARNING) 기록, 화면에 "통신 복구 — 재개 확인 필요" 표시.
  * **실패로 집계하지 않는다.** 재시도·재배차하지 않는다.
  * 운영자 **재개** 버튼 → `stopPause` 발행. 취소 시 `cancelOrder` 또는 run 중단.
* `resumeRejected` 수신 시 사유를 알람으로 표시.
* 로봇이 끊긴 동안 미션 상태는 기존 `Disconnected` 유지.

## 7. 우선순위

비상정지 > 배터리 CRITICAL > 통신 단절 PAUSE > 정상 운전

* 통신이 끊긴 동안에도 배터리 판정은 로컬에서 계속한다. CRITICAL이 되면 통신 단절 단계와 관계없이 그 자리 안전정지한다.
* 배터리 LOW 상태에서 재개 요청이 오면 4.2 확인에서 거부된다.

## 8. 파라미터

|파라미터|기본값|설정 키 (예정)|비고|
|-|-|-|-|
|T1 (PAUSE)|30 s|`Vda5050:CommLoss:PauseAfterSec`|주행 중 값 별도 단축 여부 검토 (§10)|
|T2 (HOLD)|60 s|`Vda5050:CommLoss:HoldAfterSec`|T1보다 커야 함|
|MQTT keep-alive|5 s|`Vda5050:KeepAliveSec`|로봇·관제 동일|
|기능 사용|true|`Vda5050:CommLoss:Enabled`|LAB 디버깅용 해제 가능, 현장 운영은 항상 true|

## 9. 시험 기준 (FT-11 개정안)

|#|조건|Pass 기준|
|-|-|-|
|1|주행 중 WiFi 차단 20 s 후 복구|정지 없이 주행 계속, 목표 노드 도착|
|2|주행 중 WiFi 차단 유지|t0 + 30 s(감지 지연 포함 37.5 s 이내)에 감속 정지, 황색 램프|
|3|코봇 검사 중 WiFi 차단 유지|현재 웨이포인트 이동 완료 후 정지, 다음 촬영 없음|
|4|차단 60 s 경과|부저 경보, 정지 유지|
|5|PAUSE 후 통신 복구|**30 s 이상 관찰해도 움직이지 않음**, 관제에 재개 대기 알람|
|6|관제 재개 명령|정지 직전 웨이포인트부터 재촬영, 남은 작업 정상 완료|
|7|복구 후 `cancelOrder`|정상 종료, 다음 Order 수락 가능|
|8|관제 프로세스만 종료 (브로커 유지)|조건 B로 같은 단계 거동|
|9|PAUSE 중 비상정지 → 해제 → 재개|비상정지 해제 전 재개 거부(`resumeRejected`)|

## 10. 미결 / 검토 사항

* **주행 중 T1**: 주행 중 30 s(감지 지연 포함 최대 약 37.5 s)는 원격 정지 수단 없이 움직이는 시간이다. 라이다 세이프티 존과 EMO가 있어 허용 가능하다고 보지만, 주행 중 T1만 짧게(예 10 s) 두는 방안을 현장 WiFi 끊김 패턴을 보고 결정한다.
* **브로커 위치**: HD_AMR 기본 설정은 `127.0.0.1`(로봇 PC), 관제 아키텍처 결정은 관제 서버에 Mosquitto 배치다. 판정은 양쪽 모두 커버하지만 운영 배치를 확정해 문서에 반영한다.
* **TARS-M 일시정지**: REST stop 후 같은 노드 재지령으로 재개하는 방식의 플랫폼 동작(감속 프로파일, 재출발 거동)을 LAB에서 확인한다.
* **타워 램프·부저 패턴**: 황색 점등/점멸, 부저 간헐 패턴을 FT-16 표시 규칙과 맞춘다.

## 11. 구현 과제

**HD_AMR**

* 통신 단절 감시 서비스(조건 A/B, t0 기록, T1/T2 타이머, 복구 판정).
* Order 실행기 PAUSE/RESUME 훅: 주행 REST stop·재지령, 코봇 웨이포인트 경계 정지, action `PAUSED` 상태.
* instantActions `startPause` / `stopPause` 구현 + 재개 전 확인(4.2).
* START 버튼 재개 경로, 타워 램프 황색·부저(LS IO).
* MQTT keep-alive 5 s 명시, `communicationLostPause` / `resumeRejected` 에러 발행.

**HD_ACS**

* MQTT keep-alive 5 s 명시.
* 알람 `COMM_LOSS_PAUSED` 추가, 실패 집계 제외.
* 로봇 상태 패널 "통신 복구 — 재개 확인 필요" 표시와 재개 버튼(`stopPause`).
