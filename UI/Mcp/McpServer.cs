using System;
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
	private readonly object _lifecycleLock = new();
	private readonly McpEmulatorService _service;
	private WebApplication? _application;
	private Task? _startTask;
	private CancellationTokenSource? _startCancellation;
	private Uri? _endpoint;
	private bool _stopping;
	private bool _disposed;

	internal McpServer(McpEmulatorService service)
	{
		_service = service;
	}

	internal Uri Endpoint
	{
		get {
			lock(_lifecycleLock) {
				return _endpoint ?? throw new InvalidOperationException("The MCP server is not running.");
			}
		}
	}

	internal Task StartAsync(ushort port, CancellationToken cancellationToken = default)
	{
		lock(_lifecycleLock) {
			ObjectDisposedException.ThrowIf(_disposed, this);
			if(_startTask is not null) {
				return _startTask;
			}

			_startCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			_startTask = StartCoreAsync(port, _startCancellation.Token);
			return _startTask;
		}
	}

	internal void Stop(TimeSpan timeout)
	{
		WebApplication? application;
		Task? startTask;
		CancellationTokenSource? startCancellation;
		lock(_lifecycleLock) {
			if(_stopping) {
				return;
			}
			application = _application;
			startTask = _startTask;
			if(application is null && startTask is null) {
				return;
			}
			_stopping = true;
			startCancellation = _startCancellation;
		}

		TimeSpan boundedTimeout = timeout <= TimeSpan.Zero
			? TimeSpan.Zero
			: timeout < MaximumStopTimeout ? timeout : MaximumStopTimeout;
		DateTime deadline = DateTime.UtcNow + boundedTimeout;
		try {
			startCancellation?.Cancel();
			application?.Lifetime.StopApplication();
			if(startTask is not null && !startTask.IsCompleted) {
				startTask.Wait(boundedTimeout);
			}

			application = _application;
			if(application is not null) {
				TimeSpan remaining = deadline - DateTime.UtcNow;
				if(remaining > TimeSpan.Zero) {
					using CancellationTokenSource stopCancellation = new(remaining);
					application.StopAsync(stopCancellation.Token).Wait(remaining);
				}

				remaining = deadline - DateTime.UtcNow;
				if(remaining > TimeSpan.Zero) {
					application.DisposeAsync().AsTask().Wait(remaining);
				}
			}
		} catch(Exception ex) when(ex is AggregateException or OperationCanceledException or TimeoutException) {
			Log($"[MCP] Stop did not complete cleanly: {ex.GetBaseException().Message}");
		} finally {
			startCancellation?.Dispose();
			lock(_lifecycleLock) {
				_endpoint = null;
				_application = null;
				_startTask = null;
				_startCancellation = null;
				_stopping = false;
			}
			Log("[MCP] Server stopped.");
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

	private async Task StartCoreAsync(ushort port, CancellationToken cancellationToken)
	{
		WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
		builder.Logging.ClearProviders();
		builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
		builder.Services.AddMcpServer()
			.WithHttpTransport()
			.WithTools(McpTools.Create(_service));

		WebApplication application = builder.Build();
		application.MapMcp("/mcp");
		lock(_lifecycleLock) {
			_application = application;
		}

		try {
			await application.StartAsync(cancellationToken).ConfigureAwait(false);
			string address = application.Services.GetRequiredService<IServer>()
				.Features.Get<IServerAddressesFeature>()!.Addresses.Single();
			lock(_lifecycleLock) {
				_endpoint = new Uri(new Uri(address), "/mcp");
			}
			Log($"[MCP] Server started at {address}/mcp.");
		} catch(Exception ex) {
			Log($"[MCP] Server failed to start: {ex.Message}");
			lock(_lifecycleLock) {
				_application = null;
				_startTask = null;
			}
			await application.DisposeAsync().ConfigureAwait(false);
			throw;
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
}
