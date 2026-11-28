"""Keep the normal-runner and legacy-bootstrap Keycloak fixture pin aligned."""

import importlib.util
from pathlib import Path
import tempfile
import unittest

from replay_environment import keycloak_fixture_image


def load_fixture_module(name, filename):
    path = Path(__file__).with_name(filename)
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


class NormalReplayFixturePinTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.runner = load_fixture_module("normal_fixture_runner", "run-normal-supervised.py")
        cls.legacy = load_fixture_module("legacy_fixture_bootstrap", "prime-legacy-volume.py")

    def test_tagged_and_digest_only_forms_come_from_the_fixture_release(self):
        self.assertEqual(
            "quay.io/keycloak/keycloak@sha256:831330513f55695572286e521f94fcd3c7e285250ed5b848090265a33192f669",
            keycloak_fixture_image(tagged=False),
        )
        self.assertEqual(
            "quay.io/keycloak/keycloak:26.7.2@sha256:831330513f55695572286e521f94fcd3c7e285250ed5b848090265a33192f669",
            keycloak_fixture_image(tagged=True),
        )

    def test_runner_and_historical_uid_bootstrap_use_the_shared_pin(self):
        self.assertEqual(keycloak_fixture_image(tagged=False), self.runner.KEYCLOAK_IMAGE)
        self.assertEqual(keycloak_fixture_image(tagged=True), self.runner.KEYCLOAK_IMAGE_TAGGED)
        self.assertEqual(keycloak_fixture_image(tagged=False), self.legacy.KEYCLOAK_IMAGE)

    def test_tunnel_theme_input_carries_the_tagged_pin(self):
        with tempfile.TemporaryDirectory() as scratch:
            case = Path(scratch)
            inputs = {}
            self.runner.add_tunnel_theme_input("skoolit-tunnel", case, inputs)
            self.assertEqual(str(case / "theme"), inputs["ThemeDirectory"])
            self.assertEqual(keycloak_fixture_image(tagged=True), inputs["ThemeImageReference"])

            other_inputs = {}
            self.runner.add_tunnel_theme_input("skoolit-fresh", case, other_inputs)
            self.assertEqual({}, other_inputs)


if __name__ == "__main__":
    unittest.main()
