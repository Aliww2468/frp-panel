import http.client
import importlib.util
import json
from pathlib import Path
import socket
import subprocess
import tempfile
import threading
import time
import tomllib
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('panel_server', ROOT / 'server.py')
panel = importlib.util.module_from_spec(spec)
spec.loader.exec_module(panel)


def free_port():
    with socket.socket() as sock:
        sock.bind(('127.0.0.1', 0))
        return sock.getsockname()[1]


def eventually(check, seconds=15):
    deadline = time.monotonic() + seconds
    while time.monotonic() < deadline:
        try:
            value = check()
            if value:
                return value
        except (OSError, KeyError):
            pass
        time.sleep(.15)
    raise AssertionError('Timed out waiting for local test condition')


class ConfigTests(unittest.TestCase):
    def test_preserves_global_fields_advanced_tables_and_other_arrays(self):
        source = '''serverAddr = "localhost"
auth.token = "private"
# comment
[[proxies]]
name = "alpha"
type = "tcp"
localPort = 8000
remotePort = 9000
[proxies.transport]
useEncryption = true
[[proxies]]
name = "beta"
type = "udp"
localPort = 8001
remotePort = 9001
[[visitors]]
name = "visitor"
type = "stcp"
'''
        replacement = {'name': 'renamed', 'type': 'tcp', 'localIP': '127.0.0.1', 'localPort': 8010, 'remotePort': 9010}
        result = tomllib.loads(panel.edit_text(source, 'alpha', replacement))
        original = tomllib.loads(source)
        self.assertEqual(result['auth'], original['auth'])
        self.assertEqual(result['proxies'][0]['transport'], {'useEncryption': True})
        self.assertEqual(result['proxies'][1], original['proxies'][1])
        self.assertEqual(result['visitors'], original['visitors'])
        removed = tomllib.loads(panel.edit_text(source, 'alpha', None))
        self.assertEqual(len(removed['proxies']), 1)
        self.assertEqual(removed['visitors'], original['visitors'])
        self.assertEqual(tomllib.loads(panel.dump_toml(original)), original)
        serialized = panel.dump_toml(original)
        self.assertEqual(tomllib.loads(panel.edit_text(serialized, 'alpha', replacement))['proxies'][0]['name'], 'renamed')

    def test_rejects_conflicting_names_and_invalid_ports(self):
        p = {'name': 'alpha', 'type': 'tcp', 'localIP': '127.0.0.1', 'localPort': 8000, 'remotePort': 9000}
        with self.assertRaises(ValueError):
            panel.validate_proxy(p, {'proxies': [p]})
        with self.assertRaises(ValueError):
            panel.validate_proxy({**p, 'remotePort': 65536}, {})
        with self.assertRaises(ValueError):
            panel.validate_proxy({**p, 'localPort': True}, {})


class ConnectionSettingsTests(unittest.TestCase):
    def test_connection_save_reset_token_modes_and_preservation(self):
        original_paths = panel.CLIENT, panel.CONFIG, panel.ROOT
        with tempfile.TemporaryDirectory(prefix='frp-settings-test-') as directory:
            work = Path(directory)
            panel.CLIENT, panel.CONFIG, panel.ROOT = work, work / 'frpc.toml', work
            (work / 'token.txt').write_text('file-token-value', encoding='utf-8')
            source = {'serverAddr': '127.0.0.1', 'serverPort': 7000,
                      'webServer': {'addr': '127.0.0.1', 'port': free_port(), 'user': 'admin', 'password': 'private-admin'},
                      'auth': {'tokenSource': {'type': 'file', 'file': {'path': './token.txt'}}},
                      'transport': {'poolCount': 2, 'tls': {'enable': True}},
                      'proxies': [{'name': 'old', 'type': 'tcp', 'localPort': 8000, 'remotePort': 9000}]}
            panel.CONFIG.write_text(panel.dump_toml(source), encoding='utf-8')
            try:
                with patch.object(panel, 'runtime', return_value=(False, {}, 'offline')):
                    initial = panel.get_state()
                    form = {'address': 'frp.example.com', 'port': 7001, 'user': 'home', 'tls': True, 'protocol': 'v1', 'tokenMode': 'keep', 'revision': initial['revision']}
                    with self.assertRaises(ValueError):
                        panel.save_connection({**form, 'address': 'https://example.com:7000'})
                    panel.save_connection(form)
                    saved = panel.read_config()[1]
                    self.assertEqual(saved['auth'], source['auth'])
                    self.assertEqual(saved['proxies'], source['proxies'])
                    self.assertEqual(saved['transport']['poolCount'], 2)
                    self.assertEqual(saved['serverAddr'], form['address'])
                    self.assertNotIn('private-admin', json.dumps(panel.get_state()))
                    with self.assertRaises(ValueError):
                        panel.save_connection(form)
                    current = panel.get_state()
                    panel.save_connection({**form, 'revision': current['revision'], 'tokenMode': 'replace', 'authToken': 'replacement-secret'})
                    saved = panel.read_config()[1]
                    self.assertEqual(saved['auth']['token'], 'replacement-secret')
                    self.assertNotIn('tokenSource', saved['auth'])
                    self.assertNotIn('replacement-secret', json.dumps(panel.get_state()))
                    current = panel.get_state()
                    panel.save_connection({**form, 'revision': current['revision'], 'tokenMode': 'clear'})
                    self.assertFalse(panel.get_state()['server']['hasToken'])
                    before_reset = panel.read_config()[0]
                    result = panel.reset_connection({'revision': panel.get_state()['revision']})
                    blank = panel.get_state()
                    self.assertFalse(blank['configured'])
                    self.assertFalse(blank['server']['hasToken'])
                    self.assertEqual(blank['proxies'], [])
                    self.assertEqual(blank['server']['user'], '')
                    self.assertEqual((work / 'backups' / result['backup']).read_text(), before_reset)
                    self.assertFalse((work / panel.read_config()[1]['log']['to']).exists())
                    with self.assertRaises(ValueError):
                        panel.control('start')
                    panel.save_connection({**form, 'revision': blank['revision'], 'tokenMode': 'replace', 'authToken': 'new-setup-token'})
                    self.assertTrue(panel.get_state()['configured'])
                    # Global edits are blocked while the admin port is occupied.
                    with socket.socket() as listener:
                        listener.bind(('127.0.0.1', panel.get_state()['server']['adminPort']))
                        listener.listen()
                        with self.assertRaises(ValueError):
                            panel.reset_connection({'revision': panel.get_state()['revision']})
            finally:
                panel.CLIENT, panel.CONFIG, panel.ROOT = original_paths


class LocalIntegrationTests(unittest.TestCase):
    def test_real_frp_lifecycle_reload_forwarding_and_http_guards(self):
        """Only loopback endpoints and disposable configs; never starts the real client."""
        frps_exe = ROOT.parent / 'frp_0.71.0_windows_amd64' / 'frps.exe'
        frpc_exe = ROOT.parent / 'client' / 'frpc.exe'
        if not frps_exe.exists():
            self.skipTest('Local frps.exe is unavailable; temporary binary was blocked by Windows security')
        previous = (panel.CLIENT, panel.CONFIG, panel.EXE, panel.ROOT, panel.PORT)
        with tempfile.TemporaryDirectory(prefix='frp-panel-test-') as directory:
            work = Path(directory)
            control_port, admin_port, public_port = free_port(), free_port(), free_port()
            echo = socket.socket()
            echo.bind(('127.0.0.1', 0))
            echo.listen(4)
            echo.settimeout(.3)
            stop_echo = threading.Event()

            def echo_loop():
                while not stop_echo.is_set():
                    try:
                        connection, _ = echo.accept()
                        with connection:
                            connection.settimeout(2)
                            connection.sendall(connection.recv(100))
                    except (OSError, TimeoutError):
                        continue
            worker = threading.Thread(target=echo_loop, daemon=True)
            worker.start()
            source = f'''serverAddr = "127.0.0.1"
serverPort = {control_port}
loginFailExit = false
auth.token = "integration-test-only"
webServer.addr = "127.0.0.1"
webServer.port = {admin_port}
webServer.user = "test"
webServer.password = "test-panel-password"
log.to = "./logs/frpc.log"
'''
            config_file = work / 'frpc.toml'
            config_file.write_text(source, encoding='utf-8')
            server_file = work / 'frps.toml'
            server_file.write_text(f'bindAddr = "127.0.0.1"\nbindPort = {control_port}\nproxyBindAddr = "127.0.0.1"\nauth.token = "integration-test-only"\n', encoding='utf-8')
            panel.CLIENT, panel.CONFIG, panel.EXE, panel.ROOT, panel.PORT = work, config_file, frpc_exe, work, free_port()
            frps = subprocess.Popen([str(frps_exe), '-c', str(server_file)], cwd=work, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, creationflags=panel.NO_WINDOW)
            http_server = panel.ThreadingHTTPServer(('127.0.0.1', panel.PORT), panel.Handler)
            threading.Thread(target=http_server.serve_forever, daemon=True).start()

            def api(path, body=None, headers=None):
                connection = http.client.HTTPConnection('127.0.0.1', panel.PORT, timeout=20)
                method = 'GET' if body is None else 'POST'
                request_headers = {'X-Panel-Token': panel.TOKEN, 'Content-Type': 'application/json'}
                request_headers.update(headers or {})
                connection.request(method, path, body=None if body is None else json.dumps(body), headers=request_headers)
                response = connection.getresponse()
                status, payload = response.status, json.loads(response.read())
                connection.close()
                return status, payload

            try:
                self.assertEqual(api('/api/control/start', {}, {'X-Panel-Token': ''})[0], 403)
                self.assertEqual(api('/api/control/start', {}, {'Origin': 'https://example.com'})[0], 403)
                self.assertEqual(api('/api/state', headers={'Host': 'evil.test'})[0], 403)
                initial = api('/api/state')[1]
                self.assertFalse(initial['connected'])
                self.assertNotIn('test-panel-password', json.dumps(initial))
                self.assertNotIn('integration-test-only', json.dumps(initial))
                connection_form = {'address': '127.0.0.1', 'port': control_port, 'user': '', 'tls': True, 'protocol': 'v1', 'tokenMode': 'keep', 'revision': initial['revision']}
                self.assertEqual(api('/api/connection/save', {**connection_form, 'address': 'https://example.com:7000'})[0], 400)
                self.assertEqual(api('/api/connection/save', connection_form)[0], 200)
                self.assertEqual(tomllib.loads(config_file.read_text())['auth']['token'], 'integration-test-only')
                initial = api('/api/state')[1]
                self.assertEqual(api('/api/control/start', {})[0], 200)
                eventually(lambda: api('/api/state')[1]['connected'])
                self.assertEqual(api('/api/connection/reset', {'revision': initial['revision']})[0], 400)
                proxy = {'name': 'echo', 'type': 'tcp', 'localIP': '127.0.0.1', 'localPort': echo.getsockname()[1], 'remotePort': public_port}
                status, result = api('/api/proxy/save', {'proxy': proxy, 'revision': initial['revision']})
                self.assertEqual(status, 200, result)
                eventually(lambda: any(p['status'] == 'running' for p in api('/api/state')[1]['proxies']))

                def forwarded():
                    with socket.create_connection(('127.0.0.1', public_port), timeout=2) as connection:
                        connection.sendall(b'frp-panel-live-check')
                        return connection.recv(100) == b'frp-panel-live-check'
                self.assertTrue(eventually(forwarded))
                # A stale editor must not overwrite more recent changes.
                self.assertEqual(api('/api/proxy/save', {'proxy': proxy, 'original': 'echo', 'revision': initial['revision']})[0], 400)
                current = api('/api/state')[1]
                updated = {**proxy, 'name': 'echo-renamed'}
                self.assertEqual(api('/api/proxy/save', {'proxy': updated, 'original': 'echo', 'revision': current['revision']})[0], 200)
                eventually(lambda: any(p['name'] == 'echo-renamed' and p['status'] == 'running' for p in api('/api/state')[1]['proxies']))
                self.assertTrue(eventually(forwarded))
                current = api('/api/state')[1]
                self.assertEqual(api('/api/proxy/delete', {'original': 'echo-renamed', 'revision': current['revision']})[0], 200)
                self.assertEqual(api('/api/state')[1]['proxies'], [])
                self.assertEqual(len(list((work / 'backups').glob('*.toml'))), 4)
                self.assertEqual(api('/api/control/stop', {})[0], 200)
                eventually(lambda: panel.PROCESS.poll() is not None)
                self.assertFalse(api('/api/state')[1]['connected'])
                current = api('/api/state')[1]
                self.assertEqual(api('/api/proxy/save', {'proxy': proxy, 'revision': current['revision']})[0], 200)
                current = api('/api/state')[1]
                self.assertEqual(api('/api/connection/reset', {'revision': current['revision']})[0], 200)
                blank = api('/api/state')[1]
                self.assertFalse(blank['configured'])
                self.assertFalse(blank['server']['hasToken'])
                self.assertEqual(blank['proxies'], [])
                self.assertEqual(api('/api/logs')[1]['lines'], [])
                self.assertEqual(api('/api/control/start', {})[0], 400)
                self.assertEqual(api('/api/proxy/save', {'proxy': proxy, 'revision': blank['revision']})[0], 400)
                new_form = {**connection_form, 'revision': blank['revision'], 'tokenMode': 'replace', 'authToken': 'new-test-token'}
                self.assertEqual(api('/api/connection/save', new_form)[0], 200)
                saved = api('/api/state')[1]
                self.assertTrue(saved['configured'])
                self.assertTrue(saved['server']['hasToken'])
                self.assertNotIn('new-test-token', json.dumps(saved))
                self.assertEqual(tomllib.loads(config_file.read_text())['auth']['token'], 'new-test-token')
                self.assertEqual(api('/api/connection/save', {**new_form, 'revision': saved['revision'], 'tokenMode': 'clear'})[0], 200)
                self.assertFalse(api('/api/state')[1]['server']['hasToken'])
            finally:
                http_server.shutdown()
                http_server.server_close()
                if panel.PROCESS and panel.PROCESS.poll() is None:
                    panel.PROCESS.terminate()
                    panel.PROCESS.wait(timeout=8)
                frps.terminate()
                frps.wait(timeout=8)
                stop_echo.set()
                worker.join(timeout=3)
                echo.close()
                panel.PROCESS = None
                panel.CLIENT, panel.CONFIG, panel.EXE, panel.ROOT, panel.PORT = previous


if __name__ == '__main__':
    unittest.main()
