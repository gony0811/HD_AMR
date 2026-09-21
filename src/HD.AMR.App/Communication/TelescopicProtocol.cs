namespace HD.AMR.App.Communication;

/// <summary>
/// EBLUM 3채널 홀 동기 컨트롤러(Z축 텔레스코픽) RS232/TTL 프로토콜 — <b>순수 문자열 계층</b>.
/// 시리얼 포트를 건드리지 않으므로 전 플랫폼에서 단위 테스트가 된다(실제 I/O 는 <see cref="TelescopicClient"/>).
///
/// 근거: <c>docs/RS232TTL Communication Protocol (Three-Channel Hall Synchronous Controller).xlsx</c>.
/// 9600 8N1 무패리티, <b>TTL 레벨</b>(RS232 로 쓰려면 TTL↔RS232 변환 모듈 필요).
/// RJ45 8핀: 1=CAN_L, 2=CAN_H, 3=RX1, 4=TX1, 5=RX4, 6=TX4, 7=5V, 8=GND — 외부 기기와 TX/RX 교차 결선.
///
/// 전부 <b>문답식</b>이고 모든 명령은 <c>\r\n</c> 으로 끝난다. 컨트롤러는 상태를 자동 통보하지 않으므로
/// <see cref="Read"/> 로 폴링해야 한다(사양서 FAQ 4).
/// </summary>
public static class TelescopicProtocol
{
    /// <summary>사양서 1장: 9600 8N1, 패리티 없음.</summary>
    public const int BaudRate = 9600;

    /// <summary>명령 종결자 — <c>0x0D 0x0A</c>.</summary>
    public const string Terminator = "\r\n";

    /// <summary>메모리 위치 <b>이동</b> 시 명령 유지 시간(ms). 사양서 4장.</summary>
    public const int MemoryMoveHoldMs = 50;

    /// <summary>메모리 위치 <b>저장</b> 시 명령 유지 시간(ms) — 2초 초과 유지 후 0. 사양서 Bit2~4.</summary>
    public const int MemorySaveHoldMs = 2000;

    /// <summary>리셋 명령 유지 시간(ms) — 2초 초과 유지 후 0. 사양서 Bit5.</summary>
    public const int ResetHoldMs = 2000;

    /// <summary>
    /// <c>Handle:</c> 제어 명령 비트. 사양서는 3바이트 <b>10진 문자열</b>을 보내고 컨트롤러가 이를
    /// 8비트 값으로 해석한다("015" → 15 → 0x0F). 따라서 값은 비트 OR 로 조합한다.
    /// </summary>
    [Flags]
    public enum HandleBits
    {
        /// <summary>정지(모든 비트 0).</summary>
        None = 0,

        /// <summary>Bit0 — 상승.</summary>
        Up = 1 << 0,

        /// <summary>Bit1 — 하강.</summary>
        Down = 1 << 1,

        /// <summary>Bit2 — 메모리 위치 1.</summary>
        Memory1 = 1 << 2,

        /// <summary>Bit3 — 메모리 위치 2.</summary>
        Memory2 = 1 << 3,

        /// <summary>Bit4 — 메모리 위치 3.</summary>
        Memory3 = 1 << 4,

        /// <summary>Bit5 — 추진기 모터 리셋.</summary>
        Reset = 1 << 5,
    }

    /// <summary>컨트롤러 운행 모드(응답 B 필드).</summary>
    public enum RunMode
    {
        Stopped = 0,
        MovingUp = 1,
        MovingDown = 2,
        Resetting = 3,
        MemoryMove = 4,
        ObstacleRetreat = 5,
    }

    // ── 명령 생성 ───────────────────────────────────────────────────
    /// <summary>제어 명령 문자열(종결자 제외). 예: <c>Handle:001</c>.</summary>
    public static string Handle(HandleBits bits) => $"Handle:{(int)bits:D3}";

    /// <summary>정지 — <c>Handle:000</c>. 상승/하강은 반드시 이것으로 멈춘다.</summary>
    public static string Stop() => Handle(HandleBits.None);

    /// <summary>상승 — <c>Handle:001</c>.</summary>
    public static string Up() => Handle(HandleBits.Up);

    /// <summary>하강 — <c>Handle:002</c>.</summary>
    public static string Down() => Handle(HandleBits.Down);

    /// <summary>리셋 — <c>Handle:032</c>. 2초 이상 유지한 뒤 <see cref="Stop"/>.</summary>
    public static string Reset() => Handle(HandleBits.Reset);

    /// <summary>메모리 슬롯(1~3) 명령 — 004/008/016. 이동은 50ms, 저장은 2s 유지 후 정지.</summary>
    public static string Memory(int slot) => Handle(MemoryBit(slot));

    /// <summary>메모리 슬롯(1~3)의 비트.</summary>
    public static HandleBits MemoryBit(int slot) => slot switch
    {
        1 => HandleBits.Memory1,
        2 => HandleBits.Memory2,
        3 => HandleBits.Memory3,
        _ => throw new ArgumentOutOfRangeException(nameof(slot), slot, "메모리 슬롯은 1~3 이어야 합니다."),
    };

    /// <summary>
    /// 지정 높이 이동 — <c>Target:NNN</c>. 사양서는 높이 데이터를 <b>3바이트</b>로 규정하므로
    /// 지령 가능 범위가 0~999 다(응답 높이는 4바이트인데 지령은 3바이트인 비대칭 — 벤더 확인 필요,
    /// <c>docs/TELESCOPIC_LIFT.md</c> 참조). <paramref name="digits"/> 로 자릿수를 넓힐 수 있게 열어 둔다.
    /// </summary>
    public static string Target(int heightMm, int digits = 3)
    {
        if (digits < 1 || digits > 6)
            throw new ArgumentOutOfRangeException(nameof(digits), digits, "자릿수는 1~6 이어야 합니다.");
        int max = (int)Math.Pow(10, digits) - 1;
        if (heightMm < 0 || heightMm > max)
            throw new ArgumentOutOfRangeException(nameof(heightMm), heightMm,
                $"지정 높이는 0~{max} mm 여야 합니다(사양서 높이 데이터 {digits}바이트).");
        return "Target:" + heightMm.ToString(new string('0', digits));
    }

    /// <summary>실시간 상태 조회 — <c>Read</c>. 컨트롤러는 자동 통보하지 않는다.</summary>
    public static string Read() => "Read";

    /// <summary>에러 코드 강제 클리어 — <c>ClearErr</c>.</summary>
    public static string ClearErr() => "ClearErr";

    // ── 응답 파싱 ───────────────────────────────────────────────────
    private const string StatusPrefix = "Length:";

    /// <summary>
    /// <c>Length:</c> 응답 파싱. 페이로드는
    /// 활성(1)+운행모드(1)+잠금(1)+단위(1)+소리(1)+파라미터플래그(1)+파라미터모드(1)+높이깜빡임(1)
    /// +에러코드(2)+높이(4) = 14자다.
    ///
    /// 길이가 14가 아니면 <b>앞 4자 + 뒤 6자</b>로 축약 파싱한다(펌웨어 변종 내성 — 중간 필드는
    /// 사양서가 "처리 불필요"로 둔 값이다). 겹침을 막기 위해 최소 10자를 요구한다.
    /// 형식이 아니면 null.
    /// </summary>
    public static TelescopicStatus? ParseStatus(string? response)
    {
        if (string.IsNullOrWhiteSpace(response)) return null;

        var line = response.Trim();
        if (!line.StartsWith(StatusPrefix, StringComparison.OrdinalIgnoreCase)) return null;

        var data = line[StatusPrefix.Length..].Trim();
        if (data.Length < 10) return null;

        int Digit(int i) => i >= 0 && i < data.Length && char.IsAsciiDigit(data[i]) ? data[i] - '0' : -1;

        // 에러·높이는 뒤에서 센다 — 중간 필드 개수가 달라도 어긋나지 않는다.
        if (!int.TryParse(data[^6..^4], out var error)) error = -1;
        if (!int.TryParse(data[^4..], out var height)) height = -1;

        bool exact = data.Length == 14;

        return new TelescopicStatus(
            Active: Digit(0) == 1,
            Mode: (RunMode)Digit(1),
            Unlocked: Digit(2) == 1,
            Imperial: Digit(3) == 1,
            SoundEnabled: exact ? Digit(4) == 1 : null,
            ParameterFlag: exact ? Digit(5) : null,
            ParameterMode: exact ? Digit(6) : null,
            HeightBlinking: exact ? Digit(7) == 1 : null,
            ErrorCode: error,
            HeightMm: height,
            ExactLayout: exact,
            Raw: line);
    }

    /// <summary>
    /// <c>Target:</c> 응답 파싱. 실행 결과 0=실행 가능, 1=실행 불가.
    /// 반환 true=수락, false=거부, null=형식 불명.
    /// </summary>
    public static bool? ParseTargetAck(string? response)
    {
        if (string.IsNullOrWhiteSpace(response)) return null;
        var line = response.Trim();
        if (!line.StartsWith("Target:", StringComparison.OrdinalIgnoreCase)) return null;
        var body = line["Target:".Length..].Trim();
        if (body.Length == 0 || !char.IsAsciiDigit(body[0])) return null;
        return body[0] == '0';
    }

    /// <summary><c>ClearErr OK</c> 응답 확인.</summary>
    public static bool ParseClearErrAck(string? response)
        => response is not null && response.Contains("OK", StringComparison.OrdinalIgnoreCase);

    /// <summary>에러 코드(0~16) 한국어 설명. 사양서 3장.</summary>
    public static string ErrorName(int code) => code switch
    {
        0 => "정상",
        1 => "장애물 감지 후퇴",
        2 => "과전압 알람",
        3 => "저전압 알람",
        4 => "기울기 알람",
        5 => "통신 중단 알람",
        6 => "모터 A 과전류",
        7 => "모터 B 과전류",
        8 => "모터 C 과전류",
        9 => "홀 A 신호 없음",
        10 => "홀 B 신호 없음",
        11 => "홀 C 신호 없음",
        12 => "모터 과열",
        13 => "전원 과전류",
        14 => "자세 유지 시간 초과",
        15 => "컨트롤러 미등록",
        16 => "모터 하강(슬립) 알람",
        _ => $"알 수 없는 에러({code})",
    };

    /// <summary>운행 모드 한국어 이름.</summary>
    public static string ModeName(RunMode mode) => mode switch
    {
        RunMode.Stopped => "정지",
        RunMode.MovingUp => "상승",
        RunMode.MovingDown => "하강",
        RunMode.Resetting => "리셋 중",
        RunMode.MemoryMove => "메모리 이동",
        RunMode.ObstacleRetreat => "장애물 후퇴",
        _ => $"알 수 없음({(int)mode})",
    };
}

/// <summary>
/// 텔레스코픽 컨트롤러 <c>Read</c> 응답 스냅샷.
/// </summary>
/// <param name="Active">활성 여부(0=슬립, 1=활성).</param>
/// <param name="Mode">운행 모드.</param>
/// <param name="Unlocked">잠금 해제 여부(0=잠김, 1=해제). 잠김이면 지정 높이 이동이 거부된다.</param>
/// <param name="Imperial">높이 단위 — false=공제(mm), true=인치.</param>
/// <param name="SoundEnabled">소리 사용(사양서 "처리 불필요"). 축약 파싱 시 null.</param>
/// <param name="ParameterFlag">파라미터 설정 플래그. 축약 파싱 시 null.</param>
/// <param name="ParameterMode">파라미터 설정 모드. 축약 파싱 시 null.</param>
/// <param name="HeightBlinking">높이 표시 깜빡임. 축약 파싱 시 null.</param>
/// <param name="ErrorCode">에러 코드 0~16(0=정상). 파싱 실패 시 −1.</param>
/// <param name="HeightMm">현재 추진기 높이. 단위는 <paramref name="Imperial"/> 에 따른다. 실패 시 −1.</param>
/// <param name="ExactLayout">사양서 14자 배치와 정확히 일치했는지(false = 축약 파싱).</param>
/// <param name="Raw">원본 응답.</param>
public sealed record TelescopicStatus(
    bool Active,
    TelescopicProtocol.RunMode Mode,
    bool Unlocked,
    bool Imperial,
    bool? SoundEnabled,
    int? ParameterFlag,
    int? ParameterMode,
    bool? HeightBlinking,
    int ErrorCode,
    int HeightMm,
    bool ExactLayout,
    string Raw)
{
    /// <summary>에러 없음.</summary>
    public bool IsHealthy => ErrorCode == 0;

    /// <summary>움직이는 중(정지·장애물후퇴 제외).</summary>
    public bool IsMoving => Mode is TelescopicProtocol.RunMode.MovingUp
                                 or TelescopicProtocol.RunMode.MovingDown
                                 or TelescopicProtocol.RunMode.Resetting
                                 or TelescopicProtocol.RunMode.MemoryMove;

    public string ModeText => TelescopicProtocol.ModeName(Mode);
    public string ErrorText => TelescopicProtocol.ErrorName(ErrorCode);
    public string UnitText => Imperial ? "inch" : "mm";
}
