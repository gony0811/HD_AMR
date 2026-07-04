namespace HD_AMR.Communication;

/// <summary>
/// FAIRINO StartJOG의 좌표계(ref) 값. 값이 곧 StartJOG의 ref 인자이며,
/// StopJOG의 ref는 여기에 +1 (관절 1, 베이스 3, 툴 5, 작업물 9).
/// </summary>
public enum JogFrame
{
    /// <summary>관절 조그(nb = J1..J6).</summary>
    Joint = 0,
    /// <summary>베이스 좌표계(nb = X,Y,Z,Rx,Ry,Rz).</summary>
    Base = 2,
    /// <summary>활성 툴 좌표계(nb = X,Y,Z,Rx,Ry,Rz).</summary>
    Tool = 4,
    /// <summary>활성 작업물 좌표계(nb = X,Y,Z,Rx,Ry,Rz).</summary>
    Workpiece = 8,
}

/// <summary><see cref="JogFrame"/> 확장: StartJOG/StopJOG의 ref 값 산출.</summary>
public static class JogFrameExtensions
{
    /// <summary>StartJOG에 넘길 ref 값.</summary>
    public static int StartRef(this JogFrame frame) => (int)frame;

    /// <summary>StopJOG에 넘길 ref 값 = StartJOG ref + 1.</summary>
    public static int StopRef(this JogFrame frame) => (int)frame + 1;
}
