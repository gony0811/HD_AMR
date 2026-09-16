using Avalonia;
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
        builder.Services.AddHdAmrServices(builder.Configuration);
        var host = builder.Build();

        // DB 초기화(EnsureCreated + 후방호환 스키마 + 시드) 1회 실행 — 기존 Web Program.cs 로직 이식.
        DatabaseInitializer.Initialize(host.Services);

        Services = host.Services;

        try
        {
            host.Start();
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            // 호스티드 서비스(AMR/Cobot 등) 정리. 기존 5초 ShutdownTimeout 이 초과분을 강제 종료.
            host.StopAsync().GetAwaiter().GetResult();
            host.Dispose();
        }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
