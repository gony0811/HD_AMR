# 검사 실행 타입 카탈로그 (Inspection Types)

> HD_ACS가 HD_AMR에 `startWeldInspection` 액션을 보낼 때 구분해야 하는 **검사 타입**을
> 면(Wall)·용접 형상 기준으로 정리한 표준안. 대상 화물창은 **KC-2B 멤브레인** 시스템.
> 상태: **제안(표준안)** — `profileId` enum은 미구현. 용접선 정의·종/횡 포함 여부는 §9 결정 대기.

---

## 1. 책임 경계 (먼저 확인) ⚠️

| 구분 | 책임 | 내용 |
|------|------|------|
| **HD_ACS** | *무엇을 / 어디를* | 검사 대상 용접선 기하(위치·시작/끝), 검사 조건(profileId), 정차점·standoff 산출 |
| **HD_AMR** | *어떻게* | 툴 회전각(수직/수평)·접근 자세·검사 시퀀스 — **ACS가 명령하지 않음** |

- **툴 수직/수평 회전은 ACS 명령 축이 아니다.** HD_AMR이 seam 벡터(start→end)와 노드 theta(벽 정면)에서 **자동 유도**한다 (VDA5050 사양서 §4.4, ADR-004, 단일 상대 원칙).
- 따라서 검사 타입은 **별도 액션 동사가 아니라 `startWeldInspection` 하나 + 파라미터(주로 `profileId`) 조합**으로 표현한다.

---

## 2. 대상 면(Wall ID)과 면 자세 분류

화물창 단면은 팔각 프리즘 + 마구리 2면 = **10면** (`TankGeometry.GenerateWalls`).
정차점·standoff·접근 방향은 **면 자세**로 결정된다.

| 면 자세군 | Wall ID | 자세 | ACS 정차/접근 특성 |
|-----------|---------|------|--------------------|
| **바닥** | `B` | 수평(위 향함) | 바닥 위 정차, 위→아래 촬영 |
| **천장** | `T` | 수평(아래 향함) | 아래→위 촬영 |
| **수직 평면벽** | `SM` `PM` `F` `A` | 수직 | 벽 정면 이격 정차 |
| **하부 챔퍼** | `SL` `PL` | 경사(45°) | 경사 정면 이격 정차 |
| **상부 챔퍼** | `SU` `PU` | 경사(45°) | 경사 정면 이격 정차 |

→ **면 자세 = 5군.** standoff·정차각은 이미 `ref.wall` 법선에서 자동 산출된다(별도 명령 불필요).

---

## 3. 검사 형상 유형 — 독립 5종

멤브레인 용접선은 코로게이션 격자를 따라 형성된다. 도면 규약: **360mm 주기 점선 = 코로게이션 top 마커(검사 대상 아님)**, **실선 = 용접선**.

형상은 **두 성격**으로 나뉘지만, 카탈로그상 **서로 독립인 5종**으로 취급한다(조합 매트릭스 아님):

**(A) 면 위 용접선 교차(junction)** — 한 면 위에서 용접선이 몇 갈래로 만나는가

| 형상 | 갈래 | 설명 | 위치 |
|------|:---:|------|------|
| **LINE (직선)** | 1 | 면 위 직선 용접선 구간 | 면 내부 |
| **CROSS3 (T자)** | **3** | 용접선이 T자로 만나는 3갈래 교차 | 면 내부 격자 교차 |
| **CROSS4 (십자 十)** | **4** | 용접선 2개가 교차하는 4갈래 십자 | 면 내부 격자 교차 |

**(B) 판재 접합 코너(plate corner)** — 벽면이 몇 개 만나 접히는가. **접힌 면에 브릿지 플레이트를 덧대므로** 실형상을 수식으로 만들 수 없어 **전용 조그+캡처 교시 필수**(§7)

| 형상 | 면 수 | 설명 | 위치 |
|------|:---:|------|------|
| **CORNER2 (2면 코너)** | **2** | 두 벽면이 만나는 모서리(edge) 접합부 + 브릿지 플레이트 | 이면 코너 |
| **CORNER3 (3면 코너)** | **3** | 세 벽면이 만나는 꼭짓점(vertex) 접합부 + 브릿지 플레이트 | 삼면 코너 |

→ **형상 = 5종** (LINE·CROSS3·CROSS4·CORNER2·CORNER3). 접미사 숫자 = **팔 갈래 수(CROSS3/CROSS4)** 또는 **접합 면 수(CORNER2/CORNER3)**.

> **교시/실행 방식 요약** — 상세 §5:
> - **LINE**: 도면(X-Z 단면) 솎기 (현행).
> - **CROSS3·CROSS4**: **캡처 교시로 단일화** — 코로게이션 격자 교차부는 평탄하지 않아 수식 생성으로는 법선·깊이를 담지 못하므로, X-Y 6-DOF 조그+캡처 교시(`/inspection-points`)로 통일한다. (패턴 수식 생성은 폐기 — 교시 시작 템플릿 용도로만 잔존)
> - **CORNER2·CORNER3**: **캡처 교시 전용**(브릿지 플레이트 실측). 수식 생성 불가.
>
> → **결론: LINE(도면 솎기)을 제외한 모든 형상은 6-DOF 캡처 교시**로 경유점을 확보한다.

---

## 4. 검사 타입 매트릭스 (유효 조합)

**면 위 junction(LINE·CROSS3·CROSS4)** 은 면 자세 5군과 결합한다. **코너(CORNER2·CORNER3)** 는 면 자세 무관(코너 자체가 면 배치를 규정) — 캡처 교시로 위치별 해결.

| 형상 \ 면자세 | 바닥 B | 천장 T | 수직벽 SM/PM/F/A | 하부챔퍼 SL/PL | 상부챔퍼 SU/PU | 코너 |
|---|:--:|:--:|:--:|:--:|:--:|:--:|
| LINE (직선) | ✓ | ✓ | ✓ | ✓ | ✓ | — |
| CROSS3 (T자 3갈래) | ✓ | ✓ | ✓ | ✓ | ✓ | — |
| CROSS4 (십자 4갈래) | ✓ | ✓ | ✓ | ✓ | ✓ | — |
| CORNER2 (2면 코너) | — | — | — | — | — | ✓ (1종±거울) |
| CORNER3 (3면 코너) | — | — | — | — | — | ✓ (1종±거울) |

**= 5(LINE) + 5(CROSS3) + 5(CROSS4) + 1(CORNER2) + 1(CORNER3) = 17종** (코너 거울 L/R 분리 시 최대 19).

> **코너가 각 1종인 근거(각도)**: 마구리(A·F) 도면 실측 결과 팔각 단면 8개 꼭짓점 내각이 **전부 135°**(챔퍼 전부 45° 등각). 3면 코너의 각도 구성은 모두 **(135°·90°·90°)로 동일** → 각도상 1종(+거울). 단, 브릿지 플레이트 실형상은 위치별로 다를 수 있어 **레시피는 1종(타입)이되 경유점은 위치별 캡처 프로필**로 관리한다(§7).

---

## 5. profileId 카탈로그 (표준안)

액션은 `startWeldInspection` 하나 유지, `profileId` enum으로 검사 타입을 구분한다.

> **명칭 정합**: 본 문서의 `profileId`는 VDA 계약의 `params.inspectionProfileId`([VDA5050_INTERFACE_SPEC](VDA5050_INTERFACE_SPEC.md) §8.5) 계열이다. 단, VDA `inspectionProfileId`는 원래 "촬영/측정 프로파일" 의미라 축이 다르므로, 실제 계약 반영 방식(재정의 / 신규 `inspectionType` 필드 / 문서 참조)은 **N13 협의 확정 대기** — 현재는 제안이다.

| profileId | 형상 | 대상 Wall ID | 교시/실행 | 비고 |
|-----------|------|--------------|-----------|------|
| `LINE-FLOOR` | LINE | `B` | 도면 솎기 | 바닥 |
| `LINE-CEIL` | LINE | `T` | 도면 솎기 | 천장 |
| `LINE-WALL` | LINE | `SM` `PM` `F` `A` | 도면 솎기 | 수직 평면벽 |
| `LINE-CHMR-LO` | LINE | `SL` `PL` | 도면 솎기 | 하부 챔퍼 |
| `LINE-CHMR-UP` | LINE | `SU` `PU` | 도면 솎기 | 상부 챔퍼 |
| `CROSS3-FLOOR` | CROSS3 (3갈래) | `B` | 캡처 교시 | **신규** |
| `CROSS3-CEIL` | CROSS3 (3갈래) | `T` | 캡처 교시 | **신규** |
| `CROSS3-WALL` | CROSS3 (3갈래) | `SM` `PM` `F` `A` | 캡처 교시 | **신규** |
| `CROSS3-CHMR-LO` | CROSS3 (3갈래) | `SL` `PL` | 캡처 교시 | **신규** |
| `CROSS3-CHMR-UP` | CROSS3 (3갈래) | `SU` `PU` | 캡처 교시 | **신규** |
| `CROSS4-FLOOR` | CROSS4 (4갈래) | `B` | 캡처 교시 | |
| `CROSS4-CEIL` | CROSS4 (4갈래) | `T` | 캡처 교시 | |
| `CROSS4-WALL` | CROSS4 (4갈래) | `SM` `PM` `F` `A` | 캡처 교시 | |
| `CROSS4-CHMR-LO` | CROSS4 (4갈래) | `SL` `PL` | 캡처 교시 | |
| `CROSS4-CHMR-UP` | CROSS4 (4갈래) | `SU` `PU` | 캡처 교시 | |
| `CORNER2` | CORNER2 (2면) | 코너(옆면 2) | **캡처 교시 전용** | 브릿지 플레이트 · 거울 시 `CORNER2-L`/`CORNER2-R` · **신규** |
| `CORNER3` | CORNER3 (3면) | 코너(마구리+옆면 2) | **캡처 교시 전용** | 브릿지 플레이트 · 거울 시 `CORNER3-L`/`CORNER3-R` |

→ **17개 profileId** (코너 거울 분리 시 최대 19).

> **교시/실행 열**: *도면 솎기* = 면 DXF 단면(X-Z) 솎기(LINE 현행). *캡처 교시* = X-Y 6-DOF 조그+캡처
> 교시(`/inspection-points`) — CROSS3·CROSS4·CORNER2·CORNER3 공통(패턴 수식 생성은 폐기). CORNER3 온보드
> 현행은 고정 슬롯(`corner3.*`, §9) — 추후 재정의 대상.

---

## 6. 액션 파라미터 매핑 (`startWeldInspection`)

| 파라미터 | 출처 | 검사 타입과의 관계 |
|----------|------|--------------------|
| `profileId` | §5 카탈로그 | **검사 타입 선택자** |
| `seamType` | 현재 `LINE` 고정 | 꺾인 선은 세그먼트 분할 |
| `seamStartW` / `seamEndW` | 도면(u,v)→T_W_D 변환 | 용접선 기하 = **툴 방향(수직/수평)을 AMR이 유도하는 근거** |
| `drawingPos(u,v)` | 도면 좌표 echo | 위치 참조 |
| `sectionDxfId` | 면별 DXF(§8) | 상세 형상 참조 |
| `standoff` / 정차각 | `ref.wall` 법선 자동 산출 | 면 자세별 접근 |
| `anchorGroupId` | `{tank}-L{n}-{wall}-{영역}` | 앵커 공유 판정 |
| `taskId` / `attempt` | ACS 발급 (VDA5050 §8.1.1, N14) | 검사 타입과 무관 — **비전 CAPTURE_REQ 로 중계되는 작업 식별자**(taskId = SAIGE `productId`, attempt = 재검사 시도 번호) |

**핵심**: 툴 수직/수평은 파라미터로 넘기지 않는다 — `seamStartW→EndW` 방향으로 AMR이 결정.

---

## 7. 코너부(2면·3면)와 브릿지 플레이트

**코너부는 접힌 면에 브릿지 플레이트를 덧대 접합한다.** 그래서 코너 용접선의 실형상은 base 면 기하나 패턴 수식으로 **유도할 수 없고**, 브릿지 플레이트를 포함한 실제 자세를 **조그+캡처로 6-DOF 교시**해야 한다(수식 생성 금지).

- **CORNER2 (2면 코너)**: 두 벽면이 모서리(edge)를 따라 접히고 그 위에 브릿지 플레이트. 접근·촬상 자세는 두 면의 이면각에 종속 → 위치별 캡처.
- **CORNER3 (3면 코너)**: 세 벽면이 꼭짓점(vertex)에서 접히고 브릿지 플레이트. 접근 순서(면1·면2·면3)와 자세 캡처.

**각도 균일성(코너 타입이 각 1종인 근거)**
- 근거 도면: `drawing/2D도면/WALL A.dxf`, `WALL F.dxf` (마구리, 단면=팔각).
- `KC-2B Steel Wall` 외곽 8정점 내각 = **전부 135°** (양쪽 동일), 챔퍼 전부 45° 등각.
- 3면 코너의 각도 구성은 전부 (135°·90°·90°) → **각도상 1종(+거울)**.
- ⚠️ 한계: "각도 동일"은 필요조건일 뿐이며, **브릿지 플레이트 실형상**(치수·이음 위치)은 코너 위치별로 다를 수 있다. 따라서 **레시피 id 는 타입 1종**(CORNER2/CORNER3, +거울)으로 두되, **경유점은 위치(섹션)별 캡처 프로필**로 관리한다. CORNER2 의 각도·거울 규칙 및 대상 코너 목록은 KC-2B 표준 코너 부재 사양으로 확정 필요(§9-6).

---

## 8. 근거 도면·참고

- 면별 도면: `drawing/2D도면/WALL {B,SL,PL,SM,PM,SU,PU,T,F,A}.dxf` — 용접선 정본(KC-2B).
- 주요 레이어: `KC-2B Membrane Sheet(UM)`(용접선 후보 + 코로게이션), `KC-2B Steel Wall`(면 경계).
- 관련 문서: [VDA5050_INTERFACE_SPEC](VDA5050_INTERFACE_SPEC.md) §4.4(정차점)·§8.1(seamType)·**§8.5(타입 카탈로그)·§8.5.1(`seamType`×`wall_code`→레시피 매핑)**, [TANK_WALL_LAYOUT](TANK_WALL_LAYOUT.md), [INSPECTION_SCENARIO](INSPECTION_SCENARIO.md).

---

## 9. 미결·결정 필요 항목

1. **종/횡 방향을 profileId에 넣을지** — 넣으면 종수 2배. 현재 권장: **넣지 않음**(AMR이 seam 기하로 유도).
2. **용접선 확정 정의** — "실선 = 용접선, 360mm 점선 = 코로게이션 top" 규칙을 데이터(레이어/선종) 필터로 확정.
3. **~~4점 십자를 직선과 별도 타입으로 둘지~~** — **확정(2026-09-15): 독립 5종**(LINE·CROSS3·CROSS4·CORNER2·CORNER3). T자(3갈래)를 신규 추가, 코너를 2면/3면으로 분리.
4. **코너 거울(L/R) 분리 여부** — AMR 접근이 좌우 대칭이면 1종 유지, 아니면 2종. CORNER2/CORNER3 각각 해당.
5. **profileId enum 구현** — `ref.area_task.profile_id` / 액션 `param_schema` 반영.
   > HD_AMR 측 진행: 정본 카탈로그(§5) **17종 전체**(`LINE-*` 5 + `CROSS3-*` 5 + `CROSS4-*` 5 + `CORNER2` + `CORNER3`)가
   > 온보드 DB(`InspectionRecipes`, id = §5 문자열 그대로)로 시드되어 `(seamType, wall_code)` → 레시피 매핑·실행
   > 배선 완료(VDA5050_INTERFACE_SPEC §8.5.1 (5)).
   > `LINE-*` 5종만 실행 활성, `CROSS3-*`/`CROSS4-*`/`CORNER2`/`CORNER3`은 실행 게이트 OFF(캡처 교시·실기 검증 후 활성화).
   > **`CORNER2` 는 실행 스텝 미구현(게이트 OFF 로 차단) — corner2 슬롯/캡처 배선은 §9-6 후속.**
   > 계약(`param_schema`) 반영 방식은 여전히 N13 미확정 — ACS는 자유 문자열 `inspectionProfileId` 유지.
   >
   > **HD_AMR 실행 시퀀스 구현(2026-09-15 추가)** — `CROSS4-*`/`CORNER3`의 온보드 실행 방법이 구현됨
   > (게이트는 실기 검증 전이라 여전히 OFF, 온보드 `/recipes` 페이지에서 활성화):
   > - **검사 방향 자동 유도**: seam 벡터(start→end)를 노드 theta 기준 벽면-로컬 투영해 수평/수직 자동 판정
   >   (`SeamDirectionResolver`) — §1 의 "ACS 가 명령하지 않음" 원칙이 ACS 경로에 실제 배선됨. LINE 포함 공통.
   > - **CROSS3·CROSS4** (캡처 교시로 단일화, 2026-09-15): 정렬 1회(wobj 프레임 등록) 후, `/inspection-points`에서
   >   교시한 6-DOF 절대 프로필(`PoseAbsolute`)의 경유점을 그대로 실행(코로게이션 법선·깊이 반영). **패턴 수식 런타임
   >   생성 경로는 제거 완료**(레시피 `PatternJson` 필드·오케스트레이터 생성·`WaypointsOverride` 삭제). `CrossPatternGenerator`
   >   는 `/inspection-points`·`/inspection`의 "십자 패턴 채우기" **교시 시작 템플릿**으로만 잔존.
   > - **CORNER3**: 평탄면 정렬 미적용 — 고정 티칭 슬롯 `corner3.{L|R}.{approach,face1..3,retreat}` 직접 순회
   >   (`cornerInspectionRunStep`), SurfaceType=Corner 촬상. 거울 L/R 은 별도 티칭 2세트,
   >   side 판별 P*→L / S*→R (F/A 코너의 side 규칙은 N13 협의 필요 — 기본 L).

6. **신규 타입(CROSS3·CORNER2) 구현** `[대부분 완료]` — 정본화 + 계약 enum 확장 반영 완료(2026-09-15):
   - **CROSS3(3갈래)** ✅ **완료**: `SeamTypeKind.Cross3` + resolver(`CROSS3-{면자세}`) + `CROSS3-*` 5종 시드(게이트 OFF) + `/inspection-points` 교시 옵션 + 오케스트레이터 배선(캡처 절대 프로필 실행). **파서 수용**(계약 enum 확장, §9-7) — ACS 도달 가능.
   - **CORNER2(2면)** 🔶 **부분**: `SeamTypeKind.Corner2` + resolver(`CORNER2`) + 레시피 시드(게이트 OFF) + 파서 수용 완료. **실행 스텝 미구현** — corner2 슬롯/캡처 배선은 KC-2B 코너 부재 사양(거울 규칙·브릿지 플레이트 자세) 확정 후 후속.
   - **CROSS4 수식 경로 폐기** ✅ **완료(2026-09-15)**: 레시피 `PatternJson` 필드·오케스트레이터 런타임 생성·`WaypointsOverride` 주입·`/recipes` PatternJson 컬럼·DB 스키마 컬럼 제거. `CrossPatternGenerator`는 교시 템플릿 전용으로 잔존. (기존 DB 의 PatternJson 컬럼은 EF 미매핑으로 무해)
   - **CORNER3(3면)**: 현행 고정 슬롯 유지(옵션1) — 추후 재정의.
7. **`seamType` enum 계약 확장** ✅ **반영(2026-09-15)** `[협의 N13]` — 계약 enum = `LINE`/`CROSS3`/`CROSS4`/`CORNER2`/`CORNER3`.
   AMR 파서·resolver·17종 시드 구현 완료(legacy `CROSS`→CROSS4·`CORNER`→CORNER3 수용). **ACS 는 canonical 5값으로 발행 전환 필요** — 값 합의·전환 시점은 N13. (VDA5050_INTERFACE_SPEC §8.1/§8.2/§8.5.1·개정 1.5)

> 결정이 내려지면 본 문서와 `startWeldInspection` `param_schema`, 관련 코드/DB를 함께 갱신한다.
