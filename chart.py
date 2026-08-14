# -*- coding: utf-8 -*-
"""分时图绘制组件：QPainter 自绘折线 + 昨收基准线 + 均价线 + 涨跌填充。"""
from PyQt6.QtCore import QPointF, QRectF, Qt
from PyQt6.QtGui import QColor, QFont, QImage, QLinearGradient, QPainter, QPainterPath, QPen, QPolygonF
from PyQt6.QtWidgets import QWidget

BG = QColor(24, 28, 36)
GRID = QColor(56, 62, 76)
RED = QColor(226, 76, 66)      # 涨
GREEN = QColor(40, 178, 110)   # 跌
YELLOW = QColor(245, 194, 66)  # 均价线
TEXT = QColor(158, 165, 178)
MONO = "Consolas"

SESSION_MINUTES = 240  # 09:30-11:30 + 13:00-15:00 = 240 分钟


class MinuteChart(QWidget):
    def __init__(self, parent=None):
        super().__init__(parent)
        self._points = []        # [(mins, price, avg)]
        self._prev_close = None
        self._msg = "加载中…"
        self._fallback = None    # 备选：新浪分时图 QImage
        self.setMinimumSize(150, 60)

    def set_data(self, points, prev_close):
        """points: [(time_str, price, avg)]"""
        self._fallback = None
        self._points = []
        for t, price, avg in points:
            try:
                ts = str(t).zfill(4)
                hh, mm = int(ts[:2]), int(ts[2:])
                mins = hh * 60 + mm - 9 * 60 - 30
                if mins > 120:      # 午休 90 分钟不计入
                    mins -= 90
                mins = max(0, min(SESSION_MINUTES, mins))
                self._points.append((mins, float(price), float(avg)))
            except (TypeError, ValueError):
                continue
        self._prev_close = prev_close
        if not self._points:
            self._msg = "暂无分时数据（非交易时段）"
        self.update()

    def set_loading(self):
        self._points = []
        self._prev_close = None
        self._fallback = None
        self._msg = "加载中…"
        self.update()

    def show_image(self, data: bytes):
        """显示新浪分时图 GIF（腾讯数据源失败时的降级）。"""
        img = QImage.fromData(data)
        if img.isNull():
            self.set_loading()
            return
        self._fallback = img
        self._points = []
        self.update()

    def _map(self, mins, price, x0, y0, pw, ph, ymin, ymax):
        x = x0 + pw * mins / SESSION_MINUTES
        y = y0 + ph * (ymax - price) / (ymax - ymin)
        return QPointF(x, y)

    def paintEvent(self, _ev):
        p = QPainter(self)
        p.setRenderHint(QPainter.RenderHint.Antialiasing)
        w, h = self.width(), self.height()
        # 小尺寸时精简边距与标注，避免挤成一团
        small = w < 240 or h < 100
        if small:
            left, right, top, bottom = 8, 4, 6, 8
        else:
            left, right, top, bottom = 34, 8, 14, 16
        pw, ph = w - left - right, h - top - bottom

        if self._fallback is not None:
            # 降级图：保持宽高比居中显示
            img = self._fallback
            scale = min(pw / img.width(), ph / img.height())
            dw, dh = img.width() * scale, img.height() * scale
            dx = left + (pw - dw) / 2
            dy = top + (ph - dh) / 2
            p.drawImage(QRectF(dx, dy, dw, dh), img)
            p.setPen(QPen(TEXT, 1, Qt.PenStyle.DashLine))
            p.drawRect(int(dx), int(dy), int(dw), int(dh))
            return

        if not self._points:
            p.setPen(TEXT)
            f = QFont("Microsoft YaHei UI", 8)
            p.setFont(f)
            p.drawText(self.rect(), Qt.AlignmentFlag.AlignCenter, self._msg)
            return

        prices = [pt[1] for pt in self._points]
        avgs = [pt[2] for pt in self._points if pt[2] is not None]
        vals = prices + avgs
        if self._prev_close is not None:
            vals = vals + [self._prev_close]
        ymin, ymax = min(vals), max(vals)
        pad = (ymax - ymin) * 0.04 or 0.01
        ymin, ymax = ymin - pad, ymax + pad

        x0, y0 = left, top
        # 网格 + 纵轴刻度（上/中/下）
        p.setPen(QPen(GRID, 1))
        for frac in (0.0, 0.5, 1.0):
            yy = y0 + ph * frac
            p.drawLine(QPointF(x0, yy), QPointF(x0 + pw, yy))
        for frac in (0.0, 0.5, 1.0):
            xx = x0 + pw * frac
            p.drawLine(QPointF(xx, y0), QPointF(xx, y0 + ph))

        # 纵轴价格刻度（小尺寸下省略）
        if not small:
            def draw_label(text, x, y, align=Qt.AlignmentFlag.AlignRight | Qt.AlignmentFlag.AlignVCenter,
                           color=TEXT):
                p.setPen(color)
                p.drawText(int(x), int(y), int(left - 6), 14, align, text)

            f = QFont(MONO, 8)
            p.setFont(f)
            draw_label(f"{ymax:.2f}", x0, y0 - 7)
            if self._prev_close is not None:
                draw_label(f"{self._prev_close:.2f}", x0, y0 + ph / 2 - 7, color=YELLOW)
            draw_label(f"{ymin:.2f}", x0, y0 + ph - 7)

        # X 轴刻度（小尺寸下省略）
        if not small:
            p.setPen(TEXT)
            xlabels = [("09:30", 0.0), ("11:30/13:00", 0.5), ("15:00", 1.0)]
            for text, frac in xlabels:
                xx = x0 + pw * frac
                fm = p.fontMetrics()
                tw = fm.horizontalAdvance(text)
                p.drawText(int(xx - tw / 2), y0 + ph + 8, text)

        # 昨收基准线（黄色虚线）
        if self._prev_close is not None:
            yy = y0 + ph * (ymax - self._prev_close) / (ymax - ymin)
            pen = QPen(YELLOW, 1, Qt.PenStyle.DashLine)
            p.setPen(pen)
            p.drawLine(QPointF(x0, yy), QPointF(x0 + pw, yy))

        # 涨跌方向决定主色
        last_price = prices[-1]
        up = self._prev_close is None or last_price >= self._prev_close
        main = RED if up else GREEN

        # 均价线（细虚线）
        avg_pts = [pt for pt in self._points if pt[2] is not None]
        if len(avg_pts) > 1:
            poly = QPolygonF(
                [self._map(m, a, x0, y0, pw, ph, ymin, ymax) for m, _, a in avg_pts])
            p.setPen(QPen(QColor(240, 210, 120, 200), 1, Qt.PenStyle.DotLine))
            p.drawPolyline(poly)

        # 价格线 + 下方渐变填充
        poly = QPolygonF(
            [self._map(m, pr, x0, y0, pw, ph, ymin, ymax) for m, pr, _ in self._points])
        path = QPainterPath()
        path.addPolygon(poly)
        path.lineTo(poly.last().x(), y0 + ph)
        path.lineTo(poly.first().x(), y0 + ph)
        path.closeSubpath()
        grad = QLinearGradient(0, y0, 0, y0 + ph)
        c = QColor(main)
        c.setAlpha(70)
        grad.setColorAt(0.0, c)
        c2 = QColor(main)
        c2.setAlpha(14)
        grad.setColorAt(1.0, c2)
        p.fillPath(path, grad)

        p.setPen(QPen(main, 1.6))
        p.drawPolyline(poly)

        # 末点价格标签（小尺寸下更紧凑）
        last_pt = self._points[-1]
        lx, ly = poly.last().x(), poly.last().y()
        label = f"{last_price:.2f}"
        tag_h = 11 if small else 14
        tag_fs = 7 if small else 8
        fm = p.fontMetrics()
        tw = fm.horizontalAdvance(label) + 6
        bx = min(max(lx - tw / 2, x0), x0 + pw - tw)
        by = ly - tag_h - 1 if ly - tag_h - 1 > y0 else ly + 4
        p.setPen(Qt.PenStyle.NoPen)
        p.setBrush(QColor(main))
        p.drawRoundedRect(int(bx), int(by), int(tw), tag_h, 3, 3)
        p.setPen(QColor(255, 255, 255))
        p.setFont(QFont(MONO, tag_fs))
        p.drawText(int(bx + 3), int(by + tag_h - 3), label)

        # 图例（小尺寸下省略）
        if not small:
            legend = QFont("Microsoft YaHei UI", 8)
            p.setFont(legend)
            p.setPen(QPen(main, 2))
            p.drawLine(x0 + 2, y0 - 12, x0 + 16, y0 - 12)
            p.setPen(TEXT)
            p.drawText(x0 + 20, y0 - 8, "价格")
            p.setPen(QPen(QColor(240, 210, 120, 200), 2, Qt.PenStyle.DotLine))
            p.drawLine(x0 + 58, y0 - 12, x0 + 72, y0 - 12)
            p.setPen(TEXT)
            p.drawText(x0 + 76, y0 - 8, "均价")
            if self._prev_close is not None:
                p.setPen(QPen(YELLOW, 1, Qt.PenStyle.DashLine))
                p.drawLine(x0 + 120, y0 - 12, x0 + 134, y0 - 12)
                p.setPen(TEXT)
                p.drawText(x0 + 138, y0 - 8, "昨收")
