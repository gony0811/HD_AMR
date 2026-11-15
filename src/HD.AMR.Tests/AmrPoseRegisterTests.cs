using HD.AMR.App.Communication;
using HD.AMR.App.Service;

namespace HD.AMR.Tests;

/// <summary>
/// TARS-M 입력 포즈 레지스터 해석 — 실기 2026-11-03 FC04 원시값과 같은 순간의 REST /robot/pose 값으로 고정.
/// 종전 해석(Lo=19, Hi=20)은 X 를 상위 16비트만 남겨 14.75 로 잘랐다(8~16 m 구간 6.25 cm 단위).
/// </summary>
public class AmrPoseRegisterTests
{
    // reg15..28 원시값 (FC04, 0-based)
    private static readonly ushort[] Raw = new ushort[65];

    static AmrPoseRegisterTests()
    {
        ushort[] r20to25 = { 0x416C, 0x4E90, 0x403D, 0xB5EC, 0xC048, 0x9AC1 };
        r20to25.CopyTo(Raw, 20);
    }

    private static float Decode(ushort addr) => AMRService.HiLoToFloat(Raw[addr], Raw[addr + 1]);

    [Fact]
    public void Pose_MatchesRestPose()
    {
        Assert.Equal(14.769180224576418, Decode(AmrRegisterMap.Input.PoseX), 5);
        Assert.Equal(2.9642287365142503, Decode(AmrRegisterMap.Input.PoseY), 5);
        Assert.Equal(-3.134445409993887, Decode(AmrRegisterMap.Input.PoseAngle), 5);
    }

    [Fact]
    public void OldReading_TruncatedX()
    {
        // 종전 주소·순서 (Lo=19, Hi=20) 재현 — 회귀 방지용 기록
        float old = BitConverter.Int32BitsToSingle((int)(((uint)Raw[20] << 16) | Raw[19]));
        Assert.Equal(14.75f, old);
        Assert.NotEqual(old, Decode(AmrRegisterMap.Input.PoseX));
    }
}
