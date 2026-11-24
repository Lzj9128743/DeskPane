using System.Runtime.InteropServices;

namespace DeskBox.Services;

/// <summary>
/// Controls visibility of the native Windows desktop icons (SysListView32 under
/// SHELLDLL_DefView) using standard Shell WM_COMMAND messaging — no Explorer
/// injection, safe across Windows 10 and Windows 11.
/// </summary>
public static partial class DesktopIconVisibilityService
{
    private const uint WM_COMMAND = 0x0111;
    private const nint ToggleDesktopIconsCommandId = 0x7402;

    [LibraryImport("user32.dll", EntryPoint = "FindWindowW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [LibraryImport("user32.dll", EntryPoint = "FindWindowExW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr FindWindowEx(IntPtr hWndParent, IntPtr hWndChildAfter, string? lpszClass, string? lpszWindow);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindowVisible(IntPtr hWnd);

    [LibraryImport("user32.dll", EntryPoint = "SendMessageW")]
    private static partial nint SendMessage(IntPtr hWnd, uint msg, nint wParam, nint lParam);

    private static (IntPtr DefView, IntPtr ListView) FindDesktopIconWindows()
    {
        IntPtr progman = FindWindow("Progman", null);
        IntPtr defView = progman != IntPtr.Zero
            ? FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null)
            : IntPtr.Zero;

        if (defView == IntPtr.Zero)
        {
            IntPtr workerW = IntPtr.Zero;
            while ((workerW = FindWindowEx(IntPtr.Zero, workerW, "WorkerW", null)) != IntPtr.Zero)
            {
                defView = FindWindowEx(workerW, IntPtr.Zero, "SHELLDLL_DefView", null);
                if (defView != IntPtr.Zero)
                {
                    break;
                }
            }
        }

        IntPtr listView = defView != IntPtr.Zero
            ? FindWindowEx(defView, IntPtr.Zero, "SysListView32", null)
            : IntPtr.Zero;
        return (defView, listView);
    }

    public static bool? AreDesktopIconsVisible()
    {
        var (_, listView) = FindDesktopIconWindows();
        return listView == IntPtr.Zero ? null : IsWindowVisible(listView);
    }

    public static bool SetDesktopIconsVisible(bool visible)
    {
        var (defView, listView) = FindDesktopIconWindows();
        if (defView == IntPtr.Zero || listView == IntPtr.Zero)
        {
            return false;
        }

        bool currentlyVisible = IsWindowVisible(listView);
        if (currentlyVisible == visible)
        {
            return true;
        }

        SendMessage(defView, WM_COMMAND, ToggleDesktopIconsCommandId, 0);
        return IsWindowVisible(listView) == visible;
    }

    public static bool ToggleDesktopIcons()
    {
        var (defView, listView) = FindDesktopIconWindows();
        if (defView == IntPtr.Zero || listView == IntPtr.Zero)
        {
            return false;
        }

        SendMessage(defView, WM_COMMAND, ToggleDesktopIconsCommandId, 0);
        return true;
    }
}
