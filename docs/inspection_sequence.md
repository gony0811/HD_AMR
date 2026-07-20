# 검사 시퀀스 설계 문서

> 최종 수정: 2026-07-05

## 1. 개요

HD_AMR 시스템의 검사 시퀀스는 AMR(자율주행 이동체), 코봇(FAIRINO 협동로봇), 뎁스 카메라(Intel RealSense D435), 3점 레이저 변위센서(OMRON ZP-LS300S)를 순차적으로 제어하여 검사 대상물의 정밀 초점거리를 확보하는 자동화 워크플로우이다.

### 시퀀스 구성

| 순서 | 단계 | 장비 | Key |
|------|------|------|-----|
| ① | AMR 검사위치 이동 | AMR (Modbus TCP) | `amrMove` |
| ② | Cobot 검사 대기위치 이동 | 코봇 (XML-RPC) | `cobotInspection` |
| ③ | 카메라 거리 정렬 (400mm) | 뎁스 카메라 + 코봇 | `cameraAlign` |
| ④ | 평탄면 센터링 (레이저 정렬) | 뎁스 카메라 + 레이저 센서 + 코봇 | `flatSurfaceAlign` |

### 실행 모드

- **풀오토**: `SequenceService.RunAllAsync()` — 모든 단계를 순서대로 자동 실행. 실패 시 즉시 중단.
- **세미오토**: `SequenceService.RunStepAsync(key)` — 특정 단계만 수동 실행.

---

## 2. 아키텍처

### 핵심 타입

```
HD_AMR/Service/Sequence/
├── ISequenceStep.cs          # 인터페이스 + DTO (StepResult, StepValidation, SequenceContext 등)
├── SequenceService.cs        # 실행 엔진 (레지스트리, 풀오토/세미오토, 상태 이벤트)
└── Steps/
    ├── AmrMoveStep.cs            # ① AMR 이동
    ├── CobotInspectionMoveStep.cs # ② 코봇 검사 대기위치
    ├── CameraAlignStep.cs        # ③ 카메라 거리 정렬
    └── FlatSurfaceAlignStep.cs   # ④ 평탄면 센터링
```

### ISequenceStep 인터페이스

```csharp
public interface ISequenceStep
{
    string Key { get; }              // 고유 식별자
    string DisplayName { get; }      // UI 표시명
    int DefaultOrder { get; }        // 실행 순서 (100, 200, 300, ...)
    StepValidation Validate(SequenceContext ctx);
    Task<StepResult> ExecuteAsync(SequenceContext ctx, CancellationToken ct);
}
```

### SequenceContext (단계 간 공유 데이터)

- `Tool` (int): 활성 tool 번호 (기본 1)
- `Velocity` (int): 이동 속도 % (기본 20)
- `Positions` (Dictionary): 티칭된 위치 목록 (시퀀스 시작 시 DB에서 자동 로드)
- `Bag` (Dictionary): 단계 간 임시 데이터 전달용

### DI 등록

모든 Step과 SequenceService는 **Scoped**로 등록된다 (Blazor Server 서킷 단위 수명). 하드웨어 서비스(CobotService, CameraService 등)는 Singleton이므로 Scoped에서 안전하게 주입 가능하다.

```csharp
// Program.cs
builder.Services.AddScoped<ISequenceStep, AmrMoveStep>();
builder.Services.AddScoped<ISequenceStep, CobotInspectionMoveStep>();
builder.Services.AddScoped<ISequenceStep, CameraAlignStep>();
builder.Services.AddScoped<ISequenceStep, FlatSurfaceAlignStep>();
builder.Services.AddScoped<SequenceService>();
```

### 새 단계 추가 방법

1. `ISequenceStep`을 구현하는 클래스를 `Steps/` 폴더에 생성
2. `DefaultOrder` 값으로 순서 지정 (기존: 100, 200, 300, 400)
3. `Program.cs`에 `builder.Services.AddScoped<ISequenceStep, MyNewStep>();` 추가

---

## 3. 단계별 상세

### ① AMR 검사위치 이동 (`AmrMoveStep`)

**목적**: 자율주행 이동체를 검사 대상물 앞의 티칭된 위치로 이동시킨다.

**선행조건**:
- 코봇 RPC 연결
- 홈 위치 티칭 완료 (`TeachingPosition.Key = "home"`)

**동작**:
1. 코봇의 현재 관절각을 홈 위치와 비교 (허용오차 ±0.5°/축)
2. 홈이 아니면 MoveJ로 홈 복귀 (안전한 자세에서 AMR 이동)
3. AMR에 Task/Job 번호로 이동 명령 전송 (현재 미구현 — TODO)

**비고**: AMR 이동 명령은 AMR 메뉴에서 정의한 Task/Job 번호를 사용한다. Modbus TCP 레지스터를 통해 Task 번호 쓰기 → 실행 명령으로 구현 예정.

---

### ② Cobot 검사 대기위치 이동 (`CobotInspectionMoveStep`)

**목적**: 코봇을 검사 대기 위치로 이동시킨다.

**선행조건**:
- 코봇 RPC 연결
- 검사 준비 위치 티칭 완료 (`TeachingPosition.Key = "inspectionReady"`)

**동작**:
1. 티칭된 검사 준비 위치의 BASE 좌표(X,Y,Z,Rx,Ry,Rz)로 MoveL 직선 이동

**검사 평면에 따른 대기 위치**:

검사 대상물의 면에 따라 대기 위치가 달라질 수 있다. 향후 검사 평면(전면, 천장면, 바닥면, 챔퍼부 등)별 대기 위치를 별도로 티칭하고, 시퀀스 실행 시 선택하는 구조로 확장 예정이다.

| 검사 평면 | 설명 | 비고 |
|-----------|------|------|
| 전면 | 대상물 정면 | 기본 |
| 천장면 | 대상물 상단 | 코봇 자세 변경 필요 |
| 바닥면 | 대상물 하단 | 코봇 자세 변경 필요 |
| 챔퍼부 | 모서리/경사면 | 경사 각도에 따른 접근 |

---

### ③ 카메라 거리 정렬 (`CameraAlignStep`)

**목적**: 뎁스 카메라로 검사 카메라의 초점거리(약 400mm)까지 툴을 이동시킨다.

**선행조건**:
- 코봇 RPC 연결, 카메라 연결
- 검사 준비 위치 티칭 완료
- 현재 자세가 검사 준비 위치일 것 (② 단계 완료)

**동작**:
1. 현재 자세가 검사 준비 위치인지 관절각으로 검증 (±0.5°)
2. 깊이 ROI의 최소값을 100ms 간격 10회 샘플링 → 유효값 평균
3. 목표 거리(400mm)와의 차이(delta) 계산
4. 안전 가드: |delta| > 600mm이면 중단 (폭주 방지)
5. tool 1을 BASE -Y 방향으로 delta만큼 MoveByOffset

**깊이 ROI**: 카메라 페이지에서 저장한 ROI가 있으면 사용, 없으면 중앙 30% 기본영역.

**파라미터**:

| 항목 | 값 | 설명 |
|------|-----|------|
| TargetDistanceMm | 400 | 목표 초점거리 |
| MaxAlignTravelMm | 600 | 1회 보정 최대 이동량 |
| 측정 횟수 | 10회 × 100ms | 1초간 샘플링 |

---

### ④ 평탄면 센터링 (`FlatSurfaceAlignStep`)

**목적**: 3점 레이저 변위센서로 정밀 초점거리를 측정하기 위해, 가장 평평한 면을 센서 중앙에 위치시킨다.

레이저 변위센서의 3점 측정이 신뢰성 있으려면 3개 레이저 포인트가 모두 평탄한 면 위에 있어야 한다. 용접 비드, 모서리, 챔퍼 등 depth 변화가 큰 영역에서는 측정이 왜곡된다.

**선행조건**:
- 코봇 RPC 연결, 카메라 연결, 레이저 변위센서 연결
- 검사 준비 위치 티칭 완료

#### Phase A — 뎁스 카메라 평탄영역 탐색

깊이 프레임의 ROI를 5×5(25셀) 그리드로 분할하고, 각 셀 내 depth 픽셀의 표준편차(σ)를 계산한다. σ가 최소인 셀이 가장 평평한 영역이다.

```
알고리즘:
1. ROI를 N×N 그리드로 분할 (기본 N=5)
2. 각 셀 내:
   - 유효 depth 픽셀(≠0)의 합(sum), 제곱합(sumSq), 개수(valid) 계산
   - 유효 비율 < 50%인 셀은 후보에서 제외
   - σ = √(sumSq/valid - (sum/valid)²)
3. 10회 샘플 중 σ가 최소인 결과 채택 (뎁스 노이즈 평활화)
4. 해당 셀 중심의 정규화 좌표 (u, v) 출력
```

**왜 depth σ인가**: 평탄면은 depth 값이 균일(σ ≈ 0)하고, 용접 비드/모서리/챔퍼 등은 depth 변화가 커서 σ가 높다. 단순하면서도 효과적인 평탄도 지표이다.

#### Phase B — 코봇 횡이동

평탄 셀 중심과 현재 센서 중심(ROI 중심) 사이의 픽셀 오프셋을 mm로 환산하고, MoveByOffset으로 BASE XY 횡이동한다.

```
픽셀 → mm 변환:
1. 카메라 intrinsics 사용 가능 시 (CameraD2CParams):
   Δmm = Δpx × depth / fx
2. intrinsics 미사용 시 (FOV 근사):
   Intel RealSense D435 Depth 공칭 FOV: H≈87°, V≈58°
   Δmm = Δu × 2 × depth × tan(FOV/2)
```

**좌표 매핑** (기본 가정: 카메라가 전방을 향할 때):
- 뎁스 이미지 X → 코봇 BASE X
- 뎁스 이미지 Y → 코봇 BASE -Z

실제 장착 방향에 따라 매핑을 조정해야 한다.

**안전 가드**: 횡이동량 > 100mm이면 중단.

#### Phase C — 레이저 3점 검증/미세보정

레이저 변위센서의 `GetPlanePose()`로 평면 틸트(rx, ry)를 측정하고, 임계값 초과 시 반복 보정한다.

```
반복 보정 알고리즘:
1. 3회 샘플 평균으로 PlanePose(rx, ry, z) 획득
2. |rx| < 0.5° AND |ry| < 0.5° → 정렬 완료
3. 초과 시:
   - 보정량 = tan(틸트) × 현재 거리
   - Δx ∝ ry (Y축 틸트 → X방향 이동)
   - Δy ∝ rx (X축 틸트 → Y방향 이동)
   - 1회 보정량 최대 ±20mm (클램프)
   - MoveByOffset 실행 (속도 = min(설정속도, 10%))
4. 최대 5회 반복. 수렴 실패 시 중단.
```

**파라미터 요약**:

| 항목 | 값 | 설명 |
|------|-----|------|
| TiltThresholdDeg | 0.5° | 평탄 판정 임계값 |
| MaxCorrectionIterations | 5 | 미세보정 최대 반복 |
| MaxCorrectionMoveMm | 20mm | 1회 보정 최대 이동량 |
| GridSize | 5 | 평탄도 분석 그리드 (5×5) |
| MaxLateralMoveMm | 100mm | Phase B 횡이동 최대 허용 |
| 측정 샘플 | Phase A: 10회, Phase C: 3회 | 노이즈 저감 |

---

## 4. 하드웨어 인터페이스

### 사용 장비 및 통신

| 장비 | 프로토콜 | 서비스 | 시퀀스 사용 단계 |
|------|----------|--------|-----------------|
| AMR | Modbus TCP (10.10.100.200:502) | AMRService | ① |
| FAIRINO 코봇 | XML-RPC (10.10.100.11:20003) | CobotService | ①②③④ |
| Intel RealSense D435 | librealsense USB | CameraService | ③④ |
| OMRON ZP-LS300S | EtherNet/IP (192.168.0.1:44818) | LaserDisplacementSensorService | ④ |

### 레이저 변위센서 헤드 배치 (툴 좌표계)

3개 헤드가 툴(플랜지)에 강체 고정. 빔 방향 = 툴 +Z 평행.

```
        Ch2 (+Y)
         ▲
        / \
       /   \
      /     \
     /       \
   Ch1 ───── Ch3   → (+X)
```

기본값 (placeholder, 실장비에서 확정 필요):
- Ch1: (-50, 0) mm
- Ch2: (0, 86.6) mm — 등변삼각형 높이
- Ch3: (50, 0) mm

설정: `appsettings.json`의 `LaserDisplacementSensor` 섹션 `Head1OffsetXmm` ~ `Head3OffsetYmm`.

---

## 5. 상태 관리 및 UI

### SequenceService 상태

- `RunState`: Idle / Running / Stopping
- `CurrentStepKey`: 현재 실행 중인 단계 (null이면 idle)
- `StepStatuses`: 각 단계의 상태 스냅샷 (Pending / Running / Completed / Failed / Skipped)
- `StateChanged` 이벤트: UI의 `StateHasChanged()` 트리거

### Sequence.razor UI

- 단계 목록 테이블: 각 단계의 상태 배지, 검증 결과, 실행 버튼
- 풀오토 버튼: 전체 시퀀스 자동 실행
- 정지 버튼: 즉시 정지 (CancellationToken + StopMotionImmediate)
- 오류 해제 버튼: 코봇 fault 복구

---

## 6. 향후 확장 계획

- **검사 평면별 대기 위치**: 전면/천장/바닥/챔퍼별 티칭 위치 분리
- **AMR 이동 명령 구현**: Modbus TCP 레지스터를 통한 Task/Job 실행
- **검사 스캔 단계 추가**: 도면 기반 웨이포인트 순회 + 비전/레이저 측정
- **용접 검사 단계 추가**: 용접선 추적, 비드 검사
- **텔레스코픽 모듈 통합**: RS232C 통신 (현재 별도 TEST 프로젝트)
- **단계 활성화/비활성화**: UI에서 개별 단계 on/off 토글
- **시퀀스 프리셋**: 용도별(검사, 용접 등) 사전 정의 시퀀스
