<div align="center">

<img src="web/icon.png" width="96" alt="LotusLED Desk">

# LotusLED Desk

**Controla desde tu PC las tiras LED Bluetooth de la app LotusLamp X, sin el teléfono.**

Color · brillo · 22 efectos · modo música · ambilight · se enciende con Windows y se apaga con el PC

![Windows](https://img.shields.io/badge/Windows-10%20%7C%2011-0078D6?logo=windows)
![Bluetooth LE](https://img.shields.io/badge/Bluetooth-LE-0082FC?logo=bluetooth)
![License: MIT](https://img.shields.io/badge/license-MIT-green)

<img src="web/setup.jpg" width="480" alt="Setup con LotusLED Desk">

</div>

---

## ¿Qué es?

Muchas tiras LED baratas para el monitor o el escritorio se manejan con la app móvil **LotusLamp X**.
LotusLED Desk habla directamente con esas tiras por **Bluetooth LE** desde Windows, así que puedes
controlarlas desde el PC con una interfaz moderna que reacciona a tu música o a tu pantalla.

**Controladores compatibles:** `L7179`, `ELK-BLEDOM`, `MELK`, `LEDDMX`, `BJ_LED`, `Triones` y otros
que usan el protocolo de tramas `7E … EF` (servicio `FFF0`, característica `FFF3`).

## Funciones

| | |
|---|---|
| 🎨 **Color** | Rueda interactiva, código HEX, temperatura de blanco y favoritos |
| ✨ **Efectos** | 22 efectos dinámicos (salto, respiración, fundido, destello) con control de velocidad |
| 🎵 **Modo música** | Espectro, pulso, arcoíris o golpe al ritmo del audio del PC o del micrófono |
| 🖥️ **Ambilight** | Las luces copian en tiempo real el color dominante de tu pantalla |
| ⏻ **Automático** | Se enciende al iniciar sesión y se apaga al apagar o reiniciar el PC |
| 🔔 **Bandeja** | Icono en la bandeja del sistema para encender/apagar sin abrir la ventana |
| ⏲️ **Temporizador** | Apagado programado |

## Instalación

1. Descarga el proyecto (**Code → Download ZIP**) y descomprímelo donde quieras.
2. Haz doble clic en **`Instalar.bat`**.
3. Cierra LotusLamp X en el móvil (la tira solo acepta una conexión a la vez).
4. Abre **LotusLED Desk** desde el Escritorio, pulsa **Conectar tiras** y elige tu tira.

Desde ese momento se reconecta sola. Para quitarlo: **`Desinstalar.bat`**.

> **Requisitos:** Windows 10/11 con Bluetooth, y Microsoft Edge o Google Chrome (la interfaz usa Web Bluetooth).

## Cómo funciona

```
LotusLED.ps1 ──► servidor local http://localhost:8787 ──► ventana Edge/Chrome (modo app)
     │                                                        │
     ├─ icono en la bandeja del sistema                       └─ web/ (interfaz + Web Bluetooth → tira LED)
     └─ al apagar Windows avisa a la interfaz para apagar las luces

LotusShutdown.exe ─► tarea programada de respaldo que apaga la tira al apagar el PC
LotusLED.exe      ─► versión alternativa 100% nativa (WinForms + Bluetooth de WinRT)
```

| Carpeta / archivo | Qué hace |
|---|---|
| `web/` | Interfaz (HTML/CSS/JS): `ble.js` protocolo Bluetooth, `sync.js` música y ambilight, `app.js` lógica |
| `LotusLED.ps1` | Lanzador: servidor local, bandeja del sistema y aviso de apagado |
| `src/` | Código C# de la versión nativa (`LotusLED.exe`) y de `LotusShutdown.exe` |
| `build.ps1` | Compila los dos `.exe` con el compilador que ya trae Windows |
| `Instalar.ps1` / `Desinstalar.ps1` | Accesos directos, inicio automático y tarea de apagado |

## Compilar los .exe

No necesitas Visual Studio:

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1
```

Para que `LotusShutdown.exe` sepa qué tira apagar, crea un archivo `device.cfg` junto al `.exe`
con la MAC de tu tira (por ejemplo `AA:BB:CC:DD:EE:FF`), o pásala como argumento.
Ese archivo no se sube a GitHub.

## Protocolo (para curiosos)

Cada comando es una trama de 9 bytes (controladores ELK-BLEDOM):

| Acción | Trama |
|---|---|
| Encender | `7E 00 04 F0 00 01 FF 00 EF` |
| Apagar | `7E 00 04 00 00 00 FF 00 EF` |
| Color RGB | `7E 00 05 03 RR GG BB 00 EF` |
| Brillo (0–100) | `7E 00 01 BB 00 00 00 00 EF` |
| Efecto | `7E 00 03 ID 03 00 00 00 EF` |
| Velocidad (0–100) | `7E 00 02 VV 00 00 00 00 EF` |

## Licencia

[MIT](LICENSE): úsalo, modifícalo y compártelo libremente.

---

<div align="center">Hecho por <b>Leivis</b> para su setup 💡</div>
