# Yib for Windows

Yib for Windows is a tiny Windows system-tray app that lets you shake the mouse to open a dial, pick a number, and paste recent files from Downloads/Desktop/custom folders.

## Build

From the project folder:

```powershell
dotnet publish -c Release -r win-x64 --self-contained true
```

The published single-file executable will be created under:

```text
Yib/bin/Release/net8.0-windows/win-x64/publish/
```

## Startup on boot

The app includes a tray menu item for "เริ่มทำงานตอนเปิดเครื่อง". Enabling it writes a registry Run entry so the app starts with Windows.
