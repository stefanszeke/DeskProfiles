using System.Runtime.InteropServices;
using System.IO;
namespace DeskProfiles;

// Shell interfaces and method ordering follow the Windows SDK.
// Inspired by Raymond Chen's sample for IFolderView desktop positioning.
internal static class DesktopIcons
{
    [StructLayout(LayoutKind.Sequential)] public struct Point { public int X; public int Y; }

    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IServiceProvider { [PreserveSig] int QueryService(ref Guid service, ref Guid iid, out IntPtr obj); }

    [ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellBrowser
    {
        void GetWindow(out IntPtr hwnd); void ContextSensitiveHelp(bool enter);
        void InsertMenusSB(); void SetMenuSB(); void RemoveMenusSB(); void SetStatusTextSB();
        void EnableModelessSB(); void TranslateAcceleratorSB(); void BrowseObject();
        void GetViewStateStream(); void GetControlWindow(); void SendControlMsg();
        void QueryActiveShellView(out IntPtr shellView);
    }

    [ComImport, Guid("cde725b0-ccc9-4519-917e-325d72fab4ce"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IFolderView
    {
        void GetCurrentViewMode(out uint mode); void SetCurrentViewMode(uint mode);
        void GetFolder(ref Guid iid, out IntPtr folder); void Item(int index, out IntPtr pidl);
        void ItemCount(uint flags, out int count);
        void Items(uint flags, ref Guid iid, out IntPtr items);
        void GetSelectionMarkedItem(out int index); void GetFocusedItem(out int index);
        void GetItemPosition(IntPtr pidl, out Point pt); void GetSpacing(out Point spacing);
        void GetDefaultSpacing(out Point spacing); void GetAutoArrange();
        void SelectItem(int index, uint flags);
        void SelectAndPositionItems(uint count, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] IntPtr[] pidls,
            [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] Point[] points, uint flags);
    }

    [ComImport, Guid("000214F2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IEnumIDList
    {
        [PreserveSig] int Next(uint celt, out IntPtr item, IntPtr fetched);
        void Skip(uint count); void Reset(); void Clone(out IntPtr clone);
    }

    [ComImport, Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85"), InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    interface IShellWindows
    {
        [DispId(0)] int Count { get; }
        [DispId(0x60020000)] object Item([In, Optional] object index);
        [DispId(0x60020001)] object _NewEnum();
        [DispId(0x60020002)] void Register();
        [DispId(0x60020003)] void RegisterPending();
        [DispId(0x60020004)] void RevokeCookie();
        [DispId(0x60020005)] void OnNavigate();
        [DispId(0x60020006)] void OnActivated();
        [DispId(0x60020008)] object FindWindowSW(ref object location, ref object root, int classType, out int hwnd, int options);
    }

    static IFolderView OpenView()
    {
        object? windows = null, dispatch = null;
        IntPtr browserPtr = IntPtr.Zero, viewPtr = IntPtr.Zero;
        try
        {
            windows = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39"))!);
            if (windows is null) throw new InvalidOperationException("Windows Shell is unavailable.");
            object location = 0; // CSIDL_DESKTOP
            object root = Type.Missing;
            dispatch = ((IShellWindows)windows).FindWindowSW(ref location, ref root, 8, out _, 1);
            if (dispatch is not IServiceProvider service) throw new InvalidOperationException("Desktop Shell service was not found.");
            var sid = new Guid("4C96BE40-915C-11CF-99D3-00AA004AE837"); // SID_STopLevelBrowser
            var browserGuid = typeof(IShellBrowser).GUID;
            Marshal.ThrowExceptionForHR(service.QueryService(ref sid, ref browserGuid, out browserPtr));
            var browser = (IShellBrowser)Marshal.GetObjectForIUnknown(browserPtr);
            try { browser.QueryActiveShellView(out viewPtr); }
            finally { Marshal.ReleaseComObject(browser); }
            if (viewPtr == IntPtr.Zero) throw new InvalidOperationException("Desktop view is unavailable.");
            var viewGuid = typeof(IFolderView).GUID;
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(viewPtr, ref viewGuid, out var fvPtr));
            try { return (IFolderView)Marshal.GetObjectForIUnknown(fvPtr); }
            finally { Marshal.Release(fvPtr); }
        }
        finally
        {
            if (viewPtr != IntPtr.Zero) Marshal.Release(viewPtr);
            if (browserPtr != IntPtr.Zero) Marshal.Release(browserPtr);
            if (dispatch != null && Marshal.IsComObject(dispatch)) Marshal.ReleaseComObject(dispatch);
            if (windows != null && Marshal.IsComObject(windows)) Marshal.ReleaseComObject(windows);
        }
    }

    // PIDL is a sequence of SHITEMID blocks; each starts with a ushort cb and terminates with a zero ushort.
    static byte[] PidlBytes(IntPtr pidl)
    {
        var length = 0;
        while (true)
        {
            ushort size = (ushort)Marshal.ReadInt16(pidl, length);
            if (size == 0) { length += 2; break; }
            if (size < 2 || length + size > 65536) throw new InvalidDataException("Invalid PIDL.");
            length += size;
        }
        var bytes = new byte[length]; Marshal.Copy(pidl, bytes, 0, length); return bytes;
    }
    static Dictionary<string, (IntPtr Pidl, Point Position)> Enumerate(IFolderView view)
    {
        var found = new Dictionary<string, (IntPtr Pidl, Point Position)>(StringComparer.Ordinal);
        var iid = typeof(IEnumIDList).GUID;
        view.Items(2, ref iid, out var enumPtr); // SVGIO_ALLVIEW
        if (enumPtr == IntPtr.Zero) throw new InvalidOperationException("Icon enumeration failed.");
        var iterator = (IEnumIDList)Marshal.GetObjectForIUnknown(enumPtr);
        Marshal.Release(enumPtr);
        try
        {
            while (iterator.Next(1, out var pidl, IntPtr.Zero) == 0)
            {
                if (pidl == IntPtr.Zero) break;
                try
                {
                    view.GetItemPosition(pidl, out var pos);
                    var key = Convert.ToBase64String(PidlBytes(pidl));
                    found[key] = (pidl, pos);
                }
                catch { Marshal.FreeCoTaskMem(pidl); throw; }
            }
            return found;
        }
        catch { foreach (var entry in found.Values) Marshal.FreeCoTaskMem(entry.Pidl); throw; }
        finally { Marshal.ReleaseComObject(iterator); }
    }
    public static List<IconPosition> Capture()
    {
        var view = OpenView();
        try
        {
            var icons = Enumerate(view);
            try { return icons.Select(kv => new IconPosition { Pidl = kv.Key, X = kv.Value.Position.X, Y = kv.Value.Position.Y }).ToList(); }
            finally { foreach (var icon in icons.Values) Marshal.FreeCoTaskMem(icon.Pidl); }
        }
        finally { Marshal.ReleaseComObject(view); }
    }
    public static int Restore(IReadOnlyCollection<IconPosition> positions)
    {
        var view = OpenView();
        try
        {
            var icons = Enumerate(view);
            try
            {
                var pidls = new List<IntPtr>(); var points = new List<Point>();
                foreach (var pos in positions)
                    if (icons.TryGetValue(pos.Pidl, out var match)) { pidls.Add(match.Pidl); points.Add(new Point { X = pos.X, Y = pos.Y }); }
                if (pidls.Count > 0) view.SelectAndPositionItems((uint)pidls.Count, pidls.ToArray(), points.ToArray(), 0x80); // SVSI_POSITIONITEM
                return pidls.Count;
            }
            finally { foreach (var icon in icons.Values) Marshal.FreeCoTaskMem(icon.Pidl); }
        }
        finally { Marshal.ReleaseComObject(view); }
    }
}
