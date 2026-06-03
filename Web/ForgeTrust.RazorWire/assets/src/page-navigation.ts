interface Window {
    RazorWirePageNavigationInitialized?: boolean;
    RazorWire?: {
        config?: Record<string, unknown>;
        connectionManager?: unknown;
        localTimeFormatter?: unknown;
        formFailureManager?: unknown;
        pageNavigationManager?: unknown;
    };
}

interface PageNavigationDiagnostic {
    message: string;
    impact: string;
    fix: string;
    docs: string;
}

interface PageNavigationEntry {
    link: Element;
    target: HTMLElement;
}

type PageNavigationScrollRoot = Window | Element;

(function () {
    if (window.RazorWirePageNavigationInitialized) return;
    window.RazorWirePageNavigationInitialized = true;

    class PageNavigationManager {
        controllers = new Map<Element, PageNavigationController>();
        diagnostics: PageNavigationDiagnostic[] = [];
        isStarted = false;
        resizeListener = () => this.scheduleActiveLinkVisibilitySync();

        start() {
            if (this.isStarted) return;
            this.isStarted = true;
            this.scan();
            document.addEventListener('turbo:render', () => this.scan());
            document.addEventListener('turbo:load', () => this.scan());
            document.addEventListener('turbo:frame-load', () => this.scan());
            window.addEventListener?.('hashchange', () => this.refreshActiveFromHash());
            window.addEventListener?.('popstate', () => this.refreshActiveFromHash());
            window.addEventListener?.('resize', this.resizeListener);
        }

        scan() {
            document.querySelectorAll('[data-rw-page-nav]').forEach(root => this.register(root));
            this.prune();
        }

        register(root: Element) {
            const existing = this.controllers.get(root);
            if (existing) {
                existing.refresh();
                return;
            }

            const controller = new PageNavigationController(root, this);
            this.controllers.set(root, controller);
            controller.connect();
        }

        prune() {
            for (const [root, controller] of this.controllers) {
                if (!root.isConnected) {
                    controller.disconnect();
                    this.controllers.delete(root);
                }
            }
        }

        refreshActiveFromHash() {
            for (const controller of this.controllers.values()) {
                if (!controller.refreshActiveFromHash()) {
                    controller.refreshActiveFromViewport();
                }
            }
        }

        syncActiveLinkVisibility() {
            for (const controller of this.controllers.values()) {
                controller.syncActiveLinkVisibility();
            }
        }

        scheduleActiveLinkVisibilitySync() {
            for (const controller of this.controllers.values()) {
                controller.scheduleActiveLinkVisibilitySync();
            }
        }

        getDiagnostics() {
            return [...this.diagnostics];
        }

        clearDiagnostics() {
            this.diagnostics.length = 0;
        }

        recordDiagnostic(message: string, impact: string, fix: string) {
            const diagnostic = {
                message,
                impact,
                fix,
                docs: 'Web/ForgeTrust.RazorWire/Docs/page-navigation.md#troubleshooting'
            };
            if (this.diagnostics.some(existing => existing.message === message && existing.fix === fix)) return;
            this.diagnostics.push(diagnostic);

            if (window.RazorWire?.config?.developmentDiagnostics === true && typeof console?.warn === 'function') {
                console.warn(`RazorWire page navigation: ${message} Impact: ${impact} Fix: ${fix} Docs: ${diagnostic.docs}`);
            }
        }
    }

    class PageNavigationController {
        private static readonly activationOffset = 64;
        private static readonly hashBoundaryBefore = 16;
        private static readonly hashBoundaryAfter = 160;

        entries: PageNavigationEntry[] = [];
        activeLink: Element | null = null;
        scrollRoot: PageNavigationScrollRoot | null = null;
        activeLinkAnimationFrame = 0;
        activeLinkVisibilityAnimationFrame = 0;
        activeLinkVisibilityContainer: Element | null = null;
        activeLinkVisibilityObserver: ResizeObserver | null = null;
        rootClickListener = (event: Event) => this.handleRootClick(event);
        toggleClickListener = (event: Event) => this.handleToggleClick(event);
        scrollListener = () => this.scheduleActiveRefresh();
        activeLinkVisibilityResizeListener = () => this.scheduleActiveLinkVisibilitySync();

        constructor(
            private readonly root: Element,
            private readonly manager: PageNavigationManager) {
        }

        connect() {
            this.root.setAttribute('data-rw-page-nav-enhanced', 'true');
            this.refresh();
            this.root.addEventListener?.('click', this.rootClickListener);
            this.root.addEventListener?.('click', this.toggleClickListener);
        }

        disconnect() {
            this.root.removeAttribute('data-rw-page-nav-enhanced');
            this.root.removeEventListener?.('click', this.rootClickListener);
            this.root.removeEventListener?.('click', this.toggleClickListener);
            this.unbindScrollRoot();
            this.cancelActiveRefresh();
            this.cancelActiveLinkVisibilitySync();
            this.unbindActiveLinkVisibilityObserver();
        }

        refresh() {
            this.entries = Array.from(this.root.querySelectorAll('[data-rw-page-nav-link]'))
                .filter(link => link.tagName === 'A')
                .map(link => ({ link, target: this.resolveTarget(link) }))
                .filter((entry): entry is PageNavigationEntry => entry.target !== null);
            this.bindScrollRoot();
            this.syncPanelState();
            this.refreshActiveFromHash() || this.refreshActiveFromViewport();
            this.scheduleActiveLinkVisibilitySync();
        }

        refreshActiveFromHash() {
            const entry = this.getActiveEntryFromHash();
            if (!entry) return false;
            this.setActiveLink(entry.link);
            return true;
        }

        refreshActiveFromViewport() {
            if (this.entries.length === 0) {
                this.setActiveLink(null);
                return;
            }

            const hashEntry = this.getActiveEntryFromHash();
            if (hashEntry) {
                const hashRect = hashEntry.target.getBoundingClientRect?.();
                const rootTop = this.getScrollRootTop();
                const hashBoundaryAfter = Math.max(
                    PageNavigationController.hashBoundaryAfter,
                    this.getActivationOffset(hashEntry.target) + PageNavigationController.hashBoundaryBefore);
                if (hashRect && hashRect.top >= rootTop - PageNavigationController.hashBoundaryBefore && hashRect.top <= rootTop + hashBoundaryAfter) {
                    this.setActiveLink(hashEntry.link);
                    return;
                }
            }

            let active = this.entries[0];
            for (const entry of this.entries) {
                const rect = entry.target.getBoundingClientRect?.();
                const activationTop = this.getScrollRootTop() + this.getActivationOffset(entry.target);
                if (!rect || rect.top > activationTop) break;
                active = entry;
            }

            this.setActiveLink(active.link);
        }

        getActiveEntryFromHash() {
            const id = this.decodeHash(window.location.hash || '');
            return id ? this.entries.find(candidate => candidate.target.id === id) ?? null : null;
        }

        setActiveLink(link: Element | null) {
            if (this.activeLink === link) {
                this.scheduleActiveLinkVisibilitySync();
                return;
            }

            for (const entry of this.entries) {
                entry.link.removeAttribute('aria-current');
                entry.link.removeAttribute('data-rw-page-nav-active');
            }

            this.activeLink = link;
            link?.setAttribute('aria-current', 'location');
            link?.setAttribute('data-rw-page-nav-active', 'true');
            this.root.dispatchEvent(new CustomEvent('razorwire:page-nav:active-change', {
                bubbles: true,
                detail: { link }
            }));
            this.scheduleActiveLinkVisibilitySync();
        }

        handleRootClick(event: Event) {
            const mouse = event as MouseEvent;
            if (event.defaultPrevented || mouse.button !== 0 || mouse.metaKey || mouse.ctrlKey || mouse.shiftKey || mouse.altKey) return;
            const target = event.target;
            if (!(target instanceof Element)) return;

            const link = target.closest('a[data-rw-page-nav-link]');
            if (!link || !this.root.contains(link) || link.getAttribute('download') !== null) return;
            const targetAttr = link.getAttribute('target');
            if (targetAttr && targetAttr !== '_self') return;

            const entry = this.entries.find(candidate => candidate.link === link);
            if (!entry) return;

            const href = link.getAttribute('href') || '';
            let targetUrl: URL;
            try {
                targetUrl = new URL(href, window.location.href);
            } catch {
                return;
            }

            const shouldUpdateHistory = window.location.href !== targetUrl.href;
            const canUpdateHistory = typeof window.history?.pushState === 'function';
            if (shouldUpdateHistory && !canUpdateHistory) {
                this.setActiveLink(link);
                this.setPanelOpen(false);
                return;
            }

            event.preventDefault();
            if (shouldUpdateHistory) {
                window.history.pushState(null, '', targetUrl.hash);
            }

            const reduceMotion = window.matchMedia?.('(prefers-reduced-motion: reduce)')?.matches ?? false;
            this.scrollEntryIntoView(entry, reduceMotion ? 'auto' : 'smooth');
            this.setActiveLink(link);
            this.setPanelOpen(false);
        }

        scheduleActiveRefresh() {
            if (this.activeLinkAnimationFrame !== 0) return;
            const requestFrame = window.requestAnimationFrame?.bind(window)
                ?? ((callback: FrameRequestCallback) => window.setTimeout?.(() => callback(Date.now()), 16) ?? setTimeout(() => callback(Date.now()), 16));
            this.activeLinkAnimationFrame = requestFrame(() => {
                this.activeLinkAnimationFrame = 0;
                this.refreshActiveFromViewport();
            });
        }

        cancelActiveRefresh() {
            if (this.activeLinkAnimationFrame === 0) return;
            const cancelFrame = window.cancelAnimationFrame?.bind(window) ?? window.clearTimeout?.bind(window) ?? clearTimeout;
            cancelFrame(this.activeLinkAnimationFrame);
            this.activeLinkAnimationFrame = 0;
        }

        scheduleActiveLinkVisibilitySync() {
            if (this.activeLinkVisibilityAnimationFrame !== 0) return;
            const requestFrame = window.requestAnimationFrame?.bind(window)
                ?? ((callback: FrameRequestCallback) => window.setTimeout?.(() => callback(Date.now()), 16) ?? setTimeout(() => callback(Date.now()), 16));
            this.activeLinkVisibilityAnimationFrame = requestFrame(() => {
                this.activeLinkVisibilityAnimationFrame = 0;
                this.syncActiveLinkVisibility();
            });
        }

        cancelActiveLinkVisibilitySync() {
            if (this.activeLinkVisibilityAnimationFrame === 0) return;
            const cancelFrame = window.cancelAnimationFrame?.bind(window) ?? window.clearTimeout?.bind(window) ?? clearTimeout;
            cancelFrame(this.activeLinkVisibilityAnimationFrame);
            this.activeLinkVisibilityAnimationFrame = 0;
        }

        syncActiveLinkVisibility() {
            const container = this.resolveActiveLinkRevealContainer();
            this.bindActiveLinkVisibilityObserver(container);
            if (!container || !this.activeLink) return;

            const containerRect = container.getBoundingClientRect?.();
            const linkRect = this.activeLink.getBoundingClientRect?.();
            if (!containerRect || !linkRect) return;

            const style = typeof window.getComputedStyle === 'function' ? window.getComputedStyle(container) : null;
            const topInset = this.getScrollPadding(style, 'top');
            const bottomInset = this.getScrollPadding(style, 'bottom');
            const currentTop = container.scrollTop ?? 0;
            const maxTop = Math.max(0, (container.scrollHeight ?? 0) - (container.clientHeight ?? 0));
            let nextTop = currentTop;

            if (linkRect.top < containerRect.top + topInset) {
                nextTop += linkRect.top - containerRect.top - topInset;
            } else if (linkRect.bottom > containerRect.bottom - bottomInset) {
                nextTop += linkRect.bottom - containerRect.bottom + bottomInset;
            } else {
                return;
            }

            nextTop = Math.min(maxTop, Math.max(0, nextTop));
            if (Math.abs(nextTop - currentTop) < 1) return;

            if (typeof container.scrollTo === 'function') {
                container.scrollTo({ top: nextTop, behavior: 'auto' });
            } else {
                container.scrollTop = nextTop;
            }
        }

        resolveActiveLinkRevealContainer(): Element | null {
            const link = this.activeLink;
            if (!link || !this.root.contains(link) || !this.isRenderedElement(link)) return null;

            let current = link.parentElement;
            while (current) {
                if (this.isHiddenElement(current) || this.isClippedNonScrollableElement(current)) return null;
                if (this.root.contains(current) && this.isRenderedElement(current) && this.isVerticalRevealContainer(current)) {
                    return current;
                }

                if (current === this.root) break;
                current = current.parentElement;
            }

            return null;
        }

        isVerticalRevealContainer(element: Element) {
            const style = typeof window.getComputedStyle === 'function' ? window.getComputedStyle(element) : null;
            const overflowY = style?.overflowY || style?.overflow || '';

            return /(auto|scroll|overlay)/.test(overflowY)
                && element.clientHeight > 0
                && element.scrollHeight > element.clientHeight;
        }

        isClippedNonScrollableElement(element: Element) {
            const style = typeof window.getComputedStyle === 'function' ? window.getComputedStyle(element) : null;
            const overflowY = style?.overflowY || style?.overflow || '';

            return /(hidden|clip)/.test(overflowY) && element.scrollHeight > element.clientHeight;
        }

        isHiddenElement(element: Element) {
            const style = typeof window.getComputedStyle === 'function' ? window.getComputedStyle(element) : null;

            return style?.display === 'none'
                || style?.visibility === 'hidden'
                || style?.contentVisibility === 'hidden';
        }

        isRenderedElement(element: Element) {
            if (this.isHiddenElement(element)) return false;

            const rect = element.getBoundingClientRect?.();
            if (!rect) return false;
            const width = rect.width ?? rect.right - rect.left;
            const height = rect.height ?? rect.bottom - rect.top;

            return width > 0 && height > 0;
        }

        getScrollPadding(style: CSSStyleDeclaration | null, edge: 'top' | 'bottom') {
            const blockValue = edge === 'top'
                ? style?.scrollPaddingBlockStart
                : style?.scrollPaddingBlockEnd;
            const physicalValue = edge === 'top'
                ? style?.scrollPaddingTop
                : style?.scrollPaddingBottom;
            const value = Number.parseFloat(blockValue || physicalValue || '');

            return Number.isFinite(value) && value > 0 ? value : 0;
        }

        bindActiveLinkVisibilityObserver(container: Element | null) {
            if (typeof ResizeObserver === 'undefined') return;
            if (this.activeLinkVisibilityObserver && this.activeLinkVisibilityContainer === container) return;

            this.unbindActiveLinkVisibilityObserver();
            this.activeLinkVisibilityContainer = container;
            this.activeLinkVisibilityObserver = new ResizeObserver(this.activeLinkVisibilityResizeListener);
            this.activeLinkVisibilityObserver.observe(this.root);
            if (container && container !== this.root) {
                this.activeLinkVisibilityObserver.observe(container);
            }
        }

        unbindActiveLinkVisibilityObserver() {
            this.activeLinkVisibilityObserver?.disconnect();
            this.activeLinkVisibilityObserver = null;
            this.activeLinkVisibilityContainer = null;
        }

        bindScrollRoot() {
            const nextRoot = this.resolveScrollRoot();
            if (this.scrollRoot === nextRoot) return;
            this.unbindScrollRoot();
            nextRoot.addEventListener?.('scroll', this.scrollListener, { passive: true });
            this.scrollRoot = nextRoot;
        }

        unbindScrollRoot() {
            this.scrollRoot?.removeEventListener?.('scroll', this.scrollListener);
            this.scrollRoot = null;
        }

        resolveScrollRoot(): PageNavigationScrollRoot {
            const target = this.entries[0]?.target;
            let current = target?.parentElement ?? null;
            while (current && current !== document.body && current !== document.documentElement) {
                const style = typeof window.getComputedStyle === 'function' ? window.getComputedStyle(current) : null;
                const overflowY = style?.overflowY ?? '';
                if (/(auto|scroll|overlay)/.test(overflowY) && current.scrollHeight > current.clientHeight) {
                    return current;
                }

                current = current.parentElement;
            }

            return window;
        }

        getScrollRootTop() {
            if (this.scrollRoot && this.scrollRoot !== window) {
                return (this.scrollRoot as Element).getBoundingClientRect?.().top ?? 0;
            }

            return 0;
        }

        scrollEntryIntoView(entry: PageNavigationEntry, behavior: ScrollBehavior) {
            if (!this.scrollRoot || this.scrollRoot === window) {
                entry.target.scrollIntoView?.({ block: 'start', behavior });
                return;
            }

            const root = this.scrollRoot as Element & {
                clientHeight?: number;
                scrollHeight?: number;
                scrollTo?: (options: ScrollToOptions) => void;
                scrollTop?: number;
            };
            const rootTop = root.getBoundingClientRect?.().top ?? 0;
            const targetTop = entry.target.getBoundingClientRect?.().top ?? rootTop;
            const currentTop = root.scrollTop ?? 0;
            const maxTop = Math.max(0, (root.scrollHeight ?? 0) - (root.clientHeight ?? 0));
            const top = Math.min(maxTop, Math.max(0, currentTop + targetTop - rootTop - this.getActivationOffset(entry.target)));

            if (typeof root.scrollTo === 'function') {
                root.scrollTo({ top, behavior });
            } else {
                root.scrollTop = top;
            }
        }

        getActivationOffset(target: HTMLElement | null) {
            const style = target && typeof window.getComputedStyle === 'function' ? window.getComputedStyle(target) : null;
            const scrollMarginTop = Number.parseFloat(style?.scrollMarginTop ?? '');

            return Number.isFinite(scrollMarginTop) && scrollMarginTop > 0
                ? Math.max(PageNavigationController.activationOffset, scrollMarginTop)
                : PageNavigationController.activationOffset;
        }

        handleToggleClick(event: Event) {
            const target = event.target;
            if (!(target instanceof Element)) return;
            const toggle = target.closest('[data-rw-page-nav-toggle]');
            if (!toggle || !this.root.contains(toggle)) return;
            const { panel } = this.resolvePanel();
            if (panel) this.setPanelOpen(toggle.getAttribute('aria-expanded') === 'false');
        }

        syncPanelState() {
            const { toggle } = this.resolvePanel();
            if (toggle) this.setPanelOpen(toggle.getAttribute('aria-expanded') !== 'false');
        }

        setPanelOpen(open: boolean) {
            const { toggle, panel } = this.resolvePanel();
            if (!toggle || !panel) return;
            toggle.setAttribute('aria-expanded', open ? 'true' : 'false');
            toggle.setAttribute('data-rw-page-nav-toggle-state', open ? 'open' : 'closed');
            panel.setAttribute('data-rw-page-nav-panel-state', open ? 'open' : 'closed');
            if (open) this.scheduleActiveLinkVisibilitySync();
        }

        resolvePanel() {
            const toggle = this.root.querySelector('[data-rw-page-nav-toggle]');
            if (!toggle) return { toggle: null, panel: null };

            const controls = toggle.getAttribute('aria-controls');
            if (controls) {
                const panel = document.getElementById(controls);
                if (!panel) {
                    this.manager.recordDiagnostic(
                        `data-rw-page-nav-toggle controls "${controls}", but no element with that id exists.`,
                        'Panel close behavior is disabled for this nav root.',
                        `Add id="${controls}" to the panel or remove the toggle value.`);
                }

                return { toggle, panel };
            }

            return { toggle, panel: this.root.querySelector('[data-rw-page-nav-panel]') };
        }

        resolveTarget(link: Element): HTMLElement | null {
            let url: URL;
            try {
                url = new URL(link.getAttribute('href') || '', window.location.href);
            } catch {
                return null;
            }

            const current = new URL(window.location.href);
            if (url.origin !== current.origin || url.pathname !== current.pathname || url.search !== current.search || !url.hash) {
                return null;
            }

            const id = this.decodeHash(url.hash);
            return id ? document.getElementById(id) : null;
        }

        decodeHash(hash: string) {
            try {
                return decodeURIComponent(hash.replace(/^#/, ''));
            } catch {
                return '';
            }
        }
    }

    const pageNavigationManager = new PageNavigationManager();
    window.RazorWire = { ...(window.RazorWire || {}), pageNavigationManager };

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', () => pageNavigationManager.start());
    } else {
        pageNavigationManager.start();
    }
})();
