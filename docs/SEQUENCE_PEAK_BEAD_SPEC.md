# 시퀀스 ⑤~⑫ 구현 명세서 — Peak/Bead 측정 및 각도 산출

> **문서 목적**: HD_AMR 시퀀스 페이지에 Peak 찾기 / 센터링 / Bead 찾기 / 각도 산출 단계를 추가하기 위한 구현 명세.
> 이 문서만 읽고도 구현이 가능하도록 기존 코드의 위치·시그니처·동작을 함께 기술한다.
>
> - **대상 저장소**: `HD_AMR`
> - **작성 기준 브랜치**: `claude/sequence-page-unit-action-591aab`
> - **상태**: 설계 확정, 구현 미착수
> - **선행 조건**: 시퀀스 ①~④가 이미 구현되어 동작 중

---

## 목차

1. [배경 및 목표](#1-배경-및-목표)
2. [기존 아키텍처](#2-기존-아키텍처)
3. [재사용할 기존 자산](#3-재사용할-기존-자산)
4. [좌표계·축·부호 규약](#4-좌표계축부호-규약)
5. [시퀀스 단계 정의](#5-시퀀스-단계-정의)
6. [파라미터 정의](#6-파라미터-정의)
7. [변경 파일 목록](#7-변경-파일-목록)
8. [상세 구현 명세](#8-상세-구현-명세)
9. [실패 처리 정책](#9-실패-처리-정책)
10. [빌드·검증 절차](#10-빌드검증-절차)
11. [브링업 절차](#11-브링업-절차)
12. [설계 결정 이력](#12-설계-결정-이력)
13. [보류 항목](#13-보류-항목)

---

## 1. 배경 및 목표

### 1.1 측정 대상

측정물은 **코루게이션(파형) 판재**이며, 다음 특징을 가진다.

- **Peak(마루)** 가 일정 간격(**pitch, 공칭 370mm**)으로 반복된다.
- Peak를 가로지르는 방향으로 **용접 비드(bead)** 가 판재 **양 끝까지 연속으로** 이어져 있다.
- 목표는 인접한 두 Peak 위치에서 비드의 위치 오차 `d1`, `d2`를 측정하고,
  `θ = atan2(d2 − d1, pitch)` 로 **비드선의 기울기(각도)** 를 산출하는 것이다.

### 1.2 기존 수동 절차 (Weld Tracking 페이지)

현재는 운영자가 `WeldTrackingPanel.razor`에서 수동으로 수행한다.

1. `Peak 찾기` 버튼 → 자홍색 Peak 선 표시 + FOV 센터로부터의 이격거리 표시
2. 운영자가 로봇을 수동으로 이동해 Peak를 FOV 센터에 맞춤
3. `비드 찾기 #1` 버튼 → 초록 비드선 + 빨간 교점 표시, `d1` 저장
4. 로봇을 pitch(370mm)만큼 이동
5. `Peak 찾기` → 수동 이동 → `비드 찾기 #2` → `d2` 저장
6. `각도 산출` 버튼 → θ 표시

### 1.3 목표

위 절차를 **시퀀스 페이지에서 자동 실행**되는 8개 단계(⑤~⑫)로 구현한다.
풀오토(전체 순차 실행)와 세미오토(단계별 개별 실행) 모두 지원해야 한다.

### 1.4 범위 외 (이번 구현에 포함하지 않음)

- 각도 결과의 DB 저장
- 각도에 따른 코봇 자세 보정 모션
- 시퀀스 페이지에 오버레이 이미지(썸네일) 표시
- 다중 샘플링 / 재시도
- Peak 미검출 시 스캔 복구

---

## 2. 기존 아키텍처

### 2.1 프로젝트 구조

```
HD_AMR/                          ← 솔루션 폴더 (git root 기준 한 단계 아래)
├── HD_AMR/                      ← 클래스 라이브러리 (도메인/통신/서비스)
│   ├── Communication/
│   │   ├── FairinoRpcClient.cs          코봇 RPC
│   │   ├── RealSenseSettings.cs         카메라 해상도 설정
│   │   └── Weld/
│   │       ├── DepthPeakAnalyzer.cs     Peak 탐색 (깊이 기반, OpenCV 불필요)
│   │       ├── WeldMaskAnalyzer.cs      비드 중심선/교점 + 오버레이 렌더링
│   │       ├── IWeldVisionDetector.cs   검출기 인터페이스
│   │       ├── WeldVisionDetector.cs    고전 CV 검출기
│   │       └── RoiProfileStore.cs       ROI JSON 영속화
│   ├── Models/
│   │   ├── WeldModels.cs                RoiRect, PeakInfo, PeakFindResult 등
│   │   └── CameraD2CParams.cs
│   └── Service/
│       ├── CameraService.cs             RealSense 프레임/깊이/픽셀→mm
│       ├── CobotService.cs              코봇 래퍼 (BackgroundService)
│       ├── ParameterService.cs          key/value 파라미터 (EF Core + SQLite)
│       ├── TeachingService.cs           티칭 위치
│       ├── WeldTrackingService.cs       Peak/Bead 오케스트레이션 (싱글톤)
│       └── Sequence/
│           ├── ISequenceStep.cs         스텝 인터페이스 + 컨텍스트 + 결과 타입
│           ├── SequenceService.cs       실행 엔진
│           └── Steps/
│               ├── AmrMoveStep.cs               ① order 100
│               ├── CobotInspectionMoveStep.cs   ② order 200
│               ├── CameraAlignStep.cs           ③ order 300
│               └── FlatSurfaceAlignStep.cs      ④ order 400
└── HD_AMR.Web/                  ← Blazor Server
    ├── Program.cs                       DI 등록
    ├── Components/Pages/
    │   ├── Sequence.razor               시퀀스 페이지
    │   └── WeldTrackingPanel.razor      Weld 페이지
    └── Services/
        └── DlWeldVisionDetector.cs      YOLOv8-seg ONNX 검출기
```

### 2.2 시퀀스 스텝 플러그인 구조

**핵심**: `Sequence.razor`에는 하드코딩된 단계가 하나도 없다.
`ISequenceStep` 구현체를 DI에 등록하면 UI 테이블에 행이 **자동으로 추가**된다.

`HD_AMR/Service/Sequence/ISequenceStep.cs`:

```csharp
public interface ISequenceStep
{
    string Key { get; }                  // 고유 식별 키
    string DisplayName { get; }          // UI 표시명
    int DefaultOrder { get; }            // 실행 순서 (기존은 100 단위)
    StepValidation Validate(SequenceContext context);
    Task<StepResult> ExecuteAsync(SequenceContext context, CancellationToken ct);
}

public record StepValidation(bool IsValid, string? Message = null)
{
    public static StepValidation Ok() => new(true);
    public static StepValidation Fail(string message) => new(false, message);
}

public record StepResult(bool Success, string Message)
{
    public static StepResult Ok(string message) => new(true, message);
    public static StepResult Fail(string message) => new(false, message);
}

public class SequenceContext
{
    public int Tool { get; set; } = 1;
    public int Velocity { get; set; } = 20;
    public Dictionary<string, Data.Entities.TeachingPosition> Positions { get; set; } = new();
    public Dictionary<string, object> Bag { get; set; } = new();   // 스텝 간 데이터 전달
}
```

### 2.3 실행 엔진 동작

`HD_AMR/Service/Sequence/SequenceService.cs`:

- 생성자에서 `IEnumerable<ISequenceStep>`을 받아 `DefaultOrder` 순으로 정렬
- `RunAllAsync(context, ct)` — 순차 실행, **실패 시 즉시 중단**
- `RunStepAsync(key, context, ct)` — 세미오토 단일 실행
- `StopAsync()` — CTS 취소 + `CobotService.StopMotionImmediateAsync()`
- 실행 전 `TeachingService.ListAsync()`로 `context.Positions` 자동 갱신
- 예외/`OperationCanceledException`은 엔진이 캐치하여 `StepState.Failed`로 처리

> **중요**: 실패 시 후속 단계 중단은 엔진이 이미 처리한다. 각 스텝은 `StepResult.Fail`만 반환하면 된다.

### 2.4 세미오토 시 컨텍스트 유지

`Sequence.razor`의 `_context`는 **컴포넌트 필드**이므로 세미오토로 단계를 하나씩 눌러도
`Bag`의 내용이 유지된다. 단, 사용자가 임의 순서로 실행할 수 있으므로
**후속 단계는 `Validate()`에서 선행 데이터 존재 여부를 반드시 검사해야 한다.**

```csharp
// Sequence.razor:126
private readonly SequenceContext _context = new();
```

---

## 3. 재사용할 기존 자산

### 3.1 `WeldTrackingService` (싱글톤)

`HD_AMR/Service/WeldTrackingService.cs`

| 멤버 | 시그니처 | 설명 |
|---|---|---|
| `PeakRoi` | `RoiRect?` (private set) | Peak 탐색 ROI. `SetPeakRoi(RoiRect?)` 로 설정 |
| `WeldRoi` | `RoiRect?` (private set) | 비드 검출 ROI. `SetWeldRoi(RoiRect?)` 로 설정 |
| `Params` | `WeldDetectionParams` (get only) | 검출 파라미터. `Mode`, `ProgressAxis`, `DlConfidence` 등 |
| `Method` | `WeldDetectionMethod` | `Dl`(기본) 또는 `Param` |
| `DetectorAvailable` | `bool` | 검출기 사용 가능 여부 (Windows + OpenCV) |
| `Pitch` | `double` | 각도 산출용 pitch(mm). **기본 0, 영속화 안 됨** |
| `ScaleCorrection` | `double` | 2점 보정계수. 기본 1.0 |
| `ScaleCorrectionEnabled` | `bool` | 2점 보정 적용 여부 |
| `ScaleAvailable` | `bool` | `Fx() > 0` |
| `M1`, `M2` | `PeakMeasurement?` (private set) | Peak #1/#2 측정 슬롯 |
| `Angle` | `AngleResult?` (private set) | 각도 산출 결과 |
| `State` | `WeldTrackingState` (private set) | 상태 머신 |
| `Message` | `string?` (private set) | UI 표시 메시지 |
| `FindPeak()` | `PeakFindResult` | **동기**. Peak 찾기 + 오버레이 생성 |
| `CapturePeak(int id)` | `void` | **동기**. Peak + Bead 검출 → `M1`/`M2` 저장 |
| `ComputeAngle()` | `void` | `M1`,`M2`,`Pitch`로 θ 산출 → `Angle` |
| `DMm(PeakMeasurement m)` | `double` | 픽셀 d → mm 환산 |
| `ResetMeasurements()` | `void` | `M1`/`M2`/`Angle`/`LastPeakFind` 초기화 |

**제약 (신규 코드가 해결해야 할 문제)**:
- 모든 메서드가 **동기**이며 `CancellationToken`을 받지 않는다.
- **락이 없다.** Weld 페이지를 열어둔 채 시퀀스를 돌리면 레이스가 발생한다.
- ROI를 **인자로 받지 않고** 내부 상태(`PeakRoi`/`WeldRoi`)를 사용한다.
  시퀀스가 이를 대입하면 운영자가 튜닝해 저장한 프로파일을 덮어쓴다.

### 3.2 `CameraService` (싱글톤)

`HD_AMR/Service/CameraService.cs`

```csharp
public CameraFrame? LatestColor { get; }   // "mjpg" 또는 "rgb24"
public CameraFrame? LatestDepth { get; }   // "depth16", LE uint16 mm, 0=무효
public CameraFrame? LatestIr    { get; }   // "ir8" 또는 "ir16"
public bool IsConnected { get; }
public bool IsIrActive { get; }
public CameraD2CParams? GetD2CParams();

public int? GetLatestDepthMmAt(double u, double v);              // 정규화 0..1
public DepthRoiStats? ComputeDepthRoiStats(double x, double y, double w, double h);
public DepthFlatnessResult? FindFlattest(double roiX, double roiY, double roiW, double roiH,
                                          int gridSize = 5, double minValidRatio = 0.5);

/// deltaU/deltaV 는 정규화 델타([-1,1]), zMm 은 거리. intrinsics 우선, FOV 근사 폴백.
public (double DxMm, double DyMm) PixelDeltaToMm(double deltaU, double deltaV, double zMm);
public CenterOffsetResult? ComputeCenterOffsetMm(double u, double v, double? zMm = null);
```

### 3.3 `CobotService` / `FairinoRpcClient`

```csharp
// CobotService
public FairinoRpcClient Rpc { get; }
public bool IsConnected { get; }
public FairinoState? State { get; }

// FairinoRpcClient — 이번 구현에서 사용
public async Task<double[]> GetTcpPoseInBaseAsync(int tool, CancellationToken ct = default);
public Task<int> MoveByOffsetAsync(double[] anchorPose, int user, double[] offset,
                                   int? tool = null, double? vel = null, CancellationToken ct = default);
// offset = [dx, dy, dz, drx, dry, drz]  (BASE 프레임 기준, mm/deg)
// 반환값 0 = 성공. 그 외는 에러코드 → FairinoErrorCodes.Suffix(rc) 로 설명 문자열 획득
```

### 3.4 `ParameterService` (Scoped)

`HD_AMR/Service/ParameterService.cs`

```csharp
public Task<string?> GetAsync(string name);
public Task<int?>    GetIntAsync(string name);
public Task<double?> GetDoubleAsync(string name);
public Task<bool?>   GetBoolAsync(string name);
public Task SetAsync(string name, string value, string? description = null);
public Task SetDoubleAsync(string name, double value, string? description = null);
public Task SetBoolAsync(string name, bool value, string? description = null);
```

### 3.5 관련 모델 타입

`HD_AMR/Models/WeldModels.cs`

```csharp
public sealed record RoiRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public bool IsEmpty => Width <= 0 || Height <= 0;
    public RoiRect? ClampTo(int frameW, int frameH);
    public static RoiRect Full(int w, int h);
}

public sealed class PeakFindResult
{
    public bool Found { get; init; }
    public string? Message { get; init; }
    public double ProgressPos { get; init; }    // 진행축 좌표(px, 검출 프레임 기준)
    public double OffsetPx { get; init; }       // Peak − FOV센터 (px)
    public double OffsetMm { get; init; }       // 위를 mm 환산 (ScaleAvailable=false면 무의미)
    public int    DepthMm { get; init; }        // Peak 지점 깊이(mm)
    public double Confidence { get; init; }
    public bool   ScaleAvailable { get; init; }
    public static PeakFindResult Fail(string msg);
}

public sealed class PeakMeasurement
{
    public int PeakId { get; init; }            // 1 또는 2
    public double DPixel { get; init; }
    public double Confidence { get; init; }
    public PeakInfo? Peak { get; init; }
    public DateTime At { get; init; }
    public byte[]? OverlayJpeg { get; init; }
    public double DepthZ { get; init; }
}

public sealed class WeldDetectionResult
{
    public bool Success { get; init; }
    public string? Message { get; init; }
    public double Confidence { get; init; }     // = coverage (0~1)
    public IReadOnlyList<PixelPoint> Centerline { get; init; }
    public double ReferencePos { get; init; }
    public double WeldCenterAtTarget { get; init; }
    public double DPixel { get; init; }
    public PixelPoint? WeldPoint { get; init; }
    public PixelPoint? RefPoint { get; init; }
    public byte[]? OverlayJpeg { get; init; }
    public IReadOnlyList<BeadSpan>? BeadSpans { get; init; }
    // ★ LineFitOk 를 신규 추가한다 (§8.2 참조)
}

public sealed class AngleResult
{
    public double D1 { get; init; }             // mm
    public double D2 { get; init; }             // mm
    public double Pitch { get; init; }          // mm
    public double NominalPitch { get; init; }
    public double PeakShift { get; init; }      // 현재 항상 0
    public bool   Corrected { get; init; }      // 현재 항상 false
    public double ThetaRad { get; init; }
    public double ThetaDeg { get; init; }
    public string Unit { get; init; }
}
```

### 3.6 카메라 해상도 (중요)

`HD_AMR/Communication/RealSenseSettings.cs`

```csharp
public int ColorWidth  { get; set; } = 1280;
public int DepthWidth  { get; set; } = 848;
public int DepthHeight { get; set; } = 480;
public int IrWidth     { get; set; } = 848;   // ← Depth 와 동일
public int IrHeight    { get; set; } = 480;   // ← Depth 와 동일
```

**IR 해상도 = Depth 해상도**이므로, IR 모드에서는 Depth 좌표와 검출 프레임 좌표가 **완전히 동일**하다.
`WeldTrackingService.ComputePeak()`도 IR 모드에서 좌표 변환 없이 그대로 사용한다:

```csharp
// WeldTrackingService.cs:291
if (Params.Mode == WeldImageMode.Ir)
    return DepthPeakAnalyzer.Analyze(depth, PeakRoi, Params.ProgressAxis);
```

> **따라서 이번 구현은 IR 모드를 전제로 한다.** RGB 모드는 해상도가 다르고(1280×720)
> D2C 재투영이 필요하므로 `Validate()`에서 차단한다.

---

## 4. 좌표계·축·부호 규약

### 4.1 기존 시퀀스의 축 사용

| 스텝 | 축 매핑 |
|---|---|
| ③ `CameraAlignStep` | **BASE Y** = 카메라 시선(standoff). 400mm 정렬을 −Y 방향으로 |
| ④ `FlatSurfaceAlignStep` Phase B | **BASE X** = 영상 가로, **BASE −Z** = 영상 세로 |

### 4.2 이번 구현의 축 사용

- **진행축(progress axis) = 영상 가로 = BASE X**
  (`Params.ProgressAxis == WeldProgressAxis.Horizontal` 전제)
- Peak는 BASE X 방향으로 pitch(370mm) 간격 배열
- 센터링 이동과 Peak2 이동 모두 **BASE X** 단일 축

### 4.3 두 개의 독립적인 부호 파라미터

**절대로 하나로 합치지 말 것.** 서로 독립된 미지수다.

| 파라미터 | 의미 | 결정 요인 |
|---|---|---|
| `Camera.Axis.XSign` | 영상 +X 픽셀 → BASE ±X | **카메라 장착 방향** (물리적 고정) |
| `Weld.Peak.PitchDir` | Peak2가 Peak1의 어느 쪽인가 | **측정 방향 선택** (설비 배치) |

**연산식**:

```
⑥⑩ 센터링 이동량(BASE X, mm) = (−1) × offsetMm × XSign
⑧   Peak2 이동량(BASE X, mm) = PitchMm × PitchDir
```

> `XSign`과 `PitchDir`을 **곱하지 않는다.** 하나를 뒤집어도 다른 쪽 동작에 영향이 없어야 한다.
> 합쳐놓으면 ⑧의 방향을 바꾸려 할 때 ⑥⑩ 센터링이 같이 뒤집혀 발산한다.

### 4.4 mm 환산 방식 (하이브리드)

`PeakFindResult.OffsetMm`은 `WeldTrackingService.EffectiveMmPerPixel()`이 계산하며,
이는 **간이 FOV intrinsic**(appsettings의 `IrHFovDeg=90`)에 **2점 실측 보정계수**를 곱한 값이다.

```csharp
// WeldTrackingService.cs:469
private double EffectiveMmPerPixel(double depthZ)
{
    double fx = Fx();                          // (w/2)/tan(HFov/2) — SDK intrinsics 미사용
    if (fx <= 0) return 0;
    double z = depthZ > 0 ? depthZ : _settings.DefaultWorkDistanceMm;
    double mmpp = z / fx;
    return ScaleCorrectionEnabled ? mmpp * ScaleCorrection : mmpp;
}
```

반면 `CameraService.PixelDeltaToMm()`은 **실제 D2C intrinsics**를 쓰지만 실측 보정이 없다.

**채택 규칙**:

| 조건 | 사용할 값 |
|---|---|
| `ScaleCorrectionEnabled == true` | `PeakFindResult.OffsetMm` (2점 실측 보정됨 → 우선) |
| `ScaleCorrectionEnabled == false` | `CameraService.PixelDeltaToMm()` 경로로 재환산 + 경고 로그 |
| 두 값 차이 > 15% | 경고 로그 (동작은 계속) |

**`CameraService` 경로 환산식** (진행축이 Horizontal일 때):

```csharp
var frame = camera.LatestIr;                          // 848×480
double mmPerPx = camera.PixelDeltaToMm(1.0 / frame.Width, 0, depthMm).DxMm;
double offsetMm = result.OffsetPx * mmPerPx;
```

> `PixelDeltaToMm`은 **정규화 델타**([-1,1])를 받는다. 픽셀 값을 그대로 넣으면 안 된다.

---

## 5. 시퀀스 단계 정의

### 5.1 전체 구성

| # | Key | Order | 클래스 | peakId | 설명 |
|---|---|---|---|---|---|
| ① | `amrMove` | 100 | `AmrMoveStep` | — | (기존) AMR 검사위치 이동 |
| ② | `cobotInspection` | 200 | `CobotInspectionMoveStep` | — | (기존) Cobot 검사위치 이동 |
| ③ | `cameraAlign` | 300 | `CameraAlignStep` | — | (기존) 카메라 거리 정렬 400mm |
| ④ | `flatSurfaceAlign` | 400 | `FlatSurfaceAlignStep` | — | (기존) 평탄면 센터링 |
| **⑤** | `peak1Find` | **500** | `PeakFindStep` | 1 | Peak1 찾기 |
| **⑥** | `peak1Center` | **600** | `PeakCenteringStep` | 1 | Peak1 센터링 |
| **⑦** | `bead1Find` | **700** | `BeadFindStep` | 1 | Bead1 찾기 |
| **⑧** | `peak2Approach` | **800** | `PeakApproachStep` | 2 | Peak2 이동 (pitch) |
| **⑨** | `peak2Find` | **900** | `PeakFindStep` | 2 | Peak2 찾기 |
| **⑩** | `peak2Center` | **1000** | `PeakCenteringStep` | 2 | Peak2 센터링 |
| **⑪** | `bead2Find` | **1100** | `BeadFindStep` | 2 | Bead2 찾기 |
| **⑫** | `weldAngle` | **1200** | `WeldAngleStep` | — | 각도 산출 |

> 클래스는 8개가 아니라 **5개**다. ⑤⑨, ⑥⑩, ⑦⑪은 `peakId`만 다른 동일 동작이므로
> 생성자 파라미터로 구분하고 DI에 두 번 등록한다.

### 5.2 각 단계 동작 요약

#### ⑤⑨ Peak 찾기 (`PeakFindStep`)

1. Depth ROI 파라미터를 읽어 `RoiRect`(픽셀)로 변환
2. `WeldTrackingService.FindPeakAsync(roi, ct)` 호출
3. 자홍색 Peak 선이 그려진 오버레이가 서버에 저장됨 (Weld 페이지에서 확인 가능)
4. FOV 센터로부터의 이격거리를 mm로 환산 (§4.4)
5. `context.Bag["peak{id}.find"]`에 결과 저장
6. **peakId == 2일 때 추가 가드**: `|offsetMm| > PitchMm / 3` 이면 실패
   (⑧에서 pitch만큼 이동했으므로 Peak2는 센터 근처에 있어야 정상. 크게 벗어났으면 다른 Peak를 잡은 것)

#### ⑥⑩ Peak 센터링 (`PeakCenteringStep`)

1. `Bag["peak{id}.find"]`에서 `offsetMm`을 읽음
2. 이동량 = `(−1) × offsetMm × XSign`, **±100mm로 클램프**
3. `GetTcpPoseInBaseAsync(tool)` → `MoveByOffsetAsync(anchor, user:0, [move,0,0,0,0,0], tool, vel)`
4. **이동 후 `FindPeakAsync` 1회 재측정** (모션 없음)
5. 잔차 판정:
   - `|잔차| < |초기 offset|` → 성공
   - `|잔차| >= |초기 offset|` → **실패**, `"이격거리 증가 — Camera.Axis.XSign 확인 필요"` 메시지
6. `Bag["peak{id}.find"]`를 재측정 결과로 **갱신** (⑦⑪이 최신 값을 쓰도록)

> **반복 루프 없음, 수렴 임계 없음.** 개루프 1회 이동 + 검증 1회.
> 재측정의 목적은 정밀도가 아니라 **XSign 오설정 조기 발견**이다.

#### ⑦⑪ Bead 찾기 (`BeadFindStep`)

1. Depth ROI를 Peak ROI / Weld ROI 양쪽에 사용
2. `WeldTrackingService.CapturePeakAsync(id, peakRoi, weldRoi, ct)` 호출
   - 내부에서 Peak 재검출 → 비드 마스크 검출 → 초록 중심선 피팅 → 빨간 교점 계산
   - `M1` 또는 `M2` 슬롯에 저장, 오버레이 생성
3. 실패 판정 (§9)
4. 성공 시 `d`(mm), coverage, 외삽 거리를 메시지에 기록

#### ⑧ Peak2 이동 (`PeakApproachStep`)

1. `PitchMm`(370)과 `PitchDir`(±1)을 읽음
2. 이동량 = `PitchMm × PitchDir` (BASE X)
3. `MoveByOffsetAsync` 실행
4. 안정화 대기 `await Task.Delay(500, ct)`

#### ⑫ 각도 산출 (`WeldAngleStep`)

1. `M1`, `M2` 존재 확인
2. `WeldTrackingService.Pitch = PitchMm` 설정 (기본값 0이므로 **반드시 설정 필요**)
3. `ComputeAngle()` 호출
4. `Angle` 결과를 메시지로 표시
5. `M1`/`M2`의 품질 지표(coverage, 외삽 거리)를 종합해 신뢰도 경고 병기

---

## 6. 파라미터 정의

### 6.1 신규 ParameterService 키

| 키 | 타입 | 기본값 | 설명 |
|---|---|---|---|
| `Weld.Peak.PitchMm` | double | `370` | Peak 간 pitch(mm). ⑧ 이동량 및 ⑫ 각도식 분모 |
| `Weld.Peak.PitchDir` | double | `+1` | Peak2 이동 방향. `+1` 또는 `−1` |
| `Camera.Axis.XSign` | double | `+1` | 영상 +X → BASE ±X. `+1` 또는 `−1` |

> `GetDoubleAsync`로 읽고, 값이 없으면 기본값을 사용한다.
> 부호 파라미터는 `Math.Sign()`으로 정규화한다. `0`이면 `+1`로 간주.

**입력 UI (구현됨)**: Sequence 페이지의 각 행 `파라미터 (mm)` 컬럼에서 인라인 편집한다
(main 머지로 들어온 ②③ 패턴과 동일 — `@bind:after`로 즉시 DB 저장).

| 행 | 입력 | 바인딩 키 |
|---|---|---|
| ⑥⑩ Peak 센터링 | `X부호` 셀렉트 (+1/−1) — 두 행이 같은 값 공유 | `Camera.Axis.XSign` |
| ⑧ Peak2 이동 | `Pitch` 숫자 + `방향` 셀렉트 (+1/−1) | `Weld.Peak.PitchMm`, `Weld.Peak.PitchDir` |

스텝 코드는 UI 와 무관하게 실행 시마다 DB 에서 읽으므로, Parameters 페이지(`/parameters`)에서
수정해도 동일하게 반영된다. 두 화면은 같은 키를 편집한다.

### 6.2 기존 ParameterService 키 (재사용)

| 키 | 타입 | 폴백 | 설명 |
|---|---|---|---|
| `Camera.Depth.Roi.Enabled` | bool | `false` | 저장된 ROI 사용 여부 |
| `Camera.Depth.Roi.X` | double | `0.35` | 정규화 0~1 |
| `Camera.Depth.Roi.Y` | double | `0.35` | 정규화 0~1 |
| `Camera.Depth.Roi.W` | double | `0.30` | 정규화 0~1 |
| `Camera.Depth.Roi.H` | double | `0.30` | 정규화 0~1 |

**Depth ROI를 Peak ROI / Weld ROI로 그대로 사용한다.**
정규화 → 픽셀 변환은 IR 프레임 크기 기준:

```csharp
var f = camera.LatestIr;   // 848×480
var roi = new RoiRect(
    (int)Math.Round(x * f.Width),
    (int)Math.Round(y * f.Height),
    (int)Math.Round(w * f.Width),
    (int)Math.Round(h * f.Height)
).ClampTo(f.Width, f.Height);
```

> **운영 지침**: Depth ROI 폭은 **370mm 이상**으로 설정해야 Peak를 놓치지 않는다.
> 400mm 스탠드오프 / IR 90° FOV 기준 화면 실폭은 약 800mm이므로,
> 370mm ≈ 프레임 폭의 **46%**에 해당한다.
>
> 폭이 pitch보다 넓으면 ROI 안에 Peak가 2개 들어올 수 있고, 분석기는 **더 가까운 쪽**을
> 선택한다. 판이 기울어 있으면 중앙이 아닌 Peak를 잡을 수 있다.
> → **ROI 폭 > pitch 환산 픽셀이면 경고 로그를 남긴다** (동작은 차단하지 않음).

### 6.3 기존 appsettings (참고, 변경 없음)

`HD_AMR.Web/appsettings.json`

```json
"WeldTracking": {
  "ProfileDirectory": "RoiProfiles",
  "AutoLoadProfile": "default",
  "PitchCorrectionEnabled": true,   // 미사용(dead config)
  "PitchCorrectionSign": -1,        // 미사용(dead config)
  "IrHFovDeg": 90.0,
  "ColorHFovDeg": 69.0,
  "DefaultWorkDistanceMm": 500.0
}
```

---

## 7. 변경 파일 목록

### 7.1 신규 파일

| 경로 | 내용 |
|---|---|
| `HD_AMR/HD_AMR/Service/Sequence/Steps/WeldSequenceSupport.cs` | 공용 헬퍼 (ROI 변환, mm 환산, 파라미터 키 상수) |
| `HD_AMR/HD_AMR/Service/Sequence/Steps/PeakFindStep.cs` | ⑤⑨ |
| `HD_AMR/HD_AMR/Service/Sequence/Steps/PeakCenteringStep.cs` | ⑥⑩ |
| `HD_AMR/HD_AMR/Service/Sequence/Steps/BeadFindStep.cs` | ⑦⑪ |
| `HD_AMR/HD_AMR/Service/Sequence/Steps/PeakApproachStep.cs` | ⑧ |
| `HD_AMR/HD_AMR/Service/Sequence/Steps/WeldAngleStep.cs` | ⑫ |

### 7.2 수정 파일

| 경로 | 변경 내용 |
|---|---|
| `HD_AMR/HD_AMR/Models/WeldModels.cs` | `WeldDetectionResult`에 `LineFitOk` 속성 추가 |
| `HD_AMR/HD_AMR/Communication/Weld/WeldMaskAnalyzer.cs` | `Analyze()` 반환문에 `LineFitOk = fitOk` 추가 |
| `HD_AMR/HD_AMR/Service/WeldTrackingService.cs` | `SemaphoreSlim` + `FindPeakAsync` / `CapturePeakAsync` 추가 |
| `HD_AMR/HD_AMR.Web/Program.cs` | 신규 스텝 DI 등록 |
| `HD_AMR/HD_AMR.Web/Components/Pages/Sequence.razor` | `StepCircle()`을 ⑫까지 확장 |

---

## 8. 상세 구현 명세

### 8.1 `WeldSequenceSupport.cs` (신규)

중복 코드 방지를 위한 공용 헬퍼. 기존 `CameraAlignStep`/`FlatSurfaceAlignStep`은
`GetDepthRoiAsync()`를 각자 중복 구현하고 있으나, 신규 스텝은 이 헬퍼를 공유한다.

```csharp
namespace HD_AMR.Service.Sequence.Steps;

/// <summary>⑤~⑫ 시퀀스 공용 헬퍼. ROI 변환·mm 환산·파라미터 키를 한곳에 모은다.</summary>
internal static class WeldSequenceSupport
{
    // ── 파라미터 키 ────────────────────────────────────────────────
    public const string RoiEnabledKey = "Camera.Depth.Roi.Enabled";
    public const string RoiXKey       = "Camera.Depth.Roi.X";
    public const string RoiYKey       = "Camera.Depth.Roi.Y";
    public const string RoiWKey       = "Camera.Depth.Roi.W";
    public const string RoiHKey       = "Camera.Depth.Roi.H";

    public const string PitchMmKey    = "Weld.Peak.PitchMm";
    public const string PitchDirKey   = "Weld.Peak.PitchDir";
    public const string XSignKey      = "Camera.Axis.XSign";

    public const double DefaultPitchMm = 370.0;

    /// <summary>Bag 키: Peak 찾기 결과. id는 1 또는 2.</summary>
    public static string PeakFindBagKey(int id) => $"peak{id}.find";

    /// <summary>정규화 Depth ROI를 읽어 IR 프레임 픽셀 ROI로 변환. 실패 시 null.</summary>
    public static async Task<(RoiRect? Roi, string Src)> GetRoiAsync(
        ParameterService param, CameraService camera)
    {
        var f = camera.LatestIr;
        if (f is null) return (null, "IR 프레임 없음");

        double x = 0.35, y = 0.35, w = 0.30, h = 0.30;
        string src = "중앙 기본 ROI";
        try
        {
            if (await param.GetBoolAsync(RoiEnabledKey) == true)
            {
                var px = await param.GetDoubleAsync(RoiXKey) ?? 0;
                var py = await param.GetDoubleAsync(RoiYKey) ?? 0;
                var pw = await param.GetDoubleAsync(RoiWKey) ?? 0;
                var ph = await param.GetDoubleAsync(RoiHKey) ?? 0;
                if (pw > 0 && ph > 0 && px + pw <= 1.0001 && py + ph <= 1.0001)
                {
                    x = px; y = py; w = pw; h = ph;
                    src = "저장 ROI";
                }
            }
        }
        catch { /* DB 미준비 등 — 기본값 폴백 */ }

        var roi = new RoiRect(
            (int)Math.Round(x * f.Width),
            (int)Math.Round(y * f.Height),
            (int)Math.Round(w * f.Width),
            (int)Math.Round(h * f.Height)).ClampTo(f.Width, f.Height);

        return (roi, src);
    }

    /// <summary>부호 파라미터(+1/−1) 읽기. 없거나 0이면 +1.</summary>
    public static async Task<int> GetSignAsync(ParameterService param, string key)
    {
        try
        {
            var v = await param.GetDoubleAsync(key);
            if (v is null || v == 0) return 1;
            return Math.Sign(v.Value);
        }
        catch { return 1; }
    }

    public static async Task<double> GetPitchMmAsync(ParameterService param)
    {
        try { return await param.GetDoubleAsync(PitchMmKey) ?? DefaultPitchMm; }
        catch { return DefaultPitchMm; }
    }

    /// <summary>
    /// Peak 이격거리를 mm로 환산. 2점 보정이 켜져 있으면 WeldTrackingService 값(실측 보정)을,
    /// 아니면 CameraService intrinsics 경로를 사용한다. 두 값 차이가 15% 넘으면 경고를 반환.
    /// </summary>
    public static (double Mm, string Note) ResolveOffsetMm(
        PeakFindResult r, WeldTrackingService weld, CameraService camera)
    {
        double weldMm = r.OffsetMm;

        double camMm = 0;
        var f = camera.LatestIr;
        if (f is not null && f.Width > 0 && r.DepthMm > 0)
        {
            double mmPerPx = camera.PixelDeltaToMm(1.0 / f.Width, 0, r.DepthMm).DxMm;
            camMm = r.OffsetPx * mmPerPx;
        }

        if (weld.ScaleCorrectionEnabled && r.ScaleAvailable)
        {
            string note = "";
            if (camMm != 0 && Math.Abs(weldMm) > 1e-6)
            {
                double diffPct = Math.Abs(camMm - weldMm) / Math.Abs(weldMm) * 100;
                if (diffPct > 15) note = $" ⚠ 스케일 경로 차이 {diffPct:0}%";
            }
            return (weldMm, $"2점보정(×{weld.ScaleCorrection:0.000}){note}");
        }

        if (camMm != 0)
            return (camMm, "intrinsics ⚠ 2점 보정 미적용");

        return (weldMm, "FOV 근사 ⚠ 2점 보정 미적용");
    }

    /// <summary>pitch를 픽셀로 환산. 실패 시 0.</summary>
    public static double PitchToPixels(double pitchMm, int depthMm, CameraService camera)
    {
        var f = camera.LatestIr;
        if (f is null || f.Width <= 0 || depthMm <= 0) return 0;
        double mmPerPx = camera.PixelDeltaToMm(1.0 / f.Width, 0, depthMm).DxMm;
        return mmPerPx > 0 ? pitchMm / mmPerPx : 0;
    }
}
```

> `RoiRect`, `PeakFindResult`는 `HD_AMR.Models` 네임스페이스에 있다. `using HD_AMR.Models;` 필요.

### 8.2 `WeldModels.cs` 수정

`WeldDetectionResult`에 속성 하나 추가:

```csharp
/// <summary>
/// 비드 중심선을 최소자승 <b>직선</b>으로 피팅했는지 여부.
/// false = 피팅 실패로 중앙값 폴백 사용 → 빨간 교점이 Peak 위치와 무관해지므로
/// 각도 산출의 근거로 쓸 수 없다.
/// </summary>
public bool LineFitOk { get; init; } = true;
```

**기본값을 `true`로 두는 이유**: `WeldDetectionResult.Fail(msg)` 같은 기존 생성 경로가
이 필드를 설정하지 않으므로, 기본값이 `false`면 실패 경로에서 의미가 뒤섞인다.
`Success == false`인 경우 `LineFitOk`는 의미 없는 값이므로 **`Success`를 먼저 검사**해야 한다.

### 8.3 `WeldMaskAnalyzer.cs` 수정

`Analyze()`의 **성공 반환문**(현재 `WeldMaskAnalyzer.cs:160` 부근)에 한 줄 추가:

```csharp
return new WeldDetectionResult
{
    Success = true,
    Confidence = Math.Clamp(coverage, 0, 1),
    LineFitOk = fitOk,                          // ★ 추가
    Centerline = pts,
    ReferencePos = refPos,
    WeldCenterAtTarget = weldCenterFull,
    DPixel = d,
    WeldPoint = weldPt,
    RefPoint = refPt,
    // ... 나머지 기존 필드 유지
};
```

`fitOk`는 이미 `var (a, b, fitOk) = FitLine(centerCross, valid, sCount);` (`:88`)로 계산되어 있다.

> **회귀 영향 없음**: Weld 페이지는 이 필드를 읽지 않는다.

### 8.4 `WeldTrackingService.cs` 확장

#### 8.4.1 락 필드 추가

```csharp
/// <summary>시퀀스와 Weld 페이지의 동시 접근 직렬화. 신규 *Async 메서드에서만 사용.</summary>
private readonly SemaphoreSlim _gate = new(1, 1);
```

#### 8.4.2 신규 메서드

**기존 동기 메서드(`FindPeak`, `CapturePeak`, `ComputeAngle`)는 그대로 둔다.**
Weld 페이지가 사용 중이므로 시그니처를 바꾸면 회귀 위험이 있다.

```csharp
// ── 시퀀스용 비동기 API ──────────────────────────────────────────
// ROI를 인자로 받아 일시 적용하고, 완료 후 원래 ROI를 복원한다.
// 운영자가 Weld 페이지에서 튜닝해 저장한 프로파일을 시퀀스가 덮어쓰지 않도록 한다.

/// <summary>
/// 시퀀스용 Peak 찾기. 지정한 ROI를 일시 적용해 <see cref="FindPeak"/>를 수행하고
/// 원래 ROI를 복원한다. 동시 실행은 <c>_gate</c>로 직렬화된다.
/// </summary>
public async Task<PeakFindResult> FindPeakAsync(RoiRect peakRoi, CancellationToken ct = default)
{
    await _gate.WaitAsync(ct);
    var savedPeak = PeakRoi;
    var savedWeld = WeldRoi;
    try
    {
        PeakRoi = peakRoi;
        return await Task.Run(FindPeak, ct);
    }
    finally
    {
        PeakRoi = savedPeak;
        WeldRoi = savedWeld;
        _gate.Release();
    }
}

/// <summary>
/// 시퀀스용 비드 찾기. 지정한 ROI를 일시 적용해 <see cref="CapturePeak"/>를 수행하고
/// 원래 ROI를 복원한다. 결과 <see cref="PeakMeasurement"/>(M1/M2)와
/// 마지막 검출 결과(<see cref="LastDetect"/>)는 유지되어 UI에서 확인할 수 있다.
/// </summary>
public async Task<(PeakMeasurement? Measurement, WeldDetectionResult? Detect)> CapturePeakAsync(
    int id, RoiRect peakRoi, RoiRect weldRoi, CancellationToken ct = default)
{
    await _gate.WaitAsync(ct);
    var savedPeak = PeakRoi;
    var savedWeld = WeldRoi;
    try
    {
        PeakRoi = peakRoi;
        WeldRoi = weldRoi;
        await Task.Run(() => CapturePeak(id), ct);
        return (id == 1 ? M1 : M2, LastDetect);
    }
    finally
    {
        PeakRoi = savedPeak;
        WeldRoi = savedWeld;
        _gate.Release();
    }
}

/// <summary>시퀀스용 각도 산출. pitch(mm)를 설정한 뒤 <see cref="ComputeAngle"/>를 수행한다.</summary>
public async Task<AngleResult?> ComputeAngleAsync(double pitchMm, CancellationToken ct = default)
{
    await _gate.WaitAsync(ct);
    try
    {
        Pitch = pitchMm;
        await Task.Run(ComputeAngle, ct);
        return Angle;
    }
    finally { _gate.Release(); }
}
```

**구현 주의사항**:

- `PeakRoi`/`WeldRoi`는 현재 `private set`이다. 클래스 내부이므로 직접 대입 가능하다.
- `Task.Run`을 쓰는 이유: 기존 메서드가 동기이고 OpenCV 추론이 수백 ms 걸릴 수 있어
  Blazor 서킷 스레드를 막지 않기 위함이다.
- `CancellationToken`은 `Task.Run` 진입 전까지만 유효하다.
  기존 동기 메서드 내부를 취소할 수는 없다(수 초 이내로 끝나므로 허용).
- `_gate` 획득 실패(타임아웃)는 처리하지 않는다. 취소는 `ct`로 전파된다.
- **ROI 복원**은 반드시 `finally`에서 수행한다.

### 8.5 `PeakFindStep.cs` (⑤⑨)

```csharp
public class PeakFindStep : ISequenceStep
{
    private readonly WeldTrackingService _weld;
    private readonly CameraService _camera;
    private readonly CobotService _cobot;
    private readonly ParameterService _param;
    private readonly ILogger<PeakFindStep> _logger;
    private readonly int _peakId;   // 1 또는 2

    public PeakFindStep(int peakId, WeldTrackingService weld, CameraService camera,
        CobotService cobot, ParameterService param, ILogger<PeakFindStep> logger) { ... }

    public string Key => $"peak{_peakId}Find";
    public string DisplayName => $"Peak{_peakId} 찾기";
    public int DefaultOrder => _peakId == 1 ? 500 : 900;
    ...
}
```

#### `Validate()`

```
1. _cobot.IsConnected            → false면 Fail("코봇 RPC 미연결")
2. _camera.IsConnected           → false면 Fail("카메라 미연결")
3. _camera.LatestIr is null      → Fail("IR 프레임 없음 — IR 스트림 활성 확인")
4. _weld.Params.Mode != Ir       → Fail("IR 모드가 아닙니다 — Weld 페이지에서 IR 모드로 전환")
5. !_weld.DetectorAvailable      → Fail("검출기 비활성 (OpenCV 네이티브 없음 — Windows 전용)")
6. PitchMm <= 0                  → Fail("Weld.Peak.PitchMm 파라미터가 0 이하입니다")
```

> `Validate()`는 **동기 메서드**다. `ParameterService`는 비동기 API만 제공하므로,
> pitch 검사는 `ExecuteAsync` 시작 부분에서 수행하거나,
> 스텝 생성 시 캐시한 값을 사용한다. **권장: `ExecuteAsync` 앞부분에서 검사하고 `StepResult.Fail` 반환.**
> `Validate()`에서는 동기적으로 확인 가능한 항목(1~5)만 검사한다.

#### `ExecuteAsync()`

```
1. pitchMm = await GetPitchMmAsync(param);  pitchMm <= 0 → Fail
2. (roi, roiSrc) = await GetRoiAsync(param, camera);  roi is null → Fail
3. ROI 폭 경고 검사:
     pitchPx = PitchToPixels(pitchMm, <직전 깊이 또는 400>, camera)
     roi.Width > pitchPx 이면 LogWarning
     (깊이를 아직 모르므로 400mm를 가정하거나, 이 검사를 4단계 이후로 미뤄도 무방)
4. r = await weld.FindPeakAsync(roi, ct)
5. !r.Found → Fail($"Peak 미검출 — {r.Message}")
6. (offsetMm, scaleNote) = ResolveOffsetMm(r, weld, camera)
7. peakId == 2 이면 오검출 가드:
     if (Math.Abs(offsetMm) > pitchMm / 3)
         return Fail($"Peak2 이격 {offsetMm:+0.0;-0.0}mm 가 pitch/3({pitchMm/3:0}mm) 초과 — " +
                     "다른 Peak를 잡았을 가능성. PitchDir / PitchMm 확인 필요.");
8. context.Bag[PeakFindBagKey(_peakId)] = r;
   context.Bag[$"peak{_peakId}.offsetMm"] = offsetMm;
9. return Ok($"Peak{_peakId} 찾음 — 이격 {offsetMm:+0.0;-0.0}mm ({r.OffsetPx:+0.0;-0.0}px), " +
             $"깊이 {r.DepthMm}mm, conf {r.Confidence:0.00}, {scaleNote}, ROI={roiSrc}");
```

**메시지 예시**:
```
Peak1 찾음 — 이격 +38.2mm (+152.0px), 깊이 402mm, conf 0.87, 2점보정(×1.024), ROI=저장 ROI
```

### 8.6 `PeakCenteringStep.cs` (⑥⑩)

```csharp
public string Key => $"peak{_peakId}Center";
public string DisplayName => $"Peak{_peakId} 센터링";
public int DefaultOrder => _peakId == 1 ? 600 : 1000;

private const double MaxCenteringMoveMm = 100.0;   // 1회 이동 클램프
```

#### `Validate()`

```
1~5. PeakFindStep과 동일 (코봇/카메라/IR프레임/IR모드/검출기)
6. context.Bag에 PeakFindBagKey(_peakId) 없음
     → Fail($"Peak{_peakId} 측정값 없음 — {(_peakId==1 ? "⑤" : "⑨")} 단계를 먼저 실행하세요.")
```

#### `ExecuteAsync()`

```
 1. Bag에서 initialOffsetMm 읽기
 2. xSign = await GetSignAsync(param, XSignKey)
 3. moveMm = -initialOffsetMm * xSign
 4. |moveMm| < 0.5 → Ok("이미 센터 근방 — 이동 생략") 후 종료
 5. clamped = Math.Clamp(moveMm, -MaxCenteringMoveMm, +MaxCenteringMoveMm)
    clamped != moveMm 이면 LogWarning (클램프 발생)
 6. anchor = await cobot.Rpc.GetTcpPoseInBaseAsync(context.Tool, ct)
 7. offset = new[] { clamped, 0.0, 0.0, 0.0, 0.0, 0.0 }
 8. rc = await cobot.Rpc.MoveByOffsetAsync(anchor, user: 0, offset,
                                            tool: context.Tool, vel: context.Velocity, ct: ct)
    rc != 0 → Fail($"센터링 이동 실패 (rc={rc}){FairinoErrorCodes.Suffix(rc)}")
 9. await Task.Delay(500, ct)                      // 모션 안정화
10. (roi, _) = await GetRoiAsync(param, camera)
11. r2 = await weld.FindPeakAsync(roi, ct)
    !r2.Found → Fail("이동 후 Peak 미검출 — Camera.Axis.XSign 또는 ROI 확인 필요.")
12. (residualMm, _) = ResolveOffsetMm(r2, weld, camera)
13. if (Math.Abs(residualMm) >= Math.Abs(initialOffsetMm))
        return Fail($"이격거리 증가 ({initialOffsetMm:+0.0;-0.0} → {residualMm:+0.0;-0.0}mm) — " +
                    $"Camera.Axis.XSign(현재 {xSign:+0;-0}) 부호를 반대로 설정해 보세요.");
14. context.Bag[PeakFindBagKey(_peakId)] = r2;            // 최신값으로 갱신
    context.Bag[$"peak{_peakId}.offsetMm"] = residualMm;
15. return Ok($"Peak{_peakId} 센터링 완료 — {clamped:+0.0;-0.0}mm 이동, " +
              $"잔차 {residualMm:+0.0;-0.0}mm (이동 전 {initialOffsetMm:+0.0;-0.0}mm)");
```

### 8.7 `BeadFindStep.cs` (⑦⑪)

```csharp
public string Key => $"bead{_peakId}Find";
public string DisplayName => $"Bead{_peakId} 찾기";
public int DefaultOrder => _peakId == 1 ? 700 : 1100;
```

#### `Validate()`

```
1~5. PeakFindStep과 동일
6. context.Bag에 PeakFindBagKey(_peakId) 없음
     → Fail($"Peak{_peakId} 측정값 없음 — 앞 단계를 먼저 실행하세요.")
```

#### `ExecuteAsync()`

```
1. (roi, roiSrc) = await GetRoiAsync(param, camera);  null → Fail
2. (m, detect) = await weld.CapturePeakAsync(_peakId, roi, roi, ct)
3. detect is null || !detect.Success
     → Fail($"Bead{_peakId} 검출 실패 — {detect?.Message ?? "결과 없음"}")
       ※ 원인 구분은 detect.Message가 이미 담고 있음:
         "DL 검출 0건 — conf(0.25)/mask(0.50) 조정 또는 추가 학습 필요."
         "비드 후보 부족(coverage=9%) — ROI/파라미터를 조정하세요."
4. !detect.LineFitOk
     → Fail($"Bead{_peakId} 직선피팅 실패(중앙값 폴백) — 각도 산출 근거 없음. " +
            $"coverage={detect.Confidence:P0}")
5. m is null → Fail("측정 슬롯 저장 실패")
6. 진단 수치 계산:
     dMm        = weld.DMm(m)
     coverage   = detect.Confidence
     extrapPx   = ComputeExtrapolation(detect, m)   // §8.7.1
7. return Ok($"Bead{_peakId} 찾음 — d={dMm:+0.0;-0.0}mm ({detect.DPixel:+0.0;-0.0}px), " +
             $"coverage={coverage:P0}, 외삽 {extrapPx:0}px, ROI={roiSrc}")
```

#### 8.7.1 외삽 거리 계산

비드가 실제로 검출된 구간의 바깥에서 빨간 교점을 읽었다면 그 거리를 기록한다.
각도 왜곡이 관측될 때 원인 판별에 사용한다. **동작을 차단하지는 않는다.**

```csharp
/// <summary>
/// 빨간 교점(WeldPoint)이 비드 검출 구간(Centerline의 진행축 범위) 밖이면
/// 그 초과 거리(px)를 반환. 구간 안이면 0.
/// </summary>
private static double ComputeExtrapolation(WeldDetectionResult r, PeakMeasurement m)
{
    if (r.Centerline.Count == 0 || r.WeldPoint is null) return 0;

    // ProgressAxis == Horizontal 전제 → 진행축은 X
    double first = r.Centerline[0].X;
    double last  = r.Centerline[^1].X;
    double lo = Math.Min(first, last), hi = Math.Max(first, last);
    double t = r.WeldPoint.X;

    if (t < lo) return lo - t;
    if (t > hi) return t - hi;
    return 0;
}
```

> **주의**: 피팅 성공 시 `Centerline`은 정확히 2점(유효 구간의 시작·끝)이다
> (`WeldMaskAnalyzer.cs:124-131`). 피팅 실패 시에는 모든 유효 슬라이스 점이 들어가지만,
> 그 경우는 4단계에서 이미 실패 처리된다.

### 8.8 `PeakApproachStep.cs` (⑧)

```csharp
public string Key => "peak2Approach";
public string DisplayName => "Peak2 이동 (pitch)";
public int DefaultOrder => 800;
```

#### `Validate()`

```
1. _cobot.IsConnected → false면 Fail("코봇 RPC 미연결")
```

> Peak2 이동은 순수 모션이므로 카메라/검출기 조건은 필요 없다.
> pitch 값 검사는 `ExecuteAsync`에서 수행한다.

#### `ExecuteAsync()`

```
1. pitchMm = await GetPitchMmAsync(param);  <= 0 → Fail
2. pitchDir = await GetSignAsync(param, PitchDirKey)
3. moveMm = pitchMm * pitchDir
4. anchor = await cobot.Rpc.GetTcpPoseInBaseAsync(context.Tool, ct)
5. offset = new[] { moveMm, 0.0, 0.0, 0.0, 0.0, 0.0 }
6. rc = await cobot.Rpc.MoveByOffsetAsync(anchor, user: 0, offset,
                                           tool: context.Tool, vel: context.Velocity, ct: ct)
   rc != 0 → Fail($"Peak2 이동 실패 (rc={rc}){FairinoErrorCodes.Suffix(rc)}")
7. await Task.Delay(500, ct)     // 모션 안정화
8. return Ok($"Peak2 위치로 {moveMm:+0.0;-0.0}mm 이동 완료 (pitch={pitchMm:0.#}mm, dir={pitchDir:+0;-0})")
```

### 8.9 `WeldAngleStep.cs` (⑫)

```csharp
public string Key => "weldAngle";
public string DisplayName => "각도 산출";
public int DefaultOrder => 1200;
```

#### `Validate()`

```
1. _weld.M1 is null → Fail("Peak1 측정값 없음 — ⑦ 단계를 먼저 실행하세요.")
2. _weld.M2 is null → Fail("Peak2 측정값 없음 — ⑪ 단계를 먼저 실행하세요.")
3. !_weld.ScaleAvailable → Fail("스케일(fx) 없음 — 해상도/FOV 설정 확인")
```

#### `ExecuteAsync()`

```
1. pitchMm = await GetPitchMmAsync(param);  <= 0 → Fail
2. angle = await weld.ComputeAngleAsync(pitchMm, ct)
3. angle is null → Fail($"각도 산출 실패 — {weld.Message}")
4. 신뢰도 경고 조합:
     warn = ""
     M1/M2의 Confidence(coverage)가 낮으면 (예: < 0.3) "⚠ coverage 낮음" 추가
     (임계값은 공정 검증 전이므로 로그·메시지 표기용으로만 사용, 실패 처리하지 않음)
5. return Ok($"θ = {angle.ThetaDeg:0.00}° " +
             $"(d1={angle.D1:+0.0;-0.0}mm, d2={angle.D2:+0.0;-0.0}mm, pitch={angle.Pitch:0.#}mm){warn}")
```

**메시지 예시**:
```
θ = 1.23° (d1=-4.1mm, d2=+3.8mm, pitch=370.0mm)
```

> **주의**: `WeldTrackingService.Pitch`는 기본값이 0이고 영속화되지 않는다.
> `ComputeAngleAsync(pitchMm)`가 이를 설정하므로 별도 처리는 불필요하다.
> 부수효과로 Weld 페이지의 Pitch 입력란에도 이 값이 반영된다(의도된 동작).
>
> 또한 `CapturePeak()`은 `M1`,`M2`,`Pitch>0`,`ScaleAvailable`이 모두 충족되면
> **자동으로 `ComputeAngle()`을 호출**한다(`WeldTrackingService.cs:278`).
> ⑫를 실행하기 전에 이미 `Angle`이 채워져 있을 수 있으나, ⑫가 pitch를 명시적으로
> 설정하고 재계산하므로 문제되지 않는다.

### 8.10 `Program.cs` DI 등록

기존 등록 블록(`Program.cs:99-104`) 아래에 추가한다.

```csharp
// 시퀀스 단계 등록 (ISequenceStep). 새 단계 추가 시 여기에 한 줄만 추가.
builder.Services.AddScoped<ISequenceStep, AmrMoveStep>();
builder.Services.AddScoped<ISequenceStep, CobotInspectionMoveStep>();
builder.Services.AddScoped<ISequenceStep, CameraAlignStep>();
builder.Services.AddScoped<ISequenceStep, FlatSurfaceAlignStep>();

// ⑤~⑫ Peak/Bead 측정 — peakId 파라미터로 같은 클래스를 두 번 등록한다.
builder.Services.AddScoped<ISequenceStep>(sp =>
    ActivatorUtilities.CreateInstance<PeakFindStep>(sp, 1));        // ⑤ order 500
builder.Services.AddScoped<ISequenceStep>(sp =>
    ActivatorUtilities.CreateInstance<PeakCenteringStep>(sp, 1));   // ⑥ order 600
builder.Services.AddScoped<ISequenceStep>(sp =>
    ActivatorUtilities.CreateInstance<BeadFindStep>(sp, 1));        // ⑦ order 700
builder.Services.AddScoped<ISequenceStep, PeakApproachStep>();      // ⑧ order 800
builder.Services.AddScoped<ISequenceStep>(sp =>
    ActivatorUtilities.CreateInstance<PeakFindStep>(sp, 2));        // ⑨ order 900
builder.Services.AddScoped<ISequenceStep>(sp =>
    ActivatorUtilities.CreateInstance<PeakCenteringStep>(sp, 2));   // ⑩ order 1000
builder.Services.AddScoped<ISequenceStep>(sp =>
    ActivatorUtilities.CreateInstance<BeadFindStep>(sp, 2));        // ⑪ order 1100
builder.Services.AddScoped<ISequenceStep, WeldAngleStep>();         // ⑫ order 1200

builder.Services.AddScoped<SequenceService>();
```

**주의사항**:

- `ActivatorUtilities.CreateInstance<T>(sp, args)`는 **명시 인자를 생성자 앞쪽부터** 매칭한다.
  따라서 `peakId`를 **생성자 첫 번째 파라미터**로 두어야 한다.
- 필요한 `using`: `Microsoft.Extensions.DependencyInjection`(이미 있음),
  `HD_AMR.Service.Sequence.Steps`(기존 등록에서 이미 참조 중).
- 라이프타임 정합성: 스텝은 `Scoped`, `WeldTrackingService`/`CameraService`/`CobotService`는
  `Singleton`, `ParameterService`는 `Scoped`. Scoped가 Singleton을 주입받는 것은 정상이다.

### 8.11 `Sequence.razor` 수정

`StepCircle()`이 ⑩까지만 지원한다(`Sequence.razor:197`). ⑫까지 확장한다.

```csharp
private static string StepCircle(int n) => n switch
{
    1 => "①", 2 => "②", 3 => "③", 4 => "④", 5 => "⑤",
    6 => "⑥", 7 => "⑦", 8 => "⑧", 9 => "⑨", 10 => "⑩",
    11 => "⑪", 12 => "⑫", 13 => "⑬", 14 => "⑭", 15 => "⑮",
    _ => $"({n})"
};
```

> 이 외 `Sequence.razor` 변경은 없다. 스텝 목록·상태·실행 버튼은 모두 자동 렌더링된다.

---

## 9. 실패 처리 정책

### 9.1 원칙

- **재시도 없음.** 1회 실패 시 즉시 `StepResult.Fail`.
- **후속 단계 중단은 엔진이 처리.** `SequenceService.RunAllAsync`가 첫 실패에서 루프를 빠져나간다.
- **로봇은 현재 위치 유지.** 실패 시점에 모션 중이 아니므로 별도 정지 처리 불필요.
- **진단 수치를 메시지에 담는다.** 공정 검증 전이라 임계값을 정할 수 없으므로,
  실제 수치를 남겨 나중에 기준을 정할 수 있게 한다.

### 9.2 Bead 검출 실패 판정

기존 코드에 이미 **2단계 게이트가 직렬로** 존재한다. 시퀀스는 `Success == false`만 보면 된다.

```
1단계  DlWeldVisionDetector.cs:59-68
       DL이 conf 0.25 이상으로 검출 시도 → 0건이면
       Success=false, Message="DL 검출 0건 — conf(0.25)/mask(0.50) 조정 또는 추가 학습 필요."

2단계  WeldMaskAnalyzer.cs:69-75
       마스크는 있으나 validCount < 3 || coverage < 0.15 이면
       Success=false, Message="비드 후보 부족(coverage=X%) — ROI/파라미터를 조정하세요."
       ※ 이 경우에도 오버레이 JPEG는 생성된다(자홍 Peak선 + ROI만, 초록/빨강 없음)
```

**시퀀스가 추가로 판정할 것은 `LineFitOk` 하나뿐이다.**

| 조건 | 처리 |
|---|---|
| `detect.Success == false` | 즉시 실패. 원인은 `detect.Message`가 구분 |
| `detect.LineFitOk == false` | 즉시 실패 — 중앙값 폴백은 각도 산출 근거가 될 수 없음 |
| 그 외 | 성공. coverage·외삽거리를 메시지에 기록 |

> **coverage 임계값을 새로 정하지 않는다.** 0.15는 Weld 페이지에서도 동일하게 적용 중인
> 기존 동작이며, 시퀀스만 다른 기준을 쓰면 두 화면이 따로 놀게 된다.
> 공정 검증 후 실제 coverage 분포를 보고 조정한다.

### 9.3 Peak 검출 실패 판정

| 조건 | 처리 |
|---|---|
| `r.Found == false` | 즉시 실패 |
| (⑨만) `\|offsetMm\| > pitchMm/3` | 즉시 실패 — 다른 Peak를 잡았을 가능성 |
| 그 외 | 성공. confidence를 메시지에 기록 |

> `DepthPeakAnalyzer`는 정상 탐색에 실패하면 **전역 최소 깊이 픽셀로 폴백**하고
> confidence에 0.5배 페널티를 준다(`DepthPeakAnalyzer.cs:96-114`).
> 현재는 이 폴백 결과도 수용한다(confidence 값을 메시지에 남겨 추후 판단).

### 9.4 센터링 실패 판정

| 조건 | 처리 |
|---|---|
| `MoveByOffsetAsync` rc != 0 | 즉시 실패 + `FairinoErrorCodes.Suffix(rc)` |
| 이동 후 Peak 미검출 | 즉시 실패 — XSign 확인 안내 |
| `\|잔차\| >= \|초기 offset\|` | 즉시 실패 — **XSign 부호 반대 안내** |

### 9.5 외삽 (허용)

비드는 물리적으로 판재 **양 끝까지 연속**이며, 검출 실패는 비전의 한계일 뿐이다.
따라서 검출 구간 밖에서 빨간 교점을 읽는 **외삽은 정당하다. 차단하지 않는다.**

단, 외삽 거리를 메시지에 기록해 각도 왜곡 관측 시 원인 판별에 쓸 수 있게 한다.
두 측정(⑦⑪)의 외삽 거리가 크게 다르면 그것이 왜곡의 유력한 원인이다.

---

## 10. 빌드·검증 절차

### 10.1 빌드

솔루션 폴더(`HD_AMR/`)에서 실행한다. (git root 기준 `cd HD_AMR`)

```bash
dotnet build HD_AMR.sln
```

SDK는 `HD_AMR/global.json`이 `8.0.0` / `rollForward: latestMinor`로 고정한다. .NET 8 SDK 8.0.x 필요.

### 10.2 실행

```bash
dotnet run --project HD_AMR.Web            # http://localhost:5253
dotnet run --project HD_AMR.Web --launch-profile https   # https://localhost:7278
```

### 10.3 정적 검증 체크리스트

- [ ] `dotnet build` 경고 0 (nullable 경고 포함)
- [ ] 시퀀스 페이지에 12개 행이 순서대로 표시됨
- [ ] ⑪⑫가 `(11)`, `(12)`가 아니라 `⑪`, `⑫`로 표시됨
- [ ] 코봇/카메라 미연결 상태에서 각 행의 `상태` 열에 올바른 미충족 사유가 표시됨
- [ ] 모든 실행 버튼이 비활성화됨 (선행조건 미충족 시)
- [ ] Weld 페이지를 열었다 닫아도 저장된 ROI 프로파일이 변경되지 않음 (ROI 복원 확인)

### 10.4 기능 검증 (하드웨어 필요)

- [ ] ①~④ 실행 후 ⑤ 실행 → Peak 이격거리가 표시됨
- [ ] Weld 페이지에서 자홍색 Peak 선이 그려진 오버레이 확인
- [ ] ⑥ 실행 → 잔차가 초기값보다 작아짐
- [ ] ⑦ 실행 → 초록 비드선 + 빨간 교점 오버레이 확인, `d1` 표시
- [ ] ⑧ 실행 → 370mm 이동
- [ ] ⑨ 실행 → 이격거리가 pitch/3 이내
- [ ] ⑩⑪ 실행 → `d2` 표시
- [ ] ⑫ 실행 → θ 표시
- [ ] `▶▶ 풀오토` 실행 → ①~⑫ 연속 동작
- [ ] 중간 실패 시 후속 단계가 실행되지 않음
- [ ] `■ 정지` 버튼으로 중단 가능

---

## 11. 브링업 절차

`Camera.Axis.XSign`과 `Weld.Peak.PitchDir`은 **실측으로 확정**해야 한다.
두 파라미터는 서로 간섭하지 않으므로 **순서대로** 진행한다.

### 11.1 사전 준비

1. Weld 페이지에서 검출 모드를 **IR**로 설정
2. Weld 페이지에서 **2점 스케일 보정** 수행 (권장 — 안 하면 intrinsics 폴백)
3. Camera 페이지에서 **Depth ROI를 370mm 이상 폭**으로 설정하고 저장
   (400mm 스탠드오프 기준 프레임 폭의 약 46% 이상)
4. 파라미터 화면에서 다음 값 확인/생성
   - `Weld.Peak.PitchMm` = `370`
   - `Weld.Peak.PitchDir` = `1`
   - `Camera.Axis.XSign` = `1`

### 11.2 1단계 — `Camera.Axis.XSign` 확정

1. ①~④를 실행해 검사 위치·평탄면 정렬 완료
2. **⑤ → ⑥만 반복 실행** (세미오토)
3. 판정:

| ⑥ 결과 | 조치 |
|---|---|
| 성공, 잔차가 초기값보다 **작음** | `XSign` 현재 값이 정답 ✅ |
| 실패, `"이격거리 증가"` 메시지 | `XSign`을 **반대 부호로** 변경 후 재시도 |
| 실패, `"이동 후 Peak 미검출"` | `XSign` 반대로 시도. 그래도 실패면 ROI 폭 확인 |

> 안전장치: 1회 이동이 ±100mm로 클램프되고 반복 루프가 없으므로 폭주하지 않는다.

### 11.3 2단계 — `Weld.Peak.PitchDir` 확정

`XSign` 확정 후 진행한다.

1. ⑤⑥⑦ 실행 (Peak1 측정 완료)
2. **⑧ → ⑨ 실행**
3. 판정:

| ⑨ 결과 | 조치 |
|---|---|
| 성공, 이격거리가 pitch/3 이내 | `PitchDir` 현재 값이 정답 ✅ |
| 실패, `"pitch/3 초과"` 메시지 | `PitchDir`을 **반대 부호로** 변경 후 재시도 |
| 실패, `"Peak 미검출"` | `PitchDir` 반대로 시도. 그래도 실패면 `PitchMm` 실측값 확인 |

### 11.4 3단계 — 전체 검증

1. `▶▶ 풀오토` 실행
2. ⑫의 θ 값과 각 단계 메시지의 진단 수치를 기록
3. 확인 항목:
   - ⑦⑪의 **coverage** 값 → 정상 범위 파악 (추후 임계값 결정 근거)
   - ⑦⑪의 **외삽 거리** → 두 값이 크게 다르면 각도 왜곡 원인
   - ⑤⑨의 **스케일 경로 경고** → 15% 초과 경고가 나오면 2점 보정 재수행

### 11.5 파라미터 변경 방법

XSign·PitchMm·PitchDir 은 **Sequence 페이지의 해당 행(⑥⑧⑩) 파라미터 컬럼**에서 바로 수정하는
것이 가장 빠르다(§6.1 입력 UI 참조). `Parameters` 페이지(`/parameters`)나
SQLite DB(`hd_amr.db`)의 `Parameters` 테이블에서 수정해도 동일하게 반영된다.

---

## 12. 설계 결정 이력

구현 시 이 결정들을 되돌리지 말 것. 각 항목의 근거를 함께 기록한다.

| # | 결정 | 근거 |
|---|---|---|
| 1 | 원래 시나리오의 "⑤ Y축 60mm 이동" **제거** | 레이저와 카메라의 60mm 설치 이격을 보정하려는 의도였으나, ④ 평탄면 센터링의 종료 위치가 가변적이라 고정 오프셋 이동이 무의미 |
| 2 | 클래스 8개가 아닌 **5개** + `peakId` 파라미터화 | ⑤⑨, ⑥⑩, ⑦⑪이 파라미터만 다른 동일 동작. 중복 제거 |
| 3 | mm 환산 **하이브리드** (2점 보정 우선) | `WeldTrackingService`는 간이 FOV intrinsic이지만 **2점 실측 보정**이 있고, `CameraService`는 실제 intrinsics지만 실측 보정이 없음. 실측 보정이 있으면 그쪽이 신뢰도 높음 |
| 4 | **IR 모드 전제**, RGB 모드 차단 | IR(848×480) = Depth(848×480) 해상도 동일 → 좌표 변환 불필요. RGB(1280×720)는 D2C 재투영 필요 |
| 5 | `XSign`과 `PitchDir` **분리** | 카메라 장착 방향(물리)과 측정 방향 선택(설비)은 독립 미지수. 합치면 한쪽을 뒤집을 때 다른 쪽이 발산 |
| 6 | 센터링 **개루프 1회 + 재측정 검증** | 정밀도 불필요(⑦⑪이 Peak를 재측정하므로). 재측정의 목적은 **XSign 오설정 조기 발견** |
| 7 | **단일샷** (다중 샘플링 없음) | 사전 검증에서 단일샷으로도 Peak를 잘 찾았고, Peak 위치의 정밀도 요구가 낮음 |
| 8 | **재시도 없음** | 1회 실패 확정. 재시도는 문제를 가릴 뿐 |
| 9 | **Depth ROI를 Peak/Bead ROI로 그대로 사용** | IR 모드에서 좌표계 동일. 운영자에게 "370mm 이상으로 설정"을 전달하는 것으로 대응 |
| 10 | **외삽 허용** | 비드는 물리적으로 판재 양끝까지 연속. 검출 실패는 비전 한계일 뿐이므로 선을 늘려 읽는 것이 정당 |
| 11 | coverage 임계값 **신설하지 않음** | 공정 검증 전이라 근거 없는 숫자. 기존 0.15 게이트를 그대로 따르고 실측값을 로그로 축적 |
| 12 | ⑫ 각도는 **표시만** | DB 저장·자세 보정은 θ 신뢰도 확인 후 |
| 13 | 시퀀스 페이지에 **썸네일 없음** | ③④도 텍스트 메시지만 반환. 오버레이는 Weld 페이지에서 확인 |
| 14 | `WeldTrackingService` 기존 동기 메서드 **유지** | Weld 페이지가 사용 중. 시그니처 변경은 회귀 위험 |
| 15 | ROI를 **인자로 전달 + finally 복원** | 서비스 상태를 직접 대입하면 운영자가 튜닝해 저장한 프로파일을 덮어씀 |

---

## 13. 보류 항목

이번 구현 범위 밖. 실측 결과를 보고 결정한다.

| 항목 | 조건 | 비고 |
|---|---|---|
| Peak 미검출 시 X축 스캔 복구 | 1차 테스트에서 미검출이 잦으면 | ±pitch/2 범위를 2~3분할 이동·재탐색 |
| 다중 샘플링 (median of N) | 단일샷 성능이 불충분하면 | ③④는 10회/100ms 샘플링 패턴 사용 중 |
| coverage 임계값 상향 | 실측 coverage 분포 확인 후 | 현재 0.15 |
| `DepthPeakAnalyzer` "센터 최근접 우선" 옵션 | ROI 폭 > pitch 상황에서 오검출이 잦으면 | 현재는 최소 깊이(=가장 가까운 Peak) 선택 |
| 각도 DB 저장 | θ 신뢰도 확인 후 | 측정 이력 엔티티 + 마이그레이션 필요 |
| 각도 기반 코봇 자세 보정 (⑬) | θ 신뢰도 확인 후 | 회전축·부호 정의 추가 필요 |
| 시퀀스 페이지 오버레이 썸네일 | 필요해지면 | ③④⑤⑦⑨⑪을 한꺼번에 추가하는 것이 일관적 |
| `PitchCorrectionEnabled` / `PitchCorrectionSign` 정리 | — | appsettings에 있으나 **읽는 코드가 없는 dead config** |
| `GetDepthRoiAsync` 중복 제거 | — | `CameraAlignStep`/`FlatSurfaceAlignStep`이 각자 중복 구현 중. 신규 `WeldSequenceSupport`로 통합 가능 |

---

## 부록 A. 참조 코드 위치 요약

| 대상 | 파일:줄 |
|---|---|
| 시퀀스 스텝 인터페이스 | `HD_AMR/HD_AMR/Service/Sequence/ISequenceStep.cs` |
| 시퀀스 실행 엔진 | `HD_AMR/HD_AMR/Service/Sequence/SequenceService.cs` |
| 시퀀스 UI | `HD_AMR/HD_AMR.Web/Components/Pages/Sequence.razor` |
| `StepCircle()` | `Sequence.razor:197` |
| 기존 스텝 예시(모션) | `Steps/CobotInspectionMoveStep.cs` |
| 기존 스텝 예시(카메라+반복) | `Steps/FlatSurfaceAlignStep.cs` |
| Depth ROI 읽기 패턴 | `Steps/CameraAlignStep.cs:124-140` |
| Peak 탐색 알고리즘 | `Communication/Weld/DepthPeakAnalyzer.cs:26` |
| Peak 폴백(confidence 0.5배) | `Communication/Weld/DepthPeakAnalyzer.cs:96-114` |
| 비드 중심선 + 교점 | `Communication/Weld/WeldMaskAnalyzer.cs:25` |
| coverage 게이트 | `Communication/Weld/WeldMaskAnalyzer.cs:69` |
| 직선 피팅 `fitOk` | `Communication/Weld/WeldMaskAnalyzer.cs:88` |
| 피팅 실패 폴백 | `Communication/Weld/WeldMaskAnalyzer.cs:99-116` |
| 초록선 그리기 | `Communication/Weld/WeldMaskAnalyzer.cs:283` |
| 빨간점 그리기 | `Communication/Weld/WeldMaskAnalyzer.cs:291` |
| 자홍선 그리기 | `Communication/Weld/WeldMaskAnalyzer.cs:301-350` |
| DL 검출 0건 게이트 | `HD_AMR.Web/Services/DlWeldVisionDetector.cs:59-68` |
| `FindPeak()` | `Service/WeldTrackingService.cs:155` |
| `CapturePeak()` | `Service/WeldTrackingService.cs:239` |
| `ComputePeak()` (IR 분기) | `Service/WeldTrackingService.cs:286,291` |
| `ComputeAngle()` | `Service/WeldTrackingService.cs:342` |
| `EffectiveMmPerPixel()` | `Service/WeldTrackingService.cs:469` |
| `Fx()` (간이 intrinsic) | `Service/WeldTrackingService.cs:479` |
| 2점 보정 산출 | `Service/WeldTrackingService.cs:535` |
| `PixelDeltaToMm()` | `Service/CameraService.cs:271` |
| 카메라 해상도 설정 | `Communication/RealSenseSettings.cs:15-29` |
| 코봇 오프셋 이동 | `Communication/FairinoRpcClient.cs:400` |
| DI 등록 (시퀀스) | `HD_AMR.Web/Program.cs:99-104` |
| DI 등록 (Weld) | `HD_AMR.Web/Program.cs:63-83` |

## 부록 B. 용어

| 용어 | 의미 |
|---|---|
| Peak | 코루게이션 판재의 마루. 깊이 최소(카메라에 가장 가까운) 지점 |
| Bead | 용접 비드. 판재를 가로질러 연속으로 이어진 용접선 |
| pitch | 인접 Peak 간 거리. 공칭 370mm |
| 진행축 (progress axis) | 비드가 흐르는 방향. `Horizontal`이면 영상 X축 |
| cross축 | 진행축에 수직인 축. `d` 측정 방향 |
| `d` | 기준선(FOV 센터)으로부터 비드 중심까지의 cross축 거리 |
| coverage | 비드가 검출된 슬라이스 비율 = `validCount / ROI 진행축 픽셀수` |
| 외삽 | 비드가 검출된 구간 **밖**에서 피팅 직선의 값을 읽는 것 |
| 자홍색 선 | Peak 위치를 나타내는 세로선 (magenta, BGR `(255,0,255)`) |
| 초록 선 | 비드 중심선 (BGR `(0,230,0)`) |
| 빨간 점 | 자홍선 ∩ 초록선 교점 = 측정 지점 (BGR `(0,0,255)`) |
| standoff | 카메라와 측정면 사이 거리. ③에서 400mm로 정렬 |
