using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DiscordRPC.IO;
using DiscordRPC.Logging;

namespace Voidstrap.Integrations;

internal sealed record VoidstrapPresenceContext(
	string Owner,
	string Details,
	string State,
	string ImageUrl = "",
	string ImageText = "",
	string ButtonLabel = "",
	string ButtonUrl = "");

internal static class VoidstrapPresence
{
	private static readonly object Sync = new();

	private static VoidstrapPresenceContext? _context;

	public static VoidstrapPresenceContext? Current
	{
		get
		{
			lock (Sync)
			{
				return _context;
			}
		}
	}

	public static void Set(VoidstrapPresenceContext context)
	{
		lock (Sync)
		{
			_context = context;
		}
	}

	public static void Clear(string owner)
	{
		lock (Sync)
		{
			if (_context != null && string.Equals(_context.Owner, owner, StringComparison.Ordinal))
			{
				_context = null;
			}
		}
	}

	public static string Clip(string? value, int maxBytes)
	{
		string text = (value ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
		while (text.Contains("  ", StringComparison.Ordinal))
		{
			text = text.Replace("  ", " ", StringComparison.Ordinal);
		}
		if (Encoding.UTF8.GetByteCount(text) <= maxBytes)
		{
			return text;
		}
		const string ellipsis = "...";
		int budget = maxBytes - Encoding.UTF8.GetByteCount(ellipsis);
		StringBuilder builder = new StringBuilder();
		int used = 0;
		System.Globalization.TextElementEnumerator elements = System.Globalization.StringInfo.GetTextElementEnumerator(text);
		while (elements.MoveNext())
		{
			string element = elements.GetTextElement();
			int size = Encoding.UTF8.GetByteCount(element);
			if (used + size > budget)
			{
				break;
			}
			builder.Append(element);
			used += size;
		}
		return builder.ToString().TrimEnd() + ellipsis;
	}

	public static bool IsWebUrl(string? value)
	{
		return Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
			&& uri.Scheme == Uri.UriSchemeHttps
			&& (value?.Length ?? 0) <= 512;
	}

	public static string PlatformName { get; } = ResolvePlatformName();

	private static string ResolvePlatformName()
	{
		if (OperatingSystem.IsWindows())
		{
			return "Windows";
		}
		if (OperatingSystem.IsMacOS())
		{
			return "macOS";
		}
		foreach (string path in new[] { "/run/host/os-release", "/etc/os-release", "/usr/lib/os-release" })
		{
			try
			{
				if (!File.Exists(path))
				{
					continue;
				}
				foreach (string line in File.ReadLines(path))
				{
					if (!line.StartsWith("NAME=", StringComparison.Ordinal))
					{
						continue;
					}
					string name = line[5..].Trim().Trim('"', '\'').Trim();
					foreach (string suffix in new[] { " GNU/Linux", " Linux" })
					{
						if (name.Length > suffix.Length && name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
						{
							name = name[..^suffix.Length].TrimEnd();
						}
					}
					name = Clip(name, 40);
					if (name.Length > 0)
					{
						return name;
					}
				}
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
			}
		}
		return "Linux";
	}
}

internal sealed class DiscordActivityPipe : INamedPipeClient
{
	private readonly ManagedNamedPipeClient _inner = new();

	private readonly object _gate = new();

	private readonly string? _activityName;

	private bool _sealed;

	private bool _hasActivity;

	private long _activityPid = Environment.ProcessId;

	public DiscordActivityPipe(string? activityName = null)
	{
		_activityName = activityName;
	}

	public ILogger Logger
	{
		get => _inner.Logger;
		set => _inner.Logger = value;
	}

	public bool IsConnected => _inner.IsConnected;

	[Obsolete("The connected pipe is not neccessary information.")]
	public int ConnectedPipe => _inner.ConnectedPipe;

	public bool Connect(int pipe) => _inner.Connect(pipe);

	public bool ReadFrame(out PipeFrame frame) => _inner.ReadFrame(out frame);

	public bool WriteFrame(PipeFrame frame)
	{
		lock (_gate)
		{
			if (frame.Opcode == Opcode.Frame)
			{
				if (_sealed)
					return true;
				frame = Inspect(frame);
			}
			return _inner.WriteFrame(frame);
		}
	}

	public void ClearAndSeal()
	{
		lock (_gate)
		{
			if (_sealed)
				return;
			_sealed = true;
			if (!_hasActivity || !_inner.IsConnected)
				return;

			JsonObject payload = new()
			{
				["cmd"] = "SET_ACTIVITY",
				["args"] = new JsonObject { ["pid"] = _activityPid },
				["nonce"] = Guid.NewGuid().ToString("N")
			};
			try
			{
				if (_inner.WriteFrame(new PipeFrame { Opcode = Opcode.Frame, Message = payload.ToJsonString() }))
					_hasActivity = false;
			}
			catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
			{
			}
		}
	}

	public void Close()
	{
		lock (_gate)
			_inner.Close();
	}

	public void Dispose()
	{
		lock (_gate)
			_inner.Dispose();
	}

	private PipeFrame Inspect(PipeFrame frame)
	{
		try
		{
			if (JsonNode.Parse(frame.Message) is JsonObject payload
				&& payload["cmd"]?.GetValue<string>() == "SET_ACTIVITY"
				&& payload["args"] is JsonObject arguments)
			{
				if (arguments["pid"] is JsonValue pid && pid.TryGetValue(out long value))
					_activityPid = value;
				_hasActivity = arguments["activity"] is JsonObject;
				if (_activityName != null && arguments["activity"] is JsonObject activity)
				{
					activity["name"] = _activityName;
					frame.Message = payload.ToJsonString();
				}
			}
		}
		catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
		{
		}
		return frame;
	}
}
