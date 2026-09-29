using System;
using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Voidstrap.UI;

internal static class LinuxWebImageSource
{
	private const string LogIdent = "LinuxWebImageSource";

	private static bool _installed;

	public static void Install()
	{
		if (_installed || !OperatingSystem.IsLinux())
			return;

		_installed = true;
		try
		{
			object? context = typeof(XamlReader).GetProperty("BamlSharedSchemaContext", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
			MethodInfo? lookup = context?.GetType().GetMethod("GetKnownXamlType", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(Type) }, null);
			object? knownType = lookup?.Invoke(context, new object[] { typeof(ImageSource) });
			object? converter = knownType?.GetType().GetProperty("TypeConverter", BindingFlags.Public | BindingFlags.Instance)?.GetValue(knownType);
			FieldInfo? instance = converter?.GetType().GetField("_instance", BindingFlags.NonPublic | BindingFlags.Instance);
			FieldInfo? instanceSet = converter?.GetType().GetField("_instanceIsSet", BindingFlags.NonPublic | BindingFlags.Instance);
			if (instance == null || instanceSet == null)
			{
				App.Logger.WriteLine(LogIdent, "The image source converter could not be located, web images keep loading on the interface thread");
				return;
			}

			instance.SetValue(converter, new Converter());
			instanceSet.SetValue(converter, true);
			App.Logger.WriteLine(LogIdent, "Web images in bindings now load in the background");
		}
		catch (Exception ex)
		{
			App.Logger.WriteException(LogIdent, ex);
		}
	}

	private static bool TryGetWebUri(object? value, out Uri uri)
	{
		uri = null!;
		Uri? candidate = value switch
		{
			Uri direct => direct,
			string text when text.Length > 0 && Uri.TryCreate(text.Trim(), UriKind.Absolute, out Uri? parsed) => parsed,
			_ => null
		};
		if (candidate == null || !candidate.IsAbsoluteUri)
			return false;
		if (!string.Equals(candidate.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
			&& !string.Equals(candidate.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
			return false;
		uri = candidate;
		return true;
	}

	private static ImageSource CreateDeferred(Uri uri)
	{
		DrawingGroup content = new DrawingGroup();
		DrawingImage image = new DrawingImage(content);
		Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
		Task<BitmapSource?> load = Voidstrap.Utility.DynamicRenderSystem.LoadWebImageAsync(uri.AbsoluteUri);
		if (load.IsCompletedSuccessfully)
		{
			Present(content, load.Result);
			return image;
		}

		load.ContinueWith(task =>
		{
			BitmapSource? bitmap = task.IsCompletedSuccessfully ? task.Result : null;
			if (bitmap == null || dispatcher.HasShutdownStarted)
				return;
			dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => Present(content, bitmap)));
		}, TaskScheduler.Default);
		return image;
	}

	private static void Present(DrawingGroup content, BitmapSource? bitmap)
	{
		if (bitmap == null || content.IsFrozen || bitmap.Width <= 0 || bitmap.Height <= 0)
			return;
		content.Children.Clear();
		content.Children.Add(new ImageDrawing(bitmap, new Rect(0, 0, bitmap.Width, bitmap.Height)));
	}

	private sealed class Converter : ImageSourceConverter
	{
		public override object? ConvertFrom(ITypeDescriptorContext? context, CultureInfo? culture, object value)
		{
			if (TryGetWebUri(value, out Uri uri))
				return CreateDeferred(uri);
			return base.ConvertFrom(context, culture, value);
		}
	}
}
