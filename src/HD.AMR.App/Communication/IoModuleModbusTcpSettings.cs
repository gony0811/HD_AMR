namespace HD.AMR.App.Communication;

/// <summary>
/// LS산전 IO 모듈(XEL-BSSRT RAPIEnet+ 어댑터 + XBE-DC16A 입력 16점 + XBE-TN16A 출력 16점) Modbus TCP 설정.
/// (모듈 구성은 2026-09-16 현장 사진(docs\IMG_4067.HEIC)으로 확정.)
///
/// 주소 맵은 LS "통신 디바이스 간편 사용설명서"(docs, 2023-11-16) §3 Step.8·부록 A + 실기 프로브로 확정:
///   · 리프레시 영역은 0x200 부터 — <b>입력 리프레시(FC04/FC02)</b> = [LED 헤더 4바이트(워드 0x200~0x201,
///     부록 A.2.2 비트 정의)] + [DC16A 입력 1워드 = 0x202]. TN16A 는 입력 리프레시 기여 0바이트(echo 없음).
///   · <b>출력 리프레시(Holding)</b> = 0x200 부터 출력 모듈 데이터 — TN16A 1워드 = Holding 0x200
///     (FC06/FC16 쓰기, FC03 x1 되읽기 가능).
///   · 비트 주소 = 워드 × 16 (FC02 0x2000 대역 = 워드 0x200 대역의 비트 뷰).
///   · 유닛 ID 는 장비가 무시(실측) — SlaveId 는 형식상 유지.
///
/// ⚠ 출력이 실제로 구동되려면 어댑터 드라이버 설정이 필요하다(사용설명서 §3 Step.3):
///   XG5000 으로 어댑터 접속 → 드라이버 "RAPIEnet v2" → <b>Disable</b> (공장 초기값이 RAPIEnet v2 라
///   RAPIEnet 마스터 대기 상태(RNS 점멸)에서는 Modbus 출력 쓰기가 반영되지 않는 것을 실기로 확인).
/// </summary>
public class IoModuleModbusTcpSettings : ModbusTcpSettings
{
    public IoModuleModbusTcpSettings()
    {
        Name = "IoModule";
        IpAddress = "10.10.100.202";
        Port = 502;
        SlaveId = 20;   // LS산전 IO 모듈 station no (장비는 유닛 ID 무시 — 실측)
    }

    /// <summary>입력(XBE-DC16A) Discrete Input(FC02) 시작 주소 — LED 헤더 4바이트 뒤,
    /// 워드 0x202 의 비트 뷰 = 0x2020 (매뉴얼 부록 A.2 + 실측 확정).</summary>
    public ushort InputStart { get; set; } = 0x2020;

    /// <summary>입력 접점 수.</summary>
    public ushort InputCount { get; set; } = 16;

    /// <summary>어댑터 LED 상태 헤더(Input Register, FC04) 워드 주소 — 2워드(부록 A.2.2 비트 정의).
    /// RUN/RMS/RNS/RELAY/LINK 상태(0=Off,1=On,2=Blink) 진단 표시용.</summary>
    public ushort LedHeaderAddress { get; set; } = 0x200;

    /// <summary>출력(XBE-TN16A) Holding Register 워드 주소 — 출력 리프레시 첫 워드(0x200).
    /// FC16 으로 워드 단위 쓰기, FC03 x1 로 명령값 되읽기.</summary>
    public ushort OutputWriteAddress { get; set; } = 0x200;

    /// <summary>출력 접점 수 — TN16A 16점(현장 사진 확정).</summary>
    public ushort OutputCount { get; set; } = 16;
}
