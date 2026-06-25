# AMR 배터리 상태 표시 / 배터리 시각화

AMR의 Modbus 배터리 잔량·연결 상태를 상단바(TopBar)와 대시보드(Home)에 실시간 표시하고,
배터리를 "채워지는 배터리 모양 + 충전 번개 + 잔량별 색상"으로 시각화하는 기능.

## 데이터 흐름

```
AMR (Modbus TCP, 502)
   │  Input Register 0~64 벌크 읽기 (1초 주기)
   ▼
AMRService  (싱글톤 + BackgroundService)
   │  ExecuteAsync 루프: 연결 보장 → ReadStatusAsync() → LatestStatus 캐싱
   │  · 정상: 1초 간격
   │  · 실패/미연결: LatestStatus = null, 5초 백오프
   ▼
LatestStatus (RobotStatus?)  ← 단일 진실 공급원
   ▼
TopBar / Home / Amr 컴포넌트 (1초 PeriodicTimer 로 StateHasChanged)
   · IsConnected, LatestStatus.Battery 만 읽음 (직접 Modbus 호출 안 함)
```

- `ModbusTcpClient`은 모든 I/O를 `SemaphoreSlim(1,1)`로 직렬화하므로 백그라운드 폴링과 컴포넌트 폴링이 겹쳐도 소켓 손상 없음.
- 상태 읽기는 `AMRService`가 단독으로 수행(단일 폴러). 컴포넌트는 캐시(`LatestStatus`)만 소비.

## Modbus 배터리 레지스터 (Input Register 50~54)

| 주소 | 항목 | 변환식 | 비고 |
|------|------|--------|------|
| 50 | 배터리 잔량 | `raw / 10000 * 100` | 0~100 % |
| 51 | 전압 | `raw / 100` | V |
| 52 | 전류 | `raw / 100` | A |
| 53 | 온도 | `raw / 100` | °C |
| 54 | 충전 상태 | enum | `Charging=1`, `Discharging=2` |

매핑 위치: `HD_AMR/Service/AMRService.cs` `ReadStatusAsync()`,
모델: `HD_AMR/Models/BatteryStatus.cs`, 열거형: `HD_AMR/Enums/ChargingState.cs`.

## 표시 규칙

### 잔량별 색상 임계값

| 잔량 | 색상 | 값 |
|------|------|-----|
| ≥ 50 % | 초록 | `#22c55e` |
| 20 % ~ 50 % | 황색 | `#f59e0b` (--warn) |
| < 20 % | 빨강 | `#ef4444` (--danger) |
| 미연결(null) | 회색 | `#d1d5db` |

### 충전 표시

- `ChargingState.Charging` 이고 잔량이 있을 때 배터리 내부에 **번개(⚡)** 표시.
- `Discharging` / 미연결이면 번개 없음.

### 연결 상태

- AMR 연결 여부는 `AMRService.IsConnected`(TCP 소켓 상태).
- TopBar AMR 배지, Home AMR 카드 배지: 연결 시 "연결", 아니면 "미연결".

## 변경 / 신규 파일

| 파일 | 내용 |
|------|------|
| `HD_AMR/Service/AMRService.cs` | `LatestStatus` 속성 추가, `ExecuteAsync` 루프에서 1초 주기 상태 폴링·캐싱 |
| `HD_AMR.Web/Components/Shared/BatteryIndicator.razor` (+ `.razor.css`) | **신규** 공용 컴포넌트. 인라인 SVG 배터리(잔량 채움 + 색상 + 충전 번개). 파라미터: `Percent(int?)`, `Charging(bool)`, `HeightRem(double)` |
| `HD_AMR.Web/Components/Layout/TopBar.razor` (+ `.razor.css`) | AMR 배지를 `IsConnected` 기반 연결/미연결로, 배터리 영역을 `BatteryIndicator`로 교체. 하드코딩 빨강 아이콘/텍스트 제거 |
| `HD_AMR.Web/Components/Pages/Home.razor` (+ `.razor.css`) | 배터리 카드의 가로 게이지 막대를 `BatteryIndicator`로 교체, 색상 임계값 갱신, 충전 시 부제 "충전 중" |
| `HD_AMR.Web/Components/Pages/Amr.razor` | 자체 `ReadStatusAsync` 중복 폴링 제거 → `Svc.LatestStatus` 캐시 사용 |
| `HD_AMR.Web/Components/_Imports.razor` | `@using HD_AMR.Web.Components.Shared` 추가 |

## BatteryIndicator 사용 예

```razor
@* TopBar: 작게 *@
<BatteryIndicator Percent="BatteryPercent" Charging="IsCharging" HeightRem="1.35" />

@* Home 대시보드: 크게 *@
<BatteryIndicator Percent="BatteryPercent" Charging="IsCharging" HeightRem="2.4" />
```

- `BatteryPercent` = `Amr.LatestStatus is { } s ? (int)Math.Round(s.Battery.LevelPercent) : null`
- `IsCharging` = `Amr.LatestStatus?.Battery.ChargingState == ChargingState.Charging`

## 검증

1. 빌드: `cd HD_AMR && dotnet build HD_AMR.sln` (오류 0개).
2. 실행: `dotnet run --project HD_AMR.Web` → http://localhost:5253
3. AMR(또는 Modbus 시뮬레이터, `appsettings.json`의 `Amr` 섹션) 연결 후:
   - 70 % → 초록·약 70 % 채움 / 35 % → 황색 / 15 % → 빨강.
   - `ChargingState` 충전(1) → 배터리 안에 번개, 방전(2) → 번개 없음.
   - 연결 해제 → 회색 빈 배터리 + `--%`, AMR 배지 "미연결".
