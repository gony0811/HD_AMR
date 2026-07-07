using System.Buffers.Binary;

namespace HD_AMR.Communication.Vision;

/// <summary>
/// CAPTURE_REQ 의 15바이트 DATA 페이로드 생성.
/// 레이아웃: [0]Surface Type, [1-2]Surface ID(LE), [3-6]PosX(LE,mm), [7-10]PosY(LE,mm), [11-14]예약(0).
/// </summary>
public static class CaptureReqPayload
{
    public static byte[] Build(SurfaceType type, ushort surfaceId, int posX, int posY)
    {
        var data = new byte[15];
        data[0] = (byte)type;
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(1, 2), surfaceId);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(3, 4), posX);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(7, 4), posY);
        // bytes 11..14 reserved → 0x00
        return data;
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

/// <summary>사양 시트 5 "Surface ID 정의" 그대로. ID 0x01~0x0A, 전부 Flat.</summary>
public static class SurfaceCatalog
{
    public static readonly IReadOnlyList<SurfaceInfo> All = new SurfaceInfo[]
    {
        new(0x01, "바닥 (Bottom)",       SurfaceType.Flat, "U: 선수→선미, V: 좌현→우현"),
        new(0x02, "천장 (Top)",          SurfaceType.Flat, "U: 선수→선미, V: 좌현→우현"),
        new(0x03, "좌현벽 (Port)",        SurfaceType.Flat, "U: 선수→선미, V: 바닥→천장"),
        new(0x04, "우현벽 (Starboard)",   SurfaceType.Flat, "U: 선수→선미, V: 바닥→천장"),
        new(0x05, "전벽 (Forward)",      SurfaceType.Flat, "U: 좌현→우현, V: 바닥→천장"),
        new(0x06, "후벽 (Aft)",          SurfaceType.Flat, "U: 좌현→우현, V: 바닥→천장"),
        new(0x07, "하부 좌현 챔퍼",        SurfaceType.Flat, "U: 선수→선미, V: 바닥→좌현벽"),
        new(0x08, "하부 우현 챔퍼",        SurfaceType.Flat, "U: 선수→선미, V: 바닥→우현벽"),
        new(0x09, "상부 좌현 챔퍼",        SurfaceType.Flat, "U: 선수→선미, V: 천장→좌현벽"),
        new(0x0A, "상부 우현 챔퍼",        SurfaceType.Flat, "U: 선수→선미, V: 천장→우현벽"),
    };

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
