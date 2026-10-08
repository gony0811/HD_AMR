# HD_AMR `moveToSeamStart` 구현 가이드 (코봇 seam 시작점 이동 시험)

| 항목 | 내용 |
|---|---|
| 문서 성격 | **HD_AMR 구현 착수 가이드** (계약이 아님 — 정본 계약은 아래 사양서) |
| 정본 계약 | `VDA5050_INTERFACE_SPEC.md` **§8.7**(개정 2026-10-08) — 본 문서는 그 위의 구현 안내 |
| 대상 | HD_AMR 통합 운영 S/W 개발팀 |
| 작성일 | 2026-10-08 |
| 관련 절 | 계약: 사양서 §8(카탈로그 표)·**§8.7**·§8.1(position 공용) · 협의: §10 **N19** · reach 동기: §4.4·N10 |
| 협의 상태 | `[N19]` — ACS 발행 경로·시뮬레이터 **구현 완료**, **HD_AMR 핸들러 미구현(본 문서 범위)** |

> 이 문서는 사양서를 **재서술하지 않는다.** 계약 정본은 `VDA5050_INTERFACE_SPEC.md` §8.7이며,
> 여기서는 HD_AMR이 `moveToSeamStart` 핸들러를 착수할 수 있도록 **무엇을 어디에 구현하는지**를 정리한다.
> 필드·형식의 최종 판정은 항상 사양서가 우선한다.

---

## 1. 목적·범위

본검사(`startWeldInspection`: 정렬 → 자세 시퀀스 → 촬영/측정) 전에, **코봇툴이 용접선 시작점에 실제로 도달하는지(reach)만**
가볍게 확인하는 시험 액션이다. 운영자가 특정 용접선을 지정하면 ACS가 그 정차점·시작점 좌표로 Order를 발행하고,
AMR은 **정차 지점으로 주행한 뒤 코봇툴을 seam 시작점까지만 이동시키고 멈춘다(촬영 없음).**

- **범위 안(본 문서):** HD_AMR이 `moveToSeamStart` 액션을 수신·해석하고, 주행 후 **코봇툴을 `position.seamStartW`로 이동**,
  성공/실패를 `actionState`로 보고하도록 구현.
- **범위 밖:** 촬영·측정·`CAPTURE_REQ`·자세 시퀀스·`taskId`/`attempt` 관통(이 액션은 검사가 아니다). 비전 S/W 무관.

> **왜 필요한가 (N10).** 벽에서 standoff(정차 이격)가 부족하면 코봇이 접힌 자세로 seam에 닿아 손목 특이점(|J5|)·특이자세(`rc`)로
> 실패한다(사양서 §4.4·N10). 이 시험의 **FAILED 결과(사유 포함)가 곧 적정 standoff 산정의 실측 데이터**가 된다.

---

## 2. 현행 상태

| 측 | 상태 |
|---|---|
| **HD_ACS** | ✅ 구현 완료 — 액션 카탈로그 등재(`ref.action_catalog`), 발행 엔드포인트, 시뮬레이터 처리. 2026-10-08 |
| **시뮬레이터** | ✅ `moveToSeamStart` 수신 → `seamStartW` 로그 + `FINISHED` 보고 (ACS 단독 E2E용) |
| **HD_AMR** | ❌ **미구현** — 본 문서 범위. 핸들러 없으면 실코봇은 움직이지 않는다 |

---

## 3. 입력 계약 요약 (HD_AMR이 수신하는 것)

### 3.1 전송 경로
- 토픽: `uagv/v2/{manufacturer}/{serialNumber}/order` (일반 order와 동일 — **instantActions 아님**), QoS 1.
- Order 1건 = **노드 1개(sequenceId=0) + 액션 1건**. `edges` 빈 배열. `orderUpdateId` 0.
- 노드 `nodePosition` = **검사 정차점(standoff가 반영된 벽면 이격 위치, 맵 좌표)**. AMR은 여기로 주행한다(일반 검사 주행과 동일).

### 3.2 액션 파라미터 (`actionParameters` — key/value **2쌍**)

| key | 타입 | 의미 |
|---|---|---|
| `jobRef` | string | 작업 역추적 키 (`TEST-SEAM-{taskId}` 형식). AMR은 로깅 외 해석 불요 |
| `position` | object | 좌표 — `startWeldInspection`의 `position`과 **동일 구조**(§8.1) |

`position` 내부:

| 필드 | 타입 | 의미 |
|---|---|---|
| `seamStartW` | number[3] | **용접선 시작점의 맵(월드) 좌표 `[x,y,z]` m** — ACS가 도면 좌표에 유효 `T_W_D`(도면→맵 강체변환) 적용. **코봇툴이 이동할 목표점.** |
| `seamEndW` | number[3] | 용접선 끝점 맵 좌표 — 방향 참고용(이 시험은 시작점까지만 이동). |
| `drawingPos` | object | 도면 좌표 echo — `tank`·`level`·`wall_code`·`u`·`v`·`x`·`y`·`z`. **`wall_code`가 티칭 자세 선택 키**(10면: `B`·`SL`·`PL`·`SM`·`PM`·`SU`·`PU`·`T`·`F`·`A`). |

> **`params` 객체는 없다.** `seamType`·`inspectionProfileId`·`standoffMm`·`taskId`·`attempt`·`anchorGroupId` 등 **검사용 파라미터를 보내지 않는다** —
> 이 액션은 검사가 아니라 reach 이동 시험이기 때문이다. `wallNormalW`도 보내지 않는다(툴 자세는 `wall_code` 티칭으로 결정, §8.1과 동일).

### 3.3 param_schema (ACS가 발행 직전 자체 검증 — 참고)

```json
{
  "type": "object",
  "required": ["jobRef", "position"],
  "properties": {
    "jobRef": { "type": "string" },
    "position": {
      "type": "object",
      "required": ["seamStartW", "seamEndW", "drawingPos"],
      "properties": {
        "seamStartW": { "type": "array", "items": { "type": "number" }, "minItems": 3, "maxItems": 3 },
        "seamEndW":   { "type": "array", "items": { "type": "number" }, "minItems": 3, "maxItems": 3 },
        "drawingPos": {
          "type": "object",
          "required": ["tank", "level", "wall_code", "u", "v", "x", "y", "z"]
        }
      }
    }
  }
}
```

---

## 4. 구현 규칙 — HD_AMR이 해야 할 것

| # | 단계 | 요건 |
|---|---|---|
| 1 | 액션 파싱 | `actionType == "moveToSeamStart"` 분기. `jobRef`·`position.seamStartW`·`position.drawingPos.wall_code` 추출. 형식 오류 시 액션 `FAILED` + `errors`(`orderValidationError`). |
| 2 | 주행 | 노드 `nodePosition`으로 주행 — **일반 검사 주행과 동일**(TARS-M `/robot/go`, `stopFlag:true`). |
| 3 | 코봇 이동 | 도달 후 **코봇툴을 `position.seamStartW`(맵 좌표)로 이동**. 툴 자세는 `wall_code` 티칭. **`startWeldInspection`의 접근 단계(①~④ 수준)만 재사용** — 그 이상 진행하지 않는다. |
| 4 | **정지** | seam 시작점 도달 후 **정지**. **촬영·측정·`CAPTURE_REQ`·스캔 패턴·자세 시퀀스 없음.** 코봇 홈 복귀 여부는 온보드 판단(안전 우선). |
| 5 | 성공 보고 | 도달 성공 시 `state.actionStates[]`의 해당 `actionId`에 `actionStatus: "FINISHED"`, `resultDescription`(예: `"reached"`). |
| 6 | 실패 보고 | reach 불가(관절 한계·손목 특이점·특이자세·충돌 위험)면 `actionStatus: "FAILED"` + `errors[]`(유형은 §6.4 — 장비/실행 불가 계열) + **`resultDescription`에 사유**(가능하면 `필요 플랜지 뻗음 ≥ N mm, 현재 M mm` / `|J5|=…` / `rc=…`). 이 사유가 N10 standoff 회신 데이터. |

**핵심 차이 (startWeldInspection 대비):**
- 같은 `position`(seamStartW·wall_code)을 쓰되 **접근까지만, 촬영 없음**.
- `params`(seamType/profile/taskId/attempt/anchorGroup) **없음** — 앵커 공유·attempt 관통 로직 **불필요**.
- 완료 판정은 **도달 성공/실패**뿐(검사 결과 아님).

> 구현 재사용 힌트: 본검사 시퀀스에서 "정렬 → seam 접근" 까지의 스텝을 분리해 재사용하고, 그 뒤 촬영 스텝을 건너뛰고 종결하면 된다.

---

## 5. 골든 메시지 (ACS → AMR, order 전문)

```json
{
  "headerId": 51, "timestamp": "2026-10-08T05:20:11.004Z", "version": "2.0.0",
  "manufacturer": "HHI", "serialNumber": "AMR-01",
  "orderId": "a1b2c3d4-...", "orderUpdateId": 0,
  "nodes": [
    {
      "nodeId": "TEST-a1b2c3d4", "sequenceId": 0, "released": true,
      "nodePosition": { "x": 12.482, "y": 5.117, "theta": 1.571,
                        "allowedDeviationXY": 0.08, "allowedDeviationTheta": 0.07, "mapId": "CT1-L2" },
      "actions": [
        {
          "actionType": "moveToSeamStart",
          "actionId": "8f3c19aa-0000-4000-8000-0000000000e2",
          "blockingType": "HARD",
          "actionParameters": [
            { "key": "jobRef", "value": "TEST-SEAM-3a9f2c14-8e51-4d7a-b2c9-1f6e0a5d3b47" },
            { "key": "position", "value": {
                "seamStartW": [12.510, 5.980, 1.420],
                "seamEndW":   [13.310, 5.980, 1.420],
                "drawingPos": { "tank": "CT1", "level": 2, "wall_code": "SM",
                                "u": 3.120, "v": 1.420, "x": 3.120, "y": 0.0, "z": 1.420 } } }
          ]
        }
      ]
    }
  ],
  "edges": []
}
```

성공 응답(state, 발췌):
```json
"actionStates": [
  { "actionId": "8f3c19aa-0000-4000-8000-0000000000e2", "actionType": "moveToSeamStart",
    "actionStatus": "FINISHED", "resultDescription": "reached" }
]
```

실패 응답(발췌):
```json
"actionStates": [
  { "actionId": "8f3c19aa-...", "actionType": "moveToSeamStart",
    "actionStatus": "FAILED", "resultDescription": "정차 거리 부족 — 필요 플랜지 뻗음 ≥ 350 mm, 현재 230 mm (|J5|=2.1°)" }
],
"errors": [ { "errorType": "inspectionFailed", "errorLevel": "WARNING",
              "errorDescription": "8f3c19aa-...: reach 불가(특이자세)" } ]
```

> `errorType`은 §6.4 확정 7종 중 장비/실행 불가 계열을 사용한다(현행 `inspectionFailed` 권장 — 검사 액션 실패와 동계열).
> 별도 reach-fail 유형이 필요하면 N19에서 협의한다.

---

## 6. ACS 측 동작 (참고 — HD_AMR 구현 요건 아님)

- 발행 트리거: 운영 UI(계획 ▸ 작업 그리드) "코봇 이동 시험(시작점)" 또는 `POST /api/robots/{robotId}/test/seam-start { taskId }`.
- ACS는 지정 용접선(area-task)에서 **정차점(standoff) → nodePosition**, **seam 시작점 → `seamStartW`**, **면 → `wall_code`**를 산출한다.
- 가드: 진행 중 run 없음·로봇이 해당 층에 있어야 함(아니면 거부). 감사로그 `TEST_SEAM_START`.
- 소스: `InspectionDispatcher.MoveToSeamStartAsync`·`BuildSeamStartTestOrder` (HD_ACS). `seamStartW`는 `startWeldInspection`과 동일 빌더(`WeldInspectionPayload.BuildPosition`)로 생성 — **두 액션의 시작점 좌표는 동일하다.**

---

## 7. N19 협의 필요 항목 (HD_AMR 회신 요망)

1. **actionType 명칭** `moveToSeamStart` 확정 여부(변경 희망 시 회신).
2. **실패 `errorType`**: reach 불가를 `inspectionFailed`로 통일할지, 신규 유형(예: `reachUnreachable`)을 둘지.
3. **`resultDescription` 포맷**: N10 회신에 쓰도록 **플랜지 뻗음·|J5|·`rc`** 등 수치를 어떤 형식으로 실을지(자유문자열 vs 구조화).
4. **코봇 홈 복귀** 정책: 시험 종료 후 자동 복귀 여부.
5. 번호: ACS 사본은 **N19** 사용(AMR 보유본 out-of-tree N17 seam z·N18 거리 파라미터와 충돌 회피) — 양측 사양서 통합 시 정리.

---

## 8. 수용 기준 (연동 완료 판정)

- [ ] AMR이 `moveToSeamStart` 수신 → 노드 주행 → 코봇툴을 `seamStartW`로 이동(촬영 없음) → 정지.
- [ ] 도달 성공 시 `actionStatus: FINISHED` 보고, 실패 시 `FAILED` + 사유(가능하면 reach 수치).
- [ ] `wall_code` 티칭 자세로 툴 자세 결정(§8.1과 동일 규약).
- [ ] 촬영·`CAPTURE_REQ`·`taskId`/`attempt`가 발생하지 않음(검사 아님).
- [ ] 실기에서 standoff 부족 케이스가 `FAILED`로 드러나 N10 회신 데이터 산출.
