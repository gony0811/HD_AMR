// HD_AMR ↔ 비전 검사 S/W v3.2 프로토콜 검증기 (독립 콘솔 TCP 서버).
//
// 비전 S/W(TCP 서버, 장치 0x02)를 흉내 낸다. VisionPanel(자동화, TCP 클라이언트, 0x01)이
// 접속해 Send 를 누르면 이 프로그램이 프레임을 수신·파싱·검증하고 CAPTURE_RES 를 회신한다.
//
// 사양(vision_interface_v3.2, HD.AMR.App 과 독립 구현):
//   프레임 = STX(1=0x02) | LENGTH(2 LE) | SEQ(1) | COMMAND(1) | FROM(1) | TO(1) | DATA(n) | CHECKSUM(1) | ETX(1=0x03)
//   LENGTH   = COMMAND + FROM + TO + DATA = 3 + DATA 길이
//   CHECKSUM = LENGTH(2B, 전송 그대로) ~ DATA 전체의 XOR
//   CAPTURE_REQ(0x02) DATA 34B: [0]surfaceType [1-2]wallId LE [3-6]u [7-10]v [11-14]h(Int32 LE)
//                               [15-30]taskId(GUID 16B, RFC4122 빅엔디안) [31]attempt [32-33]captureSeq LE
//   HEARTBEAT(0x01) DATA 20B: ts 14 ASCII(yyyyMMddHHmmss) + reserved 6
//   CAPTURE_RES(0x03)/ERROR_NOTI(0x04) DATA 2B: 결과 코드 UInt16 LE
//
// 실행: dotnet run --project tools/VisionProtocolTester -- [--port 5000] [--result 0x0000] [--bad-checksum]

using System.Net;
using System.Net.Sockets;
using System.Text;

var opts = Options.Parse(args);
Console.OutputEncoding = Encoding.UTF8;

Log.Banner(opts);

var listener = new TcpListener(IPAddress.Any, opts.Port);
listener.Start();
Console.CancelKeyPress += (_, e) => { e.Cancel = false; };

var stats = new Stats();

while (true)
{
    Log.Info($"포트 {opts.Port} 에서 접속 대기 중… (Ctrl+C 종료)");
    TcpClient client;
    try { client = await listener.AcceptTcpClientAsync(); }
    catch (Exception ex) { Log.Error($"Accept 실패: {ex.Message}"); break; }

    var remote = client.Client.RemoteEndPoint?.ToString() ?? "?";
    Log.Info($"접속됨: {remote}");
    try { await HandleClientAsync(client, opts, stats); }
    catch (Exception ex) { Log.Error($"세션 오류: {ex.Message}"); }
    Log.Info($"연결 종료: {remote}   {stats.Line()}");
    client.Dispose();
}

return;

static async Task HandleClientAsync(TcpClient client, Options opts, Stats stats)
{
    var stream = client.GetStream();
    var buffer = new List<byte>(512);
    var tmp = new byte[4096];

    while (true)
    {
        int n;
        try { n = await stream.ReadAsync(tmp); }
        catch (IOException) { break; }
        if (n == 0) break;                       // 원격 종료
        buffer.AddRange(tmp.AsSpan(0, n).ToArray());

        // 버퍼에서 뽑아낼 수 있는 프레임을 모두 처리.
        while (FrameSlicer.TryExtract(buffer, out var frame, out var skipped))
        {
            if (skipped > 0) Log.Warn($"동기화: STX 앞 {skipped}B 폐기");
            var report = Validator.Validate(frame);
            report.Print();
            stats.Add(report.Pass);

            // 검증 결과와 무관하게, CAPTURE_REQ 수신이면 CAPTURE_RES 회신(왕복 완성).
            if (report.Command == 0x02)
            {
                var res = FrameBuilder.CaptureRes(report.Seq, opts.ResultCode, opts.BadChecksum);
                await stream.WriteAsync(res);
                Log.Tx($"CAPTURE_RES  Result=0x{opts.ResultCode:X4} {Names.Result(opts.ResultCode)}"
                       + (opts.BadChecksum ? "  (의도적 CHECKSUM 오류)" : ""), res);
            }
        }
    }
}

// ─────────────────────────────────────────── 옵션 ───────────────────────────────────────────
sealed class Options
{
    public int Port = 5000;
    public ushort ResultCode = 0x0000;   // SUCCESS
    public bool BadChecksum;

    public static Options Parse(string[] args)
    {
        var o = new Options();
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--port" when i + 1 < args.Length: o.Port = int.Parse(args[++i]); break;
                case "--result" when i + 1 < args.Length:
                    var s = args[++i];
                    o.ResultCode = s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                        ? Convert.ToUInt16(s, 16) : ushort.Parse(s);
                    break;
                case "--bad-checksum": o.BadChecksum = true; break;
                case "-h" or "--help": PrintHelp(); Environment.Exit(0); break;
            }
        }
        return o;
    }

    static void PrintHelp() => Console.WriteLine(
        "VisionProtocolTester — v3.2 프레임 수신 검증기\n" +
        "  --port <n>       LISTEN 포트 (기본 5000)\n" +
        "  --result <code>  CAPTURE_RES 결과 코드 (기본 0x0000 SUCCESS, 예: 0x0001)\n" +
        "  --bad-checksum   응답 CHECKSUM 을 고의로 틀리게 (수신 파서 음성 테스트)\n");
}

// ─────────────────────────────────── 프레임 슬라이서(LENGTH 기반) ───────────────────────────────────
static class FrameSlicer
{
    const byte Stx = 0x02;
    const int MaxData = 256;

    /// <summary>버퍼 앞에서 완결된 프레임 1개를 떼어낸다. skipped = STX 탐색 중 버린 선행 바이트 수.</summary>
    public static bool TryExtract(List<byte> buf, out byte[] frame, out int skipped)
    {
        frame = Array.Empty<byte>();
        skipped = 0;
        while (true)
        {
            if (buf.Count == 0) return false;

            // STX 로 정렬.
            var stx = buf.IndexOf(Stx);
            if (stx < 0) { skipped += buf.Count; buf.Clear(); return false; }
            if (stx > 0) { skipped += stx; buf.RemoveRange(0, stx); }

            if (buf.Count < 3) return false;                 // LENGTH 를 못 읽음 → 더 대기
            int length = buf[1] | (buf[2] << 8);             // LENGTH (LE)
            int dataLen = length - 3;
            if (dataLen < 0 || dataLen > MaxData)            // LENGTH 비정상 → STX 1B 버리고 재탐색
            {
                buf.RemoveAt(0);
                skipped += 1;
                continue;
            }

            int total = 9 + dataLen;                         // STX+LEN2+SEQ+CMD+FROM+TO + DATA + CHK+ETX
            if (buf.Count < total) return false;             // 프레임 미완성 → 더 대기

            frame = buf.GetRange(0, total).ToArray();
            buf.RemoveRange(0, total);
            return true;
        }
    }
}

// ─────────────────────────────────────────── 검증 ───────────────────────────────────────────
sealed class Report
{
    public byte Command;
    public byte Seq;
    public bool Pass = true;
    public readonly List<byte> Frame = new();
    readonly List<(string label, string value, bool? ok, string note)> _rows = new();

    public void Row(string label, string value, bool? ok = null, string note = "")
    {
        _rows.Add((label, value, ok, note));
        if (ok == false) Pass = false;
    }

    public void Print()
    {
        Log.Rx($"{Frame.Count}B  {Hex.Of(Frame)}");
        foreach (var (label, value, ok, note) in _rows)
        {
            var mark = ok switch { true => "✅", false => "❌", _ => "  " };
            var line = $"    {label,-14}= {value}";
            if (note.Length > 0) line += $"   {note}";
            Log.Field(line, mark, ok);
        }
        Log.Verdict(Pass);
        Console.WriteLine();
    }
}

static class Validator
{
    public static Report Validate(byte[] f)
    {
        var r = new Report();
        r.Frame.AddRange(f);

        int length = f[1] | (f[2] << 8);
        int dataLen = length - 3;
        byte seq = f[3], cmd = f[4], from = f[5], to = f[6];
        r.Command = cmd; r.Seq = seq;
        var data = f.AsSpan(7, dataLen);
        byte chkField = f[^2];
        byte etx = f[^1];

        // ── 프레임 계층 ──
        r.Row("COMMAND", $"0x{cmd:X2} {Names.Command(cmd)}", Names.KnownCommand(cmd), Names.KnownCommand(cmd) ? "" : "미정의 명령");
        r.Row("SEQ", $"0x{seq:X2}");
        r.Row("FROM→TO", $"0x{from:X2}({Names.Device(from)}) → 0x{to:X2}({Names.Device(to)})",
            from == 0x01 && to == 0x02, from == 0x01 && to == 0x02 ? "" : "자동화(0x01)→비전(0x02) 기대");

        int expData = Names.ExpectedDataLen(cmd);
        r.Row("LENGTH", $"{length}  (DATA {dataLen}B)",
            expData < 0 ? (bool?)null : dataLen == expData,
            expData < 0 ? "" : $"{Names.Command(cmd)} DATA {expData}B 기대");

        byte chkCalc = Checksum(f, dataLen);
        r.Row("CHECKSUM", $"0x{chkField:X2}", chkField == chkCalc, chkField == chkCalc ? "" : $"계산 0x{chkCalc:X2}");
        r.Row("ETX", $"0x{etx:X2}", etx == 0x03, etx == 0x03 ? "" : "0x03 기대(프레이밍/LENGTH 오류 가능)");

        // ── DATA 계층 ──
        switch (cmd)
        {
            case 0x02: ValidateCaptureReq(r, data); break;
            case 0x01: ValidateHeartbeat(r, data); break;
            case 0x03:
            case 0x04: ValidateResult(r, data, cmd); break;
        }
        return r;
    }

    static void ValidateCaptureReq(Report r, ReadOnlySpan<byte> d)
    {
        if (d.Length != 34) { r.Row("DATA", $"{d.Length}B", false, "CAPTURE_REQ 34B 기대"); return; }

        byte st = d[0];
        int wallId = d[1] | (d[2] << 8);
        int u = ReadI32(d, 3), v = ReadI32(d, 7), h = ReadI32(d, 11);
        var taskId = GuidString(d.Slice(15, 16));   // RFC4122 빅엔디안 = 바이트 순서대로 hex
        byte attempt = d[31];
        int captureSeq = d[32] | (d[33] << 8);

        r.Row("surfaceType", $"{st} ({Names.Surface(st)})", st <= 2, st <= 2 ? "" : "0/1/2 기대");
        r.Row("wallId", $"{wallId} ({Names.Wall(wallId)})", wallId is >= 1 and <= 10, wallId is >= 1 and <= 10 ? "" : "1~10 기대");
        r.Row("u (PosX)", $"{u} mm");
        r.Row("v (PosY)", $"{v} mm");
        r.Row("h (PosZ)", $"{h} mm");
        r.Row("taskId", taskId, note: taskId == "00000000-0000-0000-0000-000000000000" ? "(Guid.Empty = 미지정)" : "");
        r.Row("attempt", $"{attempt}", attempt >= 1, attempt >= 1 ? "" : "1 이상 기대");
        r.Row("captureSeq", $"{captureSeq}", captureSeq >= 1, captureSeq >= 1 ? "" : "1 이상 기대");
    }

    static void ValidateHeartbeat(Report r, ReadOnlySpan<byte> d)
    {
        if (d.Length != 20) { r.Row("DATA", $"{d.Length}B", false, "HEARTBEAT 20B 기대"); return; }
        var ts = Encoding.ASCII.GetString(d.Slice(0, 14));
        bool tsOk = ts.Length == 14 && ts.All(char.IsDigit);
        r.Row("timestamp", ts, tsOk, tsOk ? "" : "yyyyMMddHHmmss(14자리) 기대");
    }

    static void ValidateResult(Report r, ReadOnlySpan<byte> d, byte cmd)
    {
        if (d.Length != 2) { r.Row("DATA", $"{d.Length}B", false, $"{Names.Command(cmd)} 2B 기대"); return; }
        int code = d[0] | (d[1] << 8);
        r.Row("resultCode", $"0x{code:X4} {Names.Result((ushort)code)}");
    }

    /// <summary>LENGTH(2B, 전송 LE 그대로) ~ DATA 전체의 XOR.</summary>
    public static byte Checksum(byte[] f, int dataLen)
    {
        byte x = 0;
        x ^= f[1]; x ^= f[2];                 // LENGTH (LE 2B)
        x ^= f[3]; x ^= f[4]; x ^= f[5]; x ^= f[6];  // SEQ, CMD, FROM, TO
        for (var i = 0; i < dataLen; i++) x ^= f[7 + i];
        return x;
    }

    static int ReadI32(ReadOnlySpan<byte> d, int off) =>
        d[off] | (d[off + 1] << 8) | (d[off + 2] << 16) | (d[off + 3] << 24);

    /// <summary>16바이트를 RFC4122 표준 순서(바이트 그대로 8-4-4-4-12)로 문자열화. System.Guid 미사용.</summary>
    static string GuidString(ReadOnlySpan<byte> b)
    {
        var sb = new StringBuilder(36);
        for (var i = 0; i < 16; i++)
        {
            if (i is 4 or 6 or 8 or 10) sb.Append('-');
            sb.Append(b[i].ToString("x2"));
        }
        return sb.ToString();
    }
}

// ─────────────────────────────────── 응답 프레임 빌더 ───────────────────────────────────
static class FrameBuilder
{
    public static byte[] CaptureRes(byte seq, ushort code, bool badChecksum)
    {
        // FROM=비전(0x02), TO=자동화(0x01), DATA=코드 2B LE. SEQ 는 요청 값을 에코.
        Span<byte> data = stackalloc byte[2];
        data[0] = (byte)(code & 0xFF); data[1] = (byte)(code >> 8);
        return Build(seq, 0x03, 0x02, 0x01, data, badChecksum);
    }

    static byte[] Build(byte seq, byte cmd, byte from, byte to, ReadOnlySpan<byte> data, bool badChecksum)
    {
        int length = 3 + data.Length;
        var f = new byte[9 + data.Length];
        f[0] = 0x02;
        f[1] = (byte)(length & 0xFF); f[2] = (byte)(length >> 8);
        f[3] = seq; f[4] = cmd; f[5] = from; f[6] = to;
        data.CopyTo(f.AsSpan(7));
        var chk = Validator.Checksum(f, data.Length);
        f[^2] = badChecksum ? (byte)(chk ^ 0xFF) : chk;
        f[^1] = 0x03;
        return f;
    }
}

// ─────────────────────────────────────────── 이름 표 ───────────────────────────────────────────
static class Names
{
    public static bool KnownCommand(byte c) => c is 0x01 or 0x02 or 0x03 or 0x04;
    public static string Command(byte c) => c switch
    { 0x01 => "HEARTBEAT", 0x02 => "CAPTURE_REQ", 0x03 => "CAPTURE_RES", 0x04 => "ERROR_NOTI", _ => "UNKNOWN" };

    public static int ExpectedDataLen(byte c) => c switch
    { 0x01 => 20, 0x02 => 34, 0x03 => 2, 0x04 => 2, _ => -1 };

    public static string Device(byte d) => d switch { 0x01 => "자동화", 0x02 => "비전", _ => "?" };
    public static string Surface(byte s) => s switch { 0 => "FLAT", 1 => "CORNER", 2 => "CORRUGATION", _ => "?" };

    static readonly string[] Walls =
    { "-", "바닥 B", "천장 T", "좌현벽 PM", "우현벽 SM", "전벽 F", "후벽 A",
      "하부좌현 PL", "하부우현 SL", "상부좌현 PU", "상부우현 SU" };
    public static string Wall(int id) => id is >= 1 and <= 10 ? Walls[id] : "범위 밖";

    public static string Result(ushort c) => c switch
    {
        0x0000 => "SUCCESS", 0x0001 => "ERR_TIMEOUT", 0x0002 => "ERR_CAMERA", 0x0003 => "ERR_POSITION",
        0x0004 => "ERR_SURFACE", 0x0005 => "ERR_BUSY", 0x00FF => "ERR_UNKNOWN", _ => "UNKNOWN"
    };
}

static class Hex
{
    public static string Of(IReadOnlyList<byte> b)
    {
        var sb = new StringBuilder(b.Count * 3);
        for (var i = 0; i < b.Count; i++) { if (i > 0) sb.Append(' '); sb.Append(b[i].ToString("X2")); }
        return sb.ToString();
    }
}

sealed class Stats
{
    int _rx, _pass, _fail;
    public void Add(bool pass) { _rx++; if (pass) _pass++; else _fail++; }
    public string Line() => $"[누적 수신 {_rx}, PASS {_pass}, FAIL {_fail}]";
}

// ─────────────────────────────────────────── 콘솔 출력 ───────────────────────────────────────────
static class Log
{
    static readonly object Gate = new();
    static void W(string s, ConsoleColor? c = null)
    {
        lock (Gate)
        {
            if (c is { } col) Console.ForegroundColor = col;
            Console.WriteLine(s);
            Console.ResetColor();
        }
    }

    public static void Banner(Options o) => W(
        $"╔═ HD_AMR ↔ 비전 v3.2 프로토콜 검증기 (독립 파싱) ═╗\n" +
        $"  LISTEN 0.0.0.0:{o.Port}   응답 Result=0x{o.ResultCode:X4} {Names.Result(o.ResultCode)}" +
        (o.BadChecksum ? "   [고의 CHECKSUM 오류]" : "") + "\n" +
        $"  VisionPanel 에서 Host=127.0.0.1 Port={o.Port} 로 Connect 후 Send.", ConsoleColor.Cyan);

    public static void Info(string s) => W($"· {s}", ConsoleColor.DarkGray);
    public static void Warn(string s) => W($"⚠ {s}", ConsoleColor.Yellow);
    public static void Error(string s) => W($"✗ {s}", ConsoleColor.Red);
    public static void Rx(string s) => W($"◀ RX {s}", ConsoleColor.White);
    public static void Tx(string s, byte[] f) => W($"▶ TX {s}\n     {Hex.Of(f)}", ConsoleColor.DarkCyan);
    public static void Field(string line, string mark, bool? ok) =>
        W($"{mark} {line}", ok switch { true => ConsoleColor.Green, false => ConsoleColor.Red, _ => ConsoleColor.Gray });
    public static void Verdict(bool pass) => W(pass ? "  ─────────────  ✅ PASS" : "  ─────────────  ❌ FAIL",
        pass ? ConsoleColor.Green : ConsoleColor.Red);
}
