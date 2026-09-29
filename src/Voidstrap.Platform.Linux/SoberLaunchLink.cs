using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Voidstrap.Platform.Linux;

public static class SoberLaunchLink
{
	private static readonly string[] PassThroughKeys = ["launchData", "joinAttemptId", "joinAttemptOrigin", "eventId"];

	public static Uri Normalize(Uri deeplink)
	{
		ArgumentNullException.ThrowIfNull(deeplink);
		return TryConvertWebsiteLink(deeplink.AbsoluteUri, out Uri? converted) ? converted! : deeplink;
	}

	public static bool IsHomeLaunch(Uri deeplink)
	{
		ArgumentNullException.ThrowIfNull(deeplink);
		if (!string.Equals(deeplink.Scheme, "roblox", StringComparison.OrdinalIgnoreCase))
			return false;

		string host = deeplink.Host;
		string path = deeplink.AbsolutePath.Trim('/');
		bool startLink = host.Length == 0
			|| string.Equals(host, "experiences", StringComparison.OrdinalIgnoreCase) && (path.Length == 0 || string.Equals(path, "start", StringComparison.OrdinalIgnoreCase));
		if (!startLink)
			return false;

		Dictionary<string, string> query = ParseQuery(deeplink.Query);
		return !query.ContainsKey("placeId") && !query.ContainsKey("userId");
	}

	public static string Describe(string? launchTarget)
	{
		if (string.IsNullOrWhiteSpace(launchTarget))
			return "Sober opens its home screen";

		string link = launchTarget;
		bool converted = TryConvertWebsiteLink(launchTarget, out Uri? native);
		if (converted)
			link = native!.AbsoluteUri;
		else if (launchTarget.StartsWith("roblox-player:", StringComparison.OrdinalIgnoreCase))
			return "Sober gets the website launch link unchanged because its request type has no Sober equivalent";

		if (!Uri.TryCreate(link, UriKind.Absolute, out Uri? parsed) || !string.Equals(parsed.Scheme, "roblox", StringComparison.OrdinalIgnoreCase))
			return "Sober gets the launch link unchanged";

		Dictionary<string, string> query = ParseQuery(parsed.Query);
		string prefix = converted ? "Converted the website link, " : "";
		query.TryGetValue("placeId", out string? placeId);
		if (query.TryGetValue("gameInstanceId", out string? server))
			return prefix + "Sober joins place " + placeId + " on server " + server;
		if (query.ContainsKey("accessCode") || query.ContainsKey("linkCode"))
			return prefix + "Sober joins a private server of place " + placeId;
		if (query.TryGetValue("userId", out string? userId))
			return prefix + "Sober follows user " + userId + " into their game";
		if (!string.IsNullOrEmpty(placeId))
			return prefix + "Sober joins place " + placeId;
		return prefix + "Sober opens its home screen";
	}

	public static bool TryConvertWebsiteLink(string? value, out Uri? converted)
	{
		converted = null;
		if (string.IsNullOrWhiteSpace(value) || !value.StartsWith("roblox-player:", StringComparison.OrdinalIgnoreCase))
			return false;

		Dictionary<string, string> segments = new(StringComparer.OrdinalIgnoreCase);
		foreach (string segment in value["roblox-player:".Length..].Split('+'))
		{
			int separator = segment.IndexOf(':');
			if (separator <= 0)
				continue;
			segments[segment[..separator]] = segment[(separator + 1)..];
		}

		if (!segments.TryGetValue("launchmode", out string? mode) || !string.Equals(mode, "play", StringComparison.OrdinalIgnoreCase))
			return false;
		if (!segments.TryGetValue("placelauncherurl", out string? encodedLauncher) || string.IsNullOrWhiteSpace(encodedLauncher))
			return false;

		string launcher;
		try
		{
			launcher = Uri.UnescapeDataString(encodedLauncher);
		}
		catch (UriFormatException)
		{
			return false;
		}

		int queryStart = launcher.IndexOf('?');
		if (queryStart < 0)
			return false;
		Dictionary<string, string> parameters = ParseQuery(launcher[queryStart..]);
		if (!parameters.TryGetValue("request", out string? request))
			return false;

		parameters.TryGetValue("placeId", out string? placeId);
		bool hasPlace = IsNumber(placeId);
		List<KeyValuePair<string, string>> target = [];
		switch (request.ToLowerInvariant())
		{
			case "requestgame":
				if (!hasPlace)
					return false;
				target.Add(new("placeId", placeId!));
				break;
			case "requestgamejob":
				if (!hasPlace || !parameters.TryGetValue("gameId", out string? gameId) || !IsServerId(gameId))
					return false;
				target.Add(new("placeId", placeId!));
				target.Add(new("gameInstanceId", gameId));
				break;
			case "requestprivategame":
				if (!hasPlace)
					return false;
				target.Add(new("placeId", placeId!));
				if (parameters.TryGetValue("accessCode", out string? accessCode) && !string.IsNullOrWhiteSpace(accessCode))
					target.Add(new("accessCode", accessCode));
				else if (parameters.TryGetValue("linkCode", out string? linkCode) && !string.IsNullOrWhiteSpace(linkCode))
					target.Add(new("linkCode", linkCode));
				else
					return false;
				break;
			case "requestfollowuser":
				if (!parameters.TryGetValue("userId", out string? userId) || !IsNumber(userId))
					return false;
				if (hasPlace)
					target.Add(new("placeId", placeId!));
				target.Add(new("userId", userId));
				break;
			default:
				return false;
		}

		foreach (string key in PassThroughKeys)
		{
			if (parameters.TryGetValue(key, out string? extra) && !string.IsNullOrEmpty(extra))
				target.Add(new(key, extra));
		}

		StringBuilder builder = new("roblox://experiences/start?");
		builder.AppendJoin('&', target.Select(pair => pair.Key + "=" + Uri.EscapeDataString(pair.Value)));
		return Uri.TryCreate(builder.ToString(), UriKind.Absolute, out converted);
	}

	private static Dictionary<string, string> ParseQuery(string query)
	{
		Dictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase);
		foreach (string pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
		{
			int separator = pair.IndexOf('=');
			string key = separator >= 0 ? pair[..separator] : pair;
			string raw = separator >= 0 ? pair[(separator + 1)..] : string.Empty;
			if (key.Length == 0 || values.ContainsKey(key))
				continue;
			try
			{
				values[key] = Uri.UnescapeDataString(raw.Replace('+', ' '));
			}
			catch (UriFormatException)
			{
				values[key] = raw;
			}
		}
		return values;
	}

	private static bool IsNumber(string? value)
	{
		return !string.IsNullOrEmpty(value) && value.All(char.IsAsciiDigit) && value.Length <= 20;
	}

	private static bool IsServerId(string? value)
	{
		return !string.IsNullOrEmpty(value) && value.Length <= 64 && value.All(character => char.IsAsciiHexDigit(character) || character == '-');
	}
}
