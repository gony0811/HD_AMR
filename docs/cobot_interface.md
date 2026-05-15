-- DI (PC → 로봇)
--   DI0 : 검사 시작 (pStart 이동)
--   DI1 : 스텝 이동 (AI 오프셋 누적)
--   DI2 : 구간 완료
--   DI3 : 리셋
-- DO (로봇 → PC)
--   DO0 : Busy (동작 중)
--   DO1 : Done (완료)
--   DO2 : At Start (pStart 도달)
--   DO3 : At Wait (대기위치 도달)
--   DO4 : Error
-- AI (PC → 로봇, signed short, 단위: mm)
--   AI0~2 : dx, dy, dz
--   AI3~5 : drx, dry, drz

[PC]                          [로봇]
대기위치 (pWait)
DI_START ON ──────────▶
→ DoStart(): pStart로 이동, accum=0
◀────── DO_DONE ON
DI_START OFF ─────────▶ DO_DONE OFF

(반복) ↓
AI0~5 = 첫 증분 쓰기
DI_STEP ON ──────────▶
→ DoStep(): AI 읽기 → 누적 → 이동
◀────── DO_DONE ON
(PC: 비전 검사)
DI_STEP OFF ──────────▶ DO_DONE OFF

(다음 증분 보내고 반복)
↑

마지막 스텝 후:
DI_COMPLETE ON ───────▶
→ DoComplete(): 안전 상승 + pWait 복귀
◀────── DO_DONE+DO_AT_WAIT ON
DI_COMPLETE OFF ──────▶ DO_DONE OFF
