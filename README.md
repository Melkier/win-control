# win-control

Utilidad local para Windows (.NET 8) con interfaz CLI y transporte `stdio` (`--mcp`).

## Build

```powershell
dotnet build WinControl.csproj -c Release -o bin\dist
```

## Uso

```powershell
.\bin\dist\win-control.exe --mcp
.\bin\dist\win-control.exe help
```
