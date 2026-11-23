# Batería de periféricos

Pequeña app para Windows 11 (.NET 8, WinForms) que muestra la batería de mis periféricos inalámbricos
directamente en la barra de tareas, a la izquierda del área de notificación:

```
🎧 100%   🖱 46%   ^   ESP  🔊  12:41
```

Sin software del fabricante abierto: habla directamente con los receptores USB por HID.

## Dispositivos soportados

| Dispositivo | Receptor | Cómo se lee |
|---|---|---|
| Mouse **Scyrox V8** | Dongle 8K `3554:F5F7` (cable `F5F6`) | Colección vendor `FF02:0002`, reporte `0x08` de 17 bytes, comando `0x04`. El % se calcula a partir del voltaje con la misma curva que la app oficial (3050 mV = 0 % … 4110 mV = 100 %). Protocolo Compx (el mismo de ATK/VXE/VGN). |
| Audífonos **Fractal Design Scape** | Dongle `36BC:0001` (cable `2001`) | Colección vendor `FF00:0001`, reporte `0x02` de 64 bytes: `11 21` (¿enlazados?) y `F1 21` (byte 15 = %). |

> El mouse solo responde mientras está despierto; en reposo se muestra la última lectura atenuada.
> Los audífonos no responden mientras están en la base.

## Cómo se ve

- Ícono del dispositivo + porcentaje, en **verde** (> 50 %), **amarillo** (21–50 %) o **rojo** (≤ 20 %).
- Color atenuado: dispositivo en reposo (última lectura conocida). `--`: aún sin lectura.
- Clic izquierdo: actualizar. Clic derecho: detalle, *Iniciar con Windows*, *Salir*.
- Aviso emergente al bajar de 15 %.
- Se consulta cada 30 s. Si no se puede incrustar en la barra, usa íconos de la bandeja del sistema.

Registro de actividad y errores en `%LOCALAPPDATA%\BateriaPerifericos\`.

## Compilar y ejecutar

Requiere el SDK de .NET 8.

```powershell
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish
.\publish\BateriaPerifericos.exe
```

## Agregar otro dispositivo

Implementa `IBatteryProvider` (en `Providers.cs`) y agrégalo en `Program.cs`. `Hid.Transact` se encarga de
enviar el reporte y esperar la respuesta.

## Créditos

Protocolos descubiertos por la comunidad, no son oficiales y pueden cambiar con el firmware:

- [Fan4Metal/mouse_tray](https://github.com/Fan4Metal/mouse_tray) — protocolo Compx/ATK/VXE y soporte Scyrox V8.
- [rhys-saldanha/fractal-scape-protocol](https://github.com/rhys-saldanha/fractal-scape-protocol) — protocolo del Fractal Scape.
