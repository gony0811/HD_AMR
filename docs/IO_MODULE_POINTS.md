# LS산전 IO 모듈 접점 배치표 (IO Point Map)

조작반(오퍼레이터 패널)의 버튼·램프·부저가 LS산전 IO 모듈의 어느 접점에 배선되어 있는지에 대한 정본.

- **근거**: 2026-09-18 현장 확인 IO 리스트
- **코드 단일 원천**: [`IoPointMap.cs`](../src/HD.AMR.App/Communication/IoPointMap.cs) — 이 문서와 값이 항상 같아야 한다. 배선이 바뀌면 **두 곳을 함께** 갱신할 것.
- **화면**: Web `/io` 페이지와 Desktop `IO Module` 화면이 이 맵을 그대로 라벨로 표시한다(미배선 접점은 "미배선"으로 흐리게).

## 하드웨어 / 주소 요약

| 항목 | 값 |
|---|---|
| 어댑터 | XEL-BSSRT (RAPIEnet+) |
| 입력 모듈 | XBE-DC16A — 16점 |
| 출력 모듈 | XBE-TN16A — 16점 |
| 연결 | Modbus TCP `10.10.100.202:502` (유닛 ID 무시) |
| 입력 읽기 | FC02 Discrete Input @ `0x2020` × 16 (LED 헤더 4바이트 뒤) |
| 출력 쓰기/되읽기 | Holding Register `0x200` 1워드 — FC16 쓰기 / FC03 되읽기, 비트 0..15 = OUT 0..15 |
| 어댑터 LED 헤더 | FC04 Input Register `0x200` × 2워드 |

> ⚠ 출력이 실제로 구동되려면 XG5000에서 어댑터 드라이버 `RAPIEnet v2`를 **Disable** 로 바꿔야 한다
> (LS「통신 디바이스 간편 사용설명서」 §3 Step.3). RAPIEnet 마스터 대기(RNS 점멸) 상태에서는 Modbus 출력 쓰기가 무시된다.

자세한 주소 산출 근거는 [`IoModuleModbusTcpSettings.cs`](../src/HD.AMR.App/Communication/IoModuleModbusTcpSettings.cs) 주석 참조.

## 입력 접점 (XBE-DC16A, FC02 @0x2020)

| 비트 | 현장 표기 | 신호명 | 비고 |
|---:|---|---|---|
| 0 | EMO BUTTON | 비상정지 버튼 | |
| 1 | RESET | 리셋 | |
| 2 | START | 시작 | |
| 3 | AUTO | 자동 | AUTO/MANUAL 은 모드 선택 스위치 |
| 4 | MANUAL | 수동 | |
| 5 | STOP | 정지 | |
| 6~15 | — | 미배선 | 의미 부여 금지 |

## 출력 접점 (XBE-TN16A, Holding 0x200 비트 0..15)

| 비트 | 현장 표기 | 신호명 | 비고 |
|---:|---|---|---|
| 0 | TOWER LAMP RED | 타워램프 적색 | |
| 1 | TOWER LAMP YELLOW | 타워램프 황색 | |
| 2 | TOWER LAMP GREEN | 타워램프 녹색 | |
| 3 | BUZZER | 부저 | |
| 4 | START BUTTON LAMP | 시작 버튼 램프 | |
| 5 | RESET BUTTON LAMP | 리셋 버튼 램프 | |
| 6 | A/M BUTTON LAMP | 자동/수동 버튼 램프 | AUTO/MANUAL 선택 스위치의 램프 (IN 3·4 한 쌍) |
| 7 | STOP BUTTON LAMP | 정지 버튼 램프 | |
| 8 | — | 미배선 | |
| 9 | 가이드 램프 | 가이드 램프 | |
| 10 | 비상정지 (EMO) | 비상정지(EMO) 출력 | |
| 11~15 | — | 미배선 | |

## 버튼 ↔ 램프 쌍

조작반 버튼 4종은 입력(누름 검출)과 출력(램프)이 짝을 이룬다. A/M 은 셀렉터 한 개에 입력 2점(AUTO·MANUAL)·램프 1점이다.

| 버튼 | 입력 | 램프 출력 |
|---|---|---|
| START | IN 2 | OUT 4 |
| RESET | IN 1 | OUT 5 |
| AUTO / MANUAL (A/M) | IN 3 / IN 4 | OUT 6 |
| STOP | IN 5 | OUT 7 |
| EMO | IN 0 | OUT 10 |

## 미확정 사항

1. **OUT 10 "비상정지(EMO)"의 부하** — EMO 버튼 램프인지, 안전회로로 나가는 접점인지 미확인. 구동 전 현장 확인 필요.
2. **인터록 정책** — 어떤 입력 조합에서 어떤 출력(타워램프 색/부저/버튼 램프)을 내보낼지는 아직 시퀀스에 구현되어 있지 않다. 현재는 `/io` 화면의 수동 토글만 제공한다.

## 코드에서 쓰는 법

매직 넘버 대신 `IoPointMap.In` / `IoPointMap.Out` 상수를 사용한다.

```csharp
var state = _ioModule.GetState();
if (state is not null && state.Inputs[IoPointMap.In.EmergencyStop])
{
    // 비상정지 눌림
}

await _ioModule.WriteOutputAsync(IoPointMap.Out.TowerLampRed, true);
```

미배선 접점은 `IoPointMap.Input(i)` / `IoPointMap.Output(i)` 가 `null` 을 돌려준다.
