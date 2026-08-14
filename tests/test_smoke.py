# -*- coding: utf-8 -*-
"""冒烟测试：程序化验证核心逻辑（不依赖人工点击）。

运行: python tests/test_smoke.py
"""
import os
import sys
import tempfile
import time
from pathlib import Path

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

from PyQt6.QtCore import QEvent, QPoint, QPointF, Qt  # noqa: E402
from PyQt6.QtGui import QMouseEvent  # noqa: E402
from PyQt6.QtWidgets import QApplication  # noqa: E402

import config  # noqa: E402
from widgets import FloatQuoteWindow  # noqa: E402


def pump(app, seconds):
    """驱动事件循环处理网络回调。"""
    end = time.time() + seconds
    while time.time() < end:
        app.processEvents()
        time.sleep(0.02)


def wait_until(app, cond, timeout=15.0):
    """轮询等待条件成立（抗网络抖动）。"""
    end = time.time() + timeout
    while time.time() < end:
        app.processEvents()
        if cond():
            return True
        time.sleep(0.05)
    app.processEvents()
    return cond()


def mouse_event(etype, widget, local, button, buttons):
    """构造 QMouseEvent（本地坐标 + 全局坐标）。"""
    return QMouseEvent(etype, QPointF(local), QPointF(widget.mapToGlobal(local)),
                       button, buttons, Qt.KeyboardModifier.NoModifier)


def main() -> int:
    app = QApplication(sys.argv)
    # 隔离配置：测试不污染真实 config.json
    tmp_dir = tempfile.mkdtemp(prefix="floatquote_test_")
    config.CONFIG_PATH = Path(tmp_dir) / "config.json"

    cfg = config.load()
    assert cfg["stocks"], "默认自选股为空"
    print(f"[ok] 配置加载: {cfg['stocks']}")

    w = FloatQuoteWindow()
    w.auto_switch_seconds = 0  # 主窗口关闭自动轮播，避免干扰其它断言
    w.show()
    pump(app, 3.0)

    # 1) 报价已到达并显示
    assert wait_until(app, lambda: bool(w._quotes)), "报价未到达"
    q = w._quotes.get(w.current_code())
    assert q and q.ok and q.price > 0, f"当前股票报价异常: {q}"
    print(f"[ok] 报价显示: {q.name} {q.price} ({q.change_pct}%)")
    assert w.price_label.text() != "--", "价格标签未更新"

    # 1.5) 系统托盘
    if w._tray is not None:
        assert w._tray.isVisible(), "托盘图标未显示"
        assert not w._tray.icon().isNull(), "托盘图标为空"
        assert "贵州茅台" in w._tray.toolTip(), f"托盘提示未更新: {w._tray.toolTip()}"
        print(f"[ok] 托盘图标: tooltip='{w._tray.toolTip()}'")
        # 显隐切换
        w.toggle_visible()
        pump(app, 0.3)
        assert not w.isVisible(), "隐藏悬浮窗失败"
        w.toggle_visible()
        pump(app, 0.3)
        assert w.isVisible(), "显示悬浮窗失败"
        print("[ok] 托盘显隐切换正常")
    else:
        print("[skip] 系统托盘不可用，跳过托盘测试")

    # 2) 分时数据已到达并画图（JSON 数据或降级图任一即算成功）
    code0 = w.current_code()
    assert wait_until(app, lambda: code0 in w._minute_cache or w.chart._fallback is not None), \
        "分时数据未加载"
    if code0 in w._minute_cache:
        pts, _ = w._minute_cache[code0]
        assert len(pts) > 0, "分时数据点为空"
        assert len(w.chart._points) > 0, "图表未拿到数据点"
        print(f"[ok] 分时图: {len(pts)} 个数据点, 最新价 {pts[-1][1]}")
    else:
        assert w.chart._fallback is not None, "降级图未显示"
        print("[ok] 分时图: 腾讯数据源降级，显示新浪分时图")

    # 3) 收起/展开状态切换（含尺寸收缩，实际尺寸可能被布局最小尺寸小幅钳制）
    w._set_expanded(True)
    pump(app, 0.3)
    assert w.detail_widget.isVisible(), "展开态详情未显示"
    assert abs(w.width() - w.EXPANDED_W) <= 10 and abs(w.height() - w.EXPANDED_H) <= 10, \
        f"展开尺寸不对: {w.width()}x{w.height()}"
    w._set_expanded(False)
    pump(app, 0.3)
    assert not w.detail_widget.isVisible(), "收起态详情未隐藏"
    assert abs(w.width() - w.COLLAPSED_W) <= 10 and abs(w.height() - w.COLLAPSED_H) <= 10, \
        f"收起尺寸未收缩: {w.width()}x{w.height()}"
    print(f"[ok] 收起/展开切换正常 ({w.COLLAPSED_W}x{w.COLLAPSED_H} / "
          f"{w.EXPANDED_W}x{w.EXPANDED_H})")

    # 3.5) 拖拽缩放（模拟按住右下角向外拖 60x40）
    w._set_expanded(True)
    pump(app, 0.3)
    bw, bh = w.width(), w.height()
    corner = w.mapToGlobal(QPoint(w.width() - 1, w.height() - 1))
    press = mouse_event(QEvent.Type.MouseButtonPress, w, w.mapFromGlobal(corner),
                        Qt.MouseButton.LeftButton, Qt.MouseButton.LeftButton)
    w.mousePressEvent(press)
    assert w._resize_edge == "br", f"右下角热区未识别: {w._resize_edge}"
    target = corner + QPoint(60, 40)
    w.mouseMoveEvent(mouse_event(QEvent.Type.MouseMove, w, w.mapFromGlobal(target),
                                 Qt.MouseButton.LeftButton, Qt.MouseButton.LeftButton))
    w.mouseReleaseEvent(mouse_event(QEvent.Type.MouseButtonRelease, w, w.mapFromGlobal(target),
                                    Qt.MouseButton.LeftButton, Qt.MouseButton.NoButton))
    pump(app, 0.2)
    assert w.width() == bw + 60 and w.height() == bh + 40, \
        f"缩放失败: {bw}x{bh} -> {w.width()}x{w.height()}"
    assert w.cfg.get("size_expanded") == [w.width(), w.height()], "缩放后尺寸未写入配置"
    print(f"[ok] 拖拽缩放: {bw}x{bh} -> {w.width()}x{w.height()}, 已记住")

    # 3.6) 缩放最小尺寸保护（向内拖过小应被钳住）
    corner = w.mapToGlobal(QPoint(w.width() - 1, w.height() - 1))  # 重新取当前右下角
    tiny = corner + QPoint(-500, -300)
    press = mouse_event(QEvent.Type.MouseButtonPress, w, w.mapFromGlobal(corner),
                        Qt.MouseButton.LeftButton, Qt.MouseButton.LeftButton)
    w.mousePressEvent(press)
    w.mouseMoveEvent(mouse_event(QEvent.Type.MouseMove, w, w.mapFromGlobal(tiny),
                                 Qt.MouseButton.LeftButton, Qt.MouseButton.LeftButton))
    w.mouseReleaseEvent(mouse_event(QEvent.Type.MouseButtonRelease, w, w.mapFromGlobal(tiny),
                                    Qt.MouseButton.LeftButton, Qt.MouseButton.NoButton))
    pump(app, 0.2)
    assert w.width() >= w.MIN_EXPANDED[0] and w.height() >= w.MIN_EXPANDED[1], \
        f"最小尺寸保护失效: {w.width()}x{w.height()}"
    print(f"[ok] 最小尺寸保护: {w.width()}x{w.height()}")

    # 恢复默认展开尺寸，避免影响后续断言
    w._set_expanded(False)
    pump(app, 0.2)

    # 4) 滚轮切换股票
    idx0 = w.index
    w.switch_to((idx0 + 1) % len(w.stocks))
    assert wait_until(app, lambda: w._quotes.get(w.current_code())), "切换后报价缺失"
    assert wait_until(app, lambda: w.current_code() in w._minute_cache
                      or w.chart._fallback is not None), "切换后分时未加载"
    assert w.index != idx0, "切换失败"
    print(f"[ok] 股票切换: {w.stocks[w.index]}")

    # 5) 锁定展开
    w.toggle_pinned()
    assert w._pinned, "锁定失败"
    w._set_expanded(True)
    w.leaveEvent(None)  # 模拟鼠标移出
    pump(app, 1.2)
    assert w.detail_widget.isVisible(), "锁定时不应收起"
    w.toggle_pinned()
    print("[ok] 锁定展开正常")

    # 6) 配置写回
    pos = [w.x(), w.y()]
    cfg["pos"] = pos
    config.save(cfg)
    cfg2 = config.load()
    assert cfg2["pos"] == pos, "位置未写回"
    print(f"[ok] 配置写回: pos={pos}")

    # 6.5) 股票搜索接口（按名称/代码；接口偶发超时，不可用时跳过）
    from quotes import search_stocks  # noqa: F401
    r = search_stocks("茅台")
    if not r:
        print("[skip] 搜索接口不可用（网络），跳过搜索断言")
    else:
        assert r[0][0] == "sh600519" and r[0][1] == "贵州茅台", f"搜索「茅台」异常: {r}"
        r2 = search_stocks("600519")
        assert r2 and r2[0][0] == "sh600519", f"搜索代码异常: {r2}"
        r3 = search_stocks("平安")
        assert len(r3) >= 1, "多结果搜索为空"
        print(f"[ok] 股票搜索: 茅台->{r[0]}, 平安->{len(r3)}条")

    # 7) 切换菜单显示股票名称
    from widgets import StockEditDialog  # noqa: F401
    menu = w._build_menu()
    switch = next(a for a in menu.actions() if a.text() == "切换股票")
    texts = [a.text() for a in switch.menu().actions()]
    assert any("贵州茅台" in t for t in texts), f"切换菜单未显示名称: {texts}"
    print(f"[ok] 切换菜单显示名称: {texts[:3]}")

    # 8) 自选股管理对话框
    from PyQt6.QtWidgets import QMessageBox
    QMessageBox.information = lambda *a, **k: None  # 测试中不弹阻塞提示框
    dlg = StockEditDialog(["sh600519", "sz000001"],
                          {"sh600519": "贵州茅台", "sz000001": "平安银行"})
    dlg.show()
    pump(app, 0.5)
    assert dlg.list_widget.count() == 2, "初始列表数量不对"
    assert "贵州茅台" in dlg.list_widget.item(0).text(), "列表未显示名称"
    print(f"[ok] 对话框列表: {dlg.list_widget.item(0).text()} | {dlg.list_widget.item(1).text()}")

    # 重复添加拦截
    dlg.search_edit.setText("sh600519")
    dlg._add_from_search()
    assert dlg.list_widget.count() == 2, "重复添加未被拦截"

    # 按代码添加（网络失败时容忍）
    dlg.search_edit.setText("600519")
    dlg._add_from_search()
    if dlg.list_widget.count() == 3:
        assert "贵州茅台" in dlg.list_widget.item(2).text(), "添加后未显示名称"
        print("[ok] 对话框添加: 600519 ->", dlg.list_widget.item(2).text())
    else:
        print("[skip] 网络不可用，跳过对话框添加断言")

    # 上移/下移/删除
    dlg.list_widget.setCurrentRow(0)
    before0 = dlg.codes()[0]
    dlg._move_down()
    assert dlg.codes()[1] == before0, "下移失败"
    dlg._move_up()
    assert dlg.codes()[0] == before0, "上移失败"
    n_before = dlg.list_widget.count()
    dlg.list_widget.setCurrentRow(1)
    dlg._delete_selected()
    assert dlg.list_widget.count() == n_before - 1, "删除失败"
    print(f"[ok] 对话框排序/删除: {dlg.codes()}")

    # 8.5) 悬浮条显示数量（多行列表）
    w.stocks = ["sh600519", "sz000001", "sz300750", "sz000002"]
    w.index = 0
    w.set_display_count(3)
    assert w.display_count == 3 and len(w._rows) == 3, "行数未更新"
    w._set_expanded(False)
    pump(app, 0.3)
    assert abs(w.height() - (3 * 17 + 2 * 1 + 2)) <= 4, f"收起高度未随行数变化: {w.height()}"
    assert w.visible_stocks() == ["sh600519", "sz000001", "sz300750"], \
        f"可见股票窗口错误: {w.visible_stocks()}"
    row_texts = [r.name.text() for r in w._rows]
    assert row_texts[0] and row_texts[0] != "--", f"行内容为空: {row_texts}"
    print(f"[ok] 显示数量3: 高度={w.height()}, 行={row_texts}")

    # 多行滚动：窗口向前滚动并被钳制
    w.switch_to(2)
    assert w.index == 1, f"滚动钳制错误: index={w.index}"
    assert w.visible_stocks() == ["sz000001", "sz300750", "sz000002"], \
        f"滚动后可见窗口错误: {w.visible_stocks()}"
    print(f"[ok] 多行滚动: index=1 -> {w.visible_stocks()}")

    # 悬停第 2 行 -> 展开显示该股票
    w._on_row_click("sz300750")
    assert w.current_code() == "sz300750", "点击行未选中"
    print("[ok] 点击行选中: sz300750")

    # 数量 6 -> 3：收起高度应自适应缩小（回归：曾被旧存储高度卡住）
    w.set_display_count(6)
    w._set_expanded(False)
    pump(app, 0.3)
    h6 = w.height()
    assert h6 > 60, f"数量6高度异常: {h6}"
    w.set_display_count(3)
    w._set_expanded(False)
    pump(app, 0.3)
    h3 = w.height()
    assert abs(h3 - (3 * 17 + 2 * 1 + 2)) <= 4, f"数量6->3 高度未自适应: {h3}"
    assert h3 < h6, f"缩小数量后高度未变小: {h6} -> {h3}"
    print(f"[ok] 数量自适应: 6 -> {h6}px, 3 -> {h3}px")

    # 8.7) 点击行展开 + 移开 3 秒收起
    w._set_expanded(False)
    pump(app, 0.2)
    w.set_display_count(3)
    pump(app, 0.2)
    row0 = w._rows[0]
    assert row0.code, "首行为空"
    center = row0.mapTo(w, QPoint(row0.width() // 2, row0.height() // 2))
    w.mousePressEvent(mouse_event(QEvent.Type.MouseButtonPress, w, center,
                                  Qt.MouseButton.LeftButton, Qt.MouseButton.LeftButton))
    w.mouseReleaseEvent(mouse_event(QEvent.Type.MouseButtonRelease, w, center,
                                    Qt.MouseButton.LeftButton, Qt.MouseButton.NoButton))
    pump(app, 0.3)
    assert w._expanded, "点击行未展开"
    assert w.current_code() == row0.code, f"点击未选中该行: {w.current_code()} != {row0.code}"
    print(f"[ok] 点击展开: {w.current_code()}")

    # 悬停不应展开
    w._set_expanded(False)
    pump(app, 0.2)
    w.enterEvent(None)
    pump(app, 0.3)
    assert not w._expanded, "悬停不应展开"
    print("[ok] 悬停不展开")

    # 移开 3 秒后收起
    w._on_row_click(row0.code)
    pump(app, 0.3)
    assert w._expanded, "点击未展开（3 秒收起前置）"
    w.leaveEvent(None)
    pump(app, 3.5)
    assert not w.detail_widget.isVisible(), "移开 3 秒后未收起"
    print("[ok] 移开 3 秒后收起")

    # 返回按钮：点击立即收起（无需等 3 秒）
    w._on_row_click(w._rows[0].code)
    pump(app, 0.3)
    assert w._expanded, "点击未展开（返回按钮前置）"
    w.back_btn.click()
    pump(app, 0.2)
    assert not w.detail_widget.isVisible(), "返回按钮未立即收起"
    print("[ok] 返回按钮立即收起")

    # 锁定时点返回：收起并解锁
    w.toggle_pinned()
    assert w._pinned and w._expanded, "锁定前置失败"
    w.back_btn.click()
    pump(app, 0.2)
    assert not w._expanded and not w._pinned, "锁定时返回按钮未收起/解锁"
    print("[ok] 锁定时返回按钮收起并解锁")

    # 还原
    w.set_display_count(1)
    w.stocks = ["sh600519", "sz000001", "sz300750"]
    w.index = min(w.index, len(w.stocks) - 1)
    print("[ok] 点击交互还原")

    # 9) 无操作自动轮播
    cfg["auto_switch_seconds"] = 2
    config.save(cfg)
    w2 = FloatQuoteWindow()
    w2.show()
    pump(app, 0.5)
    idx0 = w2.index
    assert wait_until(app, lambda: w2.index != idx0, timeout=8), "自动轮播未切换"
    print(f"[ok] 自动轮播: 无操作 2 秒后自动切换 -> {w2.stocks[w2.index]}")

    # 展开（悬停/锁定）时不应轮播
    w2._set_expanded(True)
    pump(app, 0.3)
    idx_pinned = w2.index
    w2._last_activity = 0.0
    pump(app, 3.0)
    assert w2.index == idx_pinned, "展开期间不应自动切换"
    print("[ok] 展开时暂停自动轮播")

    # 收起后恢复轮播
    w2._set_expanded(False)
    pump(app, 0.3)
    w2._last_activity = 0.0
    assert wait_until(app, lambda: w2.index != idx_pinned, timeout=8), "收起后未恢复轮播"
    print("[ok] 收起后恢复自动轮播")

    # 10) 切换动画效果
    w2.set_switch_effect("slide_h")
    assert w2.switch_effect == "slide_h", "动画设置未生效"
    w2.switch_to((w2.index + 1) % len(w2.stocks))
    pump(app, 0.6)
    assert w2.price_label.text() != "--", "动画后价格标签为空"
    w2.set_switch_effect("pulse")
    w2.switch_to((w2.index + 1) % len(w2.stocks))
    pump(app, 0.7)
    w2.set_switch_effect("off")
    w2.switch_to((w2.index + 1) % len(w2.stocks))
    pump(app, 0.3)
    print("[ok] 切换动画: slide_h / pulse / off 运行无异常")
    w2.close()

    # 10.5) 多行显示时的自动轮播
    cfg["auto_switch_seconds"] = 2
    cfg["stocks"] = ["sh600519", "sz000001", "sz300750", "sz000002"]
    config.save(cfg)
    w3 = FloatQuoteWindow()
    w3.auto_switch_seconds = 2
    w3.show()
    pump(app, 0.5)
    # 显示数量 2 < 股票数 4：窗口应循环滚动（0 -> 1 -> 2 -> 0 ...）
    w3.set_display_count(2)
    w3._set_expanded(False)
    pump(app, 0.2)
    seen = set()
    def collect():
        seen.add(w3.index)
        return len(seen) >= 3
    assert wait_until(app, collect, timeout=12), f"多行窗口未循环滚动: {sorted(seen)}"
    print(f"[ok] 多行轮播循环滚动: index 轨迹覆盖 {sorted(seen)} (max_start=2)")
    # 显示数量 4 >= 股票数 4（全部可见）：窗口不动，高亮循环
    w3.set_display_count(4)
    w3._set_expanded(False)
    pump(app, 0.2)
    idx_fixed = w3.index
    assert wait_until(app, lambda: w3._carousel_row is not None, timeout=8), "全部可见时高亮未循环"
    pump(app, 2.5)
    assert w3.index == idx_fixed, "全部可见时窗口不应滚动"
    print(f"[ok] 全部可见时轮播=高亮循环: row={w3._carousel_row}, index 保持 {idx_fixed}")
    w3.close()

    # 7) 编辑自选股解析
    codes = config.normalize_codes("sh600519 sz000001, sz300750\nbj430047 junk123")
    assert codes == ["sh600519", "sz000001", "sz300750", "bj430047"], f"解析异常: {codes}"
    print("[ok] 自选股解析: ", codes)

    w.close()
    print("\n全部冒烟测试通过 ✅")
    return 0


if __name__ == "__main__":
    sys.exit(main())
