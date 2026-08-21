using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace MouseWithoutBorders.EnhancedDragDrop;

public sealed record ExplorerTarget(nint Hwnd, System.Drawing.Rectangle Bounds, string FolderPath, int ZOrder, bool IsVisible);

/// <summary>Enumerates visible, non-minimized filesystem Explorer windows.</summary>
[SupportedOSPlatform("windows")]
public sealed class ExplorerTargetEnumerator
{
    public IReadOnlyList<ExplorerTarget> Enumerate()
    {
        var targets = new List<ExplorerTarget>();
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;
        dynamic windows = shell.Windows();
        try
        {
            foreach (var window in windows)
            {
                try
                {
                    nint hwnd = (nint)(long)window.HWND;
                    if (hwnd == 0 || !IsWindowVisible(hwnd) || IsIconic(hwnd)) continue;
                    string? path = TryGetFilesystemPath(window);
                    if (path is null || !TryGetActualWindowRect(hwnd, out var rect)) continue;
                    targets.Add(new ExplorerTarget(hwnd, rect.ToRectangle(), path, GetWindowZOrder(hwnd), true));
                }
                finally
                {
                    ReleaseComObject(window);
                }
            }
        }
        finally
        {
            ReleaseComObject(windows);
            ReleaseComObject(shell);
        }
        return targets.OrderBy(target => target.ZOrder).ToArray();
    }

    private static void ReleaseComObject(object value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            Marshal.FinalReleaseComObject(value);
        }
    }

    private static string? TryGetFilesystemPath(dynamic window)
    {
        try
        {
            string url = (string)window.LocationURL;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !uri.IsFile) return null;
            var path = uri.LocalPath;
            return Directory.Exists(path) ? path : null;
        }
        catch { return null; }
    }

    private static int GetWindowZOrder(nint hwnd)
    {
        var order = 0;
        for (var current = GetTopWindow(0); current != 0; current = GetWindow(current, 2))
        {
            if (current == hwnd) return order;
            order++;
        }
        return int.MaxValue;
    }

    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hWnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint hWnd);
    [DllImport("user32.dll")] private static extern nint GetTopWindow(nint hWnd);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint hWnd, uint uCmd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hWnd, out NativeRect rect);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint hWnd, uint attribute, out NativeRect rect, int size);
    private const uint DWMWA_EXTENDED_FRAME_BOUNDS = 9;

    private static bool TryGetActualWindowRect(nint hwnd, out NativeRect rect)
    {
        return DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out rect, Marshal.SizeOf<NativeRect>()) == 0
            || GetWindowRect(hwnd, out rect);
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; public System.Drawing.Rectangle ToRectangle() => System.Drawing.Rectangle.FromLTRB(Left, Top, Right, Bottom); }
}
