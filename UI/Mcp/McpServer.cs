using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mesen.Interop;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Mesen.Mcp;

internal sealed class McpServer : IDisposable
{
	private static readonly TimeSpan MaximumStopTimeout = TimeSpan.FromSeconds(2);
	private const long MaximumRequestBodySize = 512 * 1024;
	private readonly object _lifecycleLock = new();
	private readonly McpEmulatorService _service;
	private readonly McpAutomationService _automationService;
	private readonly McpExperimentService _experimentService;
	private readonly McpMemorySnapshotService _memorySnapshotService;
	private readonly McpMemorySearchService _memorySearchService;
	private readonly McpSaveStateStore _saveStates;
	private readonly McpMemorySnapshotStore _memorySnapshots;
	private readonly McpMemorySearchStore _memorySearches;
	private readonly Action<string> _toolLog;
	private readonly Func<WebApplication, CancellationToken, Task> _applicationCleanup;
	private LifecycleGeneration? _generation;
	private Task? _detachedShutdownTask;
	private long _nextGenerationId;
	private bool _stopping;
	private bool _resourcesDisposed;
	private bool _coreReleaseCompleted;
	private bool _disposed;

	internal McpServer(
		IMcpEmulatorApi api,
		Action<string>? toolLog = null,
		Func<WebApplication, CancellationToken, Task>? applicationCleanup = null)
		: this(new McpEmulatorService(api), toolLog, applicationCleanup)
	{ }

	internal McpServer(
		McpEmulatorService service,
		Action<string>? toolLog = null,
		Func<WebApplication, CancellationToken, Task>? applicationCleanup = null)
	{
		_service = service;
		_saveStates = new();
		_memorySnapshots = new();
		_memorySearches = new();
		_automationService = new(service, new McpAutomationAdapterRegistry(service.Api), _saveStates);
		_experimentService = new(service, new McpAutomationAdapterRegistry(service.Api), _saveStates);
		_memorySnapshotService = new(service, _memorySnapshots);
		_memorySearchService = new(service, _memorySearches);
		_toolLog = toolLog ?? Log;
		_applicationCleanup = applicationCleanup ?? StopAndDisposeApplicationAsync;
	}

	internal McpAutomationService AutomationService => _automationService;
	internal McpExperimentService ExperimentService => _experimentService;
	internal McpEmulatorIdentity EmulatorIdentity => _service.EmulatorIdentity;
	internal McpSaveStateStore SaveStates => _saveStates;
	internal McpMemorySnapshotStore MemorySnapshots => _memorySnapshots;
	internal McpMemorySearchStore MemorySearches => _memorySearches;
	internal Uri Endpoint
	{
		get
		{
			lock(_lifecycleLock) {
				return _generation?.Endpoint ?? throw new InvalidOperationException("The MCP server is not running.");
			}
		}
	}

	internal Task StartAsync(ushort port, CancellationToken cancellationToken = default)
	{
		lock(_lifecycleLock) {
			ObjectDisposedException.ThrowIf(_disposed, this);
			if(_stopping) {
				throw new InvalidOperationException("The MCP server is stopping.");
			}
			if(_generation is not null) {
				if(_generation.Stopping) {
					throw new InvalidOperationException("The previous MCP server generation is still stopping.");
				}
				return _generation.StartTask;
			}

			LifecycleGeneration generation = new(++_nextGenerationId, cancellationToken);
			_generation = generation;
			generation.StartTask = StartCoreAsync(generation, port);
			return generation.StartTask;
		}
	}

	internal void Stop(TimeSpan timeout)
	{
		TimeSpan boundedTimeout = timeout <= TimeSpan.Zero
			? TimeSpan.Zero
			: timeout < MaximumStopTimeout ? timeout : MaximumStopTimeout;
		long stopStarted = Stopwatch.GetTimestamp();
		lock(_lifecycleLock) {
			if(_stopping) {
				return;
			}
			_stopping = true;
		}
		_service.AbandonBeforeCoreRelease();
		LifecycleGeneration? generation;
		Task? shutdownTask;
		lock(_lifecycleLock) {
			generation = _generation;
			if(generation is not null && !generation.Stopping) {
				generation.Stopping = true;
				generation.Endpoint = null;
				generation.ForcedShutdownCancellation = new CancellationTokenSource(boundedTimeout);
				generation.ShutdownTask = StopCoreAsync(
					generation,
					_applicationCleanup,
					generation.ForcedShutdownCancellation.Token
				);
				_detachedShutdownTask = generation.ShutdownTask;
				_generation = null;
			}
			shutdownTask = generation?.ShutdownTask;
		}

		if(generation is not null) {
			TryCancel(generation.StartCancellation);
			TryCancel(generation.RequestCancellation);
			TryStopApplication(generation.Application);
		}

		DisposeResourceStores();
		try {
			if(shutdownTask is not null && !shutdownTask.Wait(RemainingStopBudget(stopStarted, boundedTimeout))) {
				Log("[MCP] HTTP host cleanup is continuing after the shutdown grace period.");
			}
		} catch(AggregateException ex) {
			Log($"[MCP] Stop did not complete cleanly: {ex.GetBaseException().Message}");
		} catch(Exception ex) {
			Log($"[MCP] Stop did not complete cleanly: {ex.GetBaseException().Message}");
		}

		lock(_lifecycleLock) {
			if(_detachedShutdownTask?.IsCompleted == true) {
				_detachedShutdownTask = null;
			}
		}
	}

	internal void CompleteCoreRelease()
	{
		lock(_lifecycleLock) {
			if(_coreReleaseCompleted) {
				return;
			}
			_coreReleaseCompleted = true;
		}
		_service.DetachManagedResourcesAfterCoreRelease();
		_service.Dispose();
	}

	internal void ProcessNotification(NotificationEventArgs e)
	{
		lock(_lifecycleLock) {
			if(_stopping) {
				return;
			}
			_service.ProcessNotification(e);
			if(e.NotificationType is ConsoleNotificationType.GameLoaded
				or ConsoleNotificationType.GameLoadFailed
				or ConsoleNotificationType.EmulationStopped) {
				long romIdentity = _service.EmulatorIdentity.Current.RomIdentity;
				_saveStates.InvalidateRom(romIdentity);
				_memorySnapshots.InvalidateRom(romIdentity);
				_memorySearches.InvalidateRom(romIdentity);
			}
		}
	}

	public void Dispose()
	{
		lock(_lifecycleLock) {
			if(_disposed) {
				return;
			}
			_disposed = true;
		}
		Stop(MaximumStopTimeout);
	}

	private void DisposeResourceStores()
	{
		lock(_lifecycleLock) {
			if(_resourcesDisposed) {
				return;
			}
			_resourcesDisposed = true;
			_saveStates.Dispose();
			_memorySnapshots.Dispose();
			_memorySearches.Dispose();
		}
	}

	private static TimeSpan RemainingStopBudget(long started, TimeSpan budget)
	{
		TimeSpan remaining = budget - Stopwatch.GetElapsedTime(started);
		return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
	}

	private async Task StartCoreAsync(LifecycleGeneration generation, ushort port)
	{
		await Task.Yield();
		WebApplication? application = null;
		try {
			WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
			builder.Logging.ClearProviders();
			builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
			builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = MaximumRequestBodySize);
			builder.Services.AddMcpServer()
				.WithHttpTransport()
				.WithTools(McpTools.Create(
					_service,
					_automationService,
					_experimentService,
					_memorySnapshotService,
					_memorySearchService,
					_toolLog));

			application = builder.Build();
			application.Use(async (context, next) => {
				using CancellationTokenSource requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(
					context.RequestAborted,
					generation.RequestCancellation.Token
				);
				context.RequestAborted = requestCancellation.Token;
				try {
					await next(context).ConfigureAwait(false);
				} catch(OperationCanceledException) when(generation.RequestCancellation.IsCancellationRequested) {
					context.Abort();
				}
				if(generation.RequestCancellation.IsCancellationRequested) {
					context.Abort();
				}
			});
			application.MapMcp("/mcp");
			bool mayStart;
			lock(_lifecycleLock) {
				generation.Application = application;
				mayStart = ReferenceEquals(_generation, generation) && !generation.Stopping;
			}
			if(!mayStart) {
				throw new OperationCanceledException(generation.StartCancellation.Token);
			}
			await application.StartAsync(generation.StartCancellation.Token).ConfigureAwait(false);
			string address = application.Services.GetRequiredService<IServer>()
				.Features.Get<IServerAddressesFeature>()!.Addresses.Single();
			bool published;
			lock(_lifecycleLock) {
				published = ReferenceEquals(_generation, generation) && !generation.Stopping;
				if(published) {
					generation.Endpoint = new Uri(new Uri(address), "/mcp");
				}
			}
			if(!published) {
				throw new OperationCanceledException(generation.StartCancellation.Token);
			}
			Log($"[MCP] Server started at {address}/mcp.");
		} catch(Exception ex) {
			bool stopping;
			lock(_lifecycleLock) {
				stopping = generation.Stopping;
				generation.Endpoint = null;
			}
			if(!stopping) {
				Log($"[MCP] Server failed to start: {ex.Message}");
				if(application is not null) {
					await EnsureApplicationCleanupAsync(
						generation,
						application,
						_applicationCleanup,
						CancellationToken.None
					).ConfigureAwait(false);
				}
				generation.StartCancellation.Dispose();
				generation.RequestCancellation.Dispose();
				lock(_lifecycleLock) {
					if(ReferenceEquals(_generation, generation)) {
						_generation = null;
					}
				}
			}
			throw;
		}
	}

	private static async Task StopCoreAsync(
		LifecycleGeneration generation,
		Func<WebApplication, CancellationToken, Task> applicationCleanup,
		CancellationToken forcedShutdownToken)
	{
		await Task.Yield();
		try {
			try {
				await generation.StartTask.ConfigureAwait(false);
			} catch(Exception) {
				// Startup cancellation and failure are cleaned up by this generation.
			}
			generation.StartTask = Task.CompletedTask;

			WebApplication? application = generation.Application;
			if(application is not null) {
				try {
					await EnsureApplicationCleanupAsync(
						generation,
						application,
						applicationCleanup,
						forcedShutdownToken
					).ConfigureAwait(false);
				} catch(Exception ex) {
					System.Diagnostics.Debug.WriteLine($"[MCP] HTTP host cleanup failed: {ex.Message}");
				}
			}
		} finally {
			generation.StartCancellation.Dispose();
			generation.RequestCancellation.Dispose();
			generation.ForcedShutdownCancellation?.Dispose();
			generation.Endpoint = null;
			generation.Application = null;
		}
	}

	private static Task EnsureApplicationCleanupAsync(
		LifecycleGeneration generation,
		WebApplication application,
		Func<WebApplication, CancellationToken, Task> applicationCleanup,
		CancellationToken forcedShutdownToken)
	{
		lock(generation) {
			generation.ApplicationCleanupTask ??= applicationCleanup(application, forcedShutdownToken);
			return generation.ApplicationCleanupTask;
		}
	}

	private static async Task StopAndDisposeApplicationAsync(WebApplication application, CancellationToken forcedShutdownToken)
	{
		application.Lifetime.StopApplication();
		try {
			await application.StopAsync(forcedShutdownToken).ConfigureAwait(false);
		} catch(OperationCanceledException) when(forcedShutdownToken.IsCancellationRequested) {
			System.Diagnostics.Debug.WriteLine("[MCP] Active HTTP requests exceeded the shutdown grace period and were closed.");
		} catch(Exception ex) {
			System.Diagnostics.Debug.WriteLine($"[MCP] HTTP host stop failed: {ex.Message}");
		}

		try {
			await application.DisposeAsync().ConfigureAwait(false);
		} catch(Exception ex) {
			System.Diagnostics.Debug.WriteLine($"[MCP] HTTP host disposal failed: {ex.Message}");
		}
	}

	private static void TryCancel(CancellationTokenSource cancellation)
	{
		try {
			cancellation.Cancel();
		} catch(ObjectDisposedException) {
			// Generation cleanup won the race with an idempotent Stop call.
		}
	}

	private static void TryStopApplication(WebApplication? application)
	{
		try {
			application?.Lifetime.StopApplication();
		} catch(ObjectDisposedException) {
			// Generation cleanup won the race with an idempotent Stop call.
		}
	}

	internal static void Log(string message)
	{
		try {
			EmuApi.WriteLogEntry(message);
		} catch(DllNotFoundException) {
			// Unit tests do not load the native emulator core.
		} catch(EntryPointNotFoundException) {
			// Unit tests do not load the native emulator core.
		}
	}

	private sealed class LifecycleGeneration
	{
		internal LifecycleGeneration(long id, CancellationToken cancellationToken)
		{
			Id = id;
			StartCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			RequestCancellation = new CancellationTokenSource();
		}

		internal long Id { get; }
		internal CancellationTokenSource StartCancellation { get; }
		internal CancellationTokenSource RequestCancellation { get; }
		internal Task StartTask { get; set; } = Task.CompletedTask;
		internal WebApplication? Application { get; set; }
		internal Uri? Endpoint { get; set; }
		internal bool Stopping { get; set; }
		internal CancellationTokenSource? ForcedShutdownCancellation { get; set; }
		internal Task? ShutdownTask { get; set; }
		internal Task? ApplicationCleanupTask { get; set; }
	}
}
