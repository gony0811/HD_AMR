namespace HD.AMR.App.Communication;

/// <summary>
/// LS산전 IO 모듈 접점 1점의 설비 신호 정의.
/// </summary>
/// <param name="Name">설비 신호명(한국어 표기).</param>
/// <param name="Code">현장 IO 리스트의 영문 표기 — 배선표·PLC 심볼과 대조용.</param>
public sealed record IoPoint(string Name, string Code);

/// <summary>
/// LS산전 IO 모듈(XBE-DC16A 입력 16점 / XBE-TN16A 출력 16점) 접점 ↔ 조작반 신호 맵.
/// 근거: 2026-09-18 현장 확인 IO 리스트(docs/IO_MODULE_POINTS.md 에 표로 기록).
///
/// 인덱스는 <see cref="IoModuleState.Inputs"/>/<see cref="IoModuleState.Outputs"/> 의 비트 인덱스와 동일하다
/// (입력 = FC02 <see cref="IoModuleModbusTcpSettings.InputStart"/> 부터, 출력 = Holding
/// <see cref="IoModuleModbusTcpSettings.OutputWriteAddress"/> 워드의 비트 0..15).
///
/// 미배선 접점은 null — UI/로직에서 "미배선"으로 취급하고 의미를 부여하지 말 것.
/// 배선이 바뀌면 이 파일과 docs/IO_MODULE_POINTS.md 를 함께 갱신한다(단일 원천).
/// </summary>
public static class IoPointMap
{
    /// <summary>이 맵의 근거 — UI 각주에 그대로 노출한다.</summary>
    public const string SourceNote = "2026-09-18 현장 확인 IO 리스트 기준";

    /// <summary>입력 접점 인덱스 상수 — 시퀀스/인터록 코드에서 매직 넘버 대신 사용.</summary>
    public static class In
    {
        /// <summary>비상정지 버튼(EMO BUTTON)</summary>
        public const int EmergencyStop = 0;

        /// <summary>리셋 버튼(RESET)</summary>
        public const int Reset = 1;

        /// <summary>시작 버튼(START)</summary>
        public const int Start = 2;

        /// <summary>자동 모드 선택(AUTO)</summary>
        public const int Auto = 3;

        /// <summary>수동 모드 선택(MANUAL)</summary>
        public const int Manual = 4;

        /// <summary>정지 버튼(STOP)</summary>
        public const int Stop = 5;
    }

    /// <summary>출력 접점 인덱스 상수 — 램프/부저 제어 코드에서 매직 넘버 대신 사용.</summary>
    public static class Out
    {
        /// <summary>타워램프 적색</summary>
        public const int TowerLampRed = 0;

        /// <summary>타워램프 황색</summary>
        public const int TowerLampYellow = 1;

        /// <summary>타워램프 녹색</summary>
        public const int TowerLampGreen = 2;

        /// <summary>부저</summary>
        public const int Buzzer = 3;

        /// <summary>시작 버튼 램프</summary>
        public const int StartButtonLamp = 4;

        /// <summary>리셋 버튼 램프</summary>
        public const int ResetButtonLamp = 5;

        /// <summary>자동/수동(A/M) 선택 버튼 램프</summary>
        public const int AutoManualButtonLamp = 6;

        /// <summary>정지 버튼 램프</summary>
        public const int StopButtonLamp = 7;

        /// <summary>가이드 램프</summary>
        public const int GuideLamp = 9;

        /// <summary>비상정지(EMO) 출력</summary>
        public const int EmergencyStop = 10;
    }

    // null = 미배선.
    private static readonly IoPoint?[] InputPoints =
    {
        /* 00 */ new("비상정지 버튼", "EMO BUTTON"),
        /* 01 */ new("리셋", "RESET"),
        /* 02 */ new("시작", "START"),
        /* 03 */ new("자동", "AUTO"),
        /* 04 */ new("수동", "MANUAL"),
        /* 05 */ new("정지", "STOP"),
        /* 06 */ null,
        /* 07 */ null,
        /* 08 */ null,
        /* 09 */ null,
        /* 10 */ null,
        /* 11 */ null,
        /* 12 */ null,
        /* 13 */ null,
        /* 14 */ null,
        /* 15 */ null,
    };

    private static readonly IoPoint?[] OutputPoints =
    {
        /* 00 */ new("타워램프 적색", "TOWER LAMP RED"),
        /* 01 */ new("타워램프 황색", "TOWER LAMP YELLOW"),
        /* 02 */ new("타워램프 녹색", "TOWER LAMP GREEN"),
        /* 03 */ new("부저", "BUZZER"),
        /* 04 */ new("시작 버튼 램프", "START BUTTON LAMP"),
        /* 05 */ new("리셋 버튼 램프", "RESET BUTTON LAMP"),
        /* 06 */ new("자동/수동 버튼 램프", "A/M BUTTON LAMP"),
        /* 07 */ new("정지 버튼 램프", "STOP BUTTON LAMP"),
        /* 08 */ null,
        /* 09 */ new("가이드 램프", "GUIDE LAMP"),
        /* 10 */ new("비상정지(EMO)", "EMO"),
        /* 11 */ null,
        /* 12 */ null,
        /* 13 */ null,
        /* 14 */ null,
        /* 15 */ null,
    };

    /// <summary>입력 접점 정의 — 미배선이면 null.</summary>
    public static IoPoint? Input(int index) =>
        index >= 0 && index < InputPoints.Length ? InputPoints[index] : null;

    /// <summary>출력 접점 정의 — 미배선이면 null.</summary>
    public static IoPoint? Output(int index) =>
        index >= 0 && index < OutputPoints.Length ? OutputPoints[index] : null;

    /// <summary>화면 표시용 입력 라벨("IN 00 · 시작" 형태의 뒷부분).</summary>
    public static string InputLabel(int index) => Input(index)?.Name ?? "미배선";

    /// <summary>화면 표시용 출력 라벨.</summary>
    public static string OutputLabel(int index) => Output(index)?.Name ?? "미배선";
}
