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
