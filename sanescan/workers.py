"""Background jobs (scanner I/O, saving) that report to the UI through Qt signals."""
from __future__ import annotations

import time
import traceback
from dataclasses import dataclass, field
from typing import Optional

from PIL import Image
from PySide6.QtCore import QThread, Signal
from PySide6.QtGui import QImage

from . import sane_net as sn
from .config import DOC_SIZES, Profile
from .saving import SaveError, save_pages, mail_files


def friendly_error(e: Exception, host: str, port: int) -> str:
    if isinstance(e, (ConnectionRefusedError,)):
        return f"saned at {host}:{port} refused the connection. Check that saned is running on the server."
    if isinstance(e, (TimeoutError,)) or "timed out" in str(e):
        return f"No answer from {host}:{port}. Check that the server is on and port {port} is open."
    if isinstance(e, OSError) and not isinstance(e, sn.SaneError):
        return f"Couldn't reach {host}:{port} ({e.strerror or e}). Check the address in App settings."
    if isinstance(e, sn.SaneError):
        if e.status == sn.STATUS_CANCELLED:
            return "Scan cancelled."
        if e.status == 3:
            return "The scanner is busy. If another program is scanning, wait for it to finish and try again."
        if e.status == 8:
            return "The scanner cover is open. Close it and try again."
        if e.status == 11:
            return "saned refused access. Check the user name in App settings and the server's saned.conf."
        return f"The scanner reported an error: {e}"
    return str(e) or e.__class__.__name__


@dataclass
class DeviceInfo:
    devices: list = field(default_factory=list)
    modes: list = field(default_factory=list)
    resolutions: list = field(default_factory=list)
    max_w: float = 215.9
    max_h: float = 297.0
    # mode -> (brightness available, contrast available)
    adjustable: dict = field(default_factory=dict)
    server_version: str = ""


class ProbeJob(QThread):
    """Connect, list scanners and read the selected scanner's capabilities."""
    done = Signal(object)          # DeviceInfo
    failed = Signal(str)
    log = Signal(str)

    def __init__(self, host, port, user, device, list_only=False):
        super().__init__()
        self.host, self.port, self.user, self.device, self.list_only = host, port, user, device, list_only

    def run(self):
        info = DeviceInfo()
        try:
            with sn.SaneClient(self.host, self.port, self.user) as c:
                v = c.server_version
                info.server_version = f"{v >> 24}.{(v >> 16) & 0xff}.{v & 0xffff}"
                info.devices = c.get_devices()
                if self.list_only or not info.devices:
                    self.done.emit(info)
                    return
                dev = self.device if any(d.name == self.device for d in info.devices) else info.devices[0].name
                c.open(dev)
                o = c.by_name
                if "mode" in o:
                    info.modes = o["mode"].choices()
                if "resolution" in o:
                    ch = o["resolution"].choices()
                    if not ch and o["resolution"].range():
                        lo, hi, q = o["resolution"].range()
                        ch = [r for r in (50, 75, 100, 150, 200, 300, 400, 600, 1200) if lo <= r <= hi]
                    info.resolutions = [int(r) for r in ch]
                if "br-x" in o and o["br-x"].range():
                    info.max_w = o["br-x"].range()[1]
                if "br-y" in o and o["br-y"].range():
                    info.max_h = o["br-y"].range()[1]
                current = c.get("mode") if "mode" in o else None
                for m in info.modes:
                    c.set("mode", m)
                    b, k = c.by_name.get("brightness"), c.by_name.get("contrast")
                    info.adjustable[m] = (bool(b and b.active), bool(k and k.active))
                if current:
                    c.set("mode", current)
                self.log.emit(f"Read capabilities of {dev}: {len(info.modes)} scan types, "
                              f"{len(info.resolutions)} resolutions")
            self.done.emit(info)
        except Exception as e:  # noqa: BLE001
            self.failed.emit(friendly_error(e, self.host, self.port))


def _to_pil(params: sn.Parameters, data: bytes, lines: Optional[int] = None) -> Image.Image:
    bpl, w = params.bytes_per_line, params.pixels_per_line
    h = lines if lines is not None else len(data) // bpl
    data = data[: bpl * h]
    if params.depth == 1:
        mode, raw = "1", "1;I"  # SANE: 1 = black; PIL: 1 = white
    elif params.format == sn.FRAME_RGB:
        mode, raw = "RGB", "RGB"
        if params.depth == 16:
            data = data[0::2]  # keep high byte (big endian after swap)
    else:
        mode, raw = "L", "L"
        if params.depth == 16:
            data = data[0::2]
    if params.depth == 16:
        bpl //= 2
    return Image.frombuffer(mode, (w, h), data, "raw", raw, bpl, 1).copy()


def pil_to_qimage(im: Image.Image) -> QImage:
    if im.mode == "1":
        im = im.convert("L")
    if im.mode == "L":
        fmt, bpp = QImage.Format_Grayscale8, 1
    else:
        im = im.convert("RGB")
        fmt, bpp = QImage.Format_RGB888, 3
    data = im.tobytes()
    q = QImage(data, im.width, im.height, im.width * bpp, fmt)
    return q.copy()


class ScanJob(QThread):
    """Scan one page with a profile's settings."""
    progress = Signal(int, int)        # bytes so far, total bytes (0 = unknown)
    partial = Signal(QImage, float)    # downscaled image so far, fraction of page height
    log = Signal(str)
    page = Signal(object, int)         # PIL image, dpi
    failed = Signal(str)

    def __init__(self, settings, profile: Profile, preview=False, min_res=100):
        super().__init__()
        self.s, self.p, self.preview, self.min_res = settings, profile, preview, min_res
        self._cancel = False

    def cancel(self):
        self._cancel = True

    def run(self):
        s, p = self.s, self.p
        try:
            t0 = time.time()
            self.log.emit(f"Connecting to saned at {s.host}:{s.port}")
            with sn.SaneClient(s.host, s.port, s.user, timeout=10) as c:
                dev = s.device
                if not dev:
                    devs = c.get_devices()
                    if not devs:
                        raise RuntimeError("saned is running but reports no scanners. Check that the "
                                           "scanner is switched on and connected to the server.")
                    dev = devs[0].name
                c.open(dev)
                self.log.emit(f"Opened {dev}")
                if self.preview:
                    res = self.min_res if s.preview_use_min else max(s.preview_res, self.min_res)
                else:
                    res = p.resolution
                mode = c.set("mode", p.mode) or p.mode
                got = c.set("resolution", res)
                dpi = int(got or res)
                c.set("source", "FlatBed")
                w, h = DOC_SIZES.get(p.size, DOC_SIZES["A4"])
                c.set("tl-x", 0.0); c.set("tl-y", 0.0)
                c.set("br-x", w); c.set("br-y", h)
                applied = [f"mode={mode}", f"resolution={dpi}", f"area={w}x{h} mm"]
                for name, val in (("brightness", p.brightness), ("contrast", p.contrast)):
                    opt = c.by_name.get(name)
                    if opt and opt.active:
                        c.set(name, val)
                        applied.append(f"{name}={val}")
                    elif val:
                        self.log.emit(f"{name.capitalize()} isn't available in {mode}, so it was skipped")
                self.log.emit("Settings: " + ", ".join(applied))

                params0 = c.get_parameters()
                total = params0.bytes_per_line * params0.lines if params0.lines > 0 else 0
                last = [0.0]
                buf_ref = {}

                def on_data(chunk, n, params):
                    now = time.time()
                    self.progress.emit(n, total)
                    if now - last[0] > 0.25 and params.bytes_per_line:
                        last[0] = now
                        self._emit_partial(buf_ref, chunk, n, params)

                buf_ref["buf"] = bytearray()
                self.log.emit("Scanning…")
                params, data = c.acquire(on_data=self._accumulate(buf_ref, on_data),
                                         should_cancel=lambda: self._cancel)
            im = _to_pil(params, data)
            self.progress.emit(len(data), len(data))
            self.partial.emit(pil_to_qimage(_thumb(im)), 1.0)
            self.log.emit(f"Page received: {im.width} x {im.height} px at {dpi} dpi, "
                          f"{len(data) / 1048576:.1f} MB in {time.time() - t0:.0f} s")
            self.page.emit(im, dpi)
        except Exception as e:  # noqa: BLE001
            if not isinstance(e, (sn.SaneError, OSError, RuntimeError)):
                traceback.print_exc()
            self.failed.emit(friendly_error(e, s.host, s.port))

    def _accumulate(self, ref, cb):
        def f(chunk, n, params):
            ref["buf"] += chunk
            cb(chunk, n, params)
        return f

    def _emit_partial(self, ref, chunk, n, params):
        buf = ref["buf"]
        lines = len(buf) // params.bytes_per_line
        if lines < 2:
            return
        try:
            im = _to_pil(params, bytes(buf[: lines * params.bytes_per_line]), lines)
            frac = lines / params.lines if params.lines > 0 else 0.0
            self.partial.emit(pil_to_qimage(_thumb(im)), frac)
        except Exception:  # noqa: BLE001
            pass


def _thumb(im: Image.Image, width: int = 900) -> Image.Image:
    if im.mode == "1":
        im = im.convert("L")
    if im.width > width:
        f = max(1, im.width // width)
        im = im.reduce(f)
    return im


class SaveJob(QThread):
    log = Signal(str)
    saved = Signal(list)       # list[Path]
    failed = Signal(str)

    def __init__(self, pages, profile: Profile, dpi: int, settings):
        super().__init__()
        self.pages, self.p, self.dpi, self.s = pages, profile, dpi, settings

    def run(self):
        try:
            paths = save_pages(self.pages, self.p, dpi=self.dpi, jpeg_quality=self.s.jpeg_quality,
                               tesseract_cfg=self.s.tesseract, ocr_lang=self.s.ocr_lang,
                               log=self.log.emit)
            self.saved.emit(paths)
        except SaveError as e:
            self.failed.emit(str(e))
        except OSError as e:
            self.failed.emit(f"Couldn't save the file: {e.strerror or e}")
        except Exception as e:  # noqa: BLE001
            traceback.print_exc()
            self.failed.emit(f"Couldn't save the file: {e}")


class MailJob(QThread):
    result = Signal(bool)

    def __init__(self, paths):
        super().__init__()
        self.paths = paths

    def run(self):
        self.result.emit(mail_files(self.paths))
