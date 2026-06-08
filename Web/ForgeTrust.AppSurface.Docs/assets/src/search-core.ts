export const searchFields = ['title', 'aliases', 'keywords', 'summary', 'headings', 'bodyText', 'entryPoints', 'languageSearchText'];

export const storeFields = [
  'id',
  'path',
  'title',
  'snippet',
  'summary',
  'breadcrumbs',
  'pageType',
  'pageTypeLabel',
  'pageTypeVariant',
  'component',
  'language',
  'languageLabel',
  'audience',
  'status',
  'navGroup'
];

export const defaultSearchOptions = {
  prefix: true,
  fuzzy: 0.1,
  boost: { title: 6, aliases: 4, headings: 3, keywords: 2, summary: 2, entryPoints: 2, languageSearchText: 2, bodyText: 1 }
};

export function createMiniSearchConfiguration() {
  return {
    fields: [...searchFields],
    storeFields: [...storeFields],
    searchOptions: {
      ...defaultSearchOptions,
      boost: { ...defaultSearchOptions.boost }
    }
  };
}

export function normalizeSearchDocument(doc: any) {
  const orderValue = Number.parseInt(String(doc?.order ?? ''), 10);

  return {
    id: String(doc?.id ?? doc?.path ?? ''),
    path: String(doc?.path ?? ''),
    title: String(doc?.title ?? '').trim(),
    summary: String(doc?.summary ?? '').trim(),
    headings: toStringArray(doc?.headings),
    bodyText: String(doc?.bodyText ?? ''),
    snippet: String(doc?.snippet ?? '').trim(),
    pageType: normalizePageTypeAlias(doc?.pageType),
    pageTypeLabel: String(doc?.pageTypeLabel ?? '').trim(),
    pageTypeVariant: normalizeFacetValue(doc?.pageTypeVariant),
    audience: normalizeFacetValue(doc?.audience),
    component: normalizeFacetValue(doc?.component),
    language: normalizeCodeLanguage(doc?.language),
    languageLabel: normalizeCodeLanguageLabel(doc?.language, doc?.languageLabel),
    aliases: toStringArray(doc?.aliases),
    keywords: toStringArray(doc?.keywords),
    entryPoints: flattenEntryPoints(doc?.entryPoints),
    status: normalizeFacetValue(doc?.status),
    navGroup: String(doc?.navGroup ?? '').trim(),
    publicSection: normalizeFacetValue(doc?.publicSection),
    publicSectionLabel: String(doc?.publicSectionLabel ?? '').trim(),
    isSectionLanding: Boolean(doc?.isSectionLanding),
    order: Number.isFinite(orderValue) ? orderValue : null,
    sequenceKey: normalizeFacetValue(doc?.sequenceKey),
    canonicalSlug: String(doc?.canonicalSlug ?? '').trim(),
    relatedPages: toStringArray(doc?.relatedPages),
    breadcrumbs: toStringArray(doc?.breadcrumbs),
    sourcePath: String(doc?.sourcePath ?? '').trim()
  };
}

export function createMiniSearchDocument(doc: any) {
  return {
    id: doc.id,
    path: doc.path,
    title: doc.title,
    aliases: doc.aliases.join(' '),
    keywords: doc.keywords.join(' '),
    entryPoints: doc.entryPoints,
    languageSearchText: buildLanguageSearchText(doc.language, doc.languageLabel),
    summary: doc.summary,
    headings: doc.headings.join(' '),
    bodyText: doc.bodyText,
    snippet: doc.snippet,
    breadcrumbs: doc.breadcrumbs,
    pageType: doc.pageType,
    pageTypeLabel: doc.pageTypeLabel ?? '',
    pageTypeVariant: doc.pageTypeVariant ?? '',
    component: doc.component,
    language: doc.language,
    languageLabel: doc.languageLabel ?? '',
    audience: doc.audience,
    status: doc.status,
    navGroup: doc.navGroup
  };
}

export function rankSearchResults(candidates: any[] = [], options: any = {}) {
  return explainSearchResultRanking(candidates, options).map((item) => item.doc);
}

export function explainSearchResultRanking(candidates: any[] = [], options: any = {}) {
  const query = normalizeRankingQuery(options.searchQuery ?? options.query);
  const filters = normalizeRankingFilters(options.filters);
  const filterIntent = createFilterIntent(filters);
  const queryInfo = analyzeRankingQuery(query);
  const seenIds = new Set<string>();
  const explanations: any[] = [];

  candidates.forEach((candidate, index) => {
    const doc = candidate?.doc ?? candidate;
    if (!doc?.id || seenIds.has(doc.id)) {
      return;
    }

    seenIds.add(doc.id);
    const miniSearchRank = Number.isFinite(candidate?.miniSearchRank) ? candidate.miniSearchRank : index;
    const miniSearchScore = Number.isFinite(candidate?.miniSearchScore) ? candidate.miniSearchScore : 0;
    const signals = classifyRankingSignals(doc, queryInfo, filterIntent);
    const priority = getRankingPriority(signals);

    explanations.push({
      doc,
      finalRank: 0,
      priority,
      miniSearchRank,
      miniSearchScore,
      matchedFields: signals.matchedFields,
      exactMatch: signals.exactMatch,
      aliasOrKeywordMatch: signals.aliasOrKeywordMatch,
      entryPointMatch: signals.entryPointMatch,
      broadTaskBoost: signals.broadTaskBoost,
      internalDemotion: signals.internalDemotion,
      internalOrContributor: signals.internalOrContributor,
      filterOverride: signals.filterOverride,
      filterMismatch: signals.filterMismatch
    });
  });

  explanations.sort(compareRankingExplanations);
  explanations.forEach((item, index) => {
    item.finalRank = index + 1;
  });

  return explanations;
}

export function isInternalOrContributorDoc(doc: any) {
  const pageType = normalizeTokenText(normalizePageTypeAlias(doc?.pageType));
  const navGroup = normalizeTokenText(doc?.navGroup);
  const publicSection = normalizeTokenText(doc?.publicSection);
  const audience = normalizeTokenText(doc?.audience);
  const status = normalizeTokenText(doc?.status);
  const path = normalizeTokenText(doc?.path);
  const sourcePath = normalizeTokenText(doc?.sourcePath);

  return [
    pageType,
    navGroup,
    publicSection,
    audience,
    status,
    path,
    sourcePath
  ].some((value) => /\b(internal|internals|contributor|contributors|maintainer|maintainers)\b/.test(value));
}

export function isSafeSearchResultPath(value: any, options: any = {}) {
  return validateSearchResultPath(value, options).isValid;
}

export function validateSearchResultPath(value: any, options: any = {}) {
  if (typeof value !== 'string' || !value.trim()) {
    return invalidSearchResultPath('missing');
  }

  if (value !== value.trim()) {
    return invalidSearchResultPath('whitespace');
  }

  if (hasControlCharacter(value)) {
    return invalidSearchResultPath('control-character');
  }

  if (value.includes('\\')) {
    return invalidSearchResultPath('backslash');
  }

  if (!value.startsWith('/')) {
    if (isAbsoluteHttpUrl(value)) {
      return invalidSearchResultPath('absolute-url');
    }

    if (hasSchemePrefix(value)) {
      return invalidSearchResultPath('scheme-url');
    }

    return invalidSearchResultPath('not-root-relative');
  }

  if (value.startsWith('//')) {
    return invalidSearchResultPath('protocol-relative');
  }

  const suffixIndex = findFirstSuffixIndex(value);
  const path = suffixIndex >= 0 ? value.slice(0, suffixIndex) : value;
  const suffix = suffixIndex >= 0 ? value.slice(suffixIndex) : '';
  if (suffix.includes('\\')) {
    return invalidSearchResultPath('backslash');
  }

  if (hasControlCharacter(suffix)) {
    return invalidSearchResultPath('control-character');
  }

  const percentValidation = validatePercentEscapes(value, false);
  if (!percentValidation.isValid) {
    return percentValidation;
  }

  const pathPercentValidation = validatePercentEscapes(path, true);
  if (!pathPercentValidation.isValid) {
    return pathPercentValidation;
  }

  let decodedPath;
  try {
    decodedPath = decodeURIComponent(path);
  } catch {
    return invalidSearchResultPath('malformed-percent-encoding');
  }

  if (hasControlCharacter(decodedPath)) {
    return invalidSearchResultPath('control-character');
  }

  if (decodedPath.includes('\\')) {
    return invalidSearchResultPath('backslash');
  }

  if (containsDotSegment(path) || containsDotSegment(decodedPath)) {
    return invalidSearchResultPath('encoded-traversal');
  }

  const allowedRoots = getAllowedSearchResultRoots(options);
  const matchedRoot = allowedRoots.find((root) => isUnderRoot(path, root.path));
  if (!matchedRoot) {
    return invalidSearchResultPath('outside-docs-root');
  }

  let relativePath = path === matchedRoot.path
    ? ''
    : path.slice(matchedRoot.path === '/' ? 1 : matchedRoot.path.length + 1);
  if (matchedRoot.isArchiveRoot) {
    relativePath = stripArchiveVersionSegment(relativePath);
    if (relativePath === null) {
      return invalidSearchResultPath('reserved-route');
    }
  }

  if (isReservedSearchResultRoute(relativePath)) {
    return invalidSearchResultPath('reserved-route');
  }

  return { isValid: true, reason: 'none', normalizedPath: path };
}

export function flattenEntryPoints(value: any) {
  const terms: string[] = [];
  collectEntryPointTerms(value, terms);

  const seen = new Set<string>();
  const unique: string[] = [];
  for (const term of terms) {
    const normalized = term.trim();
    if (!normalized) {
      continue;
    }

    const key = normalized.toLowerCase();
    if (!seen.has(key)) {
      seen.add(key);
      unique.push(normalized);
    }
  }

  return unique.join(' ');
}

function invalidSearchResultPath(reason: string) {
  return { isValid: false, reason, normalizedPath: '' };
}

function normalizeSearchDocsRootPath(value: any) {
  const raw = String(value || '').trim();
  if (!raw) {
    return '/docs';
  }

  const prefixed = raw.startsWith('/') ? raw : `/${raw}`;
  return prefixed !== '/' && prefixed.endsWith('/') ? prefixed.slice(0, -1) : prefixed;
}

function getAllowedSearchResultRoots(options: any) {
  const docsRootPath = normalizeSearchDocsRootPath(options?.docsRootPath || '/docs');
  const roots = [{ path: docsRootPath, isArchiveRoot: false }];
  const archiveRootPath = String(options?.docsArchiveRootPath || '').trim();
  if (archiveRootPath) {
    roots.push({ path: normalizeSearchDocsRootPath(archiveRootPath), isArchiveRoot: true });
  }

  return roots.sort((left, right) => right.path.length - left.path.length);
}

function findFirstSuffixIndex(value: string) {
  const queryIndex = value.indexOf('?');
  const fragmentIndex = value.indexOf('#');
  if (queryIndex < 0) {
    return fragmentIndex;
  }

  if (fragmentIndex < 0) {
    return queryIndex;
  }

  return Math.min(queryIndex, fragmentIndex);
}

function hasControlCharacter(value: string) {
  return /[\u0000-\u001f\u007f-\u009f]/.test(value);
}

function isAbsoluteHttpUrl(value: string) {
  try {
    const url = new URL(value);
    return url.protocol === 'http:' || url.protocol === 'https:';
  } catch {
    return false;
  }
}

function hasSchemePrefix(value: string) {
  const colonIndex = value.indexOf(':');
  if (colonIndex <= 0) {
    return false;
  }

  const separatorIndexes = ['/', '?', '#']
    .map((separator) => value.indexOf(separator))
    .filter((index) => index >= 0);
  const firstSeparator = separatorIndexes.length > 0 ? Math.min(...separatorIndexes) : -1;
  return firstSeparator < 0 || colonIndex < firstSeparator;
}

function validatePercentEscapes(value: string, scanSensitivePathTokens: boolean) {
  for (let index = 0; index < value.length; index += 1) {
    if (value[index] !== '%') {
      continue;
    }

    if (index + 2 >= value.length || !isHex(value[index + 1]) || !isHex(value[index + 2])) {
      return invalidSearchResultPath('malformed-percent-encoding');
    }

    const decoded = Number.parseInt(value.slice(index + 1, index + 3), 16);
    if (decoded < 0x20 || decoded === 0x7f) {
      return invalidSearchResultPath('control-character');
    }

    if (scanSensitivePathTokens && (decoded === 0x2f || decoded === 0x5c)) {
      return invalidSearchResultPath('encoded-separator');
    }

    if (scanSensitivePathTokens && decoded === 0x2e) {
      return invalidSearchResultPath('encoded-traversal');
    }

    if (decoded === 0x25 && index + 4 < value.length && isHex(value[index + 3]) && isHex(value[index + 4])) {
      const doubleDecoded = Number.parseInt(value.slice(index + 3, index + 5), 16);
      if (doubleDecoded < 0x20 || doubleDecoded === 0x7f) {
        return invalidSearchResultPath('control-character');
      }

      if (scanSensitivePathTokens && (doubleDecoded === 0x2f || doubleDecoded === 0x5c)) {
        return invalidSearchResultPath('encoded-separator');
      }

      if (scanSensitivePathTokens && doubleDecoded === 0x2e) {
        return invalidSearchResultPath('encoded-traversal');
      }
    }
  }

  return { isValid: true, reason: 'none', normalizedPath: '' };
}

function isHex(value: string) {
  return /^[0-9a-fA-F]$/.test(value);
}

function containsDotSegment(path: string) {
  return path.split('/').some((segment) => segment === '.' || segment === '..');
}

function isUnderRoot(path: string, root: string) {
  if (root === '/') {
    return path.startsWith('/');
  }

  return path === root || path.startsWith(`${root}/`);
}

function isReservedSearchResultRoute(relativePath: string) {
  if (!relativePath) {
    return false;
  }

  const trimmed = relativePath.replace(/^\/+|\/+$/g, '').toLowerCase();
  const [firstSegment] = trimmed.split('/');
  if (firstSegment === 'v' || firstSegment === 'versions') {
    return true;
  }

  return [
    'search',
    'search-index.json',
    'search.css',
    'search-client.js',
    'outline-client.js',
    'minisearch.min.js',
    '_health',
    '_health.json',
    '_routes',
    '_routes.json',
    '_search-index',
    'v',
    'versions'
  ].includes(trimmed) || trimmed.startsWith('_search-index/');
}

function stripArchiveVersionSegment(relativePath: string) {
  if (!relativePath) {
    return null;
  }

  const separator = relativePath.indexOf('/');
  const versionSegment = separator >= 0 ? relativePath.slice(0, separator) : relativePath;
  if (isReservedSearchResultRoute(versionSegment)) {
    return null;
  }

  return separator >= 0 ? relativePath.slice(separator + 1) : '';
}

function collectEntryPointTerms(value: any, terms: string[]) {
  if (typeof value === 'string') {
    terms.push(value);
    return;
  }

  if (Array.isArray(value)) {
    for (const item of value) {
      collectEntryPointTerms(item, terms);
    }

    return;
  }

  if (!value || typeof value !== 'object') {
    return;
  }

  collectEntryPointTerms(value.label, terms);
  collectEntryPointTerms(value.summary, terms);
  collectEntryPointTerms(value.keywords, terms);
  collectEntryPointTerms(value.target, terms);
  collectEntryPointTerms(value.targetText, terms);
  collectEntryPointTerms(value.path, terms);
  collectEntryPointTerms(value.href, terms);
}

function normalizeRankingQuery(value: any) {
  return normalizeWhitespace(String(value ?? '').slice(0, 500));
}

function normalizeRankingFilters(filters: any) {
  return {
    pageType: normalizePageTypeAlias(filters?.pageType),
    component: normalizeFacetValue(filters?.component),
    language: normalizeCodeLanguage(filters?.language),
    audience: normalizeFacetValue(filters?.audience),
    status: normalizeFacetValue(filters?.status)
  };
}

function createFilterIntent(filters: any) {
  const pageType = normalizePageTypeAlias(filters.pageType);
  const audience = normalizeTokenText(filters.audience);
  return {
    filters,
    hasActiveFilters: Object.values(filters).some(Boolean),
    api: pageType === 'api' || pageType === 'api-reference',
    internal: /\b(internal|internals|contributor|contributors|maintainer|maintainers)\b/.test(`${pageType} ${audience}`)
  };
}

function analyzeRankingQuery(query: string) {
  const normalized = normalizeTokenText(query);
  const tokens = normalized
    .split(' ')
    .filter((token) => token.length > 1)
    .slice(0, 8);

  return {
    raw: query,
    normalized,
    tokens,
    isInternalIntent: /\b(internal|internals|contributor|contributors|maintainer|maintainers)\b/.test(normalized),
    isTaskIntent: isTaskIntentQuery(normalized, tokens)
  };
}

function isTaskIntentQuery(normalized: string, tokens: string[]) {
  if (!normalized) {
    return false;
  }

  if (/\b(how|setup|install|configure|configuring|configuration|quickstart|start|getting started|troubleshoot|troubleshooting|fix|debug|resolve|guide|example|use|using|adopt|migrate|upgrade|publish|deploy)\b/.test(normalized)) {
    return true;
  }

  return tokens.length >= 2;
}

function classifyRankingSignals(doc: any, queryInfo: any, filterIntent: any) {
  const matchedFields: string[] = [];
  const internalOrContributor = isInternalOrContributorDoc(doc);
  const exactMatch = Boolean(queryInfo.normalized) && hasExactDocumentMatch(doc, queryInfo, matchedFields);
  const aliasOrKeywordMatch = Boolean(queryInfo.normalized) && hasMetadataMatch(
    [
      ['aliases', doc?.aliases],
      ['keywords', doc?.keywords]
    ],
    queryInfo,
    matchedFields);
  const entryPointMatch = Boolean(queryInfo.normalized) && fieldContainsQuery(doc?.entryPoints, queryInfo);
  if (entryPointMatch) {
    matchedFields.push('entryPoints');
  }

  const filterOverride = filterIntent.api || filterIntent.internal;
  const filterMismatch = filterIntent.hasActiveFilters && !matchesRankingFilters(doc, filterIntent.filters);
  const explicitInternalIntent = filterIntent.internal || queryInfo.isInternalIntent;
  const exactInternalIntent = internalOrContributor
    && explicitInternalIntent
    && (exactMatch || aliasOrKeywordMatch || entryPointMatch);
  const broadTaskBoost = queryInfo.isTaskIntent
    && !filterOverride
    && isReaderTaskDoc(doc)
    && !internalOrContributor;
  const internalDemotion = internalOrContributor
    && !explicitInternalIntent;

  return {
    matchedFields: [...new Set(matchedFields)],
    exactMatch,
    exactInternalIntent,
    aliasOrKeywordMatch,
    entryPointMatch,
    broadTaskBoost,
    internalDemotion,
    internalOrContributor,
    filterOverride,
    filterMismatch
  };
}

function getRankingPriority(signals: any) {
  if (signals.filterMismatch) {
    return -1;
  }

  if (signals.exactMatch && !signals.internalDemotion) {
    return 6;
  }

  if (signals.exactInternalIntent) {
    return 5;
  }

  if ((signals.aliasOrKeywordMatch || signals.entryPointMatch) && !signals.internalDemotion) {
    return 4;
  }

  if (signals.broadTaskBoost) {
    return 3;
  }

  if (signals.internalDemotion) {
    return 0;
  }

  return 2;
}

function compareRankingExplanations(left: any, right: any) {
  if (left.priority !== right.priority) {
    return right.priority - left.priority;
  }

  if (left.miniSearchRank !== right.miniSearchRank) {
    return left.miniSearchRank - right.miniSearchRank;
  }

  const leftOrder = left.doc?.order ?? Number.MAX_SAFE_INTEGER;
  const rightOrder = right.doc?.order ?? Number.MAX_SAFE_INTEGER;
  if (leftOrder !== rightOrder) {
    return leftOrder - rightOrder;
  }

  return String(left.doc?.path ?? '').localeCompare(String(right.doc?.path ?? ''), undefined, { sensitivity: 'base' });
}

function hasExactDocumentMatch(doc: any, queryInfo: any, matchedFields: string[]) {
  const exactFields = [
    ['title', doc?.title],
    ['path', doc?.path],
    ['sourcePath', doc?.sourcePath],
    ['canonicalSlug', doc?.canonicalSlug]
  ];

  for (const [name, value] of exactFields) {
    if (fieldExactlyMatches(value, queryInfo)) {
      matchedFields.push(name);
      return true;
    }
  }

  for (const [name, values] of [
    ['aliases', doc?.aliases],
    ['keywords', doc?.keywords],
    ['breadcrumbs', doc?.breadcrumbs],
    ['relatedPages', doc?.relatedPages]
  ]) {
    if (arrayFieldExactlyMatches(values, queryInfo)) {
      matchedFields.push(name);
      return true;
    }
  }

  return false;
}

function hasMetadataMatch(fields: any[], queryInfo: any, matchedFields: string[]) {
  let matched = false;
  for (const [name, value] of fields) {
    if (fieldContainsQuery(value, queryInfo)) {
      matchedFields.push(name);
      matched = true;
    }
  }

  return matched;
}

function isReaderTaskDoc(doc: any) {
  const pageType = normalizePageTypeAlias(doc?.pageType);
  const publicSection = normalizeTokenText(doc?.publicSection);
  const navGroup = normalizeTokenText(doc?.navGroup);

  return [
    'guide',
    'concept',
    'tutorial',
    'example',
    'how-to',
    'start-here',
    'troubleshooting',
    'faq'
  ].includes(pageType)
    || /\b(start|guide|guides|example|examples|troubleshooting|how to|tutorial|adopt|packages)\b/.test(`${publicSection} ${navGroup}`);
}

function matchesRankingFilters(doc: any, filters: any) {
  return [
    ['pageType', normalizePageTypeAlias(doc?.pageType)],
    ['component', normalizeFacetValue(doc?.component)],
    ['language', normalizeCodeLanguage(doc?.language)],
    ['audience', normalizeFacetValue(doc?.audience)],
    ['status', normalizeFacetValue(doc?.status)]
  ].every(([key, actual]) => {
    const expected = filters[key];
    return !expected || actual === expected;
  });
}

function fieldExactlyMatches(value: any, queryInfo: any) {
  const normalized = normalizeTokenText(value);
  const normalizedRoute = normalizeTokenText(stripRouteSuffix(String(value ?? '')));
  if (!normalized) {
    return false;
  }

  return normalized === queryInfo.normalized || normalizedRoute === queryInfo.normalized;
}

function arrayFieldExactlyMatches(value: any, queryInfo: any) {
  return toStringArray(value).some((item) => fieldExactlyMatches(item, queryInfo));
}

function fieldContainsQuery(value: any, queryInfo: any) {
  const normalized = normalizeTokenText(Array.isArray(value) ? value.join(' ') : value);
  if (!normalized || !queryInfo.normalized) {
    return false;
  }

  return normalized.includes(queryInfo.normalized)
    || queryInfo.tokens.length > 0 && queryInfo.tokens.every((token: string) => normalized.includes(token));
}

function stripRouteSuffix(value: string) {
  return value
    .replace(/\.html$/i, '')
    .replace(/\/index$/i, '')
    .replace(/\/readme(?:\.md)?$/i, '');
}

function normalizeTokenText(value: any) {
  return normalizeWhitespace(String(value ?? '')
    .replace(/([a-z])([A-Z])/g, '$1 $2')
    .toLowerCase()
    .replace(/[#_.:/\\-]+/g, ' ')
    .replace(/[^a-z0-9\s]+/g, ' '));
}

function normalizeWhitespace(value: string) {
  return value.replace(/\s+/g, ' ').trim();
}

function toStringArray(value: any) {
  return Array.isArray(value)
    ? value.map((item) => String(item ?? '').trim()).filter(Boolean)
    : [];
}

function normalizeFacetValue(value: any) {
  return String(value ?? '').trim();
}

export function normalizeCodeLanguage(value: any) {
  const normalized = normalizeFacetValue(value)
    .toLowerCase()
    .split(/[-_\s]+/)
    .filter(Boolean)
    .join('-');

  if (normalized === 'csharp' || normalized === 'c-sharp' || normalized === 'cs' || normalized === 'c#') {
    return 'csharp';
  }

  if (normalized === 'javascript' || normalized === 'java-script' || normalized === 'js') {
    return 'javascript';
  }

  return normalized;
}

export function normalizeCodeLanguageLabel(language: any, label: any) {
  const explicitLabel = normalizeFacetValue(label);
  if (explicitLabel) {
    return explicitLabel;
  }

  const normalized = normalizeCodeLanguage(language);
  if (normalized === 'csharp') {
    return 'C#';
  }

  if (normalized === 'javascript') {
    return 'JavaScript';
  }

  return normalized
    .split('-')
    .filter(Boolean)
    .map((segment) => segment === segment.toUpperCase() ? segment : `${segment.charAt(0).toUpperCase()}${segment.slice(1)}`)
    .join(' ');
}

export function buildLanguageSearchText(language: any, label: any) {
  const normalized = normalizeCodeLanguage(language);
  const displayLabel = normalizeCodeLanguageLabel(normalized, label);
  const terms = [normalized, displayLabel];

  if (normalized === 'csharp') {
    terms.push('csharp', 'CSharp', 'C-Sharp', 'C#');
  }

  if (normalized === 'javascript') {
    terms.push('javascript', 'JavaScript', 'js');
  }

  return [...new Set(terms.map((term) => String(term ?? '').trim()).filter(Boolean))].join(' ');
}

export function normalizePageTypeAlias(value: any) {
  const normalized = normalizeFacetValue(value)
    .toLowerCase()
    .split(/[-_\s]+/)
    .filter(Boolean)
    .join('-');

  if (normalized === 'api' || normalized === 'api-reference' || normalized === 'reference') {
    return 'api-reference';
  }

  if (normalized === 'release-note' || normalized === 'release-notes') {
    return 'release';
  }

  return normalized;
}
