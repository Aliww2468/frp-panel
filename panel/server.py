"""Local FRP panel. Python 3.11+, standard library only."""
import argparse
import base64
import hashlib
import json
import os
from pathlib import Path
import re
import secrets
import shutil
import socket
import subprocess
import threading
import time
import tomllib
import urllib.error
import urllib.request
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

ROOT = Path(__file__).resolve().parent
CLIENT = ROOT.parent / 'client'
CONFIG = CLIENT / 'frpc.toml'
EXE = CLIENT / 'frpc.exe'
LOCK = threading.Lock()
TOKEN = secrets.token_urlsafe(32)
PROCESS = None
PORT = 17600
NO_WINDOW = getattr(subprocess, 'CREATE_NO_WINDOW', 0)
OPENER = urllib.request.build_opener(urllib.request.ProxyHandler({}))


def initialize_config():
    """Create private local defaults on first launch; never replace user data."""
    CLIENT.mkdir(parents=True, exist_ok=True)
    config = {'serverAddr': '', 'serverPort': 7000,
              'webServer': {'addr': '127.0.0.1', 'port': 7400, 'user': 'panel',
                            'password': secrets.token_urlsafe(32)},
              'transport': {'tls': {'enable': True}, 'wireProtocol': 'v1'},
              'log': {'to': './logs/frpc.log', 'level': 'info', 'maxDays': 7}}
    try:
        with CONFIG.open('x', encoding='utf-8') as file:
            file.write(dump_toml(config))
    except FileExistsError:
        pass


def read_config():
    text = CONFIG.read_text(encoding='utf-8-sig')
    return text, tomllib.loads(text)


def redact(value, config):
    values = [config.get('webServer', {}).get('password'), config.get('auth', {}).get('token')]
    source = config.get('auth', {}).get('tokenSource', {}).get('file', {}).get('path')
    if source:
        try:
            values.append((CLIENT / source).read_text(encoding='utf-8').strip())
        except OSError:
            pass
    for secret in values:
        if secret:
            value = value.replace(str(secret), '[已隐藏]')
    return re.sub(r'(?i)((?:password|token|authorization)\s*[:=]\s*)\S+', r'\1[已隐藏]', value)


def admin(config, path, method='GET'):
    web = config.get('webServer', {})
    if web.get('addr', '127.0.0.1') not in ('127.0.0.1', 'localhost', '::1'):
        raise ValueError('管理接口必须绑定本机回环地址。')
    auth = base64.b64encode(f"{web.get('user', '')}:{web.get('password', '')}".encode()).decode()
    request = urllib.request.Request(f"http://127.0.0.1:{int(web.get('port', 7400))}{path}",
                                     headers={'Authorization': 'Basic ' + auth}, method=method)
    with OPENER.open(request, timeout=2) as response:
        raw = response.read(2 * 1024 * 1024)
        return json.loads(raw) if raw else None


def runtime(config):
    try:
        data = admin(config, '/api/status')
        if not isinstance(data, dict):
            return False, {}, '管理接口返回了无法识别的数据'
        return True, data, ''
    except urllib.error.HTTPError as error:
        return False, {}, f'管理接口返回 HTTP {error.code}，请检查客户端管理配置'
    except (OSError, ValueError, urllib.error.URLError):
        return False, {}, '客户端管理接口未连接'


def get_state():
    text, config = read_config()
    connected, data, error = runtime(config)
    statuses = {}
    for rows in data.values():
        if isinstance(rows, list):
            for row in rows:
                if isinstance(row, dict):
                    statuses[row.get('name', '')] = row
    proxies = []
    for p in config.get('proxies', []):
        name = p.get('name', '')
        prefix = config.get('user', '')
        live = statuses.get(f'{prefix}.{name}' if prefix else name, statuses.get(name, {}))
        proxies.append({**{k: p.get(k, '') for k in ('name', 'type', 'localIP', 'localPort', 'remotePort')},
                        'status': live.get('status', 'pending' if connected else 'offline'),
                        'error': redact(str(live.get('err', '')), config)})
    return {'token': TOKEN, 'revision': hashlib.sha256(text.encode()).hexdigest(),
            'connected': connected, 'configured': bool(config.get('serverAddr', '').strip()),
            'processRunning': PROCESS is not None and PROCESS.poll() is None,
            'error': error, 'proxies': proxies,
            'server': {'address': config.get('serverAddr', ''), 'port': config.get('serverPort', 7000),
                       'user': config.get('user', ''),
                       'hasToken': bool(config.get('auth', {}).get('token') or config.get('auth', {}).get('tokenSource')),
                       'tls': config.get('transport', {}).get('tls', {}).get('enable', True),
                       'protocol': config.get('transport', {}).get('wireProtocol', 'v1'),
                       'adminPort': config.get('webServer', {}).get('port', 7400)},
            'version': '0.71.0', 'timestamp': time.time()}


def proxy_ranges(text):
    starts = list(re.finditer(r'^\s*\[\[proxies\]\][ \t]*(?:#.*)?$', text, re.M))
    ranges = []
    for match in starts:
        end = len(text)
        for header in re.finditer(r'^\s*(\[\[?[^\]\n]+\]\]?)[ \t]*(?:#.*)?$', text[match.end():], re.M):
            label = header.group(1).strip('[]').strip()
            if not label.startswith('proxies.'):
                end = match.end() + header.start()
                break
        ranges.append((match.start(), end))
    return ranges


def validate_proxy(p, config, original=None):
    if not isinstance(p, dict):
        raise ValueError('无效的代理配置')
    name = str(p.get('name', '')).strip()
    if not re.fullmatch(r'[\w.-]{1,64}', name):
        raise ValueError('名称需为 1–64 位字母、数字、中文、横线或下划线')
    if p.get('type') not in ('tcp', 'udp'):
        raise ValueError('当前面板支持 TCP 和 UDP 代理')
    host = str(p.get('localIP', '')).strip()
    if not host or len(host) > 253 or not re.fullmatch(r'[A-Za-z0-9.:[\]_-]+', host):
        raise ValueError('请填写有效的本地 IP 或主机名')
    result = {'name': name, 'type': p['type'], 'localIP': host}
    for key in ('localPort', 'remotePort'):
        port = p.get(key)
        if type(port) is not int or not 1 <= port <= 65535:
            raise ValueError('端口必须是 1–65535 的整数')
        result[key] = port
    for existing in config.get('proxies', []):
        if existing.get('name') == original:
            continue
        if existing.get('name') == name:
            raise ValueError('代理名称已存在')
        if existing.get('type') == result['type'] and existing.get('remotePort') == result['remotePort']:
            raise ValueError('相同协议的远程端口已被另一条规则使用')
    return result


def edit_text(text, original, proxy):
    config = tomllib.loads(text)
    items = config.get('proxies', [])
    if original is None:
        return text.rstrip() + '\n\n[[proxies]]\n' + ''.join(f'{k} = {json.dumps(v, ensure_ascii=False)}\n' for k, v in proxy.items())
    index = next((i for i, p in enumerate(items) if p.get('name') == original), None)
    if index is None:
        raise ValueError('此代理已不存在，请刷新页面')
    ranges = proxy_ranges(text)
    if len(ranges) != len(items):
        raise ValueError('此配置使用了特殊 TOML 格式，请通过配置文件编辑')
    begin, end = ranges[index]
    if proxy is None:
        return text[:begin] + text[end:]
    block = text[begin:end]
    # Edit only the base fields; preserve comments and advanced proxy subtables.
    header_end = block.index(']]') + 2
    nested = re.search(r'^\s*\[', block[header_end:], re.M)
    cut = header_end + nested.start() if nested else len(block)
    base, tail = block[:cut], block[cut:]
    for key, value in proxy.items():
        line = f'{key} = {json.dumps(value, ensure_ascii=False)}'
        pattern = rf'^{key}\s*=.*$'
        if re.search(pattern, base, re.M):
            base = re.sub(pattern, lambda _: line, base, flags=re.M)
        else:
            base = base.rstrip() + '\n' + line + '\n'
    return text[:begin] + base.rstrip() + '\n\n' + tail + text[end:]


def verify_file(path):
    result = subprocess.run([str(EXE), 'verify', '-c', str(path)], cwd=CLIENT,
                            capture_output=True, timeout=15, creationflags=NO_WINDOW)
    if result.returncode:
        raise ValueError('FRP 配置校验未通过：' + redact((result.stdout + result.stderr).decode('utf-8', errors='replace')[-1800:], read_config()[1]))


def commit_config(text, candidate):
    tomllib.loads(candidate)
    temp = CLIENT / f'.panel-{secrets.token_hex(6)}.toml'
    try:
        temp.write_text(candidate, encoding='utf-8')
        verify_file(temp)
        # Detect external edits made while validation was running.
        if CONFIG.read_text(encoding='utf-8-sig') != text:
            raise ValueError('配置已被其他程序修改，请刷新后重试')
        backup_dir = ROOT / 'backups'
        backup_dir.mkdir(exist_ok=True)
        backup = backup_dir / f'frpc-{time.strftime("%Y%m%d-%H%M%S")}-{secrets.token_hex(3)}.toml'
        shutil.copy2(CONFIG, backup)
        os.replace(temp, CONFIG)
    finally:
        temp.unlink(missing_ok=True)
    return backup.name


def check_revision(body, text):
    if body.get('revision') != hashlib.sha256(text.encode()).hexdigest():
        raise ValueError('配置已发生变化，请刷新后重试')


def save_proxy(body, delete=False):
    text, config = read_config()
    check_revision(body, text)
    if not config.get('serverAddr', '').strip():
        raise ValueError('请先添加服务器连接配置')
    original = body.get('original')
    if delete and not isinstance(original, str):
        raise ValueError('缺少需要删除的代理名称')
    p = None if delete else validate_proxy(body.get('proxy'), config, original)
    candidate = edit_text(text, original, p)
    commit_config(text, candidate)
    connected, _, _ = runtime(config)
    if connected:
        try:
            admin(config, '/api/reload')
            return {'message': '规则已保存，已请求客户端热重载'}
        except Exception:
            return {'message': '规则已保存，但热重载失败；请手动重新加载或重启客户端', 'warning': True}
    return {'message': '规则已保存，将在下次启动客户端时生效'}


def dump_toml(data):
    """Serialize parsed TOML without dropping advanced settings or arrays of tables."""
    def key(value):
        return value if re.fullmatch(r'[A-Za-z0-9_-]+', value) else json.dumps(value, ensure_ascii=False)

    def scalar(value):
        if isinstance(value, str):
            return json.dumps(value, ensure_ascii=False)
        if isinstance(value, bool):
            return 'true' if value else 'false'
        if isinstance(value, (int, float)):
            return repr(value)
        if isinstance(value, list):
            return '[' + ', '.join(scalar(item) for item in value) + ']'
        if isinstance(value, dict):
            return '{' + ', '.join(f'{key(k)} = {scalar(v)}' for k, v in value.items()) + '}'
        if hasattr(value, 'isoformat'):
            return value.isoformat()
        raise ValueError('配置包含暂不支持的数据类型')

    lines = []
    def table(values, path=()):
        nested = []
        for name, value in values.items():
            if isinstance(value, dict) or (isinstance(value, list) and value and all(isinstance(item, dict) for item in value)):
                nested.append((name, value))
            else:
                lines.append(f'{key(name)} = {scalar(value)}')
        for name, value in nested:
            child = (*path, name)
            label = '.'.join(key(part) for part in child)
            if isinstance(value, dict):
                lines.extend(['', f'[{label}]'])
                table(value, child)
            else:
                for item in value:
                    lines.extend(['', f'[[{label}]]'])
                    table(item, child)
    table(data)
    return '\n'.join(lines) + '\n'


def ensure_stopped(config):
    if PROCESS is not None and PROCESS.poll() is None:
        raise ValueError('请先在概览停止客户端，再修改连接配置')
    try:
        with socket.create_connection(('127.0.0.1', config.get('webServer', {}).get('port', 7400)), timeout=.3):
            raise ValueError('客户端管理端口正在使用，请先停止客户端再修改连接配置')
    except OSError:
        pass


def save_connection(body):
    text, config = read_config()
    check_revision(body, text)
    ensure_stopped(config)
    address = body.get('address', '')
    if not isinstance(address, str) or not address.strip() or len(address) > 253:
        raise ValueError('请填写服务器 IP 或域名')
    address = address.strip()
    try:
        import ipaddress
        ipaddress.ip_address(address)
    except ValueError:
        if not all(re.fullmatch(r'[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?', label) for label in address.split('.')):
            raise ValueError('服务器地址只填写 IP 或域名，不包含 http:// 或端口')
    port = body.get('port')
    if type(port) is not int or not 1 <= port <= 65535:
        raise ValueError('服务器端口必须是 1–65535 的整数')
    user = body.get('user', '')
    if not isinstance(user, str) or len(user) > 64 or (user and not re.fullmatch(r'[\w.-]+', user)):
        raise ValueError('用户标识最多 64 位，只能包含字母、数字、中文、横线和下划线')
    if type(body.get('tls')) is not bool or body.get('protocol') not in ('v1', 'v2'):
        raise ValueError('请选择有效的 TLS 与线路协议设置')
    token_mode = body.get('tokenMode', 'keep')
    if token_mode not in ('keep', 'replace', 'clear'):
        raise ValueError('无效的认证方式')
    config['serverAddr'], config['serverPort'], config['user'] = address, port, user
    transport = config.setdefault('transport', {})
    transport.setdefault('tls', {})['enable'] = body['tls']
    transport['wireProtocol'] = body['protocol']
    if token_mode != 'keep':
        auth = config.setdefault('auth', {})
        auth.pop('tokenSource', None)
        auth.pop('oidc', None)
        auth['method'] = 'token'
        if token_mode == 'replace':
            value = body.get('authToken')
            if not isinstance(value, str) or not value or len(value) > 4096 or any(ord(char) < 32 for char in value):
                raise ValueError('请填写有效的认证 Token，长度不超过 4096')
            auth['token'] = value
        else:
            auth.pop('token', None)
    candidate = dump_toml(config)
    # Validator errors must never return the newly supplied token to the browser.
    try:
        commit_config(text, candidate)
    except ValueError as error:
        raise ValueError(redact(str(error), config)) from None
    return {'message': '连接配置已保存，可以添加代理规则并启动客户端'}


def reset_connection(body):
    text, config = read_config()
    check_revision(body, text)
    ensure_stopped(config)
    # Keep the local admin identity, but detach old server credentials and routes.
    blank = {'serverAddr': '', 'serverPort': 7000, 'loginFailExit': False,
             'webServer': config.get('webServer', {'addr': '127.0.0.1', 'port': 7400}),
             'transport': {'tls': {'enable': True}, 'wireProtocol': 'v1'},
             'log': {'to': f'./logs/frpc-{time.time_ns()}.log', 'level': 'info', 'maxDays': 7}}
    backup = commit_config(text, dump_toml(blank))
    return {'message': '连接与代理规则已重置，原配置已自动备份', 'backup': backup}


def prepare_import(body, current):
    name, content = body.get('filename', ''), body.get('content')
    if not isinstance(name, str) or not name.lower().endswith('.toml'):
        raise ValueError('请选择 FRPC 的 .toml 配置文件')
    if not isinstance(content, str) or not content.strip() or len(content.encode('utf-8')) > 256 * 1024:
        raise ValueError('配置文件不能为空，且不能超过 256 KB')
    try:
        imported = tomllib.loads(content.lstrip('\ufeff'))
    except tomllib.TOMLDecodeError:
        raise ValueError('TOML 格式有误，请检查文件中的引号、字段和表格格式') from None
    if not isinstance(imported.get('serverAddr'), str) or not imported['serverAddr'].strip():
        raise ValueError('缺少 serverAddr，请选择客户端 frpc 配置，而不是 frps 服务端配置')
    if imported.get('includes'):
        raise ValueError('此配置引用了其他配置文件，请先合并为单个 TOML 文件再导入')
    proxies = imported.get('proxies', [])
    if not isinstance(proxies, list) or any(not isinstance(proxy, dict) for proxy in proxies):
        raise ValueError('代理规则必须使用 [[proxies]] 数组格式')
    for index, proxy in enumerate(proxies, 1):
        if proxy.get('type') in ('http', 'https') and not proxy.get('customDomains') and not proxy.get('subdomain'):
            # Identify the rule by position without exposing imported names or credentials.
            raise ValueError(f'第 {index} 条 {proxy["type"].upper()} 代理缺少访问域名：请填写服务商支持的 customDomains 或 subdomain。'
                             '如需通过公网 IP:端口访问，请在服务商后台创建 TCP 隧道后重新导出配置。')
    visitors = imported.get('visitors', [])
    if not isinstance(visitors, list) or any(not isinstance(visitor, dict) for visitor in visitors):
        raise ValueError('访问者规则必须使用 [[visitors]] 数组格式')
    # The desktop app always manages its own loopback interface and log location.
    imported['webServer'] = current.get('webServer', {'addr': '127.0.0.1', 'port': 7400})
    imported['log'] = current.get('log', {'to': './logs/frpc.log', 'level': 'info', 'maxDays': 7})
    if not isinstance(imported.get('auth', {}), dict) or not isinstance(imported.get('transport', {}), dict):
        raise ValueError('auth 和 transport 必须是配置表')
    candidate = dump_toml(imported)
    summary = {'address': imported['serverAddr'], 'port': imported.get('serverPort', 7000),
               'proxyCount': len(proxies), 'visitorCount': len(visitors),
               'hasAuth': bool(imported.get('auth')), 'filename': Path(name).name}
    return candidate, summary


def import_connection(body, preview=False):
    text, current = read_config()
    if not preview:
        check_revision(body, text)
        ensure_stopped(current)
    candidate, summary = prepare_import(body, current)
    if preview:
        temp = CLIENT / f'.panel-{secrets.token_hex(6)}.toml'
        try:
            temp.write_text(candidate, encoding='utf-8')
            verify_file(temp)
        except ValueError:
            # FRP errors may contain secrets from imported advanced fields.
            raise ValueError('FRP 配置校验未通过，请检查字段、重复名称及引用文件；相对路径以 client 文件夹为基准') from None
        finally:
            temp.unlink(missing_ok=True)
        return {**summary, 'revision': hashlib.sha256(text.encode()).hexdigest()}
    try:
        backup = commit_config(text, candidate)
    except ValueError:
        raise ValueError('导入未完成，原配置未被覆盖；请重新预览并检查配置及引用文件') from None
    return {'message': f'已导入 {summary["proxyCount"]} 条代理规则，配置已保存，客户端尚未启动', 'backup': backup}


def control(action):
    global PROCESS
    _, config = read_config()
    connected, _, _ = runtime(config)
    if action == 'start':
        if not config.get('serverAddr', '').strip():
            raise ValueError('请先添加服务器连接配置')
        if connected or (PROCESS is not None and PROCESS.poll() is None):
            return {'message': '客户端已在运行'}
        # A responding but unauthenticated admin port is not a stopped client.
        try:
            with socket.create_connection(('127.0.0.1', config.get('webServer', {}).get('port', 7400)), timeout=.3):
                raise ValueError('管理端口已被占用，请检查已有客户端或管理密码')
        except (ConnectionRefusedError, TimeoutError, OSError):
            pass
        verify_file(CONFIG)
        (CLIENT / 'logs').mkdir(exist_ok=True)
        with (CLIENT / 'logs' / 'panel-launch.log').open('ab') as output:
            PROCESS = subprocess.Popen([str(EXE), '-c', str(CONFIG)], cwd=CLIENT,
                                       stdout=output, stderr=output, creationflags=NO_WINDOW)
        return {'message': '启动请求已发送，连接状态将自动更新'}
    if action == 'stop':
        if connected:
            admin(config, '/api/stop', 'POST')
        elif PROCESS is not None and PROCESS.poll() is None:
            PROCESS.terminate()
        else:
            raise ValueError('未发现可停止的客户端')
        return {'message': '停止请求已发送'}
    if action == 'reload':
        verify_file(CONFIG)
        if not connected:
            raise ValueError('客户端未连接，请先启动')
        admin(config, '/api/reload')
        return {'message': '已请求重新加载代理配置'}
    raise ValueError('未知操作')


class Handler(BaseHTTPRequestHandler):
    def log_message(self, *_):
        pass

    def reply(self, status, data, content_type='application/json; charset=utf-8'):
        raw = json.dumps(data, ensure_ascii=False).encode() if not isinstance(data, bytes) else data
        self.send_response(status)
        self.send_header('Content-Type', content_type)
        self.send_header('Content-Length', str(len(raw)))
        self.send_header('Cache-Control', 'no-store')
        self.send_header('X-Content-Type-Options', 'nosniff')
        self.send_header('Content-Security-Policy', "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; frame-ancestors 'none'; base-uri 'none'; form-action 'self'")
        self.end_headers()
        self.wfile.write(raw)

    def allowed(self, write=False):
        allowed = {f'127.0.0.1:{PORT}', f'localhost:{PORT}'}
        if self.headers.get('Host') not in allowed:
            self.reply(403, {'error': '仅允许本机访问'})
            return False
        origin = self.headers.get('Origin')
        if origin and origin not in {f'http://{host}' for host in allowed}:
            self.reply(403, {'error': '跨站请求已拒绝'})
            return False
        if write and not secrets.compare_digest(self.headers.get('X-Panel-Token', ''), TOKEN):
            self.reply(403, {'error': '会话已过期，请刷新页面'})
            return False
        return True

    def do_GET(self):
        if not self.allowed():
            return
        try:
            path = self.path.split('?')[0]
            if path == '/api/health':
                return self.reply(200, {'app': 'frp-panel', 'desktopApi': 1,
                                        'workspace': str(CLIENT.parent), 'pid': os.getpid()})
            if path == '/api/state':
                return self.reply(200, get_state())
            if path == '/api/logs':
                _, config = read_config()
                logfile = CLIENT / config.get('log', {}).get('to', './logs/frpc.log')
                lines = []
                if logfile.is_file():
                    with logfile.open('rb') as stream:
                        stream.seek(max(0, logfile.stat().st_size - 65536))
                        lines = stream.read().decode('utf-8', errors='replace').splitlines()[-100:]
                return self.reply(200, {'lines': [redact(line, config) for line in lines]})
            assets = {'/': ('index.html', 'text/html'), '/app.js': ('app.js', 'text/javascript'), '/style.css': ('style.css', 'text/css'), '/favicon.svg': ('favicon.svg', 'image/svg+xml')}
            if path in assets:
                filename, mime = assets[path]
                return self.reply(200, (ROOT / 'static' / filename).read_bytes(), mime + '; charset=utf-8')
            self.reply(404, {'error': '页面不存在'})
        except Exception:
            self.reply(500, {'error': '无法读取客户端配置，请检查 client/frpc.toml'})

    def do_POST(self):
        if not self.allowed(write=True):
            return
        try:
            size = int(self.headers.get('Content-Length', '0'))
            max_size = 2 * 1024 * 1024 if self.path in ('/api/connection/import-preview', '/api/connection/import') else 16384
            if not 0 <= size <= max_size:
                raise ValueError('请求过大')
            body = json.loads(self.rfile.read(size) or b'{}')
            if not isinstance(body, dict):
                raise ValueError('无效请求')
            with LOCK:
                if self.path == '/api/proxy/save':
                    result = save_proxy(body)
                elif self.path == '/api/proxy/delete':
                    result = save_proxy(body, delete=True)
                elif self.path == '/api/connection/save':
                    result = save_connection(body)
                elif self.path == '/api/connection/reset':
                    result = reset_connection(body)
                elif self.path == '/api/connection/import-preview':
                    result = import_connection(body, preview=True)
                elif self.path == '/api/connection/import':
                    result = import_connection(body)
                elif self.path == '/api/desktop/exit':
                    _, config = read_config()
                    connected, _, _ = runtime(config)
                    if connected or (PROCESS is not None and PROCESS.poll() is None):
                        control('stop')
                        deadline = time.monotonic() + 8
                        while time.monotonic() < deadline:
                            owned_running = PROCESS is not None and PROCESS.poll() is None
                            if not owned_running and not runtime(config)[0]:
                                break
                            time.sleep(.2)
                        else:
                            raise ValueError('客户端尚未停止，软件将继续运行，请检查后重试退出')
                    self.reply(200, {'message': '客户端已停止，面板正在退出'})
                    threading.Thread(target=self.server.shutdown, daemon=True).start()
                    return
                elif self.path.startswith('/api/control/'):
                    result = control(self.path.rsplit('/', 1)[-1])
                else:
                    return self.reply(404, {'error': '操作不存在'})
            self.reply(200, result)
        except ValueError as error:
            self.reply(400, {'error': str(error)})
        except Exception:
            self.reply(500, {'error': '操作未完成，请检查客户端日志和文件权限'})


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--port', type=int, default=17600)
    parser.add_argument('--client-dir', type=Path, default=CLIENT)
    args = parser.parse_args()
    PORT = args.port
    CLIENT = args.client_dir.resolve()
    CONFIG, EXE = CLIENT / 'frpc.toml', CLIENT / 'frpc.exe'
    initialize_config()
    server = ThreadingHTTPServer(('127.0.0.1', PORT), Handler)
    print(f'FRP Panel: http://127.0.0.1:{PORT}', flush=True)
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass
    finally:
        server.server_close()
