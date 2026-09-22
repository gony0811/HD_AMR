using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HD.AMR.App.Communication;
using HD.AMR.App.Service;
using Microsoft.Extensions.DependencyInjection;

namespace HD.AMR.Desktop.ViewModels;

/// <summary>도면 좌표의 AMR(A)와 코봇 BASE(B) 설치 자세로 T_A_B를 계산·저장한다.</summary>
public sealed partial class ArucoMountCalibrationViewModel : ViewModelBase
{
    private readonly IServiceScopeFactory _scopes;
    private bool _updatingResult;

    public Pose6 HandEye { get; } = new();
    public Pose6 DrawingAmr { get; } = new();
    public Pose6 DrawingCobot { get; } = new();
    public Pose6 Mount { get; } = new();

    [ObservableProperty] private string? _message;
    [ObservableProperty] private bool _isError;
    [ObservableProperty] private bool _hasCalculatedResult;

    public ArucoMountCalibrationViewModel(IServiceScopeFactory scopes)
    {
        _scopes = scopes;
        DrawingAmr.PropertyChanged += (_, _) => InvalidateResult();
        DrawingCobot.PropertyChanged += (_, _) => InvalidateResult();
    }

    public override void OnActivated() => _ = ReloadAsync();

    public async Task ReloadAsync()
    {
        try
        {
            using var scope = _scopes.CreateScope();
            var calibration = scope.ServiceProvider.GetRequiredService<CalibrationService>();
            _updatingResult = true;
            Mount.FromArray(await calibration.GetMountAsync());
            HandEye.FromArray(await calibration.GetHandEyeAsync());
            HasCalculatedResult = false;
        }
        catch (Exception ex) { Fail($"설정 로드 실패: {ex.Message}"); }
        finally { _updatingResult = false; }
        NotifyState();
    }

    public string HandEyeText => HandEye.ToArray().All(v => v == 0)
        ? "미설정 — ① TTC hand-eye 보정을 먼저 완료하세요."
        : $"X={HandEye.V0:0.00}  Y={HandEye.V1:0.00}  Z={HandEye.V2:0.00} mm   ·   " +
          $"Rx={HandEye.V3:0.000}  Ry={HandEye.V4:0.000}  Rz={HandEye.V5:0.000}°";

    public bool CanCalculate => HandEye.ToArray().Any(v => v != 0);
    public bool CanSave => CanCalculate && HasCalculatedResult;

    [RelayCommand(CanExecute = nameof(CanCalculate))]
    private void Calculate()
    {
        try
        {
            var tGa = FrameMath.PoseToMatrix(DrawingAmr.ToArray());
            var tGb = FrameMath.PoseToMatrix(DrawingCobot.ToArray());
            var mount = FrameMath.MatrixToPose(FrameMath.Multiply(FrameMath.Invert(tGa), tGb));
            _updatingResult = true;
            Mount.FromArray(mount);
            HasCalculatedResult = true;
            Success("도면 설치 좌표로 T_A_B를 계산했습니다. 결과를 확인한 뒤 저장하세요.");
        }
        catch (Exception ex) { Fail($"좌표 계산 실패: {ex.Message}"); }
        finally { _updatingResult = false; NotifyState(); }
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveMount()
    {
        try
        {
            using var scope = _scopes.CreateScope();
            await scope.ServiceProvider.GetRequiredService<CalibrationService>().SaveMountAsync(Mount.ToArray());
            Success("도면 좌표에서 계산한 T_A_B를 설정 DB에 저장했습니다.");
        }
        catch (Exception ex) { Fail($"저장 실패: {ex.Message}"); }
    }

    private void InvalidateResult()
    {
        if (_updatingResult) return;
        HasCalculatedResult = false;
        SaveMountCommand.NotifyCanExecuteChanged();
    }

    private void NotifyState()
    {
        OnPropertyChanged(nameof(HandEyeText));
        CalculateCommand.NotifyCanExecuteChanged();
        SaveMountCommand.NotifyCanExecuteChanged();
    }

    private void Success(string text) { Message = text; IsError = false; }
    private void Fail(string text) { Message = text; IsError = true; }
}
