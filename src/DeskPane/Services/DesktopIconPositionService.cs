// Copyright (c) DeskPane. All rights reserved.

using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using DeskPane.Models;

namespace DeskPane.Services;

/// <summary>
/// Hides individual desktop icons that belong to active virtual desktop groups
/// by moving only those items off-screen via standard SysListView32 messages
/// (LVM_SETITEMPOSITION32), and restores them to their original coordinates
/// when removed from a group or when DeskPane exits.
///
/// Zero Explorer injection, zero file moves, zero file-attribute mutations,
/// and zero background polling timers.
/// </summary>
public static partial class DesktopIconPositionService
{
    private const uint ProcessVmOperation = 0x0008;
    private const uint ProcessVmRead = 0x0010;
    private const uint ProcessVmWrite = 0x0020;
    private const uint MemCommit = 0x1000;
    private const uint MemReserve = 0x2000;
    private const uint MemRelease = 0x8000;
    private const uint PageReadWrite = 0x04;

    private const int GwlStyle = -16;
    private const int LvsAutoArrange = 0x0100;

    private const uint LvmFirst = 0x1000;
    private const uint LvmGetItemCount = LvmFirst + 4;
    private const uint LvmGetItemPosition = LvmFirst + 16;
    private const uint LvmSetItemPosition32 = LvmFirst + 49;
    private const uint LvmGetItemTextW = LvmFirst + 115;

    private const int SmXVirtualScreen = 76;
    private const int SmYVirtualScreen = 77;
    private const int SmCxVirtualScreen = 78;
    private const int SmCyVirtualScreen = 79;

    private const uint SendMessageAbortIfHung = 0x0002;
    private const uint SendMessageTimeoutMs = 1000;
    private const int OffScreenMargin = 10000;
    private const int MaxIconTextChars = 260;

    private static readonly object s_sync = new();
    private static readonly JsonSerializerOptions s_jsonOptions = new() { WriteIndented = true };
    private static Dictionary<string, SavedIconPosition>? s_savedPositions;

    private sealed class SavedIconPosition
    {
        public int X { get; set; }
        public int Y { get; set; }
        public string DisplayName { get; set; } = string.Empty;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LvItem64
    {
        public uint Mask;
        public int Item;
        public int SubItem;
        public uint State;
        public uint StateMask;
        public IntPtr TextPtr;
        public int TextMax;
        public int Image;
        public IntPtr LParam;
        public int Indent;
        public int GroupId;
        public uint Columns;
        public IntPtr ColumnsPtr;
        public IntPtr ColumnFormatPtr;
        public int GroupIndex;
    }

    [LibraryImport("user32.dll", EntryPoint = "FindWindowW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [LibraryImport("user32.dll", EntryPoint = "FindWindowExW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr FindWindowEx(IntPtr hWndParent, IntPtr hWndChildAfter, string? lpszClass, string? lpszWindow);

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static partial int GetWindowLong(IntPtr hWnd, int nIndex);

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetrics(int nIndex);

    [LibraryImport("user32.dll", EntryPoint = "SendMessageTimeoutW")]
    private static partial IntPtr SendMessageTimeout(
        IntPtr hWnd,
        uint msg,
        IntPtr wParam,
        IntPtr lParam,
        uint fuFlags,
        uint uTimeout,
        out IntPtr lpdwResult);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwProcessId);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr hObject);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr VirtualAllocEx(IntPtr hProcess, IntPtr lpAddress, nuint dwSize, uint flAllocationType, uint flProtect);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool VirtualFreeEx(IntPtr hProcess, IntPtr lpAddress, nuint dwSize, uint dwFreeType);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, nuint nSize, out nuint lpNumberOfBytesRead);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WriteProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, nuint nSize, out nuint lpNumberOfBytesWritten);

    private static string LedgerFilePath =>
        Path.Combine(DeskPaneDataPathService.Current.RootPath, "hidden-desktop-icon-positions.json");

    /// <summary>
    /// Synchronizes desktop icon visibility for all active virtual desktop groups:
    /// items inside active virtual groups are moved off-screen (outside all monitors),
    /// while items no longer in any virtual group are restored to their saved positions.
    /// </summary>
    public static void SyncForWidgets(IReadOnlyList<WidgetConfig> widgets)
    {
        var desiredHiddenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < widgets.Count; i++)
        {
            WidgetConfig widget = widgets[i];
            if (!widget.IsVirtualDesktopGroup || widget.IsDisabled)
            {
                continue;
            }

            for (int j = 0; j < widget.Items.Count; j++)
            {
                string path = widget.Items[j].Path;
                if (!string.IsNullOrWhiteSpace(path) && VirtualDesktopGroupPolicy.IsDesktopItemPath(path))
                {
                    desiredHiddenPaths.Add(NormalizePath(path));
                }
            }
        }

        SyncHiddenDesktopIcons(desiredHiddenPaths);
    }

    /// <summary>
    /// Moves every path in <paramref name="desiredHiddenPaths"/> off-screen and
    /// restores any previously hidden path that is no longer in the set.
    /// </summary>
    public static void SyncHiddenDesktopIcons(IReadOnlySet<string> desiredHiddenPaths)
    {
        lock (s_sync)
        {
            Dictionary<string, SavedIconPosition> ledger = LoadLedgerLocked();
            if (desiredHiddenPaths.Count == 0 && ledger.Count == 0)
            {
                return;
            }

            IntPtr listView = FindDesktopListView();
            if (listView == IntPtr.Zero)
            {
                return;
            }

            int style = GetWindowLong(listView, GwlStyle);
            if ((style & LvsAutoArrange) != 0)
            {
                App.LogVerbose("[DesktopIconPosition] Skipped sync because Windows desktop Auto-Arrange is enabled.");
                return;
            }

            var (vLeft, vTop, vRight, vBottom) = GetVirtualScreenBounds();
            int offScreenX = vRight + OffScreenMargin;
            int offScreenY = vBottom + OffScreenMargin;

            WithExplorerBuffer(listView, (hProcess, remoteMem, itemCount) =>
            {
                if (itemCount <= 0)
                {
                    return;
                }

                // Map icon index -> (displayName, currentPosition)
                var desktopIcons = new List<(int Index, string Name, NativePoint Pos)>(itemCount);
                for (int idx = 0; idx < itemCount; idx++)
                {
                    string name = ReadListViewItemText(listView, hProcess, remoteMem, idx);
                    if (string.IsNullOrEmpty(name))
                    {
                        continue;
                    }

                    if (TryReadListViewItemPosition(listView, hProcess, remoteMem, idx, out NativePoint pos))
                    {
                        desktopIcons.Add((idx, name, pos));
                    }
                }

                bool ledgerChanged = false;

                // 1. Restore items that are no longer in any active virtual group.
                var toRestore = ledger.Keys
                    .Where(p => !desiredHiddenPaths.Contains(p))
                    .ToList();
                foreach (string restorePath in toRestore)
                {
                    SavedIconPosition saved = ledger[restorePath];
                    int matchIndex = FindMatchingIconIndex(desktopIcons, restorePath, saved.DisplayName);
                    if (matchIndex >= 0)
                    {
                        SetListViewItemPosition(listView, hProcess, remoteMem, matchIndex, saved.X, saved.Y);
                    }

                    ledger.Remove(restorePath);
                    ledgerChanged = true;
                }

                // 2. Move desired virtual-group items off-screen (saving original coords first).
                foreach (string hidePath in desiredHiddenPaths)
                {
                    string? hintName = ledger.TryGetValue(hidePath, out SavedIconPosition? existing)
                        ? existing.DisplayName
                        : null;
                    int matchIndex = FindMatchingIconIndex(desktopIcons, hidePath, hintName);
                    if (matchIndex < 0)
                    {
                        continue;
                    }

                    var icon = desktopIcons.First(i => i.Index == matchIndex);
                    bool isCurrentlyOffScreen = IsPointOffScreen(icon.Pos, vLeft, vTop, vRight, vBottom);

                    if (!ledger.ContainsKey(hidePath))
                    {
                        // Only record a real on-screen position so we never overwrite with off-screen coords.
                        int saveX = isCurrentlyOffScreen ? Math.Max(vLeft + 24, 24) : icon.Pos.X;
                        int saveY = isCurrentlyOffScreen ? Math.Max(vTop + 24, 24) : icon.Pos.Y;
                        ledger[hidePath] = new SavedIconPosition
                        {
                            X = saveX,
                            Y = saveY,
                            DisplayName = icon.Name
                        };
                        ledgerChanged = true;
                    }

                    if (!isCurrentlyOffScreen)
                    {
                        SetListViewItemPosition(listView, hProcess, remoteMem, matchIndex, offScreenX, offScreenY);
                    }
                }

                if (ledgerChanged)
                {
                    SaveLedgerLocked(ledger);
                }
            });
        }
    }

    /// <summary>
    /// Restores all off-screen desktop icons back to their saved original coordinates
    /// (called on application shutdown).
    /// </summary>
    public static void RestoreAllHiddenDesktopIcons()
    {
        lock (s_sync)
        {
            Dictionary<string, SavedIconPosition> ledger = LoadLedgerLocked();
            if (ledger.Count == 0)
            {
                return;
            }

            IntPtr listView = FindDesktopListView();
            if (listView == IntPtr.Zero)
            {
                return;
            }

            WithExplorerBuffer(listView, (hProcess, remoteMem, itemCount) =>
            {
                if (itemCount <= 0)
                {
                    return;
                }

                var desktopIcons = new List<(int Index, string Name, NativePoint Pos)>(itemCount);
                for (int idx = 0; idx < itemCount; idx++)
                {
                    string name = ReadListViewItemText(listView, hProcess, remoteMem, idx);
                    if (!string.IsNullOrEmpty(name))
                    {
                        desktopIcons.Add((idx, name, default));
                    }
                }

                foreach (var (path, saved) in ledger)
                {
                    int matchIndex = FindMatchingIconIndex(desktopIcons, path, saved.DisplayName);
                    if (matchIndex >= 0)
                    {
                        SetListViewItemPosition(listView, hProcess, remoteMem, matchIndex, saved.X, saved.Y);
                    }
                }
            });

            ledger.Clear();
            SaveLedgerLocked(ledger);
        }
    }

    private static int FindMatchingIconIndex(
        List<(int Index, string Name, NativePoint Pos)> desktopIcons,
        string fullPath,
        string? savedDisplayName)
    {
        if (!string.IsNullOrWhiteSpace(savedDisplayName))
        {
            for (int i = 0; i < desktopIcons.Count; i++)
            {
                if (string.Equals(desktopIcons[i].Name, savedDisplayName, StringComparison.OrdinalIgnoreCase))
                {
                    return desktopIcons[i].Index;
                }
            }
        }

        string fileName = Path.GetFileName(fullPath);
        string nameWithoutExt = Path.GetFileNameWithoutExtension(fullPath);

        for (int i = 0; i < desktopIcons.Count; i++)
        {
            string iconName = desktopIcons[i].Name;
            if (string.Equals(iconName, fileName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(iconName, nameWithoutExt, StringComparison.OrdinalIgnoreCase))
            {
                return desktopIcons[i].Index;
            }
        }

        return -1;
    }

    private static (int Left, int Top, int Right, int Bottom) GetVirtualScreenBounds()
    {
        int left = GetSystemMetrics(SmXVirtualScreen);
        int top = GetSystemMetrics(SmYVirtualScreen);
        int width = Math.Max(GetSystemMetrics(SmCxVirtualScreen), 1920);
        int height = Math.Max(GetSystemMetrics(SmCyVirtualScreen), 1080);
        return (left, top, left + width, top + height);
    }

    private static bool IsPointOffScreen(NativePoint pt, int left, int top, int right, int bottom) =>
        pt.X < left - 1000 || pt.Y < top - 1000 || pt.X > right + 1000 || pt.Y > bottom + 1000;

    private static IntPtr FindDesktopListView()
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

        return defView != IntPtr.Zero
            ? FindWindowEx(defView, IntPtr.Zero, "SysListView32", null)
            : IntPtr.Zero;
    }

    private static void WithExplorerBuffer(IntPtr listView, Action<IntPtr, IntPtr, int> action)
    {
        _ = GetWindowThreadProcessId(listView, out uint processId);
        if (processId == 0)
        {
            return;
        }

        if (SendMessageTimeout(listView, LvmGetItemCount, IntPtr.Zero, IntPtr.Zero,
                SendMessageAbortIfHung, SendMessageTimeoutMs, out IntPtr countResult) == IntPtr.Zero)
        {
            return;
        }

        int itemCount = countResult.ToInt32();
        if (itemCount <= 0)
        {
            return;
        }

        IntPtr hProcess = OpenProcess(ProcessVmOperation | ProcessVmRead | ProcessVmWrite, false, processId);
        if (hProcess == IntPtr.Zero)
        {
            return;
        }

        const nuint bufferSize = 4096;
        IntPtr remoteMem = IntPtr.Zero;
        try
        {
            remoteMem = VirtualAllocEx(hProcess, IntPtr.Zero, bufferSize, MemCommit | MemReserve, PageReadWrite);
            if (remoteMem == IntPtr.Zero)
            {
                return;
            }

            action(hProcess, remoteMem, itemCount);
        }
        catch (Exception ex)
        {
            App.LogVerbose($"[DesktopIconPosition] Explorer buffer operation failed: {ex.Message}");
        }
        finally
        {
            if (remoteMem != IntPtr.Zero)
            {
                _ = VirtualFreeEx(hProcess, remoteMem, 0, MemRelease);
            }

            _ = CloseHandle(hProcess);
        }
    }

    private static string ReadListViewItemText(IntPtr listView, IntPtr hProcess, IntPtr remoteMem, int index)
    {
        int structSize = Marshal.SizeOf<LvItem64>();
        IntPtr remoteTextPtr = IntPtr.Add(remoteMem, 256);

        var lvi = new LvItem64
        {
            SubItem = 0,
            TextPtr = remoteTextPtr,
            TextMax = MaxIconTextChars
        };

        byte[] structBytes = StructToBytes(lvi);
        if (!WriteProcessMemory(hProcess, remoteMem, structBytes, (nuint)structSize, out _))
        {
            return string.Empty;
        }

        if (SendMessageTimeout(listView, LvmGetItemTextW, new IntPtr(index), remoteMem,
                SendMessageAbortIfHung, SendMessageTimeoutMs, out IntPtr charsCopied) == IntPtr.Zero)
        {
            return string.Empty;
        }

        int charCount = Math.Clamp(charsCopied.ToInt32(), 0, MaxIconTextChars);
        if (charCount == 0)
        {
            return string.Empty;
        }

        byte[] textBytes = new byte[charCount * 2];
        if (!ReadProcessMemory(hProcess, remoteTextPtr, textBytes, (nuint)textBytes.Length, out _))
        {
            return string.Empty;
        }

        return Encoding.Unicode.GetString(textBytes).TrimEnd('\0');
    }

    private static bool TryReadListViewItemPosition(
        IntPtr listView,
        IntPtr hProcess,
        IntPtr remoteMem,
        int index,
        out NativePoint point)
    {
        point = default;
        if (SendMessageTimeout(listView, LvmGetItemPosition, new IntPtr(index), remoteMem,
                SendMessageAbortIfHung, SendMessageTimeoutMs, out IntPtr result) == IntPtr.Zero ||
            result == IntPtr.Zero)
        {
            return false;
        }

        byte[] bytes = new byte[Marshal.SizeOf<NativePoint>()];
        if (!ReadProcessMemory(hProcess, remoteMem, bytes, (nuint)bytes.Length, out _))
        {
            return false;
        }

        point = BytesToStruct<NativePoint>(bytes);
        return true;
    }

    private static void SetListViewItemPosition(
        IntPtr listView,
        IntPtr hProcess,
        IntPtr remoteMem,
        int index,
        int x,
        int y)
    {
        var pt = new NativePoint { X = x, Y = y };
        byte[] bytes = StructToBytes(pt);
        if (!WriteProcessMemory(hProcess, remoteMem, bytes, (nuint)bytes.Length, out _))
        {
            return;
        }

        _ = SendMessageTimeout(listView, LvmSetItemPosition32, new IntPtr(index), remoteMem,
            SendMessageAbortIfHung, SendMessageTimeoutMs, out _);
    }

    private static byte[] StructToBytes<T>(T value) where T : struct
    {
        int size = Marshal.SizeOf<T>();
        byte[] bytes = new byte[size];
        IntPtr ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(value, ptr, false);
            Marshal.Copy(ptr, bytes, 0, size);
            return bytes;
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    private static T BytesToStruct<T>(byte[] bytes) where T : struct
    {
        int size = Marshal.SizeOf<T>();
        IntPtr ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.Copy(bytes, 0, ptr, size);
            return Marshal.PtrToStructure<T>(ptr);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    private static string NormalizePath(string path)
    {
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch
        {
            return path.Trim();
        }
    }

    private static Dictionary<string, SavedIconPosition> LoadLedgerLocked()
    {
        if (s_savedPositions is not null)
        {
            return s_savedPositions;
        }

        try
        {
            if (File.Exists(LedgerFilePath))
            {
                string json = File.ReadAllText(LedgerFilePath, Encoding.UTF8);
                var parsed = JsonSerializer.Deserialize<Dictionary<string, SavedIconPosition>>(json);
                if (parsed is not null)
                {
                    s_savedPositions = new Dictionary<string, SavedIconPosition>(parsed, StringComparer.OrdinalIgnoreCase);
                    return s_savedPositions;
                }
            }
        }
        catch (Exception ex)
        {
            App.LogVerbose($"[DesktopIconPosition] Failed to load ledger: {ex.Message}");
        }

        s_savedPositions = new Dictionary<string, SavedIconPosition>(StringComparer.OrdinalIgnoreCase);
        return s_savedPositions;
    }

    private static void SaveLedgerLocked(Dictionary<string, SavedIconPosition> ledger)
    {
        try
        {
            string? dir = Path.GetDirectoryName(LedgerFilePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            if (ledger.Count == 0)
            {
                if (File.Exists(LedgerFilePath))
                {
                    File.Delete(LedgerFilePath);
                }

                return;
            }

            string json = JsonSerializer.Serialize(ledger, s_jsonOptions);
            File.WriteAllText(LedgerFilePath, json, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            App.LogVerbose($"[DesktopIconPosition] Failed to save ledger: {ex.Message}");
        }
    }
}
