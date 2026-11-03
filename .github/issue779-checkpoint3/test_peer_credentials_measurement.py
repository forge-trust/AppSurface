"""Portable detached TRX parser controls; no kernel credential or native result is fabricated."""
from pathlib import Path
import os
import tempfile
import unittest
import xml.etree.ElementTree as ET
from unittest import mock
import peer_credentials_measurement as subject


def data(changed=None):
    root = ET.Element(subject.NS + 'TestRun')
    counters = {key: '0' for key in subject.ZERO_COUNTERS}
    counters.update(total='4', executed='4', passed='4')
    if changed in counters:
        counters[changed] = '1'
    if changed == 'unknown-counter':
        counters['unexpected'] = '1'
    ET.SubElement(ET.SubElement(root, subject.NS + 'ResultSummary'), subject.NS + 'Counters', counters)
    rows = ET.SubElement(root, subject.NS + 'Results')
    for index, name in enumerate(subject.METHODS):
        if changed == 'missing' and index == 3:
            continue
        if changed == 'duplicate' and index == 3:
            name = subject.METHODS[2]
        if changed == 'wrong-credential' and index == 2:
            name = subject.MISMATCH + '(field: "pidx")'
        ET.SubElement(rows, subject.NS + 'UnitTestResult', testName=name,
                      outcome='NotExecuted' if changed == 'outcome' and index == 0 else 'Passed')
    return ET.tostring(root, encoding='utf-8')


class PeerCredentialsMeasurementControls(unittest.TestCase):
    def test_exact_four_rows_and_non_authority_receipt(self):
        receipt = subject.parse_trx(data())
        self.assertEqual(4, receipt['executed'])
        self.assertEqual(4, receipt['passed'])
        self.assertFalse(receipt['native_acceptance'])

    def test_each_counter_and_fixed_row_neighbor_rejects(self):
        for changed in subject.ZERO_COUNTERS + ('total', 'executed', 'passed', 'unknown-counter',
                                                 'missing', 'duplicate', 'wrong-credential', 'outcome'):
            with self.subTest(changed=changed), self.assertRaises(ValueError):
                subject.parse_trx(data(changed))

    def test_xml_declarations_and_size_bound_reject_before_acceptance(self):
        for raw in (b'', b'x' * (subject.MAXIMUM_BYTES + 1),
                    b'<!DOCTYPE TestRun SYSTEM "https://invalid.invalid">' + data(),
                    b'<!ENTITY x "canary">' + data(), b'\x00' + data()):
            with self.subTest(size=len(raw)), self.assertRaises(ValueError):
                subject.parse_trx(raw)

    def test_stable_regular_single_link_reader(self):
        with tempfile.TemporaryDirectory() as directory:
            leaf = Path(directory) / 'peer-credentials.trx'
            leaf.write_bytes(data())
            self.assertEqual(4, subject.read_trx(leaf)['executed'])
            alias = Path(directory) / 'alias'
            os.link(leaf, alias)
            with self.assertRaises(ValueError):
                subject.read_trx(leaf)
            alias.unlink()
            alias.symlink_to(leaf)
            with self.assertRaises(OSError):
                subject.read_trx(alias)
            leaf.write_bytes(b'x' * (subject.MAXIMUM_BYTES + 1))
            with mock.patch.object(subject, 'parse_trx') as parser, self.assertRaises(ValueError):
                subject.read_trx(leaf)
            parser.assert_not_called()


if __name__ == '__main__':
    unittest.main()
