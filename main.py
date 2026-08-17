# -*- coding: utf-8 -*-
"""入口：启动悬浮行情小工具。

用法:
    python main.py              # 正常运行
    python main.py --snapshot x.png   # 测试模式：启动后截图保存并退出

启动时会在项目目录写入 .floatquote.pid（进程 ID），退出时自动清理，
供桌面启停脚本精确定位进程。
"""
import os
import sys

from PyQt6.QtCore import QTimer
from PyQt6.QtWidgets import QApplication

from config import app_dir
from widgets import FloatQuoteWindow

# 打包成 exe 后指向 exe 所在目录（见 config.app_dir）
PID_FILE = app_dir() / ".floatquote.pid"


def _write_pid():
    try:
        PID_FILE.write_text(str(os.getpid()), encoding="utf-8")
    except OSError:
        pass


def _remove_pid():
    try:
        PID_FILE.unlink(missing_ok=True)
    except OSError:
        pass


def main() -> int:
    app = QApplication(sys.argv)
    app.setApplicationName("FloatQuote")

    win = FloatQuoteWindow()
    win.show()

    if "--snapshot" in sys.argv:
        idx = sys.argv.index("--snapshot")
        out = sys.argv[idx + 1] if idx + 1 < len(sys.argv) else "snapshot.png"

        def snap():
            win.grab().save("snapshot_collapsed.png")
            win._set_expanded(True)

            def grab_expanded():
                win.grab().save(out)
                print(f"snapshots saved -> snapshot_collapsed.png, {out}")
                app.quit()

            QTimer.singleShot(400, grab_expanded)

        QTimer.singleShot(4500, snap)

    _write_pid()
    try:
        return app.exec()
    finally:
        _remove_pid()


if __name__ == "__main__":
    sys.exit(main())
