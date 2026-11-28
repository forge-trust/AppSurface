"""Independent, read-only managed-state observation for the supervised seed fixture."""

import copy
import http.client
import json
import ssl
from urllib.parse import quote, urlencode


def observe(port, certificate, admin_password, *, realm_name='sample', client_id='sample-web', seed=False,
            legacy_users=False):
    # Use only the generated fixture leaf as trust, with hostname verification and no redirects or proxy.
    context = ssl.SSLContext(ssl.PROTOCOL_TLS_CLIENT)
    context.load_verify_locations(cafile=str(certificate))
    connection = http.client.HTTPSConnection('localhost', port, context=context, timeout=3)

    def request(path, *, form=None, token=None):
        headers = {}
        if form is not None:
            headers['Content-Type'] = 'application/x-www-form-urlencoded'
        if token is not None:
            headers['Authorization'] = 'Bearer ' + token
        connection.request('POST' if form is not None else 'GET', path, form, headers)
        with connection.getresponse() as response:
            if response.status != 200:
                raise RuntimeError('fixture Admin API observation failed')
            body = response.read(1_048_577)
            if len(body) > 1_048_576:
                raise RuntimeError('fixture Admin API response exceeds observation limit')
            return json.loads(body)

    try:
        token = request('/realms/master/protocol/openid-connect/token', form=urlencode({
            'grant_type': 'password', 'client_id': 'admin-cli',
            'username': 'proof-admin', 'password': admin_password,
        }))['access_token']
        root = '/admin/realms/' + quote(realm_name, safe='')

        def read(path):
            return request(root + path, token=token)

        realm = read('')
        client, = read('/clients?' + urlencode({'clientId': client_id}))
        client_path = '/clients/' + quote(client['id'], safe='')
        mappers = read(client_path + '/protocol-mappers/models')
        brokers = read('/identity-provider/instances')

        def fields(value, keys):
            return {key: value.get(key) for key in keys.split()}

        client_fields = fields(client, 'id clientId publicClient enabled protocol clientAuthenticatorType '
                                       'standardFlowEnabled implicitFlowEnabled directAccessGrantsEnabled serviceAccountsEnabled')
        client_fields.update(redirectUris=sorted(client['redirectUris']), webOrigins=sorted(client['webOrigins']),
                             logout=client['attributes'].get('post.logout.redirect.uris'))
        projection = {
            'realm': fields(realm, 'id realm enabled registrationAllowed sslRequired loginTheme'),
            'client': client_fields,
            'mappers': {mapper['name']: {
                **fields(mapper, 'id name protocol protocolMapper'),
                'config': fields(mapper['config'], 'appsurface.issue888.managed claim.name claim.value jsonType.label '
                                                  'id.token.claim access.token.claim userinfo.token.claim '
                                                  'user.attribute user.session.note'),
            } for mapper in mappers},
            'brokers': {broker['alias']: {
                **fields(broker, 'alias providerId enabled trustEmail storeToken linkOnly'),
                'config': fields(broker['config'], 'appsurface.issue888.managed clientId defaultScope syncMode'),
            } for broker in brokers},
        }
        if legacy_users:
            projection['legacyUsers'] = {}
            for name in ('admin', 'viewer'):
                user, = read('/users?' + urlencode({'exact': 'true', 'username': name}))
                projection['legacyUsers'][name] = fields(user, 'id username enabled')
        if seed:
            user, = read('/users?exact=true&username=seeded-user')
            projection['seed'] = fields(user, 'id username enabled')
            if realm_name == 'skoolit':
                attribute, = [attribute for attribute in read('/users/profile')['attributes']
                              if attribute['name'] == 'skoolit_app_user_id']
                projection['seed']['applicationAttribute'] = user['attributes']['skoolit_app_user_id']
                projection['seed']['applicationAttributePermissions'] = attribute['permissions']
        return projection
    finally:
        connection.close()


def expected_after(before, scenario):
    result = copy.deepcopy(before)
    if scenario == 'drift-client':
        result['client']['redirectUris'] = ['http://127.0.0.1:19999/adversarial-callback']
    elif scenario == 'drift-mapper':
        result['mappers']['managed-role']['config']['claim.value'] = 'adversarial'
    elif scenario == 'drift-broker':
        result['brokers']['google']['enabled'] = False
    return result
