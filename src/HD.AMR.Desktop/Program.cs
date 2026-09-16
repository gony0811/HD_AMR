using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using HD.AMR.Desktop.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace HD.AMR.Desktop;

internal static class Program
{
    // Avalonia 가 App 을 매개변수 없이 생성하므로, DI 컨테이너는 정적으로 노출해 App 이 참조한다.
    public static IServiceProvider Services { get; private set; } = default!;

    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized yet.
    [STAThread]
    public static void Main(string[] args)
    {
        // 기존 HD.AMR.Web 과 동일하게 Generic Host 위에 하드웨어 서비스(싱글톤 + HostedService)를 얹는다.
        var builder = Host.CreateApplicationBuilder(args);
        // 종료 시 호스티드 서비스(AMR/Cobot 등) 정리가 길어져도 프로세스가 매달리지 않도록
        // 호스트 종료 제한 시간을 짧게 둔다(기본 30초 → 2초). 초과분은 강제 진행.
        builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(2));
        builder.Services.AddHdAmrServices(builder.Configuration);
        var host = builder.Build();

        // DB 초기화(EnsureCreated + 후방호환 스키마 + 시드) 1회 실행 — 기존 Web Program.cs 로직 이식.
        DatabaseInitializer.Initialize(host.Services);

        Services = host.Services;

        // 종료 신호(SIGINT=Ctrl+C, SIGTERM=kill)를 잡아 Avalonia 데스크톱 수명을 UI 스레드에서 종료한다.
        // StartWithClassicDesktopLifetime 은 메인 스레드를 블로킹하고 신호를 처리하지 않으므로,
        // 이 처리가 없으면 터미널에서 Ctrl+C/종료가 먹히지 않아 강제 종료해야 한다.
        using var sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, OnStopSignal);
        using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, OnStopSignal);

        try
        {
            host.Start();
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            // 호스티드 서비스 정리에 자체 하드 타임아웃을 건다. 일부 클라이언트의 Disconnect/StopAsync 는
            // 동기 블로킹(미연결 소켓 정리 대기 등)이라 호스트의 ShutdownTimeout 으로도 강제 중단되지
            // 않는다 — 제한 시간 내 끝나지 않으면 정리를 포기하고 아래 Environment.Exit 로 강제 종료한다.
            // (종료 시 영속화해야 할 상태는 없으며, 소켓 종료는 프로세스 종료로 정리된다.)
            try { host.StopAsync().Wait(TimeSpan.FromSeconds(2.5)); }
            catch { /* 정리 실패는 종료를 막지 않는다 */ }
        }

        // 잔여 비관리/블로킹 스레드(소켓 종료 대기, 네이티브 카메라/OpenCV 등)가 남아도 프로세스가
        // 확실히 끝나도록 하는 최종 보장.
        Environment.Exit(0);
    }

    // 신호 수신 시: OS 기본 종료를 취소하고 Avalonia 를 정상 종료 → 메인 스레드 블로킹 해제 → finally 실행.
    private static void OnStopSignal(PosixSignalContext ctx)
    {
        ctx.Cancel = true;   // OS 기본 종료를 취소하고 우리가 정상 종료 경로로 내려간다.
        Dispatcher.UIThread.Post(() =>
            (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown());
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
