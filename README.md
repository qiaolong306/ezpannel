# EZPannel - 轻量级服务器管理面板

## 项目简介

EZPannel 是一套基于 ASP.NET Core MVC 的轻量级服务器管理面板，用于替代 SSH 工具对多台服务器做统一管理。它由两部分组成：

- **EZPannel.Web**：Web 管理面板（.NET 10 + ASP.NET Core MVC + SignalR）
- **EZPannelClient**：运行在被管服务器上的 Python Agent（纯标准库，HTTP + WebSocket）

两者通过 Agent 通讯，不依赖 SSH 服务，因此 Windows 服务器无需额外开启 OpenSSH 也能被纳管。

## 架构

```
浏览器 ──HTTP──▶ EZPannel.Web ──HTTP(POST /api/execute)──▶ EZPannelClient
          │                    ──WebSocket(/ws)────────▶   (目标机器)
          └─SignalR(/terminalHub)─┘
```

- 面板与浏览器之间走 Cookie / JWT 认证
- 面板与 Agent 之间走 `X-Auth-Key` 鉴权，一台机器一个唯一 Key
- 交互终端由面板中转：浏览器 ⇄ SignalR ⇄ 面板 ⇄ WebSocket ⇄ Agent PTY

## 功能特性

### 服务器监控
- 多服务器卡片总览，状态并发拉取（单台失败不影响其它卡片）
- CPU / 内存 / 磁盘环形进度图与使用率
- 进程列表：按进程名、CPU、内存三列点击排序，支持分页与结束进程
- 进程详情查看

### 远程终端
- 浮动终端窗口，页面内随开随用，支持多服务器切换
- xterm.js 渲染，PTY 模式下方向键、命令历史、vim/top 等全屏程序均可用
- 选中文本 + Ctrl+C 复制（非安全上下文自动回退到 execCommand）

### 文件管理
- 可视化文件浏览器，支持进入目录、查看大小与权限
- 文件上传 / 下载 / 在线编辑 / 新建目录 / 删除

### 脚本管理
- 脚本按操作系统分类（Windows / Linux）管理
- 手动执行、执行历史记录
- 定时任务（Quartz 调度），支持增删与手动触发

### 数据库管理
- 支持 MySQL / PostgreSQL / SQL Server
- 库、表、视图、函数浏览，SQL 执行与结果展示
- 常用查询保存
- 备份计划、手动备份、备份恢复（恢复列表支持分页与按名称/日期筛选）

### 系统管理
- 用户管理（含超级管理员 admin 保护）
- 系统设置、日志查看（按天落盘的日志文件）
- 登录验证码

## 系统要求

**面板端（EZPannel.Web）**

- .NET 10.0 运行时或更高
- 支持 Windows / Linux
- 内置 SQLite，无需额外安装数据库（首次启动自动建库 `ezpannel.db`）

**Agent 端（EZPannelClient）**

- Python 3.8+
- 无需第三方依赖（纯标准库）
- Windows 上如需完整交互终端体验，建议安装 `pywinpty`（详见下文）

## 快速开始

### 1. 部署面板

方式一：直接运行

```bash
# Windows
start.bat                 # 等价于 EZPannel.Web.exe --urls "http://*:8000"

# Linux
chmod +x start.sh && ./start.sh
```

方式二：手动构建

```bash
dotnet build
dotnet publish -c Release -r win-x64   # 或 -r linux-x64
```

方式三：Docker

```bash
docker build -t ezpannel .
docker run -d -p 80:80 --name ezpannel ezpannel
```

### 2. 在被管服务器上启动 Agent

Agent 脚本随面板一起发布（`Client/EZPannelClient.py`），也可从首页「客户端下载」获取。

```bash
# 默认监听 0.0.0.0:9527，Key 首次启动自动生成并持久化
python EZPannelClient.py

# 指定监听地址 / 端口 / 工作目录
python EZPannelClient.py --host 0.0.0.0 --port 9527 --cwd D:/work

# 重新生成 Key（更换 Key 后需同步更新面板里的服务器配置）
python EZPannelClient.py --regenerate-key
```

启动后控制台会打印本机 Key，把它连同 `IP:端口` 一起填进面板的「添加服务器」即可。

### 3. 初始登录

- 默认地址：`http://your-server-ip:8000`
- 默认账号：`admin`
- 默认密码：`admin123`

首次登录后请及时修改密码。

## Agent 说明

### 接口一览

| 方法 | 路径 | 说明 |
|---|---|---|
| GET | `/health` | 健康检查，无需鉴权 |
| POST | `/api/execute` | 执行一次性命令，body `{"command","cwd","timeout"}` |
| WS | `/ws?key=<key>` | 交互式终端长连接，支持 `cols` / `rows` |
| GET | `/api/files/list?path=` | 列目录 |
| GET | `/api/files/download?path=` | 下载文件 |
| GET | `/api/files/content?path=` | 读取文本内容 |
| POST | `/api/files/upload?path=` | 上传文件 |
| POST | `/api/files/save` | 保存文本 |
| POST | `/api/files/mkdir` | 新建目录 |
| POST | `/api/files/delete` | 删除文件/目录 |

除 `/health` 外均需携带 `X-Auth-Key: <key>` 或 `Authorization: Bearer <key>`。

### 交互终端与 PTY

Agent 的终端能力按平台分级：

- **Linux / macOS**：使用标准库 `pty`，功能完整
- **Windows**：使用 `pywinpty`（ConPTY）。缺失时启动阶段会自动 `pip install`，
  也可用 `--no-auto-install` 或环境变量 `EZPANNEL_NO_AUTO_INSTALL=1` 关闭自动安装
- **都不可用时**：退回管道模式，此时退格失效、输入行不回显、Ctrl+C 与全屏程序不可用

因此 Windows 被管机若出现「不能退格 / 输入输出错乱」，基本是 `pywinpty` 没装上，可手动执行：

```bash
pip install pywinpty
```

## 监控数据口径

面板对 Windows 与 Linux 采用各自的原生数据源，但输出统一为「整机 0-100 百分比」，两端可直接横向对比。

**Windows**

| 指标 | 数据源 | 说明 |
|---|---|---|
| CPU 使用率 | `Win32_PerfRawData_PerfOS_Processor` 差分 | 取 `_Total` 的 `PercentIdleTime` / `Timestamp_PerfTime`，间隔 800ms 两次采样，`cpu = 100 - ΔIdle/ΔTs × 100` |
| 核心数 | `NumberOfLogicalProcessors` 按插槽求和 | 与 Linux `nproc` 同口径（逻辑处理器，非物理核心） |
| 内存 | `PerfOS_Memory.AvailableBytes` | `used = 总量 - 可用`，与任务管理器「可用」一致 |

说明：`Win32_Processor.LoadPercentage` 抖动大且按物理插槽返回数组，不适合直接使用；
`Win32_PerfFormattedData_PerfOS_Processor.PercentProcessorTime` 是「熟」计数器，新会话中可能稳定为 0，
故仅作为兜底。

**Linux**

| 指标 | 数据源 |
|---|---|
| CPU 使用率 | `/proc/stat` 的 `cpu` 汇总行，间隔 1 秒两次差分 |
| 内存 | `free -m` 的 used / total |
| 磁盘 | `df -h` |
| 核心数 | `nproc` |

进程列表的 CPU 百分比在两侧均按逻辑处理器数归一化后再除以核心数，与上面的整机百分比口径一致。

## 项目结构

```
EZPannel/
├── Client/
│   └── EZPannelClient.py        # 被管服务器上的 Python Agent
└── EZPannel.Web/
    ├── Controllers/             # 控制器（页面 + API）
    ├── Views/                   # Razor 视图
    │   └── Shared/
    │       ├── _FloatingTerminal.cshtml   # 浮动终端组件
    │       └── _SqlTerminal.cshtml        # SQL 执行终端
    ├── Models/                  # 数据模型
    ├── DTOs/                    # 传输对象
    ├── Data/                    # EF Core 上下文（SQLite）
    ├── Services/                # Agent 通讯、备份、脚本调度、日志
    ├── Hubs/                    # SignalR（终端中转）
    ├── Backups/                 # 数据库备份产物
    └── wwwroot/                 # 静态资源
```

## 开发指南

### 环境搭建

1. 安装 .NET 10.0 SDK
2. 安装 Visual Studio 2022 或 VS Code
3. 克隆项目代码并还原依赖

```bash
dotnet restore
```

### 构建与运行

```bash
# 构建
dotnet build

# 本地运行（默认 8000 端口）
dotnet run --urls "http://*:8000"
```

### 配置项

配置文件 `EZPannel.Web/appsettings.json`：

| 键 | 说明 |
|---|---|
| `ConnectionStrings:DefaultConnection` | 面板自身数据库，默认 `Data Source=ezpannel.db` |
| `Jwt:Key` | JWT 签名密钥，**生产环境务必替换** |
| `Jwt:Issuer` / `Jwt:Audience` | JWT 签发方与受众 |

被管服务器的数据库连接信息（MySQL / PostgreSQL / SQL Server）在面板「数据库管理」中添加，不写入配置文件。

## 常见问题

**Q：Windows 服务器终端不能退格、输入和回显错乱？**
A：被管机未安装 `pywinpty`，Agent 退回了管道模式。执行 `pip install pywinpty` 后重启 Agent 即可，Agent 也会尝试自动安装。

**Q：终端里选中文本按 Ctrl+C 变成了中断信号 `^C`？**
A：面板已拦截：有选中内容时 Ctrl+C 执行复制并阻止下发；无选中时仍是正常的中断信号。

**Q：服务器卡片 CPU 一直显示 0%？**
A：多为 WMI 计数器取不到值。本项目的 Windows 采集已改为原始计数器差分，若仍为 0，请检查被管机的性能计数器服务是否正常（`PerfOS` 相关 WMI 类是否可查询）。

**Q：添加服务器后提示连接失败？**
A：依次检查：Agent 是否已启动、`IP:端口` 是否可达（默认 9527）、防火墙是否放行、Key 是否与 Agent 打印的一致。

**Q：备份文件存放在哪？**
A：默认在 `EZPannel.Web/Backups/<数据库类型>/<日期>/` 下，可在「数据库管理 - 恢复」中查看与下载。

## 贡献指南

1. Fork 本项目
2. 创建功能分支
3. 提交代码
4. 发起 Pull Request

## 许可证

本项目采用 MIT 许可证，详见 [LICENSE](LICENSE) 文件。

## 联系方式

如有问题或建议，欢迎通过 GitHub Issues 提出。

---