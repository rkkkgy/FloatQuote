# -*- coding: utf-8 -*-
"""入口：启动悬浮行情小工具。

用法:
    python main.py              # 正常运行
    python main.py --snapshot x.png   # 测试模式：启动后截图保存并退出
"""
import sys

from PyQt6.QtCore import QTimer
from PyQt6.QtWidgets import QApplication

from widgets import FloatQuoteWindow


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

    return app.exec()


if __name__ == "__main__":
    sys.exit(main())
