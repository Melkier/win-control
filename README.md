# win-control

Utilidad local de Windows (.NET 8) y servidor MCP (`stdio`, JSON-RPC 2.0) para inspeccionar y controlar ventanas del escritorio de Windows.

## Requisitos

- Windows 10 / 11
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)

## Compilación

```powershell
dotnet build WinControl.csproj -c Release -o bin\dist
```

Esto genera `bin\dist\win-control.exe`.

## Uso como servidor MCP

Inicia en modo MCP por entrada/salida estándar:

```powershell
.\bin\dist\win-control.exe --mcp
```

Ejemplo de registro en `mcp_config.json`:

```json
{
  "mcpServers": {
    "win_control": {
      "command": "C:\\ruta\\a\\win-control\\bin\\dist\\win-control.exe",
      "args": ["--mcp"]
    }
  }
}
```

### Herramientas MCP expuestas

- `desktop_screens`: lista monitores y resoluciones físicas.
- `desktop_windows`: lista ventanas visibles con `HWND`, `PID`, proceso, título y coordenadas.
- `desktop_controls`: lista controles hijos Win32 de una ventana.
- `desktop_screenshot`: captura pantalla completa, monitor o ventana en PNG (base64 + coordenadas físicas).
- `desktop_launch`: abre una aplicación, acceso directo, carpeta o archivo.
- `desktop_focus`: restaura y trae al frente una ventana.
- `desktop_click`: clic izquierdo, derecho, medio o doble clic en coordenadas físicas `(x, y)`.
- `desktop_drag`: arrastre suave entre dos puntos.
- `desktop_scroll`: rueda del ratón en `(x, y)`.
- `desktop_type`: escribe texto Unicode en la ventana enfocada.
- `desktop_key`: envía teclas o atajos (`Enter`, `Ctrl+S`, `Alt+F4`, `Win+R`, etc.).

## Uso desde línea de comandos (CLI)

```powershell
.\bin\dist\win-control.exe screens
.\bin\dist\win-control.exe windows --filter Code
.\bin\dist\win-control.exe screenshot captura.png
.\bin\dist\win-control.exe focus "Visual Studio Code"
.\bin\dist\win-control.exe click 500 300
.\bin\dist\win-control.exe type "Hola mundo"
.\bin\dist\win-control.exe key "Ctrl+S"
```
