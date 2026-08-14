# -*- coding: utf-8 -*-
"""行情数据封装：腾讯免费接口（实时报价 + 分时数据）。

- 报价:   https://qt.gtimg.cn/q=sh600519,sz000001  (GBK, 可批量)
- 分时:   https://web.ifzq.gtimg.cn/appstock/app/minute/query?code=sh600519  (JSON)
"""
import json
import re
import time
import urllib.parse
import urllib.request
from dataclasses import dataclass
from datetime import datetime
from typing import Optional

HEADERS = {
    "User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36",
}
QUOTE_URL = "https://qt.gtimg.cn/q={codes}"
MINUTE_URL = "https://web.ifzq.gtimg.cn/appstock/app/minute/query?code={code}"
MINUTE_IMG_URL = "https://image.sinajs.cn/newchart/min/n/{code}.gif"
SEARCH_URL = "https://smartbox.gtimg.cn/s3/?v=2&q={q}&t=all"
TIMEOUT = 5


# ---------------------------------------------------------------- 数据模型
@dataclass
class Quote:
    code: str
    name: str = "--"
    price: Optional[float] = None
    prev_close: Optional[float] = None
    open: Optional[float] = None
    high: Optional[float] = None
    low: Optional[float] = None
    change: Optional[float] = None
    change_pct: Optional[float] = None
    volume: Optional[float] = None      # 手
    amount: Optional[float] = None      # 万元
    turnover: Optional[float] = None    # 换手率 %
    pe: Optional[float] = None          # 市盈率
    time: str = ""

    @property
    def ok(self) -> bool:
        return self.price is not None

    @property
    def color_key(self) -> str:
        """red(涨) / green(跌) / gray(平)。"""
        if self.change_pct is None or abs(self.change_pct) < 1e-9:
            return "gray"
        return "red" if self.change_pct > 0 else "green"


# ---------------------------------------------------------------- 工具函数
def _f(s) -> Optional[float]:
    try:
        return float(s)
    except (TypeError, ValueError):
        return None


def _http(url: str) -> str:
    req = urllib.request.Request(url, headers=HEADERS)
    with urllib.request.urlopen(req, timeout=TIMEOUT) as r:
        return r.read().decode("gbk", errors="replace")


def is_trading_time(now: Optional[datetime] = None) -> bool:
    """A股交易时段（含 9:15 集合竞价）。周末与节假日未做完整日历判断。"""
    now = now or datetime.now()
    if now.weekday() >= 5:
        return False
    hm = now.hour * 60 + now.minute
    return (9 * 60 + 15) <= hm <= (11 * 60 + 30) or 13 * 60 <= hm <= 15 * 60


def fmt_price(v: Optional[float]) -> str:
    return f"{v:.2f}" if v is not None else "--"


def fmt_pct(v: Optional[float]) -> str:
    return f"{v:+.2f}%" if v is not None else "--"


def fmt_volume(hands: Optional[float]) -> str:
    """手 -> 万手/亿手。"""
    if hands is None:
        return "--"
    if hands >= 1e8:
        return f"{hands / 1e8:.2f}亿手"
    if hands >= 1e4:
        return f"{hands / 1e4:.2f}万手"
    return f"{hands:.0f}手"


def fmt_amount(wan: Optional[float]) -> str:
    """万元 -> 万/亿。"""
    if wan is None:
        return "--"
    if wan >= 1e4:
        return f"{wan / 1e4:.2f}亿"
    return f"{wan:.0f}万"


# ---------------------------------------------------------------- 接口封装
def fetch_quotes(codes: list) -> dict:
    """批量获取报价。codes: ["sh600519", ...] -> {code: Quote}"""
    if not codes:
        return {}
    text = _http(QUOTE_URL.format(codes=",".join(codes)))
    out = {}
    for m in re.finditer(r'v_(\w+)="([^"]*)"', text):
        code, payload = m.group(1), m.group(2)
        f = payload.split("~")
        if len(f) < 35:
            continue
        q = Quote(code=code, name=f[1] or "--")
        q.price, q.prev_close, q.open = _f(f[3]), _f(f[4]), _f(f[5])
        q.volume = _f(f[6])
        q.high, q.low = _f(f[33]), _f(f[34])
        q.change, q.change_pct = _f(f[31]), _f(f[32])
        q.amount = _f(f[37])
        q.turnover, q.pe = _f(f[38]), _f(f[39])
        if len(f) > 30:
            q.time = f[30]
        out[code] = q
    return out


def fetch_minute(code: str) -> tuple:
    """分时数据 -> (points, date)。

    points: [(time_str, price, avg_price)]，time_str 形如 "0930"。
    """
    text = _http(MINUTE_URL.format(code=code))
    j = json.loads(text)
    data = j["data"][code]["data"]
    date = data.get("date", "")
    points = []
    for p in data.get("data", []):
        # 接口返回形如 "0930 1355.00 227 30758500.00" 的空格分隔字符串，
        # 偶发返回数组，这里两种都兼容。
        if isinstance(p, str):
            parts = p.split()
            if len(parts) < 3:
                continue
            t = parts[0].zfill(4)
            price = _f(parts[1])
            vol, amt = _f(parts[2]), _f(parts[3]) if len(parts) > 3 else (None, None)
        else:
            t = str(p[0]).zfill(4)
            price = _f(p[1])
            vol, amt = _f(p[2]), _f(p[3]) if len(p) > 3 else (None, None)
        if price is None:
            continue
        # 深市第 5 个字段直接给均价，否则用累计成交额/累计成交量折算
        avg = None
        if isinstance(p, (list, tuple)) and len(p) > 4:
            avg = _f(p[4])
        if avg is None and vol and amt is not None:
            avg = amt / (vol * 100.0)
        points.append((t, price, avg))
    return points, date


def fetch_minute_image(code: str) -> bytes:
    """新浪分时图 GIF（腾讯分时 JSON 不稳定时的备选源）。"""
    req = urllib.request.Request(MINUTE_IMG_URL.format(code=code),
                                 headers={**HEADERS, "Referer": "https://finance.sina.com.cn"})
    with urllib.request.urlopen(req, timeout=TIMEOUT) as r:
        return r.read()


def search_stocks(query: str, tries: int = 2) -> list:
    """按代码或名称模糊搜索 A 股，返回 [(code, name), ...]。

    数据源：腾讯 smartbox（返回 unicode-escaped 文本，多结果用 ^ 分隔）。
    """
    q = query.strip()
    if not q:
        return []
    url = SEARCH_URL.format(q=urllib.parse.quote(q))
    raw = None
    for i in range(tries):
        try:
            req = urllib.request.Request(url, headers=HEADERS)
            raw = urllib.request.urlopen(req, timeout=TIMEOUT).read()
            break
        except Exception:
            if i == tries - 1:
                return []
            time.sleep(0.4)
    text = raw.decode("latin-1", errors="replace").encode("latin-1").decode(
        "unicode_escape", errors="replace")
    results = []
    seen = set()
    for m in re.finditer(r'v_hint="([^"]*)"', text):
        for item in m.group(1).split("^"):
            parts = item.split("~")
            if (len(parts) >= 3 and parts[0] in ("sh", "sz", "bj")
                    and re.match(r"^\d{6}$", parts[1]) and parts[2]):
                code = parts[0] + parts[1]
                if code not in seen:
                    seen.add(code)
                    results.append((code, parts[2]))
    return results


if __name__ == "__main__":
    # 命令行冒烟测试
    print("== fetch_quotes ==")
    qs = fetch_quotes(["sh600519", "sz000001", "sz300750"])
    for q in qs.values():
        print(f"  {q.code} {q.name} 现价={q.price} 涨跌={q.change}({q.change_pct}%) "
              f"开={q.open} 高={q.high} 低={q.low} 量={q.volume}手 额={q.amount}万 "
              f"换手={q.turnover}% 时间={q.time} 颜色={q.color_key}")
    print("== fetch_minute ==")
    pts, date = fetch_minute("sh600519")
    print(f"  日期={date} 点数={len(pts)}")
    if pts:
        print(f"  首点={pts[0]} 末点={pts[-1]}")
