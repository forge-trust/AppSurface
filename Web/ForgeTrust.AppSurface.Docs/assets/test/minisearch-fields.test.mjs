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
      entryPoints: [{ label: 'EntryNeedle' }],
      language: 'js'
    })
  ];

  index.addAll(docs.map(createMiniSearchDocument));

  for (const term of ['TitleNeedle', 'AliasNeedle', 'KeywordNeedle', 'SummaryNeedle', 'HeadingNeedle', 'BodyNeedle', 'EntryNeedle', 'JavaScript']) {
    const results = index.search(term);
    assert.equal(results[0]?.id, 'field-probe', `${term} should match the indexed document`);
  }
});

test('real MiniSearch candidates hydrate into reader-intent ranked docs', async () => {
  const { createMiniSearchConfiguration, createMiniSearchDocument, normalizeSearchDocument, rankSearchResults } = await loadSearchCore();
  const index = new MiniSearch(createMiniSearchConfiguration());

  const docs = [
    normalizeSearchDocument({
      id: 'api',
      path: '/docs/Namespaces/ForgeTrust.AppSurface.PackageInstaller',
      title: 'PackageInstaller API',
      pageType: 'api-reference',
      bodyText: 'install package configure setup '.repeat(12)
    }),
    normalizeSearchDocument({
      id: 'guide',
      path: '/docs/packages',
      title: 'Choose and install packages',
      pageType: 'guide',
      navGroup: 'Packages',
      summary: 'Install the right package and configure the first workflow.'
    }),
    normalizeSearchDocument({
      id: 'internal',
      path: '/docs/internals/package-index',
      title: 'Internal package index',
      pageType: 'internals',
      audience: 'maintainers',
      bodyText: 'install package configure setup'
    })
  ];
  const docsById = new Map(docs.map((doc) => [doc.id, doc]));
  index.addAll(docs.map(createMiniSearchDocument));

  const candidates = index.search('install package').map((result, miniSearchRank) => ({
    doc: docsById.get(result.id),
    miniSearchRank,
    miniSearchScore: result.score
  }));
  const ranked = rankSearchResults(candidates, { query: 'install package' });

  assert.equal(ranked[0].id, 'guide');
  assert.equal(ranked.at(-1).id, 'internal');
});
