using System.Runtime.CompilerServices;

namespace UTStudio.Core.Tests.TestDoubles;

internal static class DiagnosticWait
{
    internal static async Task For(Task task, [CallerArgumentExpression(nameof(task))] string condition = "operation")
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (TimeoutException error) { throw new TimeoutException($"Not reached within 10 seconds: {condition}", error); }
    }
    internal static async Task<T> For<T>(Task<T> task, [CallerArgumentExpression(nameof(task))] string condition = "operation")
    { await For((Task)task, condition); return await task; }
    internal static async Task Until(Func<bool> condition, [CallerArgumentExpression(nameof(condition))] string description = "condition")
    {
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition())
        {
            if (watchdog.IsCancellationRequested) { throw new TimeoutException($"Not reached within 10 seconds: {description}"); }
            await Task.Yield();
        }
    }
}
