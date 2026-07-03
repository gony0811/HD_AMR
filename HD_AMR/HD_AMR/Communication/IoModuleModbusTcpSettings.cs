namespace HD_AMR.Communication;

/// <summary>
/// LS산전 IO Module ModbusTCP 연결 설정. 실제 값은 appsettings.json "IoModule" 섹션으로 덮어쓴다.
/// IO list(주소별 입출력 매핑)는 추후 확정 예정.
/// </summary>
public class IoModuleModbusTcpSettings : ModbusTcpSettings
{
    public IoModuleModbusTcpSettings()
    {
        Name = "IoModule";
        IpAddress = "10.10.100.202";
        Port = 502;
        SlaveId = 20;   // LS산전 IO 모듈 station no
    }
}
