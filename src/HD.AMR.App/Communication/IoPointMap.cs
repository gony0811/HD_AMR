namespace HD.AMR.App.Communication;

/// <summary>
/// LS산전 IO 모듈 접점 1점의 설비 신호 정의.
/// </summary>
/// <param name="Name">설비 신호명(한국어 표기).</param>
/// <param name="Code">현장 IO 리스트의 영문 표기 — 배선표·PLC 심볼과 대조용.</param>
public sealed record IoPoint(string Name, string Code);

/// <summary>
/// 조작반 배선 한 벌 — 장비 모델마다 버튼·램프가 붙은 접점이 다르다. 인덱스는 비트 번호, −1 = 그 모델에 없는 신호.
/// </summary>
public sealed class IoPanelLayout
{
    public required string Name { get; init; }

    /// <summary>이 배치의 근거 — UI 각주에 그대로 노출한다.</summary>
    public required string SourceNote { get; init; }

    // 입력
    public int InEmergencyStop { get; init; } = 0;
    public int InReset { get; init; } = 1;
    public int InStart { get; init; } = 2;
    public required int InAuto { get; init; }
    public int InManual { get; init; } = 4;
    public required int InStop { get; init; }

    // 출력
    public int OutTowerLampRed { get; init; } = 0;
    public int OutTowerLampYellow { get; init; } = 1;
    public int OutTowerLampGreen { get; init; } = 2;
    public int OutBuzzer { get; init; } = 3;
    public required int OutStartButtonLamp { get; init; }
    public required int OutResetButtonLamp { get; init; }
    public required int OutAutoManualButtonLamp { get; init; }
    public required int OutStopButtonLamp { get; init; }
    public int OutGuideLamp { get; init; } = 9;

    /// <summary>비상정지(EMO) 출력. −1 = 없음.</summary>
    public required int OutEmergencyStop { get; init; }

    /// <summary>전원 램프. −1 = 없음.</summary>
    public required int OutPowerLamp { get; init; }

    /// <summary>입력 접점 정의(16점) — 미배선은 null.</summary>
    public IoPoint?[] BuildInputs() => Build(
        (InEmergencyStop, new IoPoint("비상정지 버튼", "EMO BUTTON")),
        (InReset, new IoPoint("리셋", "RESET")),
        (InStart, new IoPoint("시작", "START")),
        (InAuto, new IoPoint("자동", "AUTO")),
        (InManual, new IoPoint("수동", "MANUAL")),
        (InStop, new IoPoint("정지", "STOP")));

    /// <summary>출력 접점 정의(16점) — 미배선은 null.</summary>
    public IoPoint?[] BuildOutputs() => Build(
        (OutTowerLampRed, new IoPoint("타워램프 적색", "TOWER LAMP RED")),
        (OutTowerLampYellow, new IoPoint("타워램프 황색", "TOWER LAMP YELLOW")),
        (OutTowerLampGreen, new IoPoint("타워램프 녹색", "TOWER LAMP GREEN")),
        (OutBuzzer, new IoPoint("부저", "BUZZER")),
        (OutStartButtonLamp, new IoPoint("시작 버튼 램프", "START BUTTON LAMP")),
        (OutResetButtonLamp, new IoPoint("리셋 버튼 램프", "RESET BUTTON LAMP")),
        (OutAutoManualButtonLamp, new IoPoint("자동/수동 버튼 램프", "A/M BUTTON LAMP")),
        (OutStopButtonLamp, new IoPoint("정지 버튼 램프", "STOP BUTTON LAMP")),
        (OutGuideLamp, new IoPoint("가이드 램프", "GUIDE LAMP")),
        (OutEmergencyStop, new IoPoint("비상정지(EMO)", "EMO")),
        (OutPowerLamp, new IoPoint("전원 램프", "POWER LAMP")));

    private static IoPoint?[] Build(params (int Index, IoPoint Point)[] points)
    {
        var arr = new IoPoint?[16];
        foreach (var (i, p) in points)
        {
            if (i < 0) continue;
            if (arr[i] is { } taken)
                throw new InvalidOperationException($"IO 접점 {i} 중복 배정: {taken.Code} / {p.Code}");
            arr[i] = p;
        }
        return arr;
    }

    /// <summary>용접검사 AMR(비전) — 2026-09-18 현장 확인 IO 리스트.</summary>
    public static readonly IoPanelLayout WeldInspection = new()
    {
        Name = "용접검사 AMR",
        SourceNote = "2026-09-18 현장 확인 IO 리스트 기준",
        InAuto = 3, InStop = 5,
        OutStartButtonLamp = 4, OutResetButtonLamp = 5, OutAutoManualButtonLamp = 6, OutStopButtonLamp = 7,
        OutEmergencyStop = 10, OutPowerLamp = -1,
    };

    /// <summary>가스검사 AMR — 2026-11-22 현장 확인. 용접검사 AMR 과 다른 점: IN03 STOP·IN05 AUTO(서로 바뀜),
    /// OUT04 RESET·OUT05 START·OUT06 STOP·OUT07 A/M 램프, OUT10 은 EMO 출력이 아니라 전원 램프.
    /// 전장에서 OUT10 을 EMO 로 다시 배선하면 OutEmergencyStop = 10, OutPowerLamp = -1 로 바꾼다(용접검사 AMR 과 동일).</summary>
    public static readonly IoPanelLayout GasDetector = new()
    {
        Name = "가스검사 AMR",
        SourceNote = "2026-11-22 가스검사 AMR 현장 확인 기준",
        InAuto = 5, InStop = 3,
        OutStartButtonLamp = 5, OutResetButtonLamp = 4, OutAutoManualButtonLamp = 7, OutStopButtonLamp = 6,
        OutEmergencyStop = -1, OutPowerLamp = 10,
    };
}

/// <summary>
/// LS산전 IO 모듈(XBE-DC16A 입력 16점 / XBE-TN16A 출력 16점) 접점 ↔ 조작반 신호 맵.
/// 장비 모델별 배치는 <see cref="IoPanelLayout"/> 에 있고, 기동 때 <see cref="Select"/> 로 고른다
/// (appsettings InspectionSensor:Mode — GasDetector 면 가스검사 AMR 배치). 고르지 않으면 용접검사 AMR 배치.
/// 근거·표는 docs/IO_MODULE_POINTS.md.
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
    private static IoPanelLayout _layout = IoPanelLayout.WeldInspection;
    private static IoPoint?[] _inputs = IoPanelLayout.WeldInspection.BuildInputs();
    private static IoPoint?[] _outputs = IoPanelLayout.WeldInspection.BuildOutputs();

    /// <summary>지금 쓰는 조작반 배치.</summary>
    public static IoPanelLayout Layout => _layout;

    /// <summary>조작반 배치 선택 — 서비스가 뜨기 전(기동 구성 단계)에 한 번 부른다.</summary>
    public static void Select(IoPanelLayout layout)
    {
        _inputs = layout.BuildInputs();
        _outputs = layout.BuildOutputs();
        _layout = layout;
    }

    /// <summary>이 맵의 근거 — UI 각주에 그대로 노출한다.</summary>
    public static string SourceNote => _layout.SourceNote;

    /// <summary>입력 접점 인덱스 — 시퀀스/인터록 코드에서 매직 넘버 대신 사용.</summary>
    public static class In
    {
        /// <summary>비상정지 버튼(EMO BUTTON)</summary>
        public static int EmergencyStop => _layout.InEmergencyStop;

        /// <summary>리셋 버튼(RESET)</summary>
        public static int Reset => _layout.InReset;

        /// <summary>시작 버튼(START)</summary>
        public static int Start => _layout.InStart;

        /// <summary>자동 모드 선택(AUTO)</summary>
        public static int Auto => _layout.InAuto;

        /// <summary>수동 모드 선택(MANUAL)</summary>
        public static int Manual => _layout.InManual;

        /// <summary>정지 버튼(STOP)</summary>
        public static int Stop => _layout.InStop;
    }

    /// <summary>출력 접점 인덱스 — 램프/부저 제어 코드에서 매직 넘버 대신 사용.</summary>
    public static class Out
    {
        /// <summary>타워램프 적색</summary>
        public static int TowerLampRed => _layout.OutTowerLampRed;

        /// <summary>타워램프 황색</summary>
        public static int TowerLampYellow => _layout.OutTowerLampYellow;

        /// <summary>타워램프 녹색</summary>
        public static int TowerLampGreen => _layout.OutTowerLampGreen;

        /// <summary>부저</summary>
        public static int Buzzer => _layout.OutBuzzer;

        /// <summary>시작 버튼 램프</summary>
        public static int StartButtonLamp => _layout.OutStartButtonLamp;

        /// <summary>리셋 버튼 램프</summary>
        public static int ResetButtonLamp => _layout.OutResetButtonLamp;

        /// <summary>자동/수동(A/M) 선택 버튼 램프</summary>
        public static int AutoManualButtonLamp => _layout.OutAutoManualButtonLamp;

        /// <summary>정지 버튼 램프</summary>
        public static int StopButtonLamp => _layout.OutStopButtonLamp;

        /// <summary>가이드 램프</summary>
        public static int GuideLamp => _layout.OutGuideLamp;

        /// <summary>비상정지(EMO) 출력. −1 = 이 모델에 없음(가스검사 AMR, 재배선 전) — 쓰기 전에 0 이상인지 확인할 것.</summary>
        public static int EmergencyStop => _layout.OutEmergencyStop;

        /// <summary>전원 램프. −1 = 이 모델에 없음(용접검사 AMR).</summary>
        public static int PowerLamp => _layout.OutPowerLamp;
    }

    /// <summary>입력 접점 정의 — 미배선이면 null.</summary>
    public static IoPoint? Input(int index) =>
        index >= 0 && index < _inputs.Length ? _inputs[index] : null;

    /// <summary>출력 접점 정의 — 미배선이면 null.</summary>
    public static IoPoint? Output(int index) =>
        index >= 0 && index < _outputs.Length ? _outputs[index] : null;

    /// <summary>화면 표시용 입력 라벨("IN 00 · 시작" 형태의 뒷부분).</summary>
    public static string InputLabel(int index) => Input(index)?.Name ?? "미배선";

    /// <summary>화면 표시용 출력 라벨.</summary>
    public static string OutputLabel(int index) => Output(index)?.Name ?? "미배선";
}
