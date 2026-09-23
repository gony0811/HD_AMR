using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HD.AMR.App.Service;
using Microsoft.Extensions.DependencyInjection;

namespace HD.AMR.Desktop.ViewModels;

/// <summary>AMR 차체(A) 기준 코봇 BASE(B)의 장착 자세 T_A_B를 직접 입력·저장한다.</summary>
public sealed partial class ArucoMountCalibrationViewModel : ViewModelBase
{
    private readonly IServiceScopeFactory _scopes;

    public Pose6 HandEye { get; } = new();
    public Pose6 Mount { get; } = new();

    [ObservableProperty] private string? _message;
    [ObservableProperty] private bool _isError;

    public ArucoMountCalibrationViewModel(IServiceScopeFactory scopes)
        => _scopes = scopes;

    public override void OnActivated() => _ = ReloadAsync();

    public async Task ReloadAsync()
    {
        try
        {
            using var scope = _scopes.CreateScope();
            var calibration = scope.ServiceProvider.GetRequiredService<CalibrationService>();
            Mount.FromArray(await calibration.GetMountAsync());
            HandEye.FromArray(await calibration.GetHandEyeAsync());
        }
        catch (Exception ex) { Fail($"설정 로드 실패: {ex.Message}"); }
        NotifyState();
    }

    public string HandEyeText => HandEye.ToArray().All(v => v == 0)
        ? "미설정 — ① TTC hand-eye 보정을 먼저 완료하세요."
        : $"X={HandEye.V0:0.00}  Y={HandEye.V1:0.00}  Z={HandEye.V2:0.00} mm   ·   " +
          $"Rx={HandEye.V3:0.000}  Ry={HandEye.V4:0.000}  Rz={HandEye.V5:0.000}°";

    public bool CanSave => HandEye.ToArray().Any(v => v != 0);

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveMount()
    {
        try
        {
            using var scope = _scopes.CreateScope();
            await scope.ServiceProvider.GetRequiredService<CalibrationService>().SaveMountAsync(Mount.ToArray());
            Success("T_A_B를 설정 DB에 저장했습니다.");
        }
        catch (Exception ex) { Fail($"저장 실패: {ex.Message}"); }
    }

    private void NotifyState()
    {
        OnPropertyChanged(nameof(HandEyeText));
        SaveMountCommand.NotifyCanExecuteChanged();
    }

    private void Success(string text) { Message = text; IsError = false; }
    private void Fail(string text) { Message = text; IsError = true; }
}
