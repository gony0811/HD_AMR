namespace HD.AMR.App.Communication;

/// <summary>
/// 아덴트로봇 TARS-M Modbus TCP 레지스터 주소 맵
/// ※ 매뉴얼 주소와 NModbus 주소의 관계가 항목마다 일정하지 않다(대부분 그대로, 일부 −1·예외). 각 상수 주석의
/// 매뉴얼 주소와 실기 대조 결과를 따른다 — 입력 포즈는 실기 확인(그대로 · Hi 먼저), 그 외 Float32 쓰기는 미확인.
/// </summary>
public static class AmrRegisterMap
{
    /// <summary>
    /// Holding Register - 로봇 제어용 (쓰기)
    /// </summary>
    public static class Holding
    {
        /// <summary>전원 제어 (매뉴얼 주소 1)</summary>
        public const ushort Power = 0;

        /// <summary>주행 모드 (매뉴얼 주소 10) — 드라이브(1), 카트(2)</summary>
        public const ushort DrivingMode = 10;

        /// <summary>Error Reset (매뉴얼 주소 11) — 활성화(1)</summary>
        public const ushort AirInitialize = 11;

        /// <summary>로봇 주행 정지 (매뉴얼 주소 12) — 활성화(1), 비활성화(2)</summary>
        public const ushort RobotStop = 12;

        /// <summary>로봇 포즈 탐색 (매뉴얼 주소 20) — 활성화</summary>
        public const ushort PoseSearch = 19;

        /// <summary>포즈 탐색 좌표 X - Float32 Hi (매뉴얼 주소 21-22)</summary>
        public const ushort PoseTargetX = 20;

        /// <summary>포즈 탐색 좌표 Y - Float32 Hi (매뉴얼 주소 23-24)</summary>
        public const ushort PoseTargetY = 22;

        /// <summary>포즈 탐색 좌표 RZ - Float32 Hi (매뉴얼 주소 25-26)</summary>
        public const ushort PoseTargetAngle = 24;

        /// <summary>상태 제어 (매뉴얼 주소 30) — 정지(1), 시작(2), 일시정지(3)</summary>
        public const ushort ExecutionControl = 30;

        /// <summary>Task Index (매뉴얼 주소 31)</summary>
        public const ushort TaskIndex = 31;

        /// <summary>Job Index (매뉴얼 주소 32)</summary>
        public const ushort JobIndex = 32;

        /// <summary>유저 변수 시작 (매뉴얼 주소 50)</summary>
        public const ushort UserVariablesStart = 50;

        /// <summary>유저 변수 끝 (매뉴얼 주소 199)</summary>
        public const ushort UserVariablesEnd = 199;
    }

    /// <summary>
    /// Input Register - 로봇 상태 (읽기 전용)
    /// </summary>
    public static class Input
    {
        /// <summary>전원 상태 (매뉴얼 주소 1) — 정상(1), 종료(2), 재시작(3), 빠른재시작(4)</summary>
        public const ushort PowerStatus = 0;

        /// <summary>로봇 상태 (매뉴얼 주소 10) — 정지(1), 시작(2), 일시정지(3)</summary>
        public const ushort RobotStatus = 10;

        /// <summary>로봇 에러 코드 (매뉴얼 주소 11)</summary>
        public const ushort RobotError = 11;

        /// <summary>로봇 주행 정지 (매뉴얼 주소 12) — 활성화(1)</summary>
        public const ushort RobotStop = 12;

        /// <summary>WiFi 상태 (매뉴얼 주소 13) — 연결(1), 연결끊김(2)</summary>
        public const ushort WiFi = 13;

        /// <summary>작업 상태 (매뉴얼 주소 14) — 대기중(1), 이동중(2), 도킹중(3), 조그운전중(4)</summary>
        public const ushort WorkStatus = 64;

        // 포즈 Float32 는 <b>Hi 워드 먼저</b>(주소 N = Hi, N+1 = Lo)이고 매뉴얼 주소를 그대로 쓴다(−1 없음).
        // 실기 2026-11-03 FC04 원시값 대조: reg20..25 = 0x416C 0x4E90 0x403D 0xB5EC 0xC048 0x9AC1 →
        // (Hi=20, Lo=21) = 14.76918 = REST /robot/pose x. 종전 (Lo=19, Hi=20) 해석은 reg19(=0)를 Lo 로 써서
        // X 가 상위 16비트만 남아(14.75 — 8~16 m 구간 6.25 cm 단위로 잘림) Y·RZ 도 앞 값의 Lo 워드가 섞였다.

        /// <summary>포즈 X - Float32, Hi=20 · Lo=21 (매뉴얼 주소 20-21)</summary>
        public const ushort PoseX = 20;

        /// <summary>포즈 Y - Float32, Hi=22 · Lo=23 (매뉴얼 주소 22-23)</summary>
        public const ushort PoseY = 22;

        /// <summary>포즈 RZ - Float32, Hi=24 · Lo=25 (매뉴얼 주소 24-25)</summary>
        public const ushort PoseAngle = 24;

        /// <summary>맵 일치율 (매뉴얼 주소 30, %*10000)</summary>
        public const ushort MapStatus = 30;

        /// <summary>주행 모드 (매뉴얼 주소 40) — 드라이브(1), 카트(2)</summary>
        public const ushort DrivingMode = 40;

        /// <summary>배터리 전량 (매뉴얼 주소 50, %*10000)</summary>
        public const ushort BatteryLevel = 50;

        /// <summary>배터리 전압 (매뉴얼 주소 51, V*100)</summary>
        public const ushort BatteryVoltage = 51;

        /// <summary>배터리 전류 (매뉴얼 주소 52, A*100)</summary>
        public const ushort BatteryCurrent = 52;

        /// <summary>배터리 온도 (매뉴얼 주소 53, ℃*100)</summary>
        public const ushort BatteryTemp = 53;

        /// <summary>배터리 충전 여부 (매뉴얼 주소 54) — 충전중(1), 소비중(2)</summary>
        public const ushort ChargingState = 54;

        /// <summary>전체 Task 수 (매뉴얼 주소 60)</summary>
        public const ushort TotalTaskCount = 60;

        /// <summary>실행중인 Task 번호 (매뉴얼 주소 61)</summary>
        public const ushort CurrentTaskNumber = 61;

        /// <summary>전체 Job 수 (매뉴얼 주소 62)</summary>
        public const ushort TotalJobCount = 62;

        /// <summary>실행중인 Job 번호 (매뉴얼 주소 63)</summary>
        public const ushort CurrentJobNumber = 63;
    }
}
