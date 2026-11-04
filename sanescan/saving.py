"""Turning scanned pages into files: naming, folders, formats, OCR, after-save actions."""
from __future__ import annotations

import ctypes
import os
import re
import shutil
import subprocess
import tempfile
from datetime import datetime
from pathlib import Path
from typing import Callable, Optional

from PIL import Image

from .config import FILE_TYPES, Profile

INVALID = re.compile(r'[\\/:*?"<>|]')


class SaveError(Exception):
    pass


def render_name(pattern: str, profile: Profile, n: int, when: Optional[datetime] = None) -> str:
    when = when or datetime.now()
    name = (pattern.replace("{profile}", profile.tag)
            .replace("{date}", when.strftime("%Y-%m-%d"))
            .replace("{time}", when.strftime("%H%M%S"))
            .replace("{n}", f"{n:03d}"))
    name = INVALID.sub("-", name).strip().rstrip(".")
    return name or "Scan"


def choose_path(folder: Path, profile: Profile, ext: str, when: datetime,
                reserved: set[Path]) -> tuple[Path, Optional[str]]:
    """Pick a file name in folder that doesn't exist yet.

    With {n} in the pattern, the number counts up until a free name is found.
    Without it, " (2)", " (3)"... is added. Returns (path, note about renaming).
    """
    def free(p: Path) -> bool:
        return not p.exists() and p not in reserved

    if "{n}" in profile.pattern:
        for n in range(1, 100000):
            p = folder / f"{render_name(profile.pattern, profile, n, when)}.{ext}"
            if free(p):
                return p, None
    base = render_name(profile.pattern, profile, 1, when)
    p = folder / f"{base}.{ext}"
    if free(p):
        return p, None
    k = 2
    while not free(folder / f"{base} ({k}).{ext}"):
        k += 1
    q = folder / f"{base} ({k}).{ext}"
    return q, f"{p.name} already exists, saved as {q.name}"


def find_tesseract(configured: str = "") -> Optional[str]:
    cands = [configured] if configured else []
    cands += [shutil.which("tesseract") or "",
              r"C:\Program Files\Tesseract-OCR\tesseract.exe",
              r"C:\Program Files (x86)\Tesseract-OCR\tesseract.exe",
              os.path.expandvars(r"%LOCALAPPDATA%\Programs\Tesseract-OCR\tesseract.exe")]
    for c in cands:
        if c and Path(c).is_file():
            return c
    return None


def _no_window():
    return subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0


def _ocr_pdf(pages: list[Image.Image], dpi: int, out: Path, tesseract: str, lang: str,
             log: Callable[[str], None]):
    from pypdf import PdfWriter, PdfReader
    with tempfile.TemporaryDirectory(prefix="scancenter-ocr-") as td:
        writer = PdfWriter()
        for i, im in enumerate(pages, 1):
            log(f"Recognizing text on page {i} of {len(pages)} ({lang})")
            src = Path(td) / f"p{i}.png"
            im.save(src, dpi=(dpi, dpi))
            base = Path(td) / f"p{i}"
            r = subprocess.run([tesseract, str(src), str(base), "-l", lang, "--dpi", str(dpi), "pdf"],
                               capture_output=True, text=True, creationflags=_no_window())
            if r.returncode != 0:
                raise SaveError(f"Tesseract failed on page {i}: {r.stderr.strip()[:300]}")
            for pg in PdfReader(str(base) + ".pdf").pages:
                writer.add_page(pg)
        with open(out, "wb") as f:
            writer.write(f)


def _write(pages: list[Image.Image], path: Path, ftype: str, dpi: int, quality: int,
           tesseract: Optional[str], lang: str, log: Callable[[str], None]):
    for im in pages:  # Pillow keeps options from an earlier save on the image; start clean
        for attr in ("encoderinfo", "encoderconfig"):
            if hasattr(im, attr):
                delattr(im, attr)
    first, rest = pages[0], pages[1:]
    if ftype == "pdfocr":
        _ocr_pdf(pages, dpi, path, tesseract, lang, log)
    elif ftype == "pdf":
        first.save(path, "PDF", save_all=True, append_images=rest, resolution=float(dpi), quality=quality)
    elif ftype == "tif":
        comp = "group4" if first.mode == "1" else "tiff_lzw"
        first.save(path, "TIFF", save_all=True, append_images=rest, compression=comp, dpi=(dpi, dpi))
    elif ftype == "jpg":
        im = first.convert("L") if first.mode == "1" else first
        im.save(path, "JPEG", quality=quality, dpi=(dpi, dpi), optimize=True)
    elif ftype == "png":
        first.save(path, "PNG", dpi=(dpi, dpi), optimize=True)
    else:
        raise SaveError(f"Unknown file type {ftype}")


def save_pages(pages: list[Image.Image], profile: Profile, *, dpi: int, jpeg_quality: int,
               tesseract_cfg: str, ocr_lang: str, log: Callable[[str], None]) -> list[Path]:
    if not pages:
        return []
    folder = Path(os.path.expandvars(profile.folder)).expanduser()
    if not folder.is_dir():
        if not profile.create:
            raise SaveError(f"The folder {folder} doesn't exist. Choose another folder, or turn on "
                            f"\"Create the folder if it's missing\".")
        folder.mkdir(parents=True, exist_ok=True)
        log(f"Created folder {folder}")

    ftype = profile.type
    label, ext, multi = FILE_TYPES.get(ftype, FILE_TYPES["pdf"])
    tesseract = None
    if ftype == "pdfocr":
        tesseract = find_tesseract(tesseract_cfg)
        if not tesseract:
            log("Tesseract OCR isn't installed, so this was saved as a normal PDF without searchable text")
            ftype = "pdf"

    groups = [pages] if multi else [[p] for p in pages]
    when = datetime.now()
    out: list[Path] = []
    reserved: set[Path] = set()
    for g in groups:
        path, note = choose_path(folder, profile, ext, when, reserved)
        reserved.add(path)
        if note:
            log(note)
        tmp = path.with_name(f".~{path.stem}.partial.{ext}")
        try:
            _write(g, tmp, ftype, dpi, jpeg_quality, tesseract, ocr_lang, log)
            os.replace(tmp, path)
        except PermissionError as e:
            raise SaveError(f"Windows refused to write {path}. Check that the folder isn't read-only "
                            f"and the file isn't open in another program. ({e.strerror})")
        finally:
            if tmp.exists():
                try:
                    tmp.unlink()
                except OSError:
                    pass
        out.append(path)
    return out


# ---------- after-save actions ----------

def show_in_folder(path: Path):
    subprocess.Popen(["explorer", "/select,", str(path)], creationflags=_no_window())


def open_file(path: Path):
    os.startfile(str(path))  # type: ignore[attr-defined]


class _MapiFileDesc(ctypes.Structure):
    _fields_ = [("ulReserved", ctypes.c_ulong), ("flFlags", ctypes.c_ulong),
                ("nPosition", ctypes.c_ulong), ("lpszPathName", ctypes.c_char_p),
                ("lpszFileName", ctypes.c_char_p), ("lpFileType", ctypes.c_void_p)]


class _MapiMessage(ctypes.Structure):
    _fields_ = [("ulReserved", ctypes.c_ulong), ("lpszSubject", ctypes.c_char_p),
                ("lpszNoteText", ctypes.c_char_p), ("lpszMessageType", ctypes.c_char_p),
                ("lpszDateReceived", ctypes.c_char_p), ("lpszConversationID", ctypes.c_char_p),
                ("flFlags", ctypes.c_ulong), ("lpOriginator", ctypes.c_void_p),
                ("nRecipCount", ctypes.c_ulong), ("lpRecips", ctypes.c_void_p),
                ("nFileCount", ctypes.c_ulong), ("lpFiles", ctypes.POINTER(_MapiFileDesc))]


def mail_files(paths: list[Path]) -> bool:
    """Open a new message in the default mail program (Simple MAPI) with files attached.

    Blocks until the user sends or closes the message, so call it from a worker thread.
    Returns False if no MAPI mail program is available.
    """
    try:
        mapi = ctypes.WinDLL("mapi32.dll")
    except OSError:
        return False
    enc = "mbcs"
    files = (_MapiFileDesc * len(paths))()
    keep = []
    for i, p in enumerate(paths):
        a, b = str(p).encode(enc, "replace"), p.name.encode(enc, "replace")
        keep += [a, b]
        files[i] = _MapiFileDesc(0, 0, 0xFFFFFFFF, a, b, None)
    subj = (paths[0].stem if len(paths) == 1 else f"{len(paths)} scanned files").encode(enc, "replace")
    msg = _MapiMessage(0, subj, b"", None, None, None, 0, None, 0, None, len(paths), files)
    MAPI_LOGON_UI, MAPI_DIALOG = 0x1, 0x8
    try:
        rc = mapi.MAPISendMail(0, 0, ctypes.byref(msg), MAPI_LOGON_UI | MAPI_DIALOG, 0)
    except (AttributeError, OSError):
        return False
    return rc in (0, 1)  # 0 = sent/handed over, 1 = user closed the message
