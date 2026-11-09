using System.Globalization;
using HD.AMR.App.Models;

namespace HD.AMR.App.Service.Sequence;

/// <summary>
/// 시퀀스 비드 찾기에서 사람에게 비드선 입력을 요청하는 조건. 파라미터 <see cref="BeadManualSettings.TriggerKey"/>.
/// 학습 데이터가 충분히 쌓여 자동 검출을 믿을 수 있게 되면 <see cref="Off"/> 로 끈다(종전 동작과 동일).
/// </summary>
public enum BeadManualTrigger
{
    /// <summary>사용 안 함 — 자동 검출 결과만 쓴다.</summary>
    Off,
    /// <summary>자동 검출 실패(검출 0건·직선피팅 실패)일 때만.</summary>
    OnFailure,
    /// <summary>실패 또는 coverage 가 <see cref="BeadManualSettings.MinConfidenceKey"/> 미만일 때.</summary>
    OnLowConfidence,
    /// <summary>매번 — 자동 결과가 있어도 사람이 확인(확정/수정)한다.</summary>
    Always,
}

/// <summary>수동 비드 입력 파라미터 키·기본값·판정. 순수 로직(테스트 대상).</summary>
public static class BeadManualSettings
{
    public const string TriggerKey = "Sequence.BeadManual.Trigger";
    public const string MinConfidenceKey = "Sequence.BeadManual.MinConfidence";
    public const string TimeoutSecKey = "Sequence.BeadManual.TimeoutSec";
    public const string JogVelPctKey = "Sequence.BeadManual.JogVelPct";
    public const string JogStepMmKey = "Sequence.BeadManual.JogStepMm";

    public const BeadManualTrigger DefaultTrigger = BeadManualTrigger.OnLowConfidence;
    public const double DefaultMinConfidence = 0.5;
    public const int DefaultTimeoutSec = 300;

    /// <summary>팝업 조그 속도 [%] 기본값과 허용 범위.</summary>
    public const double DefaultJogVelPct = 20, MinJogVelPct = 1, MaxJogVelPct = 100;

    /// <summary>팝업 조그 1회 이동량 [mm] 기본값과 허용 범위.</summary>
    public const double DefaultJogStepMm = 20, MinJogStepMm = 0.5, MaxJogStepMm = 100;

    public static double ClampJogVelPct(double v) => Math.Clamp(v, MinJogVelPct, MaxJogVelPct);
    public static double ClampJogStepMm(double v) => Math.Clamp(v, MinJogStepMm, MaxJogStepMm);

    /// <summary>파라미터 값 → 조건. 대소문자 무시, 한글 별칭 허용. 알 수 없는 값이면 null.</summary>
    public static BeadManualTrigger? ParseTrigger(string? value)
    {
        var v = value?.Trim().ToLowerInvariant();
        return v switch
        {
            "off" or "none" or "false" or "0" or "사용안함" or "끔" => BeadManualTrigger.Off,
            "failure" or "onfailure" or "fail" or "실패" => BeadManualTrigger.OnFailure,
            "lowconfidence" or "onlowconfidence" or "low" or "저신뢰" => BeadManualTrigger.OnLowConfidence,
            "always" or "on" or "true" or "1" or "매번" or "항상" => BeadManualTrigger.Always,
            _ => null,
        };
    }

    public static string ToParam(BeadManualTrigger t) => t switch
    {
        BeadManualTrigger.Off => "off",
        BeadManualTrigger.OnFailure => "failure",
        BeadManualTrigger.OnLowConfidence => "lowConfidence",
        _ => "always",
    };

    /// <summary>사람에게 물어야 하는지. <paramref name="autoOk"/>=자동 결과를 측정에 쓸 수 있는 상태.</summary>
    public static bool ShouldAsk(BeadManualTrigger trigger, bool autoOk, double confidence, double minConfidence)
        => trigger switch
        {
            BeadManualTrigger.Off => false,
            BeadManualTrigger.OnFailure => !autoOk,
            BeadManualTrigger.OnLowConfidence => !autoOk || confidence < minConfidence,
            _ => true,
        };

    /// <summary>파라미터 시드 행(ParameterService.SeedDefaultsAsync 가 없는 키만 추가).</summary>
    public static IEnumerable<(string Name, string Value, string Desc)> SeedDefaults() =>
    [
        (TriggerKey, ToParam(DefaultTrigger),
            "Bead 찾기 수동 입력 팝업 조건 — off(사용 안 함) / failure(자동 검출 실패 시) / " +
            "lowConfidence(실패 또는 coverage 가 MinConfidence 미만) / always(매번 확인). " +
            "입력한 비드선은 학습 데이터(라벨링 폴더의 _line.json 선 라벨)로 저장되고 그 선으로 시퀀스를 계속한다."),
        (MinConfidenceKey, DefaultMinConfidence.ToString(CultureInfo.InvariantCulture),
            "Bead 찾기 수동 입력 — lowConfidence 조건의 coverage 임계(0~1). 이 값 미만이면 팝업."),
        (TimeoutSecKey, DefaultTimeoutSec.ToString(CultureInfo.InvariantCulture),
            "Bead 찾기 수동 입력 대기 시간(초). 응답이 없으면 자동 결과가 있으면 그걸로 진행, 없으면 실패 처리. 10~3600."),
        (JogVelPctKey, DefaultJogVelPct.ToString(CultureInfo.InvariantCulture),
            "수동 비드 입력 팝업 조그 속도(%) — 툴 좌표계 평면 이동. 1~100. 팝업에서 바꾸면 이 값도 바뀐다."),
        (JogStepMmKey, DefaultJogStepMm.ToString(CultureInfo.InvariantCulture),
            "수동 비드 입력 팝업 조그 1회 이동량(mm) — 좌/우/상/하 버튼 한 번에 움직이는 거리. 0.5~100. 팝업에서 바꾸면 이 값도 바뀐다."),
    ];
}

/// <summary>
/// 팝업에 겹쳐 그릴 가이드(한 영상 기준 픽셀 좌표). 진행축이 가로면 Peak 선은 x=<see cref="ProgressPos"/> 세로선,
/// 기준선은 y=<see cref="RefCross"/> 가로선이다(세로 진행이면 반대).
/// </summary>
public sealed record ManualBeadGuide(
    string Modality,
    int Width,
    int Height,
    bool Horizontal,
    double ProgressPos,
    double RefCross,
    bool PeakFound,
    double? PeakCrossStart,
    double? PeakCrossEnd,
    RoiRect? BeadRoi,
    RoiRect? PeakRoi,
    IReadOnlyList<PixelPoint>? AutoLine,
    bool Calibrated = true)
{
    /// <summary>캘리브레이션 없는 컬러 영상용 — 안내선(Peak·기준선·ROI)과 d 계산 없이 라벨만 그린다.</summary>
    public static ManualBeadGuide LabelOnly(string modality, int width, int height, bool horizontal,
        IReadOnlyList<PixelPoint>? autoLine)
        => new(modality, width, height, horizontal, (horizontal ? width : height) / 2.0, (horizontal ? height : width) / 2.0,
            false, null, null, null, null, autoLine, Calibrated: false);
}

/// <summary>UI 에 보내는 수동 비드 입력 요청.</summary>
public sealed class ManualBeadRequest
{
    public Guid Id { get; } = Guid.NewGuid();
    public int PeakId { get; init; }
    public string Stage { get; init; } = "";
    /// <summary>팝업 사유(예: "자동 검출 실패 — coverage 12%").</summary>
    public string Reason { get; init; } = "";
    /// <summary>자동 결과를 그대로 쓸 수 있는지("자동 결과 사용" 버튼 활성).</summary>
    public bool AutoUsable { get; init; }
    /// <summary>처음 보여줄 영상("ir"/"rgb") — 현재 검출 입력 모드.</summary>
    public string DefaultModality { get; init; } = "ir";
    public byte[] IrPng { get; init; } = [];
    public byte[]? ColorPng { get; init; }
    public ManualBeadGuide IrGuide { get; init; } = null!;
    public ManualBeadGuide? ColorGuide { get; init; }
    public DateTime DeadlineUtc { get; init; }
    /// <summary>시퀀스가 쓰는 공구 번호 — 팝업 조그가 같은 툴 좌표계로 움직이도록.</summary>
    public int? Tool { get; init; }
    /// <summary>이 요청 전에 팝업 조그로 움직인 횟수(0 = 처음 측정 자리).</summary>
    public int JogCount { get; init; }
}

public enum ManualBeadDecision
{
    /// <summary>사람이 그린 선 사용.</summary>
    Manual,
    /// <summary>자동 검출 선을 정답으로 확인.</summary>
    UseAuto,
    /// <summary>비드 없음/실패 처리.</summary>
    Reject,
    Timeout,
    Canceled,
    /// <summary>팝업에서 조그로 코봇을 옮겼다 — 현재 자리에서 프레임을 다시 찍어 새 요청을 올린다.</summary>
    Recapture,
}

/// <summary>UI 응답. <see cref="Points"/> 는 <see cref="Modality"/> 영상 픽셀 좌표(Manual 일 때만 의미).</summary>
public sealed record ManualBeadAnswer(ManualBeadDecision Decision, string Modality, IReadOnlyList<PixelPoint> Points)
{
    public static ManualBeadAnswer Of(ManualBeadDecision d) => new(d, "ir", Array.Empty<PixelPoint>());
}

/// <summary>
/// 수동 비드 입력 요청 허브 — <b>싱글톤</b>. 시퀀스 스텝이 <see cref="AskAsync"/> 로 요청을 올리고 응답을 기다리며,
/// UI(메인 창)는 <see cref="Changed"/> 를 구독해 <see cref="Pending"/> 이 생기면 팝업을 띄우고 <see cref="Submit"/> 한다.
/// 시퀀스는 동시에 1개만 돌므로 대기 요청도 1개다. 대기 시간 초과·시퀀스 정지 시 팝업은 자동으로 닫힌다.
/// </summary>
public sealed class BeadManualLabelHub
{
    private readonly object _lock = new();
    private ManualBeadRequest? _pending;
    private TaskCompletionSource<ManualBeadAnswer>? _tcs;

    /// <summary>대기 요청이 생기거나 사라질 때. UI 스레드가 아닐 수 있다.</summary>
    public event Action? Changed;

    public ManualBeadRequest? Pending { get { lock (_lock) return _pending; } }

    public async Task<ManualBeadAnswer> AskAsync(ManualBeadRequest req, TimeSpan timeout, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<ManualBeadAnswer>(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<ManualBeadAnswer>? previous;
        lock (_lock)
        {
            previous = _tcs;
            _pending = req;
            _tcs = tcs;
        }
        previous?.TrySetResult(ManualBeadAnswer.Of(ManualBeadDecision.Canceled));
        Changed?.Invoke();

        using var timer = new CancellationTokenSource(timeout);
        using var r1 = timer.Token.Register(() => tcs.TrySetResult(ManualBeadAnswer.Of(ManualBeadDecision.Timeout)));
        using var r2 = ct.Register(() => tcs.TrySetResult(ManualBeadAnswer.Of(ManualBeadDecision.Canceled)));
        try
        {
            var answer = await tcs.Task.ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return answer;
        }
        finally
        {
            bool cleared = false;
            lock (_lock)
            {
                if (ReferenceEquals(_tcs, tcs)) { _tcs = null; _pending = null; cleared = true; }
            }
            if (cleared) Changed?.Invoke();
        }
    }

    /// <summary>UI 응답. 이미 끝난(시간 초과·다른 요청) 요청이면 false.</summary>
    public bool Submit(Guid requestId, ManualBeadAnswer answer)
    {
        TaskCompletionSource<ManualBeadAnswer>? tcs;
        lock (_lock)
        {
            if (_pending?.Id != requestId) return false;
            tcs = _tcs;
        }
        return tcs?.TrySetResult(answer) ?? false;
    }
}

/// <summary>사람이 찍은 비드선 점들의 기하 계산(순수 함수).</summary>
public static class ManualBeadGeometry
{
    /// <summary>편집 초안의 조절점 수 상한 — 자동 검출 중심선은 수백 점이라 그대로는 드래그로 고칠 수 없다.</summary>
    public const int EditDraftMaxPoints = 8;

    /// <summary>
    /// 자동 검출 중심선을 편집용 조절점으로 줄인다 — 양 끝점을 지키고 그 사이를 점 개수 기준 등간격으로 고른다.
    /// <paramref name="maxPoints"/> 이하이면 그대로. 2 미만 상한은 2 로 본다.
    /// </summary>
    public static List<PixelPoint> SimplifyForEdit(IReadOnlyList<PixelPoint> pts, int maxPoints = EditDraftMaxPoints)
    {
        maxPoints = Math.Max(2, maxPoints);
        if (pts.Count <= maxPoints) return pts.ToList();
        var result = new List<PixelPoint>(maxPoints);
        for (int k = 0; k < maxPoints; k++)
            result.Add(pts[(int)Math.Round(k * (pts.Count - 1) / (double)(maxPoints - 1))]);
        return result;
    }

    /// <summary>
    /// 점들을 진행축 좌표 p 에 대한 직선 cross = a·p + b 로 최소제곱 피팅해 <paramref name="progressPos"/> 에서의 cross 를 반환.
    /// 2점 미만이거나 점들이 진행축으로 펼쳐지지 않았으면(진행축과 수직인 선) null.
    /// </summary>
    public static double? CrossAt(IReadOnlyList<PixelPoint> pts, bool horizontal, double progressPos)
    {
        if (pts.Count < 2) return null;
        double n = pts.Count, sp = 0, sc = 0, spp = 0, spc = 0;
        foreach (var q in pts)
        {
            double p = horizontal ? q.X : q.Y, c = horizontal ? q.Y : q.X;
            sp += p; sc += c; spp += p * p; spc += p * c;
        }
        double den = n * spp - sp * sp;
        double spread = Math.Sqrt(Math.Max(0, den)) / n;   // 진행축 표준편차(px)
        if (spread < 2.0) return null;
        double a = (n * spc - sp * sc) / den;
        double b = (sc - a * sp) / n;
        return a * progressPos + b;
    }
}
