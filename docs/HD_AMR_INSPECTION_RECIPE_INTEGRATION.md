# HD_AMR 검사 레시피 연동 가이드 (startWeldInspection 2차 연동)

| 항목 | 내용 |
|---|---|
| 문서 성격 | **HD_AMR 구현 착수 가이드** (계약이 아님 — 정본 계약은 아래 사양서) |
| 정본 계약 | `VDA5050_INTERFACE_SPEC.md` (개정 1.3a 이상) — 본 문서는 그 위의 구현 안내 |
| 대상 | HD_AMR 통합 운영 S/W 개발팀 |
| 작성일 | 2026-09-14 |
| 관련 절 | 계약: 사양서 §8.1·§8.2·§8.4 / 규격: §8.5·§8.5.1 / Surface: 부록 D.3 / 카탈로그: `INSPECTION_TYPES.md` |
| 협의 상태 | `[N13]` — enum(`LINE`/`CROSS3`/`CROSS4`/`CORNER2`/`CORNER3`, 5값·카탈로그 1:1)은 **ACS 선반영·발행 중**, HD_AMR 레시피 매핑·실행은 **미구현(본 문서 범위)** |

> 이 문서는 사양서를 **재서술하지 않는다.** 계약의 정본은 `VDA5050_INTERFACE_SPEC.md`이며, 여기서는 HD_AMR이 2차 연동을 착수할 수 있도록 **무엇을 어디에 구현하는지**를 체크리스트로 정리한다. 표·필드의 최종 판정은 항상 사양서가 우선한다.

---

## 1. 목적·범위

HD_ACS는 검사 계획 단계에서 도면으로부터 **용접라인 형태(`seamType`)**를 추출해 VDA 5050 `startWeldInspection` 액션으로 전달한다(현재 `LINE`·`CROSS3`·`CROSS4`·`CORNER2`·`CORNER3` 5값 발행). HD_AMR은 이 액션을 받아 **형태·면 자세에 맞는 검사 레시피를 선택·실행**해야 한다.

- **범위 안(본 문서):** HD_AMR이 `startWeldInspection`의 `actionParameters`를 해석하고, `(seamType, wall_code)` → 레시피를 유도해 실제 검사 시퀀스를 실행하도록 연동.
- **범위 밖:** ACS의 계획·발행(이미 구현), 로봇 주행/정차(기존 VDA 계약), 촬영/측정 하드웨어 제어(HD_AMR 기존 자산).

## 2. 현행 상태 (연동 전 = 스텁)

HD_AMR의 `startWeldInspection`은 현재 **스텁**이다.

- `Service/Vda5050OrderExecutor.cs`: 노드 도달 후 액션을 `RUNNING → FINISHED "stub"`로만 보고하고 **`actionParameters`를 읽지 않는다** → ACS가 보낸 `seamType`·`wall_code`·`inspectionProfileId` 등 **전부 무시**.
- 실제 검사는 HD_AMR **자체 로컬 `InspectionProfile`**(정수 id, 온보드 UI 티칭, SQLite)로 별개 수행되며, VDA order와 연결돼 있지 않다.

**따라서 연동 = 이 둘을 잇는 작업**이다: VDA 액션 파라미터를 파싱 → `(seamType, wall_code)`로 로컬 레시피를 선택 → 실행 → 결과를 actionStatus로 보고.

## 3. 입력 계약 요약 (HD_AMR이 수신하는 것)

`startWeldInspection` 액션 파라미터는 key/value 3쌍이다. **상세·타입은 사양서 §8.1(필드 표)·§8.2(param_schema)·§8.4(골든 예시) 정본.** 요약:

| key | 하위 필드 | 연동에서의 쓰임 |
|---|---|---|
| `jobRef` | (문자열) | 로깅·역추적. 해석 불요 |
| `position` | `seamStartW`·`seamEndW` [x,y,z] m (맵 좌표) | **툴 회전(수직/수평) 자동 유도** = `seamStartW→seamEndW` 벡터 (§4.4·§8.1) |
| `position` | `drawingPos.wall_code` | **면 자세 판정 키** (10면 코드) → 레시피 선택 |
| `params` | **`seamType`** ∈ `LINE`·`CROSS3`·`CROSS4`·`CORNER2`·`CORNER3` (5값, 카탈로그 1:1) | **형상 판정 키** → 레시피 선택 |
| `params` | `sectionDxfId` | 단면 프로파일 참조(선택) |
| `params` | `inspectionProfileId` | 촬영/측정 프리셋 ID (자유 문자열, **형태와 다른 축** — §8.5 개념 구분) |
| `params` | `standoffMm`·`workingDistanceMm` | 이격/작업거리 |
| `params` | `anchorGroupId`·`seqInGroup` | 정렬 공유 그룹·순번 (§8.1) |

골든 예시(§8.4 발췌 — 정본은 사양서):

```json
{ "key": "position", "value": {
    "seamStartW": [12.510, 5.980, 1.420],
    "seamEndW":   [13.310, 5.980, 1.420],
    "drawingPos": { "tank": "CT1", "level": 2, "wall_code": "SM", "u": 3.12, "v": 1.42, "x": 3.12, "y": 0.0, "z": 1.42 } } },
{ "key": "params", "value": {
    "seamType": "LINE", "sectionDxfId": "DXF-CORR-T12", "inspectionProfileId": "INSPECT-STD-01",
    "standoffMm": 400, "workingDistanceMm": 400, "anchorGroupId": "CT1-L2-SM-ST04", "seqInGroup": 2 } }
```

## 4. 구현 규칙 — `(seamType, wall_code)` → 레시피

**면 자세 판정** (`wall_code` → 5군, `TankGeometry` 정본):

| `wall_code` | 면 자세 |
|---|---|
| `B` | 바닥 |
| `T` | 천장 |
| `SM` `PM` `F` `A` | 수직 평면벽 |
| `SL` `PL` | 하부 챔퍼 |
| `SU` `PU` | 상부 챔퍼 |

**레시피 매핑** (사양서 §8.5.1 (3), 레시피 id는 `INSPECTION_TYPES.md` §5와 동일):

| `seamType` \ 면자세 | 바닥 | 천장 | 수직벽 | 하부챔퍼 | 상부챔퍼 |
|---|---|---|---|---|---|
| `LINE` | `LINE-FLOOR` | `LINE-CEIL` | `LINE-WALL` | `LINE-CHMR-LO` | `LINE-CHMR-UP` |
| `CROSS3` | `CROSS3-FLOOR` | `CROSS3-CEIL` | `CROSS3-WALL` | `CROSS3-CHMR-LO` | `CROSS3-CHMR-UP` |
| `CROSS4` | `CROSS4-FLOOR` | `CROSS4-CEIL` | `CROSS4-WALL` | `CROSS4-CHMR-LO` | `CROSS4-CHMR-UP` |

- `CORNER2`(2면 코너)·`CORNER3`(3면 코너): 코너 형상이라 면 자세 무관 — 각각 단일 레시피 `CORNER2`·`CORNER3`. 3면 코너는 각도가 전부 (135°·90°·90°)로 균일(거울 필요 시 `wall_code`로 `CORNER3-L`/`CORNER3-R` 판별, 선택). 근거: `INSPECTION_TYPES.md` §7 마구리 도면 실측. `CORNER2`의 이면각별 세분은 실측 확인 대상(§7).
- = 면 위 형상 3×5 + 코너 2 = **17종**(코너 거울 분리 시 확대).
- **툴 수직/수평 회전은 매핑표가 결정하지 않는다.** `seamStartW→seamEndW` 벡터에서 **자동 유도**(§4.4·§8.1). 매핑표는 **스캔 패턴·면 접근**만 결정.
- **미지원/모순 조합**(예: `CORNER2`/`CORNER3`+평면 `wall_code`, 미정의 `seamType`): 처리 방식은 §8.5.1 (4)·아래 §7(N13). 기본안 = 액션 FAILED + `orderValidationError`(계약 위반) 또는 `inspectionFailed`(실행 불가).

## 5. `seamType` ≠ `Surface` (혼동 금지)

둘은 다른 계층이다 (사양서 §8.5.1 (6)):

| 구분 | `seamType` | `Surface` |
|---|---|---|
| 의미 | 용접라인 **형태**(LINE/CROSS3/CROSS4/CORNER2/CORNER3) | 경유점 **표면 형상**(Flat/Corner/Corrugation) |
| 단위 | 용접라인 1개 | 레시피 내 **경유점 1개** |
| 주체 | **HD_ACS**(도면 추출) → 액션 전송 | **HD_AMR**(레시피 내부) |
| 용도 | 어떤 **레시피를 로딩**할지 | 경유점별 **촬영/조명** 선택 |
| 전송 | VDA `params.seamType` | **전송 안 함**(AMR 내부값) |

**2단계로 동작한다:** `seamType`(라인) → **레시피 로딩** / 각 경유점의 `Surface` → **촬영/조명 선택**. `Surface` 유도 규칙(현행 온보드 구현)은 사양서 **부록 D.3**: 경유점 툴 각 `|θ| ≥ CorrugThresholdDeg` → `Corrugation`, 아니면 `Flat`, `Corner`는 수동 오버라이드. ACS는 `Surface`를 알 필요도 보낼 필요도 없다.

## 6. HD_AMR 코드 접점 (구현 관찰, 2026-09-14)

> 아래는 `gony0811/HD_AMR` 저장소 **읽기 관찰**이며 확정 계약이 아니다. 파일명·구조는 관찰 시점 기준이고, 실제 구현 위치는 HD_AMR팀이 정한다.

| 구현 항목 | 접점(관찰) | 해야 할 일 |
|---|---|---|
| 액션 파라미터 파싱 | `Service/Vda5050OrderExecutor.cs` (현재 `actionParameters` 미해석·`FINISHED "stub"`) | `startWeldInspection`에서 `params.seamType`·`drawingPos.wall_code`·`seamStartW/EndW` 추출 |
| 레시피 라이브러리 | `Data/Entities/InspectionProfile.cs` + 온보드 UI(현재 수직/수평 단일 토글) | 11종 레시피(접근 자세·스캔 패턴·촬영/측정) 정의, `(seamType,wall_code)`→레시피 id 매핑 테이블 |
| Surface 유도(기존) | `Service/Sequence/Steps/InspectionRunStep.cs`, `Communication/Vision/VisionProtocol.cs`(`SurfaceType` enum) | 신규 구현 불요 — 기존 규칙 유지(부록 D.3). 레시피 실행 시 경유점별로 그대로 사용 |

## 7. N13 결정 필요 항목 (HD_AMR 회신 요망)

| # | 항목 | 선택지 | ACS 현재 |
|---|---|---|---|
| 1 | 형상 전달 방식 | `seamType` 명시 수신 / AMR이 도면 유도 | **명시 전송**(seamType 발행 중) |
| 2 | `CROSS3`/`CROSS4`(3·4갈래 교차) vs 직선 | 별도 레시피 / `LINE`에 통합 | 별도(`CROSS3`·`CROSS4`) 발행 가능 |
| 3 | `CORNER2`/`CORNER3` 세분·거울 L/R | 단일 / 이면각·거울 분리 | 2면·3면 분리 발행, 거울은 `wall_code`로 판별 가능 |
| 4 | `inspectionProfileId` | 촬영/측정 프리셋 유지 / 형태와 통합 | **유지**(형태와 다른 축, 자유 문자열) |
| 5 | 미지원 조합 errorType | `orderValidationError` / `inspectionFailed` | 미확정 — §8.5.1 (4) |

확정 시 사양서 §8.1/§8.2에 반영하고 본 가이드를 갱신한다.

## 8. 수용 기준 (연동 완료 판정)

- [ ] `startWeldInspection` 수신 시 `params.seamType`·`drawingPos.wall_code`를 파싱하고 **결정 로그**(수신값 → 선택 레시피 id)를 남긴다.
- [ ] `LINE`·`CROSS3`·`CROSS4` × 면 자세 + `CORNER2`·`CORNER3` 각 조합이 매핑표대로 레시피를 선택한다(§4).
- [ ] 툴 수직/수평이 `seamStartW→seamEndW` 벡터로 유도된다(매핑표에 의존하지 않음).
- [ ] **기존 `LINE` 검사 회귀 없음** — 스텁 제거 후에도 현행 LINE 검사 동작 동일.
- [ ] 미지원 조합에서 조용한 오검사 없이 명시적 FAILED + errorType 보고.
- [ ] 시뮬레이터/실장 E2E로 `CROSS3`/`CROSS4`/`CORNER2`/`CORNER3` 레시피 선택까지 확인(실제 촬영은 레시피 티칭 완료 후).

---

관련 문서: `VDA5050_INTERFACE_SPEC.md` · `INSPECTION_TYPES.md`
