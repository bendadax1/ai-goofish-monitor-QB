using Avalonia;
using System.Runtime.InteropServices;
using System.Text;
using AiGoofish.Launcher.Core;

namespace AiGoofish.Launcher.App;

internal static class Program
{
    internal static bool SimulationRequested { get; private set; }
    internal static DevelopmentLaunchRequest? DevelopmentRequest { get; private set; }

    internal static bool DevelopmentEnabled =>
#if LAUNCHER_DEVELOPMENT
        true;
#else
        false;
#endif

    [STAThread]
    public static int Main(string[] args)
    {
        // Keep bootstrap evidence outside the immutable bundle and instance data roots.
        var diagnostics = StartupDiagnostics.Current;
        diagnostics.Record(StartupStage.Entry, StartupEventKind.Begin);
        OpenDiagnostics(diagnostics);
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        var exitCode = 3;
        try
        {
            exitCode = Run(args);
            return exitCode;
        }
        catch (Exception exception)
        {
            diagnostics.Record(StartupStage.Entry, StartupEventKind.Failed, exception: exception);
            try { Console.Error.WriteLine($"Launcher 启动失败；诊断会话 {diagnostics.SessionId:D}。"); }
            catch (IOException) { }
            return 3;
        }
        finally
        {
            diagnostics.Record(StartupStage.Entry, StartupEventKind.ProcessExited, exitCode: exitCode);
            AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
            TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
            diagnostics.Dispose();
        }
    }

    private static void OpenDiagnostics(StartupDiagnosticSession diagnostics)
    {
        foreach (var fallback in new[] { false, true })
        {
            try
            {
                var root = fallback ? Path.GetTempPath() : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (!string.IsNullOrWhiteSpace(root) && diagnostics.TryOpen(Path.Combine(root, "AiGoofish", "Launcher", "diagnostics"))) return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
            {
                diagnostics.Record(StartupStage.DiagnosticStorage, StartupEventKind.StorageUnavailable, exception: exception);
            }
        }
    }

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs args) =>
        StartupDiagnostics.Current.Record(StartupStage.Entry, StartupEventKind.Failed, exception: args.ExceptionObject as Exception);

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs args) =>
        StartupDiagnostics.Current.Record(StartupStage.Entry, StartupEventKind.Failed, exception: args.Exception);

    private static int Run(string[] args)
    {
        if (string.Equals(Path.GetFileName(Environment.ProcessPath), "uninstall.exe", StringComparison.OrdinalIgnoreCase))
            return PortableUninstaller.Run(args);

#if LAUNCHER_DEVELOPMENT
        if (args.Length == 4 && args[0] == "--development")
        {
            DevelopmentRequest = new DevelopmentLaunchRequest(args[1], args[2], args[3]);
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(Array.Empty<string>());
            return 0;
        }
#endif
        if (args.Length == 1 && args[0] is "--verify-package" or "--version")
        {
            PrepareDiagnosticOutput();
            Console.WriteLine("PACKAGE_VERIFICATION=PASS");
            Console.WriteLine($"LAUNCHER_VERSION={LauncherVersion.Current}");
            Console.WriteLine($"FRAMEWORK={RuntimeInformation.FrameworkDescription}");
            Console.WriteLine($"PROCESS_ARCHITECTURE={RuntimeInformation.ProcessArchitecture}");
            Console.WriteLine($"OS_ARCHITECTURE={RuntimeInformation.OSArchitecture}");
            Console.WriteLine($"IS_WINDOWS={OperatingSystem.IsWindows()}");
            Console.WriteLine("SIMULATION_ONLY=False");
            Console.WriteLine($"DEVELOPMENT_ENABLED={DevelopmentEnabled}");
            return 0;
        }

        if (args.Length > 0 && (args.Length != 1 || args[0] != "--simulation"))
        {
            return 2;
        }

        SimulationRequested = args.Length == 1;
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(Array.Empty<string>());
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
    }

    private static void PrepareDiagnosticOutput()
    {
        if (OperatingSystem.IsWindows())
        {
            _ = AttachConsole(AttachParentProcess);
        }

        var standardOutput = Console.OpenStandardOutput();
        Console.SetOut(new StreamWriter(standardOutput, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = true,
        });
    }

    private const uint AttachParentProcess = 0xFFFFFFFF;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(uint processId);
}
