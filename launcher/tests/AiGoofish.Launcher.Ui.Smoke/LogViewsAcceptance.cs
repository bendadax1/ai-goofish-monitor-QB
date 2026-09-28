using AiGoofish.Launcher.App;
using AiGoofish.Launcher.Core;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace AiGoofish.Launcher.Ui.Smoke;

internal static class LogViewsAcceptance
{
    public static async Task RunAsync(string outputDirectory)
    {
        await using var vm = new MainWindowViewModel(isSimulation: false);
        var window = new MainWindow(vm, skipInitialization: true);
        window.Show();
        try
        {
            vm.SelectPage(LauncherPage.Logs);
            Dispatcher.UIThread.RunJobs();
            // A real VM replays the process journal, including earlier acceptance cases.
            // Clear only this isolated render fixture before testing the empty state.
            vm.Logs.Clear();
            vm.Activities.Clear();
            Dispatcher.UIThread.RunJobs();
            Require(vm.IsActivityView && !window.FindControl<Border>("ActionFooter")!.IsVisible, "summary default and no duplicate footer");
            Require(window.FindControl<TextBlock>("ActivityEmptyState")!.IsEffectivelyVisible, "activity empty state");
            Click(window, "RawLogViewButton");
            Require(vm.IsRawLogView && window.FindControl<TextBlock>("RawLogEmptyState")!.IsEffectivelyVisible, "raw empty state");

            var session = Guid.NewGuid();
            var sequence = 700000L;
            StartupDiagnosticRecord Record(StartupStage stage, StartupEventKind kind,
                LauncherDiagnosticComponent component = LauncherDiagnosticComponent.Launcher,
                ProcessOutputHint hint = ProcessOutputHint.None) =>
                new(++sequence, session, null, DateTimeOffset.UtcNow, 0, stage, kind, component, [], null, hint);
            var first = Record(StartupStage.PackageVerification, StartupEventKind.Completed);
            vm.OnStartupDiagnostic(null, first);
            vm.OnStartupDiagnostic(null, first);
            vm.OnStartupDiagnostic(null, Record(StartupStage.PostgresInitialize, StartupEventKind.Completed, LauncherDiagnosticComponent.Postgres));
            vm.OnStartupDiagnostic(null, Record(StartupStage.PythonWeb, StartupEventKind.Begin, LauncherDiagnosticComponent.PythonWeb));
            vm.OnStartupDiagnostic(null, Record(StartupStage.Process, StartupEventKind.OutputHint, LauncherDiagnosticComponent.PythonWeb, ProcessOutputHint.AddressInUse));
            vm.OnStartupDiagnostic(null, Record(StartupStage.PythonWeb, StartupEventKind.Failed, LauncherDiagnosticComponent.PythonWeb));
            Dispatcher.UIThread.RunJobs();
            Require(vm.Activities.Count == 5 && vm.Logs.Count == 5, "duplicate diagnostic not repeated in either view");
            Require(vm.Activities.Any(row => row.Message.Contains("端口被占用") && row.Level == "警告"), "typed hint localization");
            Require(vm.Activities.First().Level == "错误" && vm.Logs.Last().Message.Contains("PythonWeb/Failed"), "newest activity first; raw keeps original diagnostic detail");
            vm.LogView.LevelIndex = 1;
            Require(vm.LogView.Activities.Count == 2 && vm.LogView.Logs.Count == 2, "warnings and errors filter both views");
            vm.LogView.LevelIndex = 2;
            Require(vm.LogView.Activities.Count == 1 && vm.LogView.Logs.Count == 1, "errors-only filter");
            Click(window, "PauseLogViewButton");
            var frozen = vm.LogView.CreateExportPreview(activity: true);
            vm.OnStartupDiagnostic(null, Record(StartupStage.Start, StartupEventKind.Failed));
            Dispatcher.UIThread.RunJobs();
            Require(vm.Logs.Count == 6 && vm.LogView.Activities.Count == 1 && vm.LogView.CreateExportPreview(true) == frozen,
                "pause freezes both projection and export while source continues");
            vm.LogView.LevelIndex = 0;
            Require(vm.LogView.Activities.Count == 5, "filter changes use frozen source while paused");
            vm.LogView.LevelIndex = 2;
            Click(window, "PauseLogViewButton");
            Require(vm.LogView.Activities.Count == 2, "resume reveals newly recorded errors");
            var exportPath = CompleteExport(LauncherLogViewModel.SaveNewExportAsync(outputDirectory, frozen));
            var secondPath = CompleteExport(LauncherLogViewModel.SaveNewExportAsync(outputDirectory, frozen));
            Require(exportPath != secondPath && File.ReadAllText(exportPath) == frozen &&
                File.ReadAllBytes(exportPath).SequenceEqual(new System.Text.UTF8Encoding(false).GetBytes(frozen)),
                "exports preserve immutable preview, UTF8 without BOM, and never overwrite");
            try
            {
                CompleteExport(LauncherLogViewModel.SaveNewExportAsync(outputDirectory, new string('中', 800000)));
                throw new InvalidOperationException("Log views: oversized export accepted");
            }
            catch (InvalidOperationException exception) when (!exception.Message.StartsWith("Log views: ", StringComparison.Ordinal)) { }
            vm.LogView.LevelIndex = 0;
            var pickerCalls = 0;
            var exportCompleted = false;
            window.LogExportCompletedForAcceptance = () => exportCompleted = true;
            window.LogFolderSelectionForAcceptance = () => { pickerCalls++; return Task.FromResult<string?>(outputDirectory); };
            window.LogPreviewShownForAcceptance = (preview, text, cancel, save) =>
            {
                Require(text.IsReadOnly && preview.IsVisible, "real export dialog displays read-only snapshot");
                Capture(preview, outputDirectory, "logs-export-preview.png");
                cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            };
            Click(window, "ExportLogViewButton");
            Require(exportCompleted && pickerCalls == 0, "cancel preview never opens picker or writes");
            exportCompleted = false;
            var beforeFiles = Directory.GetFiles(outputDirectory, "launcher-view-*.txt").ToHashSet();
            string? approvedSnapshot = null;
            window.LogPreviewShownForAcceptance = (preview, text, cancel, save) =>
            {
                approvedSnapshot = text.Text;
                // Arriving records and view changes after preview must not change approved bytes.
                vm.LogView.LevelIndex = 2;
                save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            };
            Click(window, "ExportLogViewButton");
            PumpUntil(() => exportCompleted);
            var newFiles = Directory.GetFiles(outputDirectory, "launcher-view-*.txt").Where(path => !beforeFiles.Contains(path)).ToArray();
            Require(newFiles.Length == 1 && File.ReadAllText(newFiles[0]) == approvedSnapshot && pickerCalls == 1,
                "real handler saves exactly approved snapshot");
            exportCompleted = false;
            window.LogFolderSelectionForAcceptance = () => Task.FromResult<string?>(Path.Combine(outputDirectory, "missing-directory"));
            Click(window, "ExportLogViewButton");
            PumpUntil(() => exportCompleted);
            Require(vm.LogView.Status.Contains("导出失败") && !Directory.GetFiles(outputDirectory, "*.tmp").Any(),
                "write failure is visible and leaves no partial export");
            vm.LogView.Status = string.Empty;
            vm.LogView.LevelIndex = 0;
            Set(vm, nameof(vm.ServiceState), "启动失败");
            Set(vm, nameof(vm.PrimaryButtonText), "一键启动");
            Set(vm, nameof(vm.IsPrimaryEnabled), true);
            foreach (var size in new[] { (1120, 760), (920, 640) })
            {
                window.Width = size.Item1;
                window.Height = size.Item2;
                foreach (var activity in new[] { true, false })
                {
                    Click(window, activity ? "ActivityViewButton" : "RawLogViewButton");
                    Require(window.FindControl<Border>("ActivityPanel")!.IsEffectivelyVisible == activity &&
                        window.FindControl<Border>("RawLogPanel")!.IsEffectivelyVisible != activity, "only selected view visible");
                    CheckVisible(window, "LogsPrimaryButton");
                    CheckVisible(window, "LogsDirectoryShortcutButton");
                    CheckVisible(window, "LogLevelFilter");
                    CheckVisible(window, "PauseLogViewButton");
                    CheckVisible(window, "ExportLogViewButton");
                    Capture(window, outputDirectory, $"logs-{(activity ? "activity" : "raw")}-{size.Item1}x{size.Item2}.png");
                }
            }
            Require(vm.Activities.Count == 6 && vm.Logs.Count == 6, "view switching never clears records");
            vm.SelectPage(LauncherPage.Overview);
            vm.SelectPage(LauncherPage.Logs);
            Require(vm.IsRawLogView, "view choice survives page navigation");
            Set(vm, nameof(vm.IsPrimaryEnabled), false);
            Set(vm, nameof(vm.PrimaryButtonText), "正在启动");
            Set(vm, nameof(vm.IsCancelEnabled), true);
            Dispatcher.UIThread.RunJobs();
            var primary = window.FindControl<Button>("LogsPrimaryButton")!;
            Require(!primary.IsEnabled && primary.Content?.ToString() == "正在启动", "busy state shared");
            CheckVisible(window, "LogsCancelButton");
            Set(vm, nameof(vm.IsStopEnabled), true);
            Dispatcher.UIThread.RunJobs();
            CheckVisible(window, "LogsStopButton");
            CheckVisible(window, "LogsPrimaryButton");
            Set(vm, nameof(vm.IsCancelEnabled), false);
            Set(vm, nameof(vm.IsPrimaryEnabled), true);
            Set(vm, nameof(vm.ServiceState), "服务运行中");
            Set(vm, nameof(vm.PrimaryButtonText), "打开管理页");
            Dispatcher.UIThread.RunJobs();
            Capture(window, outputDirectory, "logs-running-toolbar-920x640.png");

            for (var i = 0; i < 350; i++) vm.OnStartupDiagnostic(null, Record(StartupStage.Start, StartupEventKind.Completed));
            Dispatcher.UIThread.RunJobs();
            Require(vm.Activities.Count == 100 && vm.Logs.Count == 300, "independent bounded memory collections");
            vm.Logs.Clear();
            Require(vm.HasActivities && !vm.HasLogs, "raw eviction does not erase retained activity");
            vm.Activities.Clear();
            Require(!vm.HasActivities, "empty state updates");
        }
        catch (Exception exception)
        {
            try
            {
                var safeFailure = exception.Message.StartsWith("Log views: ", StringComparison.Ordinal)
                    ? exception.Message : exception.GetType().Name;
                File.WriteAllText(Path.Combine(outputDirectory, "logs-acceptance-failure.txt"), safeFailure, new System.Text.UTF8Encoding(false));
            }
            catch (Exception writeFailure)
            {
                Console.Error.WriteLine($"Log acceptance evidence write failed: {writeFailure.GetType().Name}");
            }
            throw;
        }
        finally
        {
            Set(vm, nameof(vm.IsStopEnabled), false);
            Set(vm, nameof(vm.IsCancelEnabled), false);
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
        Console.WriteLine("LOG_VIEWS_ACCEPTANCE=PASS");
    }

    private static void Click(Window window, string name)
    {
        window.FindControl<Button>(name)!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
    }

    private static string CompleteExport(Task<string> task)
    {
        PumpUntil(() => task.IsCompleted);
        return task.GetAwaiter().GetResult();
    }

    private static void PumpUntil(Func<bool> completed)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!completed())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("Log export fixture timed out");
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(2);
        }
    }

    private static void Set(MainWindowViewModel vm, string property, object value) =>
        typeof(MainWindowViewModel).GetProperty(property)!.SetValue(vm, value);

    private static void CheckVisible(Window window, string name)
    {
        var control = window.FindControl<Control>(name)!;
        var point = control.TranslatePoint(default, window);
        Require(control.IsEffectivelyVisible && point is { } p && p.X >= 0 && p.Y >= 0 &&
            p.X + control.Bounds.Width <= window.ClientSize.Width && p.Y + control.Bounds.Height <= window.ClientSize.Height,
            name + " accessible at minimum size");
    }

    private static void Capture(Window window, string directory, string name)
    {
        using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("Log preview frame unavailable");
        frame.Save(Path.Combine(directory, name), PngBitmapEncoderOptions.Default);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Log views: " + message);
    }
}
