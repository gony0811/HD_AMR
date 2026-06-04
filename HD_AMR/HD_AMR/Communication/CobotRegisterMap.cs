namespace HD_AMR.Communication;

/// <summary>
/// 코봇 Modbus TCP 레지스터 주소 맵 (Fair Innovation / FAIRINO).
/// 스펙 문서의 주소가 0-based Modbus PDU 주소이므로 NModbus에 그대로 전달한다.
/// (참고: AMR은 1-based라 별도 -1 보정이 필요하지만 코봇은 0-based.)
/// NAMUGA_AMR의 동일 맵에서 이식 — 같은 제조사라 주소가 일치한다.
/// </summary>
public static class CobotRegisterMap
{
    /// <summary>Coil (FC 0x01/0x05/0x15) — PLC→Robot 비트 쓰기</summary>
    public static class Coil
    {
        /// <summary>DI0~DI127 비트 입력 시작 주소</summary>
        public const ushort DigitalInputStart = 100;

        /// <summary>DI0~DI127 비트 입력 끝 주소</summary>
        public const ushort DigitalInputEnd = 227;

        /// <summary>DI 비트 수</summary>
        public const ushort DigitalInputCount = 128;

        /// <summary>제어기 DO 시작 주소</summary>
        public const ushort ControllerDoStart = 300;

        /// <summary>제어기 DO 끝 주소</summary>
        public const ushort ControllerDoEnd = 317;

        /// <summary>일시정지</summary>
        public const ushort Pause = 500;

        /// <summary>복구</summary>
        public const ushort Recovery = 501;

        /// <summary>시작</summary>
        public const ushort Start = 502;

        /// <summary>정지</summary>
        public const ushort Stop = 503;

        /// <summary>원점 이동</summary>
        public const ushort MoveToJobOrigin = 504;

        /// <summary>수동/자동 전환</summary>
        public const ushort ManualAutoSwitch = 505;

        /// <summary>메인 프로그램 시작</summary>
        public const ushort StartMainProgram = 506;

        /// <summary>전체 오류 해제</summary>
        public const ushort ClearAllFaults = 510;
    }

    /// <summary>Discrete Input (FC 0x02) — Robot→PLC 비트 읽기</summary>
    public static class DiscreteInput
    {
        /// <summary>DO0~DO127 비트 출력 시작 주소</summary>
        public const ushort DigitalOutputStart = 100;

        /// <summary>DO0~DO127 비트 출력 끝 주소</summary>
        public const ushort DigitalOutputEnd = 227;

        /// <summary>DO 비트 수</summary>
        public const ushort DigitalOutputCount = 128;
    }

    /// <summary>Holding Register (FC 0x03/0x06/0x16) — PLC→Robot 워드 쓰기</summary>
    public static class Holding
    {
        /// <summary>AI0~AI31 아날로그 입력 시작 주소</summary>
        public const ushort AnalogInputStart = 100;

        /// <summary>AI0~AI31 아날로그 입력 끝 주소</summary>
        public const ushort AnalogInputEnd = 131;

        /// <summary>AI 워드 수</summary>
        public const ushort AnalogInputCount = 32;
    }

    /// <summary>Input Register (FC 0x04) — Robot→PLC 워드 읽기</summary>
    public static class Input
    {
        /// <summary>AO0~AO31 아날로그 출력 시작 주소</summary>
        public const ushort AnalogOutputStart = 100;

        /// <summary>AO0~AO31 아날로그 출력 끝 주소</summary>
        public const ushort AnalogOutputEnd = 131;

        /// <summary>AO 워드 수</summary>
        public const ushort AnalogOutputCount = 32;

        /// <summary>Enable 상태 (0=Not enabled, 1=Enabled)</summary>
        public const ushort EnableState = 310;

        /// <summary>로봇 모드 (1=Manual, 0=Automatic)</summary>
        public const ushort RobotMode = 311;

        /// <summary>동작 상태 (1=Stop, 2=Run, 3=Pause, 4=Drag)</summary>
        public const ushort OperationStatus = 312;

        /// <summary>Tool 번호</summary>
        public const ushort ToolNo = 313;

        /// <summary>Job 번호</summary>
        public const ushort JobNumber = 314;

        /// <summary>비상정지 상태 (0=정상, 1=비상정지)</summary>
        public const ushort ScrumState = 315;

        /// <summary>로봇 상태 — Super soft limit fault</summary>
        public const ushort RobotStatusFault = 316;

        /// <summary>Master fault code</summary>
        public const ushort MasterFaultCode = 317;

        /// <summary>Sub fault code</summary>
        public const ushort SubFaultCode = 318;

        /// <summary>충돌 감지 (1=Collision, 0=No collision)</summary>
        public const ushort CollisionDetection = 319;

        /// <summary>Motion in place signal</summary>
        public const ushort MotionInPlace = 320;

        /// <summary>Safety stop signal S0</summary>
        public const ushort SafetyStopS0 = 321;

        /// <summary>Safety stop signal S1</summary>
        public const ushort SafetyStopS1 = 322;

        /// <summary>상태 레지스터 시작 주소</summary>
        public const ushort StatusStart = 310;

        /// <summary>상태 레지스터 수 (310~322 = 13개)</summary>
        public const ushort StatusCount = 13;
    }
}
