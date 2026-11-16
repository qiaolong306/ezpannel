#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
EZPannelClient —— 基于 HTTP + WebSocket 的轻量级命令转发代理（Agent）

用途:
    替代 SSH，在目标机器上启动监听服务：
    - HTTP      : 一次性命令执行（健康检查、状态查询、脚本执行等）
    - WebSocket : 交互式终端会话，实时、持续地回传输出（支持长连接反馈）

特性:
    1. 纯标准库实现，无第三方依赖，Windows / Linux 均可直接运行。
    2. 首次启动随机生成一个唯一 Key 并持久化到本地，之后复用（一台机器一个唯一 Key）。
    3. 所有命令执行 / 会话建立都要求携带正确的 Key，防止未授权调用。
    4. 交互终端优先使用伪终端(PTY)：Windows 上装了 pywinpty 则自动启用，
       未安装时启动阶段会自动 pip 安装（可用 --no-auto-install 或环境变量
       EZPANNEL_NO_AUTO_INSTALL=1 关闭）；Linux/macOS 使用标准库 pty；
       两者都不可用时退回管道模式。
       PTY 模式下 Ctrl+C、方向键、命令历史、vim/top 等全屏程序均可用。

用法:
    python EZPannelClient.py [--host 0.0.0.0] [--port 9527] [--cwd <工作目录>]
    python EZPannelClient.py --regenerate-key      # 重新生成 Key

接口:
    GET  /health        健康检查（无需鉴权）
    POST /api/execute   执行命令（需鉴权，一次性返回结果）
        Header: X-Auth-Key: <key>  或  Authorization: Bearer <key>
        Body:   {"command": "echo hi", "cwd": "D:/work", "timeout": 60}

    GET  /ws            交互式终端（需鉴权，WebSocket 长连接）
        URL:  ws://host:port/ws?key=<key>[&cols=120&rows=30]
        收:  文本帧   -> 写入终端的 stdin（输入命令 / 按键）
        收:  二进制帧 -> 控制指令 {"type":"resize","cols":120,"rows":30}
        发:  文本帧   <- 终端输出（stdout + stderr 合并，实时回传）

    文件操作（需鉴权，Header: X-Auth-Key）:
        GET  /api/files/list?path=...        列目录
        GET  /api/files/download?path=...    下载（二进制流）
        GET  /api/files/content?path=...     读取文本内容
        POST /api/files/upload?path=...      上传（二进制 body）
        POST /api/files/save                 保存文本 {"path", "content"}
        POST /api/files/mkdir                新建目录 {"path"}
        POST /api/files/delete               删除 {"path", "isDirectory"}
"""

import argparse
import atexit
import base64
import hashlib
import json
import os
import platform
import secrets
import shutil
import socket
import stat
import struct
import subprocess
import sys
import threading
from datetime import datetime
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import urlparse, parse_qs

if os.name != "nt":
    import fcntl
    import pty
    import select
    import termios

# Windows 伪终端（ConPTY）依赖：可选包 pywinpty，缺失时可在启动阶段自动安装。
# 没有 PTY 时终端会退回管道模式：退格失效、输入行不回显、Ctrl+C 与全屏程序不可用。
_WINPTY_PACKAGE = "pywinpty"
_WINPTY_DISABLE_ENV = "EZPANNEL_NO_AUTO_INSTALL"
_WinPtyProcess = None


def _log(msg):
    """带时间戳输出到 stderr，和 HTTP 访问日志格式保持一致。"""
    sys.stderr.write("[%s] %s\n" % (datetime.now().strftime("%Y-%m-%d %H:%M:%S"), msg))


def _import_winpty():
    """尝试导入 pywinpty，返回 PtyProcess 类；不可用时返回 None。"""
    try:
        import importlib
        import site

        importlib.invalidate_caches()
        # --user 安装的包位于用户级 site-packages，必要时补进 sys.path
        try:
            user_site = site.getusersitepackages()
            paths = [user_site] if isinstance(user_site, str) else list(user_site)
            for p in paths:
                if p and p not in sys.path and os.path.isdir(p):
                    sys.path.append(p)
        except Exception:
            pass
        from winpty import PtyProcess

        return PtyProcess
    except Exception:
        return None


def ensure_winpty(auto_install=True):
    """Windows 上确保 pywinpty 可用：已安装直接返回，缺失时尝试自动 pip 安装。

    返回 PtyProcess 类；非 Windows、安装失败或主动禁用时返回 None（退回管道模式）。
    """
    if os.name != "nt":
        return None

    proc_cls = _import_winpty()
    if proc_cls is not None:
        return proc_cls

    if not auto_install:
        _log("未检测到 pywinpty，已按 --no-auto-install 跳过自动安装")
        return None

    if os.environ.get(_WINPTY_DISABLE_ENV, "").strip().lower() not in ("", "0", "false"):
        _log("检测到 %s，跳过 pywinpty 自动安装" % _WINPTY_DISABLE_ENV)
        return None

    if not sys.executable:
        _log("无法定位当前 Python 解释器，跳过 pywinpty 自动安装")
        return None

    _log("未检测到 pywinpty，正在自动安装（首次安装需要联网）...")

    base_cmd = [
        sys.executable, "-m", "pip", "install",
        "--disable-pip-version-check", "--no-warn-script-location",
        _WINPTY_PACKAGE,
    ]

    # 先装到当前环境；权限不足（受保护目录）时退回 --user 用户级安装
    for extra in ([], ["--user"]):
        cmd = base_cmd + extra
        try:
            res = subprocess.run(
                cmd,
                stdout=subprocess.PIPE,
                stderr=subprocess.STDOUT,
                text=True,
                timeout=180,
                creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
            )
        except subprocess.TimeoutExpired:
            _log("自动安装超时（180s），可能是网络不可达")
            continue
        except Exception as ex:
            _log("自动安装调用失败：%s" % ex)
            continue

        if res.returncode == 0:
            proc_cls = _import_winpty()
            if proc_cls is not None:
                _log("pywinpty 安装成功，已启用 PTY 模式")
                return proc_cls
            _log("pywinpty 已安装但导入失败，退回管道模式")
            return None

        tail = (res.stdout or "").strip().splitlines()[-3:]
        if tail:
            _log("pip 输出：%s" % " | ".join(tail))

    _log("pywinpty 自动安装失败，可手动执行：%s -m pip install pywinpty" % sys.executable)
    return None

SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
CONFIG_FILE = os.path.join(SCRIPT_DIR, "ezpannel_client.json")

DEFAULT_HOST = "0.0.0.0"
DEFAULT_PORT = 9527
MAX_BODY_SIZE = 10 * 1024 * 1024  # 10MB

# 终端默认尺寸（列 / 行），前端可通过 ?cols=&rows= 或 resize 控制帧覆盖
DEFAULT_COLS = 120
DEFAULT_ROWS = 30
MIN_COLS, MAX_COLS = 20, 500
MIN_ROWS, MAX_ROWS = 5, 200

# WebSocket (RFC 6455)
WS_GUID = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11"
OPCODE_CONT = 0x0
OPCODE_TEXT = 0x1
OPCODE_BINARY = 0x2
OPCODE_CLOSE = 0x8
OPCODE_PING = 0x9
OPCODE_PONG = 0xA

# 活动终端注册表：Agent 退出时统一回收，避免残留 shell 进程
_TERMINALS = set()
_TERMINALS_LOCK = threading.Lock()

# WebSocket 保活心跳间隔（秒）：用于发现已经断开的连接，及时回收终端
WS_HEARTBEAT_SECONDS = 20


def load_or_create_key(config_path, regenerate=False):
    """读取已持久化的 Key；不存在或要求重新生成时，创建新 Key 并写入本地。"""
    if not regenerate and os.path.exists(config_path):
        try:
            with open(config_path, "r", encoding="utf-8") as f:
                cfg = json.load(f)
            key = cfg.get("key")
            if key:
                return key, cfg
        except (json.JSONDecodeError, OSError):
            pass

    key = secrets.token_hex(32)
    cfg = {
        "key": key,
        "machine": platform.node(),
        "system": platform.system(),
        "created_at": datetime.now().isoformat(),
    }
    with open(config_path, "w", encoding="utf-8") as f:
        json.dump(cfg, f, ensure_ascii=False, indent=2)
    return key, cfg


def run_command(command, cwd, timeout):
    """执行一次性 shell 命令并返回统一结构的结果。"""
    try:
        proc = subprocess.Popen(
            command,
            shell=True,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            cwd=cwd or None,
        )
        try:
            out, err = proc.communicate(timeout=timeout)
        except subprocess.TimeoutExpired:
            proc.kill()
            out, err = proc.communicate()
            return {
                "success": False,
                "exit_code": None,
                "stdout": (out or b"").decode("utf-8", "replace"),
                "stderr": (err or b"").decode("utf-8", "replace") + "\n[Timeout] 命令执行超时",
                "timed_out": True,
            }
        return {
            "success": proc.returncode == 0,
            "exit_code": proc.returncode,
            "stdout": (out or b"").decode("utf-8", "replace"),
            "stderr": (err or b"").decode("utf-8", "replace"),
            "timed_out": False,
        }
    except Exception as e:
        return {
            "success": False,
            "exit_code": None,
            "stdout": "",
            "stderr": str(e),
            "timed_out": False,
        }


class PipeTerminal:
    """无 PTY 时的退路：直接用管道读写 shell 进程（不支持 Ctrl+C / 全屏程序）。"""

    def __init__(self, argv, cwd=None):
        self.proc = subprocess.Popen(
            argv,
            cwd=cwd,
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT,
            creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
        )

    def read(self):
        """阻塞读取一段输出；返回 b"" 表示进程已结束。"""
        try:
            return self.proc.stdout.read1(4096) or b""
        except Exception:
            return b""

    def write(self, data):
        self.proc.stdin.write(data)
        self.proc.stdin.flush()

    def resize(self, cols, rows):
        pass

    def is_alive(self):
        return self.proc.poll() is None

    def close(self):
        try:
            self.proc.stdin.close()
        except Exception:
            pass
        try:
            self.proc.terminate()
        except Exception:
            pass
        try:
            self.proc.wait(timeout=3)
        except Exception:
            try:
                self.proc.kill()
            except Exception:
                pass


class WinPtyTerminal:
    """基于 pywinpty 的 Windows 伪终端，保留控制台交互能力。"""

    def __init__(self, argv, cwd=None, cols=DEFAULT_COLS, rows=DEFAULT_ROWS):
        self._size = (rows, cols)
        self.proc = _WinPtyProcess.spawn(
            argv, cwd=cwd, dimensions=(rows, cols)
        )

    def read(self):
        """阻塞读取一段输出；返回 b"" 表示进程已结束。"""
        while True:
            try:
                data = self.proc.read()
            except EOFError:
                return b""
            except Exception:
                return b""
            if data:
                if isinstance(data, bytes):
                    return data
                return data.encode("utf-8", "replace")
            if not self.is_alive():
                return b""

    def write(self, data):
        # pywinpty 3.x 的 write 只接受 str
        if isinstance(data, bytes):
            data = data.decode("utf-8", "replace")
        self.proc.write(data)

    def resize(self, cols, rows):
        # 尺寸没变就不要下发：ConPTY 每次 resize 都会整屏重绘，
        # 重复的 resize 会让前端光标位置和缓冲区错位，输入回显就会跑到上面几行。
        if (rows, cols) == self._size:
            return
        self._size = (rows, cols)
        try:
            self.proc.setwinsize(rows, cols)
        except Exception:
            pass

    def is_alive(self):
        try:
            return self.proc.isalive()
        except Exception:
            return False

    def close(self):
        try:
            self.proc.terminate(force=True)
        except Exception:
            pass


class PosixPtyTerminal:
    """Linux/macOS 伪终端，基于标准库 pty + select 实现。"""

    def __init__(self, argv, cwd=None, cols=DEFAULT_COLS, rows=DEFAULT_ROWS):
        self.master, slave = pty.openpty()
        self._set_size(slave, cols, rows)
        env = dict(os.environ)
        env.setdefault("TERM", "xterm-256color")
        self.proc = subprocess.Popen(
            argv,
            cwd=cwd,
            stdin=slave,
            stdout=slave,
            stderr=slave,
            start_new_session=True,
            env=env,
        )
        os.close(slave)

    @staticmethod
    def _set_size(fd, cols, rows):
        try:
            fcntl.ioctl(fd, termios.TIOCSWINSZ, struct.pack("HHHH", rows, cols, 0, 0))
        except Exception:
            pass

    def read(self):
        """阻塞读取一段输出；返回 b"" 表示进程已结束。"""
        while True:
            try:
                ready, _, _ = select.select([self.master], [], [], 0.5)
            except Exception:
                return b""
            if ready:
                try:
                    return os.read(self.master, 4096)
                except OSError:
                    return b""
            if self.proc.poll() is not None:
                return b""

    def write(self, data):
        try:
            os.write(self.master, data)
        except OSError:
            pass

    def resize(self, cols, rows):
        self._set_size(self.master, cols, rows)

    def is_alive(self):
        return self.proc.poll() is None

    def close(self):
        try:
            os.close(self.master)
        except Exception:
            pass
        try:
            self.proc.terminate()
        except Exception:
            pass
        try:
            self.proc.wait(timeout=3)
        except Exception:
            try:
                self.proc.kill()
            except Exception:
                pass


class AgentServer(ThreadingHTTPServer):
    """携带 Agent 配置的线程化 HTTP 服务器。"""

    def __init__(self, addr, handler, agent_key, base_cwd):
        super().__init__(addr, handler)
        self.agent_key = agent_key
        self.base_cwd = base_cwd
        self.daemon_threads = True


class AgentHandler(BaseHTTPRequestHandler):
    server_version = "EZPannelAgent/1.0"

    # ------------------------------------------------------------------ 工具
    def _send_json(self, code, payload):
        body = json.dumps(payload, ensure_ascii=False).encode("utf-8")
        self.send_response(code)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def _extract_key(self, query=None):
        """从 query 参数、X-Auth-Key 头、Authorization 头中提取 Key。"""
        key = None
        if query is not None:
            key = query.get("key", [None])[0]
        if not key:
            key = self.headers.get("X-Auth-Key")
        if not key:
            auth = self.headers.get("Authorization", "")
            if auth.lower().startswith("bearer "):
                key = auth[7:].strip()
        return key

    def _authorized(self, query=None):
        key = self._extract_key(query)
        return bool(key) and secrets.compare_digest(key, self.server.agent_key)

    def _resolve_cwd(self, cwd):
        if not cwd:
            return self.server.base_cwd
        if os.path.isabs(cwd):
            return cwd
        base = self.server.base_cwd or SCRIPT_DIR
        return os.path.join(base, cwd)

    def _log_message(self, fmt, *args):
        sys.stderr.write(
            "[%s] %s\n" % (datetime.now().strftime("%Y-%m-%d %H:%M:%S"), fmt % args)
        )

    # ------------------------------------------------------------------ 路由
    def do_GET(self):
        parsed = urlparse(self.path)
        path = parsed.path
        query = parse_qs(parsed.query)

        if path == "/health":
            self._send_json(
                200,
                {
                    "status": "ok",
                    "machine": platform.node(),
                    "system": platform.system(),
                    "time": datetime.now().isoformat(),
                },
            )
        elif path == "/ws":
            if self.headers.get("Upgrade", "").lower() == "websocket":
                self._handle_ws(query)
            else:
                self._send_json(400, {"success": False, "error": "websocket upgrade required"})
        elif path == "/api/files/list":
            self._handle_list_files(query)
        elif path == "/api/files/download":
            self._handle_download_file(query)
        elif path == "/api/files/content":
            self._handle_file_content(query)
        else:
            self._send_json(404, {"success": False, "error": "not found"})

    def do_POST(self):
        path = urlparse(self.path).path
        if path == "/api/execute":
            self._handle_execute()
        elif path == "/api/files/upload":
            self._handle_upload_file()
        elif path == "/api/files/save":
            self._handle_save_file()
        elif path == "/api/files/mkdir":
            self._handle_mkdir()
        elif path == "/api/files/delete":
            self._handle_delete()
        else:
            self._send_json(404, {"success": False, "error": "not found"})

    # -------------------------------------------------------------- 命令执行
    def _handle_execute(self):
        if not self._require_auth():
            return

        raw = self._read_body()
        if raw is None:
            self._send_json(400, {"success": False, "error": "invalid body size"})
            return

        try:
            data = json.loads(raw.decode("utf-8"))
        except (json.JSONDecodeError, UnicodeDecodeError):
            self._send_json(400, {"success": False, "error": "invalid json"})
            return

        command = data.get("command") or data.get("cmd")
        if not command:
            self._send_json(400, {"success": False, "error": "command required"})
            return

        cwd = self._resolve_cwd(data.get("cwd"))
        timeout = data.get("timeout", 120)
        try:
            timeout = float(timeout)
        except (TypeError, ValueError):
            timeout = 120

        result = run_command(command, cwd, timeout)
        self._send_json(200, result)

    # -------------------------------------------------------------- 文件操作
    def _require_auth(self, query=None):
        if not self._authorized(query):
            self._send_json(401, {"success": False, "error": "unauthorized"})
            return False
        return True

    def _read_body(self):
        length = int(self.headers.get("Content-Length", 0) or 0)
        if length <= 0 or length > MAX_BODY_SIZE:
            return None
        return self.rfile.read(length)

    def _resolve_fs_path(self, p):
        if not p:
            return None
        if os.path.isabs(p):
            return os.path.abspath(p)
        base = self.server.base_cwd or SCRIPT_DIR
        return os.path.abspath(os.path.join(base, p))

    def _send_binary(self, data):
        self.send_response(200)
        self.send_header("Content-Type", "application/octet-stream")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def _handle_list_files(self, query):
        if not self._require_auth(query):
            return
        p = self._resolve_fs_path(query.get("path", ["/"])[0])
        if not p or not os.path.isdir(p):
            self._send_json(404, {"success": False, "error": "directory not found"})
            return
        items = []
        try:
            with os.scandir(p) as it:
                for e in it:
                    try:
                        st = e.stat()
                    except OSError:
                        continue
                    items.append({
                        "name": e.name,
                        "path": e.path,
                        "size": st.st_size if e.is_file() else 0,
                        "isDirectory": e.is_dir(),
                        "lastModified": datetime.fromtimestamp(st.st_mtime).isoformat(),
                        "permissions": oct(stat.S_IMODE(st.st_mode)),
                    })
        except OSError as ex:
            self._send_json(500, {"success": False, "error": str(ex)})
            return
        items.sort(key=lambda x: (not x["isDirectory"], x["name"].lower()))
        self._send_json(200, {"success": True, "items": items})

    def _handle_download_file(self, query):
        if not self._require_auth(query):
            return
        p = self._resolve_fs_path(query.get("path", [None])[0])
        if not p or not os.path.isfile(p):
            self._send_json(404, {"success": False, "error": "file not found"})
            return
        try:
            with open(p, "rb") as f:
                self._send_binary(f.read())
        except OSError as ex:
            self._send_json(500, {"success": False, "error": str(ex)})

    def _handle_file_content(self, query):
        if not self._require_auth(query):
            return
        p = self._resolve_fs_path(query.get("path", [None])[0])
        if not p or not os.path.isfile(p):
            self._send_json(404, {"success": False, "error": "file not found"})
            return
        try:
            with open(p, "rb") as f:
                content = f.read().decode("utf-8", "replace")
        except OSError as ex:
            self._send_json(500, {"success": False, "error": str(ex)})
            return
        self._send_json(200, {"success": True, "content": content})

    def _handle_upload_file(self):
        query = parse_qs(urlparse(self.path).query)
        if not self._require_auth(query):
            return
        p = self._resolve_fs_path(query.get("path", [None])[0])
        if not p:
            self._send_json(400, {"success": False, "error": "path required"})
            return
        raw = self._read_body()
        if raw is None:
            self._send_json(400, {"success": False, "error": "invalid body size"})
            return
        try:
            parent = os.path.dirname(p)
            if parent and not os.path.isdir(parent):
                os.makedirs(parent, exist_ok=True)
            with open(p, "wb") as f:
                f.write(raw)
        except OSError as ex:
            self._send_json(500, {"success": False, "error": str(ex)})
            return
        self._send_json(200, {"success": True, "path": p})

    def _handle_save_file(self):
        if not self._require_auth():
            return
        raw = self._read_body()
        if raw is None:
            self._send_json(400, {"success": False, "error": "invalid body size"})
            return
        try:
            data = json.loads(raw.decode("utf-8"))
        except (json.JSONDecodeError, UnicodeDecodeError):
            self._send_json(400, {"success": False, "error": "invalid json"})
            return
        p = self._resolve_fs_path(data.get("path"))
        if not p:
            self._send_json(400, {"success": False, "error": "path required"})
            return
        try:
            with open(p, "w", encoding="utf-8") as f:
                f.write(data.get("content", ""))
        except OSError as ex:
            self._send_json(500, {"success": False, "error": str(ex)})
            return
        self._send_json(200, {"success": True})

    def _handle_mkdir(self):
        if not self._require_auth():
            return
        raw = self._read_body()
        if raw is None:
            self._send_json(400, {"success": False, "error": "invalid body size"})
            return
        try:
            data = json.loads(raw.decode("utf-8"))
        except (json.JSONDecodeError, UnicodeDecodeError):
            self._send_json(400, {"success": False, "error": "invalid json"})
            return
        p = self._resolve_fs_path(data.get("path"))
        if not p:
            self._send_json(400, {"success": False, "error": "path required"})
            return
        try:
            os.makedirs(p, exist_ok=True)
        except OSError as ex:
            self._send_json(500, {"success": False, "error": str(ex)})
            return
        self._send_json(200, {"success": True})

    def _handle_delete(self):
        if not self._require_auth():
            return
        raw = self._read_body()
        if raw is None:
            self._send_json(400, {"success": False, "error": "invalid body size"})
            return
        try:
            data = json.loads(raw.decode("utf-8"))
        except (json.JSONDecodeError, UnicodeDecodeError):
            self._send_json(400, {"success": False, "error": "invalid json"})
            return
        p = self._resolve_fs_path(data.get("path"))
        if not p or not os.path.exists(p):
            self._send_json(404, {"success": False, "error": "path not found"})
            return
        try:
            if data.get("isDirectory"):
                shutil.rmtree(p)
            else:
                os.remove(p)
        except OSError as ex:
            self._send_json(500, {"success": False, "error": str(ex)})
            return
        self._send_json(200, {"success": True})

    # ------------------------------------------------------------------ WebSocket
    def _ws_accept_key(self, key):
        return base64.b64encode(
            hashlib.sha1((key + WS_GUID).encode("utf-8")).digest()
        ).decode("utf-8")

    def _ws_read_exact(self, n):
        data = b""
        while len(data) < n:
            chunk = self.rfile.read(n - len(data))
            if not chunk:
                raise ConnectionError("connection closed")
            data += chunk
        return data

    def _ws_read_frame(self):
        """读取一帧，返回 (fin, opcode, payload)。"""
        header = self._ws_read_exact(2)
        b1, b2 = header[0], header[1]
        fin = (b1 & 0x80) != 0
        opcode = b1 & 0x0F
        masked = (b2 & 0x80) != 0
        length = b2 & 0x7F

        if length == 126:
            length = struct.unpack(">H", self._ws_read_exact(2))[0]
        elif length == 127:
            length = struct.unpack(">Q", self._ws_read_exact(8))[0]

        mask_key = self._ws_read_exact(4) if masked else b""
        payload = self._ws_read_exact(length) if length else b""
        if masked:
            payload = bytes(b ^ mask_key[i % 4] for i, b in enumerate(payload))
        return fin, opcode, payload

    def _ws_send_frame(self, opcode, payload, fin=True):
        """发送一帧（服务端帧不需要掩码）。"""
        header = bytearray()
        header.append((0x80 if fin else 0) | opcode)
        length = len(payload)
        if length < 126:
            header.append(length)
        elif length < 65536:
            header.append(126)
            header += struct.pack(">H", length)
        else:
            header.append(127)
            header += struct.pack(">Q", length)
        self.wfile.write(bytes(header) + payload)
        self.wfile.flush()

    def _ws_send_text(self, text):
        with self._ws_lock:
            try:
                self._ws_send_frame(OPCODE_TEXT, text.encode("utf-8"))
            except Exception:
                pass

    def _ws_send_pong(self, payload):
        with self._ws_lock:
            try:
                self._ws_send_frame(OPCODE_PONG, payload)
            except Exception:
                pass

    def _ws_send_close(self, code=1000):
        with self._ws_lock:
            try:
                self._ws_send_frame(OPCODE_CLOSE, struct.pack(">H", code))
            except Exception:
                pass

    def _ws_send_ping(self):
        """发送心跳帧；连接已断开时返回 False。"""
        with self._ws_lock:
            try:
                self._ws_send_frame(OPCODE_PING, b"")
                return True
            except Exception:
                return False

    def _spawn_shell(self, cols=DEFAULT_COLS, rows=DEFAULT_ROWS):
        """启动一个交互式终端会话，优先使用 PTY，失败则退回管道模式。"""
        # 尺寸夹到合理区间，避免前端传上来 0 或异常值导致伪终端缓冲畸形
        cols = min(max(cols, MIN_COLS), MAX_COLS)
        rows = min(max(rows, MIN_ROWS), MAX_ROWS)
        if os.name == "nt":
            # Windows 优先 PowerShell 7(pwsh)，否则退回内置 Windows PowerShell；
            # 启动时把控制台输入/输出编码切到 UTF-8，避免中文乱码；
            # 同时强制 ANSI 彩色输出（PS7 的 PSStyle 在管道模式下默认会关闭颜色）。
            exe = shutil.which("pwsh") or shutil.which("powershell") or "powershell.exe"
            init = (
                "$env:TERM='xterm-256color'; "
                "try { $PSStyle.OutputRendering='Ansi' } catch {}; "
                "try { [Console]::InputEncoding=[Text.Encoding]::UTF8; "
                "[Console]::OutputEncoding=[Text.Encoding]::UTF8 } catch {}"
            )
            argv = [exe, "-NoLogo", "-NoProfile", "-NoExit", "-Command", init]
            if _WinPtyProcess is not None:
                try:
                    return WinPtyTerminal(argv, None, cols, rows)
                except Exception:
                    pass
            return PipeTerminal(argv, None)

        shell = "bash" if shutil.which("bash") else "sh"
        argv = [shell, "-i"]
        try:
            return PosixPtyTerminal(argv, None, cols, rows)
        except Exception:
            return PipeTerminal(argv, None)

    def _handle_ws(self, query):
        # 鉴权：优先取 query 里的 key，其次请求头
        if not self._authorized(query):
            self._send_json(401, {"success": False, "error": "unauthorized"})
            return

        ws_key = self.headers.get("Sec-WebSocket-Key")
        if not ws_key:
            self._send_json(400, {"success": False, "error": "missing Sec-WebSocket-Key"})
            return

        # 完成握手
        accept = self._ws_accept_key(ws_key)
        self.send_response(101)
        self.send_header("Upgrade", "websocket")
        self.send_header("Connection", "Upgrade")
        self.send_header("Sec-WebSocket-Accept", accept)
        self.end_headers()
        self.wfile.flush()

        self._ws_lock = threading.Lock()
        stop_event = threading.Event()
        cols = self._query_int(query, "cols", DEFAULT_COLS)
        rows = self._query_int(query, "rows", DEFAULT_ROWS)
        term = self._spawn_shell(cols, rows)
        register_terminal(term)

        # 后台线程：终端输出 -> 实时回传
        def pump_output():
            try:
                while not stop_event.is_set():
                    chunk = term.read()
                    if not chunk:
                        break
                    self._ws_send_text(chunk.decode("utf-8", "replace"))
            except Exception:
                pass
            finally:
                if not stop_event.is_set():
                    self._ws_send_text("\r\n[进程已退出]\r\n")
                    self._ws_send_close(1000)
                stop_event.set()
                # 解除主循环在 read 上的阻塞
                try:
                    self.connection.shutdown(socket.SHUT_RDWR)
                except Exception:
                    pass

        # 后台线程：心跳保活，连接实际已断开时主动收尾，避免终端进程长期残留
        def heartbeat():
            while not stop_event.wait(WS_HEARTBEAT_SECONDS):
                if not self._ws_send_ping():
                    stop_event.set()
                    try:
                        self.connection.shutdown(socket.SHUT_RDWR)
                    except Exception:
                        pass
                    return

        pump = threading.Thread(target=pump_output, daemon=True)
        pump.start()
        beat = threading.Thread(target=heartbeat, daemon=True)
        beat.start()

        try:
            while not stop_event.is_set():
                try:
                    _, opcode, payload = self._ws_read_frame()
                except Exception:
                    break

                if opcode == OPCODE_CLOSE:
                    break
                if opcode == OPCODE_PING:
                    self._ws_send_pong(payload)
                    continue
                if opcode == OPCODE_TEXT:
                    # 文本帧 = 键盘输入
                    try:
                        term.write(payload)
                    except Exception:
                        break
                elif opcode == OPCODE_BINARY:
                    # 二进制帧 = 控制指令（如窗口尺寸变化）
                    self._handle_ws_control(term, payload)
        finally:
            stop_event.set()
            term.close()
            unregister_terminal(term)
            pump.join(timeout=3)
            try:
                self.wfile.flush()
            except Exception:
                pass

    @staticmethod
    def _query_int(query, name, default):
        """从 query 中取正整数参数，非法值回退到默认值。"""
        try:
            values = query.get(name)
            if not values:
                return default
            value = int(values[0])
            return value if value > 0 else default
        except (TypeError, ValueError):
            return default

    def _handle_ws_control(self, term, payload):
        """处理二进制控制帧，目前支持 {"type": "resize", "cols": c, "rows": r}。"""
        try:
            data = json.loads(payload.decode("utf-8"))
        except (json.JSONDecodeError, UnicodeDecodeError):
            return
        if not isinstance(data, dict):
            return
        if data.get("type") != "resize":
            return
        try:
            cols = int(data.get("cols") or 0)
            rows = int(data.get("rows") or 0)
        except (TypeError, ValueError):
            return
        if cols > 0 and rows > 0:
            term.resize(cols, rows)


def terminal_mode_name():
    """返回当前可用的终端模式说明，用于启动时提示。"""
    if os.name == "nt":
        if _WinPtyProcess is not None:
            return "PTY (pywinpty)"
        return "管道模式（安装 pywinpty 可支持 Ctrl+C / 全屏程序）"
    return "PTY (pty)"


def register_terminal(term):
    """登记活动终端，Agent 退出时统一回收。"""
    with _TERMINALS_LOCK:
        _TERMINALS.add(term)


def unregister_terminal(term):
    """注销已结束的终端。"""
    with _TERMINALS_LOCK:
        _TERMINALS.discard(term)


def close_all_terminals():
    """关闭所有活动终端，避免 Agent 退出后残留 shell 进程。"""
    with _TERMINALS_LOCK:
        terminals = list(_TERMINALS)
        _TERMINALS.clear()
    for term in terminals:
        try:
            term.close()
        except Exception:
            pass


def parse_args():
    parser = argparse.ArgumentParser(description="EZPannel HTTP/WebSocket 命令转发 Agent")
    parser.add_argument("--host", default=DEFAULT_HOST, help="监听地址，默认 0.0.0.0")
    parser.add_argument("--port", type=int, default=DEFAULT_PORT, help="监听端口，默认 9527")
    parser.add_argument("--cwd", default=SCRIPT_DIR, help="命令默认工作目录")
    parser.add_argument("--config", default=CONFIG_FILE, help="Key 配置文件路径")
    parser.add_argument("--regenerate-key", action="store_true", help="重新生成 Key")
    parser.add_argument(
        "--no-auto-install",
        action="store_true",
        help="Windows 上缺少 pywinpty 时不自动安装（离线环境可加此参数）",
    )
    return parser.parse_args()


def main():
    global _WinPtyProcess

    args = parse_args()

    # Windows 上先确保 pywinpty 可用，否则终端会退回管道模式（退格/回显失效）
    _WinPtyProcess = ensure_winpty(auto_install=not args.no_auto_install)

    key, cfg = load_or_create_key(args.config, regenerate=args.regenerate_key)

    # 进程异常退出时也回收终端，防止 shell 进程泄漏
    atexit.register(close_all_terminals)

    server = AgentServer((args.host, args.port), AgentHandler, key, args.cwd)

    print("=" * 56)
    print("  EZPannel Agent 已启动")
    print(f"  机器名    : {cfg.get('machine')}")
    print(f"  系统      : {cfg.get('system')}")
    print(f"  监听地址  : http://{args.host}:{args.port}")
    print(f"  API Key   : {key}")
    print(f"  Key 文件  : {args.config}")
    print(f"  一次性执行: POST /api/execute  Header: X-Auth-Key")
    print(f"  交互终端  : ws://{args.host}:{args.port}/ws?key=<key>")
    print(f"  终端模式  : {terminal_mode_name()}")
    print("=" * 56)

    try:
        server.serve_forever()
    except KeyboardInterrupt:
        print("\n正在停止 Agent ...")
    finally:
        server.server_close()
        # 回收所有活动终端，否则退出后会残留 powershell/bash 进程
        close_all_terminals()


if __name__ == "__main__":
    main()