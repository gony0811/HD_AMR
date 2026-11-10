using System.Buffers.Binary;
using HD.AMR.App.Models;

namespace HD.AMR.App.Communication.Vision;

/// <summary>
/// CAPTURE_REQ 의 34바이트 DATA 페이로드 생성 [v3.2]. 레이아웃 (사양: vision_interface_v3.2):
///   [0]     Surface Type  UInt8
///   [1-2]   Wall ID       UInt16 LE (구 Surface ID — 명칭만 변경, 값 1~10 불변)
///   [3-6]   PosX          Int32 LE  면-로컬 u (mm)
///   [7-10]  PosY          Int32 LE  면-로컬 v (mm)
///   [11-14] PosZ          Int32 LE  면-로컬 h (mm, 표면 높이) — 구 예약 4바이트 자리
///   [15-30] taskId        GUID 16바이트 (바이너리)
///   [31]    attempt       UInt8     시도 번호(1부터) — ACS 발급
///   [32-33] captureSeq    UInt16 LE 시도 내 촬영 번호(1부터) — 로봇 발번
/// 전체 프레임 = 9(고정 오버헤드) + 34 = 43바이트.
/// taskId 는 ACS 가 검사 작업(용접선 1구간)에 발급한 영구 식별자로, 비전 S/W 가 이를
/// SAIGE 메타데이터의 productId 로 그대로 사용해 검사 이력을 누적한다.
/// </summary>
public static class CaptureReqPayload
{
    public const int SurfaceTypeOff = 0;                    // [0]
    public const int WallIdOff      = 1;                    // [1-2]
    public const int PosXOff        = 3;                    // [3-6]
    public const int PosYOff        = 7;                    // [7-10]
    public const int PosZOff        = 11;                   // [11-14]
    public const int TaskIdOff      = 15;                   // [15-30]
    public const int AttemptOff     = 31;                   // [31]
    public const int CaptureSeqOff  = 32;                   // [32-33]
    public const int Length         = 34;

    /// <summary>v3.2: 면·위치(u,v,h)·taskId·attempt·captureSeq 를 34바이트로 직렬화.</summary>
    public static byte[] Build(
        SurfaceType type, ushort wallId,
        int posX, int posY, int posZ,
        Guid taskId, byte attempt, ushort captureSeq)
    {
        var data = new byte[Length];
        data[SurfaceTypeOff] = (byte)type;
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(WallIdOff, 2), wallId);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(PosXOff, 4), posX);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(PosYOff, 4), posY);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(PosZOff, 4), posZ);
        GuidBinary.Write(data.AsSpan(TaskIdOff, 16), taskId);
        data[AttemptOff] = attempt;
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(CaptureSeqOff, 2), captureSeq);
        return data;
    }

    /// <summary>디코드된 CAPTURE_REQ(34B) 필드 추출. 길이 부족 시 null.</summary>
    public static CaptureReqFields? TryRead(ReadOnlySpan<byte> data)
    {
        if (data.Length < Length) return null;
        return new CaptureReqFields(
            (SurfaceType)data[SurfaceTypeOff],
            BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(WallIdOff, 2)),
            BinaryPrimitives.ReadInt32LittleEndian(data.Slice(PosXOff, 4)),
            BinaryPrimitives.ReadInt32LittleEndian(data.Slice(PosYOff, 4)),
            BinaryPrimitives.ReadInt32LittleEndian(data.Slice(PosZOff, 4)),
            GuidBinary.Read(data.Slice(TaskIdOff, 16)),
            data[AttemptOff],
            BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(CaptureSeqOff, 2)));
    }
}

/// <summary>CAPTURE_REQ 디코드 결과(로그·수신 파싱용).</summary>
public readonly record struct CaptureReqFields(
    SurfaceType Type, ushort WallId,
    int PosX, int PosY, int PosZ,
    Guid TaskId, byte Attempt, ushort CaptureSeq);

/// <summary>
/// CAPTURE_RES / ERROR_NOTI 의 2바이트 DATA 페이로드 [v3.2]. 레이아웃:
///   [0-1] 결과/오류 코드 (UInt16 LE)
/// v3.2 에서 Run/Task ID 에코가 폐지되어 결과 코드만 싣는다. 응답 상관은 단일 보류 슬롯 방식.
/// </summary>
public static class CaptureResPayload
{
    public const int CodeOff = 0;                           // [0-1]
    public const int Length  = 2;

    public static byte[] Build(ResultCode code)
    {
        var data = new byte[Length];
        BinaryPrimitives.WriteUInt16LittleEndian(data, (ushort)code);
        return data;
    }

    /// <summary>결과 코드 추출. v3.2(2B)는 [0-1]. 레거시 v3(74B: Run/Task 에코 뒤 [72])도 관대하게 읽음.</summary>
    public static bool TryReadCode(ReadOnlySpan<byte> data, out ushort code)
    {
        if (data.Length == Length) { code = BinaryPrimitives.ReadUInt16LittleEndian(data); return true; }
        if (data.Length >= 74)     { code = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(72, 2)); return true; } // 레거시 74B
        if (data.Length >= 2)      { code = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(0, 2)); return true; }
        code = 0; return false;
    }
}

/// <summary>
/// GUID ↔ 16바이트 바이너리 변환. RFC 4122 표준(빅엔디안) 바이트 순서를 사용해, 하이픈 문자열을
/// 왼쪽부터 읽은 순서와 바이트 배열이 일치한다(수신 측 문자열 변환이 모호하지 않음).
/// ※ GUID 바이트 순서는 연동 시험 시 비전 S/W 와 최종 확인 대상.
/// </summary>
internal static class GuidBinary
{
    public static void Write(Span<byte> dst, Guid id) => id.TryWriteBytes(dst, bigEndian: true, out _);
    public static Guid Read(ReadOnlySpan<byte> src)    => new(src[..16], bigEndian: true);
}

/// <summary>
/// HD현대 비전 인터페이스 프로토콜 상수/코드 정의. 사양: docs/vision_interface_v3.2.
/// 프레임 = STX(1) | LENGTH(2) | SEQ(1) | COMMAND(1) | FROM(1) | TO(1) | DATA(n) | CHECKSUM(1) | ETX(1).
/// </summary>
public static class FrameConst
{
    public const byte Stx = 0x02;
    public const byte Etx = 0x03;
    public const int MaxDataLength = 256;
    // STX(1) + LENGTH(2) + SEQ(1) + COMMAND(1) + FROM(1) + TO(1) + CHECKSUM(1) + ETX(1) = 9.
    // total frame bytes = FixedOverhead + DATA_len.
    public const int FixedOverhead = 9;
}

public enum CommandCode : byte
{
    Heartbeat  = 0x01,
    CaptureReq = 0x02,
    CaptureRes = 0x03,
    ErrorNoti  = 0x04,
}

/// <summary>송수신자 ID. 자동화 S/W = 0x01(TCP 클라이언트), 비전 S/W = 0x02(TCP 서버).</summary>
public enum DeviceId : byte
{
    Automation = 0x01,
    Vision     = 0x02,
}

/// <summary>
/// CAPTURE_REQ [0] Surface Type — 비전 측 사양 표 기준 9종(0~1 자동용접부, 2~ 수동용접부).
/// 비전 S/W 는 이 값으로 저장 폴더(Metadata1~9)를 고른다. 이름·라벨·폴더는 <see cref="SurfaceTypes"/> 표에서 관리.
/// ※ 구 번호(0=Flat, 1=Corner, 2=Corrugation)로 저장된 값은 <see cref="SurfaceTypes.FromLegacy"/> 로 변환.
/// ※ 사양 주석상 4번 이후 순서는 "예시" — 확정 시 번호만 이 enum 과 표에서 갱신.
/// </summary>
public enum SurfaceType : byte
{
    Flat                         = 0x00, // 자동용접부 Flat
    Corrugation                  = 0x01, // 자동용접부 Corrugation
    Manual3Axis                  = 0x02, // 3 axis (수)
    Manual4Axis                  = 0x03, // 4 axis (수)
    Manual90Flat                 = 0x04, // 90도 Flat (수)
    Manual90Corrugation          = 0x05, // 90도 Corrugation (수)
    Manual135Flat                = 0x06, // 135도 Flat (수)
    ManualEndMembraneFlat        = 0x07, // End Membrane Flat (수)
    ManualEndMembraneCorrugation = 0x08, // End Membrane Corrugation (수)
}

/// <summary>Surface Type 표 행. Label=UI 표시, MetadataDir=비전 측 저장 폴더명, IsManual=수동용접부 여부.</summary>
public sealed record SurfaceTypeInfo(SurfaceType Type, string Label, string MetadataDir, bool IsManual);

/// <summary>Surface Type 정본 표 — UI 드롭다운·로그·CSV 주석·검증이 모두 이 표를 참조한다.</summary>
public static class SurfaceTypes
{
    public static readonly IReadOnlyList<SurfaceTypeInfo> All =
    [
        new(SurfaceType.Flat,                         "자동 Flat",                     "Metadata1", false),
        new(SurfaceType.Corrugation,                  "자동 Corrugation",              "Metadata2", false),
        new(SurfaceType.Manual3Axis,                  "수동 3축",                      "Metadata3", true),
        new(SurfaceType.Manual4Axis,                  "수동 4축",                      "Metadata4", true),
        new(SurfaceType.Manual90Flat,                 "수동 90° Flat",                 "Metadata5", true),
        new(SurfaceType.Manual90Corrugation,          "수동 90° Corrugation",          "Metadata6", true),
        new(SurfaceType.Manual135Flat,                "수동 135° Flat",                "Metadata7", true),
        new(SurfaceType.ManualEndMembraneFlat,        "수동 End Membrane Flat",        "Metadata8", true),
        new(SurfaceType.ManualEndMembraneCorrugation, "수동 End Membrane Corrugation", "Metadata9", true),
    ];

    public const byte MaxValue = (byte)SurfaceType.ManualEndMembraneCorrugation;

    public static SurfaceTypeInfo? Find(byte value) => All.FirstOrDefault(t => (byte)t.Type == value);

    public static bool IsDefined(int value) => value is >= 0 and <= 255 && Find((byte)value) is not null;

    /// <summary>표시 라벨. 미정의 값은 0xNN.</summary>
    public static string Label(byte value) => Find(value)?.Label ?? $"0x{value:X2}";

    public static string Label(SurfaceType type) => Label((byte)type);

    /// <summary>"N=라벨" 을 구분자로 이어 붙인 범례(CSV 주석·도움말용).</summary>
    public static string Legend(string separator = ", ") =>
        string.Join(separator, All.Select(t => $"{(byte)t.Type}={t.Label}"));

    /// <summary>임의 정수를 SurfaceType 으로 — 미정의 값은 Flat.</summary>
    public static SurfaceType OrFlat(int value) => IsDefined(value) ? (SurfaceType)value : SurfaceType.Flat;

    public static bool IsManual(this SurfaceType type) => Find((byte)type)?.IsManual ?? false;

    /// <summary>경유점 CSV 의 신 번호 체계 표식 주석행. 이 행이 없는 CSV 는 구 번호로 보고 <see cref="FromLegacy"/> 변환.</summary>
    public const string CsvSchemaMarker = "# surface-type-schema: 2";

    /// <summary>CSV 텍스트가 신 번호 체계(표식 행 포함)인지.</summary>
    public static bool IsCurrentSchemaCsv(string text) =>
        text.Contains(CsvSchemaMarker, StringComparison.OrdinalIgnoreCase);

    /// <summary>CSV Surface 셀 해석 — 신 체계면 0~8 그대로(미정의는 Flat), 구 체계면 <see cref="FromLegacy"/>.</summary>
    public static byte ParseCsvCell(string cell, bool currentSchema)
    {
        if (!byte.TryParse(cell.Trim(), out var s)) return (byte)SurfaceType.Flat;
        if (!currentSchema) return FromLegacy(s);
        return IsDefined(s) ? s : (byte)SurfaceType.Flat;
    }

    /// <summary>구 번호 체계(0=Flat, 1=Corner, 2=Corrugation) → 신 번호. Corner 는 사양에서 빠져 Flat 으로 흡수.</summary>
    public static byte FromLegacy(byte legacy) => legacy switch
    {
        0x01 => (byte)SurfaceType.Flat,         // 구 Corner
        0x02 => (byte)SurfaceType.Corrugation,  // 구 Corrugation
        _    => (byte)SurfaceType.Flat,
    };
}

public enum ResultCode : ushort
{
    Success      = 0x0000,
    ErrTimeout   = 0x0001,
    ErrCamera    = 0x0002,
    ErrPosition  = 0x0003,
    ErrSurface   = 0x0004,
    ErrBusy      = 0x0005,
    ErrUnknown   = 0x00FF,
}

/// <summary>면(Wall) 정보. Id=Wall ID(1~10), Code=ACS 문자 코드, Axes=v3.2 §5 U/V 축 방향.</summary>
public sealed record SurfaceInfo(ushort Id, string Code, string Name, SurfaceType Type, string Axes);


/// <summary>사양 시트 5 "Surface ID 정의". ID 0x01~0x0A, 전부 Flat.
/// 검사 면 정본 표 <see cref="WallCodes"/> 에서 생성한다 — ACS wall_code·Teaching Wall ID 와 같은 번호를 보장.</summary>
public static class SurfaceCatalog
{
    public static readonly IReadOnlyList<SurfaceInfo> All = WallCodes.All
        .Select(w => new SurfaceInfo((ushort)w.SurfaceId, w.Code, w.DisplayName, SurfaceType.Flat, w.Axes))
        .ToArray();

    public static string NameOf(ushort id) =>
        All.FirstOrDefault(s => s.Id == id)?.Name ?? $"Unknown(0x{id:X4})";

    public static string CodeOf(ushort id) =>
        All.FirstOrDefault(s => s.Id == id)?.Code ?? $"0x{id:X2}";
}

public static class ResultCodeNames
{
    public static string NameOf(ushort code) => code switch
    {
        0x0000 => "SUCCESS",
        0x0001 => "ERR_TIMEOUT",
        0x0002 => "ERR_CAMERA",
        0x0003 => "ERR_POSITION",
        0x0004 => "ERR_SURFACE",
        0x0005 => "ERR_BUSY",
        0x00FF => "ERR_UNKNOWN",
        _      => $"Unknown(0x{code:X4})",
    };
}

public static class CommandNames
{
    public static string NameOf(byte cmd) => cmd switch
    {
        0x01 => "HEARTBEAT",
        0x02 => "CAPTURE_REQ",
        0x03 => "CAPTURE_RES",
        0x04 => "ERROR_NOTI",
        _    => $"Unknown(0x{cmd:X2})",
    };
}

public static class DeviceIdNames
{
    public static string NameOf(byte id) => id switch
    {
        0x01 => "자동화",
        0x02 => "비전",
        _    => $"Unknown(0x{id:X2})",
    };
}
