namespace Voidstrap.Platform.Linux;

public enum SoberGraphicsOptimizationMode
{
	Quality,
	Balanced,
	Performance
}

public enum SoberTouchMode
{
	Off,
	On,
	FakeOff
}

public sealed record SoberNativeConfigurationOptions(
	bool? AllowGamepadPermission = null,
	bool? CloseOnLeave = null,
	bool? DiscordRpcEnabled = null,
	bool? DiscordRpcShowJoinButton = null,
	bool? EnableGameMode = null,
	bool? EnableHiDpi = null,
	bool? EnableMobileHomeScreen = null,
	SoberGraphicsOptimizationMode? GraphicsOptimizationMode = null,
	bool? ServerLocationIndicatorEnabled = null,
	SoberTouchMode? TouchMode = null,
	bool? UseConsoleExperience = null,
	bool? UseLibsecret = null,
	bool? UseOpenGl = null);

public static class SoberNativeSettings
{
	private const string ServerLocationIndicatorKey = "server_location_indicator_enabled";

	private static readonly System.Text.Json.JsonDocumentOptions DocumentOptions = new()
	{
		AllowTrailingCommas = true,
		CommentHandling = System.Text.Json.JsonCommentHandling.Skip
	};

	public static bool IsServerLocationIndicatorEnabled()
	{
		return ReadBoolean(ServerLocationIndicatorKey);
	}

	private static bool ReadBoolean(string name)
	{
		string home = Environment.GetEnvironmentVariable("HOME") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
		string path = Path.Combine(home, ".var", "app", "org.vinegarhq.Sober", "config", "sober", "config.json");
		try
		{
			if (!File.Exists(path))
				return false;
			using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path), DocumentOptions);
			return document.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
				&& document.RootElement.TryGetProperty(name, out System.Text.Json.JsonElement value)
				&& value.ValueKind == System.Text.Json.JsonValueKind.True;
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
		{
			return false;
		}
	}
}

public sealed record LinuxModSource(string RelativePath, string SourcePath);

public sealed record LinuxPlayerPreparationOptions(
	bool UseFastFlagManager = true,
	SoberNativeConfigurationOptions? NativeConfiguration = null,
	bool ApplyModifications = true,
	IReadOnlyList<LinuxModSource>? AdditionalModSources = null,
	Func<string, bool>? IgnoreModFile = null);

public interface ISoberProcessProbe
{
	Task<bool> IsRunningAsync(CancellationToken cancellationToken = default);
}

public sealed class LinuxSoberProcessProbe : ISoberProcessProbe
{
	private const string SoberApplicationId = "org.vinegarhq.Sober";

	private readonly IProcessService _processes;

	public LinuxSoberProcessProbe(IProcessService processes)
	{
		_processes = processes ?? throw new ArgumentNullException(nameof(processes));
	}

	private const string SoberProcessName = "sober";

	public static bool IsRunningNow()
	{
		return GetSandboxProcessIds().Count > 0;
	}

	public static IReadOnlyList<int> GetSandboxProcessIds()
	{
		List<int> processIds = [];
		string[] directories;
		try
		{
			directories = Directory.GetDirectories("/proc");
		}
		catch (Exception)
		{
			return processIds;
		}

		foreach (string directory in directories)
		{
			string name = Path.GetFileName(directory);
			if (name.Length == 0
				|| !char.IsAsciiDigit(name[0])
				|| !int.TryParse(name, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int processId))
				continue;

			try
			{
				if (File.ReadAllText(Path.Combine(directory, "cgroup")).Contains(SoberApplicationId, StringComparison.OrdinalIgnoreCase))
				{
					processIds.Add(processId);
					continue;
				}
			}
			catch (IOException)
			{
			}
			catch (UnauthorizedAccessException)
			{
			}
			try
			{
				if (string.Equals(File.ReadAllText(Path.Combine(directory, "comm")).Trim(), SoberProcessName, StringComparison.OrdinalIgnoreCase))
					processIds.Add(processId);
			}
			catch (IOException)
			{
			}
			catch (UnauthorizedAccessException)
			{
			}
		}

		return processIds;
	}

	public async Task<bool> IsRunningAsync(CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();

		if (IsRunningNow())
			return true;

		if (!LinuxFlatpakHost.TryCreateCommand(_processes, ["ps", "--columns=application"], out ProcessCommand command))
			return false;

		OperationResult<ProcessExecution> result = await _processes
			.ExecuteAsync(command, cancellationToken)
			.ConfigureAwait(false);
		if (!result.Succeeded || result.Value is null || result.Value.ExitCode != 0)
			return false;

		foreach (string line in result.Value.StandardOutput.Split('\n'))
		{
			if (string.Equals(line.Trim(), SoberApplicationId, StringComparison.Ordinal))
				return true;
		}

		return false;
	}
}
