using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using HD.AMR.App.Models;
using PixelPoint = HD.AMR.App.Models.PixelPoint;
using HD.AMR.App.Service.Sequence;

namespace HD.AMR.Desktop.Views;

/// <summary>
/// 시퀀스 비드 찾기의 <b>수동 비드 입력</b> 팝업. 시퀀스가 고정한 스냅샷(IR/RGB) 위에 Peak 선·기준선·ROI·자동 검출 선을
/// 겹쳐 보여주고, 작업자가 비드 중심선을 찍어 확정하면 <see cref="BeadManualLabelHub.Submit"/> 으로 돌려준다.
/// 응답 전에는 창을 닫을 수 없다(시퀀스가 멈춰 있으므로 반드시 버튼으로 결정). 시간 초과·시퀀스 정지 시 자동으로 닫힌다.
/// </summary>
public partial class BeadManualLabelWindow : Window
{
    private static readonly IBrush PeakBrush = new SolidColorBrush(Color.Parse("#FF00FF"));
    private static readonly IBrush RefBrush = new SolidColorBrush(Color.Parse("#B0B0B0"));
    private static readonly IBrush BeadRoiBrush = new SolidColorBrush(Color.Parse("#FFD000"));
    private static readonly IBrush PeakRoiBrush = new SolidColorBrush(Color.Parse("#00C6E0"));
    private static readonly IBrush AutoBrush = new SolidColorBrush(Color.Parse("#66CCFF"));
    private static readonly IBrush HitBrush = new SolidColorBrush(Color.Parse("#FF3030"));

    private readonly ManualBeadRequest? _req;
    private readonly BeadManualLabelHub? _hub;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private string _mod = "ir";
    private bool _answered;
    private List<CanvasGuideLine> _baseGuides = new();

    public Guid RequestId => _req?.Id ?? Guid.Empty;

    public BeadManualLabelWindow() => InitializeComponent();

    public BeadManualLabelWindow(ManualBeadRequest req, BeadManualLabelHub hub) : this()
    {
        _req = req;
        _hub = hub;

        Title = $"수동 비드 입력 — {req.Stage}";
        HeaderText.Text = $"{req.Stage} — 비드선을 그려 주세요";
        ReasonText.Text = req.Reason;
        UseAutoBtn.IsVisible = req.AutoUsable;
        RgbRadio.IsEnabled = req.ColorGuide is not null && req.ColorPng is not null;

        BeadCanvas.LineMode = true;
        BeadCanvas.LineChanged += UpdatePreview;
        IrRadio.IsCheckedChanged += (_, _) => { if (IrRadio.IsChecked == true) ShowModality("ir"); };
        RgbRadio.IsCheckedChanged += (_, _) => { if (RgbRadio.IsChecked == true) ShowModality("rgb"); };
        ClearBtn.Click += (_, _) => BeadCanvas.Clear();
        LoadAutoBtn.Click += (_, _) => LoadAutoLine();
        ConfirmBtn.Click += (_, _) => Confirm();
        UseAutoBtn.Click += (_, _) => Answer(ManualBeadAnswer.Of(ManualBeadDecision.UseAuto));
        RejectBtn.Click += (_, _) => Answer(ManualBeadAnswer.Of(ManualBeadDecision.Reject));

        if (req.DefaultModality == "rgb" && RgbRadio.IsEnabled) RgbRadio.IsChecked = true;
        else IrRadio.IsChecked = true;

        _timer.Tick += (_, _) => UpdateCountdown();
        UpdateCountdown();
        _timer.Start();

        hub.Changed += OnHubChanged;
        Closing += OnClosing;
        Closed += (_, _) =>
        {
            _timer.Stop();
            hub.Changed -= OnHubChanged;
        };
    }

    private ManualBeadGuide? Guide => _req is null ? null : _mod == "rgb" ? _req.ColorGuide : _req.IrGuide;

    private void ShowModality(string mod)
    {
        if (_req is null) return;
        var png = mod == "rgb" ? _req.ColorPng : _req.IrPng;
        if (png is null) return;
        _mod = mod;
        BeadCanvas.Open(png, null);
        BeadCanvas.Clear();
        _baseGuides = BuildGuides(Guide);
        LoadAutoBtn.IsEnabled = Guide?.AutoLine is { Count: >= 2 };
        UpdatePreview();
    }

    private static List<CanvasGuideLine> BuildGuides(ManualBeadGuide? g)
    {
        var list = new List<CanvasGuideLine>();
        if (g is null) return list;
        Point Pc(double prog, double cross) => g.Horizontal ? new Point(prog, cross) : new Point(cross, prog);
        int len = g.Horizontal ? g.Width : g.Height, crossLen = g.Horizontal ? g.Height : g.Width;

        void Rect(RoiRect? r, IBrush b)
        {
            if (r is null) return;
            Point a = new(r.X, r.Y), bb = new(r.Right, r.Y), c = new(r.Right, r.Bottom), d = new(r.X, r.Bottom);
            list.Add(new(a, bb, b, 1)); list.Add(new(bb, c, b, 1)); list.Add(new(c, d, b, 1)); list.Add(new(d, a, b, 1));
        }

        Rect(g.PeakRoi, PeakRoiBrush);
        Rect(g.BeadRoi, BeadRoiBrush);
        list.Add(new(Pc(0, g.RefCross), Pc(len, g.RefCross), RefBrush, 1, Dashed: true));
        // Peak 선: 전체 길이 얇게 + 비드 cross 구간 굵게. Peak 미발견이면 측정 위치(ROI 중앙)를 점선으로.
        list.Add(new(Pc(g.ProgressPos, 0), Pc(g.ProgressPos, crossLen), PeakBrush, 1, Dashed: !g.PeakFound));
        if (g is { PeakCrossStart: { } cs, PeakCrossEnd: { } ce })
            list.Add(new(Pc(g.ProgressPos, cs), Pc(g.ProgressPos, ce), PeakBrush, 3));
        if (g.AutoLine is { Count: >= 2 } auto)
            for (int i = 0; i + 1 < auto.Count; i++)
                list.Add(new(new Point(auto[i].X, auto[i].Y), new Point(auto[i + 1].X, auto[i + 1].Y), AutoBrush, 1.5, Dashed: true));
        return list;
    }

    private void LoadAutoLine()
    {
        if (Guide is not { AutoLine: { Count: >= 2 } auto } g) return;
        BeadCanvas.SetLine(new HD.AMR.App.Service.Vision.WeldLineLabel
        {
            Width = g.Width,
            Height = g.Height,
            Points = auto.Select(p => new[] { p.X, p.Y }).ToList(),
        });
    }

    private List<PixelPoint> CurrentPoints()
        => BeadCanvas.GetLineLabel()?.Points.Select(p => new PixelPoint(p[0], p[1])).ToList() ?? new();

    private void UpdatePreview()
    {
        var g = Guide;
        var pts = CurrentPoints();
        var guides = new List<CanvasGuideLine>(_baseGuides);
        double? cross = g is null ? null : ManualBeadGeometry.CrossAt(pts, g.Horizontal, g.ProgressPos);

        if (g is null)
            PreviewText.Text = "";
        else if (pts.Count < 2)
            PreviewText.Text = "비드 중심선 위의 점을 2개 이상 찍으세요.";
        else if (cross is not { } c)
            PreviewText.Text = "선이 진행 방향으로 너무 짧습니다 — 비드를 따라 더 떨어진 두 점을 찍으세요.";
        else
        {
            double d = c - g.RefCross;
            PreviewText.Text = _mod == "rgb"
                ? $"Peak 선과의 교점에서 d ≈ {d:+0.0;-0.0}px (컬러 기준, 진행 시 IR 픽셀로 환산)"
                : $"Peak 선과의 교점에서 d ≈ {d:+0.0;-0.0}px (IR 기준)";
            // 빨간 교점 표시(작은 X).
            var p = g.Horizontal ? new Point(g.ProgressPos, c) : new Point(c, g.ProgressPos);
            const double k = 6;
            guides.Add(new(new Point(p.X - k, p.Y - k), new Point(p.X + k, p.Y + k), HitBrush, 2));
            guides.Add(new(new Point(p.X - k, p.Y + k), new Point(p.X + k, p.Y - k), HitBrush, 2));
        }
        BeadCanvas.Guides = guides;
        ConfirmBtn.IsEnabled = cross is not null && !_answered;
    }

    private void Confirm()
    {
        var pts = CurrentPoints();
        if (Guide is not { } g || ManualBeadGeometry.CrossAt(pts, g.Horizontal, g.ProgressPos) is null) return;
        Answer(new ManualBeadAnswer(ManualBeadDecision.Manual, _mod, pts));
    }

    private void Answer(ManualBeadAnswer a)
    {
        if (_answered || _req is null || _hub is null) return;
        _answered = true;
        _hub.Submit(_req.Id, a);
        Close();
    }

    private void UpdateCountdown()
    {
        if (_req is null) return;
        var left = _req.DeadlineUtc - DateTime.UtcNow;
        if (left < TimeSpan.Zero) left = TimeSpan.Zero;
        CountdownText.Text = $"⏱ {(int)left.TotalMinutes}:{left.Seconds:00}";
        if (left.TotalSeconds <= 30) CountdownText.Foreground = HitBrush;
        else CountdownText.ClearValue(TextBlock.ForegroundProperty);
    }

    // 시간 초과·시퀀스 정지로 요청이 사라지면 창을 닫는다.
    private void OnHubChanged() => Dispatcher.UIThread.Post(() =>
    {
        if (_answered || _hub is null) return;
        if (_hub.Pending?.Id != RequestId) { _answered = true; Close(); }
    });

    // 사용자가 X 로 닫는 것은 막는다 — 시퀀스가 응답을 기다리고 있다. 앱 종료·소유 창 종료는 허용.
    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_answered || e.IsProgrammatic || e.CloseReason != WindowCloseReason.WindowClosing) return;
        if (_hub?.Pending?.Id != RequestId) return;
        e.Cancel = true;
        PreviewText.Text = "시퀀스가 응답을 기다리고 있습니다 — 아래 버튼으로 진행/실패를 선택하세요.";
    }
}
