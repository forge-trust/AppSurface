(() => {
  const rawConfig = window.__razorDocsConfig || {};
  const docsRootPath = normalizeDocsRootPath(rawConfig.docsRootPath || '/docs');
  const docsSearchUrl = rawConfig.docsSearchUrl || joinDocsPath(docsRootPath, 'search');
  const indexUrl = rawConfig.docsSearchIndexUrl || joinDocsPath(docsRootPath, 'search-index.json');
  const miniSearchUrl = rawConfig.miniSearchUrl || joinDocsPath(docsRootPath, 'minisearch.min.js');
  const maxQueryLength = 500;
  const topResults = 8;
  const fetchTimeoutMs = 10000;
  const docsFrameId = 'doc-content';
  const searchInputHash = '#docs-search-page-input';
  const enableDocsPartialRewrite = Boolean(
    document.querySelector('meta[name="rw-docs-static-partials"][content="1"]')
  );
  const defaultSearchOptions = {
    prefix: true,
    fuzzy: 0.1,
    boost: { title: 6, aliases: 4, headings: 3, keywords: 2, summary: 2, bodyText: 1 }
  };
  const facetDefinitions = [
    { key: 'pageType', label: 'Page Type', kind: 'chips' },
    { key: 'component', label: 'Component', kind: 'select' },
    { key: 'audience', label: 'Audience', kind: 'chips' },
    { key: 'status', label: 'Status', kind: 'chips' }
  ];
  const facetKeys = facetDefinitions.map((facet) => facet.key);
  const sidebarBoundAttribute = 'data-rw-search-sidebar-bound';
  const searchPageBoundAttribute = 'data-rw-search-page-bound';
  const shortcutsBoundAttribute = 'data-rw-search-shortcuts-bound';
  const popstateBoundFlag = '__rwDocsSearchPopstateBound';
  const mobileFilterListenerBoundFlag = '__rwDocsSearchMobileFilterListenerBound';
  const mobileFilterMedia = window.matchMedia ? window.matchMedia('(max-width: 767px)') : null;
  const pageTypeSort = new Map([
    ['guide', 0],
    ['concept', 1],
    ['tutorial', 2],
    ['example', 3],
    ['api-reference', 4],
    ['api', 4],
    ['troubleshooting', 5],
    ['release', 6]
  ]);
  const starterPageTypeGroups = [
    { key: 'guide', label: 'Guide', pageTypes: ['guide', 'concept', 'tutorial', 'start-here', 'how-to'] },
    { key: 'api-reference', label: 'API Reference', pageTypes: ['api-reference', 'api'] },
    { key: 'example', label: 'Example', pageTypes: ['example'] },
    { key: 'troubleshooting', label: 'Troubleshooting', pageTypes: ['troubleshooting'] },
    { key: 'release', label: 'Release', pageTypes: ['release'] }
  ];
  const statusSort = new Map([
    ['stable', 0],
    ['beta', 1],
    ['preview', 2],
    ['experimental', 3],
    ['deprecated', 4]
  ]);
  const searchData = {
    index: null,
    docs: [],
    sortedDocs: [],
    starterDocs: [],
    docsById: new Map(),
    docsByPath: collectInitialDocsPaths(),
    facetValues: createEmptyFacetValues(),
    loadPromise: null,
    runtimePromise: null
  };
  const searchPageState = {
    q: '',
    pageType: '',
    component: '',
    audience: '',
    status: '',
    filtersExpanded: false,
    loadState: 'idle'
  };

  function createEmptyFacetValues() {
    return {
      pageType: [],
      component: [],
      audience: [],
      status: []
    };
  }

  function normalizeDocsRootPath(path) {
    const value = String(path || '').trim();
    if (!value) {
      return '/docs';
    }

    const prefixed = value.startsWith('/') ? value : `/${value}`;
    return prefixed !== '/' && prefixed.endsWith('/') ? prefixed.slice(0, -1) : prefixed;
  }

  function joinDocsPath(root, leaf) {
    const normalizedLeaf = String(leaf || '').replace(/^\/+/, '');
    return root === '/' ? `/${normalizedLeaf}` : `${root}/${normalizedLeaf}`;
  }

  function getSidebarSearchElements() {
    return {
      input: document.getElementById('docs-search-input'),
      results: document.getElementById('docs-search-results'),
      status: document.getElementById('docs-search-status')
    };
  }

  function getSearchPageElements() {
    const root = document.getElementById('docs-search-page');
    return {
      root,
      input: document.getElementById('docs-search-page-input'),
      status: document.getElementById('docs-search-page-status'),
      filtersToggle: document.getElementById('docs-search-page-filters-toggle'),
      filtersPanel: document.getElementById('docs-search-page-filters-panel'),
      filters: document.getElementById('docs-search-page-filters'),
      activeFilters: document.getElementById('docs-search-page-active-filters'),
      starter: document.getElementById('docs-search-page-starter'),
      starterDocs: document.getElementById('docs-search-page-starter-docs'),
      failure: document.getElementById('docs-search-page-failure'),
      failureTemplate: document.getElementById('docs-search-page-failure-template'),
      resultsMeta: document.getElementById('docs-search-page-results-meta'),
      results: document.getElementById('docs-search-page-results'),
      suggestionButtons: root
        ? Array.from(root.querySelectorAll('[data-rw-search-suggestion]'))
        : []
    };
  }

  function toUrl(urlLike) {
    if (!urlLike) {
      return null;
    }

    try {
      if (urlLike instanceof URL) {
        return new URL(urlLike.toString());
      }

      return new URL(String(urlLike), window.location.href);
    } catch {
      return null;
    }
  }

  function normalizeComparablePath(path) {
    const value = String(path ?? '').trim();
    if (!value || value === '/') {
      return '/';
    }

    return value.endsWith('/') ? value.slice(0, -1) : value;
  }

  function isDocsPath(path) {
    return docsRootPath === '/'
      ? isKnownRootMountedDocsNavigationPath(path)
      : path === docsRootPath || path.startsWith(`${docsRootPath}/`);
  }

  function collectInitialDocsPaths() {
    const paths = new Set();
    const currentUrl = toUrl(window.location.href);
    const searchUrl = toUrl(docsSearchUrl);

    if (currentUrl?.pathname) {
      for (const candidate of getRootMountedDocsPathCandidates(currentUrl.pathname)) {
        paths.add(candidate);
      }
    }

    if (searchUrl?.pathname) {
      for (const candidate of getRootMountedDocsPathCandidates(searchUrl.pathname)) {
        paths.add(candidate);
      }
    }

    document
      .querySelectorAll(`a[data-turbo-frame="${docsFrameId}"][href]`)
      .forEach((anchor) => {
        const href = anchor.getAttribute('href');
        const path = toUrl(href)?.pathname;
        if (path) {
          for (const candidate of getRootMountedDocsPathCandidates(path)) {
            paths.add(candidate);
          }
        }
      });

    return paths;
  }

  function getRootMountedDocsPathCandidates(path) {
    const normalizedPath = normalizeComparablePath(path);
    const candidates = new Set([normalizedPath]);

    if (normalizedPath.endsWith('.md')) {
      candidates.add(`${normalizedPath}.html`);
    }

    if (normalizedPath.endsWith('.md.html')) {
      candidates.add(normalizedPath.slice(0, -'.html'.length));
    }

    if (normalizedPath.endsWith('/index.partial.html')) {
      const canonicalIndexPath = normalizedPath.slice(0, -'/index.partial.html'.length) || '/';
      candidates.add(canonicalIndexPath);
      candidates.add(joinDocsPath(canonicalIndexPath, 'index.html'));
    }

    if (normalizedPath.endsWith('.partial.html')) {
      const canonicalPath = normalizedPath.slice(0, -'.partial.html'.length) || '/';
      candidates.add(canonicalPath);

      if (canonicalPath !== '/' && !canonicalPath.endsWith('.html')) {
        candidates.add(`${canonicalPath}.html`);
      }

      if (canonicalPath.endsWith('/index')) {
        const withoutIndex = canonicalPath.slice(0, -'/index'.length) || '/';
        candidates.add(withoutIndex);
        candidates.add(joinDocsPath(withoutIndex, 'index.html'));
      }
    }

    return [...candidates].map(normalizeComparablePath);
  }

  function isKnownRootMountedDocsNavigationPath(path) {
    const candidates = getRootMountedDocsPathCandidates(path);
    const searchUrl = toUrl(docsSearchUrl);
    const normalizedSearchPath = normalizeComparablePath(searchUrl?.pathname);

    return candidates.some((candidate) => searchData.docsByPath.has(candidate) || candidate === normalizedSearchPath);
  }

  function getHeader(headers, name) {
    if (!headers) {
      return null;
    }

    if (headers instanceof Headers) {
      return headers.get(name);
    }

    if (typeof headers === 'object') {
      const targetName = String(name).toLowerCase();
      for (const key of Object.keys(headers)) {
        if (key.toLowerCase() === targetName) {
          return headers[key] ?? null;
        }
      }
    }

    return null;
  }

  function toDocsPartialUrl(urlLike) {
    const url = toUrl(urlLike);
    if (!url) {
      return null;
    }

    const path = url.pathname;

    if (!isDocsPath(path) || path === docsRootPath) {
      return null;
    }

    if (path.endsWith('.partial.html') || path.endsWith('/search-index.json')) {
      return null;
    }

    if (path.endsWith('/')) {
      url.pathname = `${path}index.partial.html`;
      return url;
    }

    if (path.endsWith('.html')) {
      url.pathname = `${path.slice(0, -5)}.partial.html`;
      return url;
    }

    url.pathname = `${path}.partial.html`;
    return url;
  }

  function toDocsCanonicalUrl(urlLike) {
    const url = toUrl(urlLike);
    if (!url) {
      return null;
    }

    const path = url.pathname;
    if (!isDocsPath(path) || !path.endsWith('.partial.html')) {
      return url;
    }

    let canonicalPath;
    if (path.endsWith('/index.partial.html')) {
      canonicalPath = path.slice(0, -'/index.partial.html'.length);
    } else {
      canonicalPath = path.slice(0, -'.partial.html'.length);
      if (canonicalPath.endsWith('/index')) {
        canonicalPath = canonicalPath.slice(0, -'/index'.length);
      }
    }

    url.pathname = canonicalPath || docsRootPath;
    return url;
  }

  function replaceBrowserUrl(urlLike) {
    const nextUrl = toUrl(urlLike);
    if (!nextUrl || nextUrl.origin !== window.location.origin) {
      return;
    }

    const currentUrl = new URL(window.location.href);
    if (
      currentUrl.pathname === nextUrl.pathname
      && currentUrl.search === nextUrl.search
      && currentUrl.hash === nextUrl.hash
    ) {
      return;
    }

    window.history.replaceState(
      window.history.state,
      '',
      `${nextUrl.pathname}${nextUrl.search}${nextUrl.hash}`
    );
  }

  function installDocsPartialHook() {
    if (!enableDocsPartialRewrite) {
      return;
    }

    document.addEventListener('turbo:before-fetch-request', (event) => {
      const targetFrame = event.target;
      const turboFrameHeader = getHeader(event.detail?.fetchOptions?.headers, 'Turbo-Frame');
      const isDocFrameRequest = (targetFrame && targetFrame.id === docsFrameId) || turboFrameHeader === docsFrameId;
      if (!isDocFrameRequest) {
        return;
      }

      const requestUrl = event.detail?.url;
      const canonicalUrl = toUrl(requestUrl);
      const partialUrl = toDocsPartialUrl(canonicalUrl);
      if (!partialUrl || !(requestUrl instanceof URL)) {
        return;
      }

      const docsFrame = (targetFrame && targetFrame.id === docsFrameId)
        ? targetFrame
        : document.getElementById(docsFrameId);
      if (docsFrame && canonicalUrl) {
        docsFrame.setAttribute('data-rw-canonical-url', canonicalUrl.toString());
      }

      requestUrl.pathname = partialUrl.pathname;
      requestUrl.search = partialUrl.search;
      requestUrl.hash = partialUrl.hash;
    });

    document.addEventListener('turbo:frame-load', (event) => {
      const frame = event.target;
      if (!frame || frame.id !== docsFrameId) {
        return;
      }

      const pendingCanonical = frame.getAttribute('data-rw-canonical-url');
      if (pendingCanonical) {
        frame.removeAttribute('data-rw-canonical-url');
        replaceBrowserUrl(pendingCanonical);
        if (docsRootPath === '/') {
          searchData.docsByPath = new Set([
            ...searchData.docsByPath,
            ...collectInitialDocsPaths()
          ]);
        }
        return;
      }

      const canonicalUrl = toDocsCanonicalUrl(window.location.href);
      if (canonicalUrl) {
        replaceBrowserUrl(canonicalUrl);
      }

      if (docsRootPath === '/') {
        searchData.docsByPath = new Set([
          ...searchData.docsByPath,
          ...collectInitialDocsPaths()
        ]);
      }
    });
  }

  function debounce(fn, delay) {
    let timer = null;
    return (...args) => {
      window.clearTimeout(timer);
      timer = window.setTimeout(() => fn(...args), delay);
    };
  }

  function escapeHtml(value) {
    return String(value ?? '')
      .replaceAll('&', '&amp;')
      .replaceAll('<', '&lt;')
      .replaceAll('>', '&gt;')
      .replaceAll('"', '&quot;')
      .replaceAll("'", '&#39;');
  }

  function escapeRegExp(value) {
    return String(value ?? '').replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  }

  function normalizeBadgeVariant(value) {
    const normalized = String(value ?? '')
      .trim()
      .toLowerCase()
      .replaceAll(/[^a-z0-9-]/g, '');

    return normalized || 'neutral';
  }

  function getPageTypeBadgeVariant(value) {
    return normalizeBadgeVariant(normalizePageTypeAlias(value));
  }

  function renderPageTypeBadge(item) {
    const label = String(item?.pageTypeLabel ?? '').trim();
    if (!label) {
      return '';
    }

    const variant = getPageTypeBadgeVariant(item?.pageTypeVariant);
    return `<span class="docs-page-badge docs-page-badge--${escapeHtml(variant)}">${escapeHtml(label)}</span>`;
  }

  function normalizeQuery(value) {
    return String(value ?? '').trim().slice(0, maxQueryLength);
  }

  function normalizeFacetValue(value) {
    return String(value ?? '').trim();
  }

  function normalizePageTypeAlias(value) {
    const normalized = normalizeFacetValue(value)
      .toLowerCase()
      .split(/[-_\s]+/)
      .filter(Boolean)
      .join('-');
    return normalized === 'release-note' || normalized === 'release-notes'
      ? 'release'
      : normalized;
  }

  function formatQueryForStatus(value) {
    return normalizeQuery(value).replace(/[\u0000-\u001f\u007f]/g, '').replace(/\s+/g, ' ');
  }

  function getErrorMessage(err) {
    const message = String(err?.message ?? '');
    if (message.includes('timed out')) {
      return 'Search index request timed out. Please retry.';
    }

    if (message.includes('MiniSearch runtime is not available')) {
      return 'Search is unavailable: runtime failed to load.';
    }

    if (message.includes('Failed to load search index')) {
      return 'Search index could not be loaded. Please retry.';
    }

    return 'Search is temporarily unavailable.';
  }

  function createElement(tagName, className, textContent) {
    const element = document.createElement(tagName);
    if (className) {
      element.className = className;
    }

    if (textContent !== undefined) {
      element.textContent = textContent;
    }

    return element;
  }

  function setStatus(node, text) {
    if (node) {
      node.textContent = text;
    }
  }

  function isVisibleInteractiveElement(element) {
    if (!(element instanceof HTMLElement)) {
      return false;
    }

    if (element.hidden || element.closest('[inert]') || element.closest('[aria-hidden="true"]')) {
      return false;
    }

    const styles = window.getComputedStyle(element);
    if (styles.display === 'none' || styles.visibility === 'hidden') {
      return false;
    }

    const rect = element.getBoundingClientRect();
    if (rect.width <= 0 || rect.height <= 0) {
      return false;
    }

    return rect.bottom > 0
      && rect.right > 0
      && rect.top < window.innerHeight
      && rect.left < window.innerWidth;
  }

  function isEditableElement(target) {
    if (!(target instanceof Element)) {
      return false;
    }

    return Boolean(target.closest('input, textarea, select, [contenteditable=""], [contenteditable="true"], [role="textbox"]'));
  }

  function isSearchInputElement(target) {
    if (!(target instanceof Element)) {
      return false;
    }

    return Boolean(target.closest('#docs-search-input, #docs-search-page-input'));
  }

  function isSearchPageVisible() {
    return Boolean(getSearchPageElements().root);
  }

  function getCurrentSearchQuery() {
    const page = getSearchPageElements();
    const pageQuery = normalizeQuery(page.input?.value);
    if (pageQuery) {
      return pageQuery;
    }

    return normalizeQuery(getSidebarSearchElements().input?.value);
  }

  function formatFacetValue(value) {
    const normalized = normalizeFacetValue(value);
    if (!normalized) {
      return '';
    }

    const pageTypeAlias = normalizePageTypeAlias(normalized);
    if (pageTypeAlias === 'api' || pageTypeAlias === 'api-reference') {
      return 'API Reference';
    }

    if (pageTypeAlias === 'release') {
      return 'Release';
    }

    return normalized
      .split(/[-_\s]+/)
      .filter(Boolean)
      .map((part) => part === part.toUpperCase() ? part : `${part.charAt(0).toUpperCase()}${part.slice(1)}`)
      .join(' ');
  }

  function isPageTypeInGroup(doc, group) {
    const pageType = normalizePageTypeAlias(doc?.pageType);
    return Boolean(pageType) && group.pageTypes.includes(pageType);
  }

  function getPageTypeDisplayLabel(doc) {
    const label = String(doc?.pageTypeLabel ?? '').trim();
    if (label) {
      return label;
    }

    return formatFacetValue(normalizePageTypeAlias(doc?.pageType));
  }

  function getPageTypeVariant(doc) {
    const variant = getPageTypeBadgeVariant(doc?.pageTypeVariant);
    return variant === 'neutral' && normalizePageTypeAlias(doc?.pageType) === 'release'
      ? 'release'
      : variant;
  }

  function getLocationLabel(item) {
    const context = item?.navGroup || item?.component || '';
    const path = item?.path || '';

    if (context && path) {
      return `${context} • ${path}`;
    }

    return context || path;
  }

  function normalizeSearchDocument(doc) {
    const toStringArray = (value) => Array.isArray(value)
      ? value.map((item) => String(item ?? '').trim()).filter(Boolean)
      : [];
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
      status: normalizeFacetValue(doc?.status),
      navGroup: String(doc?.navGroup ?? '').trim(),
      order: Number.isFinite(orderValue) ? orderValue : null,
      relatedPages: toStringArray(doc?.relatedPages),
      breadcrumbs: toStringArray(doc?.breadcrumbs)
    };
  }

  function sortFacetValues(key, values) {
    const list = [...values];
    if (key === 'pageType') {
      return list.sort((a, b) => (pageTypeSort.get(normalizePageTypeAlias(a)) ?? 100) - (pageTypeSort.get(normalizePageTypeAlias(b)) ?? 100) || a.localeCompare(b));
    }

    if (key === 'status') {
      return list.sort((a, b) => (statusSort.get(a.toLowerCase()) ?? 100) - (statusSort.get(b.toLowerCase()) ?? 100) || a.localeCompare(b));
    }

    return list.sort((a, b) => a.localeCompare(b));
  }

  function deriveFacetValues(docs) {
    const values = createEmptyFacetValues();
    for (const doc of docs) {
      for (const key of facetKeys) {
        const value = normalizeFacetValue(doc[key]);
        if (value) {
          values[key].push(value);
        }
      }
    }

    for (const key of facetKeys) {
      values[key] = sortFacetValues(key, [...new Set(values[key])]);
    }

    return values;
  }

  function getSearchFilters(state) {
    return {
      pageType: normalizePageTypeAlias(state.pageType),
      component: normalizeFacetValue(state.component),
      audience: normalizeFacetValue(state.audience),
      status: normalizeFacetValue(state.status)
    };
  }

  function hasActiveFilters(filters) {
    return facetKeys.some((key) => Boolean(filters[key]));
  }

  function matchesFilters(doc, filters, skipKey = null) {
    return facetKeys.every((key) => {
      if (key === skipKey) {
        return true;
      }

      const expected = normalizeFacetValue(filters[key]);
      if (!expected) {
        return true;
      }

      const actual = key === 'pageType'
        ? normalizePageTypeAlias(doc[key])
        : normalizeFacetValue(doc[key]);
      return actual === expected;
    });
  }

  function matchesStoredResult(result, filters) {
    return facetKeys.every((key) => {
      const expected = normalizeFacetValue(filters[key]);
      if (!expected) {
        return true;
      }

      const actual = key === 'pageType'
        ? normalizePageTypeAlias(result?.[key])
        : normalizeFacetValue(result?.[key]);
      return actual === expected;
    });
  }

  function compareBrowseDocs(left, right) {
    const leftTypeRank = pageTypeSort.get(normalizePageTypeAlias(left.pageType)) ?? 100;
    const rightTypeRank = pageTypeSort.get(normalizePageTypeAlias(right.pageType)) ?? 100;
    if (leftTypeRank !== rightTypeRank) {
      return leftTypeRank - rightTypeRank;
    }

    const leftOrder = left.order ?? Number.MAX_SAFE_INTEGER;
    const rightOrder = right.order ?? Number.MAX_SAFE_INTEGER;
    if (leftOrder !== rightOrder) {
      return leftOrder - rightOrder;
    }

    return left.title.localeCompare(right.title, undefined, { sensitivity: 'base' })
      || left.path.localeCompare(right.path, undefined, { sensitivity: 'base' });
  }

  function sortDocsForBrowse(docs) {
    return [...docs].sort(compareBrowseDocs);
  }

  function deriveStarterDocs(docs) {
    const starters = [];
    for (const group of starterPageTypeGroups) {
      const doc = docs.find((candidate) => isPageTypeInGroup(candidate, group));
      if (doc) {
        starters.push({ group, doc });
      }
    }

    return starters;
  }

  function readSearchPageStateFromUrl() {
    const params = new URLSearchParams(window.location.search);
    return {
      q: normalizeQuery(params.get('q')),
      pageType: normalizeFacetValue(params.get('pageType')),
      component: normalizeFacetValue(params.get('component')),
      audience: normalizeFacetValue(params.get('audience')),
      status: normalizeFacetValue(params.get('status'))
    };
  }

  function writeSearchPageUrl(historyMode = 'replace') {
    if (!isSearchPageVisible()) {
      return;
    }

    const url = new URL(window.location.href);
    const filters = getSearchFilters(searchPageState);
    const query = normalizeQuery(searchPageState.q);

    if (query) {
      url.searchParams.set('q', query);
    } else {
      url.searchParams.delete('q');
    }

    for (const key of facetKeys) {
      if (filters[key]) {
        url.searchParams.set(key, filters[key]);
      } else {
        url.searchParams.delete(key);
      }
    }

    url.hash = '';
    const nextUrl = `${url.pathname}${url.search}${url.hash}`;
    const currentUrl = `${window.location.pathname}${window.location.search}${window.location.hash}`;
    if (currentUrl === nextUrl) {
      return;
    }

    const historyState = window.history.state && typeof window.history.state === 'object'
      ? { ...window.history.state, rwDocsSearch: true }
      : { rwDocsSearch: true };

    if (historyMode === 'push') {
      window.history.pushState(historyState, '', nextUrl);
      return;
    }

    window.history.replaceState(historyState, '', nextUrl);
  }

  function consumeSearchPageAutofocusHash(page) {
    if (!page.input || window.location.hash !== searchInputHash) {
      return;
    }

    page.input.focus();
    page.input.select?.();

    const url = new URL(window.location.href);
    url.hash = '';
    window.history.replaceState(window.history.state, '', `${url.pathname}${url.search}`);
  }

  function focusVisibleSearchInput() {
    const page = getSearchPageElements();
    if (isVisibleInteractiveElement(page.input)) {
      page.input.focus();
      page.input.select?.();
      return true;
    }

    const sidebar = getSidebarSearchElements();
    if (isVisibleInteractiveElement(sidebar.input)) {
      sidebar.input.focus();
      sidebar.input.select?.();
      return true;
    }

    return false;
  }

  function activateSearchEntryPoint() {
    if (focusVisibleSearchInput()) {
      return true;
    }

    if (!isSearchPageVisible()) {
      navigateToSearchPageWithQuery(getCurrentSearchQuery());
      return true;
    }

    return false;
  }

  function shouldTargetDocsFrame(href) {
    const url = toUrl(href);
    if (!url || url.origin !== window.location.origin) {
      return false;
    }

    const normalizedPath = normalizeComparablePath(url.pathname);
    if (normalizedPath === docsRootPath) {
      return false;
    }

    if (docsRootPath !== '/') {
      return isDocsPath(normalizedPath);
    }

    return isKnownRootMountedDocsNavigationPath(normalizedPath);
  }

  function applyDocsNavigationTarget(anchor, href) {
    if (!(anchor instanceof HTMLAnchorElement)) {
      return;
    }

    if (shouldTargetDocsFrame(href)) {
      anchor.setAttribute('data-turbo-frame', docsFrameId);
      anchor.setAttribute('data-turbo-action', 'advance');
      return;
    }

    anchor.setAttribute('data-turbo-frame', '_top');
    anchor.removeAttribute('data-turbo-action');
  }

  function getDocsNavigationAttributes(href) {
    if (shouldTargetDocsFrame(href)) {
      return ` data-turbo-frame="${docsFrameId}" data-turbo-action="advance"`;
    }

    return ' data-turbo-frame="_top"';
  }

  function navigateToSearchPageWithQuery(query) {
    const url = new URL(docsSearchUrl, window.location.origin);
    const normalized = normalizeQuery(query);
    if (normalized) {
      url.searchParams.set('q', normalized);
    }

    url.hash = searchInputHash;
    window.location.assign(url.toString());
  }

  function getHighlightTokens(query) {
    return [...new Set(
      normalizeQuery(query)
        .split(/\s+/)
        .map((token) => token.trim())
        .filter((token) => token.length > 1)
    )].sort((left, right) => right.length - left.length);
  }

  function createHighlightedFragment(text, tokens) {
    const fragment = document.createDocumentFragment();
    const source = String(text ?? '');
    if (!source) {
      return fragment;
    }

    if (!tokens.length) {
      fragment.append(source);
      return fragment;
    }

    const regex = new RegExp(`(${tokens.map(escapeRegExp).join('|')})`, 'ig');
    let lastIndex = 0;

    for (const match of source.matchAll(regex)) {
      const index = match.index ?? 0;
      if (index > lastIndex) {
        fragment.append(source.slice(lastIndex, index));
      }

      const mark = createElement('mark');
      mark.textContent = match[0];
      fragment.append(mark);
      lastIndex = index + match[0].length;
    }

    if (lastIndex < source.length) {
      fragment.append(source.slice(lastIndex));
    }

    return fragment;
  }

  function buildBreadcrumbLabels(doc) {
    if (Array.isArray(doc.breadcrumbs) && doc.breadcrumbs.length > 0) {
      return doc.breadcrumbs;
    }

    const fallback = [];
    if (doc.navGroup) {
      fallback.push(doc.navGroup);
    }

    const path = String(doc.path ?? '');
    const pathPart = path.split('#')[0];
    const pathSegments = pathPart
      .replace(new RegExp(`^${escapeRegExp(docsRootPath)}\\/?`, 'i'), '')
      .split('/')
      .map((segment) => {
        try {
          return decodeURIComponent(segment);
        } catch {
          return segment;
        }
      })
      .filter(Boolean);

    if (pathSegments.length > 1) {
      fallback.push(pathSegments[pathSegments.length - 2]);
    }

    return [...new Set(fallback.filter(Boolean))];
  }

  function createSearchResultBadge(text, secondary = false) {
    const badge = createElement(
      'span',
      secondary ? 'docs-search-result-badge docs-search-result-badge-secondary' : 'docs-search-result-badge',
      text
    );
    return badge;
  }

  function createSearchResultPageTypeBadge(doc) {
    const label = getPageTypeDisplayLabel(doc);
    if (!label) {
      return null;
    }

    return createElement(
      'span',
      `docs-page-badge docs-page-badge--${getPageTypeVariant(doc)} docs-search-result-page-type-badge`,
      label
    );
  }

  function createSearchResultArticle(doc, queryTokens, options = {}) {
    const article = createElement(
      'article',
      options.starter
        ? 'docs-search-result docs-search-result-starter'
        : 'docs-search-result'
    );

    const breadcrumbs = buildBreadcrumbLabels(doc);
    if (breadcrumbs.length > 0) {
      const breadcrumbRow = createElement('div', 'docs-search-result-breadcrumbs');
      breadcrumbs.forEach((label, index) => {
        if (index > 0) {
          breadcrumbRow.append(createElement('span', 'docs-search-result-breadcrumb-separator', '/'));
        }

        breadcrumbRow.append(createElement('span', null, label));
      });
      article.append(breadcrumbRow);
    }

    const title = createElement('h2', 'docs-search-result-title');
    const link = createElement('a');
    link.href = doc.path;
    applyDocsNavigationTarget(link, doc.path);
    link.append(createHighlightedFragment(doc.title, queryTokens));
    title.append(link);
    article.append(title);

    const metaParts = [
      doc.navGroup,
      getPageTypeDisplayLabel(doc),
      doc.path
    ].map((part) => String(part ?? '').trim()).filter(Boolean);
    if (metaParts.length > 0) {
      article.append(createElement('p', 'docs-search-result-meta-line', [...new Set(metaParts)].join(' • ')));
    }

    const badgeRow = createElement('div', 'docs-search-result-badges');
    const pageTypeBadge = createSearchResultPageTypeBadge(doc);
    if (pageTypeBadge) {
      badgeRow.append(pageTypeBadge);
    }

    if (doc.component) {
      badgeRow.append(createSearchResultBadge(formatFacetValue(doc.component)));
    }

    if (doc.audience) {
      badgeRow.append(createSearchResultBadge(formatFacetValue(doc.audience), true));
    }

    if (doc.status) {
      badgeRow.append(createSearchResultBadge(formatFacetValue(doc.status), true));
    }

    if (badgeRow.childNodes.length > 0) {
      article.append(badgeRow);
    }

    const snippetText = doc.summary || doc.snippet || '';
    if (snippetText) {
      const snippet = createElement('p', 'docs-search-result-snippet');
      snippet.append(createHighlightedFragment(snippetText, queryTokens));
      article.append(snippet);
    }

    return article;
  }

  function createNoResultsRecovery(view) {
    const container = createElement('section', 'docs-search-page-no-results');
    container.append(createElement('h2', 'docs-search-page-section-title', 'No pages matched this exact combination.'));
    const activeSummary = [];
    if (view.normalizedQuery) {
      activeSummary.push(`query "${formatQueryForStatus(view.normalizedQuery)}"`);
    }

    for (const filter of view.activeFilters) {
      activeSummary.push(`${filter.label}: ${filter.displayValue}`);
    }

    if (activeSummary.length > 0) {
      container.append(createElement('p', 'docs-search-page-no-results-summary', `Current search: ${activeSummary.join(', ')}.`));
    }

    container.append(createElement('p', 'docs-search-page-starter-copy', 'Search worked, but this combination is too narrow. Try a broader query or follow one of these recovery paths.'));

    if (view.activeFilters.length > 0) {
      const clearButton = createElement('button', 'docs-search-page-clear-filters', 'Clear filters');
      clearButton.type = 'button';
      clearButton.dataset.rwClearAllFilters = 'true';
      container.append(clearButton);
    }

    const links = createElement('div', 'docs-search-page-no-results-links');
    view.recoveryLinks.forEach((link) => {
      const anchor = createElement('a', 'docs-search-page-no-results-link', link.title);
      anchor.href = link.href;
      applyDocsNavigationTarget(anchor, link.href);
      links.append(anchor);
    });
    container.append(links);
    return container;
  }

  function createLoadingSkeletons(count = 3) {
    const fragment = document.createDocumentFragment();
    for (let index = 0; index < count; index += 1) {
      const article = createElement('article', 'docs-search-result docs-search-result-skeleton');
      article.setAttribute('aria-hidden', 'true');
      article.append(createElement('div', 'docs-search-skeleton docs-search-skeleton-title'));
      article.append(createElement('div', 'docs-search-skeleton docs-search-skeleton-meta'));
      article.append(createElement('div', 'docs-search-skeleton docs-search-skeleton-line'));
      article.append(createElement('div', 'docs-search-skeleton docs-search-skeleton-line docs-search-skeleton-line-short'));
      fragment.append(article);
    }
    return fragment;
  }

  function selectRecoveryDoc(docs, predicate) {
    return sortDocsForBrowse(docs.filter(predicate))[0] ?? null;
  }

  function normalizeDocReference(value) {
    return String(value ?? '')
      .trim()
      .replace(new RegExp(`^${escapeRegExp(docsRootPath)}\\/?`, 'i'), '')
      .replace(/\.html$/i, '')
      .replace(/\.md$/i, '')
      .replace(/^\/+/, '')
      .toLowerCase();
  }

  function resolveRelatedDoc(reference) {
    const normalizedReference = normalizeDocReference(reference);
    if (!normalizedReference) {
      return null;
    }

    const directMatch = searchData.docsById.get(reference);
    if (directMatch) {
      return directMatch;
    }

    return searchData.docs.find((doc) => {
      const idReference = normalizeDocReference(doc.id);
      const pathReference = normalizeDocReference(doc.path);
      const titleReference = String(doc.title ?? '').trim().toLowerCase();
      return normalizedReference === idReference
        || normalizedReference === pathReference
        || normalizedReference === titleReference;
    }) ?? null;
  }

  function buildRecoveryLinks(sourceDocs) {
    const preferredDocs = sourceDocs.length > 0 ? sourceDocs : searchData.docs;
    const allDocs = searchData.docs;
    const links = [];

    const pushLink = (doc, title = null) => {
      if (!doc) {
        return;
      }

      if (links.some((link) => link.href === doc.path)) {
        return;
      }

      links.push({ title: title || doc.title, href: doc.path });
    };

    const pushRelatedLinks = (docs) => {
      for (const doc of docs.slice(0, 5)) {
        for (const reference of doc.relatedPages || []) {
          pushLink(resolveRelatedDoc(reference));
          if (links.length >= 3) {
            return;
          }
        }
      }
    };

    pushRelatedLinks(preferredDocs);

    if (links.length < 3) {
      pushLink(
        selectRecoveryDoc(
          preferredDocs,
          (doc) => ['guide', 'concept', 'tutorial', 'troubleshooting'].includes(normalizePageTypeAlias(doc.pageType))
            || doc.path.includes('/guides/')),
        'Browse guides');
      pushLink(
        selectRecoveryDoc(
          preferredDocs,
          (doc) => normalizePageTypeAlias(doc.pageType) === 'example'
            || doc.path.includes('/examples/')),
        'Open an example');
      pushLink(
        selectRecoveryDoc(
          preferredDocs,
          (doc) => ['api', 'api-reference'].includes(normalizePageTypeAlias(doc.pageType))
            || doc.path.includes('/Namespaces/')),
        'Explore API reference');
    }

    if (links.length < 3) {
      const namespacesPath = joinDocsPath(docsRootPath, 'Namespaces');
      const namespacesDoc = selectRecoveryDoc(
        allDocs,
        (doc) => doc.path.endsWith('/Namespaces.html')
          || normalizeComparablePath(doc.path) === normalizeComparablePath(namespacesPath));
      pushLink(namespacesDoc, 'Browse namespaces');
    }

    if (links.length < 3) {
      links.push({ title: 'Documentation index', href: docsRootPath });
    }

    return links;
  }

  function deriveFacetState(baseDocs, filters) {
    return facetDefinitions
      .map((facet) => {
        const selectedValue = normalizeFacetValue(filters[facet.key]);
        const siblingDocs = baseDocs.filter((doc) => matchesFilters(doc, filters, facet.key));
        const facetValues = [...searchData.facetValues[facet.key]];
        if (selectedValue && !facetValues.includes(selectedValue)) {
          facetValues.unshift(selectedValue);
        }

        const options = facetValues.map((value) => {
          const count = siblingDocs.filter((doc) => {
            const actual = facet.key === 'pageType'
              ? normalizePageTypeAlias(doc[facet.key])
              : normalizeFacetValue(doc[facet.key]);
            return actual === value;
          }).length;
          return {
            value,
            count,
            selected: value === selectedValue,
            disabled: count === 0 && value !== selectedValue
          };
        });

        if (options.length === 0) {
          return null;
        }

        return {
          ...facet,
          options,
          selectedValue
        };
      })
      .filter(Boolean);
  }

  function runRankedSearch(query, filters, maxResults = null) {
    if (!searchData.index) {
      return [];
    }

    const normalizedQuery = normalizeQuery(query);
    const filterFn = hasActiveFilters(filters)
      ? (result) => matchesStoredResult(result, filters)
      : undefined;

    let results;
    if (normalizedQuery) {
      results = searchData.index.search(normalizedQuery, {
        ...defaultSearchOptions,
        filter: filterFn
      });
    } else if (hasActiveFilters(filters)) {
      results = searchData.sortedDocs.filter((doc) => matchesFilters(doc, filters))
        .map((doc) => ({ id: doc.id }));
    } else {
      results = [];
    }

    if (typeof maxResults === 'number') {
      return results.slice(0, maxResults);
    }

    return results;
  }

  function buildSearchView() {
    const filters = getSearchFilters(searchPageState);
    const activeFilters = facetDefinitions
      .map((facet) => {
        const value = normalizeFacetValue(filters[facet.key]);
        if (!value) {
          return null;
        }

        return {
          key: facet.key,
          label: facet.label,
          value,
          displayValue: formatFacetValue(value)
        };
      })
      .filter(Boolean);
    const normalizedQuery = normalizeQuery(searchPageState.q);
    const isStarter = !normalizedQuery && activeFilters.length === 0;
    const baseResults = normalizedQuery
      ? runRankedSearch(normalizedQuery, createEmptyFacetValues())
      : [];
    const baseDocs = normalizedQuery
      ? baseResults.map((result) => searchData.docsById.get(result.id)).filter(Boolean)
      : searchData.sortedDocs;
    const resultDocs = normalizedQuery
      ? (activeFilters.length > 0
          ? baseDocs.filter((doc) => matchesFilters(doc, filters))
          : baseDocs)
      : (activeFilters.length > 0
          ? searchData.sortedDocs.filter((doc) => matchesFilters(doc, filters))
          : []);
    const orderedResultDocs = normalizedQuery ? resultDocs : resultDocs;

    return {
      normalizedQuery,
      activeFilters,
      facets: deriveFacetState(baseDocs, filters),
      resultDocs: orderedResultDocs,
      isStarter,
      starterDocs: searchData.starterDocs,
      recoveryLinks: buildRecoveryLinks(baseDocs)
    };
  }

  function ensureMiniSearchLoaded() {
    if (window.MiniSearch) {
      return Promise.resolve(window.MiniSearch);
    }

    if (!searchData.runtimePromise) {
      const runtimeScripts = Array.from(
        document.querySelectorAll('script[data-rw-search-runtime="minisearch"]:not([data-rw-search-failed="true"])')
      );

      if (runtimeScripts.length > 0) {
        const existingScript = runtimeScripts[0];

        searchData.runtimePromise = new Promise((resolve, reject) => {
          let settled = false;
          let timeoutId = 0;

          const cleanup = () => {
            existingScript.removeEventListener('load', onLoad);
            existingScript.removeEventListener('error', onError);
            if (timeoutId) {
              window.clearTimeout(timeoutId);
            }
          };

          const resolveIfAvailable = () => {
            if (settled) {
              return;
            }

            cleanup();
            if (window.MiniSearch) {
              settled = true;
              existingScript.dataset.rwSearchLoaded = 'true';
              resolve(window.MiniSearch);
              return;
            }

            settled = true;
            existingScript.dataset.rwSearchFailed = 'true';
            reject(new Error('MiniSearch runtime is not available.'));
          };

          const onLoad = () => {
            resolveIfAvailable();
          };

          const onError = () => {
            if (settled) {
              return;
            }

            settled = true;
            cleanup();
            existingScript.dataset.rwSearchFailed = 'true';
            reject(new Error('MiniSearch runtime is not available.'));
          };

          existingScript.addEventListener('load', onLoad, { once: true });
          existingScript.addEventListener('error', onError, { once: true });
          timeoutId = window.setTimeout(resolveIfAvailable, fetchTimeoutMs);

          if (window.MiniSearch
            || existingScript.dataset.rwSearchLoaded === 'true'
            || existingScript.readyState === 'complete'
            || existingScript.readyState === 'loaded') {
            resolveIfAvailable();
          }
        }).catch((error) => {
          searchData.runtimePromise = null;
          throw error;
        });
      } else {
        document
          .querySelectorAll('script[data-rw-search-runtime="minisearch"][data-rw-search-failed="true"]')
          .forEach((script) => script.remove());

        searchData.runtimePromise = new Promise((resolve, reject) => {
          const script = document.createElement('script');

          const cleanup = () => {
            script.removeEventListener('load', onLoad);
            script.removeEventListener('error', onError);
          };

          const onLoad = () => {
            cleanup();
            if (window.MiniSearch) {
              script.dataset.rwSearchLoaded = 'true';
              resolve(window.MiniSearch);
            } else {
              script.dataset.rwSearchFailed = 'true';
              reject(new Error('MiniSearch runtime is not available.'));
            }
          };

          const onError = () => {
            cleanup();
            script.dataset.rwSearchFailed = 'true';
            reject(new Error('MiniSearch runtime is not available.'));
          };

          script.addEventListener('load', onLoad, { once: true });
          script.addEventListener('error', onError, { once: true });
          script.src = miniSearchUrl;
          script.defer = true;
          script.dataset.rwSearchRuntime = 'minisearch';
          document.head.append(script);
        }).catch((error) => {
          searchData.runtimePromise = null;
          throw error;
        });
      }
    }

    return searchData.runtimePromise;
  }

  async function loadIndex() {
    await ensureMiniSearchLoaded();
    if (!window.MiniSearch) {
      throw new Error('MiniSearch runtime is not available.');
    }

    const controller = new AbortController();
    const timeout = window.setTimeout(() => controller.abort(), fetchTimeoutMs);
    let response;

    try {
      // Keep the request credential mode aligned with the layout preload so
      // the browser can reuse the preloaded response instead of warning.
      response = await fetch(indexUrl, { credentials: 'include', signal: controller.signal });
    } catch (error) {
      if (error instanceof DOMException && error.name === 'AbortError') {
        throw new Error('Search index request timed out.');
      }

      throw error;
    } finally {
      window.clearTimeout(timeout);
    }

    if (!response.ok) {
      throw new Error(`Failed to load search index: ${response.status}`);
    }

    const payload = await response.json();
    const docs = Array.isArray(payload.documents)
      ? payload.documents.map(normalizeSearchDocument).filter((doc) => doc.id && doc.path && doc.title)
      : [];

    const MiniSearch = window.MiniSearch;
    const index = new MiniSearch({
      fields: ['title', 'aliases', 'keywords', 'summary', 'headings', 'bodyText'],
      storeFields: ['id', 'path', 'title', 'snippet', 'summary', 'pageType', 'pageTypeLabel', 'pageTypeVariant', 'component', 'audience', 'status', 'navGroup'],
      searchOptions: defaultSearchOptions
    });

    index.addAll(docs.map((doc) => ({
      id: doc.id,
      path: doc.path,
      title: doc.title,
      aliases: doc.aliases.join(' '),
      keywords: doc.keywords.join(' '),
      summary: doc.summary,
      headings: doc.headings.join(' '),
      bodyText: doc.bodyText,
      snippet: doc.snippet,
      pageType: doc.pageType,
      pageTypeLabel: doc.pageTypeLabel ?? '',
      pageTypeVariant: doc.pageTypeVariant ?? '',
      component: doc.component,
      audience: doc.audience,
      status: doc.status,
      navGroup: doc.navGroup
    })));

    searchData.index = index;
    searchData.docs = docs;
    searchData.sortedDocs = sortDocsForBrowse(docs);
    searchData.starterDocs = deriveStarterDocs(searchData.sortedDocs);
    searchData.docsById = new Map(docs.map((doc) => [doc.id, doc]));
    searchData.docsByPath = new Set([
      ...searchData.docsByPath,
      ...docs.flatMap((doc) => getRootMountedDocsPathCandidates(toUrl(doc.path)?.pathname ?? doc.path))
    ]);
    searchData.facetValues = deriveFacetValues(docs);
  }

  function ensureSearchResourcesLoaded() {
    if (searchData.index) {
      return Promise.resolve();
    }

    if (!searchData.loadPromise) {
      searchData.loadPromise = loadIndex().catch((error) => {
        searchData.loadPromise = null;
        throw error;
      });
    }

    return searchData.loadPromise;
  }

  function clearSidebarResults(sidebar) {
    if (!sidebar.results) {
      return;
    }

    sidebar.results.innerHTML = '';
    sidebar.results.classList.add('hidden');
    sidebar.input?.removeAttribute('aria-activedescendant');
  }

  function renderSidebarMessage(sidebar, message) {
    if (!sidebar.results) {
      return;
    }

    sidebar.results.classList.remove('hidden');
    sidebar.results.innerHTML = `<li class="docs-search-empty" role="presentation">${escapeHtml(message)}</li>`;
    setStatus(sidebar.status, message);
  }

  function renderSidebarResults(sidebar, queryResults, query, activeIndex = -1) {
    const { input, results, status } = sidebar;
    if (!results) {
      return;
    }

    if (!query) {
      clearSidebarResults(sidebar);
      setStatus(status, '');
      return;
    }

    if (!queryResults.length) {
      renderSidebarMessage(sidebar, 'No matching docs found.');
      return;
    }

    results.classList.remove('hidden');
    results.innerHTML = queryResults.map((item, index) => {
      const selected = index === activeIndex ? 'true' : 'false';
      const pageTypeBadge = renderPageTypeBadge(item);
      const navigationAttributes = getDocsNavigationAttributes(item.path);
      return `<li id="docs-search-option-${index}" role="option" aria-selected="${selected}" tabindex="-1" class="docs-search-option" data-href="${escapeHtml(item.path)}">
        <a href="${escapeHtml(item.path)}"${navigationAttributes}>
          <span class="docs-search-option-title-row">
            <span class="docs-search-option-title">${escapeHtml(item.title)}</span>
            ${pageTypeBadge}
          </span>
          <span class="docs-search-option-path">${escapeHtml(getLocationLabel(item))}</span>
        </a>
      </li>`;
    }).join('');

    setStatus(status, `${queryResults.length} result(s).`);
    if (activeIndex >= 0 && input) {
      input.setAttribute('aria-activedescendant', `docs-search-option-${activeIndex}`);
    }
  }

  function setActiveSidebarOption(items, activeIndex, input) {
    items.forEach((item, index) => {
      const selected = index === activeIndex;
      item.setAttribute('aria-selected', selected ? 'true' : 'false');
      item.classList.toggle('active', selected);
    });

    if (activeIndex >= 0 && items[activeIndex] && input) {
      input.setAttribute('aria-activedescendant', items[activeIndex].id);
      items[activeIndex].scrollIntoView?.({ block: 'nearest', inline: 'nearest' });
      return;
    }

    input?.removeAttribute('aria-activedescendant');
  }

  function bindSidebar() {
    const sidebar = getSidebarSearchElements();
    const { input, results } = sidebar;
    if (!input || !results) {
      return;
    }

    if (input.getAttribute(sidebarBoundAttribute) === '1') {
      return;
    }

    input.setAttribute(sidebarBoundAttribute, '1');
    let activeIndex = -1;
    let lastRenderedQuery = '';

    const performSearch = debounce(async () => {
      const query = normalizeQuery(input.value);
      if (query !== input.value) {
        input.value = query;
      }

      if (!query) {
        activeIndex = -1;
        lastRenderedQuery = '';
        clearSidebarResults(sidebar);
        setStatus(sidebar.status, '');
        return;
      }

      try {
        if (!searchData.index) {
          renderSidebarMessage(sidebar, 'Loading search index...');
        }

        await ensureSearchResourcesLoaded();
        const queryResults = runRankedSearch(query, createEmptyFacetValues(), topResults);
        activeIndex = queryResults.length > 0 ? 0 : -1;
        renderSidebarResults(sidebar, queryResults, query, activeIndex);
        lastRenderedQuery = query;
      } catch (error) {
        activeIndex = -1;
        renderSidebarMessage(sidebar, getErrorMessage(error));
      }
    }, 120);

    input.addEventListener('focus', () => {
      ensureSearchResourcesLoaded().catch((error) => {
        if (normalizeQuery(input.value)) {
          renderSidebarMessage(sidebar, getErrorMessage(error));
        }
      });
    });

    input.addEventListener('input', () => {
      activeIndex = -1;
      performSearch();
    });

    input.addEventListener('keydown', async (event) => {
      const currentQuery = normalizeQuery(input.value);
      if (currentQuery && currentQuery !== lastRenderedQuery && searchData.index) {
        const refreshed = runRankedSearch(currentQuery, createEmptyFacetValues(), topResults);
        activeIndex = refreshed.length > 0 ? 0 : -1;
        renderSidebarResults(sidebar, refreshed, currentQuery, activeIndex);
        lastRenderedQuery = currentQuery;
      }

      const items = Array.from(results.querySelectorAll('[role="option"]'));
      if (!items.length) {
        return;
      }

      if (event.key === 'ArrowDown') {
        event.preventDefault();
        activeIndex = Math.min(activeIndex + 1, items.length - 1);
        setActiveSidebarOption(items, activeIndex, input);
      } else if (event.key === 'ArrowUp') {
        event.preventDefault();
        activeIndex = Math.max(activeIndex - 1, 0);
        setActiveSidebarOption(items, activeIndex, input);
      } else if (event.key === 'Enter') {
        if (activeIndex >= 0 && items[activeIndex]) {
          event.preventDefault();
          const anchor = items[activeIndex].querySelector('a');
          anchor?.click();
        }
      } else if (event.key === 'Escape') {
        clearSidebarResults(sidebar);
      }
    });
  }

  function syncSearchPageFilterPanel(page) {
    if (!page.filtersPanel || !page.filtersToggle) {
      return;
    }

    const isMobile = mobileFilterMedia ? mobileFilterMedia.matches : window.innerWidth < 768;
    page.filtersToggle.setAttribute('aria-expanded', isMobile && searchPageState.filtersExpanded ? 'true' : 'false');
    page.filtersPanel.hidden = isMobile && !searchPageState.filtersExpanded;
  }

  function handleMobileFilterChange() {
    const page = getSearchPageElements();
    if (!page.root) {
      return;
    }

    syncSearchPageFilterPanel(page);
  }

  function setSearchPageBusy(page, isBusy) {
    page.results?.setAttribute('aria-busy', isBusy ? 'true' : 'false');
  }

  function ensureSearchPageFailureContent(page) {
    if (!page.failure || page.failure.hasChildNodes()) {
      return;
    }

    if (page.failureTemplate instanceof HTMLTemplateElement) {
      page.failure.append(page.failureTemplate.content.cloneNode(true));
      return;
    }

    page.failure.append(createElement('h2', 'docs-search-page-section-title', 'Search is temporarily unavailable'));
  }

  function clearSearchPageFailureContent(page) {
    page.failure?.replaceChildren();
  }

  function renderSearchPageFilters(page, view) {
    if (!page.filters) {
      return;
    }

    const fragment = document.createDocumentFragment();
    view.facets.forEach((facet, index) => {
      const group = createElement('section', 'docs-search-page-filter-group');
      const heading = createElement('h2', 'docs-search-page-filter-label', facet.label);
      const headingId = `docs-search-page-filter-label-${facet.key || `facet-${index}`}`;
      heading.id = headingId;
      group.append(heading);

      if (facet.kind === 'select') {
        const select = createElement('select', 'docs-search-page-select');
        select.id = `docs-search-page-select-${facet.key || `facet-${index}`}`;
        select.setAttribute('aria-labelledby', headingId);
        select.dataset.rwFacetKey = facet.key;
        const emptyOption = createElement('option', null, `All ${facet.label}`);
        emptyOption.value = '';
        select.append(emptyOption);

        facet.options.forEach((option) => {
          const optionElement = createElement('option', null, `${formatFacetValue(option.value)}${option.count > 0 ? ` (${option.count})` : ''}`);
          optionElement.value = option.value;
          optionElement.selected = option.selected;
          optionElement.disabled = option.disabled;
          select.append(optionElement);
        });

        group.append(select);
      } else {
        const row = createElement('div', 'docs-search-page-chip-row');
        facet.options.forEach((option) => {
          const chip = createElement('button', 'docs-search-page-chip', formatFacetValue(option.value));
          chip.type = 'button';
          chip.dataset.rwFacetKey = facet.key;
          chip.dataset.rwFacetValue = option.value;
          chip.setAttribute('aria-pressed', option.selected ? 'true' : 'false');
          chip.disabled = option.disabled;
          row.append(chip);
        });
        group.append(row);
      }

      fragment.append(group);
    });

    page.filters.replaceChildren(fragment);
  }

  function renderSearchPageActiveFilters(page, view) {
    if (!page.activeFilters) {
      return;
    }

    if (view.activeFilters.length === 0) {
      page.activeFilters.hidden = true;
      page.activeFilters.replaceChildren();
      return;
    }

    const fragment = document.createDocumentFragment();
    view.activeFilters.forEach((filter) => {
      const pill = createElement('div', 'docs-search-page-active-filter');
      pill.append(createElement('span', null, `${filter.label}: ${filter.displayValue}`));
      const button = createElement('button', null, 'Clear');
      button.type = 'button';
      button.dataset.rwClearFacetKey = filter.key;
      button.setAttribute('aria-label', `Clear ${filter.label} filter${filter.displayValue ? `: ${filter.displayValue}` : ''}`);
      pill.append(button);
      fragment.append(pill);
    });

    page.activeFilters.hidden = false;
    page.activeFilters.replaceChildren(fragment);
  }

  function renderSearchPageStarterDocs(page, view) {
    if (!page.starterDocs) {
      return;
    }

    if (!view.isStarter || view.starterDocs.length === 0) {
      page.starterDocs.hidden = true;
      page.starterDocs.replaceChildren();
      return;
    }

    const fragment = document.createDocumentFragment();
    fragment.append(createElement('h2', 'docs-search-page-starter-heading', 'Browse the index'));

    const list = createElement('div', 'docs-search-page-starter-list');
    for (const starter of view.starterDocs) {
      const article = createSearchResultArticle(starter.doc, [], { starter: true });
      article.dataset.rwStarterPageType = starter.group.key;
      list.append(article);
    }

    fragment.append(list);
    page.starterDocs.replaceChildren(fragment);
    page.starterDocs.hidden = false;
  }

  function renderSearchPageResults(page, view) {
    if (!page.results || !page.resultsMeta) {
      return;
    }

    const queryTokens = getHighlightTokens(view.normalizedQuery);

    if (view.isStarter) {
      page.resultsMeta.hidden = true;
      page.results.replaceChildren();
      return;
    }

    if (view.resultDocs.length === 0) {
      const descriptor = view.normalizedQuery
        ? `No results for "${formatQueryForStatus(view.normalizedQuery)}".`
        : 'No results for the current filters.';
      page.resultsMeta.hidden = false;
      page.resultsMeta.textContent = descriptor;
      page.results.replaceChildren(createNoResultsRecovery(view));
      return;
    }

    const fragment = document.createDocumentFragment();
    view.resultDocs.forEach((doc) => {
      fragment.append(createSearchResultArticle(doc, queryTokens));
    });

    page.resultsMeta.hidden = false;
    page.resultsMeta.textContent = view.normalizedQuery
      ? `${view.resultDocs.length} result(s) for "${formatQueryForStatus(view.normalizedQuery)}".`
      : `${view.resultDocs.length} page(s) for the current filters.`;
    page.results.replaceChildren(fragment);
  }

  function renderSearchPage() {
    const page = getSearchPageElements();
    if (!page.root || !page.status || !page.results) {
      return;
    }

    syncSearchPageFilterPanel(page);

    if (searchPageState.loadState === 'loading' && !searchData.index) {
      setStatus(page.status, 'Loading search index...');
      clearSearchPageFailureContent(page);
      page.failure.hidden = true;
      page.starter.hidden = true;
      page.starterDocs?.replaceChildren();
      if (page.starterDocs) {
        page.starterDocs.hidden = true;
      }
      page.resultsMeta.hidden = true;
      page.results.replaceChildren(createLoadingSkeletons());
      setSearchPageBusy(page, true);
      return;
    }

    if (searchPageState.loadState === 'error' && !searchData.index) {
      setStatus(page.status, 'Search is temporarily unavailable.');
      ensureSearchPageFailureContent(page);
      page.failure.hidden = false;
      page.starter.hidden = true;
      page.starterDocs?.replaceChildren();
      if (page.starterDocs) {
        page.starterDocs.hidden = true;
      }
      page.resultsMeta.hidden = true;
      page.results.replaceChildren();
      setSearchPageBusy(page, false);
      return;
    }

    if (!searchData.index) {
      return;
    }

    const view = buildSearchView();
    renderSearchPageFilters(page, view);
    renderSearchPageActiveFilters(page, view);
    renderSearchPageStarterDocs(page, view);
    clearSearchPageFailureContent(page);
    page.failure.hidden = true;
    page.starter.hidden = !view.isStarter;
    setSearchPageBusy(page, false);

    if (view.isStarter) {
      setStatus(page.status, 'Search is ready. Try a starter query or browse by filter.');
      page.resultsMeta.hidden = true;
      page.results.replaceChildren();
      return;
    }

    setStatus(page.status, view.resultDocs.length > 0
      ? 'Search results updated.'
      : 'Try loosening one filter or using a broader term.');
    renderSearchPageResults(page, view);
  }

  function setSearchPageState(nextState, historyMode = 'replace') {
    Object.assign(searchPageState, nextState);

    const page = getSearchPageElements();
    if (page.input && searchPageState.q !== page.input.value) {
      page.input.value = searchPageState.q;
    }

    if (historyMode !== 'none') {
      writeSearchPageUrl(historyMode);
    }

    renderSearchPage();
  }

  async function loadSearchPageData() {
    if (searchData.index) {
      searchPageState.loadState = 'ready';
      renderSearchPage();
      return;
    }

    searchPageState.loadState = 'loading';
    renderSearchPage();

    try {
      await ensureSearchResourcesLoaded();
      searchPageState.loadState = 'ready';
      renderSearchPage();
    } catch (error) {
      console.error(error);
      searchPageState.loadState = 'error';
      renderSearchPage();
    }
  }

  function bindSearchPage() {
    const page = getSearchPageElements();
    const { root, input, filters, activeFilters, failure, filtersToggle, results, suggestionButtons } = page;
    if (!root || !input || !filters || !activeFilters || !failure || !filtersToggle) {
      return;
    }

    if (root.getAttribute(searchPageBoundAttribute) === '1') {
      return;
    }

    root.setAttribute(searchPageBoundAttribute, '1');
    Object.assign(searchPageState, readSearchPageStateFromUrl());
    searchPageState.loadState = searchData.index ? 'ready' : 'loading';
    input.value = searchPageState.q;

    const onInput = debounce(() => {
      const query = normalizeQuery(input.value);
      setSearchPageState({ q: query }, 'replace');
    }, 140);

    input.addEventListener('input', () => {
      if (searchData.index) {
        setSearchPageBusy(page, true);
      }
      onInput();
    });

    filters.addEventListener('click', (event) => {
      const chip = event.target.closest('[data-rw-facet-key][data-rw-facet-value]');
      if (!(chip instanceof HTMLButtonElement)) {
        return;
      }

      const key = chip.dataset.rwFacetKey;
      const value = normalizeFacetValue(chip.dataset.rwFacetValue);
      if (!key) {
        return;
      }

      const nextValue = normalizeFacetValue(searchPageState[key]) === value ? '' : value;
      setSearchPageState({ [key]: nextValue }, 'push');
    });

    filters.addEventListener('change', (event) => {
      const select = event.target;
      if (!(select instanceof HTMLSelectElement) || !select.dataset.rwFacetKey) {
        return;
      }

      setSearchPageState({ [select.dataset.rwFacetKey]: normalizeFacetValue(select.value) }, 'push');
    });

    activeFilters.addEventListener('click', (event) => {
      const button = event.target.closest('[data-rw-clear-facet-key]');
      if (!(button instanceof HTMLButtonElement)) {
        return;
      }

      const key = button.dataset.rwClearFacetKey;
      if (!key) {
        return;
      }

      setSearchPageState({ [key]: '' }, 'push');
    });

    results?.addEventListener('click', (event) => {
      const button = event.target.closest('[data-rw-clear-all-filters]');
      if (!(button instanceof HTMLButtonElement)) {
        return;
      }

      setSearchPageState({ pageType: '', component: '', audience: '', status: '' }, 'push');
    });

    failure.addEventListener('click', (event) => {
      const target = event.target;
      if (!(target instanceof Element)) {
        return;
      }

      const retry = target.closest('#docs-search-page-retry');
      if (!(retry instanceof HTMLButtonElement)) {
        return;
      }

      loadSearchPageData().catch((error) => console.error(error));
    });

    filtersToggle.addEventListener('click', () => {
      searchPageState.filtersExpanded = !searchPageState.filtersExpanded;
      syncSearchPageFilterPanel(page);
    });

    suggestionButtons.forEach((button) => {
      button.addEventListener('click', () => {
        const query = normalizeQuery(button.dataset.rwSearchSuggestion);
        setSearchPageState({ q: query }, 'push');
        input.focus();
        input.select?.();
      });
    });

    if (mobileFilterMedia && !window[mobileFilterListenerBoundFlag]) {
      mobileFilterMedia.addEventListener('change', handleMobileFilterChange);
      window[mobileFilterListenerBoundFlag] = '1';
    }

    if (!window[popstateBoundFlag]) {
      window.addEventListener('popstate', () => {
        const nextPage = getSearchPageElements();
        if (!nextPage.root) {
          return;
        }

        Object.assign(searchPageState, readSearchPageStateFromUrl());
        searchPageState.loadState = searchData.index
          ? 'ready'
          : (searchData.loadPromise ? 'loading' : 'error');
        nextPage.input.value = searchPageState.q;
        renderSearchPage();
      });
      window[popstateBoundFlag] = '1';
    }

    renderSearchPage();
    consumeSearchPageAutofocusHash(page);
    loadSearchPageData().catch((error) => console.error(error));
  }

  function bindGlobalSearchShortcuts() {
    if (document.documentElement.getAttribute(shortcutsBoundAttribute) === '1') {
      return;
    }

    document.documentElement.setAttribute(shortcutsBoundAttribute, '1');
    document.addEventListener('keydown', (event) => {
      if (event.defaultPrevented) {
        return;
      }

      const editableTarget = isEditableElement(event.target);
      const searchInputTarget = isSearchInputElement(event.target);

      if (!event.metaKey && !event.ctrlKey && !event.altKey && event.key === '/') {
        if (editableTarget) {
          return;
        }

        if (activateSearchEntryPoint()) {
          event.preventDefault();
        }
        return;
      }

      if ((event.metaKey || event.ctrlKey) && !event.shiftKey && String(event.key).toLowerCase() === 'k') {
        if (editableTarget && !searchInputTarget) {
          return;
        }

        event.preventDefault();
        if (isSearchPageVisible()) {
          focusVisibleSearchInput();
        } else {
          navigateToSearchPageWithQuery(getCurrentSearchQuery());
        }
      }
    });
  }

  function initOnTurboFrameLoad(event) {
    const frame = event.target;
    if (!(frame instanceof Element) || frame.id !== docsFrameId) {
      return;
    }

    init().catch((error) => console.error('Search init failed:', error));
  }

  async function init() {
    bindGlobalSearchShortcuts();
    bindSidebar();
    bindSearchPage();
  }

  installDocsPartialHook();
  const hasTurbo = typeof window !== 'undefined'
    && (Object.prototype.hasOwnProperty.call(window, 'Turbo')
      || typeof window.Turbo !== 'undefined'
      || document.documentElement.hasAttribute('data-turbo'));
  let skipNextTurboLoadInit = false;

  function runInit() {
    init().catch((error) => console.error('Search init failed:', error));
  }

  function isCurrentSearchDomBound() {
    const sidebar = getSidebarSearchElements();
    if (sidebar.input?.getAttribute(sidebarBoundAttribute) === '1') {
      return true;
    }

    const page = getSearchPageElements();
    return page.root?.getAttribute(searchPageBoundAttribute) === '1';
  }

  if (hasTurbo) {
    document.addEventListener('turbo:load', () => {
      if (skipNextTurboLoadInit && isCurrentSearchDomBound()) {
        skipNextTurboLoadInit = false;
        return;
      }

      skipNextTurboLoadInit = false;
      runInit();
    });
    document.addEventListener('turbo:frame-load', initOnTurboFrameLoad);

    // The initial page load still needs an eager bootstrap so search works
    // before Turbo emits its first load event for the document.
    if (document.readyState === 'loading') {
      skipNextTurboLoadInit = true;
      document.addEventListener('DOMContentLoaded', runInit, { once: true });
    } else if (document.readyState === 'interactive') {
      skipNextTurboLoadInit = true;
      runInit();
    } else {
      runInit();
    }
  } else if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', runInit, { once: true });
  } else {
    runInit();
  }
})();
