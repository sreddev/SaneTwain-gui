"""Minimal pure-Python client for the SANE network protocol (saned, port 6566).

Implements the RPCs needed to list devices, read and set options, and
acquire images. Reference: SANE standard, chapter "Network Protocol".
"""
from __future__ import annotations

import socket
import struct
from dataclasses import dataclass
from typing import Any, Callable, Optional

# RPC codes
NET_INIT, NET_GET_DEVICES, NET_OPEN, NET_CLOSE = 0, 1, 2, 3
NET_GET_OPTION_DESCRIPTORS, NET_CONTROL_OPTION = 4, 5
NET_GET_PARAMETERS, NET_START, NET_CANCEL, NET_AUTHORIZE, NET_EXIT = 6, 7, 8, 9, 10

# Value types
TYPE_BOOL, TYPE_INT, TYPE_FIXED, TYPE_STRING, TYPE_BUTTON, TYPE_GROUP = range(6)
# Units
UNIT_NONE, UNIT_PIXEL, UNIT_BIT, UNIT_MM, UNIT_DPI, UNIT_PERCENT, UNIT_MICROSECOND = range(7)
# Constraint types
CONSTRAINT_NONE, CONSTRAINT_RANGE, CONSTRAINT_WORD_LIST, CONSTRAINT_STRING_LIST = range(4)
# Capabilities
CAP_SOFT_SELECT, CAP_HARD_SELECT, CAP_SOFT_DETECT = 1, 2, 4
CAP_EMULATED, CAP_AUTOMATIC, CAP_INACTIVE, CAP_ADVANCED = 8, 16, 32, 64
# Actions
ACTION_GET, ACTION_SET, ACTION_AUTO = 0, 1, 2
# Info flags
INFO_INEXACT, INFO_RELOAD_OPTIONS, INFO_RELOAD_PARAMS = 1, 2, 4
# Frame formats
FRAME_GRAY, FRAME_RGB, FRAME_RED, FRAME_GREEN, FRAME_BLUE = range(5)

STATUS_TEXT = {
    0: "Success", 1: "Operation not supported", 2: "Operation cancelled",
    3: "Device busy", 4: "Invalid argument", 5: "End of file",
    6: "Document feeder jammed", 7: "Document feeder out of documents",
    8: "Scanner cover is open", 9: "Error during device I/O",
    10: "Out of memory", 11: "Access denied",
}
STATUS_GOOD, STATUS_CANCELLED, STATUS_EOF = 0, 2, 5

SANE_VERSION = (1 << 24) | (0 << 16) | 3


class SaneError(Exception):
    def __init__(self, status: int, where: str = ""):
        self.status = status
        msg = STATUS_TEXT.get(status, f"Unknown SANE status {status}")
        super().__init__(f"{where}: {msg}" if where else msg)


@dataclass
class Device:
    name: str
    vendor: str
    model: str
    type: str

    @property
    def label(self) -> str:
        return f"{self.vendor} {self.model}".strip() or self.name


@dataclass
class Option:
    index: int
    name: str
    title: str
    desc: str
    type: int
    unit: int
    size: int
    cap: int
    constraint_type: int
    constraint: Any = None  # (min,max,quant) | list[int] | list[str]

    @property
    def active(self) -> bool:
        return not (self.cap & CAP_INACTIVE)

    @property
    def settable(self) -> bool:
        return bool(self.cap & CAP_SOFT_SELECT) and self.active

    def to_python(self, v):
        if self.type == TYPE_FIXED:
            return v / 65536.0
        return v

    def choices(self) -> list:
        if self.constraint_type == CONSTRAINT_STRING_LIST:
            return list(self.constraint)
        if self.constraint_type == CONSTRAINT_WORD_LIST:
            return [self.to_python(v) for v in self.constraint]
        return []

    def range(self) -> Optional[tuple]:
        if self.constraint_type == CONSTRAINT_RANGE:
            lo, hi, q = self.constraint
            return self.to_python(lo), self.to_python(hi), self.to_python(q)
        return None


@dataclass
class Parameters:
    format: int
    last_frame: bool
    bytes_per_line: int
    pixels_per_line: int
    lines: int
    depth: int


class _Wire:
    def __init__(self, sock: socket.socket):
        self.sock = sock

    def send(self, data: bytes):
        self.sock.sendall(data)

    def word(self, v: int):
        self.send(struct.pack(">I", v & 0xFFFFFFFF))

    def string(self, s: Optional[str]):
        if s is None:
            self.word(0)
            return
        b = s.encode("latin-1", "replace") + b"\0"
        self.word(len(b))
        self.send(b)

    def exact(self, n: int) -> bytes:
        buf = bytearray()
        while len(buf) < n:
            chunk = self.sock.recv(n - len(buf))
            if not chunk:
                raise ConnectionError("saned closed the connection")
            buf += chunk
        return bytes(buf)

    def rword(self) -> int:
        return struct.unpack(">I", self.exact(4))[0]

    def rint(self) -> int:
        return struct.unpack(">i", self.exact(4))[0]

    def rstring(self) -> Optional[str]:
        n = self.rword()
        if n == 0:
            return None
        return self.exact(n).split(b"\0", 1)[0].decode("latin-1")


class SaneClient:
    """One control connection to saned. Not thread-safe; use from one thread."""

    def __init__(self, host: str, port: int = 6566, user: str = "",
                 password: str = "", timeout: float = 8.0):
        self.host, self.port, self.user, self.password = host, port, user, password
        self.timeout = timeout
        self.sock: Optional[socket.socket] = None
        self.w: Optional[_Wire] = None
        self.handle: Optional[int] = None
        self.options: list[Option] = []
        self.by_name: dict[str, Option] = {}

    # ---- connection ----
    def connect(self):
        self.sock = socket.create_connection((self.host, self.port), timeout=self.timeout)
        self.sock.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
        self.w = _Wire(self.sock)
        self.w.word(NET_INIT)
        self.w.word(SANE_VERSION)
        self.w.string(self.user)
        status = self.w.rword()
        self.server_version = self.w.rword()
        if status != STATUS_GOOD:
            raise SaneError(status, "init")
        return self

    def close(self):
        try:
            if self.handle is not None and self.w:
                self.w.word(NET_CLOSE)
                self.w.word(self.handle)
                self.w.rword()
        except Exception:
            pass
        self.handle = None
        try:
            if self.w:
                self.w.word(NET_EXIT)
        except Exception:
            pass
        try:
            if self.sock:
                self.sock.close()
        finally:
            self.sock = self.w = None

    def __enter__(self):
        return self.connect()

    def __exit__(self, *exc):
        self.close()

    def _authorize(self, resource: str):
        w = self.w
        w.word(NET_AUTHORIZE)
        w.string(resource)
        w.string(self.user)
        w.string(self.password)
        w.rword()  # dummy

    # ---- devices ----
    def get_devices(self) -> list[Device]:
        w = self.w
        w.word(NET_GET_DEVICES)
        status = w.rword()
        n = w.rword()
        devs = []
        for _ in range(n):
            if w.rword():  # NULL pointer terminates the list
                continue
            devs.append(Device(*(w.rstring() or "" for _ in range(4))))
        if status != STATUS_GOOD:
            raise SaneError(status, "get devices")
        return devs

    def open(self, device: str):
        w = self.w
        w.word(NET_OPEN)
        w.string(device)
        while True:
            status = w.rword()
            handle = w.rword()
            resource = w.rstring()
            if resource:
                self._authorize(resource)
                continue
            break
        if status != STATUS_GOOD:
            raise SaneError(status, f"open {device}")
        self.handle = handle
        self.reload_options()

    # ---- options ----
    def reload_options(self):
        w = self.w
        w.word(NET_GET_OPTION_DESCRIPTORS)
        w.word(self.handle)
        n = w.rword()
        opts = []
        for i in range(n):
            if w.rword():
                opts.append(None)
                continue
            name, title, desc = w.rstring() or "", w.rstring() or "", w.rstring() or ""
            typ, unit, size, cap, ctype = (w.rword() for _ in range(5))
            constraint = None
            if ctype == CONSTRAINT_RANGE:
                if not w.rword():
                    constraint = (w.rint(), w.rint(), w.rint())
            elif ctype == CONSTRAINT_WORD_LIST:
                m = w.rword()
                lst = [w.rint() for _ in range(m)]
                constraint = lst[1:]  # first element is the count
            elif ctype == CONSTRAINT_STRING_LIST:
                m = w.rword()
                constraint = [s for s in (w.rstring() for _ in range(m)) if s is not None]
            opts.append(Option(i, name, title, desc, typ, unit, size, cap, ctype, constraint))
        self.options = [o for o in opts if o is not None]
        self.by_name = {o.name: o for o in self.options if o.name}

    def _control(self, opt: Option, action: int, value=None):
        w = self.w
        w.word(NET_CONTROL_OPTION)
        w.word(self.handle)
        w.word(opt.index)
        w.word(action)
        w.word(opt.type)
        if opt.type == TYPE_STRING:
            raw = (value or "").encode("latin-1", "replace")[: max(opt.size - 1, 0)] if action == ACTION_SET else b""
            raw = raw + b"\0" * (opt.size - len(raw))
            w.word(opt.size)
            w.word(len(raw))
            w.send(raw)
        elif opt.type in (TYPE_BOOL, TYPE_INT, TYPE_FIXED):
            count = max(opt.size // 4, 1)
            vals = value if isinstance(value, (list, tuple)) else [value] * count
            if action != ACTION_SET:
                vals = [0] * count
            w.word(count * 4)
            w.word(count)
            for v in vals:
                if opt.type == TYPE_FIXED:
                    v = int(round(float(v) * 65536))
                w.word(int(v))
        else:  # button / group
            w.word(0)
            w.word(0)
        while True:
            status = w.rword()
            info = w.rword()
            vtype = w.rword()
            w.rword()  # value size
            alen = w.rword()
            if vtype == TYPE_STRING:
                data = w.exact(alen)
                result = data.split(b"\0", 1)[0].decode("latin-1")
            elif vtype in (TYPE_BOOL, TYPE_INT, TYPE_FIXED):
                vals = [w.rint() for _ in range(alen)]
                vals = [opt.to_python(v) for v in vals] if vtype == TYPE_FIXED else vals
                result = vals[0] if len(vals) == 1 else vals
            else:
                w.exact(alen) if alen else None
                result = None
            resource = w.rstring()
            if resource:
                self._authorize(resource)
                continue
            break
        if status != STATUS_GOOD:
            raise SaneError(status, f"option '{opt.name}'")
        if info & INFO_RELOAD_OPTIONS:
            self.reload_options()
        return result, info

    def get(self, name: str):
        opt = self.by_name[name]
        return self._control(opt, ACTION_GET)[0]

    def set(self, name: str, value) -> Any:
        """Set an option; returns the value the backend actually applied."""
        opt = self.by_name.get(name)
        if opt is None or not opt.settable:
            return None
        if opt.type == TYPE_STRING and opt.constraint_type == CONSTRAINT_STRING_LIST:
            if value not in opt.constraint:
                return None
        self._control(opt, ACTION_SET, value)
        try:
            return self.get(name)
        except Exception:
            return value

    # ---- acquisition ----
    def get_parameters(self) -> Parameters:
        w = self.w
        w.word(NET_GET_PARAMETERS)
        w.word(self.handle)
        status = w.rword()
        p = Parameters(w.rword(), bool(w.rword()), w.rint(), w.rint(), w.rint(), w.rint())
        if status != STATUS_GOOD:
            raise SaneError(status, "get parameters")
        return p

    def cancel(self):
        try:
            self.w.word(NET_CANCEL)
            self.w.word(self.handle)
            self.w.rword()
        except Exception:
            pass

    def start(self) -> tuple[socket.socket, int]:
        w = self.w
        w.word(NET_START)
        w.word(self.handle)
        while True:
            status = w.rword()
            port = w.rword()
            byte_order = w.rword()
            resource = w.rstring()
            if resource:
                self._authorize(resource)
                continue
            break
        if status != STATUS_GOOD:
            raise SaneError(status, "start")
        data = socket.create_connection((self.host, port), timeout=max(self.timeout, 60))
        return data, byte_order

    def acquire(self, on_data: Optional[Callable[[bytes, int, Parameters], None]] = None,
                should_cancel: Optional[Callable[[], bool]] = None) -> tuple[Parameters, bytes]:
        """Run one single-frame (or 3-pass RGB) scan and return raw image bytes.

        on_data(chunk, bytes_so_far, params) is called as data arrives.
        """
        frames = {}
        params = None
        while True:
            data_sock, byte_order = self.start()
            params = self.get_parameters()
            buf = bytearray()
            try:
                dw = _Wire(data_sock)
                while True:
                    if should_cancel and should_cancel():
                        self.cancel()
                        raise SaneError(STATUS_CANCELLED, "scan")
                    n = dw.rword()
                    if n == 0xFFFFFFFF:
                        status = dw.exact(1)[0]
                        if status not in (STATUS_GOOD, STATUS_EOF):
                            raise SaneError(status, "reading image")
                        break
                    chunk = dw.exact(n)
                    buf += chunk
                    if on_data:
                        on_data(chunk, len(buf), params)
            finally:
                data_sock.close()
            if params.depth == 16 and byte_order == 0x1234:  # little endian -> swap to big
                b = bytearray(buf)
                b[0::2], b[1::2] = buf[1::2], buf[0::2]
                buf = b
            frames[params.format] = bytes(buf)
            if params.last_frame:
                break
        self.cancel()  # tell the backend this image is done
        if params.format in (FRAME_RED, FRAME_GREEN, FRAME_BLUE):
            r, g, b = frames.get(FRAME_RED, b""), frames.get(FRAME_GREEN, b""), frames.get(FRAME_BLUE, b"")
            out = bytearray(len(r) * 3)
            out[0::3], out[1::3], out[2::3] = r, g, b
            params.format = FRAME_RGB
            params.bytes_per_line *= 3
            return params, bytes(out)
        return params, frames[params.format]
