interface Window {
    RazorWireSectionCopyInitialized?: boolean;
    RazorWire?: {
        config?: Record<string, unknown>;
        connectionManager?: unknown;
        localTimeFormatter?: unknown;
        formFailureManager?: unknown;
        pageNavigationManager?: unknown;
        sectionCopyManager?: unknown;
    };
}

interface SectionCopyDiagnostic {
    message: string;
    impact: string;
    fix: string;
    docs: string;
}

interface SectionCopyBinding {
    button: HTMLElement;
    target: HTMLElement;
    title: string;
    generated: boolean;
    listener: EventListener;
}

(function () {
    if (window.RazorWireSectionCopyInitialized) return;
    window.RazorWireSectionCopyInitialized = true;

    const markerSelector = '[data-rw-section-copy], [data-rw-section-copy-target]';

    class SectionCopyManager {
        controllers = new Map<Element, SectionCopyController>();
        diagnostics: SectionCopyDiagnostic[] = [];
        isStarted = false;

        start() {
            if (this.isStarted) return;
            this.isStarted = true;
            this.scan();
            document.addEventListener('turbo:render', () => this.scan());
            document.addEventListener('turbo:load', () => this.scan());
            document.addEventListener('turbo:frame-load', () => this.scan());
        }

        scan() {
            const explicitRoots = Array.from(document.querySelectorAll('[data-rw-section-copy-root]'));
            const ownedRoots = new Set<Element>(explicitRoots);
            for (const root of explicitRoots) {
                this.register(root);
            }

            const ambientRoot = document.body ?? document.documentElement;
            if (ambientRoot && !explicitRoots.includes(ambientRoot)) {
                if (this.hasUnownedMarkers(explicitRoots)) {
                    this.register(ambientRoot);
                    ownedRoots.add(ambientRoot);
                } else {
                    this.unregister(ambientRoot);
                }
            }

            this.unregisterUnownedControllers(ownedRoots);
            this.prune();
        }

        register(root: Element) {
            const existing = this.controllers.get(root);
            if (existing) {
                existing.refresh();
                return;
            }

            const controller = new SectionCopyController(root, this);
            this.controllers.set(root, controller);
            controller.connect();
        }

        unregister(root: Element) {
            const controller = this.controllers.get(root);
            if (!controller) return;

            controller.disconnect();
            this.controllers.delete(root);
        }

        unregisterUnownedControllers(ownedRoots: Set<Element>) {
            for (const root of this.controllers.keys()) {
                if (root.isConnected && !ownedRoots.has(root)) {
                    this.unregister(root);
                }
            }
        }

        prune() {
            for (const [root, controller] of this.controllers) {
                if (!root.isConnected) {
                    controller.disconnect();
                    this.controllers.delete(root);
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
                docs: 'Web/ForgeTrust.RazorWire/Docs/section-copy.md#troubleshooting'
            };
            if (this.diagnostics.some(existing => existing.message === message && existing.fix === fix)) return;
            this.diagnostics.push(diagnostic);

            if (window.RazorWire?.config?.developmentDiagnostics === true && typeof console?.warn === 'function') {
                console.warn(`RazorWire section copy: ${message} Impact: ${impact} Fix: ${fix} Docs: ${diagnostic.docs}`);
            }
        }

        private hasUnownedMarkers(explicitRoots: Element[]) {
            const markers = Array.from(document.querySelectorAll(markerSelector));
            return markers.some(marker => !explicitRoots.some(root => root.contains(marker)));
        }
    }

    class SectionCopyController {
        private bindings: SectionCopyBinding[] = [];
        private generatedButtons: HTMLElement[] = [];
        private generatedStatus: HTMLElement | null = null;
        private status: HTMLElement | null = null;
        private feedbackTimer = 0;
        private fallback: HTMLElement | null = null;
        private fallbackSource: HTMLElement | null = null;
        private fallbackKeydownListener: EventListener | null = null;
        private fallbackPointerListener: EventListener | null = null;

        constructor(
            private readonly root: Element,
            private readonly manager: SectionCopyManager) {
        }

        connect() {
            this.root.setAttribute('data-rw-section-copy-enhanced', 'true');
            this.refresh();
        }

        disconnect() {
            this.closeFallback(false);
            this.clearFeedbackTimer();
            for (const binding of this.bindings) {
                binding.button.removeEventListener('click', binding.listener);
                binding.button.removeAttribute('data-rw-section-copy-enhanced');
                binding.button.removeAttribute('data-rw-section-copy-state');
                binding.button.removeAttribute('data-rw-section-copy-message');
            }

            for (const button of this.generatedButtons) {
                button.remove();
            }

            this.generatedStatus?.remove();
            this.generatedButtons = [];
            this.bindings = [];
            this.generatedStatus = null;
            this.status = null;
            this.root.removeAttribute('data-rw-section-copy-enhanced');
        }

        refresh() {
            const preservedRoot = this.root;
            this.disconnect();
            preservedRoot.setAttribute('data-rw-section-copy-enhanced', 'true');
            this.status = this.resolveStatus();
            this.addGeneratedButtons();
            this.bindAuthoredButtons();
        }

        private bindAuthoredButtons() {
            const authoredControls = this.getOwnedElements('[data-rw-section-copy]');
            for (const control of authoredControls) {
                if (control.getAttribute('data-rw-section-copy-inserted') === 'true') continue;
                if (control.tagName !== 'BUTTON') {
                    this.manager.recordDiagnostic(
                        `Element <${control.tagName.toLowerCase()}> uses data-rw-section-copy but is not a button.`,
                        'RazorWire will not bind copy behavior to the element.',
                        'Move data-rw-section-copy to a button so keyboard and assistive technology behavior is correct.');
                    continue;
                }

                this.bindButton(control as HTMLElement, false);
            }
        }

        private addGeneratedButtons() {
            for (const target of this.getOwnedElements('[data-rw-section-copy-target]')) {
                if (!(target instanceof HTMLElement)) continue;
                if (!target.id) {
                    this.manager.recordDiagnostic(
                        'data-rw-section-copy-target is missing an id.',
                        'RazorWire cannot generate a stable section URL for this target.',
                        'Add a document-unique id to the section target.');
                    continue;
                }

                if (this.findElementsById(target.id).length > 1) {
                    this.manager.recordDiagnostic(
                        `Section copy target id "${target.id}" is duplicated in the document.`,
                        'RazorWire cannot guarantee which section the copied URL identifies.',
                        'Make section target ids unique before enabling section copy for them.');
                    continue;
                }

                if (this.hasExistingGeneratedPlacement(target, target.id)) {
                    continue;
                }

                const title = this.getTitle(target, target.id);
                const button = document.createElement('button');
                button.type = 'button';
                button.textContent = 'Copy link';
                button.setAttribute('data-rw-section-copy', target.id);
                button.setAttribute('data-rw-section-copy-title', title);
                button.setAttribute('data-rw-section-copy-inserted', 'true');
                button.setAttribute('aria-label', `Copy link to ${title}`);

                if (/^H[1-6]$/i.test(target.tagName)) {
                    target.insertAdjacentElement('afterend', button);
                } else {
                    target.appendChild(button);
                }

                this.generatedButtons.push(button);
                this.bindButton(button, true);
            }
        }

        private bindButton(button: HTMLElement, generated: boolean) {
            const targetId = this.normalizeTargetId(button.getAttribute('data-rw-section-copy') ?? '', button);
            if (!targetId) return;

            const matches = this.findElementsById(targetId);
            if (matches.length > 1) {
                this.manager.recordDiagnostic(
                    `Section copy target id "${targetId}" is duplicated in the document.`,
                    'RazorWire will not copy a link because the fragment is ambiguous.',
                    'Make ids document-unique, then re-scan the section copy manager.');
                return;
            }

            const target = matches[0];
            if (!target) {
                this.manager.recordDiagnostic(
                    `data-rw-section-copy points to "${targetId}", but no element with that id exists.`,
                    'The copy button is left unbound because it would create a broken fragment URL.',
                    `Add id="${targetId}" to the section or update data-rw-section-copy.`);
                return;
            }

            const title = button.getAttribute('data-rw-section-copy-title')?.trim() || this.getTitle(target, targetId);
            const listener = (event: Event) => {
                event.preventDefault();
                void this.copySectionLink(button, target, title);
            };
            button.addEventListener('click', listener);
            button.setAttribute('data-rw-section-copy-enhanced', 'true');
            this.bindings.push({ button, target, title, generated, listener });
        }

        private async copySectionLink(button: HTMLElement, target: HTMLElement, title: string) {
            const url = this.buildSectionUrl(target.id);
            this.closeFallback(false);

            try {
                const clipboard = navigator?.clipboard;
                if (typeof clipboard?.writeText !== 'function') {
                    throw new Error('Clipboard API is unavailable.');
                }

                await clipboard.writeText(url);
                this.showCopiedFeedback(button, title);
            } catch {
                this.showFallback(button, title, url);
            }
        }

        private showCopiedFeedback(button: HTMLElement, title: string) {
            const message = `Copied link to ${title}.`;
            this.clearFeedbackTimer();
            button.setAttribute('data-rw-section-copy-state', 'copied');
            button.setAttribute('data-rw-section-copy-message', message);
            if (this.status) {
                this.status.textContent = message;
            }

            this.feedbackTimer = window.setTimeout(() => {
                button.removeAttribute('data-rw-section-copy-state');
                button.removeAttribute('data-rw-section-copy-message');
            }, 2200);
        }

        private showFallback(button: HTMLElement, title: string, url: string) {
            const message = `Copy unavailable. Select the link for ${title}.`;
            this.clearFeedbackTimer();
            button.setAttribute('data-rw-section-copy-state', 'fallback');
            button.setAttribute('data-rw-section-copy-message', message);
            if (this.status) {
                this.status.textContent = message;
            }

            const fallback = document.createElement('div');
            fallback.setAttribute('data-rw-section-copy-fallback', 'true');
            fallback.setAttribute('role', 'dialog');
            fallback.setAttribute('aria-label', `Copy link to ${title}`);

            const input = document.createElement('input');
            input.type = 'text';
            input.readOnly = true;
            input.value = url;
            input.setAttribute('aria-label', 'Section link');

            const closeButton = document.createElement('button');
            closeButton.type = 'button';
            closeButton.textContent = 'Close';
            closeButton.addEventListener('click', () => this.closeFallback(false));

            fallback.append(input, closeButton);
            button.insertAdjacentElement('afterend', fallback);
            this.fallback = fallback;
            this.fallbackSource = button;

            this.fallbackKeydownListener = (event: Event) => {
                const keyboard = event as KeyboardEvent;
                if (keyboard.key === 'Escape') {
                    keyboard.preventDefault();
                    this.closeFallback(true);
                }
            };
            this.fallbackPointerListener = (event: Event) => {
                const target = event.target;
                if (target instanceof Node && this.fallback && !this.fallback.contains(target) && target !== button) {
                    this.closeFallback(false);
                }
            };

            fallback.addEventListener('keydown', this.fallbackKeydownListener);
            fallback.addEventListener('focusout', () => {
                window.setTimeout(() => {
                    const active = document.activeElement;
                    if (active instanceof Node && this.fallback?.contains(active)) return;
                    this.closeFallback(false);
                }, 0);
            });
            document.addEventListener('pointerdown', this.fallbackPointerListener);

            window.setTimeout(() => {
                input.focus?.();
                input.select?.();
            }, 0);
        }

        private closeFallback(returnFocus: boolean) {
            if (!this.fallback) return;

            if (this.fallbackKeydownListener) {
                this.fallback.removeEventListener('keydown', this.fallbackKeydownListener);
            }

            if (this.fallbackPointerListener) {
                document.removeEventListener('pointerdown', this.fallbackPointerListener);
            }

            const source = this.fallbackSource;
            source?.removeAttribute('data-rw-section-copy-state');
            source?.removeAttribute('data-rw-section-copy-message');
            this.fallback.remove();
            this.fallback = null;
            this.fallbackSource = null;
            this.fallbackKeydownListener = null;
            this.fallbackPointerListener = null;

            if (returnFocus && source?.isConnected) {
                source.focus?.();
            }
        }

        private resolveStatus() {
            const status = this.getOwnedElements('[data-rw-section-copy-status]')[0] ?? null;
            if (status instanceof HTMLElement) {
                const ariaLive = status.getAttribute('aria-live')?.trim().toLowerCase();
                if (ariaLive === 'polite' || ariaLive === 'assertive') {
                    return status;
                }

                this.manager.recordDiagnostic(
                    'data-rw-section-copy-status must include aria-live="polite" or aria-live="assertive".',
                    'RazorWire will create an internal polite status region instead.',
                    'Add aria-live="polite" to the status element or remove data-rw-section-copy-status.');
            }

            const generatedStatus = document.createElement('span');
            generatedStatus.setAttribute('data-rw-section-copy-status', 'true');
            generatedStatus.setAttribute('data-rw-section-copy-status-generated', 'true');
            generatedStatus.setAttribute('aria-live', 'polite');
            generatedStatus.setAttribute('style', 'position:absolute;width:1px;height:1px;padding:0;margin:-1px;overflow:hidden;clip:rect(0,0,0,0);white-space:nowrap;border:0;');
            this.root.appendChild(generatedStatus);
            this.generatedStatus = generatedStatus;
            return generatedStatus;
        }

        private getOwnedElements(selector: string) {
            const candidates = [
                ...(this.root.matches(selector) ? [this.root] : []),
                ...Array.from(this.root.querySelectorAll(selector))
            ];
            const rootIsExplicit = this.root.hasAttribute('data-rw-section-copy-root');

            return candidates.filter(element => {
                if (element === this.root) return true;

                const ownerRoot = element.closest('[data-rw-section-copy-root]');
                return rootIsExplicit
                    ? ownerRoot === this.root
                    : ownerRoot === null;
            });
        }

        private normalizeTargetId(rawValue: string, button: Element) {
            let value = rawValue.trim();
            if (value === '' || value === '#') {
                this.manager.recordDiagnostic(
                    'data-rw-section-copy is blank.',
                    'RazorWire cannot determine which section link to copy.',
                    'Set data-rw-section-copy to a target id, with or without a leading #.');
                return '';
            }

            if (value.startsWith('#')) {
                value = value.slice(1);
            }

            try {
                value = decodeURIComponent(value);
            } catch {
                this.manager.recordDiagnostic(
                    `data-rw-section-copy value "${rawValue}" is not valid percent-encoded text.`,
                    'RazorWire leaves the copy button unbound because the target id cannot be decoded.',
                    'Use a literal id value or valid percent encoding such as api%20key.');
                return '';
            }

            if (!value) {
                this.manager.recordDiagnostic(
                    'data-rw-section-copy resolves to an empty id.',
                    'RazorWire cannot determine which section link to copy.',
                    'Set data-rw-section-copy to a non-empty target id.');
                return '';
            }

            if (button.getAttribute('href')) {
                this.manager.recordDiagnostic(
                    'data-rw-section-copy should be used on buttons, not navigation links.',
                    'Copy behavior may conflict with navigation.',
                    'Render a button for section copy and keep anchors for navigation.');
            }

            return value;
        }

        private getTitle(target: HTMLElement, fallback: string) {
            return target.getAttribute('data-rw-section-copy-title')?.trim()
                || target.getAttribute('aria-label')?.trim()
                || target.textContent?.trim()
                || fallback;
        }

        private buildSectionUrl(id: string) {
            const url = new URL(window.location.href);
            url.hash = id;
            return url.toString();
        }

        private hasExistingGeneratedPlacement(target: HTMLElement, id: string) {
            if (/^H[1-6]$/i.test(target.tagName)) {
                const next = target.nextElementSibling;
                return !!next && this.isCopyButtonForTarget(next, id);
            }

            return Array.from(target.children).some(child => this.isCopyButtonForTarget(child, id));
        }

        private isCopyButtonForTarget(element: Element, id: string) {
            if (!element.hasAttribute('data-rw-section-copy')) return false;

            return element.tagName === 'BUTTON'
                && this.normalizeTargetId(element.getAttribute('data-rw-section-copy') ?? '', element) === id;
        }

        private findElementsById(id: string) {
            return Array.from(document.querySelectorAll('[id]'))
                .filter((element): element is HTMLElement => element instanceof HTMLElement && element.id === id);
        }

        private clearFeedbackTimer() {
            if (this.feedbackTimer === 0) return;
            window.clearTimeout(this.feedbackTimer);
            this.feedbackTimer = 0;
        }
    }

    const sectionCopyManager = new SectionCopyManager();
    window.RazorWire = { ...(window.RazorWire || {}), sectionCopyManager };

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', () => sectionCopyManager.start());
    } else {
        sectionCopyManager.start();
    }
})();
