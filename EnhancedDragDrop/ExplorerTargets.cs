using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace MouseWithoutBorders.EnhancedDragDrop;

public sealed record ExplorerTarget(nint Hwnd, System.Drawing.Rectangle Bounds, string FolderPath, int ZOrder, bool IsVisible);

/// <summary>Enumerates filesystem Explorer windows and the desktop without exposing unsupported Shell namespaces.</summary>
[SupportedOSPlatform("windows")]
public sealed class ExplorerTargetEnumerator
{
    public IReadOnlyList<ExplorerTarget> Enumerate()
    {
        var targets = new List<ExplorerTarget>();
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;
        foreach (var window in shell.Windows())
        {
            nint hwnd = (nint)(long)window.HWND;
            if (hwnd == 0 || !IsWindowVisible(hwnd) || IsIconic(hwnd)) continue;
            string? path = TryGetFilesystemPath(window);
            if (path is null || !GetWindowRect(hwnd, out var rect)) continue;
            targets.Add(new ExplorerTarget(hwnd, rect.ToRectangle(), path, GetWindowZOrder(hwnd), true));
        }
        var desktop = GetShellWindow();
        if (desktop != 0 && GetWindowRect(desktop, out var desktopRect))
            targets.Add(new ExplorerTarget(desktop, desktopRect.ToRectangle(), Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), int.MaxValue, true));
        return targets.OrderBy(target => target.ZOrder).ToArray();
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
    [DllImport("user32.dll")] private static extern nint GetShellWindow();
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hWnd, out NativeRect rect);

    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; public System.Drawing.Rectangle ToRectangle() => System.Drawing.Rectangle.FromLTRB(Left, Top, Right, Bottom); }
}
