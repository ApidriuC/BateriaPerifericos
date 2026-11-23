using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace BateriaPerifericos;

/// <summary>Un ícono por periférico en la bandeja: silueta del dispositivo del color del nivel y el % abajo.</summary>
public sealed class TrayContext : ApplicationContext
{
    // El mouse solo contesta mientras se usa, así que se consulta seguido (la consulta es mínima).
    private const int PollMs = 30_000;
    private const int LowBattery = 15;
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "BateriaPerifericos";

    private readonly List<Slot> _slots = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 300 };
    private readonly ContextMenuStrip _menu = new();
    private readonly ToolStripMenuItem _startup = new("Iniciar con Windows") { CheckOnClick = true };
    private readonly TaskbarWidget _widget;
    private readonly bool _widgetMode;
    private bool _busy;

    private sealed class Slot
    {
        public required IBatteryProvider Provider { get; init; }
        public required NotifyIcon Icon { get; init; }
        public required ToolStripMenuItem MenuItem { get; init; }
        public bool LowWarned;
        public BatteryReading? LastGood;
        public DateTime LastGoodAt;
        /// <summary>La última lectura no fue en vivo (dispositivo en reposo o desconectado).</summary>
        public bool Stale;
    }

    public TrayContext(IEnumerable<IBatteryProvider> providers)
    {
        foreach (var p in providers)
        {
            var item = new ToolStripMenuItem($"{p.Name}: leyendo…") { Enabled = false };
            _menu.Items.Add(item);
            var icon = new NotifyIcon { ContextMenuStrip = _menu, Text = p.Name, Visible = true };
            SetIcon(icon, MakeIcon(p.Kind, IconRenderer.Gray, "--"));
            icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) _ = RefreshAsync(); };
            _slots.Add(new Slot { Provider = p, Icon = icon, MenuItem = item });
        }

        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Actualizar ahora", null, (_, _) => _ = RefreshAsync());
        _startup.Checked = IsStartupEnabled();
        _startup.CheckedChanged += (_, _) => SetStartup(_startup.Checked);
        _menu.Items.Add(_startup);
        _menu.Items.Add("Salir", null, (_, _) => ExitThread());

        // Panel dentro de la barra de tareas (como "🎧 44%  🖱 43%"). Si no se puede incrustar,
        // se usan los íconos de la bandeja.
        LoadLastReadings();

        _widget = new TaskbarWidget(_menu, () => _ = RefreshAsync());
        _widgetMode = _widget.Attach();
        if (_widgetMode)
        {
            foreach (var s in _slots) s.Icon.Visible = false;
            UpdateWidget();
        }

        Activity.Log($"inicio (modo {(_widgetMode ? "panel" : "bandeja")})");
        _timer.Tick += async (_, _) =>
        {
            bool first = _timer.Interval != PollMs;
            _timer.Interval = PollMs;
            await RefreshAsync();
            // Explorer registra los íconos al mostrarlos la primera vez, pero solo lee "IsPromoted"
            // cuando un ícono aparece: si se acaba de anclar, se vuelven a agregar para que aplique ya.
            if (first && !_widgetMode && PinTrayIcons())
                foreach (var s in _slots) { s.Icon.Visible = false; s.Icon.Visible = true; }
        };
        _timer.Start();
    }

    private async Task RefreshAsync()
    {
        if (_busy) { Activity.Log("actualización omitida: la anterior sigue en curso"); return; }
        _busy = true;
        try
        {
            foreach (var slot in _slots)
            {
                BatteryReading r;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    // Una consulta que no responda no puede bloquear las siguientes.
                    var read = Task.Run(slot.Provider.Read);
                    r = await Task.WhenAny(read, Task.Delay(5000)) == read
                        ? await read
                        : new BatteryReading(DeviceState.Asleep, Detail: "Sin respuesta");
                }
                catch (Exception ex) { r = new BatteryReading(DeviceState.NotConnected, Detail: ex.Message); }
                Activity.Log($"{slot.Provider.Name}: {r.State} {r.Percent}% {r.Detail} ({sw.ElapsedMilliseconds} ms)");
                Apply(slot, r);
            }
            UpdateWidget();
            SaveLastReadings();
        }
        catch (Exception ex) { Activity.Log("error en actualización: " + ex); }
        finally { _busy = false; }
    }

    // Última lectura por dispositivo, para no mostrar "--" tras reiniciar si el mouse está en reposo.
    private static readonly string StateFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BateriaPerifericos", "ultima-lectura.json");

    private sealed record Saved(int Percent, DateTime At);

    private void LoadLastReadings()
    {
        try
        {
            var data = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, Saved>>(File.ReadAllText(StateFile));
            Activity.Log($"cargado: {data?.Count ?? -1} lecturas");
            foreach (var s in _slots)
                if (data != null && data.TryGetValue(s.Provider.Name, out var v))
                {
                    s.LastGood = new BatteryReading(DeviceState.Ok, v.Percent);
                    s.LastGoodAt = v.At;
                    s.Stale = true;
                }
        }
        catch (FileNotFoundException) { /* primera ejecución */ }
        catch (Exception ex) { Activity.Log("no se pudo cargar la última lectura: " + ex.Message); }
    }

    private void SaveLastReadings()
    {
        try
        {
            var data = _slots.Where(s => s.LastGood != null)
                .ToDictionary(s => s.Provider.Name, s => new Saved(s.LastGood!.Percent, s.LastGoodAt));
            Directory.CreateDirectory(Path.GetDirectoryName(StateFile)!);
            File.WriteAllText(StateFile, System.Text.Json.JsonSerializer.Serialize(data));
        }
        catch (Exception ex) { Activity.Log("no se pudo guardar la última lectura: " + ex.Message); }
    }

    private void UpdateWidget()
    {
        if (!_widgetMode) return;
        var items = _slots.Select(s =>
        {
            var kind = s.Provider.Kind;
            if (s.LastGood is not { } lg) return new WidgetItem(kind, "--", IconRenderer.Gray);
            var color = IconRenderer.LevelColor(lg.Percent);
            if (s.Stale) color = Color.FromArgb((color.R + 2 * 150) / 3, (color.G + 2 * 150) / 3, (color.B + 2 * 150) / 3);
            return new WidgetItem(kind, $"{lg.Percent}%", color);
        }).ToList();
        _widget.SetItems(items);
    }

    private void Apply(Slot slot, BatteryReading r)
    {
        string name = slot.Provider.Name;
        if (r.State == DeviceState.Ok) { slot.LastGood = r; slot.LastGoodAt = DateTime.Now; }
        if (r.State == DeviceState.NotConnected) slot.LastGood = null;
        slot.Stale = r.State != DeviceState.Ok;

        string status = r.State switch
        {
            DeviceState.Ok => $"{r.Percent}%" + (r.Charging ? " (cargando)" : "") + (r.Detail is null ? "" : $" · {r.Detail}"),
            DeviceState.Asleep when slot.LastGood is { } lg =>
                $"{lg.Percent}% a las {slot.LastGoodAt:HH:mm} · {(r.Detail ?? "en reposo").ToLowerInvariant()}",
            DeviceState.Asleep => r.Detail ?? "En reposo (úsalo para leer la batería)",
            _ => "Receptor no conectado",
        };

        slot.MenuItem.Text = $"{name}: {status}";
        string tip = $"{name}\n{status}";
        slot.Icon.Text = tip.Length > 127 ? tip[..127] : tip;

        var kind = slot.Provider.Kind;
        if (r.State == DeviceState.Ok)
        {
            SetIcon(slot.Icon, MakeIcon(kind, IconRenderer.LevelColor(r.Percent), r.Percent.ToString()));

            if (r.Percent <= LowBattery && !r.Charging && !slot.LowWarned)
            {
                slot.LowWarned = true;
                // En modo panel el ícono de bandeja está oculto: se muestra solo para el aviso.
                if (_widgetMode)
                {
                    slot.Icon.Visible = true;
                    var ni = slot.Icon;
                    void Hide(object? s, EventArgs e) { ni.Visible = false; ni.BalloonTipClosed -= Hide; ni.BalloonTipClicked -= Hide; }
                    ni.BalloonTipClosed += Hide;
                    ni.BalloonTipClicked += Hide;
                }
                slot.Icon.ShowBalloonTip(5000, "Batería baja", $"{name}: {r.Percent}%. Conéctalo a cargar.", ToolTipIcon.Warning);
            }
            else if (r.Percent > LowBattery + 5 || r.Charging)
            {
                slot.LowWarned = false;
            }
        }
        else if (r.State == DeviceState.Asleep && slot.LastGood is { } lg)
        {
            // En reposo: se mantiene el último % conocido, atenuado.
            SetIcon(slot.Icon, MakeIcon(kind, IconRenderer.LevelColor(lg.Percent), lg.Percent.ToString(), dimmed: true));
        }
        else
        {
            SetIcon(slot.Icon, MakeIcon(kind, IconRenderer.Gray, "--"));
        }
    }

    private static Icon MakeIcon(DeviceKind kind, Color color, string? text, bool dimmed = false)
    {
        using var bmp = IconRenderer.Render(kind, color, text, dimmed);
        IntPtr h = bmp.GetHicon();
        try { return (Icon)Icon.FromHandle(h).Clone(); }
        finally { DestroyIcon(h); }
    }

    private static void SetIcon(NotifyIcon ni, Icon icon)
    {
        var old = ni.Icon;
        ni.Icon = icon;
        old?.Dispose();
    }

    /// <summary>
    /// Deja los íconos siempre visibles en la barra (Windows 11: Configuración › Personalización ›
    /// Barra de tareas › Otros íconos de la bandeja del sistema). Solo toca entradas de este .exe
    /// en las que el usuario aún no ha elegido nada, para respetar si luego decide ocultarlos.
    /// </summary>
    private static bool PinTrayIcons()
    {
        bool changed = false;
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings");
            if (root == null) return false;
            foreach (var id in root.GetSubKeyNames())
            {
                using var key = root.OpenSubKey(id, writable: true);
                if (key?.GetValue("ExecutablePath") is string exe
                    && string.Equals(exe, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase)
                    && key.GetValue("IsPromoted") is null)
                {
                    key.SetValue("IsPromoted", 1, RegistryValueKind.DWord);
                    changed = true;
                }
            }
        }
        catch { /* sin permisos o versión de Windows distinta: se ignora */ }
        return changed;
    }

    private static bool IsStartupEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(RunValue) is string;
    }

    private static void SetStartup(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(RunValue, $"\"{Environment.ProcessPath}\"");
        else key.DeleteValue(RunValue, throwOnMissingValue: false);
    }

    protected override void ExitThreadCore()
    {
        _timer.Stop();
        _widget.Dispose();
        foreach (var s in _slots) { s.Icon.Visible = false; s.Icon.Dispose(); }
        _menu.Dispose();
        base.ExitThreadCore();
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);
}
