using System.Globalization;
using System.Text.RegularExpressions;

namespace HD.AMR.App.Communication.GasDetector;

/// <summary>XL3000flex 운전 상태(<c>*STATus?</c> 응답).</summary>
public enum Xl3000Mode
{
    Unknown,
    Standby,
    RunUp,
    Measuring,
    Calibrating,
    EmissionOff,
    Error,
}

/// <summary>
/// INFICON XL3000flex(LDS3000 플랫폼) <b>ASCII 프로토콜</b> — 명령 문자열·응답 파싱만 담는 순수 계층.
/// 근거: INFICON "Interface Description LDS3000" 2장(ASCII Protocol).
///
/// · 명령은 <c>*</c> 로 시작하고 CR(0x0D)로 끝난다. 대소문자 구분 없음, 단어 구분은 콜론.
/// · 응답은 요청 데이터, <c>OK</c>, 또는 <c>Exx</c>(오류 번호) 한 줄.
/// · 권장 타이밍: 샘플 주기 100 ms 이상, 응답 타임아웃 1500 ms, 응답을 받은 뒤에 다음 명령.
/// · IO1000 기본 통신값: 19200 bps, 8N1, 핸드셰이크 없음.
/// </summary>
public static partial class Xl3000AsciiProtocol
{
    public const int DefaultBaudRate = 19200;
    public const int RecommendedTimeoutMs = 1500;
    public const int MinSampleIntervalMs = 100;
    public const char Terminator = '\r';

    /// <summary>리크레이트 — 장비에 설정된 단위(LD 128).</summary>
    public const string ReadLeakRate = "*READ?";
    /// <summary>리크레이트 [mbar·l/s](LD 129) — 참고 표시용. 판정은 SAIGE 임계값 단위(장비 설정 단위, 예: ppm)의 <see cref="ReadLeakRate"/> 로 한다.</summary>
    public const string ReadLeakRateMbar = "*READ:MBAR*l/s?";
    /// <summary>운전 상태 — STBY/ERROR/CAL/ACCL/MEAS/EMI OFF.</summary>
    public const string ReadStatus = "*STATus?";
    /// <summary>트리거 1~4 초과 여부 — "ON"/"OFF" 4개(LD 385).</summary>
    public const string ReadTrigger = "*STATus:TRIGger?";
    /// <summary>현재 에러/경고 — "NO ERROR/WARNING" 또는 번호(LD 290).</summary>
    public const string ReadError = "*STATus:ERRor?";
    /// <summary>스니프 모드 리크레이트 단위(LD 432).</summary>
    public const string ReadUnitSniff = "*CONFig:UNIT:LRSniff?";
    public const string Start = "*STArt";
    public const string Stop = "*STOp";
    public const string Zero = "*ZERO";
    public const string ClearError = "*CLS";

    /// <summary>송신 프레임 = 명령 + CR.</summary>
    public static string Frame(string command) => command + Terminator;

    public static bool IsOk(string response) => response.Trim().Equals("OK", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"^E(\d{2})$", RegexOptions.IgnoreCase)]
    private static partial Regex ErrorRegex();

    /// <summary>응답이 <c>Exx</c> 오류 응답이면 번호를 돌려준다.</summary>
    public static bool TryGetError(string response, out int code)
    {
        var m = ErrorRegex().Match(response.Trim());
        code = m.Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
        return m.Success;
    }

    /// <summary>ASCII 오류 번호 설명(사양 2.6).</summary>
    public static string ErrorMeaning(int code) => code switch
    {
        1 => "명령 시작 문자 오류('*' 없음)",
        2 => "허용되지 않은 공백",
        3 => "명령어 1 오류",
        4 => "명령어 2 오류",
        5 => "명령어 3 오류",
        6 => "RS232 제어 권한 없음(제어 위치 설정 확인)",
        7 => "인자 오류",
        8 => "데이터 없음",
        9 => "에러 버퍼 오버플로",
        10 => "유효하지 않은 명령",
        11 => "조회 불가 명령",
        12 => "조회만 가능한 명령",
        13 => "미구현 명령",
        _ => "알 수 없는 오류",
    };

    [GeneratedRegex(@"^([+-]?\d+(?:\.\d*)?)([+-]\d+)$")]
    private static partial Regex ShortExponentRegex();

    /// <summary>
    /// 숫자 응답 파싱 — <c>2.876E-7</c> 같은 지수 표기와 사양의 축약형 <c>4.5-7</c>(= 4.5E-7)을 모두 받는다.
    /// 소수점은 항상 점이다.
    /// </summary>
    public static bool TryParseNumber(string response, out double value)
    {
        var s = response.Trim();
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            return true;
        var m = ShortExponentRegex().Match(s);
        return m.Success && double.TryParse($"{m.Groups[1].Value}E{m.Groups[2].Value}",
            NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    /// <summary><c>*STATus?</c> 응답 → 운전 상태.</summary>
    public static Xl3000Mode ParseMode(string response)
    {
        var s = response.Trim().ToUpperInvariant();
        if (s.StartsWith("ERR")) return Xl3000Mode.Error;
        if (s.StartsWith("MEAS")) return Xl3000Mode.Measuring;
        if (s.StartsWith("STBY") || s.StartsWith("STANDBY")) return Xl3000Mode.Standby;
        if (s.StartsWith("ACCL") || s.StartsWith("RUNUP")) return Xl3000Mode.RunUp;
        if (s.StartsWith("CAL")) return Xl3000Mode.Calibrating;
        if (s.StartsWith("EMI")) return Xl3000Mode.EmissionOff;
        return Xl3000Mode.Unknown;
    }

    public static string ModeText(Xl3000Mode mode) => mode switch
    {
        Xl3000Mode.Standby => "대기(STBY)",
        Xl3000Mode.RunUp => "기동 중(ACCL)",
        Xl3000Mode.Measuring => "측정 중(MEAS)",
        Xl3000Mode.Calibrating => "교정 중(CAL)",
        Xl3000Mode.EmissionOff => "이미션 OFF",
        Xl3000Mode.Error => "에러",
        _ => "알 수 없음",
    };

    /// <summary><c>*STATus:TRIGger?</c> 응답("ON,OFF,OFF,OFF" 류) → 트리거 1~4 초과 여부. 형식이 다르면 null.</summary>
    public static bool[]? ParseTriggers(string response)
    {
        var states = response
            .Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim().ToUpperInvariant())
            .Where(t => t is "ON" or "OFF" or "1" or "0")
            .Select(t => t is "ON" or "1")
            .ToArray();
        return states.Length == 4 ? states : null;
    }

    /// <summary><c>*STATus:ERRor?</c> 응답이 "에러/경고 없음"인지.</summary>
    public static bool IsNoError(string response)
        => response.Contains("NO ERROR", StringComparison.OrdinalIgnoreCase) || response.Trim() is "0" or "000";

    /// <summary>단위 응답을 화면 표기로. 모르는 값은 원문 그대로.</summary>
    public static string UnitText(string response) => response.Trim().ToUpperInvariant() switch
    {
        "MBAR*L/S" => "mbar·l/s",
        "PA*M3/S" => "Pa·m³/s",
        "ATM*CC/S" => "atm·cc/s",
        "TORR*L/S" => "Torr·l/s",
        "PPM" => "ppm",
        "G/A" => "g/a",
        "OZ/YR" => "oz/yr",
        _ => response.Trim(),
    };

    /// <summary>
    /// 단위 비교용 정규화 — 장비 응답(<c>MBAR*L/S</c>), 화면 표기(<c>mbar·l/s</c>), SAIGE 표기(<c>ppm</c>)를
    /// 같은 키로 맞춘다(소문자, 공백·<c>*</c>·<c>·</c>·<c>³</c> 정리).
    /// </summary>
    public static string NormalizeUnit(string? unit)
        => (unit ?? "").Trim().ToLowerInvariant()
            .Replace(" ", "").Replace("*", "").Replace("·", "").Replace("³", "3");

    /// <summary>두 단위 표기가 같은 단위인지.</summary>
    public static bool SameUnit(string? a, string? b)
        => NormalizeUnit(a) is { Length: > 0 } na && na == NormalizeUnit(b);

    [GeneratedRegex(@"\d+")]
    private static partial Regex FirstNumberRegex();

    /// <summary>
    /// <c>*STATus:ERRor?</c> 응답 → 정수 에러·경고 번호(SAIGE <c>sensor.errorCode</c>). 에러 없음이면 0,
    /// 번호를 찾지 못하면 null. 응답 형식(예: <c>W45</c>, <c>E12</c>, <c>45</c>)은 장비 확인 전이라 첫 숫자를 쓴다.
    /// </summary>
    public static int? ParseErrorCode(string? response)
    {
        if (response is null) return null;
        if (IsNoError(response)) return 0;
        var m = FirstNumberRegex().Match(response);
        return m.Success && int.TryParse(m.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;
    }

    /// <summary>운전 상태 → SAIGE <c>sensor.state</c> 값(측정·대기·교정·예열·에러). 목록은 SAIGE 사양 v0.1 에서 미확정.</summary>
    public static string SensorStateCode(Xl3000Mode mode) => mode switch
    {
        Xl3000Mode.Measuring => "MEASURING",
        Xl3000Mode.Standby => "STANDBY",
        Xl3000Mode.Calibrating => "CALIBRATING",
        Xl3000Mode.RunUp => "WARMUP",
        Xl3000Mode.Error => "ERROR",
        Xl3000Mode.EmissionOff => "EMISSION_OFF",
        _ => "UNKNOWN",
    };
}
