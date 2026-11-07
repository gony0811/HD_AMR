using HD.AMR.App.Models;
using HD.AMR.App.Service.Vision;
using Microsoft.Extensions.Logging;

namespace HD.AMR.App.Service.Sequence.Steps;

public enum BeadAssistKind
{
    /// <summary>조건에 해당하지 않아 묻지 않음 — 자동 결과 그대로 판정.</summary>
    NotAsked,
    /// <summary>스냅샷을 만들 수 없어 묻지 못함(IR/Depth 프레임 없음) — 자동 결과 그대로 판정.</summary>
    Unavailable,
    /// <summary>사람이 그린 선으로 측정 완료.</summary>
    Manual,
    /// <summary>사람이 "자동 결과로 진행"을 선택.</summary>
    AutoAccepted,
    /// <summary>응답 없음(시간 초과)이지만 자동 결과가 있어 그대로 진행.</summary>
    AutoAfterTimeout,
    /// <summary>사람이 실패 처리.</summary>
    Rejected,
    /// <summary>응답 없음 + 자동 결과도 없음.</summary>
    NoAnswer,
}

public sealed record BeadAssistOutcome(BeadAssistKind Kind, PeakMeasurement? Measurement, string Note)
{
    /// <summary>자동 결과 판정 로직을 그대로 타야 하는 경우.</summary>
    public bool UseAutoPath => Kind is BeadAssistKind.NotAsked or BeadAssistKind.Unavailable
        or BeadAssistKind.AutoAccepted or BeadAssistKind.AutoAfterTimeout;
}

/// <summary>
/// 시퀀스 비드 찾기의 <b>수동 비드 입력</b>. 파라미터 조건(<see cref="BeadManualSettings"/>)에 해당하면 현재 프레임을
/// 고정해 UI 팝업(<see cref="BeadManualLabelHub"/>)으로 비드선을 받고, 그 선으로 d 를 측정해 시퀀스를 계속한다.
/// 입력한 선은 같은 프레임과 함께 캡처 폴더에 학습 데이터(<c>_line.json</c>·<c>_maskdraft.png</c>)로 저장된다.
/// 조건이 off 면 아무 것도 하지 않는다(종전 동작).
/// </summary>
public sealed class BeadManualAssist
{
    /// <summary>팝업 조그 이동 뒤 프레임을 다시 찍기 전 대기 [ms].</summary>
    internal const int RecaptureSettleMs = 500;

    private readonly BeadManualLabelHub _hub;
    private readonly WeldTrackingService _weld;
    private readonly CameraService _camera;
    private readonly ParameterService _param;
    private readonly LabelDataService _labels;
    private readonly ILogger<BeadManualAssist> _logger;

    public BeadManualAssist(BeadManualLabelHub hub, WeldTrackingService weld, CameraService camera,
        ParameterService param, LabelDataService labels, ILogger<BeadManualAssist> logger)
    {
        _hub = hub;
        _weld = weld;
        _camera = camera;
        _param = param;
        _labels = labels;
        _logger = logger;
    }

    /// <param name="stage">표시용 단계 이름(예: "Bead1 찾기").</param>
    /// <param name="autoOk">자동 결과를 측정으로 쓸 수 있는지(스텝의 기존 판정 기준).</param>
    /// <param name="autoFailNote">자동 실패 사유(팝업 표시용). autoOk 면 null.</param>
    public async Task<BeadAssistOutcome> RunAsync(int peakId, string stage, PeakMeasurement? autoM,
        WeldDetectionResult? detect, bool autoOk, string? autoFailNote, RoiRect peakRoi, RoiRect beadRoi,
        SequenceContext context, CancellationToken ct)
    {
        var trigger = BeadManualSettings.ParseTrigger(await _param.GetAsync(BeadManualSettings.TriggerKey))
                      ?? BeadManualSettings.DefaultTrigger;
        var minConf = Math.Clamp(await _param.GetDoubleAsync(BeadManualSettings.MinConfidenceKey)
                                 ?? BeadManualSettings.DefaultMinConfidence, 0, 1);
        var timeoutSec = Math.Clamp(await _param.GetIntAsync(BeadManualSettings.TimeoutSecKey)
                                    ?? BeadManualSettings.DefaultTimeoutSec, 10, 3600);
        double conf = detect?.Confidence ?? 0;

        if (!BeadManualSettings.ShouldAsk(trigger, autoOk, conf, minConf))
            return new(BeadAssistKind.NotAsked, autoM, "");

        string reason = !autoOk
            ? $"자동 검출 실패 — {autoFailNote ?? "결과 없음"}"
            : conf < minConf
                ? $"자동 검출 신뢰도 낮음 — coverage {conf:P0} < {minConf:P0}"
                : $"확인 요청(매번) — 자동 coverage {conf:P0}";

        // 팝업에서 조그로 코봇을 옮기면(Recapture) 현재 자리에서 프레임을 다시 찍어 새 요청을 올린다. 옮긴 뒤에는
        // 자동 결과가 처음 자리 기준이라 쓸 수 없다 — "자동 결과로 진행"을 막고, 시간 초과도 실패로 본다.
        ManualBeadSnapshot? snap;
        ManualBeadAnswer answer;
        var jogs = 0;
        while (true)
        {
            var useAuto = autoOk && jogs == 0;
            snap = await _weld.PrepareManualBeadAsync(peakId, peakRoi, beadRoi, detect, ct);
            if (snap is null)
            {
                _logger.LogWarning("[{Stage}] 수동 비드 입력 불가 — IR/Depth 프레임 없음. 자동 결과로 판정.", stage);
                return jogs == 0
                    ? new(BeadAssistKind.Unavailable, autoM, " (수동 입력 불가: 프레임 없음)")
                    : new(BeadAssistKind.NoAnswer, null, $" — 조그 {jogs}회 후 프레임을 다시 찍지 못했습니다");
            }

            string defaultMod = snap.AutoModality == "rgb" && snap.ColorGuide is not null ? "rgb" : "ir";
            var req = new ManualBeadRequest
            {
                PeakId = peakId,
                Stage = stage,
                Reason = jogs == 0 ? reason : $"{reason} · 조그 {jogs}회 후 다시 찍은 화면(자동 결과 사용 불가)",
                AutoUsable = useAuto,
                DefaultModality = defaultMod,
                IrPng = snap.IrPng,
                ColorPng = snap.ColorPng,
                IrGuide = snap.IrGuide,
                ColorGuide = snap.ColorGuide,
                DeadlineUtc = DateTime.UtcNow.AddSeconds(timeoutSec),
                Tool = context.Tool,
                JogCount = jogs,
            };

            context.Progress?.Invoke($"{stage}: 수동 비드 입력 대기(최대 {timeoutSec}s) — {req.Reason}");
            _logger.LogInformation("[{Stage}] 수동 비드 입력 요청: {Reason} (조건={Trigger}, 대기 {Sec}s, 조그 {Jogs}회)",
                stage, req.Reason, BeadManualSettings.ToParam(trigger), timeoutSec, jogs);

            answer = await _hub.AskAsync(req, TimeSpan.FromSeconds(timeoutSec), ct);
            if (answer.Decision != ManualBeadDecision.Recapture) break;

            jogs++;
            _logger.LogInformation("[{Stage}] 팝업 조그로 코봇 이동 — 프레임 다시 찍기 ({Jogs}회)", stage, jogs);
            await Task.Delay(RecaptureSettleMs, ct);   // 이동 직후 프레임 흔들림·지연 대기

            // 새 자리에서 자동 검출을 다시 돌려 찾은 선을 팝업에 초안으로 보여준다(측정 슬롯도 새 자리 값으로 갱신 —
            // 이후 결과는 수동 확정으로만 쓰므로 영향 없음). 실패해도 수동 입력은 계속한다.
            try { detect = (await _weld.CapturePeakAsync(peakId, peakRoi, beadRoi, ct)).Detect; }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[{Stage}] 조그 후 자동 검출 실패 — 선 초안 없이 수동 입력", stage);
                detect = null;
            }
        }
        autoOk = autoOk && jogs == 0;

        switch (answer.Decision)
        {
            case ManualBeadDecision.Manual:
            {
                var m = await _weld.ApplyManualBeadAsync(snap, answer.Modality, answer.Points, ct);
                if (m is null)
                    return new(BeadAssistKind.Rejected, null,
                        " — 수동 입력 선이 진행축 방향으로 충분히 길지 않아 측정할 수 없습니다");
                var saved = await SaveTrainingAsync(snap, answer.Modality, answer.Points, ct);
                _logger.LogInformation("[{Stage}] 수동 비드 입력 적용: 영상={Mod}, d={DPx:0.0}px, 학습 데이터={Saved}",
                    stage, answer.Modality, m.DPixel, saved);
                var jogNote = jogs > 0 ? $", 조그 {jogs}회 후" : "";
                return new(BeadAssistKind.Manual, m, $" [수동 입력 {answer.Modality.ToUpperInvariant()}{jogNote}, {saved}]");
            }
            case ManualBeadDecision.UseAuto when autoOk:
                return new(BeadAssistKind.AutoAccepted, autoM, " [작업자: 자동 결과로 진행]");
            case ManualBeadDecision.Timeout when autoOk:
                return new(BeadAssistKind.AutoAfterTimeout, autoM, " [수동 입력 시간 초과 — 자동 결과로 진행]");
            case ManualBeadDecision.Timeout:
                return new(BeadAssistKind.NoAnswer, null, $" — 수동 입력 대기 {timeoutSec}s 초과");
            case ManualBeadDecision.Canceled:
                ct.ThrowIfCancellationRequested();
                return new(BeadAssistKind.NoAnswer, null, " — 수동 입력 요청이 취소되었습니다");
            default:
                return new(BeadAssistKind.Rejected, null, " — 작업자가 실패 처리");
        }
    }

    /// <summary>스냅샷 프레임 + 선 라벨 + 마스크를 캡처 폴더에 저장. 실패해도 시퀀스는 계속한다(결과 문자열만 반환).</summary>
    private async Task<string> SaveTrainingAsync(ManualBeadSnapshot s, string modality,
        IReadOnlyList<PixelPoint> points, CancellationToken ct)
    {
        try
        {
            if (await _labels.GetDirAsync() is null) return "캡처 폴더 미설정 — 학습 데이터 저장 생략";
            var r = await ManualBeadLabelWriter.SaveAsync(_camera, _labels, s, modality, points, $"seqP{s.PeakId}", ct);
            return $"학습 데이터 저장 {r.Summary}";
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "수동 비드 입력 학습 데이터 저장 실패 — 측정은 계속 사용");
            return $"학습 데이터 저장 실패: {ex.Message}";
        }
    }
}
