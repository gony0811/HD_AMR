namespace HD.AMR.App.Service;

/// <summary>
/// 운전자에게 보여줄 도달·순회 경고(싱글톤). 장치 HEAVY 알람(<see cref="DeviceAlarmState"/>)과 일부러 분리한다 —
/// 그 상태는 통신 두절을 래치해 ACS order 를 RESET 전까지 거절하므로, 용접선 한 건이 팔 밖에 있다는 이유로
/// 장비 전체를 잠그면 안 된다. 화면 배너와 운영 로그가 이 객체를 함께 본다. 확인 버튼으로 지운다.
/// </summary>
public sealed class OperatorAlarmState
{
    private readonly object _lock = new();
    private OperatorAlarm? _current;

    /// <summary>배너가 바뀌면 발생(임의 스레드). UI 는 디스패처로 마샬링할 것.</summary>
    public event Action? Changed;

    public OperatorAlarm? Current
    {
        get { lock (_lock) return _current; }
    }

    /// <summary>이전 경고를 이 내용으로 바꾼다. <paramref name="isError"/> 면 시험이동 불가(적색), 아니면 경고(황색).</summary>
    public void Raise(string text, bool isError)
    {
        var line = $"{text} ({DateTime.Now:HH:mm:ss})";
        lock (_lock) _current = new OperatorAlarm(line, isError, DateTime.UtcNow);
        Changed?.Invoke();
    }

    public void Clear()
    {
        bool had;
        lock (_lock)
        {
            had = _current is not null;
            _current = null;
        }
        if (had) Changed?.Invoke();
    }
}

/// <param name="Text">배너에 그대로 보여줄 문장(시각 포함).</param>
/// <param name="IsError">true = 코봇 시험이동 불가(적색). false = 자동 시퀀스 도달 경고(황색).</param>
public sealed record OperatorAlarm(string Text, bool IsError, DateTime RaisedUtc);
