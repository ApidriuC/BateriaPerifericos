# Batería de periféricos

Pequeña app para Windows 11 (.NET 8, WinForms + Win32) que muestra la batería de mis periféricos inalámbricos
directamente en el extremo izquierdo de la barra de tareas, en todas las pantallas:

```
🎧 91%   🖱⚡ 65%                 ⊞  📁  🌐  …                 ^  ESP  🔊  12:41
```

Sin software del fabricante abierto: habla directamente con los receptores USB por HID.

## Dispositivos soportados

| Dispositivo | Receptor | Cómo se lee |
|---|---|---|
| Mouse **Scyrox V8** | Dongle 8K `3554:F5F7` (cable `F5F6`) | Colección vendor `FF02:0002`, reporte `0x08` de 17 bytes, comando `0x04`. El % se calcula a partir del voltaje con la misma curva que la app oficial (3050 mV = 0 % … 4110 mV = 100 %). Mientras carga se descuentan ~35 mV que agrega la corriente de carga (calibrado contra la app oficial). Protocolo Compx (el mismo de ATK/VXE/VGN). |
| Audífonos **Fractal Design Scape** | Dongle `36BC:0001` (cable `2001`) | Colección vendor `FF00:0001`, reporte `0x02` de 64 bytes: `11 21` (¿enlazados?) y `F1 21` (byte 15 = %). |

> El mouse solo responde mientras está despierto; en reposo se muestra la última lectura atenuada.
> Los audífonos no responden mientras están en la base.

## Cómo se ve

- Ícono del dispositivo + porcentaje, en **verde** (> 50 %), **amarillo** (21–50 %) o **rojo** (≤ 20 %).
- Rayito en la esquina del ícono: cargando (el % va subiendo y nunca baja mientras carga).
- Color atenuado: dispositivo en reposo (última lectura conocida). `--`: aún sin lectura.
- Aparece en la barra de cada pantalla, también si conectas una nueva o se reinicia el Explorador.
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
