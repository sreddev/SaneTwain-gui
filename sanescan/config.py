"""Settings storage.

App-wide settings live in the same sanetwain.ini that SaneTwain uses, so both
programs share the connection details. Keys SaneTwain already understands are
read and written in its own sections; settings that only Scan Center uses go
in [ScanCenter] and [ScanCenter.<Profile>] sections, which SaneTwain ignores.
"""
from __future__ import annotations

import configparser
import os
import shutil
from dataclasses import dataclass, field, asdict, fields
from pathlib import Path

INI_PATH = Path(os.environ.get("USERPROFILE", str(Path.home()))) / "sanetwain.ini"

FILE_TYPES = {
    "pdf": ("PDF, all pages in one file", "pdf", True),
    "pdfocr": ("Searchable PDF (OCR)", "pdf", True),
    "tif": ("TIFF, all pages in one file", "tif", True),
    "jpg": ("JPEG, one file per page", "jpg", False),
    "png": ("PNG, one file per page", "png", False),
}

# (width mm, height mm). The DCP-150C glass is 215.9 x 297 mm.
DOC_SIZES = {
    "A4": (210.0, 297.0),
    "Letter": (215.9, 279.4),
    "A5": (148.0, 210.0),
    "A6": (105.0, 148.0),
    "Business card": (90.0, 55.0),
    "Photo 10 x 15 cm": (100.0, 150.0),
    "Full glass": (215.9, 297.0),
}

AFTER_SAVE = {
    "none": "Do nothing else",
    "folder": "Show the file in its folder",
    "open": "Open the file",
    "mail": "Attach it to a new e-mail",
    "print": "Print it",
}

DEFAULT_MODES = ["Black & White", "Gray[Error Diffusion]", "True Gray", "24bit Color"]
DEFAULT_RESOLUTIONS = [100, 150, 200, 300, 400, 600, 1200]


@dataclass
class Profile:
    key: str
    label: str
    tag: str
    folder: str
    pattern: str = "{profile}_{date}_{time}"
    type: str = "pdf"
    resolution: int = 300
    mode: str = "True Gray"
    size: str = "A4"
    brightness: int = 0
    contrast: int = 0
    multi: bool = True
    after: str = "folder"
    create: bool = True
    save_file: bool = True   # only used with after="print": False makes a plain copy


def default_profiles(root: str) -> list[Profile]:
    return [
        Profile("file", "Scan to File", "File", str(Path(root) / "Documents"),
                "{profile}_{date}_{time}", "pdf", 300, "True Gray", "A4", 0, 0, True, "folder"),
        Profile("image", "Scan to Image", "Image", str(Path(root) / "Images"),
                "Photo_{date}_{n}", "jpg", 600, "24bit Color", "A4", 0, 0, False, "open"),
        Profile("ocr", "Scan to OCR", "OCR", str(Path(root) / "OCR"),
                "OCR_{date}_{n}", "pdfocr", 300, "True Gray", "A4", 0, 10, True, "open"),
        Profile("email", "Scan to E-mail", "Mail", str(Path(root) / "Mail"),
                "Mail_{date}_{time}", "pdf", 150, "24bit Color", "A4", 0, 0, True, "mail"),
        Profile("copy", "Copy", "Copy", str(Path(root) / "Copies"),
                "Copy_{date}_{time}", "pdf", 300, "24bit Color", "A4", 0, 0, False, "print",
                save_file=False),
    ]


@dataclass
class AppSettings:
    # [Connection] (shared with SaneTwain)
    host: str = "192.168.254.1"
    port: int = 6566
    user: str = ""
    device: str = ""
    get_devices: bool = True
    # [Preview]
    preview_res: int = 100
    preview_use_min: bool = True
    preview_autostart: bool = False
    # [General]
    topmost: bool = False
    disable_esc: bool = False
    hide_disabled: bool = False
    # [Save]
    jpeg_quality: int = 90
    # [Printer]
    printer: str = ""          # empty = Windows default printer
    print_setup: bool = True
    print_delayed: bool = False
    # [ScanCenter]
    start_with_windows: bool = False
    default_folder: str = r"D:\Scans"
    ocr_lang: str = "eng"
    tesseract: str = ""
    sound: bool = False
    notify: bool = True
    theme: str = "system"
    geometry: str = ""
    last_profile: str = "file"
    profiles: list = field(default_factory=list)


# (attribute, section, key, kind)
_MAP = [
    ("host", "Connection", "Host", str), ("port", "Connection", "Port", int),
    ("user", "Connection", "User", str), ("device", "Connection", "Device", str),
    ("get_devices", "Connection", "GetDevices", bool),
    ("preview_res", "Preview", "Resolution", int), ("preview_use_min", "Preview", "UseMinimum", bool),
    ("preview_autostart", "Preview", "AutoStart", bool),
    ("topmost", "General", "TopMost", bool), ("disable_esc", "General", "DisableEsc", bool),
    ("hide_disabled", "General", "HideDisabled", bool),
    ("jpeg_quality", "Save", "JPEGQuality", int),
    ("printer", "Printer", "Default", str), ("print_setup", "Printer", "Setup", bool),
    ("print_delayed", "Printer", "Delayed", bool),
    ("start_with_windows", "ScanCenter", "StartWithWindows", bool),
    ("default_folder", "ScanCenter", "DefaultFolder", str), ("ocr_lang", "ScanCenter", "OCRLanguage", str),
    ("tesseract", "ScanCenter", "Tesseract", str), ("sound", "ScanCenter", "Sound", bool),
    ("notify", "ScanCenter", "Notify", bool), ("theme", "ScanCenter", "Theme", str),
    ("geometry", "ScanCenter", "Geometry", str), ("last_profile", "ScanCenter", "LastProfile", str),
]

_PROFILE_KINDS = {f.name: f.type for f in fields(Profile)}


def _parser() -> configparser.ConfigParser:
    cp = configparser.ConfigParser(interpolation=None, strict=False)
    cp.optionxform = str  # keep SaneTwain's key casing
    return cp


def _conv(kind, raw: str):
    if kind is bool or kind == "bool":
        return raw.strip() not in ("0", "", "false", "False", "no")
    if kind is int or kind == "int":
        return int(float(raw.strip()))
    return raw


def load(path: Path | None = None) -> AppSettings:
    path = path or INI_PATH
    s = AppSettings()
    cp = _parser()
    if path.exists():
        cp.read(path, encoding="utf-8-sig")
    for attr, sec, key, kind in _MAP:
        if cp.has_option(sec, key):
            try:
                setattr(s, attr, _conv(kind, cp.get(sec, key)))
            except ValueError:
                pass
    profiles = []
    for p in default_profiles(s.default_folder):
        sec = f"ScanCenter.{p.key}"
        if cp.has_section(sec):
            for name, kind in _PROFILE_KINDS.items():
                if name == "key" or not cp.has_option(sec, name):
                    continue
                try:
                    setattr(p, name, _conv(kind, cp.get(sec, name)))
                except ValueError:
                    pass
        profiles.append(p)
    s.profiles = profiles
    return s


def save(s: AppSettings, path: Path | None = None):
    path = path or INI_PATH
    backup = path.with_name(path.name.replace(".ini", ".ini.before-scancenter"))
    cp = _parser()
    if path.exists():
        cp.read(path, encoding="utf-8-sig")
        if not backup.exists():
            shutil.copy2(path, backup)
    for attr, sec, key, kind in _MAP:
        if not cp.has_section(sec):
            cp.add_section(sec)
        v = getattr(s, attr)
        cp.set(sec, key, ("1" if v else "0") if kind is bool else str(v))
    for p in s.profiles:
        sec = f"ScanCenter.{p.key}"
        if not cp.has_section(sec):
            cp.add_section(sec)
        for name, v in asdict(p).items():
            if name == "key":
                continue
            cp.set(sec, name, ("1" if v else "0") if isinstance(v, bool) else str(v))
    tmp = path.with_suffix(".ini.tmp")
    with open(tmp, "w", encoding="utf-8") as f:
        cp.write(f, space_around_delimiters=False)
    os.replace(tmp, path)
