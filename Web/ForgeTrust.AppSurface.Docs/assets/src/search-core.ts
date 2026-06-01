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
    order: Number.isFinite(orderValue) ? orderValue : null,
    relatedPages: toStringArray(doc?.relatedPages),
    breadcrumbs: toStringArray(doc?.breadcrumbs)
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
