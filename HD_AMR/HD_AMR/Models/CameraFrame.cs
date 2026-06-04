namespace HD_AMR.Models;

/// <summary>
/// 카메라에서 막 캡처한 한 장의 프레임 스냅샷. 불변(record) — 캡처 스레드는 새 인스턴스를 만들어
/// <c>Interlocked.Exchange</c> 로 교체하고, 소비자(MJPEG 엔드포인트/Blazor)는 참조 하나만 읽으면
/// 락 없이 안전하다. <see cref="PixelFormat"/> 으로 컬러/깊이를 구분한다.
/// </summary>
/// <param name="Pixels">
/// 픽셀 버퍼. <c>"rgb24"</c> 이면 W·H·3 RGB888 (row-major), <c>"depth16"</c> 이면 W·H 개의
/// little-endian uint16 (단위 mm, 0=무효).
/// </param>
/// <param name="Width">픽셀 너비.</param>
/// <param name="Height">픽셀 높이.</param>
/// <param name="PixelFormat"><c>"rgb24"</c> 또는 <c>"depth16"</c>.</param>
/// <param name="CapturedAt">캡처 타임스탬프 (UTC).</param>
public sealed record CameraFrame(
    byte[] Pixels,
    int Width,
    int Height,
    string PixelFormat,
    DateTime CapturedAt);
