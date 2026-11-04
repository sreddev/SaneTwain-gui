# SaneTwain Scan Center (C# / WPF)

Native Windows version of Scan Center, built for the Microsoft Store (MSIX). It scans from any
scanner shared with SANE (`saned`) on the network. Its ControlCenter3-style buttons save each scan
straight into the folder you choose when the scan finishes.

The Python version in the parent folder still works. This version replaces it.

## Build and run

Requires the .NET 10 SDK. Packaging also needs the Windows 10/11 SDK (for makeappx, makepri and signtool).

```powershell
dotnet build ScanCenter.slnx
dotnet run --project src\ScanCenter.App
```

## Projects

| Project | What it does |
|---|---|
| `src/ScanCenter.Core` | SANE network protocol, scanner capabilities, scanning (glass and document feeder), file naming, saving (PDF, searchable PDF, TIFF, JPEG, PNG), Windows OCR, e-mail attachment (Simple MAPI), settings and `sanetwain.ini` sharing |
| `src/ScanCenter.App` | WPF app with the Windows 11 Fluent theme (light/dark follows Windows): scan buttons, live preview, settings, printing, notification area, start with Windows, single instance |
| `tools/ScanCenter.Smoke` | Console check against a real scanner: `ScanCenter.Smoke <host> <outFolder>` |
| `packaging/` | MSIX manifest, logos and build scripts |

## Settings

- Stored in `%LOCALAPPDATA%\ScanCenter\settings.json`. In the MSIX build, Windows keeps this inside the
  package's own data folder.
- On first run, connection, preview, JPEG and printer settings are imported from `%USERPROFILE%\sanetwain.ini`
  if it exists. After that, changes to those keys are written back to it (App settings > General >
  *Share connection settings with SaneTwain*). Only SaneTwain's own keys are changed, in place, and the
  original file is copied to `sanetwain.ini.before-scancenter` the first time.
- For testing, `SCANCENTER_HOME` and `SCANCENTER_SANETWAIN_INI` point these at other locations.

## Window layout

- **Header:** app name, scanner and connection state, and **Settings** (options for the whole app).
- **Left sidebar:** settings for the chosen scan button, from the top of the window down, in three groups:
  Destination, Scan quality, and When the scan ends.
- **Scan buttons:** compact outline icons based on ControlCenter3's (File, Image, OCR, E-mail, Copy), each
  in its own colour. Hovering a button shows ▶ to scan at once. **+ Custom** adds your own button. It starts
  with the chosen button's settings, and you can rename it, give it an icon and delete it.
- **Main area:** toolbar, scanner glass with live preview, and page thumbnails, then saved files and
  activity log below a divider, and the status bar.
- **Theme:** Dark by default. Light or "Match Windows" can be chosen in Settings > General.

## Differences from the Python version

- **OCR uses the engine built into Windows,** so Tesseract isn't needed. The languages are the
  Windows OCR language packs that are installed (Settings > Time & language > Language & region).
- **Scan type follows the scanner.** Each button stores "colour / grey / black & white" and maps it to
  the scanner's own mode names, so the same settings work on other scanners.
- **Document feeders are supported.** If the scanner has an ADF source, a button can use it. One
  press then scans every page in the feeder.
- **Nothing is tied to one scanner.** Sizes larger than the scanner's glass are hidden, and on first
  start without settings the app asks for the server.
- **Start with Windows** uses the package's startup task when installed as MSIX. Users can also switch
  it in Settings > Apps > Startup. Run unpackaged, it uses the registry Run key.

## Microsoft Store packaging

```powershell
cd packaging
# Test package for this PC
.\make-dev-cert.ps1          # once; then trust the .cer as it explains (admin)
.\build-msix.ps1 -Sign       # artifacts\ScanCenter_1.0.0.0_x64.msix
# Store upload (values from Partner Center > Product management > Product identity)
.\build-msix.ps1 -IdentityName "<Package/Identity/Name>" -Publisher "<Package/Identity/Publisher>" `
                 -PublisherDisplayName "<your publisher name>" -Version 1.0.0.0
```

The package is self-contained, since the Store doesn't install the .NET desktop runtime for MSIX
desktop apps. It declares `runFullTrust`, which is standard for desktop apps in the Store: Partner
Center asks for a one-line reason, for example "Desktop app that saves scans to user-chosen folders
and prints." It also declares `privateNetworkClientServer` for talking to saned on the local network.

Before submitting:

- Run the Windows App Certification Kit on the package.
- Add a privacy policy URL in Partner Center. The app makes network connections, but only to the
  scanner server the user enters, and it collects nothing.
- Avoid other companies' trademarks (SaneTwain, Brother, ControlCenter) in the Store name and listing.
- Store listing screenshots and `packaging/Assets/StoreListing-512.png` are yours to replace.
- `packaging/make_assets.py` redraws all logos and the app icon.
