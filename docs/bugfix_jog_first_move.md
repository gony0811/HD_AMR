# 버그 수정 기록 — 조그 첫 이동이 엉뚱한 위치로 가는 문제

날짜: 2026-06-25
대상: 코봇(FAIRINO) 조그/이동 (`/cobot` 페이지)

## 증상
- 조그(BASE 좌표계)에서 **첫 명령(예: +X 10mm)이 10mm가 아니라 엉뚱한 위치로 이동**.
- **두 번째 명령부터는** 정상적으로 지정한 거리만큼 이동.

## 근본 원인 — 활성 공구(active tool) 프레임 불일치
조그 한 번의 동작 흐름:
1. `BaseJog`이 앵커(현재) 포즈를 `GetTcpPoseInBaseAsync` → **`GetForwardKin`**(정기구학)으로 계산한다.
   `GetForwardKin`은 **컨트롤러의 현재 활성 공구** 프레임 기준 포즈를 돌려준다.
2. 실제 이동 `MoveByOffsetAsync` → `MoveL`은 **`tool=_jogTool`(UI 기본값 1)** 로 전송된다.

문제는 연결 시점에 활성 공구를 설정하지 않았다는 것이다.
- `FairinoRpcSettings.DefaultToolId`는 `0`이었고 UI는 공구 `1`을 사용 → 불일치.
- `CobotService` 연결 루틴은 서보만 켜고 활성 공구를 지정하지 않았다.

그 결과:
- **첫 조그**: FK 앵커(활성 공구, tool 1 아님)와 이동(tool 1)의 공구 프레임이 어긋나
  앵커가 실제 위치와 달라져 엉뚱한 곳으로 이동.
- 그런데 그 **첫 `MoveL(tool=1)`이 컨트롤러의 활성 공구를 1로 동기화**시킨다.
- **두 번째부터**: FK 앵커도 tool 1 프레임이 되어 이동 공구와 일치 → 정상.

즉, "첫 명령만 빗나가고 이후 정상"이라는 증상과 정확히 일치한다.

> 참고: 로봇은 실제 TCP가 설정된 **공구 1** 로 조그/이동한다(사용자 확인). 공구 1의 TCP 오프셋만큼
> 앵커가 틀어져 첫 이동이 빗나갔다.

## 수정 내용
핵심 아이디어: **FK로 앵커를 읽기 전에, 그 이동에 사용할 공구로 활성 공구를 먼저 맞춘다.**
이렇게 하면 첫 명령부터 FK 프레임과 `MoveL`의 공구가 항상 일치한다(UI에서 공구를 바꾼 직후의
첫 조그도 안전).

### `HD_AMR/HD_AMR/Communication/FairinoRpcClient.cs`
- 활성 공구 추적 필드 `private int _activeTool = -1;` 추가(`-1` = 미상). 연결 시 `-1`로 초기화,
  해제(`Disconnect`) 시에도 `-1`.
- 신규 메서드 `EnsureActiveToolAsync(int tool, ct)`:
  - `tool == _activeTool`이면 no-op(중복 호출 방지).
  - 다르면 해당 공구 좌표를 `GetToolCoordAsync`로 읽어 **같은 값으로 `SetToolCoordAsync`**(round-trip)
    호출 → 좌표값 변경 없이 그 공구를 현재(활성) 공구로 선택하고 `_activeTool` 갱신.
- `GetTcpPoseInBaseAsync`에 `int tool` 파라미터 추가 — FK 호출 전에 `EnsureActiveToolAsync(tool)` 실행.
- `MoveLAsync`는 성공(rc=0) 후 `_activeTool = t`로 갱신 — `MoveL`의 `tool` 인자가 컨트롤러
  활성 공구를 바꾸는 부작용과 추적값을 일치시킨다.

### `HD_AMR/HD_AMR.Web/Components/Pages/Cobot.razor`
`GetTcpPoseInBaseAsync` 호출부 4곳이 "이동에 쓸 공구"를 전달하도록 변경:
- `BaseJog` → `_jogTool`
- 오프셋 증분 이동(`MoveByOffset`, current 모드) → `_offTool`
- `UseCurrentPose` → `_moveTool`
- `CapturePoint`(표시용) → `_jogTool`

### `HD_AMR/HD_AMR/Communication/FairinoRpcSettings.cs`
- `DefaultToolId` 기본값 `0` → `1` (실제 TCP가 설정된 공구, UI 기본값과 일치).

### `HD_AMR/HD_AMR/Service/CobotService.cs`
- 연결 직후 `SetServoEnableAsync(true)` 다음에 `EnsureActiveToolAsync(DefaultToolId)` 호출 —
  첫 동작 전에 활성 공구를 확정한다.

## 검증 방법
1. 빌드: `cd HD_AMR && dotnet build HD_AMR.sln` (오류 0 확인).
2. 앱 실행 후 코봇 연결. **연결 직후 첫 동작으로** 조그 +X 10mm 실행 → 정확히 10mm 이동 확인.
3. 같은 축으로 연속 조그가 계속 정상인지 확인.
4. UI에서 공구 번호를 바꾼 뒤 첫 조그도 정상인지 확인(구조적 수정 검증).
5. 로그에 `활성 공구 #1로 동기화`가 찍히는지 확인.

## 남은 확인 사항 (실물)
- FAIRINO에서 `SetToolCoord`(round-trip)가 실제로 **활성 공구를 전환**하는지 실물에서 확인 필요.
  RPC 계층 시그니처가 SDK 기준 "추정"이므로, 전환되지 않으면 펌웨어 전용 공구 선택 RPC를
  `IFairinoRpc`에 추가해 `EnsureActiveToolAsync` 내부 호출을 교체한다.

## 후속 (2026-07-01) — 활성 공구 전환 방식 폐기, 클라이언트 재프레임으로 교체
위 수정은 활성 공구를 **무변위 MoveJ**(현재 관절각 + tool 인자)로 전환했는데, 실물에서 이 MoveJ가
`rc=154`로 거부되어(변위 0 궤적 거부 + MoveJ가 enable/자동 모드를 요구 — 단순 포즈 조회엔 부적절)
`현재 포즈 조회 실패: 활성 공구 #1 전환(MoveJ) 실패 (rc=154)`로 나타났다.

해결: **포즈 조회가 모션을 전혀 보내지 않도록** `GetTcpPoseInBaseAsync`를 재작성했다. 활성 공구를 물리적으로
바꾸는 대신, 이동 공구 T의 TCP 포즈를 공구 오프셋으로 클라이언트에서 합성한다:
`P_T = P_active ∘ inv(offset_active) ∘ offset_T` (`offset_k` = `GetToolCoord(k)`, 공구 0 = identity).
- `EnsureActiveToolAsync`는 **삭제**됐고, 연결 흐름(`CobotService`)도 더는 전환 모션을 보내지 않는다.
- 변환 수학은 신규 `Communication/PoseMath.cs`(ZYX RPY 도 규약, `ComputeFramePose`와 동일)에 있다.
- 이 문서의 SetToolCoord/MoveJ 전환 방식은 위 재프레임으로 **대체**되었다. 오일러 규약은 여전히
  실물 대조(감독된 MoveJ 후 GetForwardKin 비교) 검증이 필요하다.
