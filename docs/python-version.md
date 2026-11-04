# SaneTwain Scan Center: Python version (first version)

A modern Windows front end for the Brother DCP-150C (or any scanner) shared by
`saned` on the network. It replaces the SaneTwain window with four
ControlCenter3-style scan buttons, and each one saves straight into its own
folder as soon as the scan ends.

## Start

Double-click **`Scan Center.bat`**. The first run creates `.venv` and installs
the dependencies (PySide6, Pillow, pypdf). After that it opens straight away.

From a terminal: `.venv\Scripts\python -m sanescan`

## What it does

- **Scan to File / Image / OCR / E-mail.** Each button has its own folder, file name
  pattern, file type, resolution, document size, scan type, brightness and contrast.
- **Saves when the scan ends.** No Save dialog. Missing folders are created (optional).
  Taken names get ` (2)` added, so nothing is overwritten. Files are written to a
  temporary name first, so a half-written file never appears.
- **File name tokens:** `{profile}` `{date}` (2026-11-04) `{time}` (143205) `{n}` (001, 002…
  counts up to the first free number in the folder).
- **Multi-page documents.** After each page you choose *Scan next page* or *Finish and save*.
  PDF and TIFF hold all pages in one file. JPEG and PNG write one file per page.
  Right-click a page thumbnail to remove it.
- **Searchable PDF (OCR).** Needs [Tesseract](https://github.com/UB-Mannheim/tesseract/wiki).
  Without it, Scan to OCR saves a normal PDF and says so in the Activity log.
- **After saving:** show it in Explorer, open it, or attach it to a new e-mail
  (default mail program via Simple MAPI, e.g. Outlook or Thunderbird).
- **Copy button and printing.** *Copy* scans and prints on the printer chosen in
  App settings > Printer, without saving a file unless *Also save a file* is ticked. Any other
  button can use *Print it* as its after-save action. Pages print at their real size. With
  *Wait until all pages are scanned* off, each page prints as soon as it's scanned.
- **Start with Windows.** App settings > General. Scan Center then starts in the notification
  area, closing the window keeps it running, and right-clicking the icon offers each scan
  button and Quit. Only one copy runs at a time; starting it again brings the window forward.
- **Live preview.** The image appears line by line while the scanner reads it.
- Shortcuts: **Ctrl+S** scans, **F5** previews, **Esc** cancels a running scan.

## Settings

App settings (gear button, top right) are stored in `%USERPROFILE%\sanetwain.ini`,
the same file SaneTwain uses. Connection, preview, JPEG quality and window options
use SaneTwain's own keys, so both programs stay in sync. Scan Center's own settings go
in `[ScanCenter]` and `[ScanCenter.<button>]` sections, which SaneTwain ignores.
Before the first write, the original file is copied to
`sanetwain.ini.before-scancenter`.

## How it works

`sanescan/sane_net.py` is a small pure-Python client for the SANE network protocol
(port 6566). It talks to saned directly, the same way SaneTwain.ds does, so nothing
changes on the server and the TWAIN driver keeps working for other programs.

| File | Role |
|---|---|
| `sanescan/sane_net.py` | SANE network protocol: devices, options, acquisition |
| `sanescan/workers.py` | Background scan, probe, save and mail jobs |
| `sanescan/saving.py` | File naming, folders, PDF/TIFF/JPEG/PNG, OCR, after-save actions |
| `sanescan/config.py` | Reading and writing `sanetwain.ini` |
| `sanescan/main_window.py` | Main window |
| `sanescan/settings_dialog.py` | App settings dialog |
| `sanescan/theme.py` | Light and dark styles |
