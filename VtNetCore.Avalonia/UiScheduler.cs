using System;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using Avalonia.Threading;

namespace VtNetCore.Avalonia
{
    internal sealed class UiScheduler : LocalScheduler
    {
        public static UiScheduler Instance { get; } = new UiScheduler();

        public override IDisposable Schedule<TState>(TState state, TimeSpan dueTime,
            Func<IScheduler, TState, IDisposable> action)
        {
            var result = new SingleAssignmentDisposable();

            void Run()
            {
                if (!result.IsDisposed) result.Disposable = action(this, state);
            }

            var delay = Scheduler.Normalize(dueTime);
            if (delay == TimeSpan.Zero)
            {
                Dispatcher.UIThread.Post(Run);
                return result;
            }

            return new CompositeDisposable(DispatcherTimer.RunOnce(Run, delay), result);
        }
    }
}
