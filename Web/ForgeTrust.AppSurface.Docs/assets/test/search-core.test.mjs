import assert from 'node:assert/strict';
import { performance } from 'node:perf_hooks';
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

test('rankSearchResults preserves exact lookup matches ahead of broad candidates', async () => {
  const { normalizeSearchDocument, rankSearchResults } = await loadSearchCore();
  const docs = [
    normalizeSearchDocument({
      id: 'broad-guide',
      path: '/docs/guides/appsurface',
      title: 'AppSurface guide',
      pageType: 'guide',
      bodyText: 'AddRazorWire AddRazorWire AddRazorWire setup and configuration'
    }),
    normalizeSearchDocument({
      id: 'symbol',
      path: '/docs/Namespaces/ForgeTrust.RazorWire#AddRazorWire',
      sourcePath: 'Web/ForgeTrust.RazorWire/RazorWireServiceCollectionExtensions.cs',
      title: 'AddRazorWire',
      pageType: 'api-reference',
      aliases: ['AddRazorWire']
    })
  ];

  const ranked = rankSearchResults([
    { doc: docs[0], miniSearchRank: 0, miniSearchScore: 20 },
    { doc: docs[1], miniSearchRank: 1, miniSearchScore: 2 }
  ], { query: 'AddRazorWire' });

  assert.equal(ranked[0].id, 'symbol');
});

test('rankSearchResults promotes metadata and entry point matches before body-only matches', async () => {
  const { normalizeSearchDocument, rankSearchResults } = await loadSearchCore();
  const docs = [
    normalizeSearchDocument({
      id: 'body',
      path: '/docs/reference/body',
      title: 'Runtime reference',
      pageType: 'api-reference',
      bodyText: 'Search index repair appears many times in generated API detail.'
    }),
    normalizeSearchDocument({
      id: 'alias',
      path: '/docs/troubleshooting/search',
      title: 'Troubleshoot search indexing',
      pageType: 'troubleshooting',
      aliases: ['missing results'],
      keywords: ['search index repair'],
      entryPoints: [{ label: 'Refresh index', keywords: ['repair search index'] }]
    })
  ];

  const ranked = rankSearchResults([
    { doc: docs[0], miniSearchRank: 0, miniSearchScore: 10 },
    { doc: docs[1], miniSearchRank: 1, miniSearchScore: 1 }
  ], { query: 'search index repair' });

  assert.equal(ranked[0].id, 'alias');
});

test('rankSearchResults prefers task docs for broad task queries but honors explicit API filters', async () => {
  const { explainSearchResultRanking, normalizeSearchDocument, rankSearchResults } = await loadSearchCore();
  const docs = [
    normalizeSearchDocument({
      id: 'api',
      path: '/docs/Namespaces/ForgeTrust.AppSurface.Packages',
      title: 'Package API Reference',
      pageType: 'api-reference',
      bodyText: 'Set up package install configure'
    }),
    normalizeSearchDocument({
      id: 'guide',
      path: '/docs/packages',
      title: 'Package adoption guide',
      pageType: 'guide',
      navGroup: 'Packages',
      bodyText: 'Set up package install configure'
    })
  ];

  const broadRanked = rankSearchResults([
    { doc: docs[0], miniSearchRank: 0, miniSearchScore: 10 },
    { doc: docs[1], miniSearchRank: 1, miniSearchScore: 8 }
  ], { query: 'set up package' });
  const apiFiltered = rankSearchResults([
    { doc: docs[1], miniSearchRank: 0, miniSearchScore: 8 },
    { doc: docs[0], miniSearchRank: 1, miniSearchScore: 10 }
  ], { query: 'set up package', filters: { pageType: 'api-reference' } });
  const apiExplanation = explainSearchResultRanking([
    { doc: docs[0], miniSearchRank: 0, miniSearchScore: 10 }
  ], { query: 'set up package', filters: { pageType: 'api-reference' } });

  assert.equal(broadRanked[0].id, 'guide');
  assert.equal(apiFiltered[0].id, 'api');
  assert.equal(apiExplanation[0].filterOverride, true);
  assert.equal(apiExplanation[0].broadTaskBoost, false);
});

test('rankSearchResults boosts hyphenated task page types for broad task queries', async () => {
  const { normalizeSearchDocument, rankSearchResults } = await loadSearchCore();
  const docs = [
    normalizeSearchDocument({
      id: 'reference',
      path: '/docs/reference/install',
      title: 'Install reference',
      pageType: 'api-reference',
      bodyText: 'install configure setup'
    }),
    normalizeSearchDocument({
      id: 'how-to',
      path: '/docs/how-to/install',
      title: 'How to install',
      pageType: 'how-to',
      bodyText: 'install configure setup'
    }),
    normalizeSearchDocument({
      id: 'start-here',
      path: '/docs/start',
      title: 'Start here',
      pageType: 'start-here',
      bodyText: 'install configure setup'
    })
  ];

  const ranked = rankSearchResults([
    { doc: docs[0], miniSearchRank: 0, miniSearchScore: 10 },
    { doc: docs[1], miniSearchRank: 1, miniSearchScore: 8 },
    { doc: docs[2], miniSearchRank: 2, miniSearchScore: 7 }
  ], { query: 'install configure' });

  assert.deepEqual(ranked.map((doc) => doc.id), ['how-to', 'start-here', 'reference']);
});

test('rankSearchResults demotes internal docs for broad queries but protects exact internal intent', async () => {
  const { normalizeSearchDocument, rankSearchResults } = await loadSearchCore();
  const docs = [
    normalizeSearchDocument({
      id: 'internal',
      path: '/docs/internals/search-diagnostics',
      title: 'Internal search diagnostics',
      pageType: 'internals',
      audience: 'maintainers',
      bodyText: 'Debug search indexing'
    }),
    normalizeSearchDocument({
      id: 'public',
      path: '/docs/troubleshooting/search',
      title: 'Troubleshoot search indexing',
      pageType: 'troubleshooting',
      bodyText: 'Debug search indexing'
    })
  ];

  const broadRanked = rankSearchResults([
    { doc: docs[0], miniSearchRank: 0, miniSearchScore: 10 },
    { doc: docs[1], miniSearchRank: 1, miniSearchScore: 8 }
  ], { query: 'debug search indexing' });
  const exactInternal = rankSearchResults([
    { doc: docs[1], miniSearchRank: 0, miniSearchScore: 8 },
    { doc: docs[0], miniSearchRank: 1, miniSearchScore: 10 }
  ], { query: 'internal search diagnostics' });

  assert.equal(broadRanked[0].id, 'public');
  assert.equal(exactInternal[0].id, 'internal');
});

test('rankSearchResults keeps exact internal metadata demoted without explicit internal intent', async () => {
  const { normalizeSearchDocument, rankSearchResults } = await loadSearchCore();
  const docs = [
    normalizeSearchDocument({
      id: 'internal',
      path: '/docs/contributors/search-diagnostics',
      title: 'Search diagnostics',
      aliases: ['diagnostics', 'contributor diagnostics'],
      pageType: 'guide',
      audience: 'contributors'
    }),
    normalizeSearchDocument({
      id: 'public',
      path: '/docs/troubleshooting/search-diagnostics',
      title: 'Troubleshoot search diagnostics',
      pageType: 'troubleshooting'
    })
  ];

  const broadRanked = rankSearchResults([
    { doc: docs[0], miniSearchRank: 0, miniSearchScore: 10 },
    { doc: docs[1], miniSearchRank: 1, miniSearchScore: 5 }
  ], { query: 'diagnostics' });
  const explicitRanked = rankSearchResults([
    { doc: docs[1], miniSearchRank: 0, miniSearchScore: 5 },
    { doc: docs[0], miniSearchRank: 1, miniSearchScore: 10 }
  ], { query: 'contributor diagnostics' });

  assert.equal(broadRanked[0].id, 'public');
  assert.equal(explicitRanked[0].id, 'internal');
});

test('explainSearchResultRanking exposes local non-telemetry ranking reasons', async () => {
  const { explainSearchResultRanking, normalizeSearchDocument } = await loadSearchCore();
  const doc = normalizeSearchDocument({
    id: 'alias',
    path: '/docs/troubleshooting/search',
    title: 'Troubleshoot search indexing',
    aliases: ['missing search results'],
    pageType: 'troubleshooting'
  });

  const [explanation] = explainSearchResultRanking([
    { doc, miniSearchRank: 3, miniSearchScore: 4.5 }
  ], { query: 'missing search results' });

  assert.equal(explanation.finalRank, 1);
  assert.equal(explanation.miniSearchRank, 3);
  assert.equal(explanation.miniSearchScore, 4.5);
  assert.equal(explanation.aliasOrKeywordMatch, true);
  assert.deepEqual(explanation.matchedFields, ['aliases']);
});

test('rankSearchResults keeps large-corpus rescoring bounded', async () => {
  const { normalizeSearchDocument, rankSearchResults } = await loadSearchCore();
  const candidates = Array.from({ length: 5000 }, (_, index) => ({
    doc: normalizeSearchDocument({
      id: `doc-${index}`,
      path: `/docs/generated/${index}`,
      title: `Generated page ${index}`,
      pageType: index % 10 === 0 ? 'guide' : 'api-reference',
      bodyText: 'install configure setup package '.repeat(20),
      order: index
    }),
    miniSearchRank: index,
    miniSearchScore: 5000 - index
  }));

  const started = performance.now();
  const ranked = rankSearchResults(candidates, { query: 'install package' });
  const elapsed = performance.now() - started;
  const budgetMs = process.env.CI ? 2500 : 1000;

  assert.equal(ranked.length, 5000);
  assert.equal(ranked[0].id, 'doc-0');
  assert.ok(elapsed < budgetMs, `ranking took ${elapsed}ms; budget is ${budgetMs}ms`);
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
    ['/docs/_harvest', 'reserved-route'],
    ['/docs/_harvest/rebuild', 'reserved-route'],
    ['/docs/_search-index/refresh', 'reserved-route'],
    ['/docs/v/9.9.9/guide.html', 'reserved-route'],
    ['/docs/versions/1.2.3/guide.html', 'reserved-route'],
    ['/docs/versions/search', 'reserved-route'],
    ['/docs/versions/_harvest/rebuild', 'reserved-route'],
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
    ['/docs/guide.html\u0085', 'control-character'],
    ['/docs/%C2%85', 'control-character'],
    ['/docs/%0a', 'control-character'],
    ['/docs/guide.html?q=%0a', 'control-character'],
    ['/docs/guide.html?q=%250a', 'control-character']
  ];

  for (const [path, reason] of cases) {
    const options = [
      '/docs/versions/search',
      '/docs/versions/_harvest/rebuild',
      '/docs/versions/_health',
      '/docs/versions/_search-index/refresh',
      '/docs/versions/1.2.3/_search-index/refresh'
    ].includes(path)
      ? { docsRootPath: '/docs', docsArchiveRootPath: '/docs/versions' }
      : { docsRootPath: '/docs' };
    assert.equal(validateSearchResultPath(path, options).reason, reason, path);
  }
});
