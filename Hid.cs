using System.Runtime.InteropServices;
using HidSharp;
using HidSharp.Reports;
using Microsoft.Win32.SafeHandles;

namespace BateriaPerifericos;

internal static class Hid
{
    public static bool HasUsage(HidDevice d, uint page, uint usage)
    {
        try
        {
            uint full = (page << 16) | usage;
            return d.GetReportDescriptor().DeviceItems.Any(i => i.Usages.GetAllValues().Contains(full));
        }
        catch { return false; }
    }

    /// <summary>
    /// Envía un reporte y espera la respuesta que cumpla <paramref name="match"/>.
    /// La lectura se deja pendiente ANTES de escribir: el dongle Scyrox no entrega la
    /// respuesta si no hay una lectura en curso cuando llega.
    /// </summary>
    public static byte[]? Transact(HidDevice dev, byte[] request, int replyLength, bool useSetOutputReport,
        int timeoutMs, Func<byte[], bool> match)
    {
        HidStream? stream = null;
        try
        {
            if (!dev.TryOpen(out stream)) return null;
            stream.ReadTimeout = 100;

            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            var started = new ManualResetEventSlim();
            var reader = Task.Run(() =>
            {
                var buf = new byte[Math.Max(replyLength, dev.GetMaxInputReportLength())];
                started.Set();
                while (DateTime.UtcNow < deadline)
                {
                    try
                    {
                        int n = stream.Read(buf);
                        if (n >= replyLength && match(buf)) return buf[..n];
                    }
                    catch (TimeoutException) { }
                    catch (IOException) { return null; }
                    catch (ObjectDisposedException) { return null; }
                }
                return null;
            });

            started.Wait(500);
            Thread.Sleep(20);

            if (useSetOutputReport)
            {
                using var h = CreateFile(dev.DevicePath, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
                    IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
                if (h.IsInvalid || !HidD_SetOutputReport(h, request, request.Length)) return null;
            }
            else
            {
                stream.Write(request);
            }

            return reader.Result;
        }
        catch
        {
            return null;
        }
        finally
        {
            stream?.Dispose();
        }
    }

    private const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3;

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_SetOutputReport(SafeFileHandle h, byte[] buffer, int length);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sa,
        uint disposition, uint flags, IntPtr template);
}
