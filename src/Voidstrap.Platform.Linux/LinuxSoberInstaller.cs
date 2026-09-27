using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Voidstrap.Platform.Linux;

public enum SoberInstallationStatus
{
	FlatpakMissing,
	NotInstalled,
	Installed
}

public sealed record SoberInstallationState(SoberInstallationStatus Status, string? Version, string Message);

public sealed partial class LinuxSoberInstaller
{
	private const string SoberApplicationId = "org.vinegarhq.Sober";
	private const string StalledCode = "SoberInstallStalled";
	private static readonly TimeSpan StallTimeout = TimeSpan.FromMinutes(10);
	private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);
	private const string RemoteName = "flathub";
	private const string RemoteUrl = "https://dl.flathub.org/repo/flathub.flatpakrepo";
	private const string ReferenceUrl = "https://sober.vinegarhq.org/sober.flatpakref";
	private const string FlatpakMissingMessage = "Flatpak is not installed. Installing Sober will set it up automatically.";

	private readonly IProcessService _processes;

	private static readonly object InstallGate = new();
	private static Task<OperationResult>? _activeInstall;
	private static Action<string>? _activeReport;
	private static string _lastStatus = string.Empty;

	public LinuxSoberInstaller(IProcessService processes)
	{
		_processes = processes ?? throw new ArgumentNullException(nameof(processes));
	}

	public static bool CanInstall(CapabilityDescriptor capability)
	{
		ArgumentNullException.ThrowIfNull(capability);
		return capability.State == CapabilityState.RequiresExternalRuntime;
	}

	public async Task<SoberInstallationState> DetectAsync(CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		if (!LinuxFlatpakHost.TryCreateCommand(_processes, ["info", SoberApplicationId], out ProcessCommand command))
		{
			return new SoberInstallationState(SoberInstallationStatus.FlatpakMissing, null, FlatpakMissingMessage);
		}

		OperationResult<ProcessExecution> result = await _processes
			.ExecuteAsync(command, cancellationToken)
			.ConfigureAwait(false);
		if (!result.Succeeded || result.Value is null || result.Value.ExitCode != 0)
		{
			return new SoberInstallationState(SoberInstallationStatus.NotInstalled, null, "Sober is not installed");
		}

		string? version = FlatpakApplicationInfo.ParseVersion(result.Value.StandardOutput);
		return new SoberInstallationState(
			SoberInstallationStatus.Installed,
			string.IsNullOrWhiteSpace(version) ? null : version,
			string.IsNullOrWhiteSpace(version) ? "Sober is installed" : "Sober " + version + " is installed");
	}

	public Task<OperationResult> InstallAsync(CancellationToken cancellationToken = default, Action<string>? report = null)
	{
		lock (InstallGate)
		{
			if (report is not null)
				_activeReport += report;

			if (_activeInstall is { IsCompleted: false })
			{
				if (report is not null && _lastStatus.Length > 0)
					report(_lastStatus);
				return _activeInstall.WaitAsync(cancellationToken);
			}

			_lastStatus = string.Empty;
			_activeInstall = RunInstallAsync(cancellationToken);
			return _activeInstall;
		}
	}

	public static bool IsInstallRunningElsewhere()
	{
		return FindInstallElsewhere() != 0;
	}

	private static int FindInstallElsewhere()
	{
		try
		{
			foreach (string directory in Directory.EnumerateDirectories("/proc"))
			{
				if (!int.TryParse(Path.GetFileName(directory), out int processId) || processId == Environment.ProcessId)
					continue;

				string commandLine;
				try
				{
					commandLine = File.ReadAllText(Path.Combine(directory, "cmdline"));
				}
				catch (Exception)
				{
					continue;
				}

				if (commandLine.Contains("flatpak", StringComparison.Ordinal)
					&& commandLine.Contains("\0install\0", StringComparison.Ordinal)
					&& (commandLine.Contains(SoberApplicationId, StringComparison.Ordinal) || commandLine.Contains(ReferenceUrl, StringComparison.Ordinal)))
				{
					return processId;
				}
			}
		}
		catch (Exception)
		{
		}

		return 0;
	}

	private async Task<OperationResult> RunInstallAsync(CancellationToken cancellationToken)
	{
		try
		{
			return await InstallOnceAsync(cancellationToken).ConfigureAwait(false);
		}
		finally
		{
			lock (InstallGate)
			{
				_activeReport = null;
				_lastStatus = string.Empty;
			}
		}
	}

	private static void Report(string message)
	{
		Action<string>? report;
		lock (InstallGate)
		{
			_lastStatus = message;
			report = _activeReport;
		}

		try
		{
			report?.Invoke(message);
		}
		catch (Exception)
		{
		}
	}

	private async Task<OperationResult> InstallOnceAsync(CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		if (!LinuxFlatpakHost.TryCreateCommand(_processes, [], out _))
		{
			Report("Installing Flatpak");
			OperationResult flatpak = await LinuxInstallationUpdates.InstallFlatpakPrerequisiteAsync(_processes, CancellationToken.None).ConfigureAwait(false);
			cancellationToken.ThrowIfCancellationRequested();
			if (!flatpak.Succeeded)
				return flatpak;
		}

		int other = FindInstallElsewhere();
		if (other != 0)
		{
			await WaitForInstallElsewhereAsync(other, cancellationToken).ConfigureAwait(false);
			if ((await DetectAsync(cancellationToken).ConfigureAwait(false)).Status == SoberInstallationStatus.Installed)
				return OperationResult.Success();
		}

		Report("Adding Flathub");
		OperationResult remote = await RunAsync(
			["remote-add", "--if-not-exists", "--user", RemoteName, RemoteUrl],
			"FlathubRemoteFailed",
			"The Flathub repository could not be added",
			cancellationToken).ConfigureAwait(false);

		if (remote.Succeeded)
		{
			OperationResult fromRemote = await InstallTargetAsync([RemoteName, SoberApplicationId], "Installing Sober from Flathub", cancellationToken).ConfigureAwait(false);
			if (fromRemote.Succeeded || fromRemote.Failure?.Code == StalledCode)
			{
				return fromRemote;
			}

			OperationResult fromReference = await InstallTargetAsync([ReferenceUrl], "Trying the Sober download reference", cancellationToken).ConfigureAwait(false);
			return fromReference.Succeeded ? fromReference : fromRemote;
		}

		OperationResult referenceOnly = await InstallTargetAsync([ReferenceUrl], "Installing Sober from its download reference", cancellationToken).ConfigureAwait(false);
		return referenceOnly.Succeeded ? referenceOnly : remote;
	}

	private static async Task WaitForInstallElsewhereAsync(int processId, CancellationToken cancellationToken)
	{
		string log = InstallLogPath;
		bool shared = WritesTo(processId, log);
		Report(shared ? "Installing Sober" : "Waiting for another Sober install to finish");
		long lastLength = -1;
		DateTime lastChange = DateTime.UtcNow;
		while (FindInstallElsewhere() != 0)
		{
			await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
			if (!shared)
				continue;

			(long length, string line) = ReadLogTail(log);
			if (length != lastLength)
			{
				lastLength = length;
				lastChange = DateTime.UtcNow;
				string? progress = DescribeProgress(line);
				if (progress is not null)
					Report("Installing Sober: " + progress);
			}
			else if (DateTime.UtcNow - lastChange > StallTimeout)
			{
				TryKill(processId);
				return;
			}
		}
	}

	private static string InstallLogPath
	{
		get
		{
			string? cache = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
			if (string.IsNullOrWhiteSpace(cache) || !Path.IsPathRooted(cache))
				cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache");
			return Path.Combine(cache, "voidstrap", "sober-install.log");
		}
	}

	private static bool WritesTo(int processId, string path)
	{
		try
		{
			string? target = new FileInfo(Path.Combine("/proc", processId.ToString(CultureInfo.InvariantCulture), "fd", "1")).LinkTarget;
			return target is not null && string.Equals(target, Path.GetFullPath(path), StringComparison.Ordinal);
		}
		catch (Exception)
		{
			return false;
		}
	}

	private static (long Length, string Line) ReadLogTail(string path)
	{
		try
		{
			using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
			long length = stream.Length;
			int size = (int)Math.Min(length, 4096);
			stream.Seek(length - size, SeekOrigin.Begin);
			byte[] buffer = new byte[size];
			stream.ReadExactly(buffer);
			string text = Encoding.UTF8.GetString(buffer);
			int end = text.LastIndexOf('\n');
			if (end < 0)
				return (length, string.Empty);
			int start = end == 0 ? 0 : text.LastIndexOf('\n', end - 1) + 1;
			return (length, text[start..end].Trim());
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			return (-1, string.Empty);
		}
	}

	private static string ReadLogText(string path)
	{
		try
		{
			using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
			int size = (int)Math.Min(stream.Length, 262144);
			stream.Seek(stream.Length - size, SeekOrigin.Begin);
			byte[] buffer = new byte[size];
			stream.ReadExactly(buffer);
			return Encoding.UTF8.GetString(buffer);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			return string.Empty;
		}
	}

	private static void TryKill(int processId)
	{
		try
		{
			using Process process = Process.GetProcessById(processId);
			process.Kill(true);
		}
		catch (Exception)
		{
		}
	}

	private static string? DescribeProgress(string line)
	{
		Match step = StepPattern().Match(line);
		Match percent = PercentPattern().Match(line);
		Match speed = SpeedPattern().Match(line);
		List<string> parts = [];
		if (step.Success)
			parts.Add(step.Groups[1].Value + " of " + step.Groups[2].Value);
		if (percent.Success)
			parts.Add(percent.Value);
		if (speed.Success)
			parts.Add(speed.Value.Replace('\u00a0', ' '));
		return parts.Count == 0 ? null : string.Join(", ", parts);
	}

	[GeneratedRegex(@"(\d+)/(\d+)\u2026", RegexOptions.CultureInvariant)]
	private static partial Regex StepPattern();

	[GeneratedRegex(@"\d{1,3}%", RegexOptions.CultureInvariant)]
	private static partial Regex PercentPattern();

	[GeneratedRegex(@"[\d.,]+\s(?:bytes|[kMGT]B)/s", RegexOptions.CultureInvariant)]
	private static partial Regex SpeedPattern();

	public async Task<OperationResult> UninstallAsync(CancellationToken cancellationToken = default)
	{
		if (!LinuxFlatpakHost.TryCreateCommand(_processes, ["kill", SoberApplicationId], out ProcessCommand killCommand))
			return OperationResult.Fail("FlatpakMissing", FlatpakMissingMessage);

		try
		{
			await _processes
				.ExecuteAsync(killCommand, cancellationToken)
				.ConfigureAwait(false);
		}
		catch (Exception)
		{
		}

		OperationResult user = await RunAsync(
			["uninstall", "--user", "--assumeyes", "--noninteractive", "--delete-data", SoberApplicationId],
			"SoberUninstallFailed",
			"Sober could not be removed",
			cancellationToken).ConfigureAwait(false);

		if (user.Succeeded)
			return user;

		return await RunAsync(
			["uninstall", "--assumeyes", "--noninteractive", "--delete-data", SoberApplicationId],
			"SoberUninstallFailed",
			"Sober could not be removed",
			cancellationToken).ConfigureAwait(false);
	}

	private async Task<OperationResult> InstallTargetAsync(
		IReadOnlyList<string> target,
		string stage,
		CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		Report(stage);
		List<string> arguments = ["install", "--user", "--assumeyes", "--or-update"];
		arguments.AddRange(target);
		if (!LinuxFlatpakHost.TryCreateCommand(_processes, arguments, out ProcessCommand command))
			return OperationResult.Fail("FlatpakMissing", FlatpakMissingMessage, CapabilityState.RequiresExternalRuntime);

		string log = InstallLogPath;
		Process? started;
		try
		{
			Directory.CreateDirectory(Path.GetDirectoryName(log)!);
			File.WriteAllText(log, string.Empty);
			ProcessStartInfo startInfo = new("/bin/sh")
			{
				UseShellExecute = false,
				CreateNoWindow = true
			};
			startInfo.ArgumentList.Add("-c");
			startInfo.ArgumentList.Add("trap '' HUP PIPE; exec \"$@\" >>\"$VOIDSTRAP_SOBER_INSTALL_LOG\" 2>&1 </dev/null");
			startInfo.ArgumentList.Add("sh");
			startInfo.ArgumentList.Add(command.FileName);
			foreach (string argument in command.Arguments)
				startInfo.ArgumentList.Add(argument);
			startInfo.Environment["VOIDSTRAP_SOBER_INSTALL_LOG"] = log;
			started = Process.Start(startInfo);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
		{
			return OperationResult.Fail("SoberInstallFailed", "Sober could not be installed: " + exception.Message);
		}

		if (started is null)
			return OperationResult.Fail("SoberInstallFailed", "Sober could not be installed: Flatpak did not start");

		using Process process = started;
		using CancellationTokenRegistration stopOnCancel = cancellationToken.Register(KillQuietly, process);
		long lastLength = 0;
		DateTime lastChange = DateTime.UtcNow;
		try
		{
			while (!process.HasExited)
			{
				try
				{
					await process.WaitForExitAsync(cancellationToken).WaitAsync(PollInterval, cancellationToken).ConfigureAwait(false);
				}
				catch (TimeoutException)
				{
				}

				(long length, string line) = ReadLogTail(log);
				if (length != lastLength)
				{
					lastLength = length;
					lastChange = DateTime.UtcNow;
					string? progress = DescribeProgress(line);
					if (progress is not null)
						Report(stage + ": " + progress);
				}
				else if (!process.HasExited && DateTime.UtcNow - lastChange > StallTimeout)
				{
					KillQuietly(process);
					return OperationResult.Fail(StalledCode, "Sober could not be installed: Flatpak made no progress for " + (int)StallTimeout.TotalMinutes + " minutes. Check your internet connection and that no other Flatpak install or update is running, then try again.");
				}
			}
		}
		catch (OperationCanceledException)
		{
			KillQuietly(process);
			throw;
		}

		if (process.ExitCode == 0)
			return OperationResult.Success();

		return OperationResult.Fail("SoberInstallFailed", DescribeFailure(ReadLogText(log)));
	}

	private static string DescribeFailure(string detail)
	{
		string[] lines = detail.Replace("\r", string.Empty, StringComparison.Ordinal)
			.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		string line = lines.LastOrDefault(static candidate => candidate.StartsWith("error:", StringComparison.OrdinalIgnoreCase))
			?? lines.LastOrDefault()
			?? string.Empty;
		string reason = line.StartsWith("error:", StringComparison.OrdinalIgnoreCase) ? line[6..].Trim() : line;
		int remote = reason.IndexOf(" from remote ", StringComparison.Ordinal);
		int separator = remote < 0 ? -1 : reason.IndexOf(": ", remote, StringComparison.Ordinal);
		if (separator >= 0)
			reason = reason[(separator + 2)..].Trim();
		string full = reason;
		int nested;
		while (reason.StartsWith("While ", StringComparison.Ordinal) && (nested = reason.IndexOf(": ", StringComparison.Ordinal)) >= 0)
			reason = reason[(nested + 2)..].Trim();
		int code = reason.StartsWith('[') ? reason.IndexOf("] ", StringComparison.Ordinal) : -1;
		if (code > 0)
			reason = reason[(code + 2)..].Trim();

		string advice;
		if (ContainsAny(full, "space", "ENOSPC", "min-free"))
			advice = "There is not enough free disk space. Sober and the runtimes it needs take about 2 GB, so free up some space and try again.";
		else if (ContainsAny(full, "resolve", "timeout", "timed out", "connect", "network", "status 5", "fetching", "curl", "TLS", "SSL", "reset"))
			advice = "Flathub could not be reached or stopped responding. Check your internet connection and try again, Flathub can also be busy for a few minutes.";
		else if (ContainsAny(full, "lock"))
			advice = "Another Flatpak install or update is running. Wait for it to finish and try again.";
		else
			advice = "Try again, and if it keeps failing run flatpak install flathub org.vinegarhq.Sober in a terminal to see the full error.";

		return reason.Length == 0
			? "Sober could not be installed. " + advice
			: "Sober could not be installed. " + advice + "\n\nFlatpak said: " + reason;
	}

	private static bool ContainsAny(string text, params string[] markers)
	{
		return markers.Any(marker => text.Contains(marker, StringComparison.OrdinalIgnoreCase));
	}

	private static void KillQuietly(object? state)
	{
		try
		{
			((Process)state!).Kill(true);
		}
		catch (Exception)
		{
		}
	}

	private async Task<OperationResult> RunAsync(
		IReadOnlyList<string> arguments,
		string failureCode,
		string failureMessage,
		CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		if (!LinuxFlatpakHost.TryCreateCommand(_processes, arguments, out ProcessCommand command))
			return OperationResult.Fail("FlatpakMissing", FlatpakMissingMessage, CapabilityState.RequiresExternalRuntime);

		OperationResult<ProcessExecution> result = await _processes
			.ExecuteAsync(command, cancellationToken)
			.ConfigureAwait(false);
		cancellationToken.ThrowIfCancellationRequested();
		if (!result.Succeeded || result.Value is null)
		{
			return result.Failure is null
				? OperationResult.Fail(failureCode, failureMessage)
				: OperationResult.Fail(failureCode, failureMessage + ": " + result.Failure.Message, result.Failure.State);
		}

		if (result.Value.ExitCode != 0)
		{
			string detail = string.IsNullOrWhiteSpace(result.Value.StandardError)
				? result.Value.StandardOutput
				: result.Value.StandardError;
			return string.IsNullOrWhiteSpace(detail)
				? OperationResult.Fail(failureCode, failureMessage)
				: OperationResult.Fail(failureCode, failureMessage + ": " + detail.Trim());
		}

		return OperationResult.Success();
	}
}
