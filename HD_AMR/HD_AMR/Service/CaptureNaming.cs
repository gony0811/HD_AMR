using System.Text.RegularExpressions;

namespace HD_AMR.Service;

/// <summary>
/// 캡처 파일 일련번호 규약("NNN_…")을 한곳에서 관리한다. 카메라 캡처(<see cref="CameraService"/>)와
/// 실패 케이스 추가(LabelDataService) 등 서로 다른 저장 경로가 <b>같은 번호 체계</b>를 공유하도록 한다.
/// 접두 번호는 3자리(001…) 기본이며, 폴더 내 기존 최대 번호에서 이어서 부여한다.
/// </summary>
public static class CaptureNaming
{
    // "NNN_…" 접두(3~4자리)만 인식. 원시 타임스탬프 yyyyMMdd(8자리)는 3~4자리 뒤 밑줄이 없어 자동 구분.
    // 접두 뒤가 날짜든 hard_ 든 무관하게 번호만 읽는다.
    private static readonly Regex SeqRx = new(@"^(\d{3,4})_", RegexOptions.Compiled);

    /// <summary>폴더 내 기존 캡처의 최대 일련번호 +1(없으면 1).</summary>
    public static int NextSeq(string dir)
    {
        int max = 0;
        if (!Directory.Exists(dir)) return 1;
        foreach (var f in Directory.GetFiles(dir))
        {
            var m = SeqRx.Match(Path.GetFileName(f));
            if (m.Success && int.TryParse(m.Groups[1].Value, out var n) && n > max) max = n;
        }
        return max + 1;
    }

    /// <summary>일련번호를 3자리 접두 문자열로("001_").</summary>
    public static string Prefix(int seq) => $"{seq:D3}_";
}
