using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HD.AMR.App.Communication.Vda5050;
using HD.AMR.App.Service;

namespace HD.AMR.Desktop.ViewModels;

/// <summary>
/// ACS MQTT 브로커 설정 창(상단 ACS 칩·대시보드 ACS 카드에서 연다) — IP 주소·포트를 입력해 저장하고 바로 연결한다.
/// 주소는 <see cref="AcsBrokerStore"/> 파일에 남아 다음 실행에도 쓰인다. 연결 상태는 창이 1초마다 <see cref="Refresh"/> 로 갱신한다.
/// </summary>
public sealed partial class AcsBrokerViewModel : ObservableObject
{
    private readonly Vda5050AdapterService _vda;
    private DateTime? _requestedAt;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    private string _host;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    private string _portText;

    /// <summary>입력 검증·적용 결과 안내(없으면 빈 문자열).</summary>
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private bool _messageIsError;

    public AcsBrokerViewModel(Vda5050AdapterService vda)
    {
        _vda = vda;
        _host = vda.BrokerHost;
        _portText = vda.BrokerPort.ToString();
    }

    public bool BrokerOn => _vda.IsBrokerConnected;
    public string BrokerStatusText => !_vda.IsEnabled ? "어댑터 꺼짐" : _vda.IsBrokerConnected ? "접속" : "미접속";
    public string CurrentText => $"현재 브로커 {_vda.BrokerHost}:{_vda.BrokerPort}";

    public bool AcsOn => _vda.IsBrokerConnected &&
                         (_vda.AcsConnectionLiveness == AcsLiveness.Online || _vda.AcsRecentlyActive) &&
                         _vda.AcsConnectionLiveness is not (AcsLiveness.Offline or AcsLiveness.Broken);
    public string AcsStatusText =>
        !_vda.IsBrokerConnected ? "-"
        : _vda.AcsConnectionLiveness == AcsLiveness.Offline ? "OFFLINE"
        : _vda.AcsConnectionLiveness == AcsLiveness.Broken ? "CONNECTIONBROKEN"
        : _vda.AcsConnectionLiveness == AcsLiveness.Online || _vda.AcsRecentlyActive ? "연결" : "대기(생존 신호 없음)";

    /// <summary>접속 진행·실패 안내 — 접속되면 비운다.</summary>
    public string DetailText
    {
        get
        {
            if (!_vda.IsEnabled) return "VDA5050 어댑터가 꺼져 있습니다(appsettings.json 의 Vda5050:Enabled). 주소는 저장되지만 접속하지 않습니다.";
            if (_vda.IsBrokerConnected) return "브로커에 접속했습니다. ACS 가 같은 브로커에 붙어 있으면 'ACS' 가 연결로 바뀝니다.";
            if (_vda.LastConnectError is { Length: > 0 } err) return $"접속 실패 — {err} (자동으로 다시 시도합니다)";
            return _requestedAt is not null ? "접속 중…" : "브로커에 접속하지 못했습니다(자동으로 다시 시도합니다).";
        }
    }

    public string StoreHint => $"주소는 {AcsBrokerStore.FilePath} 에 저장되어 다음 실행에도 쓰입니다(appsettings.json 값보다 우선).";

    private bool CanConnect => !string.IsNullOrWhiteSpace(Host) && !string.IsNullOrWhiteSpace(PortText);

    /// <summary>저장 후 연결 — 같은 주소면 다시 접속만 한다.</summary>
    [RelayCommand(CanExecute = nameof(CanConnect))]
    private void Connect()
    {
        if (!int.TryParse(PortText.Trim(), out var port))
        {
            Message = "포트는 숫자로 입력하세요(기본 1883).";
            MessageIsError = true;
            return;
        }

        var (applied, note) = _vda.SetBroker(Host, port);
        if (applied)
        {
            _requestedAt = DateTime.Now;
            Host = _vda.BrokerHost;
            PortText = _vda.BrokerPort.ToString();
            Message = note ?? $"{_vda.BrokerHost}:{_vda.BrokerPort} 로 저장하고 연결을 시작했습니다.";
            MessageIsError = note is not null;
        }
        else
        {
            Message = note ?? "적용하지 못했습니다.";
            MessageIsError = true;
        }
        Refresh();
    }

    /// <summary>연결 상태 표시 갱신(창의 타이머가 호출).</summary>
    public void Refresh()
    {
        if (_vda.IsBrokerConnected) _requestedAt = null;
        OnPropertyChanged(nameof(BrokerOn));
        OnPropertyChanged(nameof(BrokerStatusText));
        OnPropertyChanged(nameof(CurrentText));
        OnPropertyChanged(nameof(AcsOn));
        OnPropertyChanged(nameof(AcsStatusText));
        OnPropertyChanged(nameof(DetailText));
    }
}
