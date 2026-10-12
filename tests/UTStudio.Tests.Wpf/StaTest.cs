using System.Windows.Threading;

[assembly: DoNotParallelize]

namespace UTStudio.Tests.Wpf;

internal static class StaTest
{
    internal static Task Run(Func<Task> test, TimeSpan? timeout = null)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            Task operation;
            Task? finishedOperation = null;
            try { operation = test(); }
            catch (Exception error) { operation = Task.FromException(error); }
            _ = operation.ContinueWith(finished =>
            {
                finishedOperation = finished;
                dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            Dispatcher.Run();
            Task finished = finishedOperation ?? operation;
            if (finished.IsFaulted) { completion.TrySetException(finished.Exception!.InnerExceptions); }
            else if (finished.IsCanceled) { completion.TrySetCanceled(); }
            else { completion.TrySetResult(); }
        })
        { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task.WaitAsync(timeout ?? TimeSpan.FromSeconds(15));
    }
}
