import importlib.util
from pathlib import Path
import tempfile
import unittest

spec = importlib.util.spec_from_file_location('first_run_backend', Path(__file__).resolve().parents[1] / 'server.py')
panel = importlib.util.module_from_spec(spec)
spec.loader.exec_module(panel)


class FirstRunTests(unittest.TestCase):
    def test_blank_private_defaults_and_existing_config_preserved(self):
        with tempfile.TemporaryDirectory() as folder:
            panel.CLIENT = Path(folder) / 'client'
            panel.CONFIG = panel.CLIENT / 'frpc.toml'
            panel.initialize_config()
            text, config = panel.read_config()
            self.assertEqual(config['serverAddr'], '')
            self.assertNotIn('proxies', config)
            self.assertEqual(config['webServer']['addr'], '127.0.0.1')
            self.assertGreaterEqual(len(config['webServer']['password']), 32)
            panel.initialize_config()
            self.assertEqual(panel.CONFIG.read_text(), text)
            panel.CONFIG.write_text('serverAddr = "example.com"\n')
            panel.initialize_config()
            self.assertEqual(panel.CONFIG.read_text(), 'serverAddr = "example.com"\n')
