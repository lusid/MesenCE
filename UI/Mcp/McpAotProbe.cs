using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Mesen.Mcp;

internal static class McpAotProbe
{
	internal static WebApplication Build()
	{
		WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
		builder.Services.AddMcpServer().WithHttpTransport();
		WebApplication app = builder.Build();
		app.MapMcp("/mcp");
		return app;
	}
}
