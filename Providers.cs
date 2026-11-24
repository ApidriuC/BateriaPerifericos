using HidSharp;
using HidSharp.Reports;

namespace BateriaPerifericos;

public enum DeviceState { Ok, Asleep, NotConnected }

public sealed record BatteryReading(DeviceState State, int Percent = 0, bool Charging = false, string? Detail = null);

public interface IBatteryProvider
{
    string Name { get; }
    /// <summary>Silueta que se dibuja en el ícono.</summary>
    DeviceKind Kind { get; }
    BatteryReading Read();
}

/// <summary>
/// Mouse Scyrox V8 por su dongle 8K (chip Compx/Nordic, mismo protocolo que ATK/VXE/VGN).
/// Colección vendor FF02:0002, reporte 0x08 de 17 bytes; los 17 bytes suman 0x55 (mod 256).
/// Respuesta a 0x04: [6] = % batería, [7] = alimentación USB, [8..9] = mV (big-endian).
/// </summary>
public sealed class ScyroxProvider : IBatteryProvider
{
    private const int Vid = 0x3554;
    private static readonly int[] Pids = { 0xF5F7 /* dongle */, 0xF5F6 /* cable */ };
    private const byte CmdPower = 0x04;

    public string Name => "Mouse Scyrox V8";
    public DeviceKind Kind => DeviceKind.Mouse;

    public BatteryReading Read()
    {
        var dev = DeviceList.Local.GetHidDevices(Vid)
            .Where(d => Pids.Contains(d.ProductID))
            .OrderBy(d => d.ProductID == 0xF5F6 ? 0 : 1)
            .FirstOrDefault(d => d.GetMaxOutputReportLength() == 17 && Hid.HasUsage(d, 0xFF02, 0x0002));
        if (dev == null) return new(DeviceState.NotConnected);

        var req = new byte[17];
        req[0] = 0x08; req[1] = CmdPower;
        req[16] = Checksum(req);

        for (int attempt = 0; attempt < 2; attempt++)
        {
            var r = Hid.Transact(dev, req, 17, useSetOutputReport: true, timeoutMs: 600,
                match: b => b[0] == 0x08 && b[1] == CmdPower && b[16] == Checksum(b));
            if (r == null) continue;

            int firmwarePct = r[6];
            if (firmwarePct > 100) continue;
            bool usb = r[7] != 0;
            int mv = (r[8] << 8) | r[9];
            // Igual que la app oficial: % a partir del voltaje con su tabla. Con el cable conectado el
            // firmware reporta un valor fijo (95) y el voltaje sube por la corriente de carga, así que
            // se descuenta ese aumento antes de usar la tabla (medido contra la app: 3934 mV → 51 %).
            // El firmware en 100 indica carga completa.
            int pct = mv == 0 ? firmwarePct
                : usb ? (firmwarePct >= 100 ? 100 : PercentFromMillivolts(mv - ChargingOffsetMv))
                : PercentFromMillivolts(mv);
            return new(DeviceState.Ok, pct, usb && pct < 100, $"{mv / 1000.0:0.00} V");
        }
        // El dongle está conectado pero el mouse no contesta: apagado o dormido.
        return new(DeviceState.Asleep);
    }

    private static byte Checksum(byte[] f)
    {
        int sum = 0;
        for (int i = 0; i < 16; i++) sum += f[i];
        return (byte)((0x55 - sum) & 0xFF);
    }

    /// <summary>
    /// Tabla voltaje→% de la app oficial ScyRox (HIDUsb.dll): un punto cada 5 %, de 0 % a 100 %.
    /// Se interpola linealmente entre puntos y se trunca, igual que la app (3854 mV → 46 %).
    /// </summary>
    private static readonly int[] CurveMv =
    {
        3050, 3420, 3480, 3540, 3600, 3660, 3720, 3760, 3800, 3840, 3880,
        3920, 3940, 3960, 3980, 4000, 4020, 4040, 4060, 4080, 4110,
    };

    /// <summary>Aumento aproximado del voltaje mientras carga (deducido comparando con la app oficial).</summary>
    internal const int ChargingOffsetMv = 45;

    internal static int PercentFromMillivolts(int mv)
    {
        if (mv <= CurveMv[0]) return 0;
        if (mv >= CurveMv[^1]) return 100;
        int i = 1;
        while (mv > CurveMv[i]) i++;
        double frac = (double)(mv - CurveMv[i - 1]) / (CurveMv[i] - CurveMv[i - 1]);
        return (int)((i - 1 + frac) * 5);
    }
}

/// <summary>
/// Audífonos Fractal Design Scape por su dongle 2.4 GHz (o cable USB-C).
/// Colección vendor FF00:0001, reporte 0x02 de 64 bytes.
///   02 11 21 → estado del dongle; [4] = 1 si los audífonos están enlazados (0 = en la base).
///   02 F1 21 → estado de los audífonos (no responde si están en la base); [15] = % batería.
/// Protocolo no oficial: https://github.com/rhys-saldanha/fractal-scape-protocol
/// </summary>
public sealed class FractalScapeProvider : IBatteryProvider
{
    private const int Vid = 0x36BC;
    private static readonly int[] Pids = { 0x0001 /* dongle */, 0x2001 /* cable */ };

    public string Name => "Audífonos Fractal Scape";
    public DeviceKind Kind => DeviceKind.Headset;

    public BatteryReading Read()
    {
        var dev = DeviceList.Local.GetHidDevices(Vid)
            .Where(d => Pids.Contains(d.ProductID))
            .FirstOrDefault(d => d.GetMaxOutputReportLength() == 64 && Hid.HasUsage(d, 0xFF00, 0x0001));
        if (dev == null) return new(DeviceState.NotConnected);

        var linkReq = new byte[64]; linkReq[0] = 0x02; linkReq[1] = 0x11; linkReq[2] = 0x21;
        var link = Hid.Transact(dev, linkReq, 64, useSetOutputReport: false, timeoutMs: 500,
            match: b => b[0] == 0x02 && b[1] == 0x11 && b[2] == 0x21);
        if (link != null && link[4] == 0)
            return new(DeviceState.Asleep, Detail: "En la base o apagados");

        var req = new byte[64]; req[0] = 0x02; req[1] = 0xF1; req[2] = 0x21;
        var r = Hid.Transact(dev, req, 64, useSetOutputReport: false, timeoutMs: 500,
            match: b => b[0] == 0x02 && b[1] == 0xF1 && b[2] == 0x21);
        if (r == null || r[15] > 100) return new(DeviceState.Asleep, Detail: "En la base o apagados");

        return new(DeviceState.Ok, r[15]);
    }
}
