export const searchFields = ['title', 'aliases', 'keywords', 'summary', 'headings', 'bodyText', 'entryPoints'];

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
  'audience',
  'status',
  'navGroup'
];

export const defaultSearchOptions = {
  prefix: true,
  fuzzy: 0.1,
  boost: { title: 6, aliases: 4, headings: 3, keywords: 2, summary: 2, entryPoints: 2, bodyText: 1 }
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
    summary: doc.summary,
    headings: doc.headings.join(' '),
    bodyText: doc.bodyText,
    snippet: doc.snippet,
    breadcrumbs: doc.breadcrumbs,
    pageType: doc.pageType,
    pageTypeLabel: doc.pageTypeLabel ?? '',
    pageTypeVariant: doc.pageTypeVariant ?? '',
    component: doc.component,
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

    const key = normalized.toLocaleLowerCase();
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

function normalizePageTypeAlias(value: any) {
  const normalized = normalizeFacetValue(value).toLowerCase();
  if (normalized === 'api' || normalized === 'api-reference' || normalized === 'reference') {
    return 'api-reference';
  }

  if (normalized === 'release-note' || normalized === 'release-notes') {
    return 'release';
  }

  return normalized;
}
