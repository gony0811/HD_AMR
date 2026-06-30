using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace HD_AMR.Web.Services;

/// <summary>
/// Windows 네이티브 폴더 선택 대화상자(IFileOpenDialog, FOS_PICKFOLDERS)를 띄운다.
/// 이 앱은 로컬 Windows 에서 호스팅되므로 서버=사용자 PC 라, 대화상자가 사용자 화면에 뜬다.
/// 비-Windows 에서는 null 을 돌려주어(프로젝트 TFM 은 net8.0 유지) 크로스플랫폼 빌드를 깨지 않는다.
/// COM 은 STA 스레드를 요구하므로 전용 STA 스레드에서 실행한다.
/// </summary>
public static class NativeFolderDialog
{
    /// <summary>폴더를 고르면 전체 경로, 취소/비Windows/실패면 null.</summary>
    public static string? PickFolder()
    {
        if (!OperatingSystem.IsWindows()) return null;
        string? result = null;
        var t = new Thread(() => result = ShowDialog());
        t.SetApartmentState(ApartmentState.STA);
        t.IsBackground = true;
        t.Start();
        t.Join();
        return result;
    }

    [SupportedOSPlatform("windows")]
    private static string? ShowDialog()
    {
        IFileOpenDialog? dialog = null;
        try
        {
            dialog = (IFileOpenDialog)new FileOpenDialogRcw();
            dialog.GetOptions(out uint opts);
            dialog.SetOptions(opts | FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM);
            int hr = dialog.Show(IntPtr.Zero);
            if (hr != 0) return null;   // 취소 등(HRESULT != S_OK)
            dialog.GetResult(out IShellItem item);
            item.GetDisplayName(SIGDN_FILESYSPATH, out IntPtr p);
            string? path = Marshal.PtrToStringUni(p);
            Marshal.FreeCoTaskMem(p);
            Marshal.ReleaseComObject(item);
            return path;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (dialog is not null) Marshal.ReleaseComObject(dialog);
        }
    }

    private const uint FOS_PICKFOLDERS = 0x20;
    private const uint FOS_FORCEFILESYSTEM = 0x40;
    private const uint SIGDN_FILESYSPATH = 0x80058000;

    // CLSID_FileOpenDialog
    [ComImport, Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")]
    private class FileOpenDialogRcw { }

    // IFileOpenDialog : IFileDialog : IModalWindow — vtable 순서를 정확히 맞춰야 한다.
    // 호출하지 않는 메서드는 자리(slot)만 맞추는 placeholder(파라미터 없는 void)로 둔다.
    [ComImport, Guid("d57c7288-d4ad-4768-be02-9d969532d960"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOpenDialog
    {
        // IModalWindow
        [PreserveSig] int Show(IntPtr parent);                       // 1
        // IFileDialog
        void _SetFileTypes();                                        // 2
        void _SetFileTypeIndex();                                    // 3
        void _GetFileTypeIndex();                                    // 4
        void _Advise();                                             // 5
        void _Unadvise();                                          // 6
        void SetOptions(uint fos);                                   // 7
        void GetOptions(out uint pfos);                              // 8
        void _SetDefaultFolder();                                    // 9
        void _SetFolder();                                          // 10
        void _GetFolder();                                         // 11
        void _GetCurrentSelection();                                // 12
        void _SetFileName();                                       // 13
        void _GetFileName();                                       // 14
        void _SetTitle();                                          // 15
        void _SetOkButtonLabel();                                   // 16
        void _SetFileNameLabel();                                   // 17
        void GetResult([MarshalAs(UnmanagedType.Interface)] out IShellItem ppsi); // 18
        // (이하 AddPlace 등은 호출하지 않으므로 선언 생략)
    }

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void _BindToHandler();                                       // 1
        void _GetParent();                                          // 2
        void GetDisplayName(uint sigdnName, out IntPtr ppszName);     // 3
        // (GetAttributes/Compare 생략)
    }
}
