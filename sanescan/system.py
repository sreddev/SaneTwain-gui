"""Windows integration: start with Windows, printers and printing."""
from __future__ import annotations

import sys
from pathlib import Path

from PIL import Image
from PySide6.QtCore import QRectF
from PySide6.QtPrintSupport import QPrintDialog, QPrinter, QPrinterInfo

RUN_KEY = r"Software\Microsoft\Windows\CurrentVersion\Run"
RUN_NAME = "SaneTwain Scan Center"
LAUNCHER = Path(__file__).resolve().parent.parent / "scancenter.pyw"


# ---------------------------------------------------------------- start with Windows
def _pythonw() -> str:
    exe = Path(sys.executable)
    w = exe.with_name("pythonw.exe")
    return str(w if w.exists() else exe)


def startup_command() -> str:
    return f'"{_pythonw()}" "{LAUNCHER}" --tray'


def startup_enabled() -> bool:
    try:
        import winreg
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, RUN_KEY) as k:
            winreg.QueryValueEx(k, RUN_NAME)
            return True
    except OSError:
        return False


def set_startup(enabled: bool):
    import winreg
    with winreg.OpenKey(winreg.HKEY_CURRENT_USER, RUN_KEY, 0, winreg.KEY_SET_VALUE) as k:
        if enabled:
            winreg.SetValueEx(k, RUN_NAME, 0, winreg.REG_SZ, startup_command())
        else:
            try:
                winreg.DeleteValue(k, RUN_NAME)
            except FileNotFoundError:
                pass


# ---------------------------------------------------------------- printing
def printer_names() -> list[str]:
    return [p.printerName() for p in QPrinterInfo.availablePrinters()]


def default_printer_name() -> str:
    return QPrinterInfo.defaultPrinterName()


def make_printer(name: str) -> tuple[QPrinter | None, str]:
    """Return (printer, problem). An empty name means the Windows default printer."""
    info = None
    if name:
        info = next((p for p in QPrinterInfo.availablePrinters() if p.printerName() == name), None)
        if info is None:
            return None, f"The printer \"{name}\" isn't installed on this PC. Choose another one in App settings > Printer."
    else:
        info = QPrinterInfo.defaultPrinter()
        if info.isNull():
            return None, "Windows has no default printer. Choose a printer in App settings > Printer."
    pr = QPrinter(info, QPrinter.HighResolution)
    pr.setFullPage(False)
    return pr, ""


def ask_options(printer: QPrinter, parent) -> bool:
    dlg = QPrintDialog(printer, parent)
    dlg.setWindowTitle("Print scanned pages")
    return dlg.exec() == QPrintDialog.Accepted


def print_images(printer: QPrinter, pages: list[Image.Image], dpi: int, title: str) -> str:
    """Print pages at their real size (scaled down only if larger than the paper).

    Returns an empty string on success, otherwise a message.
    """
    from PySide6.QtGui import QPainter
    from .workers import pil_to_qimage

    printer.setDocName(title)
    if all(im.mode in ("1", "L") for im in pages):
        printer.setColorMode(QPrinter.GrayScale)
    painter = QPainter()
    if not painter.begin(printer):
        return f"Couldn't start printing on {printer.printerName()}. Check that the printer is on and connected."
    try:
        res = printer.resolution()
        area = printer.pageLayout().paintRectPixels(res)
        for i, im in enumerate(pages):
            if i and not printer.newPage():
                return "The printer stopped accepting pages."
            q = pil_to_qimage(im)
            w = im.width / dpi * res
            h = im.height / dpi * res
            k = min(1.0, area.width() / w, area.height() / h)
            painter.drawImage(QRectF(0, 0, w * k, h * k), q)
    finally:
        painter.end()
    return ""
