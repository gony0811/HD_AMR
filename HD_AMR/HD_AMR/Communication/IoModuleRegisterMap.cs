namespace HD_AMR.Communication;

/// <summary>
/// LS산전 IO 모듈 Modbus 주소 맵. 물리 구성: XBE-DC16A(디지털 입력 16점) + XBE-TN08A(트랜지스터 출력 8점).
/// 입력은 Discrete Input(FC02), 출력은 Coil(FC01 읽기 / FC05 쓰기)로 매핑한다.
/// 입력과 출력은 Modbus 상 별도 주소공간이라 둘 다 0에서 시작한다.
/// 실제 하드웨어 구성이 다르면(예: 입력도 Coil 영역에 매핑) 여기 Start/Count만 조정하면 된다.
/// </summary>
public static class IoModuleRegisterMap
{
    /// <summary>XBE-DC16A 디지털 입력 16점 — Discrete Input(FC02) 0~15</summary>
    public static class Input
    {
        public const ushort Start = 0;
        public const ushort Count = 16;
    }

    /// <summary>XBE-TN08A 트랜지스터 출력 8점 — Coil(FC01 읽기 / FC05 쓰기) 0~7</summary>
    public static class Output
    {
        public const ushort Start = 0;
        public const ushort Count = 8;
    }
}
