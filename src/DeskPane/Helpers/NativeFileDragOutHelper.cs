// Copyright (c) DeskPane. All rights reserved.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace DeskPane.Helpers;

/// <summary>
/// Starts a native Win32 OLE file drag (<c>ole32!DoDragDrop</c>) from an STA UI thread.
/// Used as a fallback when WinUI 3's XAML <c>UIElement.CanDrag</c> / <c>StartDragAsync</c>
/// is unavailable (such as elevated / UAC-disabled Administrator desktop sessions).
/// </summary>
internal static unsafe partial class NativeFileDragOutHelper
{
    private const uint DropEffectNone = 0;
    private const uint DropEffectCopy = 1;
    private const uint DropEffectMove = 2;
    private const uint DropEffectLink = 4;

    private const uint MkLButton = 0x0001;
    private const int SOk = 0;
    private const int ENoInterface = unchecked((int)0x80004002);
    private const int DragDropSDrop = 0x00040100;
    private const int DragDropSCancel = 0x00040101;
    private const int DragDropSUseDefaultCursors = 0x00040102;

    private static readonly Guid IidIUnknown = new("00000000-0000-0000-C000-000000000046");
    private static readonly Guid IidIDropSource = new("00000121-0000-0000-C000-000000000046");

    [LibraryImport("ole32.dll")]
    private static partial int DoDragDrop(
        nint pDataObj,
        nint pDropSource,
        uint dwOKEffects,
        out uint pdwEffect);

    [StructLayout(LayoutKind.Sequential)]
    private struct DropSourceVtbl
    {
        public delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int> QueryInterface;
        public delegate* unmanaged[Stdcall]<nint, uint> AddRef;
        public delegate* unmanaged[Stdcall]<nint, uint> Release;
        public delegate* unmanaged[Stdcall]<nint, int, uint, int> QueryContinueDrag;
        public delegate* unmanaged[Stdcall]<nint, uint, int> GiveFeedback;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeDropSourceInstance
    {
        public DropSourceVtbl* Vtbl;
    }

    private static readonly DropSourceVtbl* s_vtbl;
    private static readonly NativeDropSourceInstance* s_singletonDropSource;

    static NativeFileDragOutHelper()
    {
        s_vtbl = (DropSourceVtbl*)RuntimeHelpers.AllocateTypeAssociatedMemory(
            typeof(NativeFileDragOutHelper),
            sizeof(DropSourceVtbl));
        s_vtbl->QueryInterface = &QueryInterface;
        s_vtbl->AddRef = &AddRef;
        s_vtbl->Release = &Release;
        s_vtbl->QueryContinueDrag = &QueryContinueDrag;
        s_vtbl->GiveFeedback = &GiveFeedback;

        s_singletonDropSource = (NativeDropSourceInstance*)RuntimeHelpers.AllocateTypeAssociatedMemory(
            typeof(NativeFileDragOutHelper),
            sizeof(NativeDropSourceInstance));
        s_singletonDropSource->Vtbl = s_vtbl;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int QueryInterface(nint self, Guid* riid, nint* ppvObject)
    {
        if (ppvObject is null)
        {
            return ENoInterface;
        }

        if (riid is not null && (*riid == IidIUnknown || *riid == IidIDropSource))
        {
            *ppvObject = self;
            return SOk;
        }

        *ppvObject = 0;
        return ENoInterface;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint AddRef(nint self) => 1;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint Release(nint self) => 1;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int QueryContinueDrag(nint self, int fEscapePressed, uint grfKeyState)
    {
        if (fEscapePressed != 0)
        {
            return DragDropSCancel;
        }

        if ((grfKeyState & MkLButton) == 0)
        {
            return DragDropSDrop;
        }

        return SOk;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int GiveFeedback(nint self, uint dwEffect) =>
        DragDropSUseDefaultCursors;

    /// <summary>
    /// Runs a synchronous OLE drag-and-drop operation for the given file/folder paths.
    /// Returns true if the drop completed with a non-None effect.
    /// </summary>
    public static bool DoDragDropFiles(IReadOnlyList<string> paths, out uint performedEffect)
    {
        performedEffect = DropEffectNone;
        if (paths is null || paths.Count == 0)
        {
            return false;
        }

        if (!ShellDataObjectBuilder.TryCreateHdropDataObject(paths, out nint dataObject) ||
            dataObject == 0)
        {
            return false;
        }

        try
        {
            int hr = DoDragDrop(
                dataObject,
                (nint)s_singletonDropSource,
                DropEffectMove | DropEffectCopy | DropEffectLink,
                out performedEffect);
            return hr == DragDropSDrop && performedEffect != DropEffectNone;
        }
        catch (Exception ex)
        {
            App.Log($"[NativeDragOut] DoDragDrop failed: {ex.Message}");
            return false;
        }
        finally
        {
            ShellDropDelegator.ReleaseObject(dataObject);
        }
    }
}
