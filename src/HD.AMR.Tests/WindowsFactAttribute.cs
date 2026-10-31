using System.Runtime.InteropServices;

namespace HD.AMR.Tests;

/// <summary>
/// Windows x64 에서만 실행하는 [Fact]. OpenCvSharp 네이티브(OpenCvSharpExtern)는 <c>OpenCvSharp4.runtime.win</c> 만
/// 참조하므로(대상 플랫폼 = Windows x64) macOS·Linux 개발 PC 에서는 라이브러리 로드 자체가 실패한다 —
/// 그 실패를 결함으로 세지 않도록 건너뜀으로 표시한다.
/// </summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            Skip = "OpenCvSharp 네이티브는 win-x64 만 포함 — Windows x64 에서만 실행";
    }
}
