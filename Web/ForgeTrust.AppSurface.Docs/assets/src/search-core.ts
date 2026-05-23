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
  return normalizeFacetValue(value)
    .toLowerCase()
    .split(/[-_\s]+/)
    .filter(Boolean)
    .join('-');
}

export function normalizeCodeLanguageLabel(language: any, label: any) {
  const explicitLabel = normalizeFacetValue(label);
  if (explicitLabel) {
    return explicitLabel;
  }

  const normalized = normalizeCodeLanguage(language);
  if (normalized === 'csharp' || normalized === 'c-sharp' || normalized === 'cs') {
    return 'C#';
  }

  if (normalized === 'javascript' || normalized === 'java-script' || normalized === 'js') {
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

  if (normalized === 'csharp' || normalized === 'c-sharp' || normalized === 'cs') {
    terms.push('csharp', 'CSharp', 'C-Sharp', 'C#');
  }

  if (normalized === 'javascript' || normalized === 'java-script' || normalized === 'js') {
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
