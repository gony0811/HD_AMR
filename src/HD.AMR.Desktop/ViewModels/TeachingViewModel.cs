using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HD.AMR.App.Communication;
using HD.AMR.App.Data.Entities;
using HD.AMR.App.Service;
using Microsoft.Extensions.DependencyInjection;

namespace HD.AMR.Desktop.ViewModels;

/// <summary>
/// 티칭 위치(BASE 좌표계) 관리. 기존 Teaching.razor 이식 — 현재 위치 캡처/이동/초기화/삭제, 이름·Wall ID 편집,
/// 작업물 좌표계 N 기준 상대 pose 저장·추종 이동, 활성 작업물 배지 + 베이스 전환.
/// TeachingService(Scoped)는 작업마다 scope 를 연다.
/// </summary>
public sealed partial class TeachingViewModel : ViewModelBase
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly CobotService _cobot;
    private readonly DispatcherTimer _timer;
    private CancellationTokenSource? _moveCts;

    public ObservableCollection<TeachingRowVm> Rows { get; } = new();

    [ObservableProperty] private int _runTool = 1;
    [ObservableProperty] private int _capUser;      // 캡처 시 작업물 좌표계(0=베이스)
    [ObservableProperty] private int _vel = 20;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _message;
    [ObservableProperty] private bool _isErr;
    [ObservableProperty] private string _newName = "";
    [ObservableProperty] private int _newSurfaceId;

    public bool CobotConnected => _cobot.IsConnected;
    public bool HasActiveUser => _cobot.State?.User is >= 0;
    public bool ActiveIsBase => _cobot.State?.User == 0;
    public bool ActiveNotBase => _cobot.State?.User is > 0;
    public string ActiveUserText => _cobot.State?.User switch
    {
        0 => "활성 작업물: #0 (베이스)",
        int n and > 0 => $"활성 작업물: #{n} (베이스 아님)",
        _ => "활성 작업물: 미상",
    };

    public TeachingViewModel(IServiceScopeFactory scopeFactory, CobotService cobot)
    {
        _scopeFactory = scopeFactory;
        _cobot = cobot;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => RefreshStatus();
    }

    public override async void OnActivated()
    {
        _timer.Start();
        RefreshStatus();
        await ReloadAsync();
    }

    public override void OnDeactivated() => _timer.Stop();

    private void RefreshStatus()
    {
        OnPropertyChanged(nameof(CobotConnected));
        OnPropertyChanged(nameof(HasActiveUser));
        OnPropertyChanged(nameof(ActiveIsBase));
        OnPropertyChanged(nameof(ActiveNotBase));
        OnPropertyChanged(nameof(ActiveUserText));
    }

    private async Task ReloadAsync()
    {
        try
        {
            var list = await With(s => s.ListAsync());
            Rows.Clear();
            foreach (var p in list) Rows.Add(new TeachingRowVm(p));
        }
        catch (Exception ex) { Fail($"목록 로드 실패: {ex.Message}"); }
    }

    [RelayCommand]
    private async Task Capture(TeachingRowVm row)
    {
        if (!_cobot.IsConnected || IsBusy) return;
        IsBusy = true; row.IsBusy = true; Message = null;
        try
        {
            var pose = await _cobot.Rpc.GetTcpPoseInBaseAsync(RunTool);
            var joints = await _cobot.Rpc.GetActualJointPosAsync();
            if (CapUser > 0)
            {
                // 작업물 좌표계 N 기준으로 고정: 현재 프레임 T_N 을 읽어 상대 pose 로 변환해 저장.
                var tN = await _cobot.Rpc.GetWObjCoordAsync(CapUser);
                var rel = FrameMath.ToFrame(pose, tN);
                await With(s => s.SaveCaptureAsync(row.Id, pose, joints, RunTool, CapUser, rel));
                Ok($"현재 위치를 작업물 #{CapUser} 좌표계 기준으로 저장했습니다.");
            }
            else
            {
                await With(s => s.SaveCaptureAsync(row.Id, pose, joints, RunTool));
                Ok("현재 위치를 베이스 기준으로 저장했습니다.");
            }
            await ReloadAsync();
        }
        catch (Exception ex) { Fail($"저장 실패: {ex.Message}"); }
        finally { IsBusy = false; row.IsBusy = false; }
    }

    [RelayCommand]
    private async Task MoveTo(TeachingRowVm row)
    {
        var p = row.Entity;
        if (!_cobot.IsConnected || !p.IsTaught || IsBusy) return;
        IsBusy = true; row.IsBusy = true; Message = null;
        _moveCts = new CancellationTokenSource();
        try
        {
            double[] target;
            if (p.UserFrame is int n && n > 0 && p.RelX.HasValue)
            {
                // 작업물 추종: 현재(재등록된) 프레임 T_N 에 상대 pose 적용 → 베이스 목표.
                var tN = await _cobot.Rpc.GetWObjCoordAsync(n, _moveCts.Token);
                var rel = new[] { p.RelX!.Value, p.RelY!.Value, p.RelZ!.Value, p.RelRx!.Value, p.RelRy!.Value, p.RelRz!.Value };
                target = FrameMath.FromFrame(rel, tN);
            }
            else
                target = new[] { p.X!.Value, p.Y!.Value, p.Z!.Value, p.Rx!.Value, p.Ry!.Value, p.Rz!.Value };

            var rc = await _cobot.Rpc.MoveLAsync(target, tool: RunTool, user: 0, vel: Vel, ct: _moveCts.Token);
            if (rc == 0) Ok($"'{p.Name}' 위치로 이동 완료."); else Fail($"이동 실패 (rc={rc}){FairinoErrorCodes.Suffix(rc)}");
        }
        catch (OperationCanceledException) { Fail("사용자 정지"); }
        catch (Exception ex) { Fail($"이동 실패: {ex.Message}"); }
        finally { _moveCts?.Dispose(); _moveCts = null; IsBusy = false; row.IsBusy = false; }
    }

    [RelayCommand]
    private async Task ResetToBaseFrame()
    {
        if (!_cobot.IsConnected) return;
        IsBusy = true; Message = null;
        try
        {
            var rc = await _cobot.Rpc.ResetActiveFrameAsync(RunTool, 0);
            if (rc == 0) Ok("활성 좌표계를 베이스(0)로 전환했습니다."); else Fail($"활성 좌표계 전환 실패 (rc={rc}).");
        }
        catch (Exception ex) { Fail($"활성 좌표계 전환 실패: {ex.Message}"); }
        finally { IsBusy = false; RefreshStatus(); }
    }

    [RelayCommand]
    private async Task Stop()
    {
        _moveCts?.Cancel();
        try { await _cobot.StopMotionImmediateAsync(); } catch { /* 로그만 */ }
    }

    [RelayCommand]
    private async Task Add()
    {
        if (IsBusy) return;
        try
        {
            var row = await With(s => s.AddAsync(NewName, NewSurfaceId));
            NewName = ""; NewSurfaceId = 0;
            await ReloadAsync();
            Ok($"'{row.Name}' 항목을 추가했습니다 (Wall 0x{row.SurfaceId:X2}). '캡처'로 좌표를 채우세요.");
        }
        catch (Exception ex) { Fail($"항목 추가 실패: {ex.Message}"); }
    }

    [RelayCommand]
    private async Task Delete(TeachingRowVm row)
    {
        if (IsBusy) return;
        try
        {
            var ok = await With(s => s.DeleteAsync(row.Id));
            await ReloadAsync();
            if (ok) Ok("항목을 삭제했습니다."); else Fail("기본 슬롯은 삭제할 수 없습니다.");
        }
        catch (Exception ex) { Fail($"삭제 실패: {ex.Message}"); }
    }

    [RelayCommand]
    private async Task Clear(TeachingRowVm row)
    {
        if (IsBusy) return;
        try
        {
            await With(s => s.ClearAsync(row.Id));
            await ReloadAsync();
            Ok("저장된 좌표를 초기화했습니다.");
        }
        catch (Exception ex) { Fail($"초기화 실패: {ex.Message}"); }
    }

    // 이름/Wall ID 는 수정 버튼으로만 편집(실수 방지) — 버퍼 편집 → 저장/취소.
    [RelayCommand] private void BeginEdit(TeachingRowVm row) { row.EditName = row.Name; row.EditSurfaceId = row.SurfaceId; row.IsEditing = true; }
    [RelayCommand] private void CancelEdit(TeachingRowVm row) => row.IsEditing = false;

    [RelayCommand]
    private async Task SaveEdit(TeachingRowVm row)
    {
        try
        {
            await With(s => s.UpdateInfoAsync(row.Id, row.EditName, row.EditSurfaceId));
            await ReloadAsync();
            Ok($"'{row.EditName}' 항목을 저장했습니다 (Wall 0x{Math.Clamp(row.EditSurfaceId, 0, 255):X2}).");
        }
        catch (Exception ex) { Fail($"수정 실패: {ex.Message}"); }
    }

    private async Task<T> With<T>(Func<TeachingService, Task<T>> op)
    {
        using var scope = _scopeFactory.CreateScope();
        return await op(scope.ServiceProvider.GetRequiredService<TeachingService>());
    }
    private async Task With(Func<TeachingService, Task> op)
    {
        using var scope = _scopeFactory.CreateScope();
        await op(scope.ServiceProvider.GetRequiredService<TeachingService>());
    }

    private void Ok(string m) { Message = m; IsErr = false; }
    private void Fail(string m) { Message = m; IsErr = true; }

    public override void Dispose() { base.Dispose(); _moveCts?.Cancel(); }
}

/// <summary>티칭 위치 한 행(표시 + 인라인 편집 버퍼).</summary>
public sealed partial class TeachingRowVm : ObservableObject
{
    public TeachingPosition Entity { get; }
    public TeachingRowVm(TeachingPosition p) { Entity = p; }

    public int Id => Entity.Id;
    public string Name => Entity.Name;
    public int SurfaceId => Entity.SurfaceId;
    public string SurfaceHex => $"0x{Entity.SurfaceId:X2}";
    /// <summary>"0x01 · B 바닥" — Wall Code 고정 범위 밖이면 hex 만.</summary>
    public string SurfaceText => TeachingService.SurfaceLabel(Entity.SurfaceId);
    public bool IsSeed => TeachingService.IsSeedSlot(Entity.Key);
    public bool IsUser => !IsSeed;
    public bool IsTaught => Entity.IsTaught;
    public bool HasUserFrame => Entity.UserFrame is > 0;
    public string UserFrameText => Entity.UserFrame is int n && n > 0 ? $"작업물 #{n}" : "";

    public string X => F(Entity.X); public string Y => F(Entity.Y); public string Z => F(Entity.Z);
    public string Rx => F(Entity.Rx); public string Ry => F(Entity.Ry); public string Rz => F(Entity.Rz);
    public string J1 => F(Entity.J1); public string J2 => F(Entity.J2); public string J3 => F(Entity.J3);
    public string J4 => F(Entity.J4); public string J5 => F(Entity.J5); public string J6 => F(Entity.J6);

    [ObservableProperty] private bool _isEditing;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _editName = "";
    [ObservableProperty] private int _editSurfaceId;

    public bool ShowNameText => IsSeed;
    public bool ShowNameEdit => IsUser && IsEditing;
    public bool ShowNameRead => IsUser && !IsEditing;
    partial void OnIsEditingChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowNameEdit)); OnPropertyChanged(nameof(ShowNameRead));
    }

    private static string F(double? v) => v.HasValue ? v.Value.ToString("0.###") : "-";
}
