using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SolidWorksPartMatcher.App.ViewModels;
using SolidWorksPartMatcher.Application.Interfaces;
using SolidWorksPartMatcher.Excel;
using SolidWorksPartMatcher.Infrastructure.Assembly;
using SolidWorksPartMatcher.Infrastructure.Blocking;
using SolidWorksPartMatcher.Infrastructure.Clustering;
using SolidWorksPartMatcher.Infrastructure.Discovery;
using SolidWorksPartMatcher.Infrastructure.Fingerprinting;
using SolidWorksPartMatcher.Infrastructure.Hashing;
using SolidWorksPartMatcher.SolidWorks;
using SolidWorksPartMatcher.Infrastructure.Orchestration;
using SolidWorksPartMatcher.Infrastructure.Persistence;
using SolidWorksPartMatcher.Infrastructure.Step;
using System.IO;
using System.Windows;

namespace SolidWorksPartMatcher.App;

public partial class App : System.Windows.Application
{
    public static IServiceProvider Services { get; private set; } = null!;

    private static string LogDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                     "SolidWorksPartMatcher");

    protected override void OnStartup(StartupEventArgs e)
    {
        // Catch unhandled exceptions from any source before we have a window.
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        DispatcherUnhandledException               += OnDispatcherUnhandledException;

        base.OnStartup(e);

        try
        {
            var logDir = LogDir;
            Directory.CreateDirectory(logDir);

            var dbPath  = Path.Combine(logDir, "partmatcher.db");
            var logPath = Path.Combine(logDir, "app.log");

            var services = new ServiceCollection();

            services.AddLogging(b =>
            {
                b.SetMinimumLevel(LogLevel.Information);
                b.AddProvider(new RollingFileLoggerProvider(logPath));
            });

            services.AddSingleton<IPartRepository>(sp =>
                new SqlitePartRepository(dbPath, sp.GetRequiredService<ILogger<SqlitePartRepository>>()));

            services.AddSingleton<IFileDiscoveryService, FileDiscoveryService>();
            services.AddSingleton<IFileHashService, Sha256FileHashService>();
            services.AddSingleton<StaSolidWorksWorker>();
            services.AddSingleton<IPartFingerprintExtractor, SolidWorksPartFingerprintExtractor>();
            services.AddSingleton<ICandidateBlocker, BucketCandidateBlocker>();
            services.AddSingleton<ICandidateScorer, WeightedCandidateScorer>();
            services.AddSingleton<IClusterBuilder, UnionFindClusterBuilder>();
            services.AddSingleton<ICanonicalNameService, CanonicalNameService>();
            services.AddSingleton<StepGeometryExtractor>();
            services.AddSingleton<IBodyEquivalenceChecker, BodyEquivalenceChecker>();
            services.AddSingleton<ITessellationComparator, TessellationToleranceComparator>();
            services.AddSingleton<IDetailedGeometryComparator, VolumetricBodyComparator>();
            services.AddSingleton<ISolidWorksFileOpener, SolidWorksFileOpener>();
            services.AddSingleton<IWorkbookExporter, ClosedXmlWorkbookExporter>();
            services.AddSingleton<IScanOrchestrationService, ScanOrchestrationService>();
            services.AddSingleton<AssemblyComponentMatcher>();
            services.AddSingleton<IAssemblyDiffOrchestrationService, AssemblyDiffOrchestrationService>();
            services.AddSingleton<IAssemblyDiffReportExporter, AssemblyDiffWorkbookExporter>();

            services.AddTransient<MainViewModel>();
            services.AddTransient<ReviewViewModel>();

            Services = services.BuildServiceProvider();

            var window = new LauncherWindow();
            MainWindow = window;
            window.Show();
        }
        catch (Exception ex)
        {
            ReportFatalError(ex, "Startup failed");
            Shutdown(1);
        }
    }

    private void OnDispatcherUnhandledException(
        object sender,
        System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        ReportFatalError(e.Exception, "Unhandled UI exception");
        Shutdown(1);
    }

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
            ReportFatalError(ex, "Unhandled background exception");
    }

    private static void ReportFatalError(Exception ex, string context)
    {
        // Always write to the log file first (works even with no window).
        try
        {
            var logPath = Path.Combine(LogDir, "app.log");
            Directory.CreateDirectory(LogDir);
            File.AppendAllText(logPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] FATAL [{context}]\r\n" +
                $"{ex}\r\n\r\n");
        }
        catch { /* if we can't log, at least try the dialog */ }

        var msg = $"SolidWorks Part Matcher encountered a fatal error and cannot continue.\r\n\r\n" +
                  $"{ex.GetType().Name}: {ex.Message}\r\n\r\n" +
                  $"A full error log has been written to:\r\n" +
                  $"{Path.Combine(LogDir, "app.log")}";

        System.Windows.MessageBox.Show(msg, "Fatal Error",
            System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
    }
}
