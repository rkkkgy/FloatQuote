# FloatQuote — 悬浮行情小工具（C# / WPF）

Python 原版 [FloatQuote](../FloatQuote) 的 C# 重写：常驻桌面最上层的极简 A 股行情悬浮窗。
平时只显示一条窄价格条（可同时显示多只股票），**点击**某一行展开详细报价与当日分时图，
移开鼠标 3 秒自动收起，也可以点「返回」立即收起。数据来自腾讯免费行情接口。

> .NET 8 + WPF，仅支持 Windows（红涨绿跌）。

## 功能特性

- **置顶悬浮窗**：无边框、半透明圆角、可拖拽移动，位置自动记住
- **多行显示**：一次显示 1–6 只股票（名称 + 价格 + 涨跌幅），滚轮滚动窗口
- **点击展开**：点击某一行即展开该股票的详细报价 + 自绘分时图（均价线 / 昨收线 / 涨跌填充）
- **立即返回**：展开页底部「返回」按钮，无需等待 3 秒自动收起
- **自动轮播**：无操作超过设定秒数自动轮播（窗口循环滚动 / 全部可见时高亮行循环）
- **切换动画**：淡入淡出、左右滑入、上下滚动、脉冲闪烁 4 种预设
- **系统托盘**：图标随涨跌变色、悬停显示实时价格、可隐藏悬浮窗
- **自选股管理**：按名称或代码搜索添加、排序、删除
- **可调大小**：拖拽边缘/四角调整，收起/展开态各自记忆尺寸
- **可靠性**：腾讯分时源不稳定时自动降级为新浪分时图，指数退避重试

## 运行

需要 [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)。

```bash
dotnet run
```

## 使用

| 操作 | 效果 |
|---|---|
| 鼠标点击股票行 | 展开详细报价 + 分时图（显示被点击的那只股票） |
| 鼠标移开 | 3 秒后自动收起 |
| 展开页底部「返回」按钮 | 立即收起，无需等待（已锁定展开时同时解锁） |
| 鼠标悬停 | 不展开（仅高亮所在行、暂停自动轮播） |
| 左键拖拽 | 移动悬浮窗，位置自动记住 |
| 拖拽窗口边缘/四角 | 调整窗口大小（收起/展开态各自记忆尺寸） |
| 滚轮 | 滚动悬浮条窗口（切换上一批 / 下一批自选股） |
| 双击 | 锁定展开（钉住）；再双击解锁 |
| 右键 | 切换股票、自选股管理、刷新间隔、显示数量、自动轮播、切换动画、置顶开关、隐藏/显示、退出 |

## 配置

配置保存在同目录 `config.json`（首次运行自动生成，可参考 `config.example.json`）：

| 配置项 | 说明 | 默认 |
|---|---|---|
| `stocks` | 自选股列表（`sh`/`sz`/`bj` + 6 位数字） | 茅台/平安银行/宁德时代 |
| `display_count` | 悬浮条同时显示的股票数量 | 1 |
| `auto_switch_seconds` | 无操作自动轮播间隔（0=关闭） | 10 |
| `switch_effect` | 切换动画（off/fade/slide_h/slide_v/pulse） | fade |
| `refresh_seconds` | 报价刷新间隔 | 3 |
| `chart_refresh_seconds` | 分时图刷新间隔 | 30 |
| `pos` / `size_collapsed` / `size_expanded` | 窗口位置与尺寸 | — |

右键菜单 →「自选股管理」可图形化编辑自选股（按名称或代码搜索添加）。

`dotnet run` 时配置写在项目目录；发布成 exe 后写在 exe 同目录。

## 文件结构

```
FloatQuote.Win/
├── App.xaml / App.xaml.cs   # 入口、PID、冒烟/截图模式
├── MainWindow.xaml(.cs)     # 悬浮窗：多行列表、点击展开、动画、托盘、后台刷新
├── Quotes.cs                # 腾讯行情封装（报价 / 分时 / 股票搜索）
├── MinuteChart.cs           # 分时图自绘（含新浪降级图）
├── Config.cs                # config.json 读写
├── StockEditDialog.cs       # 自选股管理
├── Smoke.cs                 # 冒烟测试
├── scripts/                 # Windows 启停脚本
├── config.example.json
└── LICENSE
```

## 测试

```bash
dotnet run -- --smoke
```

测试使用临时配置，不会污染你的 `config.json`；部分网络相关的断言在接口不可用时会自动跳过。

## 打包成安装包

需要 [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)。脚本会自动安装 [Inno Setup](https://jrsoftware.org/isinfo.php)（若本机没有）。

```powershell
powershell -ExecutionPolicy Bypass -File scripts\build-installer.ps1
```

生成 `dist\FloatQuote-Setup-1.1.0.exe`（当前用户安装，无需管理员）。安装目录为 `%LocalAppData%\Programs\FloatQuote`，自选股配置写在 `%LocalAppData%\FloatQuote\config.json`，卸载时保留配置。

只想得到免安装 exe：

```bash
dotnet publish -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true ^
  -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:EnableCompressionInSingleFile=true ^
  -o publish
```

`dotnet run` 时配置写在项目目录；安装版写在 `%LocalAppData%\FloatQuote`。

## 免责声明

- 数据来自腾讯/新浪公开行情接口，仅供学习研究，**不构成任何投资建议**
- 接口为免费公开服务，偶有不稳定或变动；本项目会尽力兼容，但不保证可用性
- 节假日未做完整日历判断，休市时显示最近一次收盘数据

## 许可

[MIT License](LICENSE)
