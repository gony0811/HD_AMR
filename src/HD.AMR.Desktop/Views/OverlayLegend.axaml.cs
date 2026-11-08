using Avalonia;
using Avalonia.Controls;

namespace HD.AMR.Desktop.Views;

/// <summary>용접선 오버레이 범례(검출 용접선·연장선·정답 선·모델 마스크). 비전 학습 ④ 와 검출 비교평가가 공유한다.</summary>
public partial class OverlayLegend : UserControl
{
    /// <summary>검출 선·연장선·모델 마스크 항목 표시(④ 추론·검증 경로의 오버레이일 때).</summary>
    public static readonly StyledProperty<bool> ShowDetectionProperty =
        AvaloniaProperty.Register<OverlayLegend, bool>(nameof(ShowDetection), true);

    /// <summary>정답 선 항목 표시(정답 선 라벨이 있을 때).</summary>
    public static readonly StyledProperty<bool> ShowGroundTruthProperty =
        AvaloniaProperty.Register<OverlayLegend, bool>(nameof(ShowGroundTruth), true);

    public bool ShowDetection { get => GetValue(ShowDetectionProperty); set => SetValue(ShowDetectionProperty, value); }
    public bool ShowGroundTruth { get => GetValue(ShowGroundTruthProperty); set => SetValue(ShowGroundTruthProperty, value); }

    public OverlayLegend() => InitializeComponent();
}
