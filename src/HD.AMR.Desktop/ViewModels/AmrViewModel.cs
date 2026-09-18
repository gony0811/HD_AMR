using System.Collections.ObjectModel;
using System.Text.Json;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HD.AMR.App.Enums;
using HD.AMR.App.Communication;
using HD.AMR.App.Models;
using HD.AMR.App.Service;
using Microsoft.Extensions.Options;

namespace HD.AMR.Desktop.ViewModels;

/// <summary>
/// AMR 상태 읽기(Input Register) + 제어 쓰기(Holding Register). 기존 Amr.razor 이식.
/// 1초 폴링으로 <see cref="AMRService.LatestStatus"/> 를 바인딩하고, 쓰기 명령은 로그에 남긴다.
/// </summary>
public sealed partial class AmrViewModel : ViewModelBase
{
    private readonly AMRService _svc;
    private readonly AmrRestClient _rest;
    private readonly AmrRestSettings _restSettings;
    private readonly DispatcherTimer _timer;
    private RobotStatus? _status;
    private bool _busy;

    public AmrViewModel(AMRService svc, AmrRestClient rest, IOptions<AmrRestSettings> restOptions)
    {
        _svc = svc;
        _rest = rest;
        _restSettings = restOptions.Value;
        MapName = _restSettings.MapName;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Refresh();
    }

    public override void OnActivated() { Refresh(); _timer.Start(); }
    public override void OnDeactivated() => _timer.Stop();

    private void Refresh()
    {
        _status = _svc.IsConnected ? _svc.LatestStatus : null;
        // 모든 표시/파생 속성 새로고침(빈 문자열 = 전체 알림).
        OnPropertyChanged(string.Empty);
        WriteCommand.NotifyCanExecuteChanged();
    }

    // ── 연결/버튼 상태 ──
    public bool IsConnected => _svc.IsConnected;
    public string ConnectionText => _svc.IsConnected ? "연결됨" : "미연결";
    public bool CanWrite => !_busy && _svc.IsConnected;

    // ── 기본 상태 ──
    public string PowerStateText => Enum(_status?.PowerState);
    public string RobotStateText => Enum(_status?.RobotState);
    public string ErrorCodeText => _status?.ErrorCode.ToString() ?? "-";
    public string RobotStopText => _status?.RobotStopActive switch { null => "-", 1 => "활성화", 0 => "-", _ => "비활성화" };
    public string WiFiText => Enum(_status?.WiFi);
    public string WorkStatusText => Enum(_status?.WorkStatus);
    public string DrivingModeText => Enum(_status?.DrivingMode);

    // ── 위치 ──
    public string PoseXText => Num(_status?.Pose.X, 3);
    public string PoseYText => Num(_status?.Pose.Y, 3);
    public string PoseAngleText => Num(_status?.Pose.Angle, 3);

    // ── 배터리 ──
    public string BatteryLevelText => _status is null ? "-" : Num(_status.Battery.LevelPercent, 2) + " %";
    public string BatteryVoltageText => Num(_status?.Battery.Voltage, 2);
    public string BatteryCurrentText => Num(_status?.Battery.Current, 2);
    public string BatteryTempText => Num(_status?.Battery.TemperatureCelsius, 2);
    public string ChargingText => Enum(_status?.Battery.ChargingState);

    // ── 맵/Task ──
    public string MapStatusText => Num(_status?.MapStatusPercent, 2);
    public string TotalTaskText => _status?.TaskProgress.TotalTaskCount.ToString() ?? "-";
    public string CurrentTaskText => _status?.TaskProgress.CurrentTaskNumber.ToString() ?? "-";
    public string TotalJobText => _status?.TaskProgress.TotalJobCount.ToString() ?? "-";
    public string CurrentJobText => _status?.TaskProgress.CurrentJobNumber.ToString() ?? "-";

    // ── REST 맵 조회 ──
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadMapCommand))]
    private string _mapName = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMapImage))]
    private Bitmap? _mapImage;
    [ObservableProperty] private string _mapLoadStatus = "맵 이름은 AMR API에서 자동 조회되지 않습니다. appsettings.json에 지정하거나 아래에 입력하세요.";
    [ObservableProperty] private string _mapSummary = "";
    public bool HasMapImage => MapImage is not null;
    public string MapRequestTarget => $"{_restSettings.BaseUrl.TrimEnd('/')}/{_restSettings.MapContentPath.Trim('/')}/{{맵 이름}}";

    private bool CanLoadMap() => !_busy && !string.IsNullOrWhiteSpace(MapName);

    [RelayCommand(CanExecute = nameof(CanLoadMap))]
    private async Task LoadMapAsync()
    {
        if (_busy) return;
        _busy = true;
        LoadMapCommand.NotifyCanExecuteChanged();
        WriteCommand.NotifyCanExecuteChanged();
        MapLoadStatus = "맵을 가져오는 중…";
        try
        {
            var result = await _rest.GetMapContentAsync(MapName);
            if (!result.Ok || result.Data is not { } data)
                throw new InvalidOperationException(result.Message ?? $"AMR API 오류 ({result.Code})");

            if (!TryProperty(data, "mapping", out var mapping) || mapping.ValueKind != JsonValueKind.String)
                throw new InvalidOperationException("응답에 mapping(base64 PNG) 필드가 없습니다.");

            var bytes = Convert.FromBase64String(mapping.GetString()!);
            await using var stream = new MemoryStream(bytes, writable: false);
            var bitmap = new Bitmap(stream);
            var old = MapImage;
            MapImage = bitmap;
            old?.Dispose();

            var nodeCount = TryProperty(data, "node", out var nodes) && nodes.ValueKind == JsonValueKind.Array
                ? nodes.GetArrayLength() : 0;
            var courseCount = TryProperty(data, "course", out var courses) && courses.ValueKind == JsonValueKind.Array
                ? courses.GetArrayLength() : 0;
            MapSummary = $"{MapName.Trim()} · {bitmap.PixelSize.Width}×{bitmap.PixelSize.Height}px · 노드 {nodeCount}개 · 코스 {courseCount}개";
            MapLoadStatus = $"가져옴 {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex)
        {
            MapLoadStatus = $"맵 조회 실패: {ex.Message}";
        }
        finally
        {
            _busy = false;
            LoadMapCommand.NotifyCanExecuteChanged();
            WriteCommand.NotifyCanExecuteChanged();
        }
    }

    private static bool TryProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out value)) return true;
        value = default;
        return false;
    }

    // ── 쓰기 입력값 (콤보 SelectedIndex) ──
    // Power: 인덱스==값(0 None,1 PowerOff,2 Restart,3 QuickRestart).
    [ObservableProperty] private int _powerSel;
    // 아래 3개는 인덱스+1 이 명령값(Drive/Stop/활성화가 값 1 부터 시작).
    [ObservableProperty] private int _drivingModeIndex;   // 0→Drive(1), 1→Cart(2)
    [ObservableProperty] private int _executionIndex;     // 0→Stop(1), 1→Start(2), 2→Pause(3)
    [ObservableProperty] private int _robotStopIndex;     // 0→활성화(1), 1→비활성화(2)
    [ObservableProperty] private int _taskIndex;
    [ObservableProperty] private int _jobIndex;
    [ObservableProperty] private double _poseX;
    [ObservableProperty] private double _poseY;
    [ObservableProperty] private double _poseAngle;

    public ObservableCollection<AmrLogEntry> Log { get; } = new();

    [RelayCommand(CanExecute = nameof(CanWrite))]
    private Task Write(string what) => what switch
    {
        "power"     => Run($"Power = {PowerSel}", ct => _svc.SetPowerAsync((PowerCommand)PowerSel, ct)),
        "driving"   => Run($"DrivingMode = {DrivingModeIndex + 1}", ct => _svc.SetDrivingModeAsync((DrivingMode)(DrivingModeIndex + 1), ct)),
        "execution" => Run($"ExecutionControl = {ExecutionIndex + 1}", ct => _svc.SetExecutionControlAsync((ExecutionControl)(ExecutionIndex + 1), ct)),
        "robotstop" => Run($"RobotStop = {RobotStopIndex + 1}", ct => _svc.SetRobotStopAsync((ushort)(RobotStopIndex + 1), ct)),
        "errorreset" => Run("AirInitialize = 1", ct => _svc.AirInitializeAsync(ct)),
        "taskjob"   => Run($"Task={TaskIndex}, Job={JobIndex}", async ct =>
        {
            await _svc.SetTaskIndexAsync((ushort)TaskIndex, ct);
            await _svc.SetJobIndexAsync((ushort)JobIndex, ct);
        }),
        "posesearch" => Run("PoseSearch = 1", ct => _svc.SetPoseSearchAsync(1, ct)),
        "posetarget" => Run($"PoseTarget X={PoseX}, Y={PoseY}, A={PoseAngle}",
            ct => _svc.SetPoseTargetAsync((float)PoseX, (float)PoseY, (float)PoseAngle, ct)),
        _ => Task.CompletedTask,
    };

    [RelayCommand]
    private void ClearLog() => Log.Clear();

    private async Task Run(string label, Func<CancellationToken, Task> action)
    {
        if (_busy) return;
        _busy = true;
        WriteCommand.NotifyCanExecuteChanged();
        try
        {
            await action(CancellationToken.None);
            AddLog(label, true, null);
        }
        catch (Exception ex) { AddLog(label, false, ex.Message); }
        finally
        {
            _busy = false;
            WriteCommand.NotifyCanExecuteChanged();
        }
    }

    private void AddLog(string command, bool ok, string? error)
    {
        Log.Insert(0, new AmrLogEntry(DateTime.Now.ToString("HH:mm:ss"), command, ok, error));
        while (Log.Count > 100) Log.RemoveAt(Log.Count - 1);
    }

    private static string Enum<T>(T? value) where T : struct, System.Enum
        => value is { } v && System.Enum.IsDefined(v) ? v.ToString() : "-";

    private static string Num(float? value, int digits)
        => value is { } v ? Math.Round(v, digits).ToString() : "-";
}

public sealed record AmrLogEntry(string Time, string Command, bool Ok, string? Error)
{
    public string ResultText => Ok ? "OK" : (Error ?? "FAIL");
}
