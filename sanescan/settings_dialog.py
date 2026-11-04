"""App settings dialog (applies to every scan button)."""
from __future__ import annotations

import copy

from PySide6.QtCore import Qt
from PySide6.QtWidgets import (QCheckBox, QComboBox, QDialog, QDialogButtonBox, QFileDialog,
                               QFormLayout, QHBoxLayout, QLabel, QLineEdit, QPushButton,
                               QSlider, QSpinBox, QTabWidget, QVBoxLayout, QWidget)

from .config import INI_PATH, AppSettings
from . import system
from .saving import find_tesseract
from .workers import ProbeJob

_LIVE: list = []  # keep running jobs alive if the dialog closes first

OCR_LANGS = [("eng", "English"), ("ita", "Italian"), ("deu", "German"), ("fra", "French"),
             ("spa", "Spanish"), ("eng+ita", "English + Italian"), ("eng+deu", "English + German")]


def _row(*widgets, stretch_first=True):
    w = QWidget()
    h = QHBoxLayout(w)
    h.setContentsMargins(0, 0, 0, 0)
    h.setSpacing(6)
    for i, x in enumerate(widgets):
        h.addWidget(x, 1 if (i == 0 and stretch_first) else 0)
    return w


class SettingsDialog(QDialog):
    def __init__(self, settings: AppSettings, resolutions: list[int], parent=None):
        super().__init__(parent)
        self.setWindowTitle("App settings")
        self.setMinimumWidth(520)
        self.s = copy.deepcopy(settings)

        lay = QVBoxLayout(self)
        note = QLabel(f"These apply to every scan button. They're stored in {INI_PATH}, "
                      "so SaneTwain keeps working with the same connection.")
        note.setObjectName("Muted")
        note.setWordWrap(True)
        lay.addWidget(note)
        tabs = QTabWidget()
        lay.addWidget(tabs, 1)

        # --- Connection ---
        f = QFormLayout(); w = QWidget(); w.setLayout(f)
        self.host = QLineEdit(self.s.host)
        self.port = QSpinBox(); self.port.setRange(1, 65535); self.port.setValue(self.s.port)
        self.user = QLineEdit(self.s.user)
        self.dev = QComboBox(); self.dev.setEditable(True)
        if self.s.device:
            self.dev.addItem(self.s.device)
        self.find = QPushButton("Find scanners")
        self.getdev = QCheckBox("Look for scanners every time the app starts"); self.getdev.setChecked(self.s.get_devices)
        self.test = QPushButton("Test connection")
        self.testres = QLabel(""); self.testres.setWordWrap(True)
        f.addRow("saned server", _row(self.host, QLabel("Port"), self.port))
        f.addRow("User name", self.user)
        f.addRow("Scanner", _row(self.dev, self.find))
        f.addRow("", self.getdev)
        f.addRow("", _row(self.test, self.testres, stretch_first=False))
        self.find.clicked.connect(lambda: self._probe(list_only=True))
        self.test.clicked.connect(lambda: self._probe(list_only=False))
        tabs.addTab(w, "Connection")

        # --- Preview ---
        f = QFormLayout(); w = QWidget(); w.setLayout(f)
        self.pres = QComboBox()
        for r in sorted(set(resolutions + [self.s.preview_res])):
            self.pres.addItem(f"{r} dpi", r)
        self.pres.setCurrentIndex(max(0, self.pres.findData(self.s.preview_res)))
        self.pmin = QCheckBox("Always use the scanner's lowest resolution (fastest)")
        self.pmin.setChecked(self.s.preview_use_min)
        self.pmin.toggled.connect(lambda v: self.pres.setEnabled(not v)); self.pres.setEnabled(not self.s.preview_use_min)
        self.pauto = QCheckBox("Run a preview as soon as the app opens"); self.pauto.setChecked(self.s.preview_autostart)
        f.addRow("Preview resolution", self.pres)
        f.addRow("", self.pmin)
        f.addRow("", self.pauto)
        tabs.addTab(w, "Preview")

        # --- Saving ---
        f = QFormLayout(); w = QWidget(); w.setLayout(f)
        self.root = QLineEdit(self.s.default_folder)
        rb = QPushButton("Browse…"); rb.clicked.connect(self._browse_root)
        self.jq = QSlider(Qt.Horizontal); self.jq.setRange(50, 100); self.jq.setSingleStep(5); self.jq.setValue(self.s.jpeg_quality)
        self.jql = QLabel(str(self.s.jpeg_quality)); self.jq.valueChanged.connect(lambda v: self.jql.setText(str(v)))
        self.ocr = QComboBox()
        for code, name in OCR_LANGS:
            self.ocr.addItem(name, code)
        if self.ocr.findData(self.s.ocr_lang) < 0:
            self.ocr.addItem(self.s.ocr_lang, self.s.ocr_lang)
        self.ocr.setCurrentIndex(self.ocr.findData(self.s.ocr_lang))
        self.tess = QLineEdit(self.s.tesseract); self.tess.setPlaceholderText("Found automatically if installed")
        tb = QPushButton("Browse…"); tb.clicked.connect(self._browse_tess)
        self.tessres = QLabel(); self.tessres.setWordWrap(True); self.tess.textChanged.connect(self._tess_status)
        self.sound = QCheckBox("Play a sound when a file is saved"); self.sound.setChecked(self.s.sound)
        self.notify = QCheckBox("Show a Windows notification when a file is saved"); self.notify.setChecked(self.s.notify)
        f.addRow("Default folder for scans", _row(self.root, rb))
        f.addRow("JPEG quality", _row(self.jq, self.jql))
        f.addRow("OCR language", self.ocr)
        f.addRow("Tesseract OCR", _row(self.tess, tb))
        f.addRow("", self.tessres)
        f.addRow("", self.sound)
        f.addRow("", self.notify)
        tabs.addTab(w, "Saving")
        self._tess_status()

        # --- General ---
        f = QFormLayout(); w = QWidget(); w.setLayout(f)
        self.top = QCheckBox("Keep the window on top of other windows"); self.top.setChecked(self.s.topmost)
        self.esc = QCheckBox("Ignore the Esc key (otherwise Esc cancels a running scan)"); self.esc.setChecked(self.s.disable_esc)
        self.hide = QCheckBox("Hide settings the scanner doesn't support in the chosen scan type")
        self.hide.setChecked(self.s.hide_disabled)
        self.startup = QCheckBox("Start with Windows and wait in the notification area")
        self.startup.setChecked(system.startup_enabled())
        self.startup.setToolTip("While this is on, closing the window keeps Scan Center running next to the clock. "
                                "Use Quit in its menu to stop it.")
        self.theme = QComboBox()
        for k, v in (("system", "Match Windows"), ("light", "Light"), ("dark", "Dark")):
            self.theme.addItem(v, k)
        self.theme.setCurrentIndex(max(0, self.theme.findData(self.s.theme)))
        f.addRow("", self.top)
        f.addRow("", self.esc)
        f.addRow("", self.hide)
        f.addRow("", self.startup)
        f.addRow("Theme", self.theme)
        tabs.addTab(w, "General")

        # --- Printer ---
        f = QFormLayout(); w = QWidget(); w.setLayout(f)
        self.prn = QComboBox()
        self.prn.addItem(f"Windows default ({system.default_printer_name() or 'none'})", "")
        for n in system.printer_names():
            self.prn.addItem(n, n)
        if self.s.printer and self.prn.findData(self.s.printer) < 0:
            self.prn.addItem(f"{self.s.printer} (not installed)", self.s.printer)
        self.prn.setCurrentIndex(max(0, self.prn.findData(self.s.printer)))
        self.psetup = QCheckBox("Show printer options before printing"); self.psetup.setChecked(self.s.print_setup)
        self.pdelay = QCheckBox("Wait until all pages are scanned, then print them together")
        self.pdelay.setChecked(self.s.print_delayed)
        pn = QLabel("Used by the Copy button and by any button set to \"Print it\" when the file is saved. "
                    "Without waiting, each page prints as soon as it's scanned.")
        pn.setObjectName("Muted"); pn.setWordWrap(True)
        f.addRow("Printer", self.prn)
        f.addRow("", self.psetup)
        f.addRow("", self.pdelay)
        f.addRow("", pn)
        tabs.addTab(w, "Printer")

        bb = QDialogButtonBox(QDialogButtonBox.Save | QDialogButtonBox.Cancel)
        bb.button(QDialogButtonBox.Save).setText("Save settings")
        bb.button(QDialogButtonBox.Save).setObjectName("Primary")
        bb.accepted.connect(self._accept)
        bb.rejected.connect(self.reject)
        lay.addWidget(bb)

    def _tess_status(self):
        t = find_tesseract(self.tess.text().strip())
        self.tessres.setText(f"Using {t}" if t else
                             "Not installed. Scan to OCR will save normal PDFs until Tesseract is installed "
                             "(github.com/UB-Mannheim/tesseract/wiki).")
        self.tessres.setObjectName("Muted" if t else "Warn")
        self.tessres.style().unpolish(self.tessres); self.tessres.style().polish(self.tessres)

    def _browse_root(self):
        d = QFileDialog.getExistingDirectory(self, "Default folder for scans", self.root.text())
        if d:
            self.root.setText(d.replace("/", "\\"))

    def _browse_tess(self):
        fn, _ = QFileDialog.getOpenFileName(self, "Find tesseract.exe", r"C:\Program Files", "tesseract.exe (tesseract.exe)")
        if fn:
            self.tess.setText(fn.replace("/", "\\"))

    def _probe(self, list_only):
        self.find.setEnabled(False); self.test.setEnabled(False)
        self.testres.setObjectName("Muted"); self.testres.setText(f"Connecting to {self.host.text()}:{self.port.value()}…")
        job = ProbeJob(self.host.text().strip(), self.port.value(), self.user.text().strip(),
                       self.dev.currentText().strip(), list_only=list_only)
        _LIVE.append(job)
        job.finished.connect(lambda: _LIVE.remove(job) if job in _LIVE else None)

        def ok(info):
            cur = self.dev.currentText()
            self.dev.clear()
            for d in info.devices:
                self.dev.addItem(d.name)
                self.dev.setItemData(self.dev.count() - 1, d.label, Qt.ToolTipRole)
            if cur and self.dev.findText(cur) >= 0:
                self.dev.setCurrentText(cur)
            names = ", ".join(d.label for d in info.devices) or "none"
            self.testres.setText(f"Connected (saned {info.server_version}). Scanners: {names}")
            self._style(self.testres, "Muted")

        def bad(msg):
            self.testres.setText(msg)
            self._style(self.testres, "Warn")

        job.done.connect(ok)
        job.failed.connect(bad)
        job.finished.connect(lambda: (self.find.setEnabled(True), self.test.setEnabled(True)))
        job.start()

    @staticmethod
    def _style(w, name):
        w.setObjectName(name); w.style().unpolish(w); w.style().polish(w)

    def _accept(self):
        s = self.s
        s.host = self.host.text().strip() or s.host
        s.port = self.port.value()
        s.user = self.user.text().strip()
        s.device = self.dev.currentText().strip()
        s.get_devices = self.getdev.isChecked()
        s.preview_res = int(self.pres.currentData() or s.preview_res)
        s.preview_use_min = self.pmin.isChecked()
        s.preview_autostart = self.pauto.isChecked()
        s.default_folder = self.root.text().strip() or s.default_folder
        s.jpeg_quality = self.jq.value()
        s.ocr_lang = self.ocr.currentData()
        s.tesseract = self.tess.text().strip()
        s.sound = self.sound.isChecked()
        s.notify = self.notify.isChecked()
        s.topmost = self.top.isChecked()
        s.disable_esc = self.esc.isChecked()
        s.hide_disabled = self.hide.isChecked()
        s.theme = self.theme.currentData()
        s.printer = self.prn.currentData() or ""
        s.print_setup = self.psetup.isChecked()
        s.print_delayed = self.pdelay.isChecked()
        s.start_with_windows = self.startup.isChecked()
        try:
            system.set_startup(s.start_with_windows)
        except OSError as e:
            from PySide6.QtWidgets import QMessageBox
            QMessageBox.warning(self, "Start with Windows", f"Windows didn't allow the change: {e}")
            s.start_with_windows = system.startup_enabled()
        self.accept()

