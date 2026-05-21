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

test('createMiniSearchConfiguration includes all searchable and stored fields', async () => {
  const { createMiniSearchConfiguration } = await loadSearchCore();

  const config = createMiniSearchConfiguration();

  assert.deepEqual(config.fields, ['title', 'aliases', 'keywords', 'summary', 'headings', 'bodyText', 'entryPoints']);
  assert.deepEqual(config.searchOptions.boost, {
    title: 6,
    aliases: 4,
    headings: 3,
    keywords: 2,
    summary: 2,
    entryPoints: 2,
    bodyText: 1
  });
  assert.equal(config.searchOptions.prefix, true);
  assert.equal(config.searchOptions.fuzzy, 0.1);
  assert.ok(config.storeFields.includes('breadcrumbs'));
  assert.ok(config.storeFields.includes('status'));
});
