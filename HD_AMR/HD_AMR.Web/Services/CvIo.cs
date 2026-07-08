using OpenCvSharp;

namespace HD_AMR.Web.Services;

/// <summary>
/// 유니코드 안전 OpenCV 파일 I/O. <see cref="Cv2.ImRead(string,ImreadModes)"/>·
/// <see cref="Cv2.ImWrite(string,Mat,int[])"/> 는 경로를 네이티브에 ANSI 로 마샬링하므로
/// 비-ANSI 경로(예: 한글 폴더 <c>학습데이터</c>)에서 "Cannot marshal: Encountered unmappable
/// character" 로 실패한다. .NET 파일 I/O(유니코드)로 바이트를 읽고/쓰고, 인코딩·디코딩만
/// 메모리에서 OpenCV 로 수행해 이 문제를 우회한다.
/// </summary>
internal static class CvIo
{
    /// <summary>경로의 이미지를 읽어 Mat 으로 디코드(유니코드 경로 안전). 실패 시 빈 Mat.</summary>
    public static Mat ReadMat(string path, ImreadModes flags)
    {
        var bytes = File.ReadAllBytes(path);
        return Cv2.ImDecode(bytes, flags);
    }

    /// <summary>Mat 을 지정 확장자로 인코딩해 경로에 기록(유니코드 경로 안전).</summary>
    public static void WriteMat(string path, Mat mat, string ext = ".png")
    {
        Cv2.ImEncode(ext, mat, out var buf);
        File.WriteAllBytes(path, buf);
    }
}
