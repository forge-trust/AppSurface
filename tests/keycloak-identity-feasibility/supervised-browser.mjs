#!/usr/bin/env node
// Supervised real-Chromium OIDC proof. Credentials and protocol secrets are never retained.
import fs from 'node:fs';
import path from 'node:path';
import http from 'node:http';
import https from 'node:https';
import net from 'node:net';
import { execFileSync } from 'node:child_process';
import { createHash, X509Certificate } from 'node:crypto';
import { createRequire } from 'node:module';
import { fileURLToPath } from 'node:url';
import { setTimeout as delay } from 'node:timers/promises';

process.umask(0o077);

const startedAt = performance.now();
const args = process.argv.slice(2);
const assertions = [];
const safeNavigation = [];
const localRoutes = [];
const browserProcesses = [];
const sockets = new Set();
const contexts = [];
const servers = [];
const clients = [];
const abort = new AbortController();
const FLOW_BUDGET_MS = 48_000;
const CLOSE_BUDGET_MS = 9_000;
let stage = 'input-validation';
let errorType = null;
let resourcesClosed = false;
let deniedRequests = 0;
let pageErrors = 0;
let consoleErrors = 0;
const safeConsoleErrors = [];
let expectedCallbackRejection = false;
let proxy = null;
let appTls = null;
let proxyPort = null;
let proxyClosed = false;
let browserVersion = null;
let playwrightVersion = null;
let loginVerifiedSeconds = null;
let publicConfig = null;
let privateInput = null;
let username = null;
let userPassword = null;
let settings = null;
let chromium = null;
let flowTimedOut = false;
let signalCancelled = false;

class HarnessFailure extends Error {
    constructor(code) { super(code); this.code = code; }
}

function fail(code = 'OperationFailed') { throw new HarnessFailure(code); }

function check(name, value) {
    const passed = Boolean(value);
    assertions.push({ name, passed });
    if (!passed) fail('AssertionFailed');
}

function object(value) {
    return value !== null && typeof value === 'object' && !Array.isArray(value);
}

function getCaseInsensitive(source, key) {
    if (!object(source)) fail('InvalidInput');
    const matches = Object.keys(source).filter(name => name.toLowerCase() === key.toLowerCase());
    if (matches.length !== 1) fail('InvalidInput');
    return source[matches[0]];
}

function requiredString(source, key) {
    const value = getCaseInsensitive(source, key);
    if (typeof value !== 'string' || value.length === 0) fail('InvalidInput');
    return value;
}

function optionalString(source, key) {
    if (!object(source)) fail('InvalidInput');
    const matches = Object.keys(source).filter(name => name.toLowerCase() === key.toLowerCase());
    if (matches.length === 0) return null;
    if (matches.length !== 1) fail('InvalidInput');
    const value = source[matches[0]];
    if (value !== null && typeof value !== 'string') fail('InvalidInput');
    return value;
}

function requiredLocalUrl(value, protocol, baseOnly) {
    let url;
    try { url = new URL(value); } catch { fail('InvalidInput'); }
    if (url.protocol !== protocol || url.hostname !== 'localhost' || url.username || url.password
        || url.search || url.hash || !url.port || (baseOnly && url.pathname !== '/')
        || (!baseOnly && !/^\/realms\/[^/]+\/?$/.test(url.pathname))) fail('InvalidInput');
    const port = Number(url.port);
    if (!Number.isInteger(port) || port < 1 || port > 65535) fail('InvalidInput');
    return { url, origin: url.origin, host: url.host, port };
}

function requiredAuthority(value, port) {
    let url;
    try { url = new URL(value); } catch { fail('InvalidInput'); }
    if (url.protocol !== 'https:' || !['localhost', '127.0.0.1', 'auth-dev.getskoolit.com'].includes(url.hostname)
        || url.username || url.password || url.search || url.hash
        || !/^\/realms\/[^/]+$/.test(url.pathname)) fail('InvalidInput');
    if (!Number.isInteger(port) || port < 1 || port > 65535
        || (url.hostname === 'localhost' || url.hostname === '127.0.0.1'
            ? Number(url.port) !== port : url.port !== '')) fail('InvalidInput');
    const realm = url.pathname.slice('/realms/'.length);
    if (url.hostname === 'auth-dev.getskoolit.com' && !['appsurface-dev', 'skoolit'].includes(realm)) fail('InvalidInput');
    return { url, origin: url.origin, host: url.host, port };
}

function requiredPath(value) {
    if (typeof value !== 'string' || !value.startsWith('/') || value.startsWith('//')) fail('InvalidInput');
    let url;
    try { url = new URL(value, 'http://localhost'); } catch { fail('InvalidInput'); }
    if (url.origin !== 'http://localhost' || url.pathname !== value || url.search || url.hash) fail('InvalidInput');
    return value;
}

function validateThemeCssPath(value) {
    if (value === null) return null;
    if (!path.isAbsolute(value) || path.resolve(value) !== value
        || path.relative(path.resolve(path.dirname(value), '../../..'), value) !== path.join('login', 'resources', 'css', 'acceptance.css'))
        fail('InvalidInput');

    const themeRoot = path.resolve(path.dirname(value), '../../..');
    const rootStats = fs.lstatSync(themeRoot);
    const fileStats = fs.lstatSync(value);
    if (rootStats.isSymbolicLink() || !rootStats.isDirectory() || (rootStats.mode & 0o077) !== 0
        || fileStats.isSymbolicLink() || !fileStats.isFile()) fail('InvalidInput');
    let current = themeRoot;
    for (const part of ['login', 'resources', 'css']) {
        current = path.join(current, part);
        const stats = fs.lstatSync(current);
        if (stats.isSymbolicLink() || !stats.isDirectory()) fail('InvalidInput');
    }
    fs.accessSync(value, fs.constants.R_OK | fs.constants.W_OK);
    return value;
}

function loadPlaywrightClosure() {
    const require = createRequire(import.meta.url);
    const scriptDirectory = path.dirname(fileURLToPath(import.meta.url));
    const modulesDirectory = path.join(scriptDirectory, 'node_modules') + path.sep;
    const entry = fs.realpathSync(require.resolve('playwright'));
    if (!entry.startsWith(modulesDirectory)) fail('PlaywrightClosureUnavailable');
    const packagePath = require.resolve('playwright/package.json');
    const packageInfo = JSON.parse(fs.readFileSync(packagePath, 'utf8'));
    const module = require('playwright');
    if (!module.chromium || typeof packageInfo.version !== 'string') fail('PlaywrightClosureUnavailable');
    playwrightVersion = packageInfo.version;
    return module.chromium;
}

function track(socket) {
    sockets.add(socket);
    socket.on('error', () => socket.destroy());
    socket.once('close', () => sockets.delete(socket));
    return socket;
}

function withinBudget(operation, deadline) {
    const remaining = deadline - performance.now();
    if (remaining <= 0) return Promise.resolve(false);
    return Promise.race([
        Promise.resolve(operation).then(() => true, () => false),
        delay(remaining).then(() => false)
    ]);
}

function withAbort(operation) {
    const pending = Promise.resolve(operation);
    pending.catch(() => {});
    if (abort.signal.aborted) return Promise.reject(new HarnessFailure(signalCancelled ? 'Cancelled' : 'FlowDeadline'));
    return new Promise((resolve, reject) => {
        const cancelled = () => reject(new HarnessFailure(signalCancelled ? 'Cancelled' : 'FlowDeadline'));
        abort.signal.addEventListener('abort', cancelled, { once: true });
        pending.then(resolve, reject).finally(() => abort.signal.removeEventListener('abort', cancelled));
    });
}

function readInputs() {
    if (args.length !== 4) fail('InvalidArguments');
    const [publicPath, privatePath, enginePath, evidencePath] = args;
    const publicText = fs.readFileSync(publicPath, 'utf8');
    publicConfig = JSON.parse(publicText);
    const privateText = fs.readFileSync(privatePath, 'utf8');
    privateInput = JSON.parse(privateText);

    const privateKeys = Object.keys(privateInput ?? {});
    if (privateKeys.length !== 2) fail('InvalidInput');
    username = requiredString(privateInput, 'Username');
    userPassword = requiredString(privateInput, 'UserPassword');
    privateInput = null;

    let web = requiredLocalUrl(requiredString(publicConfig, 'WebBaseAddress'), 'http:', true);
    const authority = requiredAuthority(requiredString(publicConfig, 'Authority'), publicConfig.KeycloakPort ?? Number(new URL(publicConfig.Authority).port));
    const publicWeb = optionalString(publicConfig, 'PublicWebOrigin');
    const themeCssPath = validateThemeCssPath(optionalString(publicConfig, 'ThemeCssPath'));
    if (publicWeb !== null) {
        if (publicWeb !== 'https://app-dev.getskoolit.com'
            || authority.url.hostname !== 'auth-dev.getskoolit.com') fail('InvalidInput');
        const url = new URL(publicWeb);
        web = { url, origin: url.origin, host: url.host, port: web.port };
    }
    const callbackPath = requiredPath(requiredString(publicConfig, 'CallbackPath'));
    const signedOutCallbackPath = requiredPath(requiredString(publicConfig, 'SignedOutCallbackPath'));
    const clientId = requiredString(publicConfig, 'ClientId');
    const realm = authority.url.pathname.split('/').filter(Boolean).pop();
    let fixture;
    if (realm === 'sample' && clientId === 'sample-web') fixture = 'sample';
    else if (realm === 'appsurface-dev' && clientId === 'appsurface-web') fixture = 'appsurface';
    else if (realm === 'skoolit' && clientId === 'skoolit-web') fixture = 'skoolit';
    else fail('InvalidInput');
    const publicAuthority = authority.url.hostname === 'auth-dev.getskoolit.com';
    if (publicAuthority !== (publicWeb !== null)
        || publicAuthority && !['appsurface', 'skoolit'].includes(fixture)
        || !publicAuthority && !['localhost', '127.0.0.1'].includes(authority.url.hostname)) fail('InvalidInput');
    const configuredClientMode = optionalString(publicConfig, 'ClientMode');
    const clientMode = configuredClientMode ?? (fixture === 'sample' ? 'Confidential' : null);
    const expectedClientMode = fixture === 'appsurface' ? 'Public' : 'Confidential';
    const expectedCallbackPath = fixture === 'appsurface' ? '/signin-appsurface-oidc' : '/signin-oidc';
    const expectedSignedOutCallbackPath = fixture === 'appsurface'
        ? '/signout-callback-appsurface-oidc'
        : '/signout-callback-oidc';
    if (clientMode !== expectedClientMode || callbackPath !== expectedCallbackPath
        || signedOutCallbackPath !== expectedSignedOutCallbackPath) fail('InvalidInput');
    const expectedSubject = requiredString(publicConfig, 'ExpectedSubject');
    const certificatePath = requiredString(publicConfig, 'CertificatePath');
    const resolvedEnginePath = path.resolve(enginePath);
    if (!fs.statSync(resolvedEnginePath).isFile()) fail('InvalidInput');
    const certificate = new X509Certificate(fs.readFileSync(certificatePath));
    const spki = createHash('sha256')
        .update(certificate.publicKey.export({ type: 'spki', format: 'der' }))
        .digest('base64');

    const authorityPath = authority.url.pathname.replace(/\/$/, '');
    settings = {
        web,
        authority,
        callbackPath,
        signedOutCallbackPath,
        callbackUrl: web.origin + callbackPath,
        signedOutCallbackUrl: web.origin + signedOutCallbackPath,
        fixture,
        clientMode,
        clientId,
        expectedSubject,
        authPath: authorityPath + '/protocol/openid-connect/auth',
        logoutPath: authorityPath + '/protocol/openid-connect/logout',
        enginePath: resolvedEnginePath,
        spki: [spki],
        themeCssPath,
        evidencePath: path.resolve(evidencePath)
    };
    fs.mkdirSync(settings.evidencePath, { recursive: true, mode: 0o700 });
    const evidenceStats = fs.lstatSync(settings.evidencePath);
    if (evidenceStats.isSymbolicLink() || !evidenceStats.isDirectory() || (evidenceStats.mode & 0o077) !== 0
        || fs.existsSync(path.join(settings.evidencePath, 'receipt.json'))) fail('EvidenceUnavailable');
    chromium = loadPlaywrightClosure();
}

async function makeProxy() {
    const targets = new Map([[settings.authority.origin, {
        connectPort: settings.authority.port, workloadPort: settings.authority.port
    }]]);
    if (settings.web.url.protocol === 'https:') {
        const directory = fs.mkdtempSync(path.join(settings.evidencePath, '.app-tls-'));
        const keyPath = path.join(directory, 'key.pem');
        const certPath = path.join(directory, 'cert.pem');
        let key, cert;
        try {
            execFileSync('/usr/bin/openssl', ['req', '-x509', '-newkey', 'rsa:2048', '-nodes',
                '-subj', '/CN=app-dev.getskoolit.com', '-addext', 'subjectAltName=DNS:app-dev.getskoolit.com',
                '-days', '1', '-keyout', keyPath, '-out', certPath], { stdio: 'pipe', timeout: 10000 });
            key = fs.readFileSync(keyPath);
            cert = fs.readFileSync(certPath);
        } finally {
            if (fs.existsSync(keyPath)) fs.unlinkSync(keyPath);
            if (fs.existsSync(certPath)) fs.unlinkSync(certPath);
            fs.rmdirSync(directory);
        }
        settings.spki.push(createHash('sha256').update(
            new X509Certificate(cert).publicKey.export({ type: 'spki', format: 'der' })).digest('base64'));
        appTls = https.createServer({ key, cert }, (request, response) => {
            // This fixture accepts only origin-form requests for the exact TLS application host.
            if (request.headers.host !== settings.web.host || !request.url?.startsWith('/')
                || request.url.startsWith('//') || !['GET', 'HEAD', 'POST'].includes(request.method)) {
                deniedRequests++;
                response.writeHead(403).end();
                return;
            }
            const headers = { ...request.headers };
            for (const name of Object.keys(headers)) {
                if (name.startsWith('proxy-') || name === 'forwarded' || name.startsWith('x-forwarded-')) delete headers[name];
            }
            const outgoing = http.request({ hostname: '127.0.0.1', port: settings.web.port,
                path: request.url, method: request.method, headers }, reply => {
                response.writeHead(reply.statusCode, reply.headers);
                reply.pipe(response);
            });
            outgoing.setTimeout(7000, () => outgoing.destroy());
            outgoing.on('socket', track);
            outgoing.on('error', () => response.destroy());
            request.pipe(outgoing);
        });
        appTls.on('connection', track);
        await new Promise((resolve, reject) => {
            appTls.once('error', reject);
            appTls.listen(0, '127.0.0.1', resolve);
        });
        targets.set(settings.web.origin, { connectPort: appTls.address().port, workloadPort: settings.web.port });
    }
    const allowedOrigins = new Set([settings.web.origin, settings.authority.origin]);
    proxy = http.createServer((request, response) => {
        try {
            const url = new URL(request.url);
            if (url.origin !== settings.web.origin || url.protocol !== 'http:' || url.host !== settings.web.host
                || request.headers.host !== settings.web.host || url.username || url.password
                || !['GET', 'HEAD', 'POST'].includes(request.method)) throw new Error();
            const headers = { ...request.headers };
            delete headers['proxy-authorization'];
            delete headers['proxy-connection'];
            const outgoing = http.request({ hostname: '127.0.0.1', port: settings.web.port,
                path: url.pathname + url.search, method: request.method, headers }, reply => {
                localRoutes.push({ origin: url.origin, transport: 'HTTP', localAddress: '127.0.0.1',
                    localPort: settings.web.port, workloadPort: settings.web.port });
                response.writeHead(reply.statusCode, reply.headers);
                reply.pipe(response);
            });
            outgoing.setTimeout(7000, () => outgoing.destroy());
            outgoing.on('socket', track);
            outgoing.on('error', () => response.destroy());
            request.pipe(outgoing);
        } catch {
            deniedRequests++;
            response.writeHead(403);
            response.end('Blocked');
        }
    });
    proxy.on('connection', track);
    proxy.on('clientError', (_error, socket) => socket.destroy());
    proxy.on('error', () => { deniedRequests++; });
    proxy.on('connect', (request, client, head) => {
        try {
            const url = new URL('https://' + request.url);
            const target = targets.get(url.origin);
            if (!target
                || url.username || url.password || url.pathname !== '/' || url.search || url.hash) throw new Error();
            const upstream = track(net.connect({ host: '127.0.0.1', port: target.connectPort }));
            upstream.once('connect', () => {
                localRoutes.push({ origin: url.origin, transport: 'CONNECT', localAddress: '127.0.0.1',
                    localPort: target.connectPort, workloadPort: target.workloadPort });
                client.write('HTTP/1.1 200 Connection Established\r\n\r\n');
                if (head.length) upstream.write(head);
                upstream.pipe(client);
                client.pipe(upstream);
            });
            upstream.on('error', () => client.destroy());
            client.on('error', () => upstream.destroy());
            client.once('close', () => upstream.destroy());
        } catch {
            deniedRequests++;
            client.end('HTTP/1.1 403 Forbidden\r\n\r\n');
        }
    });
    return new Promise((resolve, reject) => {
        proxy.once('error', reject);
        proxy.listen(0, '127.0.0.1', () => {
            proxyPort = proxy.address().port;
            resolve(allowedOrigins);
        });
    });
}

async function launch(role, certificateException) {
    const flags = [
        '--proxy-bypass-list=<-loopback>',
        '--host-resolver-rules=MAP * ~NOTFOUND, EXCLUDE 127.0.0.1, EXCLUDE localhost'
    ];
    if (certificateException) flags.push('--ignore-certificate-errors-spki-list=' + settings.spki.join(','));
    const server = await chromium.launchServer({
        executablePath: settings.enginePath,
        headless: true,
        timeout: 8000,
        proxy: { server: 'http://127.0.0.1:' + proxyPort },
        args: flags
    });
    servers.push({ role, server });
    const child = server.process();
    browserProcesses.push({ role, pid: child.pid ?? null, terminal: false });
    const browser = await chromium.connect(server.wsEndpoint(), { timeout: 8000 });
    clients.push(browser);
    return browser;
}

async function makeContext(browser, allowedOrigins, observe = false) {
    const context = await browser.newContext({
        ignoreHTTPSErrors: false,
        serviceWorkers: 'block',
        viewport: { width: 1024, height: 768 }
    });
    contexts.push(context);
    await context.route('**/*', async route => {
        let url;
        try { url = new URL(route.request().url()); } catch {
            deniedRequests++;
            await route.abort('blockedbyclient');
            return;
        }
        if (allowedOrigins.has(url.origin) && !url.username && !url.password) {
            await route.continue();
            return;
        }
        deniedRequests++;
        await route.abort('blockedbyclient');
    });
    if (observe) {
        context.on('request', request => {
            if (!request.isNavigationRequest()) return;
            try {
                const url = new URL(request.url());
                safeNavigation.push({ origin: url.origin, path: url.pathname,
                    hasCode: url.searchParams.has('code'), hasState: url.searchParams.has('state') });
            } catch { deniedRequests++; }
        });
        context.on('page', page => {
            page.on('pageerror', () => { pageErrors++; });
            page.on('console', message => {
                if (message.type() !== 'error') return;
                let location;
                try { location = new URL(message.location().url); } catch { }
                const expected = expectedCallbackRejection && location?.origin === settings.web.origin
                    && location.pathname === settings.callbackPath && /\b401\b/.test(message.text());
                if (!expected) {
                    consoleErrors++;
                    safeConsoleErrors.push({ origin: location?.origin ?? 'unknown', path: location?.pathname ?? 'unknown',
                        category: /\b404\b/.test(message.text()) ? 'HTTP404' : /\b401\b/.test(message.text()) ? 'HTTP401' : 'Other' });
                }
            });
        });
    }
    return context;
}

function stateFacts(value) {
    if (!object(value)) return null;
    const authenticated = value.authenticated;
    const subject = value.subject;
    const applicationSubjectMatched = value.applicationSubjectMatched;
    return { authenticated, subject, applicationSubjectMatched };
}

async function readState(page) {
    const response = await page.goto(settings.web.origin + '/state', { waitUntil: 'domcontentloaded', timeout: 7000 });
    let body;
    try { body = await response.json(); } catch { return { status: response.status(), facts: null }; }
    return { status: response.status(), facts: stateFacts(body) };
}

async function visible(locator) {
    try { return await locator.isVisible(); } catch { return false; }
}

async function keycloakChallenge(page, authorizationChecks) {
    await page.locator('#kc-form-login').waitFor({ state: 'visible', timeout: 7000 });
    const current = new URL(page.url());
    check('challenge-on-exact-authority', current.origin === settings.authority.origin);
    check('keycloak-username-visible', await page.locator('#username').isVisible());
    check('keycloak-password-visible', await page.locator('#password').isVisible());
    const action = await page.locator('#kc-form-login').getAttribute('action');
    let actionOrigin = '';
    try { actionOrigin = new URL(action, page.url()).origin; } catch { /* recorded by the assertion below */ }
    check('keycloak-form-action-on-authority', actionOrigin === settings.authority.origin);
    check('authorize-client-callback-code-pkce-s256', authorizationChecks.length > 0
        && authorizationChecks.every(item => item.client && item.callback && item.code && item.pkce));
}

async function browserFlow(allowedOrigins) {
    stage = 'certificate-negative-control';
    const untrusted = await launch('untrusted-certificate-control', false);
    const untrustedContext = await makeContext(untrusted, allowedOrigins);
    const untrustedPage = await untrustedContext.newPage();
    let certificateRejected = false;
    try {
        await untrustedPage.goto(settings.authority.url.href, { waitUntil: 'domcontentloaded', timeout: 7000 });
    } catch (error) {
        certificateRejected = typeof error?.message === 'string' && error.message.includes('ERR_CERT_AUTHORITY_INVALID');
    }
    check('chromium-rejects-untrusted-leaf-without-exception', certificateRejected);
    if (settings.web.url.protocol === 'https:') {
        let appCertificateRejected = false;
        try {
            await untrustedPage.goto(settings.web.origin + '/', { waitUntil: 'domcontentloaded', timeout: 7000 });
        } catch (error) {
            appCertificateRejected = typeof error?.message === 'string'
                && error.message.includes('ERR_CERT_AUTHORITY_INVALID');
        }
        check('chromium-rejects-untrusted-app-leaf-without-exception', appCertificateRejected);
    }

    stage = 'supervised-login-logout';
    const browser = await launch('spki-pinned-positive-flow', true);
    browserVersion = browser.version();
    const context = await makeContext(browser, allowedOrigins, true);
    const page = await context.newPage();
    page.setDefaultTimeout(7000);
    page.setDefaultNavigationTimeout(8000);

    const initial = await readState(page);
    check('initial-state-is-anonymous', initial.status === 200 && initial.facts?.authenticated === false
        && (initial.facts.subject === null || initial.facts.subject === undefined || initial.facts.subject === ''));

    stage = 'unsolicited-callback';
    expectedCallbackRejection = true;
    const unsolicited = await page.goto(settings.web.origin + settings.callbackPath
        + '?error=access_denied&state=supervised-unsolicited', { waitUntil: 'domcontentloaded', timeout: 7000 });
    check('unsolicited-callback-returns-401', unsolicited?.status() === 401);
    const afterUnsolicited = await readState(page);
    expectedCallbackRejection = false;
    check('unsolicited-callback-keeps-anonymous-state', afterUnsolicited.status === 200
        && afterUnsolicited.facts?.authenticated === false);

    const authorizationChecks = [];
    const logoutChecks = [];
    context.on('request', request => {
        try {
            const url = new URL(request.url());
            if (url.origin !== settings.authority.origin) return;
            if (url.pathname === settings.authPath) {
                authorizationChecks.push({
                    client: url.searchParams.get('client_id') === settings.clientId,
                    callback: url.searchParams.get('redirect_uri') === settings.callbackUrl,
                    code: url.searchParams.get('response_type') === 'code',
                    pkce: url.searchParams.get('code_challenge_method') === 'S256'
                        && Boolean(url.searchParams.get('code_challenge'))
                });
            }
            if (url.pathname === settings.logoutPath) {
                logoutChecks.push({
                    client: url.searchParams.get('client_id') === settings.clientId,
                    callback: url.searchParams.get('post_logout_redirect_uri') === settings.signedOutCallbackUrl
                });
            }
        } catch { deniedRequests++; }
    });

    stage = 'keycloak-login-challenge';
    await page.goto(settings.web.origin + '/login', { waitUntil: 'domcontentloaded', timeout: 8000 });
    await keycloakChallenge(page, authorizationChecks);

    if (settings.themeCssPath) {
        stage = 'live-theme-edit';
        const sourceTheme = fs.readFileSync(settings.themeCssPath, 'utf8');
        check('theme-source-starts-with-first-marker', sourceTheme.includes('issue888-theme-first'));
        const stylesheets = await page.locator('link[rel=stylesheet]').evaluateAll(nodes => nodes.map(node => node.href));
        const matches = stylesheets.filter(href => {
            const url = new URL(href);
            return url.origin === settings.authority.origin && url.pathname.endsWith('/css/acceptance.css');
        });
        check('live-theme-stylesheet-linked-on-approved-authority', matches.length === 1);
        const stylesheet = matches[0];
        let themeFetch = 0;
        async function servedTheme() {
            return page.evaluate(async ({ href, revision }) => {
                const url = new URL(href);
                url.searchParams.set('issue888-revision', revision);
                const response = await fetch(url.href, { cache: 'no-store', credentials: 'omit' });
                return { ok: response.ok, status: response.status, url: response.url, text: await response.text() };
            }, { href: stylesheet, revision: String(++themeFetch) });
        }
        const firstTheme = await servedTheme();
        check('theme-initial-stylesheet-served-over-approved-https', firstTheme.ok && firstTheme.status === 200
            && new URL(firstTheme.url).origin === settings.authority.origin
            && new URL(firstTheme.url).protocol === 'https:' && firstTheme.text.includes('issue888-theme-first'));
        fs.writeFileSync(settings.themeCssPath, '/* issue888-theme-second */\n');
        const secondTheme = await servedTheme();
        check('theme-edited-stylesheet-served-live-without-restart', secondTheme.ok && secondTheme.status === 200
            && new URL(secondTheme.url).origin === settings.authority.origin
            && new URL(secondTheme.url).protocol === 'https:' && secondTheme.text.includes('issue888-theme-second'));
    }

    stage = 'dom-login';
    await page.locator('#username').fill(username);
    await page.locator('#password').fill(userPassword);
    username = null;
    userPassword = null;
    await page.locator('#kc-login').click();
    await page.waitForURL(url => url.origin === settings.web.origin, { waitUntil: 'domcontentloaded', timeout: 12_000 });

    stage = 'authenticated-state';
    const authenticated = await readState(page);
    check('real-code-exchange-authenticates-expected-subject', authenticated.status === 200
        && authenticated.facts?.authenticated === true && authenticated.facts.subject === settings.expectedSubject);
    if (settings.fixture === 'skoolit') {
        check('skoolit-application-projection-matches-seed', authenticated.status === 200
            && authenticated.facts?.applicationSubjectMatched === true);
    }
    check('exact-code-callback-observed-with-state', safeNavigation.some(item => item.origin === settings.web.origin
        && item.path === settings.callbackPath && item.hasCode && item.hasState));
    loginVerifiedSeconds = (performance.now() - startedAt) / 1000;

    stage = 'oidc-logout';
    await page.goto(settings.web.origin + '/logout', { waitUntil: 'domcontentloaded', timeout: 8000 });
    if (new URL(page.url()).origin === settings.authority.origin) {
        const confirm = page.locator('#kc-logout-confirm');
        await confirm.waitFor({ state: 'visible', timeout: 7000 });
        const logoutButton = page.locator('#kc-logout');
        check('keycloak-signout-confirmation-visible', await visible(confirm) && await visible(logoutButton));
        await logoutButton.click();
    }
    await page.waitForURL(url => url.origin === settings.web.origin, { waitUntil: 'domcontentloaded', timeout: 10_000 });
    check('exact-signed-out-callback-observed', safeNavigation.some(item => item.origin === settings.web.origin
        && item.path === settings.signedOutCallbackPath));
    check('logout-request-uses-exact-client-and-signed-out-callback', logoutChecks.length > 0
        && logoutChecks.every(item => item.client && item.callback));

    stage = 'signed-out-state';
    const signedOut = await readState(page);
    check('logout-clears-authenticated-state', signedOut.status === 200 && signedOut.facts?.authenticated === false
        && (signedOut.facts.subject === null || signedOut.facts.subject === undefined || signedOut.facts.subject === ''));

    stage = 'post-logout-login-challenge';
    await page.goto(settings.web.origin + '/login', { waitUntil: 'domcontentloaded', timeout: 8000 });
    await keycloakChallenge(page, authorizationChecks);
    check('post-logout-keycloak-form-requires-login', await page.locator('#password').isVisible());
    check('no-foreign-origin-attempts', deniedRequests === 0);
    check('all-proxy-routes-target-observed-loopback-ports', localRoutes.length > 0
        && localRoutes.every(route => route.localAddress === '127.0.0.1'
            && (route.origin === settings.web.origin
                ? route.workloadPort === settings.web.port
                    && (route.transport === 'HTTP' && route.localPort === settings.web.port
                        || route.transport === 'CONNECT' && settings.web.url.protocol === 'https:'
                            && route.localPort === appTls?.address().port)
                : route.origin === settings.authority.origin && route.transport === 'CONNECT'
                    && route.localPort === settings.authority.port && route.workloadPort === settings.authority.port))
        && localRoutes.some(route => route.origin === settings.web.origin)
        && localRoutes.some(route => route.origin === settings.authority.origin && route.transport === 'CONNECT'));
    check('no-unhandled-page-errors', pageErrors === 0);
    check('no-console-errors', consoleErrors === 0);
}

async function waitForChildExit(child, milliseconds) {
    if (child.exitCode !== null || child.signalCode !== null) return true;
    return new Promise(resolve => {
        const timer = setTimeout(() => finish(false), milliseconds);
        const finish = value => {
            clearTimeout(timer);
            child.removeListener('exit', onExit);
            resolve(value);
        };
        const onExit = () => finish(true);
        child.once('exit', onExit);
        if (child.exitCode !== null || child.signalCode !== null) finish(true);
    });
}

async function cleanup() {
    const deadline = performance.now() + CLOSE_BUDGET_MS;
    const settle = async (operations, allotted) => {
        const stopAt = Math.min(deadline, performance.now() + allotted);
        const results = await withinBudget(Promise.allSettled(operations), stopAt);
        return results;
    };

    await settle(contexts.map(context => context.close()), 1700);
    await settle(clients.map(client => client.close()), 1500);
    await settle(servers.map(entry => entry.server.close()), 2600);

    for (const entry of servers) {
        const child = entry.server.process();
        if (child.exitCode === null && child.signalCode === null) {
            try { child.kill('SIGTERM'); } catch { /* child may already be gone */ }
        }
    }
    await Promise.all(servers.map(entry => waitForChildExit(entry.server.process(), 700)));
    for (const entry of servers) {
        const child = entry.server.process();
        if (child.exitCode === null && child.signalCode === null) {
            try { child.kill('SIGKILL'); } catch { /* child may already be gone */ }
        }
    }
    await Promise.all(servers.map(entry => waitForChildExit(entry.server.process(), 900)));

    const proxyClose = Promise.all([proxy, appTls].filter(server => server?.listening)
        .map(server => new Promise(resolve => server.close(resolve))));
    const closingSockets = [...sockets].map(socket => new Promise(resolve => {
        socket.once('close', resolve);
        socket.destroy();
    }));
    await withinBudget(Promise.all(closingSockets), deadline);
    if (proxy) {
        try { proxy.closeAllConnections?.(); } catch { /* already closing */ }
        const remaining = Math.max(1, Math.min(1400, deadline - performance.now()));
        proxyClosed = await withinBudget(proxyClose, performance.now() + remaining);
    } else proxyClosed = true;

    for (const entry of servers) {
        const child = entry.server.process();
        const terminal = child.exitCode !== null || child.signalCode !== null;
        const receiptEntry = browserProcesses.find(item => item.role === entry.role);
        if (receiptEntry) receiptEntry.terminal = terminal;
    }
    resourcesClosed = browserProcesses.length === servers.length
        && browserProcesses.every(item => item.terminal)
        && sockets.size === 0 && proxyClosed && (!proxy || !proxy.listening) && (!appTls || !appTls.listening);
}

function writeReceipt(status, elapsedSeconds) {
    const receipt = {
        schema: 1,
        status,
        stage,
        errorType,
        assertions,
        engine: 'Chromium',
        headless: true,
        browserVersion,
        playwrightVersion,
        elapsedSeconds,
        loginVerifiedSeconds,
        resourcesClosed,
        browserProcesses,
        safeNavigation,
        localRoutes,
        deniedRequests,
        pageErrors,
        consoleErrors,
        safeConsoleErrors,
        closure: { proxyClosed, trackedSockets: sockets.size },
        qualifiedTLS: 'negative native rejection; positive exact leaf SPKI exception'
    };
    const file = path.join(settings?.evidencePath ?? path.resolve(args[3] || '.'), 'receipt.json');
    const descriptor = fs.openSync(file, 'wx', 0o600);
    try {
        fs.writeFileSync(descriptor, JSON.stringify(receipt, null, 2) + '\n');
        fs.fsyncSync(descriptor);
    } finally { fs.closeSync(descriptor); }
}

async function main() {
    const signalHandler = () => {
        signalCancelled = true;
        abort.abort();
    };
    process.on('SIGTERM', signalHandler);
    process.on('SIGINT', signalHandler);
    const deadlineTimer = setTimeout(() => {
        flowTimedOut = true;
        abort.abort();
    }, FLOW_BUDGET_MS);
    deadlineTimer.unref();
    try {
        try {
            readInputs();
            stage = 'local-restricted-proxy';
            const allowedOrigins = await withAbort(makeProxy());
            stage = 'browser-flow';
            await withAbort(browserFlow(allowedOrigins));
        } catch (error) {
            errorType = error instanceof HarnessFailure ? error.code
                : flowTimedOut ? 'FlowDeadline' : signalCancelled ? 'Cancelled' : 'OperationFailed';
        }
    } finally {
        clearTimeout(deadlineTimer);
        try { await cleanup(); } catch { resourcesClosed = false; }
        const elapsedSeconds = (performance.now() - startedAt) / 1000;
        const withinTotalBudget = elapsedSeconds < 60;
        assertions.push({ name: 'total-flow-under-60-seconds', passed: withinTotalBudget });
        if (!withinTotalBudget && !errorType) errorType = 'TotalDeadline';
        if (!resourcesClosed && !errorType) errorType = 'ResourcesUnverified';
        if (errorType) {
            try { writeReceipt('failed', elapsedSeconds); } catch { /* stdout remains sanitized */ }
            process.stdout.write(JSON.stringify({ status: 'failed', errorType, resourcesClosed }) + '\n');
            process.exitCode = 1;
        } else {
            try {
                writeReceipt('passed', elapsedSeconds);
                process.stdout.write(JSON.stringify({ status: 'passed', receipt: 'receipt.json', resourcesClosed }) + '\n');
            } catch {
                process.stdout.write(JSON.stringify({ status: 'failed', errorType: 'ReceiptWriteFailed', resourcesClosed }) + '\n');
                process.exitCode = 1;
            }
        }
        process.removeListener('SIGTERM', signalHandler);
        process.removeListener('SIGINT', signalHandler);
    }
}

await main();
