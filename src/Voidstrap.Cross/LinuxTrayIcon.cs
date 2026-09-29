using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Tmds.DBus;

namespace Voidstrap.UI.Tray;

[Dictionary]
public class StatusNotifierItemProperties
{
    public string Category = "ApplicationStatus";
    public string Id = "Voidstrap";
    public string Title = "Voidstrap";
    public string Status = "Active";
    public string IconName = "";
    public (int, int, byte[])[] IconPixmap = [];
    public string IconThemePath = "";
    public string AttentionIconName = "";
    public (int, int, byte[])[] AttentionIconPixmap = [];
    public string OverlayIconName = "";
    public (int, int, byte[])[] OverlayIconPixmap = [];
    public (string, (int, int, byte[])[], string, string) ToolTip = ("", [], "Voidstrap", "");
    public bool ItemIsMenu = true;
    public int WindowId = 0;
    public ObjectPath Menu = DbusMenuObject.Path;
}

[DBusInterface("org.kde.StatusNotifierItem")]
public interface IStatusNotifierItem : IDBusObject
{
    Task ActivateAsync(int X, int Y);
    Task SecondaryActivateAsync(int X, int Y);
    Task ContextMenuAsync(int X, int Y);
    Task ScrollAsync(int Delta, string Orientation);
    Task<IDisposable> WatchNewIconAsync(Action handler, Action<Exception>? onError = null);
    Task<IDisposable> WatchNewTitleAsync(Action handler, Action<Exception>? onError = null);
    Task<IDisposable> WatchNewToolTipAsync(Action handler, Action<Exception>? onError = null);
    Task<IDisposable> WatchNewStatusAsync(Action<string> handler, Action<Exception>? onError = null);
    Task<object> GetAsync(string prop);
    Task<StatusNotifierItemProperties> GetAllAsync();
    Task SetAsync(string prop, object val);
}

[DBusInterface("org.kde.StatusNotifierWatcher")]
public interface IStatusNotifierWatcher : IDBusObject
{
    Task RegisterStatusNotifierItemAsync(string Service);
}

[DBusInterface("org.freedesktop.Notifications")]
public interface IFreedesktopNotifications : IDBusObject
{
    Task<uint> NotifyAsync(string AppName, uint ReplacesId, string AppIcon, string Summary, string Body, string[] Actions, IDictionary<string, object> Hints, int ExpireTimeout);
}

public sealed class StatusNotifierItemObject : IStatusNotifierItem
{
    public static readonly ObjectPath Path = new ObjectPath("/StatusNotifierItem");

    private readonly object _gate = new();

    private readonly StatusNotifierItemProperties _properties = new();

    private readonly List<Action> _newIconHandlers = [];

    private readonly List<Action> _newTitleHandlers = [];

    private readonly List<Action> _newToolTipHandlers = [];

    private readonly List<Action<string>> _newStatusHandlers = [];

    public ObjectPath ObjectPath => Path;

    public event Action? Activated;

    public event Action? SecondaryActivated;

    public event Action? ContextMenuRequested;

    public void SetTitle(string title)
    {
        string shown = string.IsNullOrWhiteSpace(title) ? "Voidstrap" : title;
        lock (_gate)
        {
            _properties.Title = shown;
            _properties.ToolTip = (_properties.ToolTip.Item1, _properties.ToolTip.Item2, shown, _properties.ToolTip.Item4);
        }
        Raise(_newTitleHandlers);
        Raise(_newToolTipHandlers);
    }

    public void SetToolTip(string description)
    {
        lock (_gate)
        {
            _properties.ToolTip = (_properties.ToolTip.Item1, _properties.ToolTip.Item2, _properties.ToolTip.Item3, description ?? string.Empty);
        }
        Raise(_newToolTipHandlers);
    }

    public void SetIcon((int, int, byte[])[] pixmaps)
    {
        lock (_gate)
        {
            _properties.IconPixmap = pixmaps;
            _properties.ToolTip = (_properties.ToolTip.Item1, pixmaps, _properties.ToolTip.Item3, _properties.ToolTip.Item4);
        }
        Raise(_newIconHandlers);
        Raise(_newToolTipHandlers);
    }

    private void Raise(List<Action> handlers)
    {
        Action[] snapshot;
        lock (_gate)
        {
            snapshot = [.. handlers];
        }
        foreach (Action handler in snapshot)
        {
            try
            {
                handler();
            }
            catch (Exception ex)
            {
                Voidstrap.App.Logger?.WriteLine("LinuxTray::Signal", "A tray change could not be announced: " + ex.Message);
            }
        }
    }

    public Task ActivateAsync(int X, int Y)
    {
        Activated?.Invoke();
        return Task.CompletedTask;
    }

    public Task SecondaryActivateAsync(int X, int Y)
    {
        SecondaryActivated?.Invoke();
        return Task.CompletedTask;
    }

    public Task ContextMenuAsync(int X, int Y)
    {
        ContextMenuRequested?.Invoke();
        return Task.CompletedTask;
    }

    public Task ScrollAsync(int Delta, string Orientation)
    {
        return Task.CompletedTask;
    }

    public Task<IDisposable> WatchNewIconAsync(Action handler, Action<Exception>? onError = null)
    {
        return Subscribe(_newIconHandlers, handler);
    }

    public Task<IDisposable> WatchNewTitleAsync(Action handler, Action<Exception>? onError = null)
    {
        return Subscribe(_newTitleHandlers, handler);
    }

    public Task<IDisposable> WatchNewToolTipAsync(Action handler, Action<Exception>? onError = null)
    {
        return Subscribe(_newToolTipHandlers, handler);
    }

    public Task<IDisposable> WatchNewStatusAsync(Action<string> handler, Action<Exception>? onError = null)
    {
        return Subscribe(_newStatusHandlers, handler);
    }

    private Task<IDisposable> Subscribe<T>(List<T> handlers, T handler)
    {
        lock (_gate)
        {
            handlers.Add(handler);
        }
        return Task.FromResult<IDisposable>(new Subscription<T>(this, handlers, handler));
    }

    private sealed class Subscription<T> : IDisposable
    {
        private readonly StatusNotifierItemObject _owner;

        private readonly List<T> _handlers;

        private readonly T _handler;

        internal Subscription(StatusNotifierItemObject owner, List<T> handlers, T handler)
        {
            _owner = owner;
            _handlers = handlers;
            _handler = handler;
        }

        public void Dispose()
        {
            lock (_owner._gate)
            {
                _handlers.Remove(_handler);
            }
        }
    }

    public Task<object> GetAsync(string prop)
    {
        lock (_gate)
        {
            return Task.FromResult(prop switch
            {
                "Category" => (object)_properties.Category,
                "Id" => _properties.Id,
                "Title" => _properties.Title,
                "Status" => _properties.Status,
                "IconName" => _properties.IconName,
                "IconPixmap" => _properties.IconPixmap,
                "IconThemePath" => _properties.IconThemePath,
                "AttentionIconName" => _properties.AttentionIconName,
                "AttentionIconPixmap" => _properties.AttentionIconPixmap,
                "OverlayIconName" => _properties.OverlayIconName,
                "OverlayIconPixmap" => _properties.OverlayIconPixmap,
                "ToolTip" => _properties.ToolTip,
                "ItemIsMenu" => _properties.ItemIsMenu,
                "WindowId" => _properties.WindowId,
                "Menu" => _properties.Menu,
                _ => ""
            });
        }
    }

    public Task<StatusNotifierItemProperties> GetAllAsync()
    {
        lock (_gate)
        {
            return Task.FromResult(_properties);
        }
    }

    public Task SetAsync(string prop, object val)
    {
        return Task.CompletedTask;
    }
}

public static class LinuxTray
{
    private const string WatcherService = "org.kde.StatusNotifierWatcher";

    private const string NotificationsService = "org.freedesktop.Notifications";

    private static readonly int[] PixmapSizes = [16, 22, 24, 32, 48, 64, 128, 256];

    private static readonly object Gate = new();

    private static Connection? _connection;

    private static StatusNotifierItemObject? _item;

    private static DbusMenuObject? _menu;

    private static IDisposable? _watcherSubscription;

    private static string _busName = string.Empty;

    private static bool _started;

    private static bool _registered;

    private static string _notificationIcon = "voidstrap";

    public static StatusNotifierItemObject? Item => _item;

    public static DbusMenuObject? Menu => _menu;

    public static bool IsActive => _started;

    private static readonly TimeSpan TrayOperationTimeout = TimeSpan.FromSeconds(5.0);

    public static bool TryStart(string title)
    {
        if (!OperatingSystem.IsLinux())
        {
            return false;
        }

        lock (Gate)
        {
            if (_started)
            {
                return true;
            }
        }

        byte[]? icon = ReadIcon();
        _notificationIcon = ResolveNotificationIcon(icon);

        try
        {
            Task<bool> start = Task.Run(() => StartAsync(title, icon));
            if (!start.Wait(TrayOperationTimeout))
            {
                Voidstrap.App.Logger?.WriteLine("LinuxTray::TryStart", "The tray icon did not register in time, continuing without it");
                return false;
            }

            return start.Result;
        }
        catch (Exception ex)
        {
            Voidstrap.App.Logger?.WriteLine("LinuxTray::TryStart", "The tray icon could not be created: " + ex.GetBaseException().Message);
            return false;
        }
    }

    private static byte[]? ReadIcon()
    {
        try
        {
            return Voidstrap.Utility.LinuxDesktopEntry.ReadTrayIconPng();
        }
        catch (Exception ex)
        {
            Voidstrap.App.Logger?.WriteLine("LinuxTray::ReadIcon", "The tray icon image could not be read: " + ex.Message);
            return null;
        }
    }

    private static string ResolveNotificationIcon(byte[]? icon)
    {
        string installed = Voidstrap.Utility.LinuxDesktopEntry.InstalledIconName();
        if (installed.Length > 0)
            return installed;
        if (icon == null)
            return "voidstrap";

        try
        {
            string runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") ?? System.IO.Path.GetTempPath();
            string directory = System.IO.Path.Combine(runtime, "voidstrap");
            System.IO.Directory.CreateDirectory(directory);
            string path = System.IO.Path.Combine(directory, "icon.png");
            System.IO.File.WriteAllBytes(path, icon);
            return path;
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            return "voidstrap";
        }
    }

    public static void UpdateIcon(byte[]? icon)
    {
        StatusNotifierItemObject? item;
        lock (Gate)
        {
            item = _item;
        }

        if (!OperatingSystem.IsLinux() || icon == null)
            return;

        _notificationIcon = ResolveNotificationIcon(icon);
        if (item == null)
            return;

        _ = Task.Run(() =>
        {
            item.SetIcon(BuildPixmaps(icon));
            Voidstrap.App.Logger?.WriteLine("LinuxTray::UpdateIcon", "The tray icon now shows the selected Voidstrap icon");
        });
    }

    internal static (int, int, byte[])[] BuildPixmaps(byte[]? png)
    {
        if (png == null || png.Length == 0)
            return [];

        try
        {
            using SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32> source =
                SixLabors.ImageSharp.Image.Load<SixLabors.ImageSharp.PixelFormats.Rgba32>(png);
            List<(int, int, byte[])> pixmaps = [];
            foreach (int size in PixmapSizes)
            {
                using SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32> scaled = SixLabors.ImageSharp.Processing.ProcessingExtensions.Clone(source, context => SixLabors.ImageSharp.Processing.ResizeExtensions.Resize(context, new SixLabors.ImageSharp.Processing.ResizeOptions
                {
                    Size = new SixLabors.ImageSharp.Size(size, size),
                    Mode = SixLabors.ImageSharp.Processing.ResizeMode.Pad,
                    PadColor = SixLabors.ImageSharp.Color.Transparent,
                    Sampler = SixLabors.ImageSharp.Processing.KnownResamplers.Lanczos3
                }));
                byte[] data = new byte[size * size * 4];
                int offset = 0;
                scaled.ProcessPixelRows(accessor =>
                {
                    for (int y = 0; y < accessor.Height; y++)
                    {
                        foreach (SixLabors.ImageSharp.PixelFormats.Rgba32 pixel in accessor.GetRowSpan(y))
                        {
                            data[offset++] = pixel.A;
                            data[offset++] = pixel.R;
                            data[offset++] = pixel.G;
                            data[offset++] = pixel.B;
                        }
                    }
                });
                pixmaps.Add((size, size, data));
            }
            return [.. pixmaps];
        }
        catch (Exception ex)
        {
            Voidstrap.App.Logger?.WriteLine("LinuxTray::BuildPixmaps", "The tray icon image could not be prepared: " + ex.Message);
            return [];
        }
    }

    private static async Task<bool> StartAsync(string title, byte[]? icon)
    {
        Connection connection = new(Address.Session);
        await connection.ConnectAsync().ConfigureAwait(false);

        StatusNotifierItemObject item = new();
        item.SetTitle(title);
        item.SetIcon(BuildPixmaps(icon));

        DbusMenuObject menu = new();
        await connection.RegisterObjectAsync(item).ConfigureAwait(false);
        await connection.RegisterObjectAsync(menu).ConfigureAwait(false);

        string busName = "org.kde.StatusNotifierItem-" + Environment.ProcessId + "-1";
        await connection.RegisterServiceAsync(busName).ConfigureAwait(false);

        lock (Gate)
        {
            _connection = connection;
            _item = item;
            _menu = menu;
            _busName = busName;
            _started = true;
        }

        bool registered = await RegisterWithWatcherAsync(connection, busName).ConfigureAwait(false);
        lock (Gate)
        {
            _registered = registered;
        }
        try
        {
            IDisposable subscription = await connection.ResolveServiceOwnerAsync(WatcherService, OnWatcherOwnerChanged).ConfigureAwait(false);
            lock (Gate)
            {
                _watcherSubscription = subscription;
            }
        }
        catch (Exception ex)
        {
            Voidstrap.App.Logger?.WriteLine("LinuxTray::Start", "Tray host changes cannot be followed: " + ex.Message);
        }

        Voidstrap.App.Logger?.WriteLine("LinuxTray::Start", registered
            ? "Registered the tray icon as " + busName
            : "No tray host is running yet, the tray icon appears as soon as one starts");
        return true;
    }

    private static async Task<bool> RegisterWithWatcherAsync(Connection connection, string busName)
    {
        try
        {
            IStatusNotifierWatcher watcher = connection.CreateProxy<IStatusNotifierWatcher>(
                WatcherService,
                new ObjectPath("/StatusNotifierWatcher"));
            await watcher.RegisterStatusNotifierItemAsync(busName).ConfigureAwait(false);
            return true;
        }
        catch (DBusException ex)
        {
            Voidstrap.App.Logger?.WriteLine("LinuxTray::Register", "The tray host did not accept the icon yet: " + ex.ErrorName);
            return false;
        }
    }

    private static void OnWatcherOwnerChanged(ServiceOwnerChangedEventArgs change)
    {
        Connection? connection;
        string busName;
        lock (Gate)
        {
            if (string.IsNullOrEmpty(change.NewOwner))
            {
                if (_registered)
                    Voidstrap.App.Logger?.WriteLine("LinuxTray::HostChanged", "The tray host stopped, the icon returns when it starts again");
                _registered = false;
                return;
            }
            if (_registered)
                return;
            connection = _connection;
            busName = _busName;
        }

        if (connection == null || busName.Length == 0)
            return;

        _ = Task.Run(async () =>
        {
            if (!await RegisterWithWatcherAsync(connection, busName).ConfigureAwait(false))
                return;
            lock (Gate)
            {
                _registered = true;
            }
            Voidstrap.App.Logger?.WriteLine("LinuxTray::HostChanged", "The tray host started, the tray icon is registered with it");
        });
    }

    public static void Notify(string title, string body)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        try
        {
            if (!Task.Run(() => NotifyAsync(title, body)).Wait(TrayOperationTimeout))
            {
                Voidstrap.App.Logger?.WriteLine("LinuxTray::Notify", "The notification timed out");
            }
        }
        catch (Exception ex)
        {
            Voidstrap.App.Logger?.WriteLine("LinuxTray::Notify", "The notification could not be sent: " + ex.GetBaseException().Message);
        }
    }

    private static async Task NotifyAsync(string title, string body)
    {
        Connection? connection;
        lock (Gate)
        {
            connection = _connection;
        }

        bool temporary = connection == null;
        if (connection == null)
        {
            connection = new Connection(Address.Session);
            await connection.ConnectAsync().ConfigureAwait(false);
        }

        try
        {
            IFreedesktopNotifications notifications = connection.CreateProxy<IFreedesktopNotifications>(
                NotificationsService,
                new ObjectPath("/org/freedesktop/Notifications"));

            Dictionary<string, object> hints = new()
            {
                ["desktop-entry"] = Voidstrap.Utility.LinuxDesktopEntry.DesktopEntryId
            };

            await notifications.NotifyAsync(
                "Voidstrap",
                0u,
                _notificationIcon,
                title,
                body,
                [],
                hints,
                5000).ConfigureAwait(false);
        }
        finally
        {
            if (temporary)
                connection.Dispose();
        }
    }

    public static void Stop()
    {
        Connection? connection;
        IDisposable? subscription;
        lock (Gate)
        {
            connection = _connection;
            subscription = _watcherSubscription;
            _connection = null;
            _watcherSubscription = null;
            _item = null;
            _menu = null;
            _busName = string.Empty;
            _started = false;
            _registered = false;
        }

        try
        {
            subscription?.Dispose();
            connection?.Dispose();
        }
        catch (Exception ex)
        {
            Voidstrap.App.Logger?.WriteLine("LinuxTray::Stop", "The tray connection could not be closed: " + ex.Message);
        }
    }
}
