# SaneTwain Scan Center: user guide

This guide covers installing the app, connecting it to your scanner, scanning with the buttons,
and the app-wide settings. The last sections are for building and publishing the app.

- [Install](#install)
- [First start: connect to your scanner](#first-start-connect-to-your-scanner)
- [The window](#the-window)
- [Scanning](#scanning)
- [Scan buttons and their settings](#scan-buttons-and-their-settings)
- [Your own scan buttons](#your-own-scan-buttons)
- [After the scan: saving, printing, e-mail](#after-the-scan-saving-printing-e-mail)
- [App settings](#app-settings)
- [Notification area and start with Windows](#notification-area-and-start-with-windows)
- [Sharing settings with SaneTwain](#sharing-settings-with-sanetwain)
- [Keyboard shortcuts](#keyboard-shortcuts)
- [Troubleshooting](#troubleshooting)
- [Building and publishing](#building-and-publishing)

---

## Install

Pick one of these.

**A. Developer install (no certificate needed).** Needs Developer Mode: Windows Settings > System >
For developers > Developer Mode.

```powershell
cd csharp\packaging
.\build-msix.ps1                                   # builds artifacts\layout-x64 and the .msix
$dest = "$env:LOCALAPPDATA\SaneTwainScanCenter\app"
Remove-Item $dest -Recurse -Force -ErrorAction SilentlyContinue
Copy-Item ..\artifacts\layout-x64 $dest -Recurse
Add-AppxPackage -Register "$dest\AppxManifest.xml" -ForceApplicationShutdown
```

The app then appears in Start as **SaneTwain Scan Center**. To update it, run the same commands again.

**B. Signed package.** This is how other PCs install it.

```powershell
cd csharp\packaging
.\make-dev-cert.ps1        # once: creates dev-cert.pfx / dev-cert.cer
# once, in an administrator PowerShell, trust the certificate:
Import-Certificate -FilePath .\dev-cert.cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople
.\build-msix.ps1 -Sign
```

Then double-click `csharp\artifacts\ScanCenter_<version>_x64.msix`.

**C. Without installing.** Run `dotnet run --project csharp\src\ScanCenter.App`.

**Uninstall:** Windows Settings > Apps > Installed apps > SaneTwain Scan Center > Uninstall. Or in PowerShell:
`Get-AppxPackage ScanCenter.Dev | Remove-AppxPackage`.

## First start: connect to your scanner

If `%USERPROFILE%\sanetwain.ini` exists, the app copies the server, port, user and scanner from it, and
connects straight away. Otherwise:

1. Click **Settings** (top right) > **Connection**.
2. Enter the **saned server**: the name or IP address of the computer the scanner is attached to.
   The **Port** is normally 6566.
3. Enter a **User name** if your server needs one.
4. Click **Find scanners**, pick yours, then **Save settings**.

The title bar then shows the scanner and a green dot. A red dot means the server didn't answer; see
[Troubleshooting](#troubleshooting).

## The window

| Area | What it's for |
|---|---|
| **Title bar** | App name, scanner and connection status, **Settings**, and the minimize, maximize and close buttons. Drag it to move the window; double-click it to maximize. Hover maximize for Windows 11 snap layouts. |
| **Left sidebar** | Settings of the chosen scan button, in three groups: **Destination**, **Scan quality**, **When the scan ends**. |
| **Scan buttons** | One button per job. Click to choose; hover and press **▶** to scan at once. **+ Custom** adds your own. |
| **Toolbar** | The big **Scan** button for the chosen button, and **Preview**. |
| **Scanner glass** | Shows the page line by line as it's scanned, and the scanned pages underneath. |
| **Saved files / Activity** | Every file saved this session (double-click to open, right-click for more), and a log of what the app did. |
| **Status bar** | What's happening now, progress, and the current resolution and scan type. |

## Scanning

1. Put the document face down on the glass, top edge against the corner arrow.
2. Choose a scan button, for example **File**.
3. Press **Scan** (or **▶** on the button, or **Ctrl+S**).
4. If the button asks for more pages, either:
   - place the next page and press **Scan next page**, or
   - press **Finish and save** to write the file now.

   **Discard pages** throws the scanned pages away. Right-click a page thumbnail to remove only that page.
5. What happens next depends on the button's **Write file to disk** setting:
   - **On:** the file is saved straight into the button's folder as soon as the scan ends.
   - **Off** (the default): nothing is written yet. The pages wait in the tray and the bar shows
     **Save file**. Press it to write the file, or scan more pages, or discard them.

   Once saved, the file appears under **Saved files**, and then the button's after-save action runs.

**Preview** (F5) does a quick low-resolution pass so you can check the page's position. Nothing is saved.

**Document feeder:** if your scanner has one, the **Source** setting appears. With the feeder chosen,
one scan takes every page in the feeder.

**Cancel** stops a running scan (or press **Esc**). Pages already scanned are kept.

## Scan buttons and their settings

The built-in buttons are **File**, **Image**, **OCR**, **E-mail** and **Copy**. Every setting in the left sidebar
belongs to the chosen button and is saved automatically.

**Destination**
- **Folder:** where files go. Click **Change…** to pick one. The folder icon opens it.
  **Create the folder if it's missing** creates it on the first save.
- **File name:** a pattern with tokens. Click a token to insert it:
  - `{profile}`: the button's name, e.g. `File`
  - `{date}`: `2026-11-04`
  - `{time}`: `153012`
  - `{n}`: a counter, `001`, `002`, … It counts up to the first number not used yet in the folder.

  If a name is already taken and there's no `{n}`, the app adds ` (2)`, ` (3)`… so nothing is overwritten.
  The line under the pattern shows the next file name.
- **File type:**
  - **PDF:** all pages in one file.
  - **Searchable PDF (OCR):** a PDF with an invisible text layer, so you can search, select and copy the text.
  - **TIFF:** all pages in one file.
  - **JPEG** or **PNG:** one file per page.

**Scan quality**
- **Resolution:** 300 dpi suits documents, 600 dpi or more suits photos. Values above 1200 dpi are
  interpolated by the scanner.
- **Document size:** sizes bigger than your scanner's glass aren't offered. **Full glass** scans everything.
- **Scan type:** the scanner's own modes. For the DCP-150C these are *Black & White*, *Gray[Error Diffusion]*,
  *True Gray* and *24bit Color*.
- **Brightness / Contrast:** greyed out in scan types where the scanner sets them itself.
- The note at the bottom estimates how much data each page is.

**When the scan ends**
- **Write file to disk:** set separately for each button, and off by default. When on, the file is saved
  automatically as soon as the scan ends. When off, the pages wait until you press **Save file**. It doesn't
  apply to a button that only prints (*Print it* without *Also save a file*).
- **Ask for another page after each scan:** for documents with several pages.
- **Then:**
  - **Do nothing else**
  - **Show the file in its folder**
  - **Open the file**
  - **Attach it to a new e-mail**
  - **Print it**
- **Also save a file:** shown with *Print it*. Turn it off to make a plain copy that only goes to the printer.

**Reset this button to defaults** puts a built-in button back to how it started.

## Your own scan buttons

1. Choose the button that's closest to what you want, then click **+ Custom**.
2. The new button starts with that button's settings. It saves into a folder named after it, inside your
   default scans folder.
3. In the sidebar, set its **Name** and **Icon**. The name is also what `{profile}` puts in file names.
4. Change any other settings as usual.

Your buttons also appear in the notification-area menu. **Delete this button** removes a button; files it
already saved stay where they are.

## After the scan: saving, printing, e-mail

- **Printing** uses the printer chosen in **Settings > Printer**, or the Windows default printer. Pages
  print at their real size, scaled down only if they don't fit the paper.
  - **Show printer options before printing** asks once per document.
  - **Wait until all pages are scanned** prints the whole document at the end; otherwise each page
    prints as soon as it's scanned.
- **E-mail** opens a new message in your default mail program with the file attached (Outlook,
  Thunderbird and other programs that support Simple MAPI). If none answers, the file is shown in its
  folder instead.
- **Searchable PDF** uses the text recognition built into Windows. Choose its language in
  **Settings > Saving**.
- **Adding OCR languages:** in **Settings > Saving**, click **Manage languages…**. Windows offers 35 languages,
  among them English, French, German, Italian, Spanish, Portuguese and Romanian. Tick the ones you need and
  click **Install**. Windows asks for administrator permission once and downloads them from Windows Update,
  which takes a few minutes each. Untick an installed language to remove it. The languages belong to Windows,
  not to the app, so they can't be built into it.
  - If installing fails and the window says Windows is waiting for a restart, restart the PC and try again.
    Windows won't add components while an earlier update is waiting to finish.

## App settings

Click **Settings** in the title bar.

| Tab | Settings |
|---|---|
| **Connection** | saned server and port, user name, scanner (**Find scanners**), look for scanners at start, **Test connection** |
| **Preview** | preview resolution or "always the scanner's lowest", run a preview when the app opens |
| **Saving** | default folder for scans, JPEG quality, OCR language, sound and notification when a file is saved |
| **General** | start with Windows, keep the window on top, ignore Esc, hide unsupported settings, share settings with SaneTwain, **Theme** (Dark is the default; Light; Match Windows) |
| **Printer** | printer, show printer options, wait until all pages are scanned |

The app's settings are stored in `%LOCALAPPDATA%\ScanCenter\settings.json`. When installed from a package,
Windows keeps this file inside the package's own data folder.

## Notification area and start with Windows

The icon next to the clock shows the scanner's state:

- no dot: ready
- **amber dot:** scanning
- **red dot:** offline

Click it to open the window. Right-click it for every scan button and **Quit**.

With **Settings > General > Start with Windows** on:

- The app starts when you sign in and waits in the notification area.
- Closing the window keeps it running there. Use **Quit** in the icon's menu to stop it.
- You can also switch it in Windows Settings > Apps > Startup.

Only one copy runs at a time. Starting the app again brings its window forward.

## Sharing settings with SaneTwain

If `%USERPROFILE%\sanetwain.ini` exists, the app copies the connection, preview, JPEG quality and printer
settings from it on first start. While **Settings > General > Share connection settings with SaneTwain** is on,
the app writes changes to those settings back into it, so SaneTwain stays in sync:

- It changes only those keys, in place.
- Before its first change, it saves the original as `sanetwain.ini.before-scancenter`.

## Keyboard shortcuts

| Keys | Action |
|---|---|
| Ctrl+S | Scan with the chosen button |
| F5 | Preview |
| Esc | Cancel the scan (unless turned off in Settings > General) |
| Enter | In a custom button's name box: apply the new name |
| Win+Z / hover maximize | Snap layouts |

## Troubleshooting

| Message or problem | What to do |
|---|---|
| **Scanner not reachable / no answer** | Check that the server is on and `saned` is running (`sudo systemctl status saned.socket`). Check that port 6566 isn't blocked by a firewall. Use **Settings > Connection > Test connection**. |
| **Refused the connection** | `saned` isn't listening. On the server, enable `saned.socket`, or add your PC's network to `/etc/sane.d/saned.conf`. |
| **saned refused access** | Add your PC's address or network to `/etc/sane.d/saned.conf` on the server. Check the user name. |
| **No scanner found** | The server sees no scanner. Check it's on and its USB cable is connected; on the server, `scanimage -L` should list it. |
| **The scanner is busy** | Another program is scanning. Wait, or close SaneTwain or other scan programs. |
| **The file wasn't saved** | The message says why, e.g. a read-only folder or a missing folder with "create" turned off. Fix it, then press **Save again**; the pages are kept. |
| **No text in a searchable PDF** | Install the document's language with **Settings > Saving > Manage languages…** and choose it as the OCR language. Use 300 dpi and *True Gray* for best results. |
| **Installing an OCR language fails (0x8024001e)** | Windows is waiting for a restart from an earlier update. Restart the PC, then try again. |
| **Nothing was printed** | Choose an installed printer in **Settings > Printer**. |
| **E-mail doesn't open** | Set a default mail program in Windows that supports Simple MAPI (Outlook classic, Thunderbird). |

The **Activity** tab lists every step: the settings sent to the scanner, data received, files written.
Include it when reporting a problem.

## Building and publishing

```powershell
cd csharp
dotnet build ScanCenter.slnx                     # build everything
dotnet run --project src\ScanCenter.App           # run
tools\ScanCenter.Smoke\bin\Debug\net10.0-windows10.0.19041.0\ScanCenter.Smoke.exe <server> <folder>
                                                  # scanner check without the UI
```

- **Icons:** `python csharp\packaging\make_assets.py` redraws the app icon and all Store logos
  (concept B, "Electric frame"). The tray icon is drawn in code (`TrayIcon.cs`) so it can follow the
  taskbar colour and show the status dot.
- **Microsoft Store:** reserve the name in Partner Center, then build with the identity it gives you.
  The Store signs the package.

  ```powershell
  .\build-msix.ps1 -IdentityName "<Package/Identity/Name>" -Publisher "<Package/Identity/Publisher>" `
                   -PublisherDisplayName "<publisher name>" -DisplayName "<Store name>" -Version 1.0.0.0
  ```

  Partner Center will also ask for a privacy policy URL and a reason for the `runFullTrust` capability.
  "SaneTwain" is another author's product name; use a name of your own for the public Store listing (`-DisplayName`).
  Details: [csharp/README.md](../csharp/README.md).

For testing, `SCANCENTER_HOME` and `SCANCENTER_SANETWAIN_INI` point the settings file and `sanetwain.ini`
at other locations, so tests don't touch your own settings.
