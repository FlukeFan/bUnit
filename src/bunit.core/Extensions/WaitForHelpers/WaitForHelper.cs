using Bunit.Rendering;
using Microsoft.Extensions.Logging;

namespace Bunit.Extensions.WaitForHelpers;

/// <summary>
/// Represents a helper class that can wait for a render notifications from a <see cref="IRenderedFragmentBase"/> type,
/// until a specific timeout is reached.
/// </summary>
public abstract class WaitForHelper<T> : IDisposable
{
	private readonly TaskCompletionSource<T> checkPassedCompletionSource;
	private readonly Func<(bool CheckPassed, T Content)> completeChecker;
	private readonly IRenderedFragmentBase renderedFragment;
	private readonly ILogger<WaitForHelper<T>> logger;
	private bool isDisposed;
	private Exception? capturedException;

	/// <summary>
	/// Gets the error message passed to the user when the wait for helper times out.
	/// </summary>
	protected virtual string? TimeoutErrorMessage { get; }

	/// <summary>
	/// Gets the error message passed to the user when the wait for checker throws an exception.
	/// Only used if <see cref="StopWaitingOnCheckException"/> is true.
	/// </summary>
	protected virtual string? CheckThrowErrorMessage { get; }

	/// <summary>
	/// Gets a value indicating whether to continue waiting if the wait condition checker throws.
	/// </summary>
	protected abstract bool StopWaitingOnCheckException { get; }

	/// <summary>
	/// Gets the task that will complete successfully if the check passed before the timeout was reached.
	/// The task will complete with an <see cref="WaitForFailedException"/> exception if the timeout was reached without the check passing.
	/// </summary>
	public Task<T> WaitTask { get; }

	/// <summary>
	/// Initializes a new instance of the <see cref="WaitForHelper{T}"/> class.
	/// </summary>
	protected WaitForHelper(
		IRenderedFragmentBase renderedFragment,
		Func<(bool CheckPassed, T Content)> completeChecker,
		TimeSpan? timeout = null)
	{
		this.renderedFragment = renderedFragment ?? throw new ArgumentNullException(nameof(renderedFragment));
		this.completeChecker = completeChecker ?? throw new ArgumentNullException(nameof(completeChecker));

		logger = renderedFragment.Services.CreateLogger<WaitForHelper<T>>();
		checkPassedCompletionSource = new TaskCompletionSource<T>();
		WaitTask = CreateWaitTask(renderedFragment, timeout);

		InitializeWaiting();
	}

	/// <summary>
	/// Disposes the wait helper and cancels the any ongoing waiting, if it is not
	/// already in one of the other completed states.
	/// </summary>
	public void Dispose()
	{
		Dispose(disposing: true);
		GC.SuppressFinalize(this);
	}

	/// <summary>
	/// Disposes of the wait task and related logic.
	/// </summary>
	/// <remarks>
	/// The disposing parameter should be false when called from a finalizer, and true when called from the
	/// <see cref="Dispose()"/> method. In other words, it is true when deterministically called and false when non-deterministically called.
	/// </remarks>
	/// <param name="disposing">Set to true if called from <see cref="Dispose()"/>, false if called from a finalizer.f.</param>
	protected virtual void Dispose(bool disposing)
	{
		if (isDisposed || !disposing)
			return;

		isDisposed = true;
		checkPassedCompletionSource.TrySetCanceled();
		renderedFragment.OnAfterRender -= OnAfterRender;
		logger.LogWaiterDisposed(renderedFragment.ComponentId);
	}

	private void InitializeWaiting()
	{
		if (!WaitTask.IsCompleted)
		{
			var renderCountAtSubscribeTime = renderedFragment.RenderCount;

			// Before subscribing to renderedFragment.OnAfterRender,
			// we need to make sure that the desired state has not already been reached.
			OnAfterRender(this, EventArgs.Empty);

			SubscribeToOnAfterRender();

			// If the render count from before subscribing has changes
			// till now, we need to do trigger another check, since
			// the render may have happened asynchronously and before
			// the subscription was set up.
			if (renderCountAtSubscribeTime < renderedFragment.RenderCount)
			{
				OnAfterRender(this, EventArgs.Empty);
			}
		}
	}

	private Task<T> CreateWaitTask(IRenderedFragmentBase renderedFragment, TimeSpan? timeout)
	{
		var renderer = renderedFragment.Services.GetRequiredService<ITestRenderer>();

		// Two to failure conditions, that the renderer captures an unhandled
		// exception from a component or itself, or that the timeout is reached,
		// are executed on the renderes schedular, to ensure that OnAfterRender
		// and the continuations does not happen at the same time.
		var failureTask = renderer.Dispatcher.InvokeAsync(() =>
		{
			var taskScheduler = TaskScheduler.FromCurrentSynchronizationContext();

			var renderException = renderer
				.UnhandledException
				.ContinueWith(
					x => Task.FromException<T>(x.Result),
					CancellationToken.None,
					TaskContinuationOptions.OnlyOnRanToCompletion | TaskContinuationOptions.ExecuteSynchronously,
					taskScheduler);

			var timeoutTask = Task.Delay(GetRuntimeTimeout(timeout))
				.ContinueWith(
					x =>
					{
						logger.LogWaiterTimedOut(renderedFragment.ComponentId);
						return Task.FromException<T>(new WaitForFailedException(TimeoutErrorMessage, capturedException));
					},
					CancellationToken.None,
					TaskContinuationOptions.OnlyOnRanToCompletion | TaskContinuationOptions.ExecuteSynchronously,
					taskScheduler);

			return Task.WhenAny(renderException, timeoutTask).Unwrap();
		}).Unwrap();

		return Task.WhenAny(failureTask, checkPassedCompletionSource.Task).Unwrap();
	}

	private void OnAfterRender(object? sender, EventArgs args)
	{
		if (isDisposed || WaitTask.IsCompleted)
			return;

		try
		{
			logger.LogCheckingWaitCondition(renderedFragment.ComponentId);

			var checkResult = completeChecker();
			if (checkResult.CheckPassed)
			{
				checkPassedCompletionSource.TrySetResult(checkResult.Content);
				logger.LogCheckCompleted(renderedFragment.ComponentId);
				Dispose();
			}
			else
			{
				logger.LogCheckFailed(renderedFragment.ComponentId);
			}
		}
		catch (Exception ex)
		{
			capturedException = ex;
			logger.LogCheckThrow(renderedFragment.ComponentId, ex);

			if (StopWaitingOnCheckException)
			{
				checkPassedCompletionSource.TrySetException(new WaitForFailedException(CheckThrowErrorMessage, capturedException));
				Dispose();
			}
		}
	}

	private void SubscribeToOnAfterRender()
	{
		// There might not be a need to subscribe if the WaitTask has already
		// been completed, perhaps due to an unhandled exception from the
		// renderer or from the initial check by the checker.
		if (!isDisposed && !WaitTask.IsCompleted)
			renderedFragment.OnAfterRender += OnAfterRender;
	}

	private static TimeSpan GetRuntimeTimeout(TimeSpan? timeout)
	{
		return timeout ?? TimeSpan.FromSeconds(1);
	}
}
