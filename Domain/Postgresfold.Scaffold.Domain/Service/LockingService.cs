using Postgresfold.Scaffold.Domain.Util;
using System.Collections.Concurrent;

namespace Postgresfold.Scaffold.Domain.Service
{
    public class LockingService
    {
        private readonly Dictionary<string, SemaphoreSlim> _lockDictionary = new();
        private readonly object _syncRoot = new();

        public async Task AcquireLockAsync(string lockName, int timeoutMs = 5000)
        {
            SemaphoreSlim semaphore;

            // Ensure thread-safe access to the dictionary
            lock (_syncRoot)
            {
                if (!_lockDictionary.TryGetValue(lockName, out semaphore))
                {
                    semaphore = new SemaphoreSlim(1, 1);
                    _lockDictionary[lockName] = semaphore;
                }
            }

            // Try acquiring the semaphore
            Logger.LogDebug($"[Acquire Requested] {lockName}");
            if (!await semaphore.WaitAsync(timeoutMs))
            {
                Logger.LogError($"[Acquire Timeout] {lockName}");
                throw new TimeoutException($"Timeout while waiting for lock on {lockName}");
            }

            Logger.LogDebug($"[Locked] {lockName}");
        }

        public void ReleaseLock(string lockName)
        {
            SemaphoreSlim semaphore;

            lock (_syncRoot)
            {
                if (!_lockDictionary.TryGetValue(lockName, out semaphore))
                    Logger.LogError($"[ReleaseLock] No lock found for {lockName}");
            }

            // Release the semaphore
            semaphore.Release();
            Logger.LogDebug($"[Released] {lockName}, CurrentCount: {semaphore.CurrentCount}");

            // Clean up the semaphore if no threads are waiting
            lock (_syncRoot)
            {
                if (semaphore.CurrentCount == 1) // Indicates no active locks
                {
                    _lockDictionary.Remove(lockName);
                    semaphore.Dispose();
                    Logger.LogDebug($"[Disposed] {lockName}");
                }
            }
        }
    }
}
