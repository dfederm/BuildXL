// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.ContractsLight;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using BuildXL.Utilities.Collections;
using BuildXL.Utilities.Instrumentation.Common;
using BuildXL.Utilities.Tracing;

namespace BuildXL.Utilities.Tasks
{
    /// <summary>
    /// Static utilities related to <see cref="Task" />.
    /// </summary>
    public static class TaskUtilities
    {
        /// <summary>
        /// Returns a faulted task containing the given exception.
        /// This is the failure complement of <see cref="Task.FromResult{TResult}" />.
        /// </summary>
        [ContractOption("runtime", "checking", false)]
        public static Task<T> FromException<T>(Exception ex)
        {
            Contract.RequiresNotNull(ex);

            var failureSource = TaskSourceSlim.Create<T>();
            failureSource.SetException(ex);
            return failureSource.Task;
        }

        /// <summary>
        /// This is a variant of Task.WhenAll which ensures that all exceptions thrown by the tasks are
        /// propagated back through a single AggregateException. This is necessary because the default awaiter
        /// (as used by 'await') only takes the *first* exception inside of a task's aggregate exception.
        /// All BuildXL code should use this method instead of the standard WhenAll.
        /// </summary>
        public static async Task SafeWhenAll(IEnumerable<Task> tasks, bool wrapSingleException = true)
        {
            Contract.Requires(tasks != null);

            var whenAllTask = Task.WhenAll(tasks);
            
            // 'WhenAll' is not very 'async/await' friendly, but in some cases the original behavior is good:
            // If there is only one error it doesn't make any sense to wrap it in two AggregateExceptions.
            // So we can just 're-throw' an original exception without any changes.
            // But if more than one task failed, than we can wrap the error into a separate AggregateException.
            try
            {
                await whenAllTask;
            }
            catch
            {
                var exception = whenAllTask.Exception;
                if (exception!.InnerExceptions.Count == 1 && !wrapSingleException)
                {
                    // Just propagate a single error but only when a flag to wrap a single exception is not set.
                    throw;
                }

                // More than one error occurred, re-throw 'AggregateException' and wrap it into another AggregateException instance.
                ExceptionDispatchInfo.Capture(exception).Throw();
                throw; // This line is unreachable.
            }
        }

        /// <summary>
        /// Creates a task that will complete when all of the <see cref="T:System.Threading.Tasks.Task" /> objects in an enumerable collection have completed or when the <paramref name="token"/> is triggered.
        /// </summary>
        /// <exception cref="OperationCanceledException">The exception is thrown if the <paramref name="token"/> is canceled before the completion of <paramref name="tasks"/></exception>
        public static async Task WhenAllWithCancellationAsync(IEnumerable<Task> tasks, CancellationToken token)
        {
            // If one of the tasks passed here fails, we want to make sure that the task created by 'Task.WhenAll(tasks)' is observed
            // in order to avoid unobserved task errors.
            
            var whenAllTask = Task.WhenAll(tasks);
            
            var completedTask = await Task.WhenAny(
                Task.Delay(Timeout.InfiniteTimeSpan, token),
                whenAllTask);

            // We have one of two cases here: either all the tasks are done or the cancellation was requested.

            // If the cancellation is requested we need to make sure we observe the result of the when all task created earlier.
            whenAllTask.Forget();

            // Now, we can trigger 'OperationCancelledException' if the token is canceled.
            // (Yes, its possible that all the tasks are done already, but this is a natural race condition for this pattern).
            token.ThrowIfCancellationRequested();

            // The cancellation was not requested, but one of the tasks may fail.
            // Re-throwing the error in this case by awaiting already completed task.
            await completedTask;
        }

        /// <summary>
        /// Gets <see cref="CancellationTokenAwaitable"/> from a given <paramref name="token"/> that can be used in async methods to await the cancellation.
        /// </summary>
        /// <remarks>
        /// The method returns a special disposable type instead of just returning a Task.
        /// This is important, because the client code need to "unregister" the callback from the token when some other operations are done and the cancellation is no longer relevant.
        /// Just returning a task on a token that is never trigerred will effectively cause a memory leak.
        /// Here is a previous implementation of this method:
        /// <code>public static async Task ToAwaitable(this CancellationToken token) { try {await Task.Delay(Timeout.Infinite, token);} catch(TaskCanceledException) {} }</code>
        /// The `Delay` impelmentaiton checks if the timeout is infinite and won't start the timer, but it still will create a `DelayPromise` instance
        /// and will register for the cancellation.
        /// It means that if we call such a method many times with the same cancellation token, the registration list will grow indefinitely causing potential performance issues.
        /// </remarks>
        public static CancellationTokenAwaitable ToAwaitable(this CancellationToken token)
        {
            if (!token.CanBeCanceled)
            {
                // If the token can not be canceled, return a special global instance with a task that will never be finished.
                return CancellationTokenAwaitable.NonCancellableAwaitable;
            }

            var tcs = new TaskCompletionSource<object>();
            var registration = token.Register(static tcs => ((TaskCompletionSource<object>)tcs).SetResult(null), tcs);
            return new CancellationTokenAwaitable(tcs.Task, registration);
        }

        /// <nodoc />
        public readonly struct CancellationTokenAwaitable : IDisposable
        {
            private readonly CancellationTokenRegistration? m_registration;

            /// <nodoc />
            public CancellationTokenAwaitable(Task completionTask, CancellationTokenRegistration? registration)
            {
                m_registration = registration;
                CompletionTask = completionTask;
            }

            /// <nodoc />
            public static CancellationTokenAwaitable NonCancellableAwaitable { get; } = new CancellationTokenAwaitable(new TaskCompletionSource<object>().Task, registration: null);

            /// <nodoc />
            public Task CompletionTask { get; }

            /// <inheritdoc />
            void IDisposable.Dispose()
            {
                m_registration?.Dispose();
            }
        }

        /// <summary>
        /// This is a variant of Task.WhenAll which ensures that all exceptions thrown by the tasks are
        /// propagated back through a single AggregateException. This is necessary because the default awaiter
        /// (as used by 'await') only takes the *first* exception inside of a task's aggregate exception.
        /// All BuildXL code should use this method instead of the standard WhenAll.
        /// </summary>
        public static async Task<TResult[]> SafeWhenAll<TResult>(IEnumerable<Task<TResult>> tasks)
        {
            Contract.RequiresNotNull(tasks);

            var whenAllTask = Task.WhenAll(tasks);
            try
            {
                return await whenAllTask;
            }
            catch
            {
                if (whenAllTask.Exception != null)
                    throw whenAllTask.Exception;
                else
                    throw;
            }
        }

        /// <summary>
        /// Provides await functionality for ordinary <see cref="WaitHandle"/>s.
        /// </summary>
        /// <param name="handle">The handle to wait on.</param>
        /// <returns>The awaiter.</returns>
        public static TaskAwaiter GetAwaiter(this WaitHandle handle)
        {
            Contract.RequiresNotNull(handle);

            return handle.ToTask().GetAwaiter();
        }

        /// <summary>
        /// Provides await functionality for an array of ordinary <see cref="WaitHandle"/>s.
        /// </summary>
        /// <param name="handles">The handles to wait on.</param>
        /// <returns>The awaiter.</returns>
        public static TaskAwaiter<int> GetAwaiter(this WaitHandle[] handles)
        {
            Contract.RequiresNotNull(handles);
            Contract.RequiresForAll(handles, handle => handles != null);

            return handles.ToTask().GetAwaiter();
        }

        /// <summary>
        /// Creates a TPL Task that is marked as completed when a <see cref="WaitHandle"/> is signaled.
        /// </summary>
        /// <param name="handle">The handle whose signal triggers the task to be completed.  Do not use a <see cref="Mutex"/> here.</param>
        /// <param name="timeout">The timeout (in milliseconds) after which the task will fault with a <see cref="TimeoutException"/> if the handle is not signaled by that time.</param>
        /// <returns>A Task that is completed after the handle is signaled.</returns>
        /// <remarks>
        /// There is a (brief) time delay between when the handle is signaled and when the task is marked as completed.
        /// </remarks>
        public static Task ToTask(this WaitHandle handle, int timeout = Timeout.Infinite)
        {
            Contract.RequiresNotNull(handle);

            return ToTask(new WaitHandle[1] { handle }, timeout);
        }

        /// <summary>
        /// Creates a TPL Task that is marked as completed when any <see cref="WaitHandle"/> in the array is signaled.
        /// </summary>
        /// <param name="handles">The handles whose signals triggers the task to be completed.  Do not use a <see cref="Mutex"/> here.</param>
        /// <param name="timeout">The timeout (in milliseconds) after which the task will return a value of WaitTimeout.</param>
        /// <returns>A Task that is completed after any handle is signaled.</returns>
        /// <remarks>
        /// There is a (brief) time delay between when the handles are signaled and when the task is marked as completed.
        /// </remarks>
        public static Task<int> ToTask(this WaitHandle[] handles, int timeout = Timeout.Infinite)
        {
            Contract.RequiresNotNull(handles);
            Contract.RequiresForAll(handles, handle => handles != null);

            var tcs = TaskSourceSlim.Create<int>();
            int signalledHandle = WaitHandle.WaitAny(handles, 0);
            if (signalledHandle != WaitHandle.WaitTimeout)
            {
                // An optimization for if the handle is already signaled
                // to return a completed task.
                tcs.SetResult(signalledHandle);
            }
            else
            {
                var localVariableInitLock = new object();
                lock (localVariableInitLock)
                {
                    RegisteredWaitHandle[] callbackHandles = new RegisteredWaitHandle[handles.Length];
                    for (int i = 0; i < handles.Length; i++)
                    {
                        callbackHandles[i] = ThreadPool.RegisterWaitForSingleObject(
                            handles[i],
                            (state, timedOut) =>
                            {
                                int handleIndex = (int)state;
                                if (timedOut)
                                {
                                    tcs.TrySetResult(WaitHandle.WaitTimeout);
                                }
                                else
                                {
                                    tcs.TrySetResult(handleIndex);
                                }

                                // We take a lock here to make sure the outer method has completed setting the local variable callbackHandles contents.
                                lock (localVariableInitLock)
                                {
                                    foreach (var handle in callbackHandles)
                                    {
                                        handle.Unregister(null);
                                    }
                                }
                            },
                            state: i,
                            millisecondsTimeOutInterval: timeout,
                            executeOnlyOnce: true);
                    }
                }
            }

            return tcs.Task;
        }

        /// <summary>
        /// Creates a new <see cref="SemaphoreSlim"/> representing a mutex which can only be entered once.
        /// </summary>
        /// <returns>the semaphore</returns>
        public static SemaphoreSlim CreateMutex(bool taken = false)
        {
            return new SemaphoreSlim(initialCount: taken ? 0 : 1, maxCount: 1);
        }

        /// <summary>
        /// Asynchronously acquire a semaphore
        /// </summary>
        /// <param name="semaphore">The semaphore to acquire</param>
        /// <param name="cancellationToken">The cancellation token</param>
        /// <returns>A disposable which will release the semaphore when it is disposed.</returns>
        public static async Task<SemaphoreReleaser> AcquireAsync(this SemaphoreSlim semaphore, CancellationToken cancellationToken = default(CancellationToken))
        {
            Contract.Requires(semaphore != null);

            var stopwatch = StopwatchSlim.Start();
            await semaphore.WaitAsync(cancellationToken);
            return new SemaphoreReleaser(semaphore, stopwatch.Elapsed);
        }

        /// <summary>
        /// Synchronously acquire a semaphore
        /// </summary>
        /// <param name="semaphore">The semaphore to acquire</param>
        public static SemaphoreReleaser AcquireSemaphore(this SemaphoreSlim semaphore)
        {
            Contract.Requires(semaphore != null);
            var stopwatch = StopwatchSlim.Start();

            semaphore.Wait();
            return new SemaphoreReleaser(semaphore, stopwatch.Elapsed);
        }

        /// <summary>
        /// Consumes a task and doesn't do anything with it.  Useful for fire-and-forget calls to async methods within async methods.
        /// </summary>
        /// <param name="task">The task whose result is to be ignored.</param>
        /// <param name="unobservedExceptionHandler">Optional handler for the task's unobserved exception (if any).</param>
        public static void Forget(this Task task, Action<Exception> unobservedExceptionHandler = null)
        {
            task.ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    Analysis.IgnoreArgument(t.Exception);
                    var e = (t.Exception as AggregateException)?.InnerException ?? t.Exception;
                    unobservedExceptionHandler?.Invoke(e);
                }
            });
        }

        /// <summary>
        /// "Swallow" an exception that happen in fire-and-forget task.
        /// </summary>
        public static Task IgnoreErrors(this Task task)
        {
            task.ContinueWith(t => { }, TaskContinuationOptions.OnlyOnFaulted);
            return task;
        }

        /// <summary>
        /// "Swallow" an exception that happen in fire-and-forget task.
        /// </summary>
        public static Task IgnoreErrorsAndReturnCompletion(this Task task)
        {
            return task.ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    // Ignore the exception if task if faulted
                    Analysis.IgnoreArgument(t.Exception);
                }
            });
        }

        /// <summary>
        /// Convenience method for creating a task with a result after a given task completes
        /// </summary>
        public static async Task<T> WithResultAsync<T>(this Task task, T result)
        {
            await task;
            return result;
        }

        /// <summary>
        /// Waits for the given task to complete within the given timeout, throwing a <see cref="TimeoutException"/> if the timeout expires before the task completes
        /// </summary>
        public static Task WithTimeoutAsync(this Task task, TimeSpan timeout)
        {
            return WithTimeoutAsync(async ct =>
            {
                await task;
                return Unit.Void;
            }, timeout);

        }

        /// <summary>
        /// Waits for the given task to complete within the given timeout, throwing a <see cref="TimeoutException"/> if the timeout expires before the task completes
        /// </summary>
        public static async Task<T> WithTimeoutAsync<T>(this Task<T> task, TimeSpan timeout, CancellationToken token = default)
        {
            await WithTimeoutAsync(ct => task, timeout, token);
            return await task;
        }

        /// <summary>
        /// Waits for the given task to complete within the given timeout, throwing a <see cref="TimeoutException"/> if the timeout expires before the task completes
        /// Very elaborate logic to ensure we log the "right" thing
        /// </summary>
        public static async Task<T> WithTimeoutAsync<T>(Func<CancellationToken, Task<T>> taskFactory, TimeSpan timeout, CancellationToken token = default)
        {
            if (timeout == Timeout.InfiniteTimeSpan)
            {
                return await taskFactory(token);
            }

            using (var timeoutTokenSource = new CancellationTokenSource(timeout))
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(timeoutTokenSource.Token, token))
            {
#pragma warning disable AsyncFixer04 // A disposable object used in a fire & forget async call
                var task = taskFactory(cts.Token);
#pragma warning restore AsyncFixer04 // A disposable object used in a fire & forget async call
                Analysis.IgnoreResult(await Task.WhenAny(task, Task.Delay(Timeout.Infinite, cts.Token)));

                if (!task.IsCompleted || (task.IsCanceled && timeoutTokenSource.IsCancellationRequested))
                {
                    // The user's task is not completed or it is canceled (possibly due to timeoutTokenSource)

                    if (!token.IsCancellationRequested)
                    {
                        // Throw TimeoutException only when the original token is not canceled.
                        observeTaskAndThrow(task);
                    }

                    // Need to wait with timeout again to ensure that cancellation of a non-responding task will time out.
                    Analysis.IgnoreResult(await Task.WhenAny(task, Task.Delay(Timeout.Infinite, timeoutTokenSource.Token)));
                    if (!task.IsCompleted && timeoutTokenSource.IsCancellationRequested)
                    {
                        observeTaskAndThrow(task);
                    }
                }

                return await task;
            }

            void observeTaskAndThrow(Task task)
            {
                // Task created by the task factory is unreachable by the client of this method.
                // So we need to "observe" potential error to prevent a (possible) application crash
                // due to TaskUnobservedException.
                task.Forget();
                throw new TimeoutException($"The operation has timed out. Timeout is '{timeout}'.");
            }
        }

        /// <summary>
        /// Gets a task for the completion source which execute continuations for TaskCompletionSource.SetResult asynchronously.
        /// </summary>
        public static Task<T> GetAsyncCompletion<T>(this TaskSourceSlim<T> completion)
        {
            if (completion.Task.IsCompleted)
            {
                return completion.Task;
            }

            return GetTaskWithAsyncContinuationAsync(completion);
        }

        private static async Task<T> GetTaskWithAsyncContinuationAsync<T>(this TaskSourceSlim<T> completion)
        {
            var result = await completion.Task;

            // Yield to not block the thread which sets the result of the completion
            await Task.Yield();

            return result;
        }

        /// <summary>
        /// Allows an IDisposable-conforming release of an acquired semaphore
        /// </summary>
        [SuppressMessage("Microsoft.Performance", "CA1815:OverrideEqualsAndOperatorEqualsOnValueTypes")]
        public readonly struct SemaphoreReleaser : IDisposable
        {
            private readonly SemaphoreSlim m_semaphore;

            /// <nodoc />
            public TimeSpan LockAcquisitionDuration { get; }

            /// <summary>
            /// Creates a new releaser.
            /// </summary>
            /// <param name="semaphore">The semaphore to release when Dispose is invoked.</param>
            /// <param name="lockAcquisitionDuration">The time it took to acquire the lock.</param>
            /// <remarks>
            /// Assumes the semaphore is already acquired.
            /// </remarks>
            internal SemaphoreReleaser(SemaphoreSlim semaphore, TimeSpan lockAcquisitionDuration)
            {
                Contract.RequiresNotNull(semaphore);
                m_semaphore = semaphore;
                LockAcquisitionDuration = lockAcquisitionDuration;
            }

            /// <summary>
            /// IDispoaable.Dispose()
            /// </summary>
            public void Dispose()
            {
                m_semaphore.Release();
            }

            /// <summary>
            /// Whether this semaphore releaser is valid (and not the default value)
            /// </summary>
            public bool IsValid => m_semaphore != null;

            /// <summary>
            /// Gets the number of threads that will be allowed to enter the semaphore.
            /// </summary>
            public int CurrentCount => m_semaphore.CurrentCount;
        }

        /// <summary>
        /// Awaits given tasks, periodically calling <paramref name="action"/>.
        /// </summary>
        /// <typeparam name="TItem">Type of the collection to iterate over.</typeparam>
        /// <typeparam name="TResult">Type of the tasks' result.</typeparam>
        /// <param name="collection">Collection to iterate over.</param>
        /// <param name="taskSelector">Function to use to select a task for a given item from the given collection.</param>
        /// <param name="action">
        /// Action to call periodically (as specified by <paramref name="period"/>).
        /// The action receives
        ///   (1) total elapsed time,
        ///   (2) all original items, and
        ///   (3) a collection of non-finished items
        /// </param>
        /// <param name="period">Period at which to call <paramref name="action"/>.</param>
        /// <param name="reportImmediately">Whether <paramref name="action"/> should be called immediately.</param>
        /// <returns>The results of inidvidual tasks.</returns>
        public static async Task<TResult[]> AwaitWithProgressReporting<TItem, TResult>(
            IReadOnlyCollection<TItem> collection,
            Func<TItem, Task<TResult>> taskSelector,
            Action<TimeSpan, IReadOnlyCollection<TItem>, IReadOnlyCollection<TItem>> action,
            TimeSpan period,
            bool reportImmediately = true)
        {
            var startTime = DateTime.UtcNow;
            var timer = new StoppableTimer(
                () =>
                {
                    var elapsed = DateTime.UtcNow.Subtract(startTime);
                    var remainingItems = collection
                        .Where(item => !taskSelector(item).IsCompleted)
                        .ToList();
                    action(elapsed, collection, remainingItems);
                },
                dueTime: reportImmediately ? 0 : (int)period.TotalMilliseconds,
                period: (int)period.TotalMilliseconds);

            using (timer)
            {
                var result = await Task.WhenAll(collection.Select(item => taskSelector(item)));
                await timer.StopAsync();

                // report once at the end
                action(DateTime.UtcNow.Subtract(startTime), collection, CollectionUtilities.EmptyArray<TItem>());
                return result;
            }
        }

        /// <summary>
        /// Awaits for a given task while periodically calling <paramref name="action"/>.
        /// </summary>
        /// <typeparam name="T">Return type of the task</typeparam>
        /// <param name="task">The task to await</param>
        /// <param name="period">Period at which to call <paramref name="action"/></param>
        /// <param name="action">Action to periodically call.  The action receives elapsed time since this method was called.</param>
        /// <param name="reportImmediately">Whether <paramref name="action"/> should be called immediately.</param>
        /// <param name="reportAtEnd">Whether <paramref name="action"/> should be called at when </param>
        /// <returns>The result of the task.</returns>
        public static async Task<T> AwaitWithProgressReportingAsync<T>(
            Task<T> task,
            TimeSpan period,
            Action<TimeSpan> action,
            bool reportImmediately = true,
            bool reportAtEnd = true)
        {
            var startTime = DateTime.UtcNow;
            using var timer = new StoppableTimer(
                () => action(DateTime.UtcNow.Subtract(startTime)),
                dueTime: reportImmediately ? 0 : (int)period.TotalMilliseconds,
                period: (int)period.TotalMilliseconds);

            await task.ContinueWith(_ => timer.StopAsync()).Unwrap();

            // report once at the end
            if (reportAtEnd)
            {
                action(DateTime.UtcNow.Subtract(startTime));
            }

            return await task;
        }

        /// <summary>
        /// Evaluate Tasks and return <paramref name="errorValue"/> if evaluation was cancelled.
        /// </summary>
        public static async Task<T> WithCancellationHandlingAsync<T>(LoggingContext loggingContext, Task<T> evaluationTask, Action<LoggingContext> errorLogEvent, T errorValue, CancellationToken cancellationToken)
        {
            try
            {
                var result = await evaluationTask;
                if (result.Equals(errorValue))
                {
                    return errorValue;
                }

                // Check for cancellation one last time.
                //
                // This makes sure that we log an error and return false if cancellation is requested.
                // If we don't check for cancellation at this point, it can happen that 'result' is
                // false (because the intepreter caught OperationCanceledException and returned ErrorResult)
                // but we haven't logged an error.
                cancellationToken.ThrowIfCancellationRequested();

                return result;
            }
            catch (OperationCanceledException)
            {
                errorLogEvent(loggingContext);
                return errorValue;
            }
        }
    }
}
