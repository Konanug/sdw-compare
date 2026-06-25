using Microsoft.Extensions.Logging;
using SolidWorks.Interop.sldworks;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Windows.Threading;

namespace SolidWorksPartMatcher.SolidWorks;

/// <summary>
/// Routes all SolidWorks COM calls through a single dedicated STA thread that runs
/// a WPF Dispatcher message loop. The Dispatcher pumps COM messages while idle,
/// which is required for correct COM STA behaviour — especially on Windows 10 where
/// a blocked STA thread (no message pump) causes COM deadlocks that silently
/// terminate the host process.
/// </summary>
public sealed class StaSolidWorksWorker : IDisposable
{
    private readonly ILogger<StaSolidWorksWorker> _logger;
    private readonly Thread _sta;
    private readonly ManualResetEventSlim _ready = new(false);
    private Dispatcher? _dispatcher;
    private bool _disposed;

    // Touched only on the STA thread — no locking needed.
    private ISldWorks? _swApp;
    private bool _swInitialized;
    private bool _swOwned;

    // Marshal.GetActiveObject was removed in .NET 5+; call oleaut32 directly.
    [DllImport("oleaut32.dll")]
    private static extern int GetActiveObject(
        ref Guid rclsid, IntPtr pvReserved,
        [MarshalAs(UnmanagedType.IUnknown)] out object ppunk);

    public StaSolidWorksWorker(ILogger<StaSolidWorksWorker> logger)
    {
        _logger = logger;
        _sta = new Thread(WorkerLoop) { IsBackground = true, Name = "SW-STA" };
        _sta.SetApartmentState(ApartmentState.STA);
        _sta.Start();
        // Wait until the Dispatcher is created on the STA thread before returning,
        // so RunAsync callers always find a valid _dispatcher reference.
        _ready.Wait();
    }

    /// <summary>
    /// Queue <paramref name="work"/> onto the STA thread and await the result.
    /// The delegate runs synchronously on the STA thread; do not await inside it.
    /// </summary>
    public Task<T> RunAsync<T>(Func<T> work)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _dispatcher!.BeginInvoke(() =>
        {
            try { tcs.SetResult(work()); }
            catch (Exception ex) { tcs.SetException(ex); }
        }, DispatcherPriority.Normal);
        return tcs.Task;
    }

    /// <summary>
    /// Returns the SolidWorks application object, connecting to a running instance
    /// or launching one if none is found. Must be called only from within a
    /// <see cref="RunAsync{T}"/> delegate (i.e., on the STA thread).
    /// </summary>
    internal ISldWorks? GetOrCreateSwApp()
    {
        if (_swInitialized) return _swApp;
        _swInitialized = true;

        // Prefer latching onto an already-running SolidWorks session.
        try
        {
            var progType = Type.GetTypeFromProgID("SldWorks.Application");
            if (progType != null)
            {
                var clsid = progType.GUID;
                int hr = GetActiveObject(ref clsid, IntPtr.Zero, out var comObj);
                if (hr == 0 && comObj is ISldWorks activeApp)
                {
                    _swApp = activeApp;
                    _swOwned = false;
                    _logger.LogInformation("Attached to existing SolidWorks instance");
                    return _swApp;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "GetActiveObject for SldWorks failed — will launch new instance");
        }

        // Launch a new (invisible) SolidWorks instance.
        try
        {
            var progType = Type.GetTypeFromProgID("SldWorks.Application")
                ?? throw new InvalidOperationException(
                       "SldWorks.Application COM ProgID not found — is SolidWorks installed?");

            _logger.LogInformation("Launching SolidWorks (this may take a minute) ...");
            _swApp = (ISldWorks)Activator.CreateInstance(progType)!;
            _swApp.Visible = false;
            _swOwned = true;
            _logger.LogInformation("SolidWorks launched");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect to or launch SolidWorks");
            _swApp = null;
        }

        return _swApp;
    }

    private void WorkerLoop()
    {
        // Capture this thread's Dispatcher and signal the constructor that it is ready.
        // Dispatcher.CurrentDispatcher creates a new Dispatcher for this thread on first access.
        _dispatcher = Dispatcher.CurrentDispatcher;
        _ready.Set();

        // Run the dispatcher message loop. This keeps the STA thread alive AND pumps
        // COM messages while idle — critical for correct COM STA behaviour on Windows 10.
        // Work items posted via BeginInvoke() are executed here on the STA thread.
        Dispatcher.Run();

        // Dispatcher.Run() returns after BeginInvokeShutdown() drains the queue.
        if (_swOwned && _swApp != null)
        {
            try { _swApp.ExitApp(); }
            catch (Exception ex) { _logger.LogWarning(ex, "SolidWorks ExitApp failed on teardown"); }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Request shutdown at ApplicationIdle priority so all queued work finishes first.
        _dispatcher?.BeginInvokeShutdown(DispatcherPriority.ApplicationIdle);
        if (!_sta.Join(TimeSpan.FromSeconds(30)))
            _logger.LogWarning("SW-STA thread did not exit within 30 s after dispose");
        _ready.Dispose();
    }
}
