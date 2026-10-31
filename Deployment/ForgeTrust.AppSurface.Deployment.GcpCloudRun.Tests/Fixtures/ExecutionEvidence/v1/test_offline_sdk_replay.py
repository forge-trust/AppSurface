import importlib.util
from pathlib import Path
import unittest


SCRIPT = Path(__file__).with_name("offline-sdk-replay.py")
SPEC = importlib.util.spec_from_file_location("offline_sdk_replay", SCRIPT)
REPLAY = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(REPLAY)


class DifferencesTests(unittest.TestCase):
    def test_shorter_array_reports_missing_right_element(self):
        self.assertEqual(
            [("/values/1", "int", "missing")],
            list(REPLAY.differences({"values": [1, 2]}, {"values": [1]})),
        )

    def test_longer_array_reports_missing_left_element(self):
        self.assertEqual(
            [("/values/1", "missing", "int")],
            list(REPLAY.differences({"values": [1]}, {"values": [1, 2]})),
        )

    def test_nested_array_length_difference_keeps_recursive_path(self):
        self.assertEqual(
            [("/items/0/values/1", "str", "missing")],
            list(REPLAY.differences({"items": [{"values": ["a", "b"]}]}, {"items": [{"values": ["a"]}]})),
        )

    def test_equal_arrays_have_no_differences(self):
        self.assertEqual(
            [],
            list(REPLAY.differences({"items": [{"values": [1, 2]}]}, {"items": [{"values": [1, 2]}]})),
        )

    def test_missing_key_differs_from_explicit_null(self):
        self.assertEqual(
            [("/value", "missing", "NoneType")],
            list(REPLAY.differences({}, {"value": None})),
        )

    def test_boolean_differs_from_integer(self):
        self.assertEqual(
            [("/value", "bool", "int")],
            list(REPLAY.differences({"value": True}, {"value": 1})),
        )

    def test_generation_and_observed_generation_allow_equal_string_to_integer_values(self):
        rendered = {"metadata": {"generation": 7}, "status": {"observedGeneration": 7}}
        original = {"metadata": {"generation": "7"}, "status": {"observedGeneration": "7"}}

        diff = REPLAY.validated_differences(rendered, original)

        self.assertEqual(
            {"/metadata/generation", "/status/observedGeneration"},
            {item[0] for item in diff},
        )

    def test_timeout_allows_equal_integer_to_string_value(self):
        rendered = {"spec": {"template": {"spec": {"timeoutSeconds": "60"}}}}
        original = {"spec": {"template": {"spec": {"timeoutSeconds": 60}}}}

        diff = REPLAY.validated_differences(rendered, original)

        self.assertEqual(["/spec/template/spec/timeoutSeconds"], [item[0] for item in diff])

    def test_generation_value_mismatch_is_rejected(self):
        with self.assertRaises(ValueError):
            REPLAY.validated_differences(
                {"metadata": {"generation": 8}},
                {"metadata": {"generation": "7"}},
            )

    def test_observed_generation_value_mismatch_is_rejected(self):
        with self.assertRaises(ValueError):
            REPLAY.validated_differences(
                {"status": {"observedGeneration": 8}},
                {"status": {"observedGeneration": "7"}},
            )

    def test_timeout_value_mismatch_is_rejected(self):
        with self.assertRaises(ValueError):
            REPLAY.validated_differences(
                {"spec": {"template": {"spec": {"timeoutSeconds": "61"}}}},
                {"spec": {"template": {"spec": {"timeoutSeconds": 60}}}},
            )

    def test_missing_key_and_null_at_allowed_path_are_rejected(self):
        with self.assertRaises(ValueError):
            REPLAY.validated_differences(
                {"metadata": {"generation": 7}},
                {"metadata": {}},
            )

        with self.assertRaises(ValueError):
            REPLAY.validated_differences(
                {"metadata": {"generation": 7}},
                {"metadata": {"generation": None}},
            )

    def test_boolean_is_not_accepted_as_integer_at_allowed_path(self):
        with self.assertRaises(ValueError):
            REPLAY.validated_differences(
                {"metadata": {"generation": True}},
                {"metadata": {"generation": "1"}},
            )

    def test_list_is_not_accepted_as_integer_at_allowed_path(self):
        with self.assertRaises(ValueError):
            REPLAY.validated_differences(
                {"metadata": {"generation": [7]}},
                {"metadata": {"generation": "7"}},
            )


if __name__ == "__main__":
    unittest.main()
