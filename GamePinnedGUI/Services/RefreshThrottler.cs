using System;
using System.Threading;
using System.Threading.Tasks;

namespace GamePinned.GUI.Services
{
    /// <summary>
    /// Throttles UI refresh operations to prevent excessive service calls
    /// Implements debouncing to ensure rapid successive calls only execute once
    /// </summary>
    public class RefreshThrottler
    {
        private readonly int _delayMs;
        private CancellationTokenSource? _cancellationTokenSource;
        private readonly object _lock = new object();
        private bool _isScheduled = false;

        public RefreshThrottler(int delayMs = 500)
        {
            _delayMs = delayMs;
        }

        /// <summary>
        /// Throttle an action - if called multiple times within delay period, only executes once
        /// </summary>
        public async Task ThrottleAsync(Func<Task> action)
        {
            lock (_lock)
            {
                if (_isScheduled)
                {
                    // Cancel and dispose previous scheduled action
                    _cancellationTokenSource?.Cancel();
                    _cancellationTokenSource?.Dispose();
                }

                _cancellationTokenSource = new CancellationTokenSource();
                _isScheduled = true;
            }

            try
            {
                await Task.Delay(_delayMs, _cancellationTokenSource.Token);
                
                // Execute the action
                await action();
            }
            catch (OperationCanceledException)
            {
                // Operation was cancelled by a newer call - this is expected
            }
            finally
            {
                lock (_lock)
                {
                    _isScheduled = false;
                    _cancellationTokenSource?.Dispose();
                    _cancellationTokenSource = null;
                }
            }
        }

    }
}
