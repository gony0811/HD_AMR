using System.Buffers.Binary;
using HD.AMR.App.Models;

namespace HD.AMR.App.Communication.Vision;

/// <summary>
/// CAPTURE_REQ 의 115바이트 DATA 페이로드 생성 [v3]. 레이아웃:
///   [0-35]  Run ID   (ASCII 36, ACS 실행 GUID 정규형)
///   [36-71] Task ID  (ASCII 36, ACS 검사 TASK GUID 정규형)
///   [72-103]Job Ref  (ASCII 32, null 0x00 패딩)
///   [104]   Surface Type
///   [105-106]Surface ID (UInt16 LE)
///   [107-110]PosX (Int32 LE, mm)
///   [111-114]PosY (Int32 LE, mm)
/// Run/Task ID 는 ACS 상관 식별자로, 비전 S/W 가 이를 결과에 에코하면 엔터프라이즈가
/// 검사 결과를 ACS TASK 진행현황(inspection_result / progress)과 매칭할 수 있다.
/// </summary>
public static class CaptureReqPayload
{
    public const int RunIdLen  = 36;
    public const int TaskIdLen = 36;
    public const int JobRefLen = 32;
    public const int RunIdOff       = 0;
    public const int TaskIdOff      = RunIdOff + RunIdLen;        // 36
    public const int JobRefOff      = TaskIdOff + TaskIdLen;      // 72
    public const int SurfaceTypeOff = JobRefOff + JobRefLen;      // 104
    public const int SurfaceIdOff   = SurfaceTypeOff + 1;         // 105
    public const int PosXOff        = SurfaceIdOff + 2;           // 107
    public const int PosYOff        = PosXOff + 4;                // 111
    public const int Length         = PosYOff + 4;               // 115

    /// <summary>v3: ACS 상관 식별자(Run ID·Task ID·Job Ref) 포함.</summary>
    public static byte[] Build(Guid runId, Guid taskId, string? jobRef,
        SurfaceType type, ushort surfaceId, int posX, int posY)
    {
        var data = new byte[Length];
        GuidAscii.Write(data.AsSpan(RunIdOff, RunIdLen), runId);
        GuidAscii.Write(data.AsSpan(TaskIdOff, TaskIdLen), taskId);
        AsciiField.Write(data.AsSpan(JobRefOff, JobRefLen), jobRef);
        data[SurfaceTypeOff] = (byte)type;
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(SurfaceIdOff, 2), surfaceId);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(PosXOff, 4), posX);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(PosYOff, 4), posY);
        return data;
    }

    /// <summary>상관 식별자 없이(수동 테스트·비ACS 경로) 생성 — Run/Task ID = 0 GUID, Job Ref = 공란.</summary>
    public static byte[] Build(SurfaceType type, ushort surfaceId, int posX, int posY)
        => Build(Guid.Empty, Guid.Empty, null, type, surfaceId, posX, posY);
}

/// <summary>
/// CAPTURE_RES / ERROR_NOTI 의 74바이트 DATA 페이로드 [v3]. 레이아웃:
///   [0-35]  Run ID  (ASCII 36, 요청 값 에코)
///   [36-71] Task ID (ASCII 36, 요청 값 에코)
///   [72-73] 결과/오류 코드 (UInt16 LE)
/// </summary>
public static class CaptureResPayload
{
    public const int RunIdOff  = 0;
    public const int TaskIdOff = 36;
    public const int CodeOff   = 72;
    public const int Length    = 74;

    public static byte[] Build(Guid runId, Guid taskId, ResultCode code)
    {
        var data = new byte[Length];
        GuidAscii.Write(data.AsSpan(RunIdOff, 36), runId);
        GuidAscii.Write(data.AsSpan(TaskIdOff, 36), taskId);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(CodeOff, 2), (ushort)code);
        return data;
    }

    /// <summary>결과 코드 추출. v3(≥74B)는 [72], v2(정확히 2B) 레거시는 [0]. 부족하면 false.</summary>
    public static bool TryReadCode(ReadOnlySpan<byte> data, out ushort code)
    {
        if (data.Length >= Length) { code = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(CodeOff, 2)); return true; }
        if (data.Length >= 2)      { code = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(0, 2)); return true; }
        code = 0; return false;
    }

    /// <summary>에코된 Run/Task ID 추출(v3, ≥74B). 파싱 실패 시 Guid.Empty.</summary>
    public static (Guid RunId, Guid TaskId) ReadIds(ReadOnlySpan<byte> data)
    {
        if (data.Length < Length) return (Guid.Empty, Guid.Empty);
        return (GuidAscii.Read(data.Slice(RunIdOff, 36)), GuidAscii.Read(data.Slice(TaskIdOff, 36)));
    }
}

/// <summary>GUID ↔ 36바이트 ASCII 정규형(소문자, 하이픈) 변환. 엔디안 모호성 제거용.</summary>
internal static class GuidAscii
{
    public static void Write(Span<byte> dst, Guid id)
    {
        // dst.Length == 36. Guid "D" 포맷 = 36자 ASCII.
        Span<char> chars = stackalloc char[36];
        id.TryFormat(chars, out _, "D");
        for (var i = 0; i < 36; i++) dst[i] = (byte)chars[i];
    }

    public static Guid Read(ReadOnlySpan<byte> src)
    {
        Span<char> chars = stackalloc char[src.Length];
        for (var i = 0; i < src.Length; i++) chars[i] = (char)src[i];
        return Guid.TryParse(chars, out var g) ? g : Guid.Empty;
    }
}

/// <summary>고정 길이 ASCII 필드(null 0x00 패딩) 쓰기/읽기.</summary>
internal static class AsciiField
{
    public static void Write(Span<byte> dst, string? text)
    {
        dst.Clear();
        if (string.IsNullOrEmpty(text)) return;
        var n = 0;
        foreach (var ch in text)
        {
            if (n >= dst.Length) break;
            dst[n++] = ch < 0x80 ? (byte)ch : (byte)'?';   // ASCII 전용
        }
    }
}

/// <summary>
/// HD현대 비전 인터페이스 프로토콜 상수/코드 정의. 사양: docs/비전 인터페이스_v2.xlsx.
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

public enum SurfaceType : byte
{
    Flat        = 0x00,
    Corner      = 0x01,
    Corrugation = 0x02
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

public sealed record SurfaceInfo(ushort Id, string Name, SurfaceType Type, string Axes);

/// <summary>사양 시트 5 "Surface ID 정의". ID 0x01~0x0A, 전부 Flat.
/// 검사 면 정본 표 <see cref="WallCodes"/> 에서 생성한다 — ACS wall_code·Teaching Wall ID 와 같은 번호를 보장.</summary>
public static class SurfaceCatalog
{
    public static readonly IReadOnlyList<SurfaceInfo> All = WallCodes.All
        .Select(w => new SurfaceInfo((ushort)w.SurfaceId, w.DisplayName, SurfaceType.Flat, w.Axes))
        .ToArray();

    public static string NameOf(ushort id) =>
        All.FirstOrDefault(s => s.Id == id)?.Name ?? $"Unknown(0x{id:X4})";
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
