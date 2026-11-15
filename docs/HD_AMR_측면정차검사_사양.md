# HD_AMR 측면 정차 검사 구현 사양 (rev.0, 2026-11-15)

AMR이 벽과 나란히 서고 코봇만 J1을 ±90° 돌려 옆 벽을 검사하는 **측면 정차 검사**의 HD_AMR 구현 사양이다.
HD_ACS와의 협의(ACS 사양서 VDA 1.19 개정 예정)와 HD_AMR 운영 결정을 합친 문서다.

## 1. 목적 / 범위 / 전제

* 목적: 수직벽을 따라 늘어선 검사 영역을, AMR 방향을 매번 벽 정면으로 돌리지 않고 검사한다.
* 대상 면: 수직벽 4종 **PM / SM / F / A** 만. 바닥·천장·챔퍼는 1차 범위에서 제외한다(정면 검사만).
* 8가지 운용 경우(선수↔선미 × SM/PM, 좌현↔우현 × F/A)는 모두 **벽이 AMR의 왼쪽인가 오른쪽인가** 두 가지로 귀결된다.
* 기존 정면 검사(FRONT)는 동작이 바뀌지 않아야 한다. `stationMode`가 없는 지령은 지금과 똑같이 처리한다.

### 1.1 운영 결정 (HD_AMR)

|#|결정|비고|
|-|-|-|
|D1|AMR 이동은 **전진·후진만** 사용한다. 옴니휠 옆 이동은 가능하더라도 보류한다.|현장 보수적 운용|
|D2|측면 검사에서 정차 위치 오차는 **팔로만 보정**하고, 도달 범위 밖이면 **중단**한다. AMR 정차 보정 주행은 하지 않는다.|운영 확정|
|D3|벽을 따라 앞뒤 위치를 맞추는 전진·후진 재정차는 **1차에 넣지 않는다.** 실패 사유를 구분해 기록하고, 현장 로그를 보고 2차에 판단한다.|§7|
|D4|좌/우 검증 기준: 용접선의 벽 쪽 옆 성분이 **200 mm 이상**이어야 통과(`Side.LrMinLateralMm`, 현장 조정 가능).|§4|
|D5|측면 검사 준비 자세는 **좌/우 고정 슬롯 2개**(수직벽 4종 공용). 경로는 홈 → ready → 측면 검사 준비.|§5|
|D6|도달 실패 시 벽 방향 거리와 앞뒤 위치가 둘 다 맞지 않으면 **SIDE_REACH_NORMAL** 로 기록한다.|§6.1|

## 2. 용어 / 좌표 규약

|용어|정의|
|-|-|
|theta|노드 `nodePosition.theta` [rad]. **AMR이 서는 방향(차체 +X)**. 맵 X축 기준 CCW.|
|벽 방향(wallFacing)|AMR에서 벽을 향하는 수평 방향 [rad]. 면 법선의 방위각.|
|좌/우|theta 기준 차체 좌측(+Y) / 우측(−Y).|
|상대 높이|`용접선 z(층 바닥 기준) − 텔레스코픽 스트로크(완전 하강 = 0)` [mm]. ACS 지령 z와 같은 기준.|
|측면 준비 자세|J1을 ±90° 돌려 옆 벽을 향한 코봇 출발 자세(티칭 슬롯, §5.1).|

벽 방향 계산:

|stationMode|wallFacing|
|-|-|
|FRONT|theta (기존과 동일)|
|SIDE_LEFT|theta + π/2|
|SIDE_RIGHT|theta − π/2|

* **AMR 자세(yaw)와 벽 방향을 분리한다.** 좌표 변환 사슬(T_W_A)에는 항상 AMR yaw(theta 또는 실측 pose)를 쓰고, 면 법선·검사 방향·후퇴 방향에는 wallFacing을 쓴다.
* 측면일 때 theta는 **벽과 나란한 두 방향 중 하나**여야 한다. 어느 쪽인지는 ACS가 배차 시 정한다(진행 방향과 다를 수 있음).

## 3. 계약 (ACS → HD_AMR)

### 3.1 `stationMode` 파라미터

* 위치: `startWeldInspection` 과 `moveToSeamStart` 액션의 `params.stationMode` (string).
* 값: `FRONT` / `SIDE_LEFT` / `SIDE_RIGHT`. 대소문자 구분(정본 대문자).
* 누락 또는 null → `FRONT`.
* VDA 5050 노드에 사용자 값 칸이 없으므로, ACS는 **한 정차의 모든 해당 액션에 같은 값**을 넣는다.
* 리프트 높이(`liftStrokeMm`)는 ACS가 정차 단위로 하나만 정한다(한 정차 안에서 용접선마다 바뀌지 않음).

### 3.2 수신 검증 (order 단위, 거부 시 order 폐기)

`Vda5050OrderExecutor.Validate` 단계에서 검사한다. 실패하면 order 전체를 실행하지 않고 `errors[]`에 `orderValidationError`를 올린다(기존 거부 경로와 동일).

|#|조건|거부 사유 예|
|-|-|-|
|V1|정의되지 않은 `stationMode` 값|`stationMode 'SIDE' 미정의 (FRONT/SIDE_LEFT/SIDE_RIGHT)`|
|V2|한 노드 안에서 액션별 `stationMode`가 섞임(누락 = FRONT로 보고 비교)|`stationMode 혼재 (FRONT×2, SIDE_LEFT×1)`|
|V3|SIDE인데 `wall_code`가 PM/SM/F/A가 아님|`측면 검사는 수직벽(PM/SM/F/A)만 — wall_code=B`|
|V4|SIDE인데 노드 theta 누락|`측면 검사에 노드 theta 필요`|

### 3.3 하위 호환 / 배포 순서

* 구버전 HD_AMR은 `stationMode`를 무시하고 정면 검사로 진행해 **벽 충돌 위험**이 있다.
* 따라서 **HD_AMR을 먼저 배포**한다. ACS는 측면 기능을 설정으로 켜기 전까지 SIDE를 보내지 않는다(기본 꺼짐).

## 4. 좌/우 검증 (SIDE_LR_MISMATCH)

ACS의 좌/우 표시와 HD_AMR 계산이 다르면 벽 반대쪽으로 팔을 뻗게 되므로 검사 전에 막는다.

* 계산: 정차 노드 (x, y, theta) 기준으로 각 액션의 `seamStartW`·`seamEndW`를 차체 좌표로 바꾼다(`ReachStationFrame.WorldToBodyMm`). 차체 y(좌측 +)를 본다.
* 판정: 두 점 모두 `sign(y)`가 stationMode와 같고 `|y| ≥ Side.LrMinLateralMm`(기본 200 mm)이면 통과.
* 실패: 해당 액션 `FAILED`, resultDescription `SIDE_LR_MISMATCH: ...`(§7). 코봇·리프트는 움직이지 않는다.
* 시점: 주행 전 도달 사전 점검과 같은 단계에서 먼저 판정한다(주행 전에 알 수 있음). 액션 실행 직전에 한 번 더 확인한다.

## 5. 코봇 자세 / 운용 흐름

### 5.1 티칭 슬롯 추가

`TeachingService.Slots`에 고정 슬롯 2개를 추가한다(삭제·이름 편집 불가, 좌표만 티칭).
정면 수직벽 검사의 출발 자세인 `ready`(작업 준비 위치)에 해당하는 측면용 자세다. 수직벽 4종(PM/SM/F/A)이 같이 쓰므로 면별이 아니라 좌/우 2개만 둔다.

|키|이름|설명|
|-|-|-|
|`readySideL`|검사 준비 — 측면 좌|ready 자세에서 J1만 왼쪽 90° 돌린 자세(권장)|
|`readySideR`|검사 준비 — 측면 우|ready 자세에서 J1만 오른쪽 90° 돌린 자세(권장)|

* J1의 실제 부호(왼쪽 = +90° 인지 −90° 인지)는 장착 방향에 따라 다르다. 티칭 값이 정답이며, 저장 시 `ready`와 J1 차이가 90°±10° 범위가 아니면 경고한다.
* 측면 검사는 홈 모드(PoseMode.Home)에서만 한다. 홈 위치_2 모드면 기존 `ModeMismatch`와 같은 방식으로 실패한다.

### 5.2 진입 / 복귀 경로

```
영역 간 이동 : 코봇 홈(J1 0°) — 기존 EnsureCobotHomeBeforeDriveAsync 그대로
도착 후 진입 : 홈 → ready → readySide{L|R} → ② 거친 접근 → 이후 기존 검사 시퀀스
검사 종료    : 벽면 후퇴(기존 RetracePlanar/RetractFromWall) → readySide{L|R} → ready → 홈
```

* `SequenceEntry.PrepareAsync`, `ValidateRoute`, `ReturnToBaseAsync`에 측면 경유점을 추가한다.
* 복귀 시 현재 자세가 측면 쪽인지는 마지막 진입 모드를 기억해서 판단한다(기존 `RememberedMode`와 같은 방식). 앱 재시작으로 기억이 없으면 J1 각도로 가까운 쪽을 고른다.
* J1 회전은 ready ↔ readySide 사이 MoveJ 한 번으로 한다. 회전 중 간섭 여부는 현장 확인 항목(§11)이다.

### 5.3 기존 기능 측면 적용

|기능|측면에서 동작|조치|
|-|-|-|
|면 법선·접근점·TOOL 자세 (`SeamBaseTransform`)|wallFacing만 바꾸면 동작|`WallFacingThetaRad = wallFacing`|
|검사 방향 판정 (`SeamDirectionResolver`, `TaskBundlePlanner.Assign`)|theta를 벽 정면으로 가정|wallFacing을 넘긴다|
|정차 보정 (`TryAdjustStationAsync`, `PlanStationAdjustAsync`)|후진 = 벽에서 멀어짐을 가정|**SIDE면 실행하지 않는다**(D2). `params.stationAdjust`가 와도 무시하고 로그만 남긴다|
|도달 사전 점검·부분 수행 (`CobotReachPrecheck`)|정면 리치 맵 조회, 후진 대안 사용|측면 리치 맵 조회(§8), 후진 대안(backup) 끔|
|J6 안전 범위(−320°~−140°)|측면 자세는 정면 자세를 J1만 돌린 형태라 유지 예상|측면 리치 맵 계산 시 확인|
|진행축 가로 고정|영상 기준 — 영향 없음|없음|
|피치 이동 방향(용접선 끝점 쪽)|코봇 BASE 기준 계산 — 영향 없음|없음|

## 6. 정차 위치 오차 처리

* 팔 보정: 기존과 같다. 실측 AMR pose로 코봇 목표를 다시 계산한다.
* AMR 보정 주행: SIDE에서는 하지 않는다(D1, D2).
* 도달 불가: 액션 `FAILED`. 실패 사유를 §7의 두 가지로 구분한다.

### 6.1 실패 사유 구분 규칙

도달 실패한 용접선 점을 차체 좌표 (x: 벽 따라 앞뒤, y: 벽까지 옆 거리, z)로 놓고 측면 리치 맵에서 판정한다.

1. 그 상대 높이 구간에서 `|y|`가 [최소, 최대] 밖 → **SIDE_REACH_NORMAL** (벽 방향 거리 부족)
2. `|y|`는 범위 안인데 x 위치에서 도달 불가 → **SIDE_REACH_ALONG** (벽 따라 앞뒤 위치 부족)
3. 둘 다 해당하면 NORMAL을 우선한다(앞뒤 보정으로 해결되지 않으므로, D6).

## 7. 실패 보고 문구 (고정)

ACS 현장 실적 대조 도구가 앞머리 문구로 분류한다. **문구를 바꾸면 ACS에 사전 통보한다.**

|앞머리|의미|errorType|
|-|-|-|
|`SIDE_REACH_NORMAL:`|벽 방향 거리 부족|`inspectionFailed`|
|`SIDE_REACH_ALONG:`|벽 따라 앞뒤 위치 부족|`inspectionFailed`|
|`SIDE_LR_MISMATCH:`|stationMode 좌/우와 계산 결과 불일치|`orderValidationError`|

* 형식: `<앞머리> <사람용 설명> | key=value ...` 예)
  `SIDE_REACH_NORMAL: 벽까지 옆 거리 범위 밖 | mode=SIDE_LEFT lateralMm=1180 minMm=650 maxMm=1050 relZMm=420`
* 같은 내용을 운영 로그(OperationLog)에도 남긴다.

## 8. 측면 리치 맵

### 8.1 계산

* `ReachMap`에 벽 방향 입력을 추가한다. 지금은 `WallFacingThetaRad: 0`(벽 = 차체 +X)로 고정이다.
  * SIDE_LEFT: wallFacing = +90°(벽 = 차체 +Y), SIDE_RIGHT: −90°.
* 출발 자세는 `readySide{L|R}` 티칭 값(`StartJointsByWall` 대신 측면용 출발 자세).
* 격자 축: y = 벽까지 옆 거리, x = 벽 따라 앞뒤, z = 상대 높이.
* 저장 맵(`ReachMapCsv`) 키에 stationMode를 추가해 정면 맵과 섞이지 않게 한다.
* 좌우 각각 계산한다(코봇이 차체 중앙에 있지 않으면 결과가 다름).

### 8.2 ACS 전달 형식 (JSON 1개 파일)

```json
{
  "version": 1,
  "computedAt": "2026-11-20T10:00:00+09:00",
  "strokeRangeMm": [0, 1000],
  "default": {
    "PM": {
      "SIDE_LEFT":  [ { "relZMm": [0, 200],   "lateralMm": [650, 850, 1050], "minStrokeMm": null },
                      { "relZMm": [200, 400], "lateralMm": [600, 800, 1000], "minStrokeMm": 150 } ],
      "SIDE_RIGHT": "SAME_AS_SIDE_LEFT"
    }
  },
  "levelOverrides": {}
}
```

* `lateralMm` = [최소, 권장, 최대] 벽까지 옆 거리(벽면 ↔ 로봇 기준점).
* `minStrokeMm` = "이 구간은 스트로크 X mm 이상에서만 유효"(차체·리프트 기둥 간섭 등). 조건 없으면 null.
* 좌우가 같으면 `"SAME_AS_SIDE_LEFT"`로 표기한다.
* 리치 맵은 로봇 기준이라 **전 층 공통**(`default`). 특정 층에만 장애물이 있으면 `levelOverrides`에 층별 예외를 둔다.
* 리프트를 끈 운용(`Telescopic:Enabled=false`)은 고정 스트로크 하나로 판정한다(ACS 측 처리).
* 재계산하면 `version`을 올려 다시 전달한다.

### 8.3 ACS 판정 방식 (참고 — 협의 확정)

* ACS는 층+벽면마다 벽과의 거리를 하나로 고정하고, 영역별 리프트 높이로 높이를 맞춘다.
* 용접선이 지나는 **모든 상대 높이 구간**에서 고정 거리가 [최소, 최대] 안이어야 가능으로 판정한다(세로 용접선 포함).
* 공통 거리가 안 나오는 벽면은 그 벽면만 영역별 거리를 허용한다(예외).
* ACS는 벽 따라 ±0.2 m 어긋나도 닿는 정차를 고른다.

## 9. 코드 수정 범위

|영역|파일|내용|
|-|-|-|
|파싱|`Service/Inspection/WeldInspectionActionParser.cs`, `WeldInspectionRequest.cs`|`params.stationMode` 파싱(누락 = FRONT, 미정의 = 오류). moveToSeamStart 파서도 동일|
|수신 검증|`Service/Vda5050OrderExecutor.cs` `Validate`|V1~V4(§3.2)|
|벽 방향 계산|신규 `Service/Inspection/StationMode.cs`|enum + `WallFacing(theta, mode)` + 좌/우 검증(§4)|
|주행·정차 보정|`Vda5050OrderExecutor.cs` `RunMissionAsync`, `TryAdjustStationAsync`|AMR 이동은 theta 그대로. SIDE면 정차 보정 생략|
|사전 점검·부분 수행|`Vda5050OrderExecutor.cs`(1224~), `Service/Motion/CobotReachPrecheck.cs`|측면 맵 조회, SIDE면 backup 끔, 좌/우 검증 선행|
|검사 실행|`Service/Inspection/WeldInspectionOrchestrator.cs`(161, 363, 502, 753)|`nodeThetaRad` 대신 wallFacing 전달(검사 방향·WallFacingThetaRad). AMR yaw 용도와 분리|
|정차 보정 계산|`Service/Inspection/SeamApproachResolver.cs`(187, 247~254)|AMR yaw(`AmrYawRad`)와 벽 방향(`WallFacingThetaRad`)을 각각 받도록 분리|
|번들 계획|`Service/Inspection/TaskBundlePlanner.cs`|wallFacing으로 방향 분류|
|티칭|`Service/TeachingService.cs`|`readySideL`/`readySideR` 슬롯, J1 차이 경고|
|진입·복귀|`Service/Sequence/Steps/SequenceEntry.cs`|측면 경유점(§5.2), 측면 모드 기억|
|컨텍스트|`Service/Sequence/ISequenceStep.cs` `SequenceContext`|`StationMode` 추가(로그·복귀 경로용)|
|리치 맵|`Service/Motion/ReachMap.cs`, `ReachMapService`, `ReachMapCsv`|벽 방향 입력, 측면 출발 자세, 맵 키에 mode 추가|
|리치 맵 내보내기|신규|§8.2 JSON 생성(상대 높이 구간 집계)|
|실패 보고|`WeldInspectionOrchestrator.cs`, `IWeldInspectionExecutor.cs`|§6.1 분류, §7 문구|
|UI|Desktop `TeachingViewModel`, 리치 맵 화면, 시험 화면(`SeamMoveTestViewModel`)|측면 슬롯 표시, 측면 맵 계산·내보내기, 시험 시 stationMode 선택|

## 10. 파라미터

|키|기본|설명|
|-|-|-|
|`Side.LrMinLateralMm`|200|좌/우 검증 최소 옆 거리 [mm]. 이보다 벽 쪽 성분이 작으면 불일치로 본다|
|`Side.ReadyJ1ToleranceDeg`|10|측면 준비 자세 티칭 시 ready 대비 J1 90° 허용 오차 [°] (경고용)|

## 11. 시험 기준

### 11.1 단위 시험

* stationMode 파싱: 누락 → FRONT, 정의값 3종, 미정의 값 거부.
* 수신 검증 V1~V4.
* `WallFacing`: FRONT/LEFT/RIGHT × theta 4방향.
* 좌/우 검증: 8가지 운용 경우(§1)에서 올바른 좌/우 판정, 반대 표시는 MISMATCH.
* `SeamBaseTransform`: SIDE_LEFT 입력의 법선이 차체 +Y, TOOL 광축이 벽을 향함.
* SIDE일 때 정차 보정 미실행, 사전 점검 backup 미사용.
* 실패 사유 분류 NORMAL / ALONG / 둘 다(NORMAL 우선).
* FRONT 회귀: 기존 테스트 전부 통과.

### 11.2 현장 시험

|#|항목|합격 기준|
|-|-|-|
|F1|J1 ±90° 회전|ready ↔ readySide 회전 시 케이블 여유, 리프트 기둥·차체 간섭 없음|
|F2|벽 옆 나란히 정차|주행 S/W가 벽 옆 정차 목표를 거부하지 않음, 정차 오차 기록|
|F3|측면 검사 1회 완주|SIDE_LEFT·SIDE_RIGHT 각각 진입 → 검사 → 복귀 정상|
|F4|좌/우 불일치|일부러 반대 표시 → SIDE_LR_MISMATCH, 코봇 미동작|
|F5|도달 실패 분류|벽 거리를 일부러 벗어난 정차 → SIDE_REACH_NORMAL|
|F6|정면 회귀|기존 정면 검사 동작 변화 없음|

## 12. 미결 / 현장 확인

|#|항목|담당|
|-|-|-|
|O1|주행 S/W가 벽 옆에 나란히 서는 정차 목표를 받는지(벽 근처 장애물 여유 설정)|HD_AMR 현장/제조사|
|O2|J1 회전 방향(부호), 케이블 여유, 리프트 기둥 간섭|HD_AMR 현장|
|O3|측면 대상 면을 수직벽 4종으로 한정(V3) — ACS에 통보|HD_AMR → ACS|
|O4|`SIDE_LR_MISMATCH`의 errorType을 `orderValidationError`로 할지 ACS 확인|HD_AMR → ACS|
|O5|2차: 벽 따라 앞뒤 위치 보정(전진·후진 재정차) — SIDE_REACH_ALONG 빈도를 보고 판단|운영|

## 13. 구현 순서

1. stationMode 파싱·수신 검증·`StationMode` 유틸 + 단위 시험
2. 벽 방향 분리(orchestrator·SeamApproachResolver·TaskBundlePlanner), SIDE 정차 보정 생략
3. 측면 티칭 슬롯·진입/복귀 경로
4. 좌/우 검증, 실패 사유 분류·고정 문구
5. 측면 리치 맵 계산·사전 점검 연동
6. 리치 맵 JSON 내보내기 → ACS 전달
7. 배포(ACS보다 먼저) → 현장 시험 F1~F6 → ACS 측면 기능 켜기
