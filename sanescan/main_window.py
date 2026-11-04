"""Main window: scan buttons, live preview, per-button settings, saved files and log."""
from __future__ import annotations

import os
from datetime import datetime
from pathlib import Path

from PySide6.QtCore import QByteArray, QPointF, QRectF, QSize, Qt, QTimer
from PySide6.QtGui import (QColor, QIcon, QImage, QKeySequence, QLinearGradient, QPainter, QPen, QPixmap,
                           QPolygonF, QShortcut)
from PySide6.QtWidgets import (QApplication, QButtonGroup, QCheckBox, QComboBox, QFileDialog, QFormLayout,
                               QFrame, QHBoxLayout, QLabel, QLineEdit, QListWidget, QListWidgetItem,
                               QMainWindow, QMenu, QPlainTextEdit, QProgressBar, QPushButton, QScrollArea,
                               QSizePolicy, QSlider, QSplitter, QStyle, QSystemTrayIcon, QTabWidget,
                               QTreeWidget, QTreeWidgetItem, QVBoxLayout, QWidget)

from . import config, saving, system, theme
from .config import AFTER_SAVE, DEFAULT_MODES, DEFAULT_RESOLUTIONS, DOC_SIZES, FILE_TYPES, AppSettings, Profile
from .settings_dialog import SettingsDialog
from .workers import DeviceInfo, MailJob, ProbeJob, SaveJob, ScanJob, pil_to_qimage

GLASS_MM = (215.9, 297.0)
LIVE_JOBS: list = []


def keep(job):
    LIVE_JOBS.append(job)
    job.finished.connect(lambda: LIVE_JOBS.remove(job) if job in LIVE_JOBS else None)
    return job


def fmt_size(n: int) -> str:
    return f"{n / 1048576:.1f} MB" if n >= 1048576 else f"{max(1, round(n / 1024))} KB"


# ---------------------------------------------------------------- preview widget
class PlatenView(QWidget):
    """Draws the scanner glass, the image as it arrives and the moving lamp."""

    def __init__(self):
        super().__init__()
        self.setMinimumSize(260, 360)
        self.setSizePolicy(QSizePolicy.Expanding, QSizePolicy.Expanding)
        self.image: QImage | None = None
        self.frac = 0.0           # 0..1 of page height received, <1 shows the lamp
        self.area = (210.0, 297.0)
        self.scanning = False
        self.colors = theme.LIGHT

    def set_colors(self, t):
        self.colors = t
        self.update()

    def show_image(self, img: QImage | None, frac=1.0, area=None, scanning=False):
        self.image, self.frac, self.scanning = img, frac, scanning
        if area:
            self.area = area
        self.update()

    def paintEvent(self, _):
        p = QPainter(self)
        p.setRenderHint(QPainter.SmoothPixmapTransform)
        p.setRenderHint(QPainter.Antialiasing)
        aspect = GLASS_MM[0] / GLASS_MM[1]
        h = self.height() - 8
        w = h * aspect
        if w > self.width() - 8:
            w = self.width() - 8
            h = w / aspect
        x0, y0 = (self.width() - w) / 2, 4.0
        p.setPen(Qt.NoPen)
        p.setBrush(QColor(self.colors["platen"]))
        p.drawRoundedRect(int(x0), int(y0), int(w), int(h), 6, 6)
        # grid + corner mark
        p.setPen(QPen(QColor(255, 255, 255, 18), 1))
        for i in range(1, 8):
            xx = x0 + w * i / 8
            p.drawLine(int(xx), int(y0), int(xx), int(y0 + h))
        for i in range(1, 11):
            yy = y0 + h * i / 11
            p.drawLine(int(x0), int(yy), int(x0 + w), int(yy))
        p.setPen(Qt.NoPen)
        p.setBrush(QColor(255, 255, 255, 140))
        s = w * 0.05
        p.drawPolygon(QPolygonF([QPointF(x0 + 5, y0 + 5), QPointF(x0 + 5 + s, y0 + 5), QPointF(x0 + 5, y0 + 5 + s)]))
        aw, ah = w * self.area[0] / GLASS_MM[0], h * self.area[1] / GLASS_MM[1]
        if self.image is not None and not self.image.isNull():
            shown_h = ah * (self.frac if self.scanning else 1.0)
            if self.scanning and self.frac > 0:
                # the image holds only the received lines; scale it to the received height
                p.drawImage(QRectF(x0, y0, aw, shown_h), self.image)
            elif not self.scanning:
                p.drawImage(QRectF(x0, y0, aw, min(ah, aw * self.image.height() / max(1, self.image.width()))), self.image)
        if self.scanning:
            ly = y0 + ah * self.frac
            g = QLinearGradient(0, ly - 22, 0, ly + 22)
            lamp = QColor(self.colors["lamp"])
            c0 = QColor(lamp); c0.setAlpha(0)
            c1 = QColor(255, 214, 150, 230)
            g.setColorAt(0, c0); g.setColorAt(0.5, c1); g.setColorAt(1, c0)
            p.fillRect(QRectF(x0, ly - 22, w, 44), g)
            p.fillRect(QRectF(x0, ly - 1, w, 2), QColor(255, 242, 220))
        p.end()



# ---------------------------------------------------------------- main window
class MainWindow(QMainWindow):
    def __init__(self, settings: AppSettings):
        super().__init__()
        self.s = settings
        self.profiles = {p.key: p for p in settings.profiles}
        self.active = settings.last_profile if settings.last_profile in self.profiles else "file"
        self.info = DeviceInfo(modes=list(DEFAULT_MODES), resolutions=list(DEFAULT_RESOLUTIONS))
        self.connected = None
        self.pages: list = []          # PIL images of the current session
        self.page_dpi = 300
        self.session_profile: Profile | None = None
        self.job: ScanJob | None = None
        self.busy = False
        self._loading = False

        self.setWindowTitle("SaneTwain Scan Center")
        self.setWindowIcon(self.style().standardIcon(QStyle.SP_DriveFDIcon))
        self.resize(1240, 860)
        self._build()
        self.t = theme.apply(QApplication.instance(), self.s.theme)
        self.view.set_colors(self.t)
        self._apply_general()
        if self.s.geometry:
            self.restoreGeometry(QByteArray.fromBase64(self.s.geometry.encode()))
        self.tray = QSystemTrayIcon(self.windowIcon(), self)
        self.tray.setToolTip("SaneTwain Scan Center")
        self.tray.messageClicked.connect(self._open_last)
        self.tray.activated.connect(self._tray_activated)
        self._build_tray_menu()
        self.tray.show()
        self._really_quit = False
        self._hid_once = False
        self.printed, self.sess_printer, self.print_skip = 0, None, False
        self._finishing = []
        self.last_saved: list[Path] = []

        self.log(f"Loaded settings from {config.INI_PATH}")
        self._render_profile()
        self._update_actions()
        QTimer.singleShot(50, self.probe)

    # ------------------------------------------------------------ UI build
    def _build(self):
        root = QWidget(); self.setCentralWidget(root)
        v = QVBoxLayout(root); v.setContentsMargins(0, 0, 0, 0); v.setSpacing(0)

        # header
        hdr = QFrame(); hdr.setObjectName("Header")
        h = QHBoxLayout(hdr); h.setContentsMargins(16, 10, 12, 10)
        t = QLabel("SaneTwain Scan Center"); t.setObjectName("AppTitle")
        self.dev_dot = QLabel("●"); self.dev_label = QLabel(""); self.dev_label.setObjectName("Device")
        self.app_btn = QPushButton("App settings")
        self.app_btn.setIcon(self.style().standardIcon(QStyle.SP_FileDialogDetailedView))
        self.app_btn.clicked.connect(self.open_settings)
        h.addWidget(t); h.addStretch(1); h.addWidget(self.dev_dot); h.addWidget(self.dev_label); h.addSpacing(10); h.addWidget(self.app_btn)
        v.addWidget(hdr)

        split = QSplitter(Qt.Vertical); split.setChildrenCollapsible(False)
        v.addWidget(split, 1)

        body = QWidget(); bl = QHBoxLayout(body); bl.setContentsMargins(0, 0, 0, 0); bl.setSpacing(0)
        split.addWidget(body)

        # nav
        nav = QFrame(); nav.setObjectName("Nav"); nav.setFixedWidth(220)
        nl = QVBoxLayout(nav); nl.setContentsMargins(10, 12, 10, 12); nl.setSpacing(4)
        e = QLabel("SCAN BUTTONS"); e.setObjectName("Eyebrow"); nl.addWidget(e)
        self.prof_group = QButtonGroup(self); self.prof_group.setExclusive(True)
        self.prof_btns = {}
        for k, p in self.profiles.items():
            b = QPushButton(); b.setObjectName("Profile"); b.setCheckable(True)
            b.clicked.connect(lambda _=False, key=k: self.select_profile(key))
            self.prof_group.addButton(b); nl.addWidget(b); self.prof_btns[k] = b
        nl.addStretch(1)
        tip = QLabel("Each button keeps its own folder, file name and scan settings.")
        tip.setObjectName("Muted"); tip.setWordWrap(True); nl.addWidget(tip)
        bl.addWidget(nav)

        # stage
        stage = QFrame(); stage.setObjectName("Stage")
        sl = QVBoxLayout(stage); sl.setContentsMargins(16, 12, 16, 0); sl.setSpacing(10)
        top = QHBoxLayout()
        self.scan_btn = QPushButton("Scan"); self.scan_btn.setObjectName("Primary"); self.scan_btn.clicked.connect(self.start_session)
        self.prev_btn = QPushButton("Preview"); self.prev_btn.clicked.connect(self.preview)
        self.cancel_btn = QPushButton("Cancel"); self.cancel_btn.clicked.connect(self.cancel); self.cancel_btn.hide()
        self.hint = QLabel("Place the document face down, top edge against the top-left corner arrow."); self.hint.setObjectName("Hint")
        self.hint.setWordWrap(True)
        top.addWidget(self.scan_btn); top.addWidget(self.prev_btn); top.addWidget(self.cancel_btn); top.addSpacing(8); top.addWidget(self.hint, 1)
        sl.addLayout(top)
        self.view = PlatenView(); sl.addWidget(self.view, 1)

        # next-page bar
        self.more = QFrame(); self.more.setObjectName("MoreBar"); self.more.hide()
        ml = QVBoxLayout(self.more); ml.setContentsMargins(14, 12, 14, 12)
        self.more_title = QLabel(""); self.more_title.setObjectName("MoreTitle")
        mt = QLabel("Place the next page on the glass and scan it, or finish to save the file now. "
                    "This scanner has no document feeder, so pages go in one at a time.")
        mt.setObjectName("Muted"); mt.setWordWrap(True)
        mr = QHBoxLayout()
        self.more_yes = QPushButton("Scan next page"); self.more_yes.setObjectName("Primary"); self.more_yes.clicked.connect(self.next_page)
        self.more_no = QPushButton("Finish and save"); self.more_no.clicked.connect(self.finish_session)
        self.more_drop = QPushButton("Discard pages"); self.more_drop.clicked.connect(self.discard_session)
        mr.addWidget(self.more_yes); mr.addWidget(self.more_no); mr.addStretch(1); mr.addWidget(self.more_drop)
        ml.addWidget(self.more_title); ml.addWidget(mt); ml.addLayout(mr)
        sl.addWidget(self.more)

        # error bar
        self.err = QFrame(); self.err.setObjectName("ErrorBar"); self.err.hide()
        el = QVBoxLayout(self.err); el.setContentsMargins(14, 12, 14, 12)
        self.err_title = QLabel(""); self.err_title.setObjectName("ErrorTitle")
        self.err_body = QLabel(""); self.err_body.setWordWrap(True); self.err_body.setTextInteractionFlags(Qt.TextSelectableByMouse)
        er = QHBoxLayout()
        self.err_act = QPushButton("Retry"); self.err_act.setObjectName("Primary")
        self.err_close = QPushButton("Dismiss"); self.err_close.clicked.connect(self.err.hide)
        er.addWidget(self.err_act); er.addWidget(self.err_close); er.addStretch(1)
        el.addWidget(self.err_title); el.addWidget(self.err_body); el.addLayout(er)
        sl.addWidget(self.err)

        # tray
        trayf = QFrame(); trayf.setObjectName("Tray")
        tl = QHBoxLayout(trayf); tl.setContentsMargins(0, 6, 0, 6)
        lab = QLabel("PAGES"); lab.setObjectName("Eyebrow"); tl.addWidget(lab)
        self.tray_list = QListWidget(); self.tray_list.setViewMode(QListWidget.IconMode); self.tray_list.setFlow(QListWidget.LeftToRight)
        self.tray_list.setIconSize(QSize(48, 66)); self.tray_list.setFixedHeight(96); self.tray_list.setWrapping(False)
        self.tray_list.setMovement(QListWidget.Static); self.tray_list.setContextMenuPolicy(Qt.CustomContextMenu)
        self.tray_list.customContextMenuRequested.connect(self._tray_menu)
        self.tray_empty = QLabel("Scanned pages collect here until the file is saved."); self.tray_empty.setObjectName("Muted")
        tl.addWidget(self.tray_list, 1); tl.addWidget(self.tray_empty, 1)
        sl.addWidget(trayf)
        bl.addWidget(stage, 1)

        # settings
        scroll = QScrollArea(); scroll.setObjectName("SettingsScroll"); scroll.setWidgetResizable(True); scroll.setFixedWidth(350)
        scroll.setHorizontalScrollBarPolicy(Qt.ScrollBarAlwaysOff)
        sw = QWidget(); sw.setObjectName("Settings"); scroll.setWidget(sw)
        f = QVBoxLayout(sw); f.setContentsMargins(16, 14, 16, 14); f.setSpacing(10)
        f.setSizeConstraint(QVBoxLayout.SetMinimumSize)  # scroll instead of squashing rows
        self.set_title = QLabel(""); self.set_title.setObjectName("Eyebrow"); f.addWidget(self.set_title)

        f.addWidget(self._lab("Save to folder"))
        self.folder = QLineEdit(); self.folder.setReadOnly(True); self.folder.setMinimumWidth(60)
        fb = QPushButton("Change…"); fb.clicked.connect(self.choose_folder); self.folder_btn = fb
        ob = QPushButton(); ob.setIcon(self.style().standardIcon(QStyle.SP_DirOpenIcon)); ob.setToolTip("Open this folder")
        ob.clicked.connect(self._open_folder)
        r = QHBoxLayout(); r.addWidget(self.folder, 1); r.addWidget(fb); r.addWidget(ob); f.addLayout(r)
        self.missing = QLabel("This folder doesn't exist yet."); self.missing.setObjectName("Warn"); f.addWidget(self.missing)
        self.create = QCheckBox("Create the folder if it's missing"); f.addWidget(self.create)

        f.addWidget(self._lab("File name"))
        self.pattern = QLineEdit(); f.addWidget(self.pattern)
        tr = QHBoxLayout(); tr.setSpacing(4)
        tl2 = QLabel("Insert:"); tl2.setObjectName("Muted"); tr.addWidget(tl2)
        for tok in ("{profile}", "{date}", "{time}", "{n}"):
            tb = QPushButton(tok); tb.setObjectName("Token"); tb.setCursor(Qt.PointingHandCursor)
            tb.clicked.connect(lambda _=False, x=tok: self._insert_token(x)); tr.addWidget(tb)
        tr.addStretch(1); f.addLayout(tr)
        self.name_prev = QLabel(""); self.name_prev.setObjectName("NamePreview"); self.name_prev.setWordWrap(True)
        f.addWidget(self.name_prev)

        f.addWidget(self._lab("File type"))
        self.ftype = QComboBox()
        for k, (label, _, _) in FILE_TYPES.items():
            self.ftype.addItem(label, k)
        f.addWidget(self.ftype)
        f.addWidget(self._sep())

        g = QFormLayout(); g.setLabelAlignment(Qt.AlignLeft); g.setVerticalSpacing(8)
        self.res = QComboBox(); self.size = QComboBox(); self.mode = QComboBox()
        for cb in (self.res, self.size, self.mode):
            cb.setMinimumContentsLength(10); cb.setSizeAdjustPolicy(QComboBox.AdjustToMinimumContentsLengthWithIcon)
            cb.setMinimumHeight(30)
        for k, (w, hh) in DOC_SIZES.items():
            self.size.addItem(f"{k}  ({w:g} × {hh:g} mm)", k)
        g.addRow("Resolution", self.res); g.addRow("Document size", self.size); g.addRow("Scan type", self.mode)
        self.bright = QSlider(Qt.Horizontal); self.bright.setRange(-50, 50); self.bright.setSingleStep(5); self.bright.setPageStep(10)
        self.contrast = QSlider(Qt.Horizontal); self.contrast.setRange(-50, 50); self.contrast.setSingleStep(5); self.contrast.setPageStep(10)
        self.bright_l, self.contrast_l = QLabel("0"), QLabel("0")
        for l in (self.bright_l, self.contrast_l):
            l.setFixedWidth(30); l.setAlignment(Qt.AlignRight | Qt.AlignVCenter)
        self.bright_row = self._pair(self.bright, self.bright_l); self.contrast_row = self._pair(self.contrast, self.contrast_l)
        g.addRow("Brightness", self.bright_row); g.addRow("Contrast", self.contrast_row)
        self.form = g
        f.addLayout(g)
        self.adj_note = QLabel(""); self.adj_note.setObjectName("Muted"); self.adj_note.setWordWrap(True); f.addWidget(self.adj_note)
        f.addWidget(self._sep())
        self.multi = QCheckBox("Ask for another page after each scan"); f.addWidget(self.multi)
        f.addWidget(self._lab("When the file is saved"))
        self.after = QComboBox()
        for k, v in AFTER_SAVE.items():
            self.after.addItem(v, k)
        f.addWidget(self.after)
        self.savefile = QCheckBox("Also save a file"); f.addWidget(self.savefile)
        self.est = QLabel(""); self.est.setObjectName("Muted"); self.est.setWordWrap(True); f.addWidget(self.est)
        f.addStretch(1)
        reset = QPushButton("Reset this button to defaults"); reset.clicked.connect(self._reset_profile); f.addWidget(reset)
        bl.addWidget(scroll)

        # wire settings
        self.create.toggled.connect(lambda v: self._set("create", v))
        self.pattern.textEdited.connect(lambda v: self._set("pattern", v or "Scan_{n}"))
        self.ftype.currentIndexChanged.connect(lambda _: self._set("type", self.ftype.currentData()))
        self.res.currentIndexChanged.connect(lambda _: self._set("resolution", self.res.currentData()))
        self.size.currentIndexChanged.connect(lambda _: self._set("size", self.size.currentData()))
        self.mode.currentIndexChanged.connect(lambda _: self._set("mode", self.mode.currentData()))
        self.bright.valueChanged.connect(lambda v: self._set("brightness", v))
        self.contrast.valueChanged.connect(lambda v: self._set("contrast", v))
        self.multi.toggled.connect(lambda v: self._set("multi", v))
        self.after.currentIndexChanged.connect(lambda _: self._set("after", self.after.currentData()))
        self.savefile.toggled.connect(lambda v: self._set("save_file", v))

        # bottom tabs
        tabs = QTabWidget(); tabs.setDocumentMode(False)
        wrap = QWidget(); wl = QVBoxLayout(wrap); wl.setContentsMargins(12, 8, 12, 8); wl.addWidget(tabs)
        self.saved = QTreeWidget(); self.saved.setHeaderLabels(["Name", "Folder", "Pages", "Size", "Saved"])
        self.saved.setRootIsDecorated(False); self.saved.setAlternatingRowColors(False)
        self.saved.itemDoubleClicked.connect(lambda it, _c: self._open_path(it.data(0, Qt.UserRole)))
        self.saved.setContextMenuPolicy(Qt.CustomContextMenu); self.saved.customContextMenuRequested.connect(self._saved_menu)
        self.saved.setColumnWidth(0, 300); self.saved.setColumnWidth(1, 300)
        self.logw = QPlainTextEdit(); self.logw.setReadOnly(True); self.logw.setMaximumBlockCount(2000)
        tabs.addTab(self.saved, "Saved files"); tabs.addTab(self.logw, "Activity")
        split.addWidget(wrap)
        split.setSizes([640, 200])

        # status bar
        sb = self.statusBar()
        self.st_dot = QLabel("●"); self.st_msg = QLabel("Ready")
        self.prog = QProgressBar(); self.prog.setFixedWidth(180); self.prog.setTextVisible(False); self.prog.setRange(0, 1000)
        self.st_meta = QLabel(""); self.st_meta.setObjectName("Muted")
        sb.addWidget(self.st_dot); sb.addWidget(self.st_msg, 1); sb.addPermanentWidget(self.prog); sb.addPermanentWidget(self.st_meta)

        self.esc = QShortcut(QKeySequence(Qt.Key_Escape), self, activated=self._esc)
        QShortcut(QKeySequence("Ctrl+S"), self, activated=lambda: self.scan_btn.isEnabled() and self.start_session())
        QShortcut(QKeySequence("F5"), self, activated=lambda: self.prev_btn.isEnabled() and self.preview())

    def _lab(self, text):
        l = QLabel(text); l.setObjectName("Muted"); f = l.font(); f.setBold(True); l.setFont(f); return l

    def _sep(self):
        s = QFrame(); s.setFrameShape(QFrame.HLine); s.setObjectName("Muted"); return s

    def _pair(self, a, b):
        w = QWidget(); h = QHBoxLayout(w); h.setContentsMargins(0, 0, 0, 0); h.addWidget(a, 1); h.addWidget(b); return w

    # ------------------------------------------------------------ state helpers
    @property
    def prof(self) -> Profile:
        return self.profiles[self.active]

    def log(self, msg: str):
        self.logw.appendPlainText(f"{datetime.now():%H:%M:%S}  {msg}")

    def status(self, msg, state="ok", progress=None):
        self.st_msg.setText(msg)
        col = {"ok": self.t["ok"], "busy": self.t["lamp"], "err": self.t["err"]}[state]
        self.st_dot.setStyleSheet(f"color: {col};")
        if progress is None:
            pass
        elif progress < 0:
            self.prog.setRange(0, 0)
        else:
            self.prog.setRange(0, 1000); self.prog.setValue(int(progress * 1000))

    def _set_device_label(self):
        name = self.s.device or "no scanner chosen"
        model = next((d.label for d in self.info.devices if d.name == self.s.device), "")
        self.dev_label.setText(f"{model + ' · ' if model else ''}{name} · {self.s.host}:{self.s.port}")
        col = {True: self.t["ok"], False: self.t["err"], None: self.t["muted"]}[self.connected]
        self.dev_dot.setStyleSheet(f"color: {col};")

    def _apply_general(self):
        self.setWindowFlag(Qt.WindowStaysOnTopHint, self.s.topmost)
        if self.isVisible():
            self.show()

    # ------------------------------------------------------------ profile UI
    def select_profile(self, key):
        if self.busy or self.pages:
            self.prof_btns[self.active].setChecked(True)
            self.status("Finish or discard the pages you're scanning before switching buttons.", "busy")
            return
        self.active = key
        self.s.last_profile = key
        self._render_profile()
        self.save_settings()

    def _render_profile(self):
        self._loading = True
        p = self.prof
        for k, b in self.prof_btns.items():
            q = self.profiles[k]
            b.setText(f"{q.label}\n{_short(q.folder)}")
            b.setToolTip(q.folder)
            b.setChecked(k == self.active)
        self.set_title.setText(f"{p.label.upper()} SETTINGS")
        self.scan_btn.setText(p.label)
        self.folder.setText(p.folder); self.folder.setToolTip(p.folder)
        self.create.setChecked(p.create)
        if self.pattern.text() != p.pattern:
            self.pattern.setText(p.pattern)
        self.ftype.setCurrentIndex(max(0, self.ftype.findData(p.type)))
        self.res.clear()
        for r in sorted(set(self.info.resolutions or DEFAULT_RESOLUTIONS)):
            label = f"{r} dpi" + ("  (interpolated)" if r > 1200 else "")
            self.res.addItem(label, r)
        if self.res.findData(p.resolution) < 0:
            self.res.addItem(f"{p.resolution} dpi", p.resolution)
        self.res.setCurrentIndex(self.res.findData(p.resolution))
        self.size.setCurrentIndex(max(0, self.size.findData(p.size)))
        self.mode.clear()
        for m in self.info.modes or DEFAULT_MODES:
            self.mode.addItem(m, m)
        if self.mode.findData(p.mode) < 0:
            self.mode.addItem(p.mode, p.mode)
        self.mode.setCurrentIndex(self.mode.findData(p.mode))
        self.bright.setValue(p.brightness); self.contrast.setValue(p.contrast)
        self.multi.setChecked(p.multi)
        self.after.setCurrentIndex(max(0, self.after.findData(p.after)))
        self.savefile.setChecked(p.save_file)
        self._loading = False
        self._refresh_derived()

    def _refresh_derived(self):
        p = self.prof
        self.bright_l.setText(f"{p.brightness:+d}" if p.brightness else "0")
        self.contrast_l.setText(f"{p.contrast:+d}" if p.contrast else "0")
        folder = Path(os.path.expandvars(p.folder))
        self.missing.setVisible(not folder.is_dir())
        ext = FILE_TYPES[p.type][1]
        try:
            path, _ = saving.choose_path(folder, p, ext, datetime.now(), set()) if folder.is_dir() else \
                (folder / f"{saving.render_name(p.pattern, p, 1)}.{ext}", None)
        except OSError:
            path = folder / f"{saving.render_name(p.pattern, p, 1)}.{ext}"
        self.name_prev.setText(f"Next file: {path.name}")
        self.name_prev.setToolTip(str(path))
        b_ok, c_ok = self.info.adjustable.get(p.mode, (True, True))
        hide = self.s.hide_disabled
        for row, ok, lab in ((self.bright_row, b_ok, "Brightness"), (self.contrast_row, c_ok, "Contrast")):
            row.setEnabled(ok)
            self.form.setRowVisible(row, ok or not hide)
        self.adj_note.setVisible(not (b_ok and c_ok) and not hide)
        self.adj_note.setText(f"The scanner sets brightness and contrast itself in {p.mode}.")
        w, h = DOC_SIZES.get(p.size, DOC_SIZES["A4"])
        px = (w / 25.4 * p.resolution) * (h / 25.4 * p.resolution)
        bpp = 3 if "Color" in p.mode else (1 / 8 if p.mode == "Black & White" else 1)
        self.est.setText(f"About {fmt_size(int(px * bpp))} of image data per page "
                         f"({int(w / 25.4 * p.resolution)} × {int(h / 25.4 * p.resolution)} px).")
        self.st_meta.setText(f"{p.resolution} dpi · {p.mode} · {p.size}")
        printing = p.after == "print"
        self.savefile.setVisible(printing)
        saving_on = p.save_file or not printing
        for wdg in (self.folder, self.folder_btn, self.create, self.pattern, self.ftype, self.name_prev):
            wdg.setEnabled(saving_on)
        if not saving_on:
            self.missing.hide()
            self.name_prev.setText("No file is saved; pages only go to the printer.")
        if printing:
            where = self.s.printer or f"the Windows default printer ({system.default_printer_name() or 'none'})"
            self.est.setText(self.est.text() + f" Prints on {where}.")

    def _set(self, key, value):
        if self._loading or value is None:
            return
        setattr(self.prof, key, value)
        self._refresh_derived()
        self._save_soon()

    def _insert_token(self, tok):
        self.pattern.insert(tok)
        self._set("pattern", self.pattern.text())

    def _reset_profile(self):
        d = {p.key: p for p in config.default_profiles(self.s.default_folder)}[self.active]
        self.profiles[self.active].__dict__.update(d.__dict__)
        self._render_profile(); self.save_settings()
        self.log(f"{d.label}: settings reset to defaults")

    def choose_folder(self):
        start = self.prof.folder if Path(self.prof.folder).is_dir() else self.s.default_folder
        d = QFileDialog.getExistingDirectory(self, f"Folder for {self.prof.label}", start)
        if d:
            self.prof.folder = str(Path(d))
            self.log(f"{self.prof.label}: files will be saved in {self.prof.folder}")
            self._render_profile(); self.save_settings()

    def _open_folder(self):
        f = Path(os.path.expandvars(self.prof.folder))
        if f.is_dir():
            os.startfile(str(f))
        else:
            self.status(f"{f} doesn't exist yet. It will be created when the first file is saved.", "busy")

    # ------------------------------------------------------------ settings persistence
    def _save_soon(self):
        if not hasattr(self, "_save_timer"):
            self._save_timer = QTimer(self); self._save_timer.setSingleShot(True)
            self._save_timer.timeout.connect(self.save_settings)
        self._save_timer.start(800)

    def save_settings(self):
        self.s.profiles = list(self.profiles.values())
        try:
            config.save(self.s)
        except OSError as e:
            self.log(f"Couldn't write {config.INI_PATH}: {e.strerror or e}")

    def open_settings(self):
        if self.busy:
            return
        dlg = SettingsDialog(self.s, sorted(set(self.info.resolutions or DEFAULT_RESOLUTIONS)), self)
        if dlg.exec():
            old = (self.s.host, self.s.port, self.s.user, self.s.device)
            new = dlg.s
            new.profiles = list(self.profiles.values())
            self.s.__dict__.update(new.__dict__)
            self.profiles = {p.key: p for p in self.s.profiles}
            self.t = theme.apply(QApplication.instance(), self.s.theme)
            self.view.set_colors(self.t)
            self._apply_general()
            self.save_settings()
            self.log(f"Saved app settings to {config.INI_PATH}")
            if old != (self.s.host, self.s.port, self.s.user, self.s.device):
                self.probe()
            self._render_profile(); self._set_device_label()

    # ------------------------------------------------------------ device probe
    def probe(self):
        self.connected = None
        self._set_device_label()
        self.status(f"Connecting to saned at {self.s.host}:{self.s.port}…", "busy", -1)
        job = keep(ProbeJob(self.s.host, self.s.port, self.s.user, self.s.device))
        job.log.connect(self.log)

        def ok(info: DeviceInfo):
            self.connected = True
            if not info.devices:
                self.connected = False
                self._error("No scanner found", f"saned at {self.s.host} is running but reports no scanners. "
                            "Check that the DCP-150C is switched on and its USB cable is connected to the server.",
                            "Try again", self.probe)
                self.status("No scanner found", "err", 0)
                self._set_device_label()
                return
            if not self.s.device or not any(d.name == self.s.device for d in info.devices):
                self.s.device = info.devices[0].name
                self.log(f"Using scanner {self.s.device}")
                self.save_settings()
            self.info.devices = info.devices
            if info.modes:
                self.info.modes = info.modes
            if info.resolutions:
                self.info.resolutions = info.resolutions
            self.info.adjustable = info.adjustable
            self.log(f"Connected to saned {info.server_version} at {self.s.host}:{self.s.port}")
            self.err.hide()
            self.status("Ready", "ok", 0)
            self._set_device_label(); self._render_profile()
            if self.s.preview_autostart and not self.busy:
                self.preview()

        def bad(msg):
            self.connected = False
            self.log(msg)
            self.status("Scanner offline", "err", 0)
            self._set_device_label()
            self._error("Scanner not reachable", msg + " Your settings and folders are kept.", "Retry", self.probe)

        job.done.connect(ok); job.failed.connect(bad); job.start()

    def _error(self, title, body, act_label=None, act=None):
        self.err_title.setText(title); self.err_body.setText(body)
        try:
            self.err_act.clicked.disconnect()
        except (RuntimeError, TypeError):
            pass
        if act:
            self.err_act.setText(act_label or "Retry"); self.err_act.show()
            self.err_act.clicked.connect(lambda: (self.err.hide(), act()))
        else:
            self.err_act.hide()
        self.err.show()

    # ------------------------------------------------------------ scanning
    def _min_res(self):
        return min(self.info.resolutions) if self.info.resolutions else 100

    def _update_actions(self):
        idle = not self.busy
        in_session = bool(self.pages)
        self.scan_btn.setEnabled(idle and not in_session)
        self.prev_btn.setEnabled(idle and not in_session)
        self.app_btn.setEnabled(idle and not in_session)
        self.cancel_btn.setVisible(self.busy and self.job is not None)
        for b in self.prof_btns.values():
            b.setEnabled(idle and not in_session)
        for w in (self.more_yes, self.more_no, self.more_drop):
            w.setEnabled(idle)

    def _run_scan(self, preview: bool):
        p = self.session_profile if (self.session_profile and not preview) else self.prof
        self.err.hide(); self.more.hide()
        self.busy = True
        job = keep(ScanJob(self.s, p, preview=preview, min_res=self._min_res()))
        self.job = job
        self._update_actions()
        area = DOC_SIZES.get(p.size, DOC_SIZES["A4"])
        self.view.show_image(None, 0.0, area, scanning=True)
        label = "Preview" if preview else f"Page {len(self.pages) + 1}"
        self.status(f"{label}: connecting…", "busy", -1)
        self.hint.setText("Scanning. Keep the lid closed until the lamp stops.")

        def prog(n, total):
            if total:
                self.status(f"{label}: received {fmt_size(n)} of {fmt_size(total)}", "busy", min(1.0, n / total))
            else:
                self.status(f"{label}: received {fmt_size(n)}", "busy", -1)

        job.progress.connect(prog)
        job.partial.connect(lambda img, frac: self.view.show_image(img, frac, area, scanning=frac < 1.0))
        job.log.connect(self.log)
        job.failed.connect(self._scan_failed)
        job.page.connect(lambda im, dpi: self._page_done(im, dpi, preview))
        job.finished.connect(self._scan_finished)
        job.start()

    def _scan_finished(self):
        self.busy = False
        self.job = None
        self.hint.setText("Place the document face down, top edge against the top-left corner arrow.")
        self._update_actions()

    def _scan_failed(self, msg):
        self.view.show_image(self.view.image, 1.0, scanning=False)
        if msg == "Scan cancelled.":
            self.log("Scan cancelled")
            self.status("Scan cancelled", "ok", 0)
            if self.pages:
                self._ask_more()
            return
        self.log(msg)
        self.status("Scan failed", "err", 0)
        retry = (lambda: self._run_scan(False)) if self.session_profile else self.start_session
        self._error("The scan didn't finish", msg, "Try again", retry)
        if self.pages:
            self.more.show()

    def _page_done(self, im, dpi, preview):
        if preview:
            self.status("Preview ready. Nothing was saved.", "ok", 1)
            self.log("Preview done")
            return
        self.pages.append(im)
        self.page_dpi = dpi
        self._render_tray()
        if self.session_profile.after == "print" and not self.s.print_delayed:
            self._print_pages(self.pages[self.printed:], self.session_profile)
        if self.session_profile.multi:
            self._ask_more()
        else:
            self.finish_session()

    def _ask_more(self):
        n = len(self.pages)
        self.more_title.setText(f"{n} {'page' if n == 1 else 'pages'} scanned")
        self.more_no.setText(f"Finish and save ({n} {'page' if n == 1 else 'pages'})")
        self.more.show()
        self.status("Waiting: scan the next page or finish", "ok", 1)
        self._update_actions()
        self.more_yes.setFocus()

    def start_session(self):
        if self.busy:
            return
        self.pages = []
        self.session_profile = Profile(**self.prof.__dict__)  # freeze settings for this document
        self.printed, self.sess_printer, self.print_skip = 0, None, False
        self._render_tray()
        self._run_scan(False)

    def preview(self):
        if self.busy or self.pages:
            return
        self._run_scan(True)

    def next_page(self):
        if not self.busy:
            self._run_scan(False)

    def cancel(self):
        if self.job:
            self.job.cancel()
            self.status("Cancelling…", "busy")

    def _esc(self):
        if self.s.disable_esc:
            return
        if self.busy:
            self.cancel()
        elif self.err.isVisible():
            self.err.hide()

    def discard_session(self):
        n = len(self.pages)
        self.pages = []; self.session_profile = None
        self.more.hide(); self._render_tray(); self._update_actions()
        self.log(f"Discarded {n} scanned {'page' if n == 1 else 'pages'}")
        self.status("Pages discarded", "ok", 0)

    def finish_session(self):
        if not self.pages or self.busy:
            return
        self.more.hide()
        p = self.session_profile
        self._finishing = list(self.pages)
        if p.after == "print" and not p.save_file:
            self._print_pages(self.pages[self.printed:], p)
            n = len(self.pages)
            self.pages = []; self.session_profile = None
            self._render_tray(); self._update_actions()
            self.status(f"Copy finished: {n} {'page' if n == 1 else 'pages'}", "ok", 1)
            return
        self.busy = True
        self._update_actions()
        self.status("Saving…", "busy", -1)
        job = keep(SaveJob(list(self.pages), p, self.page_dpi, self.s))
        job.log.connect(self.log)
        job.log.connect(lambda m: self.status(m, "busy", -1))
        job.saved.connect(lambda paths: self._saved(paths, p))
        job.failed.connect(self._save_failed)
        job.finished.connect(self._scan_finished)
        job.start()

    def _save_failed(self, msg):
        self.log(msg)
        self.status("Not saved", "err", 0)
        self._error("The file wasn't saved", msg + " The scanned pages are kept, so you can fix this and save again.",
                    "Save again", self._retry_save)
        self.more.show()

    def _retry_save(self):
        # pick up any folder change the user made in the panel
        if self.session_profile:
            self.session_profile.folder = self.prof.folder
            self.session_profile.create = self.prof.create
        self.finish_session()

    def _saved(self, paths, p: Profile):
        n_pages = len(self.pages)
        self.pages = []; self.session_profile = None
        self._render_tray()
        self.last_saved = paths
        for path in paths:
            try:
                size = path.stat().st_size
            except OSError:
                size = 0
            it = QTreeWidgetItem([path.name, str(path.parent), str(n_pages if len(paths) == 1 else 1),
                                  fmt_size(size), f"{datetime.now():%H:%M:%S}"])
            it.setData(0, Qt.UserRole, str(path)); it.setToolTip(0, str(path))
            self.saved.insertTopLevelItem(0, it)
            self.log(f"Saved {path} ({fmt_size(size)})")
        self.saved.setCurrentItem(self.saved.topLevelItem(0))
        first = paths[0]
        more = f" and {len(paths) - 1} more" if len(paths) > 1 else ""
        self.status(f"Saved {first}{more}", "ok", 1)
        self._refresh_derived()
        if self.s.notify:
            self.tray.showMessage("Scan saved", f"{first.name}{more}\n{first.parent}", QSystemTrayIcon.Information, 5000)
        if self.s.sound:
            QApplication.beep()
        try:
            if p.after == "folder":
                saving.show_in_folder(first)
            elif p.after == "open":
                saving.open_file(first)
            elif p.after == "print":
                self._print_pages(self._finishing[self.printed:], p)
            elif p.after == "mail":
                job = keep(MailJob(paths))
                job.result.connect(lambda ok: ok or (self.log("No e-mail program answered, so the file is shown in its folder instead"),
                                                      saving.show_in_folder(first)))
                job.start()
        except OSError as e:
            self.log(f"Couldn't run the after-save action: {e}")

    # ------------------------------------------------------------ printing
    def _print_pages(self, pages, p: Profile):
        if not pages or self.print_skip:
            return
        if self.sess_printer is None:
            pr, problem = system.make_printer(self.s.printer)
            if pr is None:
                self.log(problem)
                self._error("Nothing was printed", problem)
                self.print_skip = True
                return
            if self.s.print_setup and not system.ask_options(pr, self):
                self.log("Printing cancelled; the scanned pages are kept")
                self.print_skip = True
                return
            self.sess_printer = pr
        n = len(pages)
        self.status(f"Printing {n} {'page' if n == 1 else 'pages'}…", "busy", -1)
        QApplication.processEvents()
        msg = system.print_images(self.sess_printer, pages, self.page_dpi,
                                  f"{p.label} {datetime.now():%Y-%m-%d %H:%M}")
        if msg:
            self.log(msg)
            self._error("Printing failed", msg)
            self.print_skip = True
            return
        self.printed += n
        self.log(f"Sent {n} {'page' if n == 1 else 'pages'} to {self.sess_printer.printerName()}")
        self.status(f"Sent to {self.sess_printer.printerName()}", "ok", 1)

    # ------------------------------------------------------------ notification area
    def _build_tray_menu(self):
        m = QMenu(self)
        m.addAction("Open Scan Center").triggered.connect(self.bring_to_front)
        m.addSeparator()
        for k, p in self.profiles.items():
            m.addAction(p.label).triggered.connect(lambda _=False, key=k: self._tray_scan(key))
        m.addSeparator()
        m.addAction("Quit").triggered.connect(self.quit_app)
        self.tray.setContextMenu(m)
        self._tray_menu_ref = m

    def _tray_activated(self, reason):
        if reason in (QSystemTrayIcon.Trigger, QSystemTrayIcon.DoubleClick):
            self.bring_to_front()

    def _tray_scan(self, key):
        self.bring_to_front()
        if self.busy or self.pages:
            return
        self.select_profile(key)
        self.start_session()

    def bring_to_front(self):
        self.showNormal(); self.raise_(); self.activateWindow()

    def quit_app(self):
        self._really_quit = True
        self.close()

    # ------------------------------------------------------------ tray & saved list
    def _render_tray(self):
        self.tray_list.clear()
        for i, im in enumerate(self.pages, 1):
            th = im.copy(); th.thumbnail((96, 132))
            pm = QPixmap.fromImage(pil_to_qimage(th))
            QListWidgetItem(QIcon(pm), f"p{i}", self.tray_list)
        has = bool(self.pages)
        self.tray_list.setVisible(has); self.tray_empty.setVisible(not has)

    def _tray_menu(self, pos):
        it = self.tray_list.itemAt(pos)
        if not it or self.busy:
            return
        row = self.tray_list.row(it)
        m = QMenu(self)
        a = m.addAction(f"Remove page {row + 1}")
        if m.exec(self.tray_list.mapToGlobal(pos)) == a:
            del self.pages[row]
            self._render_tray()
            if self.pages:
                self._ask_more()
            else:
                self.discard_session()

    def _saved_menu(self, pos):
        it = self.saved.itemAt(pos)
        if not it:
            return
        path = Path(it.data(0, Qt.UserRole))
        m = QMenu(self)
        a1 = m.addAction("Open"); a2 = m.addAction("Show in folder"); a3 = m.addAction("Copy path")
        r = m.exec(self.saved.viewport().mapToGlobal(pos))
        if r == a1:
            self._open_path(str(path))
        elif r == a2:
            saving.show_in_folder(path)
        elif r == a3:
            QApplication.clipboard().setText(str(path))

    def _open_path(self, path):
        if path and Path(path).exists():
            saving.open_file(Path(path))
        else:
            self.status("That file has been moved or deleted.", "err")

    def _open_last(self):
        if self.last_saved:
            saving.show_in_folder(self.last_saved[0])

    # ------------------------------------------------------------ close
    def closeEvent(self, e):
        if self.s.start_with_windows and not self._really_quit:
            e.ignore()
            self.hide()
            if not self._hid_once:
                self._hid_once = True
                self.tray.showMessage("Scan Center is still running",
                                      "Click the icon next to the clock to scan. Right-click it and choose Quit to stop.",
                                      QSystemTrayIcon.Information, 5000)
            return
        self._really_quit = False
        if self.pages:
            from PySide6.QtWidgets import QMessageBox
            r = QMessageBox.question(self, "Unsaved pages",
                                     f"{len(self.pages)} scanned pages haven't been saved yet. Save them before closing?",
                                     QMessageBox.Save | QMessageBox.Discard | QMessageBox.Cancel, QMessageBox.Save)
            if r == QMessageBox.Cancel:
                e.ignore(); return
            if r == QMessageBox.Save:
                e.ignore(); self.finish_session(); return
        if self.job:
            self.job.cancel(); self.job.wait(3000)
        self.s.geometry = bytes(self.saveGeometry().toBase64()).decode()
        self.save_settings()
        self.tray.hide()
        super().closeEvent(e)
        QApplication.instance().quit()


def _short(path: str, n: int = 30) -> str:
    return path if len(path) <= n else "…" + path[-(n - 1):]
