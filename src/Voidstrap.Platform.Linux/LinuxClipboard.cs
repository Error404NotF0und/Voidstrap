using System.Runtime.InteropServices;
using System.Text;

namespace Voidstrap.Platform.Linux;

public static partial class LinuxClipboard
{
	private const int PropertyNotify = 28;
	private const int SelectionClear = 29;
	private const int SelectionRequest = 30;
	private const int SelectionNotify = 31;
	private const int ClientMessage = 33;

	private const int PropModeReplace = 0;
	private const int PropertyNewValue = 0;
	private const int XFixesSelectionNotify = 0;
	private const ulong XFixesSetSelectionOwnerNotifyMask = 1;
	private const nint AnyPropertyType = 0;
	private const nint AtomAtom = 4;
	private const nint AtomString = 31;
	private const long StructureNotifyMask = 1L << 17;
	private const long PropertyChangeMask = 1L << 22;
	private const int ReadTimeoutMilliseconds = 1000;
	private const int IncrementalTimeoutMilliseconds = 5000;

	private static readonly object Gate = new();
	private static Thread? _owner;
	private static bool _unavailable;

	private static nint _display;
	private static nint _window;
	private static nint _clipboardAtom;
	private static nint _primaryAtom;
	private static nint _targetsAtom;
	private static nint _utf8Atom;
	private static nint _textAtom;
	private static nint _mimeTextAtom;
	private static nint _wakeAtom;
	private static int _fixesEventBase = -1;

	private static string _text = string.Empty;
	private static bool _hasText;

	public static event Action? Changed;

	public static bool IsAvailable
	{
		get
		{
			if (_unavailable)
				return false;
			return EnsureOwner();
		}
	}

	public static bool TracksChanges => IsAvailable && _fixesEventBase >= 0;

	public static bool SetText(string? text)
	{
		if (!OperatingSystem.IsLinux() || !EnsureOwner())
			return false;

		lock (Gate)
		{
			_text = text ?? string.Empty;
			_hasText = true;
		}

		return Wake();
	}

	public static string? GetText()
	{
		if (!OperatingSystem.IsLinux())
			return null;

		lock (Gate)
		{
			if (_hasText && _window != 0 && XGetSelectionOwner(_display, _clipboardAtom) == _window)
				return _text;
		}

		return ReadSelection();
	}

	private static bool Wake()
	{
		lock (Gate)
		{
			if (_display == 0 || _window == 0)
				return false;

			byte[] buffer = new byte[192];
			WriteInt(buffer, 0, ClientMessage);
			WriteNint(buffer, 24, _display);
			WriteNint(buffer, 32, _window);
			WriteNint(buffer, 40, _wakeAtom);
			WriteInt(buffer, 48, 32);
			XSendEvent(_display, _window, false, 0, buffer);
			_ = XFlush(_display);
			return true;
		}
	}

	private static bool EnsureOwner()
	{
		lock (Gate)
		{
			if (_unavailable)
				return false;
			if (_owner is not null)
				return _display != 0;

			try
			{
				_ = XInitThreads();
				LinuxWindowInterop.KeepIgnoringXErrors();
				nint display = XOpenDisplay(null);
				if (display == 0)
				{
					_unavailable = true;
					return false;
				}

				nint root = XDefaultRootWindow(display);
				nint window = XCreateSimpleWindow(display, root, -10, -10, 1, 1, 0, 0, 0);
				if (window == 0)
				{
					_ = XCloseDisplay(display);
					_unavailable = true;
					return false;
				}

				_ = XSelectInput(display, window, StructureNotifyMask | PropertyChangeMask);

				_display = display;
				_window = window;
				_clipboardAtom = XInternAtom(display, "CLIPBOARD", false);
				_primaryAtom = XInternAtom(display, "PRIMARY", false);
				_targetsAtom = XInternAtom(display, "TARGETS", false);
				_utf8Atom = XInternAtom(display, "UTF8_STRING", false);
				_textAtom = XInternAtom(display, "TEXT", false);
				_mimeTextAtom = XInternAtom(display, "text/plain;charset=utf-8", false);
				_wakeAtom = XInternAtom(display, "VOIDSTRAP_CLIPBOARD_WAKE", false);
				TrackOwnerChanges(display, window);

				_owner = new Thread(PumpEvents)
				{
					IsBackground = true,
					Name = "Voidstrap clipboard"
				};
				_owner.Start();
				return true;
			}
			catch (Exception)
			{
				_unavailable = true;
				return false;
			}
		}
	}

	private static void TrackOwnerChanges(nint display, nint window)
	{
		try
		{
			if (XFixesQueryExtension(display, out int eventBase, out _) == 0)
				return;

			XFixesSelectSelectionInput(display, window, _clipboardAtom, XFixesSetSelectionOwnerNotifyMask);
			_ = XFlush(display);
			_fixesEventBase = eventBase;
		}
		catch (DllNotFoundException)
		{
		}
		catch (EntryPointNotFoundException)
		{
		}
	}

	private static void PumpEvents()
	{
		byte[] buffer = new byte[192];
		while (true)
		{
			try
			{
				XNextEvent(_display, buffer);
			}
			catch (Exception)
			{
				return;
			}

			int type = ReadInt(buffer, 0);
			if (type == ClientMessage)
			{
				lock (Gate)
				{
					if (_hasText)
					{
						_ = XSetSelectionOwner(_display, _clipboardAtom, _window, 0);
						_ = XSetSelectionOwner(_display, _primaryAtom, _window, 0);
						_ = XFlush(_display);
					}
				}
				continue;
			}

			if (type == SelectionClear)
			{
				if (ReadNint(buffer, 40) == _clipboardAtom)
				{
					lock (Gate)
					{
						_hasText = false;
						_text = string.Empty;
					}
				}
				continue;
			}

			if (type == SelectionRequest)
			{
				ServeRequest(buffer);
				continue;
			}

			if (_fixesEventBase >= 0 && type == _fixesEventBase + XFixesSelectionNotify && ReadNint(buffer, 56) == _clipboardAtom)
				RaiseChanged();
		}
	}

	private static void RaiseChanged()
	{
		Action? handlers = Changed;
		if (handlers is null)
			return;

		foreach (Delegate handler in handlers.GetInvocationList())
		{
			try
			{
				((Action)handler)();
			}
			catch (Exception)
			{
			}
		}
	}

	private static void ServeRequest(byte[] buffer)
	{
		nint requestor = ReadNint(buffer, 40);
		nint selection = ReadNint(buffer, 48);
		nint target = ReadNint(buffer, 56);
		nint property = ReadNint(buffer, 64);
		nint time = ReadNint(buffer, 72);

		if (property == 0)
			property = target;

		nint result = 0;
		try
		{
			if (target == _targetsAtom)
			{
				nint[] targets = [_targetsAtom, _utf8Atom, _mimeTextAtom, AtomString, _textAtom];
				byte[] payload = new byte[targets.Length * IntPtr.Size];
				for (int index = 0; index < targets.Length; index++)
					WriteNint(payload, index * IntPtr.Size, targets[index]);
				XChangeProperty(_display, requestor, property, AtomAtom, 32, PropModeReplace, payload, targets.Length);
				result = property;
			}
			else if (target == _utf8Atom || target == _mimeTextAtom || target == AtomString || target == _textAtom)
			{
				string text;
				lock (Gate)
				{
					text = _hasText ? _text : string.Empty;
				}

				byte[] payload = target == AtomString ? Encoding.Latin1.GetBytes(text) : Encoding.UTF8.GetBytes(text);
				nint type = target == AtomString ? AtomString : target == _mimeTextAtom ? _mimeTextAtom : _utf8Atom;
				XChangeProperty(_display, requestor, property, type, 8, PropModeReplace, payload, payload.Length);
				result = property;
			}
		}
		catch (Exception)
		{
			result = 0;
		}

		byte[] reply = new byte[192];
		WriteInt(reply, 0, SelectionNotify);
		WriteNint(reply, 24, _display);
		WriteNint(reply, 32, requestor);
		WriteNint(reply, 40, selection);
		WriteNint(reply, 48, target);
		WriteNint(reply, 56, result);
		WriteNint(reply, 64, time);
		XSendEvent(_display, requestor, false, 0, reply);
		_ = XFlush(_display);
	}

	private static string? ReadSelection()
	{
		if (!EnsureOwner())
			return null;

		nint display = 0;
		nint window = 0;
		try
		{
			display = XOpenDisplay(null);
			if (display == 0)
				return null;

			nint root = XDefaultRootWindow(display);
			window = XCreateSimpleWindow(display, root, -10, -10, 1, 1, 0, 0, 0);
			if (window == 0)
				return null;

			_ = XSelectInput(display, window, PropertyChangeMask);
			nint clipboard = XInternAtom(display, "CLIPBOARD", false);
			nint destination = XInternAtom(display, "VOIDSTRAP_CLIPBOARD_IN", false);
			nint incremental = XInternAtom(display, "INCR", false);
			nint[] targets =
			[
				XInternAtom(display, "UTF8_STRING", false),
				XInternAtom(display, "text/plain;charset=utf-8", false),
				AtomString
			];

			if (XGetSelectionOwner(display, clipboard) == 0)
				return null;

			foreach (nint target in targets)
			{
				string? text = ConvertSelection(display, window, clipboard, target, destination, incremental);
				if (text is not null)
					return text;
			}

			return null;
		}
		catch (Exception)
		{
			return null;
		}
		finally
		{
			try
			{
				if (window != 0)
					_ = XDestroyWindow(display, window);
				if (display != 0)
					_ = XCloseDisplay(display);
			}
			catch (Exception)
			{
			}
		}
	}

	private static string? ConvertSelection(nint display, nint window, nint selection, nint target, nint destination, nint incremental)
	{
		_ = XConvertSelection(display, selection, target, destination, window, 0);
		_ = XFlush(display);

		byte[] buffer = new byte[192];
		if (!WaitForEvent(display, buffer, ReadTimeoutMilliseconds, static (message, _) => ReadInt(message, 0) == SelectionNotify, 0))
			return null;

		nint property = ReadNint(buffer, 56);
		if (property == 0)
			return null;

		if (!ReadProperty(display, window, property, out nint type, out int format, out byte[] bytes))
			return null;

		if (type == incremental)
			return ReadIncremental(display, window, property, target);

		if (format != 8)
			return null;

		return Decode(bytes, target);
	}

	private static string? ReadIncremental(nint display, nint window, nint property, nint target)
	{
		using MemoryStream collected = new();
		byte[] buffer = new byte[192];
		long deadline = Environment.TickCount64 + IncrementalTimeoutMilliseconds;
		while (Environment.TickCount64 < deadline)
		{
			int remaining = (int)Math.Max(1, deadline - Environment.TickCount64);
			bool arrived = WaitForEvent(
				display,
				buffer,
				remaining,
				static (message, watched) => ReadInt(message, 0) == PropertyNotify && ReadNint(message, 40) == watched && ReadInt(message, 56) == PropertyNewValue,
				property);
			if (!arrived)
				return null;

			if (!ReadProperty(display, window, property, out _, out int format, out byte[] chunk))
				return null;

			if (chunk.Length == 0)
				return Decode(collected.ToArray(), target);

			if (format != 8)
				return null;

			collected.Write(chunk, 0, chunk.Length);
		}

		return null;
	}

	private static bool WaitForEvent(nint display, byte[] buffer, int timeoutMilliseconds, Func<byte[], nint, bool> match, nint argument)
	{
		long deadline = Environment.TickCount64 + timeoutMilliseconds;
		while (Environment.TickCount64 < deadline)
		{
			if (XPending(display) == 0)
			{
				Thread.Sleep(2);
				continue;
			}

			XNextEvent(display, buffer);
			if (match(buffer, argument))
				return true;
		}

		return false;
	}

	private static string Decode(byte[] bytes, nint target)
	{
		return target == AtomString ? Encoding.Latin1.GetString(bytes) : Encoding.UTF8.GetString(bytes);
	}

	private static bool ReadProperty(nint display, nint window, nint property, out nint type, out int format, out byte[] bytes)
	{
		type = 0;
		format = 0;
		bytes = [];
		nint data = 0;
		try
		{
			int status = XGetWindowProperty(
				display,
				window,
				property,
				0,
				int.MaxValue / 4,
				true,
				AnyPropertyType,
				out type,
				out format,
				out nint count,
				out _,
				out data);

			if (status != 0)
				return false;

			if (count <= 0 || data == 0)
				return true;

			int unit = format switch
			{
				8 => 1,
				16 => 2,
				32 => IntPtr.Size,
				_ => 0
			};
			if (unit == 0)
				return false;

			bytes = new byte[(int)count * unit];
			Marshal.Copy(data, bytes, 0, bytes.Length);
			return true;
		}
		catch (Exception)
		{
			return false;
		}
		finally
		{
			if (data != 0)
			{
				try { _ = XFree(data); } catch (Exception) { }
			}
		}
	}

	private static int ReadInt(byte[] buffer, int offset) => BitConverter.ToInt32(buffer, offset);

	private static nint ReadNint(byte[] buffer, int offset) => (nint)BitConverter.ToInt64(buffer, offset);

	private static void WriteInt(byte[] buffer, int offset, int value) => BitConverter.GetBytes(value).CopyTo(buffer, offset);

	private static void WriteNint(byte[] buffer, int offset, nint value) => BitConverter.GetBytes((long)value).CopyTo(buffer, offset);

	[LibraryImport("libX11.so.6")]
	private static partial int XInitThreads();

	[LibraryImport("libX11.so.6", StringMarshalling = StringMarshalling.Utf8)]
	private static partial nint XOpenDisplay(string? display);

	[LibraryImport("libX11.so.6")]
	private static partial int XCloseDisplay(nint display);

	[LibraryImport("libX11.so.6")]
	private static partial nint XDefaultRootWindow(nint display);

	[LibraryImport("libX11.so.6")]
	private static partial nint XCreateSimpleWindow(nint display, nint parent, int x, int y, uint width, uint height, uint borderWidth, nint border, nint background);

	[LibraryImport("libX11.so.6")]
	private static partial int XDestroyWindow(nint display, nint window);

	[LibraryImport("libX11.so.6")]
	private static partial int XSelectInput(nint display, nint window, long mask);

	[LibraryImport("libX11.so.6", StringMarshalling = StringMarshalling.Utf8)]
	private static partial nint XInternAtom(nint display, string name, [MarshalAs(UnmanagedType.Bool)] bool onlyIfExists);

	[LibraryImport("libX11.so.6")]
	private static partial int XSetSelectionOwner(nint display, nint selection, nint owner, nint time);

	[LibraryImport("libX11.so.6")]
	private static partial nint XGetSelectionOwner(nint display, nint selection);

	[LibraryImport("libX11.so.6")]
	private static partial int XConvertSelection(nint display, nint selection, nint target, nint property, nint requestor, nint time);

	[LibraryImport("libX11.so.6")]
	private static partial int XChangeProperty(nint display, nint window, nint property, nint type, int format, int mode, [In] byte[] data, int count);

	[LibraryImport("libX11.so.6")]
	private static partial int XGetWindowProperty(
		nint display,
		nint window,
		nint property,
		nint offset,
		nint length,
		[MarshalAs(UnmanagedType.Bool)] bool delete,
		nint requestedType,
		out nint actualType,
		out int actualFormat,
		out nint itemCount,
		out nint bytesAfter,
		out nint data);

	[LibraryImport("libX11.so.6")]
	private static partial int XSendEvent(nint display, nint window, [MarshalAs(UnmanagedType.Bool)] bool propagate, long mask, [In] byte[] eventData);

	[LibraryImport("libX11.so.6")]
	private static partial int XNextEvent(nint display, [In, Out] byte[] eventData);

	[LibraryImport("libX11.so.6")]
	private static partial int XPending(nint display);

	[LibraryImport("libX11.so.6")]
	private static partial int XFlush(nint display);

	[LibraryImport("libX11.so.6")]
	private static partial int XFree(nint data);

	[LibraryImport("libXfixes.so.3")]
	private static partial int XFixesQueryExtension(nint display, out int eventBase, out int errorBase);

	[LibraryImport("libXfixes.so.3")]
	private static partial void XFixesSelectSelectionInput(nint display, nint window, nint selection, ulong eventMask);
}
