"""Defined-only actual finalizer DATA controls; no sampler import, real FD or native I/O."""
import ast
from pathlib import Path
from types import SimpleNamespace
import unittest


SOURCE = Path(__file__).with_name('n07-helper-live.py')


def actual_finalizer(close):
    tree = ast.parse(SOURCE.read_text(encoding='utf-8'), filename=str(SOURCE))
    selected = [node for node in tree.body
                if isinstance(node, ast.FunctionDef) and node.name == 'finish_owned_reads']
    if len(selected) != 1:
        raise ValueError('exact-finalizer-required')
    module = ast.Module(body=selected, type_ignores=[])
    namespace = {'os': SimpleNamespace(close=close)}
    # Execute only the actual extracted function definition when controls are authorized.
    exec(compile(ast.fix_missing_locations(module), str(SOURCE), 'exec'), namespace)
    return namespace['finish_owned_reads']


class N07LiveFinalizationControls(unittest.TestCase):
    def arrange(self, errors=None, clock_error=None):
        events = []
        errors = errors or {}

        def close(fd):
            events.append(('close', fd))
            if fd in errors:
                raise errors[fd]

        def clock():
            events.append(('clock', None))
            if clock_error is not None:
                raise clock_error

        return actual_finalizer(close), clock, events

    def assert_order(self, events, reads, parent):
        self.assertEqual([('close', fd) for fd in [*reads, parent]] + [('clock', None)], events)

    def test_success_closes_all_original_reads_then_parent_then_clock(self):
        finish, clock, events = self.arrange()
        self.assertIsNone(finish([11, 12, 13], 10, None, clock))
        self.assert_order(events, [11, 12, 13], 10)

    def test_first_read_close_failure_does_not_skip_later_reads_or_parent(self):
        first = OSError('first-close-data-canary')
        finish, clock, events = self.arrange({11: first, 12: OSError('later'), 10: OSError('parent')})
        with self.assertRaises(OSError) as caught:
            finish([11, 12, 13], 10, None, clock)
        self.assertIs(first, caught.exception)
        self.assert_order(events, [11, 12, 13], 10)

    def test_later_read_close_failure_is_selected_and_parent_is_attempted(self):
        first = OSError('later-read')
        finish, clock, events = self.arrange({12: first})
        with self.assertRaises(OSError) as caught:
            finish([11, 12, 13], 10, None, clock)
        self.assertIs(first, caught.exception)
        self.assert_order(events, [11, 12, 13], 10)

    def test_parent_close_failure_is_selected_after_all_read_attempts(self):
        first = OSError('parent-close')
        finish, clock, events = self.arrange({10: first})
        with self.assertRaises(OSError) as caught:
            finish([11, 12], 10, None, clock)
        self.assertIs(first, caught.exception)
        self.assert_order(events, [11, 12], 10)

    def test_validation_failure_wins_over_multiple_closes_and_late_clock(self):
        original = ValueError('validation-data-canary')
        finish, clock, events = self.arrange({11: OSError('read'), 10: OSError('parent')},
                                             ValueError('late'))
        with self.assertRaises(ValueError) as caught:
            finish([11, 12], 10, original, clock)
        self.assertIs(original, caught.exception)
        self.assert_order(events, [11, 12], 10)

    def test_first_close_failure_wins_over_final_clock_failure(self):
        first = OSError('first-close')
        finish, clock, events = self.arrange({11: first}, ValueError('late'))
        with self.assertRaises(OSError) as caught:
            finish([11, 12], 10, None, clock)
        self.assertIs(first, caught.exception)
        self.assert_order(events, [11, 12], 10)

    def test_original_clock_expiry_during_final_close_rejects_after_all_attempts(self):
        events = []
        now = [99]
        original_end = 100
        late = ValueError('original-end-expired')

        def close(fd):
            events.append(('close', fd))
            if fd == 10:
                now[0] = original_end

        def clock():
            events.append(('clock', None))
            if now[0] >= original_end:
                raise late

        finish = actual_finalizer(close)
        with self.assertRaises(ValueError) as caught:
            finish([11, 12], 10, None, clock)
        self.assertIs(late, caught.exception)
        self.assertEqual(100, original_end)
        self.assert_order(events, [11, 12], 10)

    def test_empty_partial_setup_still_attempts_parent_and_original_clock(self):
        for failure in (None, OSError('parent-partial')):
            with self.subTest(parent_close_failure=failure is not None):
                finish, clock, events = self.arrange({10: failure} if failure is not None else None)
                if failure is None:
                    self.assertIsNone(finish([], 10, None, clock))
                else:
                    with self.assertRaises(OSError) as caught:
                        finish([], 10, None, clock)
                    self.assertIs(failure, caught.exception)
                self.assert_order(events, [], 10)


if __name__ == '__main__':
    unittest.main()
