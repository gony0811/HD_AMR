using HD.AMR.App.Service.Motion;

namespace HD.AMR.Tests;

[CollectionDefinition("CobotEnvelopeStatic", DisableParallelization = true)]
public class CobotEnvelopeStaticCollection;

/// <summary>코봇 BASE 높이 파라미터(<see cref="CobotEnvelope.ApplyBaseHeight"/>) — 리프트 탈거 시 실측값으로 바꾼다.</summary>
[Collection("CobotEnvelopeStatic")]   // 정적 Default 를 바꾸므로 다른 테스트와 병렬 실행하지 않는다
public class CobotBaseHeightTests : IDisposable
{
    public void Dispose() => CobotEnvelope.ApplyBaseHeight(null);   // 기본값 복원

    [Fact]
    public void 리프트_탈거_실측값을_적용한다()
    {
        // 2026-11-17: 리프트 자리에 받침을 놓고 코봇을 직접 장착 — 바퀴 닿는 면 → 받침면 927mm.
        Assert.True(CobotEnvelope.ApplyBaseHeight("927"));
        Assert.Equal(927, CobotEnvelope.Default.BaseHeightMm, 6);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void 비어_있으면_기본값(string? raw)
    {
        Assert.True(CobotEnvelope.ApplyBaseHeight(raw));
        Assert.Equal(CobotEnvelope.DefaultBaseHeightMm, CobotEnvelope.Default.BaseHeightMm, 6);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("500")]     // 차체 윗면(887)보다 낮음 — 설정 실수
    [InlineData("3000")]
    public void 잘못된_값은_기본값으로_두고_false(string raw)
    {
        Assert.False(CobotEnvelope.ApplyBaseHeight(raw));
        Assert.Equal(CobotEnvelope.DefaultBaseHeightMm, CobotEnvelope.Default.BaseHeightMm, 6);
    }

    [Fact]
    public void 다른_치수는_그대로()
    {
        CobotEnvelope.ApplyBaseHeight("927");
        var fresh = new CobotEnvelope();
        Assert.Equal(fresh.AmrHeightMm, CobotEnvelope.Default.AmrHeightMm);
        Assert.Equal(fresh.ColumnHalfMm, CobotEnvelope.Default.ColumnHalfMm);
    }
}
