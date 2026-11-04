"""Colours and the Qt style sheet, in light and dark variants."""
from __future__ import annotations

from PySide6.QtCore import Qt
from PySide6.QtGui import QColor, QGuiApplication, QPalette

LIGHT = dict(bg="#e9edf3", surface="#ffffff", surface2="#f3f5f9", line="#d6dce6", fg="#17202c",
             muted="#5a6577", accent="#2f5bd3", accent_soft="#e3eafc", accent_fg="#ffffff",
             lamp="#f2a33a", ok="#1d8a5a", err="#c4352d", platen="#28303d")
DARK = dict(bg="#0e1218", surface="#161b24", surface2="#1c2230", line="#2a3241", fg="#e5e9f0",
            muted="#97a2b4", accent="#7196ff", accent_soft="#1f2b48", accent_fg="#0c1322",
            lamp="#f5b04f", ok="#45b985", err="#ef6a5f", platen="#0a0d12")


def is_dark(choice: str) -> bool:
    if choice == "dark":
        return True
    if choice == "light":
        return False
    try:
        return QGuiApplication.styleHints().colorScheme() == Qt.ColorScheme.Dark
    except AttributeError:
        return False


def palette_for(choice: str) -> dict:
    return DARK if is_dark(choice) else LIGHT


def apply(app, choice: str) -> dict:
    t = palette_for(choice)
    pal = QPalette()
    pal.setColor(QPalette.Window, QColor(t["bg"]))
    pal.setColor(QPalette.WindowText, QColor(t["fg"]))
    pal.setColor(QPalette.Base, QColor(t["surface"]))
    pal.setColor(QPalette.AlternateBase, QColor(t["surface2"]))
    pal.setColor(QPalette.Text, QColor(t["fg"]))
    pal.setColor(QPalette.Button, QColor(t["surface"]))
    pal.setColor(QPalette.ButtonText, QColor(t["fg"]))
    pal.setColor(QPalette.Highlight, QColor(t["accent"]))
    pal.setColor(QPalette.HighlightedText, QColor(t["accent_fg"]))
    pal.setColor(QPalette.PlaceholderText, QColor(t["muted"]))
    pal.setColor(QPalette.ToolTipBase, QColor(t["surface"]))
    pal.setColor(QPalette.ToolTipText, QColor(t["fg"]))
    for role in (QPalette.WindowText, QPalette.Text, QPalette.ButtonText):
        pal.setColor(QPalette.Disabled, role, QColor(t["muted"]))
    app.setPalette(pal)
    app.setStyleSheet(QSS.format(**t))
    return t


QSS = """
QWidget {{ font-family: "Segoe UI Variable Text", "Segoe UI", sans-serif; font-size: 10pt; color: {fg}; }}
QMainWindow, QDialog {{ background: {bg}; }}
#Header {{ background: {surface2}; border-bottom: 1px solid {line}; }}
#AppTitle {{ font-family: "Segoe UI Variable Display", "Segoe UI"; font-size: 13pt; font-weight: 700; }}
#Device {{ color: {muted}; font-family: Consolas, monospace; font-size: 9pt; }}
#Nav {{ background: {surface}; border-right: 1px solid {line}; }}
#Stage {{ background: {surface2}; }}
#Settings {{ background: {surface}; border-left: 1px solid {line}; }}
#SettingsScroll {{ background: {surface}; border: none; border-left: 1px solid {line}; }}
#Eyebrow {{ color: {muted}; font-size: 8pt; font-weight: 600; letter-spacing: 1px; }}
#Muted {{ color: {muted}; }}
#Warn {{ color: {lamp}; }}
#NamePreview {{ color: {accent}; font-family: Consolas, monospace; font-size: 9pt; }}
#Hint {{ color: {muted}; }}

QPushButton {{ background: {surface}; border: 1px solid {line}; border-radius: 6px; padding: 6px 12px; }}
QPushButton:hover {{ border-color: {accent}; }}
QPushButton:disabled {{ color: {muted}; background: {surface2}; }}
QPushButton#Primary {{ background: {accent}; color: {accent_fg}; border-color: {accent}; font-weight: 600; padding: 8px 20px; }}
QPushButton#Primary:disabled {{ background: {line}; border-color: {line}; color: {muted}; }}
QPushButton#Token {{ font-family: Consolas, monospace; font-size: 9pt; padding: 2px 6px; border-radius: 4px; background: {surface2}; }}
QPushButton#Profile {{ text-align: left; border: 1px solid transparent; background: transparent; padding: 9px 10px; border-radius: 8px; }}
QPushButton#Profile:hover {{ background: {surface2}; }}
QPushButton#Profile:checked {{ background: {accent_soft}; border-color: {accent}; color: {accent}; }}

QLineEdit, QComboBox, QSpinBox {{ background: {surface}; border: 1px solid {line}; border-radius: 6px; padding: 5px 8px; }}
QLineEdit:focus, QComboBox:focus, QSpinBox:focus {{ border-color: {accent}; }}
QLineEdit:read-only {{ background: {surface2}; font-family: Consolas, monospace; font-size: 9pt; }}
QLineEdit:disabled, QComboBox:disabled, QSpinBox:disabled {{ color: {muted}; background: {surface2}; border-color: {surface2}; }}
QCheckBox:disabled, QLabel:disabled {{ color: {muted}; }}
QComboBox::drop-down {{ border: none; width: 20px; }}
QComboBox QAbstractItemView {{ background: {surface}; border: 1px solid {line}; selection-background-color: {accent_soft}; selection-color: {fg}; }}
QCheckBox {{ spacing: 8px; }}
QSlider::groove:horizontal {{ height: 4px; background: {line}; border-radius: 2px; }}
QSlider::sub-page:horizontal {{ background: {accent}; border-radius: 2px; }}
QSlider::handle:horizontal {{ background: {accent}; width: 14px; margin: -6px 0; border-radius: 7px; }}
QSlider::groove:horizontal:disabled, QSlider::sub-page:horizontal:disabled {{ background: {line}; }}
QSlider::handle:horizontal:disabled {{ background: {muted}; }}

QProgressBar {{ background: {line}; border: none; border-radius: 3px; max-height: 6px; min-height: 6px; }}
QProgressBar::chunk {{ background: {accent}; border-radius: 3px; }}
QStatusBar {{ background: {surface2}; border-top: 1px solid {line}; }}
QStatusBar QLabel {{ padding: 0 6px; }}

QTabWidget::pane {{ border: 1px solid {line}; background: {surface}; border-radius: 6px; top: -1px; }}
QTabBar::tab {{ background: transparent; padding: 7px 14px; color: {muted}; border-bottom: 2px solid transparent; }}
QTabBar::tab:selected {{ color: {accent}; border-bottom-color: {accent}; }}
QTreeWidget, QListWidget, QPlainTextEdit {{ background: {surface}; border: none; }}
QTreeWidget::item {{ padding: 4px 2px; }}
QTreeWidget::item:selected {{ background: {accent_soft}; color: {fg}; }}
QHeaderView::section {{ background: {surface}; color: {muted}; border: none; border-bottom: 1px solid {line}; padding: 5px 6px; font-size: 8pt; font-weight: 600; }}
QPlainTextEdit {{ font-family: Consolas, monospace; font-size: 9pt; }}
#Tray {{ background: {surface}; border-top: 1px solid {line}; }}
#MoreBar {{ background: {surface}; border: 1px solid {accent}; border-radius: 10px; }}
#ErrorBar {{ background: {surface}; border: 1px solid {err}; border-radius: 10px; }}
#ErrorTitle {{ color: {err}; font-weight: 600; font-size: 11pt; }}
#MoreTitle {{ font-weight: 600; font-size: 11pt; }}
QScrollArea {{ border: none; }}
QSplitter::handle {{ background: {line}; }}
"""
