import assert from 'node:assert/strict';
import { test } from 'node:test';
import MiniSearch from 'minisearch';
import { loadSearchCore } from './load-search-core.mjs';

test('real MiniSearch matches every AppSurface Docs search field', async () => {
  const { createMiniSearchConfiguration, createMiniSearchDocument, normalizeSearchDocument } = await loadSearchCore();
  const index = new MiniSearch(createMiniSearchConfiguration());

  const docs = [
    normalizeSearchDocument({
      id: 'field-probe',
      path: '/docs/field-probe',
      title: 'TitleNeedle',
      aliases: ['AliasNeedle'],
      keywords: ['KeywordNeedle'],
      summary: 'SummaryNeedle',
      headings: ['HeadingNeedle'],
      bodyText: 'BodyNeedle',
      entryPoints: [{ label: 'EntryNeedle' }]
    })
  ];

  index.addAll(docs.map(createMiniSearchDocument));

  for (const term of ['TitleNeedle', 'AliasNeedle', 'KeywordNeedle', 'SummaryNeedle', 'HeadingNeedle', 'BodyNeedle', 'EntryNeedle']) {
    const results = index.search(term);
    assert.equal(results[0]?.id, 'field-probe', `${term} should match the indexed document`);
  }
});
