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

(function () {
    if (window.RazorWirePageNavigationInitialized) return;
    window.RazorWirePageNavigationInitialized = true;

    class PageNavigationManager {
        controllers = new Map<Element, PageNavigationController>();
        diagnostics: PageNavigationDiagnostic[] = [];
        isStarted = false;

        start() {
            if (this.isStarted) return;
            this.isStarted = true;
            this.scan();
            document.addEventListener('turbo:render', () => this.scan());
            document.addEventListener('turbo:load', () => this.scan());
            document.addEventListener('turbo:frame-load', () => this.scan());
            window.addEventListener?.('hashchange', () => this.refreshActiveFromHash());
            window.addEventListener?.('popstate', () => this.refreshActiveFromHash());
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
        entries: PageNavigationEntry[] = [];
        activeLink: Element | null = null;
        rootClickListener = (event: Event) => this.handleRootClick(event);
        toggleClickListener = (event: Event) => this.handleToggleClick(event);
        scrollListener = () => this.refreshActiveFromViewport();

        constructor(
            private readonly root: Element,
            private readonly manager: PageNavigationManager) {
        }

        connect() {
            this.root.setAttribute('data-rw-page-nav-enhanced', 'true');
            this.refresh();
            this.root.addEventListener?.('click', this.rootClickListener);
            this.root.addEventListener?.('click', this.toggleClickListener);
            window.addEventListener?.('scroll', this.scrollListener, { passive: true });
            document.addEventListener?.('scroll', this.scrollListener, { passive: true, capture: true });
        }

        disconnect() {
            this.root.removeAttribute('data-rw-page-nav-enhanced');
            this.root.removeEventListener?.('click', this.rootClickListener);
            this.root.removeEventListener?.('click', this.toggleClickListener);
            window.removeEventListener?.('scroll', this.scrollListener);
            document.removeEventListener?.('scroll', this.scrollListener, { capture: true });
        }

        refresh() {
            this.entries = Array.from(this.root.querySelectorAll('[data-rw-page-nav-link]'))
                .filter(link => link.tagName === 'A')
                .map(link => ({ link, target: this.resolveTarget(link) }))
                .filter((entry): entry is PageNavigationEntry => entry.target !== null);
            this.syncPanelState();
            this.refreshActiveFromHash() || this.refreshActiveFromViewport();
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
            if (hashEntry && this.activeLink === hashEntry.link) {
                const hashRect = hashEntry.target.getBoundingClientRect?.();
                if (hashRect && hashRect.top >= 0 && hashRect.top <= window.innerHeight) return;
                const firstRect = this.entries[0].target.getBoundingClientRect?.();
                if (firstRect && firstRect.top >= 0) return;
            }

            let active = this.entries[0];
            for (const entry of this.entries) {
                const rect = entry.target.getBoundingClientRect?.();
                if (!rect || rect.top > 96) break;
                active = entry;
            }

            this.setActiveLink(active.link);
        }

        getActiveEntryFromHash() {
            const id = this.decodeHash(window.location.hash || '');
            return id ? this.entries.find(candidate => candidate.target.id === id) ?? null : null;
        }

        setActiveLink(link: Element | null) {
            if (this.activeLink === link) return;
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

            event.preventDefault();
            if (window.location.href !== targetUrl.href) {
                window.history?.pushState?.(null, '', targetUrl.hash);
            }

            const reduceMotion = window.matchMedia?.('(prefers-reduced-motion: reduce)')?.matches ?? false;
            entry.target.scrollIntoView?.({ block: 'start', behavior: reduceMotion ? 'auto' : 'smooth' });
            this.setActiveLink(link);
            this.setPanelOpen(false);
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
