namespace HD_AMR.Communication;

/// <summary>
/// 코봇(FAIRINO) Modbus TCP 연결 설정. AMR과 동일하게 <see cref="ModbusTcpSettings"/>를 상속한다.
/// 공장 기본 코봇 IP는 192.168.57.2.
/// </summary>
public class CobotModbusTcpSettings : ModbusTcpSettings
{
    public CobotModbusTcpSettings()
    {
        Name = "Cobot";
        IpAddress = "192.168.57.2";
    }
}
