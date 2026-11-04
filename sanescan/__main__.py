"""Start SaneTwain Scan Center:  python -m sanescan [--tray]"""
import getpass
import sys

from PySide6.QtNetwork import QLocalServer, QLocalSocket
from PySide6.QtWidgets import QApplication

from . import config
from .main_window import MainWindow

SERVER = f"SaneTwainScanCenter-{getpass.getuser()}"


def _already_running() -> bool:
    """If another copy is running, ask it to show its window and return True."""
    s = QLocalSocket()
    s.connectToServer(SERVER)
    if s.waitForConnected(300):
        s.write(b"show")
        s.flush()
        s.waitForBytesWritten(300)
        s.disconnectFromServer()
        return True
    return False


def main():
    if sys.platform == "win32":
        try:
            import ctypes
            ctypes.windll.shell32.SetCurrentProcessExplicitAppUserModelID("SaneTwain.ScanCenter")
        except Exception:
            pass
    app = QApplication(sys.argv)
    app.setApplicationName("SaneTwain Scan Center")
    app.setStyle("Fusion")
    if _already_running():
        return
    settings = config.load()
    app.setQuitOnLastWindowClosed(False)  # the window decides: quit, or keep waiting in the tray
    win = MainWindow(settings)

    QLocalServer.removeServer(SERVER)
    server = QLocalServer(app)
    server.listen(SERVER)

    def on_conn():
        c = server.nextPendingConnection()
        if c:
            c.readyRead.connect(lambda: (c.readAll(), win.bring_to_front()))
    server.newConnection.connect(on_conn)

    if "--tray" in sys.argv and settings.start_with_windows:
        win.log("Started with Windows; waiting in the notification area")
    else:
        win.show()
    sys.exit(app.exec())


if __name__ == "__main__":
    main()
