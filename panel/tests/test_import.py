import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('import_backend', Path(__file__).resolve().parents[1] / 'server.py')
panel = importlib.util.module_from_spec(spec)
spec.loader.exec_module(panel)


class ImportTests(unittest.TestCase):
    def setUp(self):
        self.folder = tempfile.TemporaryDirectory(prefix='frp-import-test-')
        self.addCleanup(self.folder.cleanup)
        self.old = panel.CLIENT, panel.CONFIG, panel.ROOT
        self.addCleanup(self.restore)
        panel.CLIENT = panel.ROOT = Path(self.folder.name)
        panel.CONFIG = panel.CLIENT / 'frpc.toml'
        panel.CONFIG.write_text('serverAddr = ""\nwebServer.addr = "127.0.0.1"\nwebServer.port = 7400\nwebServer.password = "local-secret"\nlog.to = "./logs/local.log"\n')
        self.original = panel.CONFIG.read_text()
        self.body = {'filename': 'frpc.toml', 'content': '''serverAddr = "127.0.0.1"
serverPort = 7000
auth.token = "import-private-token"
webServer.addr = "0.0.0.0"
webServer.port = 9000
log.to = "unwanted.log"
[[proxies]]
name = "import-example"
type = "tcp"
localPort = 8080
remotePort = 18080
transport.useEncryption = true
'''}

    def restore(self):
        panel.CLIENT, panel.CONFIG, panel.ROOT = self.old

    def test_preview_and_import_preserve_local_management_and_backup(self):
        preview = panel.import_connection(self.body, preview=True)
        self.assertEqual(panel.CONFIG.read_text(), self.original)
        self.assertEqual(preview['proxyCount'], 1)
        self.assertNotIn('import-private-token', json.dumps(preview))
        self.assertNotIn('local-secret', json.dumps(preview))
        with patch.object(panel, 'ensure_stopped'):
            result = panel.import_connection({**self.body, 'revision': preview['revision']})
        data = panel.read_config()[1]
        self.assertEqual(data['webServer']['addr'], '127.0.0.1')
        self.assertEqual(data['webServer']['port'], 7400)
        self.assertEqual(data['log']['to'], './logs/local.log')
        self.assertEqual(data['auth']['token'], 'import-private-token')
        self.assertTrue(data['proxies'][0]['transport']['useEncryption'])
        self.assertEqual((panel.ROOT / 'backups' / result['backup']).read_text(), self.original)
        with self.assertRaises(ValueError):
            panel.import_connection({**self.body, 'revision': preview['revision']})

    def test_invalid_files_do_not_overwrite_or_expose_secrets(self):
        for body in ({**self.body, 'filename': 'frpc.ini'},
                     {**self.body, 'content': 'bindPort = 7000'},
                     {**self.body, 'content': 'auth.token = "sensitive-invalid'},
                     {**self.body, 'content': 'serverAddr = "localhost"\nincludes = ["other.toml"]'},
                     {**self.body, 'content': self.body['content'] + '\n[[proxies]]\nname = "import-example"\ntype = "tcp"\nlocalPort = 8081\nremotePort = 18081\n'}):
            with self.assertRaises(ValueError) as caught:
                panel.import_connection(body, preview=True)
            self.assertNotIn('sensitive-invalid', str(caught.exception))
            self.assertNotIn('import-private-token', str(caught.exception))
            self.assertEqual(panel.CONFIG.read_text(), self.original)

    def test_running_client_blocks_import(self):
        preview = panel.import_connection(self.body, preview=True)
        with patch.object(panel, 'ensure_stopped', side_effect=ValueError('client active')):
            with self.assertRaisesRegex(ValueError, 'client active'):
                panel.import_connection({**self.body, 'revision': preview['revision']})
        self.assertEqual(panel.CONFIG.read_text(), self.original)

    def test_web_proxy_missing_domain_has_actionable_private_error(self):
        for protocol in ('http', 'https'):
            with self.subTest(protocol=protocol):
                body = {'filename': 'web.toml', 'content': f'''serverAddr = "127.0.0.1"
auth.token = "private-web-token"
[[proxies]]
name = "private-web-name"
type = "{protocol}"
localPort = 3000
'''}
                for preview in (True, False):
                    with patch.object(panel, 'ensure_stopped'):
                        with self.assertRaisesRegex(ValueError, f'第 1 条 {protocol.upper()} 代理缺少访问域名') as caught:
                            panel.import_connection({**body, 'revision': panel.hashlib.sha256(self.original.encode()).hexdigest()}, preview=preview)
                    self.assertIn('customDomains', str(caught.exception))
                    self.assertNotIn('private-web', str(caught.exception))
                    self.assertEqual(panel.CONFIG.read_text(), self.original)

    def test_web_proxy_domain_forms_pass_real_validator_and_are_preserved(self):
        for protocol in ('http', 'https'):
            for domain in ('customDomains = ["web.example.com"]', 'subdomain = "example"'):
                with self.subTest(protocol=protocol, domain=domain):
                    body = {'filename': 'web.toml', 'content': f'''serverAddr = "127.0.0.1"
[[proxies]]
name = "web-example"
type = "{protocol}"
localPort = 3000
{domain}
transport.useEncryption = true
'''}
                    preview = panel.import_connection(body, preview=True)
                    self.assertEqual(preview['proxyCount'], 1)
                    candidate, _ = panel.prepare_import(body, panel.read_config()[1])
                    parsed = panel.tomllib.loads(candidate)['proxies'][0]
                    self.assertTrue(parsed['transport']['useEncryption'])
                    if domain.startswith('customDomains'):
                        self.assertEqual(parsed['customDomains'], ['web.example.com'])
                    else:
                        self.assertEqual(parsed['subdomain'], 'example')
                    self.assertEqual(panel.CONFIG.read_text(), self.original)
