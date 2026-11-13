// Captures one window with Windows.Graphics.Capture (works for WinUI/DirectComposition content and
// for occluded windows, unlike GDI BitBlt). Used to check the app's UI during development.
//
//   WindowShot <pid> <out.png> [--scale 0.66]

using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;
using WinRT;

if (args.Length < 2) { Console.WriteLine("usage: WindowShot <pid> <out.png> [--scale f]"); return 2; }
int pid = int.Parse(args[0]);
string outPath = Path.GetFullPath(args[1]);
double scale = 1;
int si = Array.IndexOf(args, "--scale");
if (si >= 0 && si + 1 < args.Length) scale = double.Parse(args[si + 1], System.Globalization.CultureInfo.InvariantCulture);

using var proc = Process.GetProcessById(pid);
nint hwnd = proc.MainWindowHandle;
if (hwnd == 0) { Console.WriteLine("no main window"); return 1; }

var device = Native.CreateDevice();
var item = Native.CreateItemForWindow(hwnd);
using var pool = Direct3D11CaptureFramePool.CreateFreeThreaded(device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, item.Size);
using var session = pool.CreateCaptureSession(item);
try { session.IsBorderRequired = false; } catch { }
session.IsCursorCaptureEnabled = false;

var tcs = new TaskCompletionSource<SoftwareBitmap>();
pool.FrameArrived += async (p, _) =>
{
    using var frame = p.TryGetNextFrame();
    if (frame is null || tcs.Task.IsCompleted) return;
    try { tcs.TrySetResult(await SoftwareBitmap.CreateCopyFromSurfaceAsync(frame.Surface, BitmapAlphaMode.Premultiplied)); }
    catch (Exception e) { tcs.TrySetException(e); }
};
session.StartCapture();
var done = await Task.WhenAny(tcs.Task, Task.Delay(5000));
if (done != tcs.Task) { Console.WriteLine("timeout"); return 1; }
using var bitmap = await tcs.Task;

var folder = await StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(outPath)!);
var file = await folder.CreateFileAsync(Path.GetFileName(outPath), CreationCollisionOption.ReplaceExisting);
using (IRandomAccessStream stream = await file.OpenAsync(FileAccessMode.ReadWrite))
{
    var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
    encoder.SetSoftwareBitmap(bitmap);
    if (scale != 1)
    {
        encoder.BitmapTransform.ScaledWidth = (uint)(bitmap.PixelWidth * scale);
        encoder.BitmapTransform.ScaledHeight = (uint)(bitmap.PixelHeight * scale);
        encoder.BitmapTransform.InterpolationMode = BitmapInterpolationMode.Fant;
    }
    await encoder.FlushAsync();
}
Console.WriteLine($"saved {outPath} {bitmap.PixelWidth}x{bitmap.PixelHeight}");
return 0;

static class Native
{
    [ComImport, Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        nint CreateForWindow(nint window, in Guid iid);
        nint CreateForMonitor(nint monitor, in Guid iid);
    }

    [DllImport("d3d11.dll")]
    private static extern int D3D11CreateDevice(nint adapter, int driverType, nint software, uint flags, nint levels, uint numLevels, uint sdkVersion, out nint device, out int level, out nint context);

    [DllImport("d3d11.dll")]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice, out nint graphicsDevice);

    private static readonly Guid IID_IDXGIDevice = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
    private static readonly Guid IID_IInspectable = new("AF86E2E0-B12D-4c6a-9C5A-D7AA65101E90");

    public static IDirect3DDevice CreateDevice()
    {
        const int D3D_DRIVER_TYPE_HARDWARE = 1, D3D_DRIVER_TYPE_WARP = 5;
        const uint BGRA = 0x20;
        int hr = D3D11CreateDevice(0, D3D_DRIVER_TYPE_HARDWARE, 0, BGRA, 0, 0, 7, out var d3d, out _, out var ctx);
        if (hr < 0) hr = D3D11CreateDevice(0, D3D_DRIVER_TYPE_WARP, 0, BGRA, 0, 0, 7, out d3d, out _, out ctx);
        Marshal.ThrowExceptionForHR(hr);
        Marshal.Release(ctx);
        var iid = IID_IDXGIDevice;
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(d3d, in iid, out var dxgi));
        Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi, out var inspectable));
        Marshal.Release(dxgi);
        Marshal.Release(d3d);
        var device = MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
        Marshal.Release(inspectable);
        return device;
    }

    public static GraphicsCaptureItem CreateItemForWindow(nint hwnd)
    {
        var factory = ActivationFactory.Get("Windows.Graphics.Capture.GraphicsCaptureItem");
        var interop = factory.AsInterface<IGraphicsCaptureItemInterop>();
        var iid = typeof(GraphicsCaptureItem).GUID;
        // GraphicsCaptureItem's default interface IID.
        iid = new Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760");
        nint ptr = interop.CreateForWindow(hwnd, in iid);
        var item = GraphicsCaptureItem.FromAbi(ptr);
        Marshal.Release(ptr);
        return item;
    }
}
