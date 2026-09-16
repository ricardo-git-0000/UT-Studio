using System.Runtime.CompilerServices;
using System.Windows.Threading;

namespace UTStudio.Tests.Wpf;

internal static class DiagnosticWait
{
    internal static async Task For(Task task, [CallerArgumentExpression(nameof(task))] string condition = "operation")
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (TimeoutException error) { throw new TimeoutException($"Not reached within 10 seconds: {condition}", error); }
    }
    internal static async Task Until(Func<bool> condition, [CallerArgumentExpression(nameof(condition))] string description = "condition")
    {
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition())
        {
            if (watchdog.IsCancellationRequested) { throw new TimeoutException($"Not reached within 10 seconds: {description}"); }
            // A normal-priority Task.Yield loop on STA can starve the DataBind operations it awaits.
            if (Dispatcher.FromThread(Thread.CurrentThread) is not null) { await Dispatcher.Yield(DispatcherPriority.Background); }
            else { await Task.Yield(); }
        }
    }
}
