# -*- coding: utf-8 -*-
"""配置读写：config.json（自选股、刷新间隔、窗口位置等）。"""
import json
import re
import sys
from pathlib import Path


def app_dir() -> Path:
    """应用数据目录：打包成 exe 时取 exe 所在目录；源码运行时取项目目录。

    若直接用 Path(__file__)，打包后 __file__ 指向临时解压目录，
    config.json 会被写到临时目录、退出即丢。
    """
    if getattr(sys, "frozen", False):
        return Path(sys.executable).resolve().parent
    return Path(__file__).resolve().parent


APP_DIR = app_dir()
CONFIG_PATH = APP_DIR / "config.json"

DEFAULTS = {
    "stocks": ["sh600519", "sz000001", "sz300750"],  # 默认: 茅台/平安银行/宁德时代
    "refresh_seconds": 3,        # 报价刷新间隔
    "chart_refresh_seconds": 30,  # 分时图刷新间隔
    "auto_switch_seconds": 10,   # 无操作自动轮播间隔（0=关闭）
    "switch_effect": "fade",     # 切换动画: off/fade/slide_h/slide_v/pulse
    "display_count": 1,          # 悬浮条同时显示的股票数量（1-6）
    "pos": [200, 120],           # 窗口位置 (x, y)
    "size_collapsed": [167, 19],  # 收起态尺寸（可拖拽调整，自动记住）
    "size_expanded": [192, 156],  # 展开态尺寸（可拖拽调整，自动记住）
    "always_on_top": True,
}

CODE_RE = re.compile(r"^(?:sh|sz|bj)\d{6}$", re.IGNORECASE)


def normalize_codes(text: str) -> list:
    """从用户输入（空格/逗号/换行分隔）解析股票代码，非法项忽略。"""
    codes = []
    for tok in re.split(r"[\s,，、;；]+", text.strip().lower()):
        if CODE_RE.match(tok):
            codes.append(tok)
    return codes


def load() -> dict:
    cfg = dict(DEFAULTS)
    try:
        if CONFIG_PATH.exists():
            data = json.loads(CONFIG_PATH.read_text(encoding="utf-8"))
            if isinstance(data, dict):
                cfg.update(data)
    except (json.JSONDecodeError, OSError):
        pass
    # 过滤非法代码，防止配置被改坏
    cfg["stocks"] = [s for s in cfg.get("stocks", []) if CODE_RE.match(str(s).lower())] or list(DEFAULTS["stocks"])
    return cfg


def save(cfg: dict):
    try:
        CONFIG_PATH.write_text(
            json.dumps(cfg, ensure_ascii=False, indent=2), encoding="utf-8")
    except OSError:
        pass


if __name__ == "__main__":
    c = load()
    print(json.dumps(c, ensure_ascii=False, indent=2))
