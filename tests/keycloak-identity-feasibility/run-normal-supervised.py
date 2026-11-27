#!/usr/bin/env python3
"""Qualify the normal public adapter caller, persistent restarts, contention and guarded recovery."""
import argparse
import fcntl
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import secrets
import shutil
import socket
import subprocess
import tempfile
import time
import urllib.request
import uuid

from replay_environment import docker_endpoint, dotnet_host, verify_empty_cache, verify_prepared_closure

_readback_spec = importlib.util.spec_from_file_location('managed_seed_readback', Path(__file__).with_name('managed-seed-readback.py'))
readback = importlib.util.module_from_spec(_readback_spec)
_readback_spec.loader.exec_module(readback)

_legacy_spec = importlib.util.spec_from_file_location('prime_legacy_volume', Path(__file__).with_name('prime-legacy-volume.py'))

ACCEPTANCE_CASES = {
    'neutral': ('neutral-fresh', 'neutral-repeat'),
    'skoolit': ('skoolit-fresh', 'skoolit-repeat', 'skoolit-google', 'skoolit-google-removed',
                'skoolit-seed-failure', 'skoolit-google-id-only', 'skoolit-google-secret-only', 'skoolit-unapproved-issuer'),
    'cold': ('neutral-fresh',),
    'legacy': ('legacy-missing', 'legacy-repeat', 'legacy-moved', 'legacy-explicit'),
    'legacy-uid1000': ('legacy-missing', 'legacy-repeat', 'legacy-moved', 'legacy-explicit'),
    'tunnel': ('skoolit-tunnel', 'skoolit-unapproved-issuer'),
}
PREFLIGHT_REJECTIONS = {'skoolit-google-id-only', 'skoolit-google-secret-only', 'skoolit-unapproved-issuer'}
MISSING_LEGACY = {'legacy-missing', 'legacy-moved'}
SEED_FAILURES = {'seed-failure', 'skoolit-seed-failure'}
KEYCLOAK_IMAGE = 'quay.io/keycloak/keycloak@sha256:831330513f55695572286e521f94fcd3c7e285250ed5b848090265a33192f669'
KEYCLOAK_IMAGE_TAGGED = KEYCLOAK_IMAGE.replace('@', ':26.7.2@')


def main():
    driver_started_at = time.time()
    driver_started = time.monotonic()
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument('--preparation',type=Path,required=True);p.add_argument('--evidence',type=Path,required=True)
    p.add_argument('--sdk', type=Path, help='Pinned SDK root, required for older preparation receipts')
    p.add_argument('--docker-endpoint', help='Qualified local Docker Unix endpoint')
    mode = p.add_mutually_exclusive_group()
    mode.add_argument('--pilot',action='store_true')
    mode.add_argument('--post-seed', choices=['post-seed-clean', 'drift-client', 'drift-mapper', 'drift-broker'])
    mode.add_argument('--acceptance', choices=ACCEPTANCE_CASES)
    a=p.parse_args();os.umask(0o077)
    historical_uid = a.acceptance == 'legacy-uid1000'
    legacy = a.acceptance in ('legacy', 'legacy-uid1000')
    a.evidence.mkdir(parents=True,exist_ok=False)
    prepared=json.loads(a.preparation.read_text());closure=Path(prepared['root'])
    dotnet = dotnet_host(a.sdk, prepared.get('sdk'))
    endpoint = docker_endpoint(a.docker_endpoint)
    neutral = a.acceptance in ('neutral', 'cold') or legacy
    realm = 'appsurface-dev' if neutral else 'skoolit' if a.acceptance else 'sample'
    client_id = 'appsurface-web' if neutral else 'skoolit-web' if a.acceptance else 'sample-web'
    callback = '/signin-appsurface-oidc' if neutral else '/signin-oidc'
    signed_out_callback = '/signout-callback-appsurface-oidc' if neutral else '/signout-callback-oidc'
    root=Path(tempfile.mkdtemp(prefix='i888-normal-run-',dir='/private/tmp'))
    leases=root/'leases';leases.mkdir()
    nuget=root/'nuget';nuget.mkdir()
    volume='appsurface-issue888-normal-'+uuid.uuid4().hex
    docker=['docker','--host',endpoint]
    values=[secrets.token_urlsafe(32) for _ in range(4)]
    cases=[];checks=[];fault=None
    children = []
    failure_shutdown = []
    bootstrap = None
    phase_times = {}
    legacy_project = moved_project = None
    kinds = ['host', 'payload', 'browser', 'dcp', 'dashboard'] if a.acceptance else ['host', 'payload']
    for kind in kinds:
        verify_prepared_closure(closure/kind, prepared[kind+'Manifest'])
    verify_empty_cache(nuget)
    def check(name,value):
        checks.append({'name':name,'passed':bool(value)})
        if not value:raise AssertionError(name)
    def command(args,timeout=8,allowed=(0,),**options):
        r=subprocess.run(args,capture_output=True,text=True,timeout=timeout,**options)
        if r.returncode not in allowed:raise RuntimeError('tool unavailable: '+args[0])
        return r
    daemon=command(docker+['info','--format','{{.ID}}']).stdout.strip()
    lease_directory = leases / hashlib.sha256((daemon+'\n'+volume).encode()).hexdigest()
    def port():
        with socket.socket() as s:s.bind(('127.0.0.1',0));return s.getsockname()[1]
    def launch(scenario,expected=None, project=None, label=None):
        case=root/((label or scenario)+'-'+str(len(cases)));case.mkdir();private=case/'private';private.mkdir()
        for name,value in zip(['admin','client','user','broker'],values):
            if not neutral or name != 'client': (private/name).write_text(value)
        inputs={'LeaseRoot':str(leases),'VolumeName':volume,'KeycloakPort':port(),'WebPort':port(),'Payload':str(closure/'payload'),'ExpectedOwner':expected,'Scenario':scenario}
        if project is not None:
            inputs['ProjectDirectory'] = str(project)
        if scenario == 'skoolit-tunnel':
            theme = case/'theme'
            css = theme/'login/resources/css/acceptance.css'
            css.parent.mkdir(parents=True)
            (theme/'login/theme.properties').write_text('parent=keycloak\nstyles=css/acceptance.css\n')
            css.write_text('/* issue888-theme-first */\n')
            inputs['ThemeDirectory'] = str(theme)
        (case/'input.json').write_text(json.dumps(inputs))
        base_env = {key: os.environ[key] for key in ('PATH', 'HOME', 'TMPDIR', 'USER', 'LOGNAME') if key in os.environ}
        env=dict(base_env,DOTNET_ROOT=str(dotnet.parent),DOTNET_ENVIRONMENT='Testing',NUGET_PACKAGES=str(nuget),NUGET_HTTP_CACHE_PATH=str(root/'http'),NUGET_SCRATCH=str(root/'scratch'),
            DOCKER_HOST=endpoint,ASPIRE_ALLOW_UNSECURED_TRANSPORT='true',DOTNET_GENERATE_ASPNET_CERTIFICATE='false',DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1',
            DcpPublisher__CliPath=str(closure/'dcp/dcp'),DcpPublisher__ExtensionsPath=str(closure/'dcp'))
        if a.acceptance:
            env.update(DOTNET_HOST_TRACE='1', DOTNET_HOST_TRACEFILE=str(case/'host.trace'))
        log=(case/'host.log').open('x')
        proc=subprocess.Popen([dotnet,str(closure/'host/ForgeTrust.AppSurface.Auth.Aspire.Keycloak.Tests.dll'),str(case),scenario],cwd=closure/'host',env=env,stdout=log,stderr=subprocess.STDOUT,stdin=subprocess.DEVNULL)
        children.append((case, inputs, proc, log))
        return case,inputs,proc,log
    def release(path, value):
        pending = path.with_suffix('.pending')
        pending.write_text(value)
        pending.replace(path)
    def theme_mount_matches(mount, source):
        return not mount['RW'] and mount['Source'] in (source, '/host_mnt'+source)
    def observe(case,inputs):
        record=json.loads((case/'registration.json').read_text())
        directory = lease_directory
        check('private-inputs-present-before-clean',any(Path(record['RunDirectory']).glob('private-*/*')))
        with (directory/'owner.lock').open('r+b') as contender:
            try:fcntl.flock(contender,fcntl.LOCK_EX|fcntl.LOCK_NB)
            except BlockingIOError:pass
            else:raise AssertionError('lease released before preclean')
        marker=json.loads((directory/'active.json').read_text())
        check('preclean-exact-owner',marker['OwnerId']==record['OwnerId'] and marker['DaemonId']==daemon and marker['VolumeName']==volume)
        stopped=[]
        for resource in marker['Containers']:
            names=command(docker+['ps','-aq','--no-trunc','--filter','name=^/'+resource['Name']+'$']).stdout.strip()
            if not names:
                if a.acceptance:
                    check('preclean-recorded-container-absent', resource['Id'] is not None
                          and not command(docker+['ps','-aq','--no-trunc','--filter','id='+resource['Id']]).stdout.strip())
                continue
            check('preclean-exact-id',resource['Id'] is None or resource['Id']==names)
            item=json.loads(command(docker+['inspect',names]).stdout)[0]
            check('preclean-stopped-owned',item['Config']['Labels']['appsurface.run']==record['OwnerId'] and not item['State']['Running'] and item['State']['Pid']==0 and item['State']['Status'] in ['exited','created'])
            top=command(docker+['top',names],allowed=(0,1))
            check('preclean-stopped-census',top.returncode==1 and 'is not running' in top.stderr.lower() and not top.stdout.strip())
            if a.acceptance:
                def allowed_mount(mount):
                    if mount['Type'] == 'volume':
                        return mount['Name'] == volume
                    if mount['Type'] != 'bind':
                        return False
                    if resource['Role'] == 'keycloak' and inputs.get('ThemeDirectory'):
                        if mount['Destination'] == '/opt/keycloak/themes/acceptance':
                            return theme_mount_matches(mount, inputs['ThemeDirectory'])
                    return (Path(mount['Source']).is_relative_to(Path(record['RunDirectory']))
                            or mount['Source'] == '/var/lib/docker/volumes/'+volume+'/_data')

                check('preclean-no-checkout-mounts', all(
                    allowed_mount(mount) for mount in item['Mounts']))
                if resource['Role'] == 'keycloak':
                    check('pinned-keycloak-image', item['Config']['Image'] in (KEYCLOAK_IMAGE, KEYCLOAK_IMAGE_TAGGED))
                    if historical_uid and bootstrap:
                        server_mount, = [m for m in item['Mounts'] if m['Destination'] == '/proof/server']
                        check('recorded-historical-server-user-and-private-mount',
                              resource.get('HistoricalServerUser') and item['Config']['User'] == '1000:0'
                              and not server_mount['RW']
                              and Path(server_mount['Source']) == Path(record['RunDirectory'])/'private-server')
                elif historical_uid:
                    check('server-inputs-not-mounted-in-other-workloads', all(
                        not m['Source'].endswith('/private-server') for m in item['Mounts']))
                if resource['Role'] == 'anchor':
                    check('owner-fence-established', marker['OwnerFenceEstablished'] is True)
                    check('anchor-private-mount-only-for-confidential',
                          any(m['Destination'] == '/proof/private' for m in item['Mounts']) != neutral)
            stopped.append({'role':resource['Role'],'id':names,'pid':0})
        (case/'preclean.release').write_text('independently-observed-stopped')
        return {'leaseHeld':True,'activeHeld':True,'stopped':stopped, 'volumeIdentity':marker['Volume']}
    def finish(case,inputs,proc,log,expect):
        limit=time.monotonic()+150;observation=None
        while proc.poll() is None:
            if (case/'preclean.ready').exists() and observation is None:observation=observe(case,inputs)
            if time.monotonic()>limit:raise TimeoutError('owned host did not terminate')
            time.sleep(.05)
        log.close()
        check('host-exit:'+case.name,proc.returncode==expect)
        if (case/'stop.request').exists():check('external-stop-under30:'+case.name,time.time()-(case/'stop.request').stat().st_mtime<30)
        text=(case/'host.log').read_text();safe=text
        for v in values:safe=safe.replace(v,'[REDACTED]')
        (a.evidence/(case.name+'.log')).write_text(safe);check('secret-free-host-log',safe==text)
        result=json.loads((case/'host-result.json').read_text()) if (case/'host-result.json').exists() else None
        if a.acceptance:
            trace = (case/'host.trace').read_text()
            check('host-probes-copied-closure', str(closure/'host') in trace)
            check('host-no-checkout-or-shared-cache-probes',
                  not any(hint in trace for hint in ('/.codex/worktrees/', '/.nuget/packages/', '/.config/developer-caches/')))
        if result:
            check('stop-under30:'+case.name,result['StopSeconds']<30)
            registration=json.loads((case/'registration.json').read_text())
            data=Path(registration['RunDirectory'])
            if result['ActiveRetained'] and data.exists():check('uncertain-inputs-retained',any(data.glob('private-*/*')))
            elif data.exists():check('private-inputs-cleaned',not any(data.glob('private-*/*')))
        entry={'scenario':case.name,'exit':proc.returncode,'result':result,'preclean':observation}
        cases.append(entry);return entry

    def assert_acceptance_declaration(baseline, inputs, scenario):
        client = baseline['client']
        ports = [inputs['WebPort']] if neutral else [inputs['WebPort'], 5000, 5055]
        origins = {f'http://{host}:{port}' for host in ('localhost', '127.0.0.1') for port in ports}
        if not neutral: origins.add('https://app-dev.getskoolit.com')
        check('fixture-mode-and-ids', baseline['realm']['realm'] == realm and client['clientId'] == client_id
              and client['publicClient'] == neutral)
        if scenario == 'skoolit-tunnel':
            check('theme-selected-by-real-keycloak', baseline['realm']['loginTheme'] == 'acceptance')
        check('exact-callback-origin-logout', set(client['redirectUris']) == {origin + callback for origin in origins}
              and set(client['logout'].split('##')) == {origin + signed_out_callback for origin in origins}
              and set(client['webOrigins']) == origins)
        check('no-alternate-client-grants', client['standardFlowEnabled'] is True
              and not any(client[key] for key in ('implicitFlowEnabled', 'directAccessGrantsEnabled', 'serviceAccountsEnabled')))
        if not neutral:
            subject = baseline['mappers']['skoolit app user id']
            provider = baseline['mappers']['skoolit identity provider alias']
            check('skoolit-exact-consumer-mappers',
                  subject['protocolMapper'] == 'oidc-usermodel-attribute-mapper'
                  and subject['config']['user.attribute'] == 'skoolit_app_user_id'
                  and subject['config']['claim.name'] == 'appsurface.subject_id'
                  and provider['protocolMapper'] == 'oidc-usersessionmodel-note-mapper'
                  and provider['config']['user.session.note'] == provider['config']['claim.name'] == 'identity_provider')
        brokers = baseline['brokers']
        if scenario == 'skoolit-google':
            google = brokers['google']
            check('google-configured-local-readback', google['providerId'] == 'google' and google['enabled']
                  and google['config']['clientId'] == 'synthetic-proof-client' and len(brokers) == 1)
        else:
            check('google-absent-or-explicitly-removed', not brokers)

    def read_state(inputs, data, *, seed=False):
        return readback.observe(inputs['KeycloakPort'], data/'public/server.crt', values[0],
                                realm_name=realm, client_id=client_id, seed=seed,
                                legacy_users=legacy)

    def theme_container(registration, inputs):
        owner = registration['OwnerId']
        name = 'appsurface-owned-'+owner+'-keycloak'
        native = command(docker+['ps', '-q', '--no-trunc', '--filter', 'name=^/'+name+'$']).stdout.strip()
        item, = json.loads(command(docker+['inspect', native]).stdout)
        check('theme-server-identity', item['Id'] == native and item['Config']['Labels']['appsurface.run'] == owner
              and item['State']['Running'] and item['Config']['Image'] in (KEYCLOAK_IMAGE, KEYCLOAK_IMAGE_TAGGED))
        mount, = [mount for mount in item['Mounts'] if mount['Destination'] == '/opt/keycloak/themes/acceptance']
        check('theme-directory-mounted-read-only', theme_mount_matches(mount, inputs['ThemeDirectory']))
        return {'id': native, 'startedAt': item['State']['StartedAt']}

    def discover_legacy(project, label, *, keep_import):
        case, inputs, proc, log = launch('legacy-name', project=project, label=label)
        proc.wait(timeout=15)
        log.close()
        check('historical-default-generated-volume', proc.returncode == 0 and not (case/'registration.json').exists())
        result = json.loads((case/'legacy-name.json').read_text())
        realm_file = Path(result['importPath'])
        check('historical-import-outside-checkout', realm_file.resolve().is_relative_to(case))
        if not keep_import:
            realm_file.unlink()
        return result['volume'], realm_file

    def absent(names):
        existing = set(command(docker+['volume', 'ls', '--format', '{{.Name}}']).stdout.splitlines())
        return existing.isdisjoint(names)
    try:
        first_subject = first_identity = first_volume = None
        scenarios = ACCEPTANCE_CASES[a.acceptance] if a.acceptance else [a.post_seed] if a.post_seed else ['success'] if a.pilot else ['success','restart','public-secret','seed-failure','uncertain-stop']
        if legacy:
            legacy_project = root/'original'
            moved_project = root/'moved'
            legacy_project.mkdir()
            moved_project.mkdir()
            volume, historical_import = discover_legacy(legacy_project, 'legacy-original-name', keep_import=True)
            moved_volume, _ = discover_legacy(moved_project, 'legacy-moved-name', keep_import=False)
            check('aspire-name-changes-on-move', volume != moved_volume)
            check('exact-proof-volume-names-absent-at-start', absent({volume, moved_volume}))
            lease_directory = leases / hashlib.sha256((daemon+'\n'+volume).encode()).hexdigest()
        for scenario in scenarios:
            launch_time = time.monotonic()
            timing = {}
            reject = scenario in PREFLIGHT_REJECTIONS
            needs_readback = a.post_seed or a.acceptance and not reject
            positive = scenario not in SEED_FAILURES | PREFLIGHT_REJECTIONS | MISSING_LEGACY | {'public-secret'} and not scenario.startswith('drift-')
            marker_before = (lease_directory/'active.json').exists()
            project = moved_project if scenario == 'legacy-moved' else legacy_project
            case,inputs,proc,log=launch(scenario, project=project)
            if scenario in MISSING_LEGACY:
                result = finish(case, inputs, proc, log, 1)
                registration = json.loads((case/'registration.json').read_text())
                missing = volume if scenario == 'legacy-missing' else moved_volume
                missing_lease = leases / hashlib.sha256((daemon+'\n'+missing).encode()).hexdigest()
                check('missing-generated-name-does-not-create-or-stage',
                      absent({missing}) and result['result']['Code'] == 'ASKEYC028'
                      and not result['result']['ActiveRetained'] and result['preclean'] is None
                      and not missing_lease.exists() and not Path(registration['RunDirectory']).exists()
                      and not list(case.glob('lifecycle-*')))
                if scenario == 'legacy-missing':
                    check('missing-volume-leaves-no-lease-artifacts', not list(leases.iterdir()))
                    module = importlib.util.module_from_spec(_legacy_spec)
                    _legacy_spec.loader.exec_module(module)
                    bootstrap = module.prime(docker, root, closure, volume, uuid.uuid4().hex, historical_import,
                                             port(), values[0], lambda p, cert, password: readback.observe(
                                                 p, cert, password, realm_name=realm, client_id=client_id, legacy_users=True),
                                             historical_uid=historical_uid)
                    check('legacy-bootstrap-stopped-before-clean', bootstrap['activeCleared']
                          and bootstrap['stopSeconds'] < 30)
                    (a.evidence/'legacy-bootstrap.json').write_text(json.dumps(bootstrap, indent=2)+'\n')
                continue
            if reject:
                result = finish(case, inputs, proc, log, 44)
                expected_code = 'ASKEYC001' if scenario == 'skoolit-unapproved-issuer' else 'ASKEYC027'
                check('pregraph-binding-or-issuer-rejection',
                      json.loads((case/'registration-rejection.json').read_text())['Code'] == expected_code
                      and not (case/'registration.json').exists() and not list(case.glob('lifecycle-*'))
                      and not marker_before and not (lease_directory/'active.json').exists())
                check('pregraph-no-preclean-needed', result['result'] is None and result['preclean'] is None)
                continue
            deadline=time.monotonic()+160
            web=False;registration=None
            baseline = after_seed = rejection_started = None
            worker_seconds = None
            drift = scenario.startswith('drift-')
            while proc.poll() is None:
                if a.acceptance and 'keycloakStarted' not in timing and (case/'lifecycle-keycloak-started.json').exists():
                    timing['keycloakStarted'] = time.monotonic() - launch_time
                if (case/'registration.json').exists() and registration is None:
                    registration=json.loads((case/'registration.json').read_text());check('registration-effect-free',registration['staged'] is False)
                if (case/'preclean.ready').exists():break
                if needs_readback and registration:
                    data = Path(registration['RunDirectory'])
                    if baseline is None and (case/'lifecycle-worker-completed.json').exists():
                        timing['workerCompleted'] = time.monotonic() - launch_time
                        baseline = read_state(inputs, data)
                        envelope = json.loads((data/'state/worker-result.json').read_text())
                        worker = envelope['result']
                        check('worker-ready-before-seed', worker['Ready'] and not (case/'lifecycle-required-seed-intent.json').exists())
                        check('effective-identities', baseline['realm']['id'] == worker['RealmId'] and baseline['client']['id'] == worker['ClientInternalId'])
                        if a.acceptance:
                            assert_acceptance_declaration(baseline, inputs, scenario)
                            worker_seconds = envelope['elapsedSeconds']
                            timing['reconciliation'] = {
                                key:worker[key] for key in ('Writes', 'Readbacks', 'Retries', 'UnknownWrites')
                            }
                            check('worker-reconcile-under30', 0 < worker_seconds < 30)
                            cases_identity = (baseline['realm']['id'], baseline['client']['id'])
                            if first_identity is None: first_identity = cases_identity
                            else: check('same-volume-realm-client-identities', first_identity == cases_identity)
                            check('extracted-worker-only', all(item['location'].startswith(('/proof/code/', '/usr/share/dotnet/'))
                                  for item in envelope['assemblies']) and not any('Aspire.Hosting' in item for item in envelope['workerReferences']))
                            check('public-client-reference-absent' if neutral else 'confidential-client-reference-present',
                                  (data/'state/client-reference.json').is_file() != neutral
                                  and (data/'private-anchor/input.json').is_file() != neutral)
                            if bootstrap:
                                old = bootstrap['observedBeforeMigration']
                                check('legacy-imported-realm-client-and-scaffold-users-preserved', cases_identity ==
                                      (old['realm']['id'], old['client']['id']) and baseline['legacyUsers'] == old['legacyUsers'])
                                if historical_uid:
                                    name = 'appsurface-owned-'+registration['OwnerId']+'-keycloak'
                                    server = command(docker+['ps','-q','--no-trunc','--filter','name=^/'+name+'$']).stdout.strip()
                                    check('exact-live-migrated-keycloak', len(server) == 64 and
                                          command(docker+['inspect','--format','{{index .Config.Labels "appsurface.run"}}',server]).stdout.strip()
                                          == registration['OwnerId'])
                                    actual = module.observe_data_ownership(
                                        lambda *args: command(docker+list(args)).stdout.strip(), server)
                                    check('uid1000-volume-and-database-identity-preserved', actual == bootstrap['ownership'])
                                    check('private-host-run-ancestor', data.stat().st_mode & 0o077 == 0)
                                    timing['preservedOwnership'] = actual
                            if scenario == 'skoolit-tunnel':
                                admin = json.loads((data/'private-worker/bindings.json').read_text())['BaseAddress']
                                declaration = json.loads((data/'public/desired.json').read_text())
                                check('public-issuer-with-owned-loopback-admin-transport',
                                      declaration['Authority'] == 'https://auth-dev.getskoolit.com/realms/skoolit'
                                      and admin == f"https://localhost:{inputs['KeycloakPort']}/")
                        else: check('baseline-config', baseline['client']['publicClient'] is False
                              and baseline['client']['redirectUris'] == ['http://localhost:' + str(inputs['WebPort']) + '/signin-oidc']
                              and baseline['mappers']['managed-role']['config']['claim.value'] == 'viewer'
                              and baseline['brokers']['google']['enabled'] is True)
                        release(case/'worker-completed.release', 'unchanged')
                    if after_seed is None and (case/'lifecycle-seeds-completed.json').exists():
                        check('web-withheld-before-readback', not (case/'lifecycle-real-web-intent.json').exists())
                        seed = json.loads((data/'state/seed-result.json').read_text())
                        check('real-seed-completed', seed['code'] == ('FixtureDriftWritten' if drift else 'Seeded'))
                        timing['seedCompleted'] = time.monotonic() - launch_time
                        after_seed = read_state(inputs, data, seed=bool(a.acceptance))
                        seed_projection = after_seed.pop('seed') if a.acceptance else None
                        if seed_projection is not None:
                            check('independent-seeded-subject-readback', seed_projection['id'] == seed['expectedSubject']
                                  and seed_projection['username'] == 'seeded-user' and seed_projection['enabled'])
                            if not neutral:
                                check('independent-skoolit-subject-schema-readback',
                                      seed_projection['applicationAttribute'] == [seed['expectedAppSubject']]
                                      and seed_projection['applicationAttributePermissions'] == {'view': ['admin'], 'edit': ['admin']})
                        expected = readback.expected_after(baseline, scenario)
                        check('single-declared-change' if drift else 'managed-state-unchanged',
                              after_seed == expected and (after_seed != baseline) == drift)
                        projection = json.dumps({'before': baseline, 'after': after_seed, 'seed': seed_projection,
                                                 'seedCode': seed['code']}, indent=2)
                        check('secret-free-readback', not any(value in projection for value in values))
                        name = scenario+'-managed-readback.json' if a.acceptance else 'managed-readback.json'
                        (a.evidence/name).write_text(projection + '\n')
                        if drift:
                            rejection_started = time.monotonic()
                        release(case/'seeds-completed.release', 'drift' if drift else 'unchanged')
                if positive and registration:
                    try:
                        with urllib.request.urlopen('http://localhost:'+str(inputs['WebPort'])+'/state',timeout=1) as response:state=json.load(response)
                        if state['authenticated'] is False:web=True;break
                    except Exception:pass
                if time.monotonic()>deadline:raise TimeoutError('normal supervised startup')
                time.sleep(.1)
            if positive:
                if not web:
                    finish(case,inputs,proc,log,1)
                check('real-web-started:'+scenario,web)
                timing['webReady'] = time.monotonic() - launch_time
                data=Path(registration['RunDirectory'])
                seeded=json.loads((data/'state/seed-result.json').read_text());subject=seeded['expectedSubject']
                if first_subject is None:first_subject=subject
                else:check('persistent-subject-preserved',subject==first_subject)
                public=json.loads((data/'public/config.json').read_text());public['CertificatePath']=str(data/'public/server.crt');public['ExpectedSubject']=subject
                if scenario == 'skoolit-tunnel':
                    theme_before = theme_container(registration, inputs)
                    css = Path(inputs['ThemeDirectory'])/'login/resources/css/acceptance.css'
                    public['ThemeCssPath'] = str(css)
                (case/'browser.json').write_text(json.dumps(public));(case/'browser-private.json').write_text(json.dumps({'Username':'seeded-user','UserPassword':values[2]}))
                out=case/'browser';out.mkdir()
                timing['browserStarted'] = time.monotonic() - launch_time
                browser=command(['node',str(closure/'browser/supervised-browser.mjs'),str(case/'browser.json'),str(case/'browser-private.json'),str(closure/'browser/engine/chrome-headless-shell-mac-arm64/chrome-headless-shell'),str(out)],timeout=75,
                                cwd=closure/'browser', allowed=(0,1))
                shutil.copytree(out,a.evidence/(case.name+'-browser'))
                br=json.loads((out/'receipt.json').read_text());check('real-oidc-browser:'+scenario,br['status']=='passed' and br['resourcesClosed'])
                timing['loginLogoutVerified'] = time.monotonic() - launch_time
                if a.acceptance == 'cold':
                    timing['loginVerified'] = timing['browserStarted'] + br['loginVerifiedSeconds']
                    check('verified-first-login-timing',
                          0 < timing['workerCompleted'] <= timing['seedCompleted'] <= timing['webReady']
                          <= timing['browserStarted'] < timing['loginVerified'] < timing['loginLogoutVerified'])
                    timing['firstLoginVerifiedUnix'] = driver_started_at + launch_time - driver_started + timing['loginVerified']
                if scenario == 'skoolit-tunnel':
                    theme_after = theme_container(registration, inputs)
                    check('theme-changed-without-keycloak-restart', theme_before == theme_after
                          and css.read_text().strip() == '/* issue888-theme-second */')
                    check('two-exact-public-origins-routed-to-loopback',
                          {route['origin'] for route in br['localRoutes'] if route['transport'] == 'CONNECT'}
                          == {'https://app-dev.getskoolit.com', 'https://auth-dev.getskoolit.com'}
                          and all(route['localAddress'] == '127.0.0.1' for route in br['localRoutes']))
                    (a.evidence/'theme-container.json').write_text(json.dumps(
                        {'before':theme_before,'after':theme_after,'readOnly':True}, indent=2)+'\n')
                if a.acceptance:
                    check('browser-exited-successfully', browser.returncode == 0)
                    check('copied-browser-processes-absent', not any(str(closure/'browser') in line for line in
                          command(['/bin/ps','-ax','-o','command=']).stdout.splitlines()))
                if scenario in ('legacy-repeat', 'legacy-explicit'):
                    opposite = 'legacy-explicit' if scenario == 'legacy-repeat' else 'legacy-repeat'
                    active = lease_directory/'active.json'
                    marker = active.read_bytes()
                    contender, contender_inputs, contender_proc, contender_log = launch(
                        opposite, project=legacy_project, label=opposite+'-contender')
                    blocked = finish(contender, contender_inputs, contender_proc, contender_log, 1)
                    contending_run = json.loads((contender/'registration.json').read_text())['RunDirectory']
                    check('legacy-and-explicit-share-the-owner-lease',
                          not Path(contending_run).exists() and not list(contender.glob('lifecycle-*'))
                          and not blocked['result']['ActiveRetained'] and blocked['preclean'] is None
                          and active.read_bytes() == marker)
                # Same physical volume, separate AppHost. It must fail without reaching bootstrap or web effects.
                if scenario=='success' and not a.pilot:
                    other,oi,op,ol=launch('contention');finish(other,oi,op,ol,1)
                    check('contender-did-not-stage',not Path(json.loads((other/'registration.json').read_text())['RunDirectory']).exists())
                    live,li,lp,ll=launch('recover-live',registration['OwnerId']);finish(live,li,lp,ll,33)
                    check('live-recovery-did-not-build-graph',not (live/'registration.json').exists())
                (case/'stop.request').write_text('stop')
            result=finish(case,inputs,proc,log,0 if positive and scenario!='uncertain-stop' else 1)
            if a.acceptance:
                result['reconciliationSeconds'] = worker_seconds
                phase_times[scenario] = timing
                check('owned-stop-verified-before-clean', result['preclean'] is not None and not result['result']['ActiveRetained']
                      and not (lease_directory/'active.json').exists())
                volume_identity = result['preclean']['volumeIdentity']
                if first_volume is None: first_volume = volume_identity
                check('same-physical-volume-incarnation', volume_identity is not None and first_volume == volume_identity)
                if historical_uid:
                    check('same-as-primed-volume', volume_identity['Name'] == bootstrap['volume']
                          and volume_identity['CreatedAt'] == bootstrap['createdAt']
                          and volume_identity['Root']['Inode'] == bootstrap['ownership']['root']['inode'])
                if not positive: check('required-seed-failure-withholds-web', baseline is not None and after_seed is None
                                       and not (case/'lifecycle-real-web-intent.json').exists())
            if drift:
                check('post-seed-rejection-latched', rejection_started is not None and after_seed is not None
                      and result['result']['Code'] == 'ASKEYC031' and result['preclean'] is not None)
                check('drift-rejection-under30', time.monotonic() - rejection_started < 30)
                check('web-never-launched', not (case/'lifecycle-real-web-intent.json').exists())
            if a.post_seed:
                check('post-seed-observation-complete', baseline is not None and after_seed is not None)
            if scenario=='uncertain-stop':
                check('uncertain-stop-retains-active',result['result']['ActiveRetained'])
                owner=registration['OwnerId']
                other,oi,op,ol=launch('blocked-restart');finish(other,oi,op,ol,1)
                check('blocked-restart-did-not-stage',not Path(json.loads((other/'registration.json').read_text())['RunDirectory']).exists())
                for recovery,expected in [('recover-wrong',uuid.uuid4().hex),('recover-correct',owner),('recover-stale',owner)]:
                    rc,ri,rp,rl=launch(recovery,expected);finish(rc,ri,rp,rl,0 if recovery=='recover-correct' else 33)
        check('runtime-cache-empty',not any(nuget.iterdir()))
        for kind in kinds:
            assertion = 'unchanged-' + kind + '-closure'
            try:
                verify_prepared_closure(closure/kind, prepared[kind+'Manifest'])
            except (OSError, ValueError):
                checks.append({'name': assertion, 'passed': False})
                raise
            check(assertion, True)
    except Exception as error:fault=type(error).__name__+': '+str(error)[:140]
    finally:
        for case, inputs, proc, log in children:
            if proc.poll() is None:
                proc.terminate()
                # On driver failure the host still owns the deadline. Observe a stopped barrier if it
                # becomes available, then leave uncertain resources and markers intact.
                stop_started = time.monotonic()
                try:
                    while proc.poll() is None and time.monotonic() - stop_started < 30:
                        if (case/'preclean.ready').exists() and not (case/'preclean.release').exists():
                            observe(case, inputs)
                        time.sleep(.05)
                except (OSError, ValueError, AssertionError, RuntimeError, subprocess.TimeoutExpired):
                    pass
                finally:
                    log.close()
                result_path = case/'host-result.json'
                failure_shutdown.append({
                    'case': case.name, 'exit': proc.poll(), 'seconds': time.monotonic()-stop_started,
                    'result': json.loads(result_path.read_text()) if result_path.is_file() else None,
                })
    receipt={'schema':1,'passed':fault is None,'fault':fault,'cases':cases,'assertions':checks,'root':str(root),'volume':volume,'daemon':daemon,'dockerEndpoint':endpoint,'preparationSha256':hashlib.sha256(a.preparation.read_bytes()).hexdigest()}
    if a.acceptance:
        receipt.update(fixture=a.acceptance,driverDirectory=str(Path(__file__).resolve().parent),
                       preparationPath=str(a.preparation.resolve()),closurePath=str(closure),phaseTimes=phase_times)
    if bootstrap:
        receipt['legacyBootstrap'] = {key:bootstrap[key] for key in ('volume','label','createdAt','activeCleared')}
    if failure_shutdown:
        receipt['driverFailureShutdown'] = failure_shutdown
    (a.evidence/'receipt.json').write_text(json.dumps(receipt,indent=2)+'\n');print(json.dumps({'passed':receipt['passed'],'fault':fault,'cases':len(cases),'checks':len(checks)}))
    return 0 if fault is None else 1

if __name__=='__main__':raise SystemExit(main())
