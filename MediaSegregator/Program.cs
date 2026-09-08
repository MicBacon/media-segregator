using Avalonia;

namespace MediaSegregator;

internal static class Program
{
    // Avalonia needs to be initialized before any UI type is touched, so keep
    // this method free of anything that pulls in SynchronizationContext.
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .WithInterFont()
        .LogToTrace();
}
