using CommunityToolkit.Mvvm.ComponentModel;

namespace Mesen.Config;

public partial class McpConfig : BaseConfig<McpConfig>
{
	public const ushort DefaultPort = 7342;

	[ObservableProperty]
	public partial bool Enabled { get; set; }

	[ObservableProperty]
	public partial ushort Port { get; set; } = DefaultPort;
}
