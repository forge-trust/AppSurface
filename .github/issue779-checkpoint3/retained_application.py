"""Pure C3 retained-receipt predicates shared by the native runner and replay."""
from __future__ import annotations

import hashlib
import json
import re
import time

BASE = '416b30919e6c4d03b634d9d3fe558884b02f0b91'
CASES = ('normal', 'sdk-normal', 'readiness', 'factory-stall', 'cancel', 'stuck-descendant', 'disposal-failure')
GUARDS = ('bundle-extra-directory', 'bundle-input-bytes', 'bundle-input-mode', 'scratch-fifo',
          'scratch-hardlink', 'scratch-oversize')


def require(value, category):
    if not value:
        raise ValueError('c3-application-' + category)


def _json(raw, category):
    require(type(raw) is bytes and len(raw) <= 8 * 1024 * 1024, category + '-bytes')
    def pairs(rows):
        value = {}
        for key, item in rows:
            require(key not in value, category + '-duplicate')
            value[key] = item
        return value
    value = json.loads(raw, object_pairs_hook=pairs,
                       parse_constant=lambda _: (_ for _ in ()).throw(ValueError(category)))
    require(type(value) is dict, category + '-object')
    return value


def validate(receipt, *, source, base, files, events, output_accounting, owner_reference_guards, deadline):
    """Validate the fixed seven-case application receipt and retained owner logs.

    `events`, `output_accounting`, and `owner_reference_guards` are fixed to the original
    run-native parsers by the two source-owned callers; callers must not supply
    alternate parsers from artifact data.
    """
    require(type(receipt) is dict and receipt.get('schema') == 'issue779-csharp-checkpoint3-native-v1'
            and receipt.get('source') == source and receipt.get('base') == base == BASE
            and type(receipt.get('exit')) is int and receipt['exit'] == 0
            and receipt.get('trusted_eligibility') is False, 'header')
    rows = receipt.get('cases')
    require(type(rows) is list and all(type(row) is dict for row in rows)
            and tuple(row.get('case') for row in rows) == CASES, 'case-order')
    for row in rows:
        require(type(deadline) in (int, float) and time.monotonic() < deadline, 'deadline')
        case = row['case']
        generation = row.get('generation')
        require(type(generation) is str and re.fullmatch('[0-9a-f]{32}', generation)
                and row.get('passed') is True and row.get('native_acceptance') is False
                and row.get('groups_empty') is True and row.get('final_groups_empty') is True
                and row.get('application_prefixes_retained') is True, 'case-settlement')
        require(type(row.get('root_exit')) is int
                and (row['root_exit'] == 0 if case in ('normal', 'sdk-normal') else row['root_exit'] != 0),
                'case-root-exit')
        account_present = case == 'disposal-failure'
        expected_accounts = [
            {'name': prefix + generation[:28], 'table': table, 'present': account_present}
            for prefix, table in (('evw', 'passwd'), ('evs', 'passwd'), ('eva', 'passwd'),
                                  ('evw', 'group'), ('evs', 'group'), ('eva', 'group'),
                                  ('evr', 'group'), ('evg', 'group'))]
        require(row.get('accounts') == expected_accounts, 'case-accounts')
        require(type(row.get('application_observations')) is list
                and sum(type(event) is dict and event.get('phase') == 'settled'
                        for event in row['application_observations']) == 1,
                'application-settled')
        settled = next(event for event in row['application_observations']
                       if type(event) is dict and event.get('phase') == 'settled')
        require(settled.get('group_empty_twice') is True and settled.get('pumps_joined') is True
                and settled.get('pending_start_joined') is True,
                'application-join')
        owner_path = 'native/' + case + '-owner.stderr'
        stdout_path = 'native/' + case + '-owner.stdout'
        stderr = files.get(owner_path)
        stdout = files.get(stdout_path)
        require(type(stderr) is bytes and type(stdout) is bytes and len(stderr) <= 8 * 1024 * 1024
                and len(stdout) <= 8 * 1024 * 1024, 'owner-logs')
        case_file = _json(files.get('native/' + case + '.json'), 'case-file')
        require(case_file == row, 'case-file-binding')
        if case not in ('normal', 'sdk-normal'):
            require(stdout == b'', 'negative-owner-stdout')
        parsed_guards = owner_reference_guards(stderr, generation)
        require(parsed_guards == row.get('owner_reference_guards'), 'owner-guard-receipt')
        if case != 'disposal-failure':
            observation = output_accounting(stderr)
            require(observation == row.get('output'), 'output-receipt')
        else:
            storage = events(stderr, 'evidence-application-storage-v1')
            require(any(event.get('phase') == 'unmount-rejected' and event.get('errno') == 16
                        for event in storage), 'disposal-storage-failure')
        if case in ('normal', 'sdk-normal'):
            require(len(stdout) > 0 and row.get('producer') is not None and row.get('dcp') is not None
                    and row.get('coverage') is not None,
                    'positive-facts')
            summary = _json(stdout, 'summary')
            events_for_case = row['application_observations']
            ready = [event for event in events_for_case
                     if type(event) is dict and event.get('phase') == 'ready']
            require(settled.get('main_code') == 1 and settled.get('main_status') == 0
                    and len(ready) == 1 and ready[0].get('dcp_checked') is True,
                    'positive-settlement')
            require(summary.get('ExecutionVerdict') == 'Passed' and summary.get('CleanupCompleted') is True
                    and summary.get('ClaimKind') == 'ObservationOnly', 'summary')
            manifest_path = 'native/' + case + '-manifest.json'
            manifest = _json(files.get(manifest_path), 'manifest')
            producer_results = manifest.get('ProducerResults')
            require(type(producer_results) is list and len(producer_results) == 1
                    and type(producer_results[0]) is dict
                    and type(producer_results[0].get('Artifacts')) is list
                    and len(producer_results[0]['Artifacts']) == 1
                    and type(producer_results[0]['Artifacts'][0]) is dict
                    and type(manifest.get('Metrics')) is dict
                    and manifest.get('ExecutionVerdict') == 'Passed'
                    and manifest.get('Metrics', {}).get('CleanupCompleted') is True
                    and manifest.get('ClaimKind') == 'ObservationOnly'
                    and producer_results[0].get('Outcome') == 'Passed'
                    and producer_results[0]['Artifacts'][0].get('RelativePath')
                    == 'merged/coverage.cobertura.xml', 'manifest')
        elif case == 'readiness':
            negative = row.get('http_negative')
            require(type(negative) is dict and negative.get('status') == 503
                    and type(negative.get('received_bytes')) is int and row.get('dcp') is not None
                    and row.get('resource') is not None, 'readiness')
        elif case == 'factory-stall':
            require(row.get('factory_entered') is True and row.get('dcp') is None
                    and not any(type(event) is dict and event.get('phase') == 'ready'
                                for event in row['application_observations']), 'factory-stall')
        elif case in ('cancel', 'stuck-descendant'):
            require(row.get('cancel_sent') is True and row.get('producer') is not None, 'cancel')
        if case == 'stuck-descendant':
            require(row.get('descendant_term_seen') is True and row.get('descendant') is not None,
                    'stuck-descendant')
        if case == 'disposal-failure':
            require(row.get('scratch_hold') is not None, 'disposal-failure')
    guards = receipt.get('workspace_guards')
    require(type(guards) is list and all(type(row) is dict for row in guards)
            and tuple(row.get('case') for row in guards) == GUARDS, 'workspace-guards')
    for row in guards:
        require(type(deadline) in (int, float) and time.monotonic() < deadline, 'deadline')
        generation = row.get('generation')
        require(type(generation) is str and re.fullmatch('[0-9a-f]{32}', generation),
                'workspace-guard-generation')
        workspace = row.get('workspace_guard')
        require(workspace is None or type(workspace) is dict, 'workspace-guard-shape')
        failure = row.get('first_failure') or (workspace or {}).get('first_failure')
        expected_code = 'ASEVD410' if row.get('case') in GUARDS[:3] else 'ASEVD402'
        expected_accounts = [
            {'name': prefix + generation[:28], 'table': table, 'present': True}
            for prefix, table in (('evw', 'passwd'), ('evs', 'passwd'), ('eva', 'passwd'),
                                  ('evw', 'group'), ('evs', 'group'), ('eva', 'group'),
                                  ('evr', 'group'), ('evg', 'group'))]
        guard_name = row.get('case')
        guard_stderr = files.get('native/' + guard_name + '-owner.stderr')
        guard_stdout = files.get('native/' + guard_name + '-owner.stdout')
        require(type(guard_stderr) is bytes and type(guard_stdout) is bytes, 'workspace-guard-logs')
        guard_file = _json(files.get('native/' + guard_name + '.json'), 'workspace-guard-file')
        failure_events = events(guard_stderr, 'evidence-native-observation-failure-v4')
        require(guard_stdout == b''
                and guard_file == row and len(failure_events) == 1
                and failure_events[0] == failure, 'workspace-guard-logs')
        require(row.get('passed') is True and row.get('native_acceptance') is False
                and row.get('groups_empty') is True and row.get('final_groups_empty') is True
                and row.get('outside_sentinel_unchanged') is True and row.get('quarantined') is True
                and type(row.get('root_exit')) is int and row['root_exit'] != 0
                and type(failure) is dict and failure.get('phase') ==
                ('WorkspaceCreate' if expected_code == 'ASEVD410' else 'ProducerSeal')
                and failure.get('diagnostic_code') == expected_code
                and row.get('accounts') == expected_accounts, 'workspace-guard-facts')
    require(type(deadline) in (int, float) and time.monotonic() < deadline, 'deadline')
    return {'schema': receipt['schema'], 'source': source, 'base': base,
            'cases': tuple(CASES), 'native_acceptance': False, 'trusted_eligibility': False}
