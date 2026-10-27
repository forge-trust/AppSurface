"""Detached XML and ordinary-file parser controls; no collector or native proof is fabricated."""
from pathlib import Path
import os
import tempfile
import unittest
import xml.etree.ElementTree as ET
from unittest import mock
import application_bundle_measurement as subject


def data(changed=None):
    root = ET.Element(subject.NS + 'TestRun')
    counters = {key: '0' for key in subject.ZERO_COUNTERS}
    counters.update(total='6', executed='6', passed='6')
    if changed in counters:
        counters[changed] = '1'
    if changed == 'unknown-counter':
        counters['unrecognizedFailure'] = '1'
    ET.SubElement(ET.SubElement(root, subject.NS + 'ResultSummary'), subject.NS + 'Counters', counters)
    rows = ET.SubElement(root, subject.NS + 'Results')
    for index, name in enumerate(subject.METHODS):
        if changed == 'missing' and index == 5:
            continue
        if changed == 'duplicate' and index == 5:
            name = subject.METHODS[0]
        if changed == 'foreign' and index == 5:
            name = 'Unselected'
        ET.SubElement(rows, subject.NS + 'UnitTestResult', testName=subject.PREFIX + name,
                      outcome='NotExecuted' if changed == 'outcome' and index == 5 else 'Passed')
    return ET.tostring(root, encoding='utf-8')


class ApplicationBundleMeasurementControls(unittest.TestCase):
    def test_exact_neighbor_and_each_single_counter_mismatch(self):
        self.assertFalse(subject.parse_trx(data())['native_acceptance'])
        for counter in subject.ZERO_COUNTERS + ('total', 'executed', 'passed', 'unknown-counter'):
            with self.subTest(counter=counter), self.assertRaises(ValueError):
                subject.parse_trx(data(counter))

    def test_exact_fixed_rows_and_xml_bounds(self):
        for changed in ('missing', 'duplicate', 'foreign', 'outcome'):
            with self.subTest(changed=changed), self.assertRaises(ValueError):
                subject.parse_trx(data(changed))
        for raw in (b'', b'x' * (subject.MAXIMUM_BYTES + 1),
                    b'<!DOCTYPE TestRun SYSTEM "https://invalid.invalid">' + data(),
                    b'<!ENTITY x "canary">' + data(), b'\x00' + data()):
            with self.assertRaises(ValueError):
                subject.parse_trx(raw)

    def test_real_file_stability_links_and_size_checked_before_xml(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            selected = root / 'application-bundle.trx'
            selected.write_bytes(data())
            self.assertEqual(6, subject.read_trx(selected)['passed'])
            original = selected.read_bytes()
            alias = root / 'alias'
            os.link(selected, alias)
            with self.assertRaises(ValueError):
                subject.read_trx(selected)
            alias.unlink()
            alias.symlink_to(selected)
            with self.assertRaises(OSError):
                subject.read_trx(alias)
            selected.write_bytes(b'x' * (subject.MAXIMUM_BYTES + 1))
            with mock.patch.object(subject, 'parse_trx') as parser, self.assertRaises(ValueError):
                subject.read_trx(selected)
            parser.assert_not_called()
            selected.write_bytes(original)
            self.assertEqual(6, subject.read_trx(selected)['executed'])


if __name__ == '__main__':
    unittest.main()
