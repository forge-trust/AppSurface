import assert from 'node:assert/strict';
import { test } from 'node:test';
import { loadSearchCore } from './load-search-core.mjs';

test('normalizeSearchDocument flattens optional entryPoints into a deterministic string', async () => {
  const { normalizeSearchDocument } = await loadSearchCore();

  const doc = normalizeSearchDocument({
    id: 'namespaces',
    path: '/docs/Namespaces',
    title: 'Namespaces',
    entryPoints: [
      'AddAppSurfaceDocs',
      { label: 'AddAppSurfaceDocs', summary: 'Registers docs', keywords: ['startup', 'docs'] },
      { target: { ignored: true }, targetText: 'UseAppSurfaceDocs', href: '/docs/setup' },
      null,
      42
    ]
  });

  assert.equal(doc.entryPoints, 'AddAppSurfaceDocs Registers docs startup docs UseAppSurfaceDocs /docs/setup');
});

test('normalizeSearchDocument handles missing and unsupported entryPoints', async () => {
  const { normalizeSearchDocument } = await loadSearchCore();

  assert.equal(normalizeSearchDocument({ id: 'a', path: '/a', title: 'A' }).entryPoints, '');
  assert.equal(normalizeSearchDocument({ id: 'b', path: '/b', title: 'B', entryPoints: 10 }).entryPoints, '');
  assert.equal(normalizeSearchDocument({ id: 'c', path: '/c', title: 'C', entryPoints: null }).entryPoints, '');
});

test('normalizeSearchDocument deduplicates entryPoints without locale-dependent casing', async () => {
  const { normalizeSearchDocument } = await loadSearchCore();
  const originalToLocaleLowerCase = String.prototype.toLocaleLowerCase;

  try {
    String.prototype.toLocaleLowerCase = function toLocaleLowerCaseShouldNotBeUsed() {
      throw new Error('entry point normalization must not depend on locale-sensitive casing');
    };

    const doc = normalizeSearchDocument({
      id: 'entry-points',
      path: '/docs/entry-points',
      title: 'Entry points',
      entryPoints: ['Install', 'install', 'Configure']
    });

    assert.equal(doc.entryPoints, 'Install Configure');
  } finally {
    String.prototype.toLocaleLowerCase = originalToLocaleLowerCase;
  }
});

test('normalizePageTypeAlias keeps client filters aligned with indexed docs', async () => {
  const { normalizePageTypeAlias, normalizeSearchDocument } = await loadSearchCore();

  for (const alias of ['api', 'API', 'reference', 'api-reference', 'api_reference', 'api reference']) {
    assert.equal(normalizePageTypeAlias(alias), 'api-reference');
    assert.equal(
      normalizeSearchDocument({ id: alias, path: `/${alias}`, title: alias, pageType: alias }).pageType,
      'api-reference'
    );
  }

  for (const alias of ['release-note', 'release-notes', 'release_notes', 'release notes']) {
    assert.equal(normalizePageTypeAlias(alias), 'release');
  }
});

test('normalizeSearchDocument preserves and labels generated code language', async () => {
  const { createMiniSearchDocument, normalizeSearchDocument } = await loadSearchCore();

  const csharpDoc = normalizeSearchDocument({
    id: 'calculator',
    path: '/docs/Namespaces/Test#calculator',
    title: 'Calculator',
    language: 'C-Sharp'
  });
  const jsDoc = normalizeSearchDocument({
    id: 'runtime',
    path: '/docs/api/javascript/razorwire',
    title: 'RazorWire JavaScript API',
    language: 'js'
  });

  assert.equal(csharpDoc.language, 'csharp');
  assert.equal(csharpDoc.languageLabel, 'C#');
  assert.equal(jsDoc.language, 'javascript');
  assert.equal(jsDoc.languageLabel, 'JavaScript');
  assert.equal(createMiniSearchDocument(csharpDoc).languageSearchText, 'csharp C# CSharp C-Sharp');
  assert.equal(createMiniSearchDocument(jsDoc).languageSearchText, 'javascript JavaScript js');
});

test('createMiniSearchConfiguration includes all searchable and stored fields', async () => {
  const { createMiniSearchConfiguration } = await loadSearchCore();

  const config = createMiniSearchConfiguration();

  assert.deepEqual(config.fields, ['title', 'aliases', 'keywords', 'summary', 'headings', 'bodyText', 'entryPoints', 'languageSearchText']);
  assert.deepEqual(config.searchOptions.boost, {
    title: 6,
    aliases: 4,
    headings: 3,
    keywords: 2,
    summary: 2,
    entryPoints: 2,
    languageSearchText: 2,
    bodyText: 1
  });
  assert.equal(config.searchOptions.prefix, true);
  assert.equal(config.searchOptions.fuzzy, 0.1);
  assert.ok(config.storeFields.includes('breadcrumbs'));
  assert.ok(config.storeFields.includes('status'));
  assert.ok(config.storeFields.includes('language'));
  assert.ok(config.storeFields.includes('languageLabel'));
});

test('validateSearchResultPath accepts docs-local browser paths under the active docs root', async () => {
  const { isSafeSearchResultPath, validateSearchResultPath } = await loadSearchCore();

  for (const path of [
    '/docs/guide.html',
    '/docs/packages/README.md.html',
    '/docs/guide.html?q=term#section',
    '/some-base/docs/v/1.2.3/guide.html',
    '/some-base/docs/versions/1.2.3/guide.html',
    '/foo/bar/versions/1.2.3/guide.html',
    '/versions/1.2.3/guide.html'
  ]) {
    const options = path.startsWith('/some-base')
      ? { docsRootPath: '/some-base/docs/v/1.2.3', docsArchiveRootPath: '/some-base/docs/versions' }
      : path.startsWith('/foo/bar')
        ? { docsRootPath: '/foo/bar/v/1.2.3', docsArchiveRootPath: '/foo/bar/versions' }
        : path.startsWith('/versions')
          ? { docsRootPath: '/v/1.2.3', docsArchiveRootPath: '/versions' }
          : { docsRootPath: '/docs', docsArchiveRootPath: '/docs/versions' };
    assert.equal(isSafeSearchResultPath(path, options), true, path);
    assert.equal(validateSearchResultPath(path, options).reason, 'none', path);
  }
});

test('validateSearchResultPath does not infer archive roots from version-like docs roots', async () => {
  const { validateSearchResultPath } = await loadSearchCore();

  assert.equal(
    validateSearchResultPath('/api/versions/docs/guide.html', { docsRootPath: '/api/v/docs' }).reason,
    'outside-docs-root'
  );
});

test('validateSearchResultPath rejects executable, off-root, reserved, and encoded traversal paths', async () => {
  const { validateSearchResultPath } = await loadSearchCore();
  const cases = [
    ['javascript:alert(1)', 'scheme-url'],
    ['data:text/html,hi', 'scheme-url'],
    ['https://evil.example/docs/guide.html', 'absolute-url'],
    ['//evil.example/docs/guide.html', 'protocol-relative'],
    ['/admin', 'outside-docs-root'],
    ['/tenant/docs/guide.html', 'outside-docs-root'],
    ['/docs/search', 'reserved-route'],
    ['/docs/search-index.json', 'reserved-route'],
    ['/docs/search-client.js', 'reserved-route'],
    ['/docs/_search-index/refresh', 'reserved-route'],
    ['/docs/v/9.9.9/guide.html', 'reserved-route'],
    ['/docs/versions/1.2.3/guide.html', 'reserved-route'],
    ['/docs/versions/search', 'reserved-route'],
    ['/docs/versions/_health', 'reserved-route'],
    ['/docs/versions/_search-index/refresh', 'reserved-route'],
    ['/docs/versions/1.2.3/_search-index/refresh', 'reserved-route'],
    ['/docs/../admin', 'encoded-traversal'],
    ['/docs/%2e%2e/admin', 'encoded-traversal'],
    ['/docs/%252e%252e/admin', 'encoded-traversal'],
    ['/docs/%2fadmin', 'encoded-separator'],
    ['/docs/%252fadmin', 'encoded-separator'],
    ['/docs/%5cadmin', 'encoded-separator'],
    ['/docs/guide.html\\evil', 'backslash'],
    ['/docs/%', 'malformed-percent-encoding'],
    ['/docs/%0a', 'control-character'],
    ['/docs/guide.html?q=%0a', 'control-character'],
    ['/docs/guide.html?q=%250a', 'control-character']
  ];

  for (const [path, reason] of cases) {
    const options = [
      '/docs/versions/search',
      '/docs/versions/_health',
      '/docs/versions/_search-index/refresh',
      '/docs/versions/1.2.3/_search-index/refresh'
    ].includes(path)
      ? { docsRootPath: '/docs', docsArchiveRootPath: '/docs/versions' }
      : { docsRootPath: '/docs' };
    assert.equal(validateSearchResultPath(path, options).reason, reason, path);
  }
});
