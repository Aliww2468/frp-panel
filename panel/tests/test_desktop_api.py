import http.client
import importlib.util
import json
from pathlib import Path
import tempfile
import threading
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('desktop_backend', Path(__file__).resolve().parents[1] / 'server.py')
panel = importlib.util.module_from_spec(spec)
spec.loader.exec_module(panel)


class DesktopApiTests(unittest.TestCase):
    def test_health_identity_and_authenticated_shutdown(self):
        with tempfile.TemporaryDirectory(prefix='frp-desktop-api-') as directory:
            panel.CLIENT = Path(directory) / 'client'
            panel.CLIENT.mkdir()
            panel.CONFIG = panel.CLIENT / 'frpc.toml'
            panel.CONFIG.write_text('serverAddr = ""\n')
            server = panel.ThreadingHTTPServer(('127.0.0.1', 0), panel.Handler)
            panel.PORT = server.server_port
            worker = threading.Thread(target=server.serve_forever, daemon=True)
            worker.start()

            def request(method, path, token=''):
                connection = http.client.HTTPConnection('127.0.0.1', panel.PORT, timeout=3)
                connection.request(method, path, body='{}' if method == 'POST' else None, headers={'X-Panel-Token': token})
                result = connection.getresponse()
                status, value = result.status, json.loads(result.read())
                connection.close()
                return status, value

            try:
                status, health = request('GET', '/api/health')
                self.assertEqual(status, 200)
                self.assertEqual(health['workspace'], str(Path(directory)))
                self.assertEqual(health['app'], 'frp-panel')
                self.assertEqual(request('POST', '/api/desktop/exit')[0], 403)
                self.assertTrue(worker.is_alive())
                with patch.object(panel, 'runtime', return_value=(False, {}, 'offline')):
                    self.assertEqual(request('POST', '/api/desktop/exit', panel.TOKEN)[0], 200)
                worker.join(timeout=3)
                self.assertFalse(worker.is_alive())
                self.assertEqual(panel.CONFIG.read_text(), 'serverAddr = ""\n')
            finally:
                server.shutdown()
                server.server_close()
