# -*- coding: utf-8 -*-
"""悬浮窗主窗口：收起/展开两态、置顶、拖拽、悬停、滚轮切换、右键菜单。"""
import sys
import time

from PyQt6.QtCore import (QAbstractAnimation, QObject, QParallelAnimationGroup,
                          QPoint, QPointF, QPropertyAnimation, QRect, QRectF,
                          QRunnable, Qt, QThreadPool, QTimer, QEasingCurve,
                          pyqtSignal)
from PyQt6.QtGui import (QColor, QCursor, QFont, QIcon, QPainter, QPainterPath,
                         QPen, QPixmap, QPolygonF)
from PyQt6.QtWidgets import (QApplication, QDialog, QGraphicsOpacityEffect,
                             QGridLayout, QHBoxLayout, QInputDialog, QLabel,
                             QLineEdit, QListWidget, QListWidgetItem, QMenu,
                             QMessageBox, QPushButton, QSystemTrayIcon,
                             QVBoxLayout, QWidget)

import config
import quotes
from chart import MinuteChart as MinuteChartWidget

# ---------------------------------------------------------------- 主题色
BG_MAIN = QColor(30, 34, 45, 245)
BORDER = QColor(72, 80, 98)
TEXT_MAIN = QColor(232, 235, 242)
TEXT_SUB = QColor(150, 157, 170)
RED = QColor(228, 77, 67)
GREEN = QColor(40, 178, 110)
GRAY = QColor(150, 157, 170)
YELLOW = QColor(245, 194, 66)
MONO = "Consolas"
UI_FONT = "Microsoft YaHei UI"

COLOR_HEX = {"red": "#E44D43", "green": "#28B26E", "gray": "#969DAA"}


# ---------------------------------------------------------------- 托盘图标
def _make_tray_icon(color_key: str = "gray") -> QIcon:
    """自绘托盘图标：深色圆角底 + 迷你走势线（涨升/跌降，随行情变色）。"""
    pm = QPixmap(64, 64)
    pm.fill(Qt.GlobalColor.transparent)
    p = QPainter(pm)
    p.setRenderHint(QPainter.RenderHint.Antialiasing)
    path = QPainterPath()
    path.addRoundedRect(QRectF(2, 2, 60, 60), 14, 14)
    p.fillPath(path, BG_MAIN)
    p.setPen(QPen(BORDER, 2))
    p.drawPath(path)

    color = {"red": RED, "green": GREEN, "gray": GRAY}.get(color_key, GRAY)
    pen = QPen(color, 4)
    pen.setCapStyle(Qt.PenCapStyle.RoundCap)
    pen.setJoinStyle(Qt.PenJoinStyle.RoundJoin)
    p.setPen(pen)
    if color_key == "red":      # 涨：向上的走势
        pts = [QPointF(8, 46), QPointF(20, 40), QPointF(30, 42),
               QPointF(40, 28), QPointF(50, 32), QPointF(56, 20)]
    elif color_key == "green":  # 跌：向下的走势
        pts = [QPointF(8, 20), QPointF(20, 28), QPointF(30, 26),
               QPointF(40, 40), QPointF(50, 36), QPointF(56, 48)]
    else:                       # 平
        pts = [QPointF(8, 30), QPointF(20, 34), QPointF(30, 30),
               QPointF(40, 34), QPointF(50, 30), QPointF(56, 32)]
    p.drawPolyline(QPolygonF(pts))
    p.end()
    return QIcon(pm)


# ---------------------------------------------------------------- 后台取数任务
class WorkerSignals(QObject):
    quotes = pyqtSignal(dict, int)              # (quotes_dict, seq)
    minute = pyqtSignal(str, list, str, int)    # (code, points, date, seq) 分时数据
    minute_image = pyqtSignal(str, bytes, int)  # (code, gif_bytes, seq) 降级分时图
    degraded = pyqtSignal(str, int)             # (code, seq) 数据源失败但降级成功
    error = pyqtSignal(str)


class FetchQuotesTask(QRunnable):
    def __init__(self, codes, signals, seq):
        super().__init__()
        self.codes, self.signals, self.seq = codes, signals, seq

    def run(self):
        try:
            qs = quotes.fetch_quotes(self.codes)
            self.signals.quotes.emit(qs, self.seq)
        except Exception as e:  # noqa: BLE001
            self.signals.error.emit(f"报价刷新失败: {e}")


class FetchMinuteTask(QRunnable):
    def __init__(self, code, signals, seq):
        super().__init__()
        self.code, self.signals, self.seq = code, signals, seq

    def run(self):
        try:
            pts, date = quotes.fetch_minute(self.code)
            self.signals.minute.emit(self.code, pts, date, self.seq)
            return
        except Exception as e:  # noqa: BLE001
            last_err = e
        # 腾讯数据源失败 -> 降级用新浪分时图
        try:
            img = quotes.fetch_minute_image(self.code)
            if img:
                self.signals.minute_image.emit(self.code, img, self.seq)
                self.signals.degraded.emit(self.code, self.seq)  # 计入数据源退避
                return
            last_err = RuntimeError("新浪分时图返回为空")
        except Exception as e2:  # noqa: BLE001
            last_err = e2
        self.signals.error.emit(f"分时数据失败: {last_err}")


# ---------------------------------------------------------------- 可动画标签
class _AnimatedLabel(QWidget):
    """布局容器 + 内部可自由定位的 QLabel，支持股票切换动画。"""

    def __init__(self, font, color_style, max_w=None, parent=None):
        super().__init__(parent)
        self._max_w = max_w
        self._label = QLabel("--", self)
        self._label.setFont(font)
        self._label.setStyleSheet(color_style)
        self._effect = QGraphicsOpacityEffect(self._label)
        self._label.setGraphicsEffect(self._effect)
        self._anim = None
        # 鼠标事件穿透到窗口（拖拽/缩放/滚轮不受标签阻挡）
        for wdg in (self, self._label):
            wdg.setAttribute(Qt.WidgetAttribute.WA_TransparentForMouseEvents)
        self._update_size()

    def _update_size(self):
        self._label.adjustSize()
        w = self._label.width()
        if self._max_w:
            w = min(w, self._max_w)
        self.setFixedSize(w, self._label.height())
        self._label.move(0, 0)

    def text(self) -> str:
        return self._label.text()

    def set_text(self, text, color_style=None):
        self._label.setText(text)
        if color_style:
            self._label.setStyleSheet(color_style)
        self._update_size()

    def _run(self, anim):
        if self._anim is not None and self._anim.state() == QAbstractAnimation.State.Running:
            self._anim.stop()
        self._anim = anim
        anim.start()

    def reset(self):
        self._label.move(0, 0)
        self._effect.setOpacity(1.0)

    def play(self, effect: str, direction: int = 1):
        """切换动画。effect: fade/slide_h/slide_v/pulse/off；direction 控制滑入方向。"""
        if effect == "off":
            self.reset()
            return
        if effect == "fade":
            self._label.move(0, 0)
            a = QPropertyAnimation(self._effect, b"opacity", self)
            a.setDuration(280)
            a.setEasingCurve(QEasingCurve.Type.OutCubic)
            a.setStartValue(0.0)
            a.setEndValue(1.0)
            self._effect.setOpacity(0.0)
            self._run(a)
        elif effect in ("slide_h", "slide_v"):
            group = QParallelAnimationGroup(self)
            a1 = QPropertyAnimation(self._effect, b"opacity", group)
            a1.setDuration(320)
            a1.setEasingCurve(QEasingCurve.Type.OutCubic)
            a1.setStartValue(0.0)
            a1.setEndValue(1.0)
            dx, dy = (direction * 18, 0) if effect == "slide_h" else (0, direction * 14)
            start = QPoint(dx, dy)
            a2 = QPropertyAnimation(self._label, b"pos", group)
            a2.setDuration(320)
            a2.setEasingCurve(QEasingCurve.Type.OutCubic)
            a2.setStartValue(start)
            a2.setEndValue(QPoint(0, 0))
            self._label.move(start.x(), start.y())
            self._effect.setOpacity(0.0)
            self._run(group)
        elif effect == "pulse":
            self._label.move(0, 0)
            a = QPropertyAnimation(self._effect, b"opacity", self)
            a.setDuration(420)
            a.setStartValue(0.0)
            a.setKeyValueAt(0.35, 1.0)
            a.setKeyValueAt(0.65, 0.5)
            a.setEndValue(1.0)
            self._effect.setOpacity(0.0)
            self._run(a)
        else:
            self.reset()


# ---------------------------------------------------------------- 悬浮条行
class _StockRow(QWidget):
    """悬浮条中的一行：名称 + 价格 + 涨跌幅；悬停高亮，点击由窗口统一识别。"""

    ROW_H = 17
    HOVER_BG = QColor(46, 52, 68, 210)
    HIGHLIGHT_BG = QColor(56, 104, 80, 220)   # 自动轮播"当前"高亮（区别于悬停）

    def __init__(self, window, parent=None):
        super().__init__(parent)
        self._window = window
        self.code = None
        self._hovered = False
        self._highlight = False
        self.setCursor(Qt.CursorShape.PointingHandCursor)
        plain = "background: transparent;"
        self.name = _AnimatedLabel(
            QFont(UI_FONT, 8, QFont.Weight.Bold),
            f"color: {TEXT_MAIN.name()}; background: transparent;", max_w=66)
        self.price = _AnimatedLabel(QFont(UI_FONT, 9, QFont.Weight.Bold), plain)
        self.change = _AnimatedLabel(QFont(UI_FONT, 8, QFont.Weight.Bold), plain)
        lay = QHBoxLayout(self)
        lay.setContentsMargins(4, 0, 4, 0)
        lay.setSpacing(3)
        lay.addWidget(self.name)
        lay.addStretch(1)
        lay.addWidget(self.price)
        lay.addWidget(self.change)
        self.setFixedHeight(self.ROW_H)
        for wdg in (self.name, self.price, self.change):
            wdg.setAttribute(Qt.WidgetAttribute.WA_TransparentForMouseEvents)

    def enterEvent(self, _e):
        self._hovered = True
        self.update()

    def leaveEvent(self, _e):
        self._hovered = False
        self.update()

    def paintEvent(self, _e):
        if self._highlight:
            p = QPainter(self)
            p.fillRect(self.rect(), self.HIGHLIGHT_BG)
        elif self._hovered:
            p = QPainter(self)
            p.fillRect(self.rect(), self.HOVER_BG)

    def set_highlight(self, on: bool):
        if self._highlight != on:
            self._highlight = on
            self.update()

    def set_stock(self, code, name, price_text, change_text, style):
        self.code = code
        self.name.set_text(name)
        self.price.set_text(price_text, style)
        self.change.set_text(change_text, style)

    def play(self, effect):
        self.name.play(effect, -1)
        self.price.play(effect, 1)
        self.change.play(effect, 1)

    def clear(self):
        self.code = None
        self.name.set_text("")
        self.price.set_text("")
        self.change.set_text("")


# ---------------------------------------------------------------- 自选股管理对话框
class StockEditDialog(QDialog):
    """自选股管理：列表显示「名称 代码」，支持按名称/代码搜索添加、排序、删除。"""

    DARK_QSS = """
        QDialog { background: #232733; color: #E8EBF2; }
        QLineEdit { background: #1B1F2A; color: #E8EBF2; border: 1px solid #3A4150;
                    border-radius: 4px; padding: 4px 6px; }
        QListWidget { background: #1B1F2A; color: #E8EBF2; border: 1px solid #3A4150;
                      border-radius: 4px; }
        QListWidget::item { padding: 4px 8px; }
        QListWidget::item:selected { background: #3A4150; }
        QListWidget::item:hover { background: #2C3140; }
        QPushButton { background: #2C3140; color: #E8EBF2; border: 1px solid #3A4150;
                      border-radius: 4px; padding: 4px 14px; }
        QPushButton:hover { background: #3A4150; }
        QPushButton:disabled { color: #5A6170; }
        QPushButton:default { background: #3E6B4F; }
        QMessageBox { background: #232733; }
    """

    def __init__(self, codes, names, parent=None):
        super().__init__(parent)
        self.setWindowTitle("自选股管理")
        self.setMinimumWidth(400)
        self.setMinimumHeight(360)
        self.setStyleSheet(self.DARK_QSS)
        self._codes = list(codes)
        self._names = dict(names)   # code -> name
        self._searching = False

        self.search_edit = QLineEdit(self)
        self.search_edit.setPlaceholderText("输入代码或名称添加，如 sh600519 或「茅台」")
        self.add_btn = QPushButton("添加", self)
        self.add_btn.setToolTip("按代码或名称搜索添加")
        search_row = QHBoxLayout()
        search_row.addWidget(self.search_edit, 1)
        search_row.addWidget(self.add_btn)

        self.list_widget = QListWidget(self)
        self.list_widget.setAlternatingRowColors(False)

        self.up_btn = QPushButton("上移", self)
        self.down_btn = QPushButton("下移", self)
        self.del_btn = QPushButton("删除", self)
        for b in (self.up_btn, self.down_btn, self.del_btn):
            b.setEnabled(False)
        btn_row = QHBoxLayout()
        btn_row.addWidget(self.up_btn)
        btn_row.addWidget(self.down_btn)
        btn_row.addWidget(self.del_btn)
        btn_row.addStretch(1)
        self.hint_label = QLabel("提示：回车可直接搜索添加 · 双击列表项确认", self)
        self.hint_label.setStyleSheet("color: #9AA0AA; background: transparent;")

        self.ok_btn = QPushButton("确定", self)
        self.ok_btn.setDefault(True)
        cancel_btn = QPushButton("取消", self)
        bottom = QHBoxLayout()
        bottom.addStretch(1)
        bottom.addWidget(self.ok_btn)
        bottom.addWidget(cancel_btn)

        layout = QVBoxLayout(self)
        layout.addLayout(search_row)
        layout.addWidget(self.list_widget, 1)
        layout.addLayout(btn_row)
        layout.addWidget(self.hint_label)
        layout.addLayout(bottom)

        self.add_btn.clicked.connect(self._add_from_search)
        self.search_edit.returnPressed.connect(self._add_from_search)
        self.up_btn.clicked.connect(self._move_up)
        self.down_btn.clicked.connect(self._move_down)
        self.del_btn.clicked.connect(self._delete_selected)
        self.ok_btn.clicked.connect(self.accept)
        cancel_btn.clicked.connect(self.reject)
        self.list_widget.currentRowChanged.connect(self._update_buttons)
        self.list_widget.itemDoubleClicked.connect(lambda _item: self.accept())

        self._fetch_missing_names()
        self._refresh_list()

    # ---- 数据
    def codes(self) -> list:
        return list(self._codes)

    def name_of(self, code: str):
        return self._names.get(code)

    def _fetch_missing_names(self):
        """为缺少名称的股票补取名称（阻塞很短，列表更友好）。"""
        missing = [c for c in self._codes if not self._names.get(c)]
        if not missing:
            return
        try:
            QApplication.setOverrideCursor(Qt.CursorShape.WaitCursor)
            qs = quotes.fetch_quotes(missing)
            for c, q in qs.items():
                if q and q.name and q.name != "--":
                    self._names[c] = q.name
        except Exception:
            pass
        finally:
            QApplication.restoreOverrideCursor()

    # ---- 列表
    def _refresh_list(self):
        self.list_widget.clear()
        for code in self._codes:
            name = self._names.get(code)
            text = f"{name}  {code}" if name else code
            item = QListWidgetItem(text)
            item.setData(Qt.ItemDataRole.UserRole, code)
            self.list_widget.addItem(item)

    def _update_buttons(self, row: int):
        has = row >= 0
        self.del_btn.setEnabled(has)
        self.up_btn.setEnabled(has and row > 0)
        self.down_btn.setEnabled(has and row < len(self._codes) - 1)

    # ---- 操作
    def _add_from_search(self):
        text = self.search_edit.text().strip()
        if not text or self._searching:
            return
        self._searching = True
        try:
            QApplication.setOverrideCursor(Qt.CursorShape.WaitCursor)
            results = []
            try:
                results = quotes.search_stocks(text)
            except Exception:
                results = []
            # 兜底：输入看起来是代码时直接取行情名称（如北交所代码）
            if not results and config.CODE_RE.match(text.lower()):
                try:
                    qs = quotes.fetch_quotes([text.lower()])
                    q = qs.get(text.lower())
                    if q and q.name and q.name != "--":
                        results = [(text.lower(), q.name)]
                except Exception:
                    pass
        finally:
            QApplication.restoreOverrideCursor()
            self._searching = False

        if not results:
            QMessageBox.information(
                self, "未找到",
                f"未找到与「{text}」匹配的 A 股。\n试试输入 6 位代码，如 sh600519。")
            return
        if len(results) > 1:
            labels = [f"{n}  {c}" for c, n in results]
            choice, ok = QInputDialog.getItem(
                self, "选择股票", "找到多个匹配，请选择：", labels, 0, False)
            if not ok:
                return
            code, name = results[labels.index(choice)]
        else:
            code, name = results[0]
        if code in self._codes:
            QMessageBox.information(self, "已在列表", f"{name}（{code}）已在自选股中。")
            return
        self._codes.append(code)
        self._names[code] = name
        self._refresh_list()
        self.search_edit.clear()
        self.list_widget.setCurrentRow(len(self._codes) - 1)

    def _move_up(self):
        row = self.list_widget.currentRow()
        if row > 0:
            self._codes[row - 1], self._codes[row] = self._codes[row], self._codes[row - 1]
            self._refresh_list()
            self.list_widget.setCurrentRow(row - 1)

    def _move_down(self):
        row = self.list_widget.currentRow()
        if 0 <= row < len(self._codes) - 1:
            self._codes[row], self._codes[row + 1] = self._codes[row + 1], self._codes[row]
            self._refresh_list()
            self.list_widget.setCurrentRow(row + 1)

    def _delete_selected(self):
        row = self.list_widget.currentRow()
        if row >= 0:
            self._codes.pop(row)
            self._refresh_list()


# ---------------------------------------------------------------- 主窗口
class FloatQuoteWindow(QWidget):
    # 默认尺寸 = 原尺寸的一半（长、宽各缩小一半）
    COLLAPSED_W, COLLAPSED_H = 167, 19   # 收起态
    EXPANDED_W, EXPANDED_H = 192, 156    # 展开态
    MIN_COLLAPSED = (130, 17)            # 拖拽缩放的最小尺寸
    MIN_EXPANDED = (160, 100)
    RESIZE_MARGIN = 6                    # 边缘热区像素

    EDGE_CURSOR = {
        "l": Qt.CursorShape.SizeHorCursor,
        "r": Qt.CursorShape.SizeHorCursor,
        "t": Qt.CursorShape.SizeVerCursor,
        "b": Qt.CursorShape.SizeVerCursor,
        "tl": Qt.CursorShape.SizeFDiagCursor,
        "br": Qt.CursorShape.SizeFDiagCursor,
        "tr": Qt.CursorShape.SizeBDiagCursor,
        "bl": Qt.CursorShape.SizeBDiagCursor,
    }

    def __init__(self):
        super().__init__(None, Qt.WindowType.FramelessWindowHint
                         | Qt.WindowType.WindowStaysOnTopHint
                         | Qt.WindowType.Tool)
        self.setAttribute(Qt.WidgetAttribute.WA_TranslucentBackground, True)
        self.setWindowTitle("FloatQuote")

        self.cfg = config.load()
        self.stocks = self.cfg["stocks"]
        self.index = 0
        self.refresh_seconds = int(self.cfg.get("refresh_seconds", 3))
        self.chart_refresh_seconds = int(self.cfg.get("chart_refresh_seconds", 30))

        self._quotes = {}            # code -> Quote
        self._minute_cache = {}      # code -> (points, prev_close)
        self._pinned = False
        self._expanded = False
        self._drag_offset = None
        self._resize_edge = None          # 当前拖拽缩放的边缘
        self._resize_origin = None        # 按下时的窗口几何
        self._resize_start = None         # 按下时的全局鼠标位置
        self._mouse_active = False        # 拖动/缩放期间忽略收起
        self.switch_effect = self.cfg.get("switch_effect", "fade")
        if self.switch_effect not in ("off", "fade", "slide_h", "slide_v", "pulse"):
            self.switch_effect = "fade"
        self.auto_switch_seconds = int(self.cfg.get("auto_switch_seconds", 0))
        self._last_activity = time.time()
        self.display_count = max(1, min(6, int(self.cfg.get("display_count", 1))))
        self._selected_code = None       # 点击选中的股票（展开时展示）
        self._hovered = False            # 鼠标是否在窗口内（悬停不展开，仅暂停轮播）
        self._carousel_row = None        # 全部可见时轮播高亮的行下标
        self._press_local = None         # 按下位置（用于识别点击）
        self._press_global = None
        self._pressed_row_code = None
        self._collapsed_size = self._load_size(
            "size_collapsed", (self.COLLAPSED_W, self.COLLAPSED_H), self.MIN_COLLAPSED)
        self._expanded_size = self._load_size(
            "size_expanded", (self.EXPANDED_W, self.EXPANDED_H), self.MIN_EXPANDED)
        self._tray = None            # 系统托盘图标
        self._tray_icon_key = None
        self._quote_seq = 0
        self._minute_seq = 0
        self._minute_pending = False   # 分时拉取在途标志，防止 tick 重复触发
        self._minute_fail = 0          # 连续失败次数（用于指数退避）
        self._minute_retry_at = 0.0    # 退避到期时间
        self._last_offline_fetch = 0.0
        self._last_minute_fetch = 0.0

        self._signals = WorkerSignals()
        self._signals.quotes.connect(self._on_quotes)
        self._signals.minute.connect(self._on_minute)
        self._signals.minute_image.connect(self._on_minute_image)
        self._signals.degraded.connect(self._on_degraded)
        self._signals.error.connect(self._on_error)

        self._build_ui()
        self._build_tray()

        pos = self.cfg.get("pos")
        if isinstance(pos, (list, tuple)) and len(pos) == 2:
            self.move(int(pos[0]), int(pos[1]))

        self._collapse_timer = QTimer(self)
        self._collapse_timer.setSingleShot(True)
        self._collapse_timer.timeout.connect(lambda: self._set_expanded(False))
        self._tick_timer = QTimer(self)
        self._tick_timer.timeout.connect(self._tick)
        self._tick_timer.start(self.refresh_seconds * 1000)

        # 无操作自动轮播：每秒检查一次空闲时间
        self._idle_timer = QTimer(self)
        self._idle_timer.timeout.connect(self._check_auto_switch)
        self._idle_timer.start(1000)

        # 任何退出路径（含菜单「退出」）都等待后台取数线程结束，避免退出崩溃
        app = QApplication.instance()
        if app is not None:
            app.aboutToQuit.connect(lambda: QThreadPool.globalInstance().waitForDone(6000))

        self._set_expanded(False)
        self._refresh_quotes()
        self._refresh_minute()
        self._update_rows()

    # ------------------------------------------------------------ UI 构建
    def _load_size(self, key: str, default, minimum):
        """从配置读取尺寸并夹取到最小尺寸，非法值回退默认。"""
        v = self.cfg.get(key)
        if isinstance(v, (list, tuple)) and len(v) == 2:
            try:
                return [max(int(v[0]), minimum[0]), max(int(v[1]), minimum[1])]
            except (TypeError, ValueError):
                pass
        return list(default)

    def _build_ui(self):
        # 紧凑布局：字体与间距整体缩小，适配半尺寸窗口
        plain_style = "background: transparent;"
        self.name_label = _AnimatedLabel(
            QFont(UI_FONT, 8, QFont.Weight.Bold),
            f"color: {TEXT_MAIN.name()}; background: transparent;", max_w=66)
        self.price_label = _AnimatedLabel(QFont(UI_FONT, 9, QFont.Weight.Bold), plain_style)
        self.change_label = _AnimatedLabel(QFont(UI_FONT, 8, QFont.Weight.Bold), plain_style)

        header = QHBoxLayout()
        header.setContentsMargins(4, 0, 4, 0)
        header.setSpacing(3)
        header.addWidget(self.name_label)
        header.addStretch(1)
        header.addWidget(self.price_label)
        header.addWidget(self.change_label)
        # 展开态表头（收起时隐藏）
        self.header_widget = QWidget(self)
        self.header_widget.setLayout(header)

        # 收起态多行列表（数量 = display_count）
        self.rows_widget = QWidget(self)
        self.rows_layout = QVBoxLayout(self.rows_widget)
        self.rows_layout.setContentsMargins(0, 0, 0, 0)
        self.rows_layout.setSpacing(1)
        self._rows = []
        self._ensure_rows()

        # 详细信息区（展开时显示），2 列 × 4 行适配窄宽度
        self.info_labels = {}
        grid = QGridLayout()
        grid.setSpacing(1)
        rows = [("今开", "open"), ("昨收", "prev_close"), ("最高", "high"),
                ("最低", "low"), ("成交量", "volume"), ("成交额", "amount"),
                ("换手", "turnover"), ("市盈", "pe")]
        for i, (title, key) in enumerate(rows):
            t = QLabel(title)
            t.setFont(QFont(UI_FONT, 8))
            t.setStyleSheet(f"color: {TEXT_SUB.name()}; background: transparent;")
            v = QLabel("--")
            v.setFont(QFont(MONO, 8))
            v.setStyleSheet(f"color: {TEXT_MAIN.name()}; background: transparent;")
            grid.addWidget(t, i // 2, (i % 2) * 2)
            grid.addWidget(v, i // 2, (i % 2) * 2 + 1)
            self.info_labels[key] = v

        self.chart = MinuteChartWidget()
        self.chart.setStyleSheet("background: transparent;")

        detail = QVBoxLayout()
        detail.setContentsMargins(4, 0, 4, 0)
        detail.setSpacing(3)
        detail.addLayout(grid)
        detail.addWidget(self.chart, 1)
        # 返回按钮：展开页底部，点击立即收起（无需等待移开 3 秒）
        self.back_btn = QPushButton("返回", self)
        self.back_btn.setFont(QFont(UI_FONT, 8))
        self.back_btn.setFixedHeight(16)
        self.back_btn.setCursor(Qt.CursorShape.PointingHandCursor)
        self.back_btn.setStyleSheet(
            "QPushButton { background: #2C3140; color: #E8EBF2; border: 1px solid #3A4150;"
            " border-radius: 8px; padding: 0 10px; }"
            "QPushButton:hover { background: #3A4150; }")
        self.back_btn.clicked.connect(self._collapse_now)
        detail.addWidget(self.back_btn, 0, Qt.AlignmentFlag.AlignHCenter)

        self.detail_widget = QWidget(self)
        self.detail_widget.setLayout(detail)

        outer = QVBoxLayout(self)
        outer.setContentsMargins(4, 1, 4, 1)
        outer.setSpacing(3)
        outer.addWidget(self.header_widget)
        outer.addWidget(self.rows_widget)
        outer.addWidget(self.detail_widget)
        self.setToolTip("点击股票展开 · 移开 3 秒收起 · 拖边角调整大小 · 滚轮切换 · 双击锁定 · 右键菜单")

    def paintEvent(self, _e):
        """绘制圆角深色背景（窗口为透明背景，必须自绘）。"""
        p = QPainter(self)
        p.setRenderHint(QPainter.RenderHint.Antialiasing)
        path = QPainterPath()
        path.addRoundedRect(QRectF(self.rect()).adjusted(0.5, 0.5, -0.5, -0.5), 10, 10)
        p.fillPath(path, BG_MAIN)
        p.setPen(QPen(BORDER, 1))
        p.drawPath(path)

    def _effective_count(self) -> int:
        """实际显示行数：不超过自选股总数，避免出现空行。"""
        return max(1, min(self.display_count, len(self.stocks)))

    def _collapsed_min_h(self) -> int:
        n = self._effective_count()
        return n * _StockRow.ROW_H + (n - 1) * self.rows_layout.spacing() + 2

    def _set_expanded(self, expanded: bool):
        self._expanded = expanded
        self._clear_carousel()
        self.detail_widget.setVisible(expanded)
        self.header_widget.setVisible(expanded)
        self.rows_widget.setVisible(not expanded)
        if not expanded:
            self._selected_code = None
        # 先让布局重算最小尺寸，否则收起时的 resize 会被"旧的最小尺寸"钳制住
        self.layout().activate()
        if expanded:
            size, minimum = self._expanded_size, self.MIN_EXPANDED
            self.resize(max(size[0], minimum[0]), max(size[1], minimum[1]))
        else:
            # 收起态高度始终由行数决定（自适应），宽度记忆用户调整。
            # 布局最小尺寸缓存可能在删行后滞后，先解除钳制再 resize，避免高度缩不回去。
            w = max(self._collapsed_size[0], self.MIN_COLLAPSED[0])
            h = self._collapsed_min_h()
            self.setMinimumSize(1, 1)
            self.resize(w, h)
            self.setMinimumSize(0, 0)

    # ------------------------------------------------------------ 数据刷新
    def current_code(self) -> str:
        """当前展示的股票：点击选中的优先，否则为悬浮条窗口首只。"""
        if self._selected_code:
            return self._selected_code
        return self.stocks[self.index]

    def visible_stocks(self) -> list:
        """当前悬浮条显示的股票窗口。"""
        n = self.display_count
        start = min(self.index, max(0, len(self.stocks) - n))
        return self.stocks[start:start + n]

    def _ensure_rows(self):
        """按有效行数增删行组件。"""
        target = self._effective_count()
        while len(self._rows) < target:
            row = _StockRow(self)
            self.rows_layout.addWidget(row)
            self._rows.append(row)
        for i in range(len(self._rows) - 1, target - 1, -1):
            row = self._rows.pop(i)
            self.rows_layout.removeWidget(row)
            row.deleteLater()

    def _update_rows(self, animate: bool = False):
        """刷新悬浮条各行的内容（报价到达/切换/数量变化时调用）。"""
        self._ensure_rows()
        codes = self.visible_stocks()
        for i, row in enumerate(self._rows):
            if i < len(codes):
                code = codes[i]
                q = self._quotes.get(code)
                if q is not None:
                    color = COLOR_HEX[q.color_key]
                    style = f"color: {color}; background: transparent;"
                    arrow = "▲" if q.color_key == "red" else ("▼" if q.color_key == "green" else "—")
                    row.set_stock(code, q.name, quotes.fmt_price(q.price),
                                  f"{arrow} {quotes.fmt_pct(q.change_pct)}", style)
                else:
                    row.set_stock(code, code, "--", "--",
                                  f"color: {TEXT_SUB.name()}; background: transparent;")
                if animate:
                    row.play(self.switch_effect)
            else:
                row.clear()

    def _tick(self):
        now = time.time()
        if quotes.is_trading_time():
            self._refresh_quotes()
            if (self.current_code() not in self._minute_cache
                    or now - self._last_minute_fetch >= self.chart_refresh_seconds):
                self._refresh_minute()
        elif now - self._last_offline_fetch > 300:   # 非交易时段 5 分钟一次
            self._last_offline_fetch = now
            self._refresh_quotes()

    def _refresh_quotes(self):
        self._quote_seq += 1
        QThreadPool.globalInstance().start(
            FetchQuotesTask(list(self.stocks), self._signals, self._quote_seq))

    def _refresh_minute(self, force: bool = False):
        if self._minute_pending:
            return
        if not force and time.time() < self._minute_retry_at:
            return  # 失败退避中，不空转
        self._minute_pending = True
        self._last_minute_fetch = time.time()
        self._minute_seq += 1
        QThreadPool.globalInstance().start(
            FetchMinuteTask(self.current_code(), self._signals, self._minute_seq))

    def _on_quotes(self, qs: dict, seq: int):
        if seq != self._quote_seq:
            return
        self._quotes.update(qs)
        self._update_display()
        self._update_rows()

    def _on_minute(self, code: str, points: list, date: str, seq: int):
        if seq != self._minute_seq:
            return  # 在途旧任务的结果，丢弃
        self._minute_pending = False
        self._minute_fail = 0
        self._minute_retry_at = 0.0
        prev = self._quotes.get(code).prev_close if self._quotes.get(code) else None
        self._minute_cache[code] = (points, prev)
        if code == self.current_code():
            self.chart.set_data(points, prev)

    def _on_minute_image(self, code: str, data: bytes, seq: int):
        if seq != self._minute_seq:
            return
        self._minute_pending = False
        # 降级图只保证有图可看；数据源恢复前保留退避状态
        if code == self.current_code():
            self.chart.show_image(data)

    def _on_degraded(self, code: str, seq: int):
        if seq != self._minute_seq:
            return
        self._minute_pending = False
        self._apply_minute_backoff()

    def _on_error(self, msg: str):
        if msg.startswith("分时"):
            self._minute_pending = False
            self._apply_minute_backoff()
        else:
            self._minute_pending = False
        print(f"[FloatQuote] {msg}", file=sys.stderr)

    def _apply_minute_backoff(self):
        """数据源失败指数退避：15s -> 30s -> 60s -> 120s（上限）。"""
        self._minute_fail += 1
        delay = min(120, 15 * (2 ** (self._minute_fail - 1)))
        self._minute_retry_at = time.time() + delay

    def _update_display(self):
        q = self._quotes.get(self.current_code())
        if q is None:
            return
        color = COLOR_HEX[q.color_key]
        style = f"color: {color}; background: transparent;"
        arrow = "▲" if q.color_key == "red" else ("▼" if q.color_key == "green" else "—")
        self.name_label.set_text(q.name)
        self.price_label.set_text(quotes.fmt_price(q.price), style)
        self.change_label.set_text(f"{arrow} {quotes.fmt_pct(q.change_pct)}", style)

        m = {
            "open": quotes.fmt_price(q.open),
            "prev_close": quotes.fmt_price(q.prev_close),
            "high": quotes.fmt_price(q.high),
            "low": quotes.fmt_price(q.low),
            "volume": quotes.fmt_volume(q.volume),
            "amount": quotes.fmt_amount(q.amount),
            "turnover": quotes.fmt_pct(q.turnover) if q.turnover is not None else "--",
            "pe": f"{q.pe:.2f}" if q.pe is not None else "--",
        }
        for key, val in m.items():
            if key in self.info_labels:
                self.info_labels[key].setText(val)

        # 同步托盘：悬停提示显示实时价格，图标随涨跌变色
        if self._tray is not None:
            self._tray.setToolTip(
                f"{q.name}  {quotes.fmt_price(q.price)}  ({quotes.fmt_pct(q.change_pct)})")
            if q.color_key != self._tray_icon_key:
                self._tray_icon_key = q.color_key
                self._tray.setIcon(_make_tray_icon(q.color_key))

    # ------------------------------------------------------------ 交互
    def _mark_activity(self):
        """记录用户操作时间（用于自动轮播的空闲判断）。"""
        self._last_activity = time.time()

    def enterEvent(self, _e):
        # 悬停不再展开，仅取消收起并标记活动（暂停自动轮播）
        self._hovered = True
        self._collapse_timer.stop()
        self._mark_activity()

    def leaveEvent(self, _e):
        self._hovered = False
        self._mark_activity()
        # 拖动/缩放期间鼠标可能短暂移出窗口，不触发收起
        if not self._pinned and not self._mouse_active:
            self._collapse_timer.start(3000)   # 移开 3 秒后收起

    def _edge_at(self, pos: QPoint):
        """返回光标所在边缘：l/r/t/b/tl/tr/bl/br，不在边缘返回 None。"""
        x, y = pos.x(), pos.y()
        w, h = self.width(), self.height()
        m = self.RESIZE_MARGIN
        left, right = x <= m, x >= w - m
        top, bottom = y <= m, y >= h - m
        if top and left:
            return "tl"
        if top and right:
            return "tr"
        if bottom and left:
            return "bl"
        if bottom and right:
            return "br"
        if left:
            return "l"
        if right:
            return "r"
        if top:
            return "t"
        if bottom:
            return "b"
        return None

    def _row_at(self, pos: QPoint):
        """返回窗口坐标 pos 所在的行对应的股票代码（不在行上返回 None）。"""
        for row in self._rows:
            if row.code and row.isVisible():
                rect = QRect(row.mapTo(self, QPoint(0, 0)), row.size())
                if rect.contains(pos):
                    return row.code
        return None

    def mousePressEvent(self, e):
        if e.button() != Qt.MouseButton.LeftButton:
            return
        self._clear_carousel()
        self._mark_activity()
        self._mouse_active = True
        local = e.position().toPoint()
        self._press_local = local
        self._press_global = e.globalPosition().toPoint()
        self._pressed_row_code = None if self._expanded else self._row_at(local)
        edge = self._edge_at(local)
        if edge:
            self._resize_edge = edge
            self._resize_origin = self.frameGeometry()
            self._resize_start = e.globalPosition().toPoint()
        else:
            self._drag_offset = e.globalPosition().toPoint() - self.frameGeometry().topLeft()

    def mouseMoveEvent(self, e):
        self._mark_activity()
        if e.buttons() & Qt.MouseButton.LeftButton:
            if self._resize_edge is not None:
                self._do_resize(e.globalPosition().toPoint())
                return
            if self._drag_offset is not None:
                self.move(e.globalPosition().toPoint() - self._drag_offset)
        else:
            # 悬停边缘时显示缩放光标
            edge = self._edge_at(e.position().toPoint())
            self.setCursor(self.EDGE_CURSOR.get(edge, Qt.CursorShape.ArrowCursor))

    def _do_resize(self, gpos: QPoint):
        geo = self._resize_origin
        edge = self._resize_edge
        x, y, w, h = geo.x(), geo.y(), geo.width(), geo.height()
        dx = gpos.x() - self._resize_start.x()
        dy = gpos.y() - self._resize_start.y()
        min_w, min_h = self.MIN_EXPANDED if self._expanded else self.MIN_COLLAPSED
        if "l" in edge:
            nw = w - dx
            if nw >= min_w:
                x = geo.x() + dx
                w = nw
        if "r" in edge:
            w = max(min_w, w + dx)
        if "t" in edge:
            nh = h - dy
            if nh >= min_h:
                y = geo.y() + dy
                h = nh
        if "b" in edge:
            h = max(min_h, h + dy)
        self.setGeometry(x, y, w, h)

    def mouseReleaseEvent(self, e):
        if e.button() != Qt.MouseButton.LeftButton:
            return
        self._mouse_active = False
        moved = (e.globalPosition().toPoint() - self._press_global).manhattanLength() \
            if self._press_global is not None else 9999
        self._press_local = None
        self._press_global = None
        if self._resize_edge is not None:
            self._resize_edge = None
            self._resize_origin = None
            self._resize_start = None
            self._save_size()
            self._save_pos()
            return
        if self._drag_offset is not None:
            self._drag_offset = None
            self._save_pos()
            # 未移动的按下+释放 = 点击：展开并选中该行股票
            if moved <= 4 and not self._expanded:
                code = self._pressed_row_code or self.stocks[self.index]
                self._on_row_click(code)
        self._pressed_row_code = None

    def _collapse_now(self):
        """返回按钮：立即收起；已锁定展开时同时解锁。"""
        self._collapse_timer.stop()
        if self._pinned:
            self._pinned = False
            if hasattr(self, "pin_act"):
                self.pin_act.setChecked(False)
        self._set_expanded(False)

    def _on_row_click(self, code: str):
        """点击悬浮条某行：展开窗口并展示该股票详情与分时图。"""
        self._selected_code = code
        self._mark_activity()
        self._collapse_timer.stop()
        self._set_expanded(True)
        self._update_display()
        if code in self._minute_cache:
            self.chart.set_data(*self._minute_cache[code])
        else:
            self.chart.set_loading()
            self._minute_pending = False
            self._minute_retry_at = 0.0
            self._refresh_minute(force=True)

    def mouseDoubleClickEvent(self, e):
        if e.button() == Qt.MouseButton.LeftButton:
            self.toggle_pinned()

    def wheelEvent(self, e):
        self._mark_activity()
        if len(self.stocks) < 2:
            return
        dy = e.angleDelta().y()
        if dy > 0:
            self.switch_to((self.index - 1) % len(self.stocks))
        elif dy < 0:
            self.switch_to((self.index + 1) % len(self.stocks))

    def switch_to(self, i: int):
        """滚动悬浮条窗口，使第 i 只股票成为首行；超出范围时钳制。"""
        max_start = max(0, len(self.stocks) - self.display_count)
        target = max(0, min(i, max_start))
        if target == self.index:
            return
        self._clear_carousel()
        self.index = target
        self._mark_activity()
        self._update_display()
        self._update_rows(animate=not self._expanded)
        self._play_switch_animation()
        self._refresh_quotes()
        self._minute_pending = False  # 取消在途旧股票的分时拉取（seq 机制丢弃其结果）
        self._minute_retry_at = 0.0
        self._refresh_minute(force=True)
        cached = self._minute_cache.get(self.current_code())
        if cached:
            self.chart.set_data(*cached)
        else:
            self.chart.set_loading()

    def _play_switch_animation(self):
        """展开态切换动画（收起态由 _update_rows 对各行播放）。"""
        if not self.isVisible() or not self._expanded:
            return
        self.name_label.play(self.switch_effect, -1)
        self.price_label.play(self.switch_effect, 1)
        self.change_label.play(self.switch_effect, 1)

    def _check_auto_switch(self):
        """无操作停留超过设定秒数后自动轮播（展开/悬停/隐藏时不切）。

        显示数量 < 股票数：显示窗口循环滚动；
        显示数量 >= 股票数（全部可见）：轮播高亮行，保证有可见反馈。
        """
        if self.auto_switch_seconds <= 0 or len(self.stocks) < 2:
            return
        if not self.isVisible() or self._expanded or self._hovered:
            return
        if time.time() - self._last_activity < self.auto_switch_seconds:
            return
        max_start = max(0, len(self.stocks) - self.display_count)
        if max_start > 0:
            # 窗口位置 0..max_start 循环
            self.switch_to((self.index + 1) % (max_start + 1))
        else:
            self._set_carousel_highlight(
                (self._carousel_row + 1) if self._carousel_row is not None else 0)
            self._mark_activity()

    def _set_carousel_highlight(self, row: int):
        """高亮指定行（循环轮播用），并清除旧高亮。"""
        n = len(self._rows)
        if n == 0:
            return
        row %= n
        if self._carousel_row == row:
            return
        if self._carousel_row is not None and 0 <= self._carousel_row < n:
            self._rows[self._carousel_row].set_highlight(False)
        self._carousel_row = row
        self._rows[row].set_highlight(True)

    def _clear_carousel(self):
        """清除轮播高亮（用户交互时调用）。"""
        if self._carousel_row is not None:
            n = len(self._rows)
            if 0 <= self._carousel_row < n:
                self._rows[self._carousel_row].set_highlight(False)
            self._carousel_row = None

    def set_auto_switch(self, secs: int):
        self.auto_switch_seconds = secs
        self.cfg["auto_switch_seconds"] = secs
        config.save(self.cfg)

    def set_switch_effect(self, key: str):
        if key not in ("off", "fade", "slide_h", "slide_v", "pulse"):
            return
        self.switch_effect = key
        self.cfg["switch_effect"] = key
        config.save(self.cfg)

    def toggle_pinned(self):
        self._pinned = not self._pinned
        if self._pinned:
            self._collapse_timer.stop()
            self._set_expanded(True)
        # 未锁定时等鼠标移出再收起（leaveEvent 处理）
        if hasattr(self, "pin_act"):
            self.pin_act.setChecked(self._pinned)

    def toggle_top(self):
        flags = self.windowFlags()
        if flags & Qt.WindowType.WindowStaysOnTopHint:
            flags &= ~Qt.WindowType.WindowStaysOnTopHint
        else:
            flags |= Qt.WindowType.WindowStaysOnTopHint
        self.setWindowFlags(flags)
        self.show()
        self.cfg["always_on_top"] = bool(flags & Qt.WindowType.WindowStaysOnTopHint)
        config.save(self.cfg)
        if hasattr(self, "top_act"):
            self.top_act.setChecked(self.cfg["always_on_top"])

    def set_refresh(self, secs: int):
        self.refresh_seconds = secs
        self.cfg["refresh_seconds"] = secs
        config.save(self.cfg)
        self._tick_timer.start(secs * 1000)

    def edit_stocks(self):
        names = {c: q.name for c, q in self._quotes.items() if q and q.name}
        dlg = StockEditDialog(self.stocks, names, self)
        if dlg.exec() != QDialog.DialogCode.Accepted:
            return
        codes = dlg.codes()
        if not codes:
            return
        cur = self.stocks[self.index]
        self.stocks = codes
        max_start = max(0, len(codes) - self.display_count)
        self.index = min(codes.index(cur) if cur in codes else 0, max_start)
        self._selected_code = None
        self._clear_carousel()
        # 用已知名称填充占位，切换菜单立即显示名称（行情刷新后会更新）
        for code in codes:
            if code not in self._quotes and dlg.name_of(code):
                self._quotes[code] = quotes.Quote(code=code, name=dlg.name_of(code))
        self.cfg["stocks"] = codes
        config.save(self.cfg)
        self._update_display()
        self._update_rows()
        self._refresh_quotes()
        self._minute_pending = False
        self._minute_retry_at = 0.0
        self._refresh_minute(force=True)

    def set_display_count(self, n: int):
        """设置悬浮条同时显示的股票数量（收起态高度随之自适应）。"""
        n = max(1, min(6, n))
        if n == self.display_count:
            return
        self.display_count = n
        self.cfg["display_count"] = n
        config.save(self.cfg)
        self._clear_carousel()
        max_start = max(0, len(self.stocks) - self._effective_count())
        self.index = min(self.index, max_start)
        self._update_rows()
        self._set_expanded(self._expanded)  # 重新计算收起高度（自适应）
        self._update_display()

    def contextMenuEvent(self, e):
        self._mark_activity()
        self._build_menu().exec(e.globalPos())

    def _build_menu(self) -> QMenu:
        menu = QMenu(self)
        menu.setStyleSheet("QMenu { background: #232733; color: #E8EBF2; }"
                           "QMenu::item:selected { background: #3A4150; }")

        toggle_text = "隐藏悬浮窗" if self.isVisible() else "显示悬浮窗"
        menu.addAction(toggle_text, self.toggle_visible)

        switch_menu = menu.addMenu("切换股票")
        for i, code in enumerate(self.stocks):
            q = self._quotes.get(code)
            label = f"{q.name}  {code}" if q and q.name else code
            act = switch_menu.addAction(label)
            act.setCheckable(True)
            act.setChecked(i == self.index)
            act.triggered.connect(lambda _=False, i=i: self.switch_to(i))

        menu.addAction("编辑自选股…", self.edit_stocks)

        refresh_menu = menu.addMenu("刷新间隔")
        for secs in (2, 3, 5, 10):
            act = refresh_menu.addAction(f"{secs} 秒")
            act.setCheckable(True)
            act.setChecked(secs == self.refresh_seconds)
            act.triggered.connect(lambda _=False, s=secs: self.set_refresh(s))

        auto_menu = menu.addMenu("自动轮播")
        for label, secs in (("关闭", 0), ("5 秒", 5), ("10 秒", 10),
                            ("20 秒", 20), ("30 秒", 30)):
            act = auto_menu.addAction(label)
            act.setCheckable(True)
            act.setChecked(secs == self.auto_switch_seconds)
            act.triggered.connect(lambda _=False, s=secs: self.set_auto_switch(s))

        count_menu = menu.addMenu("显示数量")
        for n in range(1, 7):
            act = count_menu.addAction(str(n))
            act.setCheckable(True)
            act.setChecked(n == self.display_count)
            act.triggered.connect(lambda _=False, n=n: self.set_display_count(n))

        anim_menu = menu.addMenu("切换动画")
        for label, key in (("关闭", "off"), ("淡入淡出", "fade"), ("左右滑入", "slide_h"),
                           ("上下滚动", "slide_v"), ("脉冲闪烁", "pulse")):
            act = anim_menu.addAction(label)
            act.setCheckable(True)
            act.setChecked(key == self.switch_effect)
            act.triggered.connect(lambda _=False, k=key: self.set_switch_effect(k))

        self.pin_act = menu.addAction("锁定展开")
        self.pin_act.setCheckable(True)
        self.pin_act.setChecked(self._pinned)
        self.pin_act.triggered.connect(lambda _=False: self.toggle_pinned())

        self.top_act = menu.addAction("窗口置顶")
        self.top_act.setCheckable(True)
        self.top_act.setChecked(bool(self.windowFlags() & Qt.WindowType.WindowStaysOnTopHint))
        self.top_act.triggered.connect(lambda _=False: self.toggle_top())

        menu.addSeparator()
        menu.addAction("退出", QApplication.quit)
        return menu

    # ------------------------------------------------------------ 系统托盘
    def _build_tray(self):
        if not QSystemTrayIcon.isSystemTrayAvailable():
            return
        self._tray = QSystemTrayIcon(_make_tray_icon("gray"), self)
        self._tray.setToolTip("FloatQuote · 悬浮行情")
        self._tray.activated.connect(self._on_tray_activated)
        self._tray.show()

    def _on_tray_activated(self, reason):
        if reason == QSystemTrayIcon.ActivationReason.Context:
            # 每次现建菜单，保证股票列表与勾选状态最新
            self._build_menu().exec(QCursor.pos())
        elif reason in (QSystemTrayIcon.ActivationReason.Trigger,
                        QSystemTrayIcon.ActivationReason.DoubleClick):
            self.toggle_visible()

    def toggle_visible(self):
        self._mark_activity()
        if self.isVisible():
            self.hide()
        else:
            self.show()
            self.raise_()
            self.activateWindow()

    def _save_pos(self):
        self.cfg["pos"] = [self.x(), self.y()]
        config.save(self.cfg)

    def _save_size(self):
        if self._expanded:
            self._expanded_size = [self.width(), self.height()]
            self.cfg["size_expanded"] = self._expanded_size
        else:
            self._collapsed_size = [self.width(), self.height()]
            self.cfg["size_collapsed"] = self._collapsed_size
        config.save(self.cfg)

    def closeEvent(self, e):
        self._save_pos()
        self._save_size()
        # 等待后台取数线程结束（覆盖接口 5s 超时上限），避免退出时线程仍在跑导致崩溃
        QThreadPool.globalInstance().waitForDone(6000)
        super().closeEvent(e)
