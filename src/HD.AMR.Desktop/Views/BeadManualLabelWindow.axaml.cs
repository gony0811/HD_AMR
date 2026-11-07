using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using HD.AMR.App.Models;
using HD.AMR.App.Service;
using HD.AMR.App.Service.Sequence;
using HD.AMR.App.Service.Sequence.Steps;
using Microsoft.Extensions.DependencyInjection;
using PixelPoint = HD.AMR.App.Models.PixelPoint;

namespace HD.AMR.Desktop.Views;

/// <summary>
/// <b>수동 비드 입력</b> 팝업. 두 가지 모드:
/// <list type="bullet">
/// <item><b>시퀀스</b>: 시퀀스가 고정한 스냅샷 위에 선을 받아 <see cref="BeadManualLabelHub.Submit"/> 으로 돌려준다.
/// 응답 전에는 창을 닫을 수 없고, 시간 초과·시퀀스 정지 시 자동으로 닫힌다.</item>
/// <item><b>테스트</b>(Dashboard): 라이브 화면(기본, 첫 점을 찍으면 정지)이나 저장된 촬영본 위에 선을 그려 d 를 확인한다.
/// 시퀀스 측정에는 영향이 없고, 체크하면 학습 데이터로 저장한다.</item>
/// </list>
/// </summary>
public partial class BeadManualLabelWindow : Window
{
    private static readonly IBrush PeakBrush = new SolidColorBrush(Color.Parse("#FF00FF"));
    private static readonly IBrush RefBrush = new SolidColorBrush(Color.Parse("#B0B0B0"));
    private static readonly IBrush BeadRoiBrush = new SolidColorBrush(Color.Parse("#FFD000"));
    private static readonly IBrush PeakRoiBrush = new SolidColorBrush(Color.Parse("#00C6E0"));
    private static readonly IBrush AutoBrush = new SolidColorBrush(Color.Parse("#66CCFF"));
    private static readonly IBrush HitBrush = new SolidColorBrush(Color.Parse("#FF3030"));
    private static readonly IBrush GoodBrush = new SolidColorBrush(Color.Parse("#3CC36B"));

    /// <summary>현재 표시 중인 영상·가이드 묶음.</summary>
    private sealed record View(byte[] IrPng, byte[]? ColorPng, ManualBeadGuide IrGuide, ManualBeadGuide? ColorGuide);

    // 시퀀스 모드
    private readonly ManualBeadRequest? _req;
    private readonly BeadManualLabelHub? _hub;
    private bool _answered;

    // 테스트 모드
    private readonly bool _test;
    private readonly IServiceScope? _scope;
    private readonly BeadManualTestService? _svc;
    private readonly DispatcherTimer _liveTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private ManualBeadSnapshot? _snap;
    private bool _live;
    private bool _busy;

    // 조그(두 모드 공통)
    private IServiceScope? _jogScope;
    private BeadManualJogService? _jog;
    private bool _jogging;
    private bool _jogLoading;

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private View? _view;
    private string _mod = "ir";
    private List<CanvasGuideLine> _baseGuides = new();

    public Guid RequestId => _req?.Id ?? Guid.Empty;

    public BeadManualLabelWindow()
    {
        InitializeComponent();
        BeadCanvas.LineMode = true;
        BeadCanvas.LineChanged += OnLineChanged;
        IrRadio.IsCheckedChanged += (_, _) => { if (IrRadio.IsChecked == true) ShowModality("ir"); };
        RgbRadio.IsCheckedChanged += (_, _) => { if (RgbRadio.IsChecked == true) ShowModality("rgb"); };
        ClearBtn.Click += (_, _) => BeadCanvas.Clear();
        LoadAutoBtn.Click += (_, _) => LoadAutoLine();
        JogLeftBtn.Click += async (_, _) => await JogAsync(BeadJogDirection.Left);
        JogRightBtn.Click += async (_, _) => await JogAsync(BeadJogDirection.Right);
        JogUpBtn.Click += async (_, _) => await JogAsync(BeadJogDirection.Up);
        JogDownBtn.Click += async (_, _) => await JogAsync(BeadJogDirection.Down);
        JogVelBox.ValueChanged += async (_, _) => await SaveJogSettingsAsync();
        JogStepBox.ValueChanged += async (_, _) => await SaveJogSettingsAsync();
        JogPanel.IsVisible = false;   // 서비스가 연결되면(InitJog) 보인다
        Closed += (_, _) => _jogScope?.Dispose();
    }

    /// <summary>시퀀스 모드 — 시퀀스 요청에 응답.</summary>
    public BeadManualLabelWindow(ManualBeadRequest req, BeadManualLabelHub hub) : this()
    {
        _req = req;
        _hub = hub;
        Topmost = true;

        Title = $"수동 비드 입력 — {req.Stage}";
        HeaderText.Text = $"{req.Stage} — 비드선을 그려 주세요";
        ReasonText.Text = req.Reason;
        UseAutoBtn.IsVisible = req.AutoUsable;
        ConfirmBtn.Click += (_, _) => ConfirmSequence();
        UseAutoBtn.Click += (_, _) => Answer(ManualBeadAnswer.Of(ManualBeadDecision.UseAuto));
        RejectBtn.Click += (_, _) => Answer(ManualBeadAnswer.Of(ManualBeadDecision.Reject));

        SetView(new View(req.IrPng, req.ColorPng, req.IrGuide, req.ColorGuide), req.DefaultModality);
        InitJog(Program.Services);
        if (req.JogCount > 0) JogStatusText.Text = $"조그 {req.JogCount}회 후 다시 찍은 화면입니다.";

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

    /// <summary>테스트 모드 — Dashboard 수동 팝업. 라이브 화면으로 시작한다.</summary>
    public BeadManualLabelWindow(IServiceProvider services) : this()
    {
        _test = true;
        _scope = services.CreateScope();
        _svc = _scope.ServiceProvider.GetRequiredService<BeadManualTestService>();

        Title = "수동 비드 입력 — 테스트";
        HeaderText.Text = "수동 비드 입력 테스트";
        ReasonText.Text = "시퀀스 측정에는 영향이 없습니다. 라이브 화면은 첫 점을 찍는 순간 정지합니다.";
        CountdownText.IsVisible = false;
        AxisCombo.IsVisible = LiveBtn.IsVisible = OpenFileBtn.IsVisible = true;
        SaveCheck.IsVisible = ResultText.IsVisible = true;
        UseAutoBtn.IsVisible = false;
        LoadAutoBtn.IsVisible = false;
        RejectBtn.Content = "닫기";
        RejectBtn.Classes.Remove("danger");
        ConfirmBtn.Content = "✔ 이 선으로 d 계산";
        ResultText.Text = "";

        _mod = _svc.DefaultModality;
        AxisCombo.SelectedIndex = _svc.DefaultAxis == WeldProgressAxis.Horizontal ? 0 : 1;
        AxisCombo.SelectionChanged += async (_, _) => await RebuildAxisAsync();
        SaveCheck.IsCheckedChanged += (_, _) =>
            ConfirmBtn.Content = SaveCheck.IsChecked == true ? "✔ d 계산 + 학습 데이터 저장" : "✔ 이 선으로 d 계산";
        LiveBtn.Click += (_, _) => SetLive(!_live);
        OpenFileBtn.Click += async (_, _) => await OpenSavedAsync();
        ConfirmBtn.Click += async (_, _) => await ConfirmTestAsync();
        RejectBtn.Click += (_, _) => Close();

        InitJog(services);
        _liveTimer.Tick += async (_, _) => await LiveTickAsync();
        Opened += (_, _) => SetLive(true);
        Closed += (_, _) =>
        {
            _liveTimer.Stop();
            _scope.Dispose();
        };
    }

    private WeldProgressAxis SelectedAxis => AxisCombo.SelectedIndex == 1 ? WeldProgressAxis.Vertical : WeldProgressAxis.Horizontal;

    private ManualBeadGuide? Guide => _view is null ? null : _mod == "rgb" ? _view.ColorGuide : _view.IrGuide;

    // ── 표시 ────────────────────────────────────────────────────────

    /// <summary>영상 묶음 교체. 선택한 영상(IR/RGB)이 없으면 IR 로.</summary>
    private void SetView(View view, string preferMod)
    {
        _view = view;
        bool hasRgb = view.ColorGuide is not null && view.ColorPng is not null;
        RgbRadio.IsEnabled = hasRgb;
        ToolTip.SetTip(RgbRadio, !hasRgb ? "컬러 영상이 없어 RGB 입력을 쓸 수 없습니다."
            : view.ColorGuide is { Calibrated: false } ? "캘리브레이션 없음 — RGB 는 라벨 그리기·저장만 가능(안내선·d 계산 없음)." : null);
        var mod = preferMod == "rgb" && hasRgb ? "rgb" : "ir";
        // 같은 모드면 IsCheckedChanged 가 오지 않으므로 직접 다시 그린다.
        if (mod == "rgb" && RgbRadio.IsChecked == true || mod == "ir" && IrRadio.IsChecked == true) ShowModality(mod);
        else if (mod == "rgb") RgbRadio.IsChecked = true;
        else IrRadio.IsChecked = true;
    }

    private void ShowModality(string mod)
    {
        if (_view is null) return;
        var png = mod == "rgb" ? _view.ColorPng : _view.IrPng;
        if (png is null) return;
        _mod = mod;
        BeadCanvas.Open(png, null);
        BeadCanvas.Clear();
        _baseGuides = BuildGuides(Guide);
        LoadAutoBtn.IsEnabled = Guide?.AutoLine is { Count: >= 2 };
        // 시퀀스 모드: 자동 검출이 선을 찾았으면 편집 가능한 초안으로 깔아 둔다 — 틀린 점만 드래그해 고치고 확정하면
        // 그 선으로 측정하고 학습 데이터로 저장한다. (하늘색 점선 = 원래 검출 선은 비교용으로 그대로 남는다.)
        if (!_test && LoadAutoBtn.IsEnabled) LoadAutoLine();
        UpdatePreview();
    }

    private static List<CanvasGuideLine> BuildGuides(ManualBeadGuide? g)
    {
        var list = new List<CanvasGuideLine>();
        if (g is null) return list;
        if (!g.Calibrated)
        {
            // 캘리브레이션 없는 컬러: IR 안내선을 옮길 수 없어 자동 검출 선만 그린다.
            if (g.AutoLine is { Count: >= 2 } a)
                for (int i = 0; i + 1 < a.Count; i++)
                    list.Add(new(new Point(a[i].X, a[i].Y), new Point(a[i + 1].X, a[i + 1].Y), AutoBrush, 1.5, Dashed: true));
            return list;
        }
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
            Points = ManualBeadGeometry.SimplifyForEdit(auto).Select(p => new[] { p.X, p.Y }).ToList(),
        });
    }

    /// <summary>현재 선이 자동 검출 선 그대로(수정 전)인지.</summary>
    private static bool IsAutoDraft(IReadOnlyList<PixelPoint> pts, ManualBeadGuide g)
        => g.AutoLine is { Count: >= 2 } line && ManualBeadGeometry.SimplifyForEdit(line) is var auto && auto.Count == pts.Count
           && auto.Zip(pts).All(z => Math.Abs(z.First.X - z.Second.X) < 0.5 && Math.Abs(z.First.Y - z.Second.Y) < 0.5);

    private List<PixelPoint> CurrentPoints()
        => BeadCanvas.GetLineLabel()?.Points.Select(p => new PixelPoint(p[0], p[1])).ToList() ?? new();

    private void OnLineChanged()
    {
        // 테스트 라이브: 첫 점을 찍으면 그 프레임에서 정지한다.
        if (_test && _live && CurrentPoints().Count > 0) SetLive(false);
        UpdatePreview();
    }

    private void UpdatePreview()
    {
        var g = Guide;
        var pts = CurrentPoints();
        var guides = new List<CanvasGuideLine>(_baseGuides);
        double? cross = g is not { Calibrated: true } ? null : ManualBeadGeometry.CrossAt(pts, g.Horizontal, g.ProgressPos);

        if (g is null)
            PreviewText.Text = "";
        else if (!g.Calibrated)
        {
            PreviewText.Text = "RGB 캘리브레이션 없음 — Peak·기준선 안내와 d 계산 없이 비드선 라벨만 그릴 수 있습니다"
                + (_test ? " ('학습 데이터로 저장'을 체크하고 확정하면 RGB 라벨만 저장, IR 변환은 안 됨)."
                         : ". 시퀀스 측정에는 IR 영상을 쓰세요.");
            BeadCanvas.Guides = guides;
            ConfirmBtn.IsEnabled = _test && pts.Count >= 2 && !_busy;
            return;
        }
        else if (pts.Count < 2)
            PreviewText.Text = (g.AutoLine is { Count: >= 2 } ? "자동 검출 선이 있습니다(하늘색 점선) — '자동 검출 선 불러오기'로 고쳐 쓸 수 있습니다. " : "")
                + "비드 중심선 위의 점을 2개 이상 찍으세요." + (g.PeakFound ? "" : " (Peak 미발견 — Bead ROI 중앙 위치에서 잽니다)");
        else if (cross is not { } c)
            PreviewText.Text = "선이 진행 방향으로 너무 짧습니다 — 비드를 따라 더 떨어진 두 점을 찍으세요.";
        else
        {
            double d = c - g.RefCross;
            var draftNote = IsAutoDraft(pts, g) ? "자동 검출 선 초안 — 틀렸으면 점을 드래그해 고친 뒤 확정하세요(학습 데이터로 저장). " : "";
            PreviewText.Text = draftNote + (_mod == "rgb"
                ? $"Peak 선과의 교점에서 d ≈ {d:+0.0;-0.0}px (컬러 기준, 진행 시 IR 픽셀로 환산)"
                : $"Peak 선과의 교점에서 d ≈ {d:+0.0;-0.0}px (IR 기준)");
            // 빨간 교점 표시(작은 X).
            var p = g.Horizontal ? new Point(g.ProgressPos, c) : new Point(c, g.ProgressPos);
            const double k = 6;
            guides.Add(new(new Point(p.X - k, p.Y - k), new Point(p.X + k, p.Y + k), HitBrush, 2));
            guides.Add(new(new Point(p.X - k, p.Y + k), new Point(p.X + k, p.Y - k), HitBrush, 2));
        }
        BeadCanvas.Guides = guides;
        ConfirmBtn.IsEnabled = cross is not null && !_answered && !_busy;
    }

    // ── 시퀀스 모드 ─────────────────────────────────────────────────

    private void ConfirmSequence()
    {
        var pts = CurrentPoints();
        if (Guide is not { Calibrated: true } g || ManualBeadGeometry.CrossAt(pts, g.Horizontal, g.ProgressPos) is null) return;
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

    // ── 조그 ───────────────────────────────────────────────────────

    /// <summary>조그 서비스 연결 + 파라미터(속도·1회 이동량)로 입력칸 초기화. 서비스가 없으면 조그 패널을 숨긴다.</summary>
    private async void InitJog(IServiceProvider? services)
    {
        if (services is null) return;
        try
        {
            _jogScope = services.CreateScope();
            _jog = _jogScope.ServiceProvider.GetService<BeadManualJogService>();
            if (_jog is null) return;
            JogPanel.IsVisible = true;
            var (vel, step) = await _jog.GetSettingsAsync();
            _jogLoading = true;
            JogVelBox.Value = (decimal)vel;
            JogStepBox.Value = (decimal)step;
        }
        catch (Exception ex) { JogStatusText.Text = $"조그 설정 읽기 실패: {ex.Message}"; }
        finally { _jogLoading = false; }
    }

    private double JogVel => (double)(JogVelBox.Value ?? (decimal)BeadManualSettings.DefaultJogVelPct);
    private double JogStep => (double)(JogStepBox.Value ?? (decimal)BeadManualSettings.DefaultJogStepMm);

    private async Task SaveJogSettingsAsync()
    {
        if (_jog is null || _jogLoading || JogVelBox.Value is null || JogStepBox.Value is null) return;
        try { await _jog.SaveSettingsAsync(JogVel, JogStep); }
        catch (Exception ex) { JogStatusText.Text = $"조그 설정 저장 실패: {ex.Message}"; }
    }

    /// <summary>
    /// 카메라를 화면 방향으로 1회 이동. 시퀀스 모드는 이동 뒤 "다시 찍기"로 응답해 시퀀스가 현재 자리의 프레임으로
    /// 새 팝업을 띄운다(이동 뒤 자동 결과는 쓸 수 없음). 테스트 모드는 라이브 화면을 다시 켠다.
    /// </summary>
    private async Task JogAsync(BeadJogDirection dir)
    {
        if (_jog is null || _jogging || _answered) return;
        _jogging = true;
        SetJogEnabled(false);
        JogStatusText.ClearValue(TextBlock.ForegroundProperty);
        JogStatusText.Text = $"조그 {BeadManualJogService.Name(dir)} {JogStep:0.#}mm 이동 중…";
        try
        {
            var (ok, msg) = await _jog.JogAsync(dir, JogStep, JogVel, _req?.Tool);
            JogStatusText.Text = msg;
            if (!ok) { JogStatusText.Foreground = HitBrush; return; }

            if (_req is not null) Answer(new ManualBeadAnswer(ManualBeadDecision.Recapture, _mod, Array.Empty<PixelPoint>()));
            else SetLive(true);
        }
        finally
        {
            _jogging = false;
            SetJogEnabled(true);
        }
    }

    private void SetJogEnabled(bool on)
        => JogLeftBtn.IsEnabled = JogRightBtn.IsEnabled = JogUpBtn.IsEnabled = JogDownBtn.IsEnabled = on;

    // ── 테스트 모드 ─────────────────────────────────────────────────

    private void SetLive(bool on)
    {
        if (!_test) return;
        _live = on;
        LiveBtn.Content = on ? "⏸ 라이브 정지" : "▶ 라이브";
        if (on)
        {
            BeadCanvas.Clear();
            ResultText.Text = "";
            _liveTimer.Start();
            _ = LiveTickAsync();
        }
        else _liveTimer.Stop();
    }

    private async Task LiveTickAsync()
    {
        if (!_live || _busy || _svc is null) return;
        _busy = true;
        try
        {
            var snap = await _svc.CaptureLiveAsync(SelectedAxis);
            if (!_live) return;   // 기다리는 사이 정지(첫 점)됐으면 그 프레임을 유지
            if (snap is null)
            {
                SourceText.Text = "라이브 프레임 없음 — 카메라 스트림을 시작하거나 저장된 사진을 여세요.";
                return;
            }
            ApplySnapshot(snap, $"라이브 {snap.CapturedAt:HH:mm:ss}");
        }
        catch (Exception ex) { SourceText.Text = $"라이브 실패: {ex.Message}"; }
        finally { _busy = false; UpdatePreview(); }
    }

    private void ApplySnapshot(ManualBeadSnapshot snap, string source)
    {
        _snap = snap;
        SourceText.Text = source + (snap.Peak is null ? " · Peak 미발견" : $" · Peak {snap.Peak.DepthValue}mm");
        SetView(new View(snap.IrPng, snap.ColorPng, snap.IrGuide, snap.ColorGuide), _mod);
    }

    private async Task RebuildAxisAsync()
    {
        if (_svc is null || _snap is null || _busy) return;
        if (_live) return;   // 라이브는 다음 프레임부터 새 축으로
        _busy = true;
        try
        {
            var pts = CurrentPoints();
            var snap = await _svc.RebuildAsync(_snap.Frames, SelectedAxis);
            ApplySnapshot(snap, _snap.Frames.Source);
            if (pts.Count > 0 && Guide is { } g)   // 그린 선은 유지(축만 바뀜)
                BeadCanvas.SetLine(new HD.AMR.App.Service.Vision.WeldLineLabel
                {
                    Width = g.Width, Height = g.Height,
                    Points = pts.Select(p => new[] { p.X, p.Y }).ToList(),
                });
        }
        catch (Exception ex) { SourceText.Text = $"진행축 변경 실패: {ex.Message}"; }
        finally { _busy = false; UpdatePreview(); }
    }

    private async Task OpenSavedAsync()
    {
        if (_svc is null) return;
        SetLive(false);
        var dir = await _svc.GetCaptureDirAsync();
        var start = dir is null ? null : await StorageProvider.TryGetFolderFromPathAsync(dir);
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "저장된 촬영본 열기 (…_ir.png / _rgb.png)",
            AllowMultiple = false,
            SuggestedStartLocation = start,
            FileTypeFilter = [new FilePickerFileType("촬영본 이미지") { Patterns = ["*_ir.png", "*_rgb.png", "*_depth16.png"] }],
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (path is null) return;

        _busy = true;
        try
        {
            var snap = await _svc.LoadSavedAsync(path, SelectedAxis);
            ResultText.Text = "";
            // 저장 파일을 연 경우 선택한 파일의 영상(IR/RGB)으로 보여준다.
            if (path.EndsWith("_rgb.png", StringComparison.OrdinalIgnoreCase)) _mod = "rgb";
            else if (path.EndsWith("_ir.png", StringComparison.OrdinalIgnoreCase)) _mod = "ir";
            ApplySnapshot(snap, snap.Frames.Source + (snap.Depth is null ? " · depth16 없음" : ""));
        }
        catch (Exception ex) { SourceText.Text = $"열기 실패: {ex.Message}"; }
        finally { _busy = false; UpdatePreview(); }
    }

    private async Task ConfirmTestAsync()
    {
        if (_svc is null || _snap is null) return;
        var pts = CurrentPoints();
        if (Guide is { Calibrated: false })
        {
            // 캘리브레이션 없는 RGB: d 는 못 재고 라벨만 저장한다.
            if (SaveCheck.IsChecked != true)
            {
                ResultText.Text = "캘리브레이션이 없어 d 를 계산할 수 없습니다 — '확정 시 학습 데이터로 저장'을 체크하면 RGB 라벨만 저장합니다.";
                ResultText.Foreground = HitBrush;
                return;
            }
            _busy = true; UpdatePreview();
            try
            {
                var r = await _svc.SaveAsync(_snap, _mod, pts);
                ResultText.Text = $"RGB 라벨 저장: {r.Stem}_rgb_line.json · _maskdraft.png (캘리브레이션 없음 — d 계산·IR 변환 생략)";
                ResultText.Foreground = GoodBrush;
            }
            catch (Exception ex)
            {
                ResultText.Text = $"저장 실패: {ex.Message}";
                ResultText.Foreground = HitBrush;
            }
            finally { _busy = false; UpdatePreview(); }
            return;
        }
        if (_svc.Evaluate(_snap, _mod, pts) is not { } ev)
        {
            ResultText.Text = "d 를 계산할 수 없습니다 — 선을 진행 방향으로 더 길게 그리세요.";
            ResultText.Foreground = HitBrush;
            return;
        }

        var mm = ev.DMm is { } v ? $"{v:+0.0;-0.0}mm" : "mm 환산 불가(FOV/해상도 미상)";
        var text = $"d = {ev.DIrPx:+0.0;-0.0}px(IR) = {mm} · 깊이 {ev.DepthZ:0}mm · {(ev.PeakFound ? "Peak 기준" : "Peak 미발견, ROI 중앙 기준")}"
                   + (ev.Modality == "rgb" ? $" · 컬러 d {ev.DModalityPx:+0.0;-0.0}px" : "");
        ResultText.Foreground = GoodBrush;

        if (SaveCheck.IsChecked == true)
        {
            _busy = true; UpdatePreview();
            try
            {
                var r = await _svc.SaveAsync(_snap, _mod, pts);
                text += _snap.Frames.SavedPath is null
                    ? $"\n학습 데이터 저장: {r.Stem}_{_mod} (프레임 + 선 라벨 + 마스크)"
                    : $"\n라벨 저장(덮어쓰기): {r.Stem}_{_mod}_line.json · _maskdraft.png";
                text += r.Transferred is { } t
                    ? $"\n{t.ToUpperInvariant()} 라벨도 깊이·캘리브레이션으로 변환해 저장했습니다 ({r.Stem}_{t}_line.json)."
                    : r.TransferNote is { } n ? $"\n반대 영상 라벨: {n}" : "";
            }
            catch (Exception ex)
            {
                text += $"\n저장 실패: {ex.Message}";
                ResultText.Foreground = HitBrush;
            }
            finally { _busy = false; UpdatePreview(); }
        }
        ResultText.Text = text;
    }
}
