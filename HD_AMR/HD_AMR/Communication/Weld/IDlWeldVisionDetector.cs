namespace HD_AMR.Communication.Weld;

/// <summary>
/// DL(YOLOv8-seg) 기반 용접라인 검출기 마커. 고전 CV 검출기(<see cref="IWeldVisionDetector"/>)와
/// 동시에 DI 에 등록해 <c>WeldTrackingService</c> 가 검출 방식을 런타임에 토글할 수 있도록,
/// 타입으로 구분되는 별도 인터페이스로 둔다. 계약은 <see cref="IWeldVisionDetector"/> 와 동일.
/// </summary>
public interface IDlWeldVisionDetector : IWeldVisionDetector
{
}
