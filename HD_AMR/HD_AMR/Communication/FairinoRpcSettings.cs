namespace HD_AMR.Communication;

/// <summary>
/// 페어리노(FAIRINO) 협동로봇 RPC 연결 설정.
/// 컨트롤러는 XML-RPC 명령 포트(기본 20003)와 별도 TCP 상태 피드백 포트(기본 20004)를 노출한다.
/// </summary>
public class FairinoRpcSettings
{
    public string Name { get; set; } = "Cobot";

    /// <summary>컨트롤러 IP. 공장 기본값은 192.168.58.2.</summary>
    public string IpAddress { get; set; } = "192.168.58.2";

    /// <summary>XML-RPC 명령 포트.</summary>
    public int CommandPort { get; set; } = 20003;

    /// <summary>
    /// XML-RPC HTTP 경로. 파이썬 xmlrpc.client는 경로 미지정 시 자동으로 "/RPC2"를 사용하며,
    /// FAIRINO 서버도 "/RPC2"에서만 응답한다(루트 "/"는 404). 비우면 경로 없이 보낸다.
    /// </summary>
    public string RpcPath { get; set; } = "/RPC2";

    /// <summary>실시간 상태 피드백 TCP 포트.</summary>
    public int StatePort { get; set; } = 20004;

    /// <summary>기본 공구 좌표계(Tool) 번호.</summary>
    public int DefaultToolId { get; set; } = 0;

    /// <summary>기본 사용자(작업물) 좌표계(User) 번호.</summary>
    public int DefaultUserId { get; set; } = 0;

    /// <summary>기본 이동 속도 비율(%). 안전을 위해 낮게 시작.</summary>
    public double DefaultVelPct { get; set; } = 20;

    /// <summary>연결 확인용 TCP 도달성 타임아웃(ms). 짧게 둔다.</summary>
    public int TimeoutMs { get; set; } = 5000;

    /// <summary>
    /// XML-RPC 명령 타임아웃(ms). MoveL 등은 모션 완료까지 블로킹하므로 길게 둔다(기본 10분).
    /// 파이썬 ServerProxy는 무제한이라 문제없지만 .NET 클라이언트는 명시 필요.
    /// </summary>
    public int CommandTimeoutMs { get; set; } = 600000;
}
