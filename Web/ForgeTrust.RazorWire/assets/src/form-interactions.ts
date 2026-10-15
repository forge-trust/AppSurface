interface Window {
    RazorWireFormInteractionsInitialized?: boolean;
    RazorWire?: {
        config?: Record<string, unknown>;
        connectionManager?: unknown;
        localTimeFormatter?: unknown;
        formFailureManager?: unknown;
        pageNavigationManager?: unknown;
        sectionCopyManager?: unknown;
        formInteractionsManager?: unknown;
        behaviors?: unknown;
    };
}

interface FormInteractionDiagnostic {
    message: string;
    impact: string;
    fix: string;
    docs: string;
}

type CollectionAction = 'add' | 'duplicate' | 'physical-remove' | 'mark-remove';

(function () {
    if (window.RazorWireFormInteractionsInitialized) return;
    window.RazorWireFormInteractionsInitialized = true;

    const markerSelector = '[data-rw-form-toggle], [data-rw-form-collection]';
    const collectionEvent = 'razorwire:form-collection:';
    const markerFix = 'Match metadata to unique hidden .index keys.';
    const templateFix = 'Match template token and marker.';
    const ownerFix = 'Keep owners stable.';
    const ownerChanged = 'ownership changed.';
    const rowSelector = '[data-rw-form-collection-row]';
    const rootSelector = '[data-rw-form-collection]';
    const indexAttribute = 'data-rw-form-index';
    const deleteAttribute = 'data-rw-form-collection-delete-field';
    const rowStateAttribute = 'data-rw-form-collection-row-state';
    const bindingAttributes = ['name', 'data-valmsg-for', 'data-rw-form-collection', 'data-rw-form-collection-index-token'];
    const docsPath = 'Web/ForgeTrust.RazorWire/Docs/form-interactions.md#troubleshooting';

    class FormInteractionsManager {
        controllers = new Map<HTMLFormElement, FormInteractionsController>();
        diagnostics: FormInteractionDiagnostic[] = [];
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
            const forms = new Set<HTMLFormElement>();
            for (const marker of Array.from(document.querySelectorAll(markerSelector))) {
                const form = marker.closest('form');
                if (form instanceof HTMLFormElement) {
                    forms.add(form);
                } else {
                    this.recordDiagnostic(
                        `<${marker.localName}> uses form interactions outside a form.`,
                        'A form is required.',
                        'Use a form.');
                }
            }

            for (const form of forms) {
                this.register(form);
            }

            for (const form of this.controllers.keys()) {
                if (!form.isConnected || !forms.has(form)) {
                    this.unregister(form);
                }
            }

        }

        register(form: HTMLFormElement) {
            const existing = this.controllers.get(form);
            if (existing) {
                existing.refresh();
                return;
            }

            const controller = new FormInteractionsController(form, this);
            this.controllers.set(form, controller);
            controller.connect();
        }

        unregister(form: HTMLFormElement) {
            const controller = this.controllers.get(form);
            if (!controller) return;

            controller.disconnect();
            this.controllers.delete(form);
        }

        prune() {
            for (const form of this.controllers.keys()) {
                if (!form.isConnected) {
                    this.unregister(form);
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
            const diagnostic = { message, impact, fix, docs: docsPath };
            if (this.diagnostics.some(existing => existing.message === message && existing.fix === fix)) return;
            this.diagnostics.push(diagnostic);

            if (window.RazorWire?.config?.developmentDiagnostics === true && typeof console?.warn === 'function') {
                console.warn(`RazorWire form interactions: ${message} Impact: ${impact} Fix: ${fix} Docs: ${diagnostic.docs}`);
            }
        }

    }

    class FormInteractionsController {
        private clickListener: EventListener = event => this.handleClick(event);
        private changeListener: EventListener = event => this.handleChange(event);
        private indexSeed = 0;

        constructor(
            private readonly form: HTMLFormElement,
            private readonly manager: FormInteractionsManager) {
        }

        connect() {
            this.form.setAttribute('data-rw-form-interactions-enhanced', 'true');
            this.form.addEventListener('click', this.clickListener);
            this.form.addEventListener('change', this.changeListener);
            this.refresh();
        }

        disconnect() {
            this.form.removeEventListener('click', this.clickListener);
            this.form.removeEventListener('change', this.changeListener);
            this.form.removeAttribute('data-rw-form-interactions-enhanced');
            for (const status of Array.from(this.form.querySelectorAll('[data-rw-form-collection-status-generated]'))) status.remove();
        }

        refresh() {
            for (const toggle of Array.from(this.form.querySelectorAll('[data-rw-form-toggle]'))) this.syncToggle(toggle as HTMLElement);
            this.validateRoots();
        }

        private handleChange(event: Event) {
            const target = event.target;
            if (!(target instanceof Element)) return;
            const toggle = target.closest('[data-rw-form-toggle]');
            if (toggle && this.form.contains(toggle)) {
                this.syncToggle(toggle as HTMLElement);
            }
        }

        private handleClick(event: Event) {
            const target = event.target;
            if (!(target instanceof Element)) return;

            const toggleButton = target.closest('button[data-rw-form-toggle]');
            if (toggleButton && this.form.contains(toggleButton)) {
                event.preventDefault();
                this.syncButtonToggle(toggleButton as HTMLElement);
                return;
            }

            for (const action of ['add', 'duplicate', 'remove'] as const) {
                const command = target.closest(`[data-rw-form-collection-${action}]`) as HTMLElement | null;
                if (!command || !this.form.contains(command)) continue;
                if (!this.canCommand(command)) return;
                event.preventDefault();
                if (action === 'add') this.addRow(command);
                else if (action === 'duplicate') this.duplicateRow(command);
                else this.removeRow(command);
                return;
            }
        }

        private syncButtonToggle(toggle: HTMLElement) {
            const expanded = toggle.getAttribute('aria-expanded');
            const key = (toggle.getAttribute('data-rw-form-toggle') ?? '').trim();
            // syncToggle owns missing-key/target diagnostics before applying this proposed visibility.
            const visible = expanded === null ? this.targets(this.form, key).every(target => (target as HTMLElement).hidden) : expanded !== 'true';
            this.syncToggle(toggle, visible);
        }

        private syncToggle(toggle: HTMLElement, visibleOverride: boolean | null = null) {
            const key = (toggle.getAttribute('data-rw-form-toggle') ?? '').trim();
            if (!key) {
                this.manager.recordDiagnostic(
                    'Blank form toggle.',
                    'This toggle has no target.',
                    'Match toggle and target in this form.');
                return;
            }

            const targets = this.targets(this.form, key);
            if (targets.length === 0) {
                if (this.targets(document, key).some(target => !this.form.contains(target))) {
                    this.manager.recordDiagnostic(`Toggle "${key}" points outside its form.`, 'A form is required.', 'Use a form.');
                } else {
                    this.manager.recordDiagnostic(
                        `Toggle "${key}" has no target.`,
                        'This toggle has no target.',
                        'Match toggle and target in this form.');
                }

                return;
            }

            const visible = visibleOverride ?? this.toggleVisible(toggle);
            const firstTarget = targets[0] as HTMLElement;
            if (!this.toggleEvent(toggle, firstTarget, 'razorwire:form-toggle:before-change', visible, true)) return;

            for (const target of targets) {
                this.applyToggleTarget(toggle, target as HTMLElement, visible);
            }

            toggle.setAttribute('data-rw-form-toggle-state', visible ? 'shown' : 'hidden');
            this.toggleEvent(toggle, firstTarget, 'razorwire:form-toggle:change', visible, false);
        }

        private targets(scope: Element | Document, key: string) {
            return Array.from(scope.querySelectorAll('[data-rw-form-toggle-target]')).filter(target => target.getAttribute('data-rw-form-toggle-target') === key);
        }

        private applyToggleTarget(toggle: HTMLElement, target: HTMLElement, visible: boolean) {
            target.hidden = !visible;
            target.setAttribute('data-rw-form-toggle-target-state', visible ? 'shown' : 'hidden');
            if (target.id && !toggle.hasAttribute('aria-controls')) {
                toggle.setAttribute('aria-controls', target.id);
            }
            if (toggle.localName === 'button' || toggle.localName === 'input' || toggle.getAttribute('role') === 'button' || toggle.hasAttribute('aria-expanded')) {
                toggle.setAttribute('aria-expanded', visible ? 'true' : 'false');
            }

            const shouldDisable = target.getAttribute('data-rw-form-toggle-disable-when-hidden') === 'true';
            if (!shouldDisable) return;

            for (const control of this.controls(target)) {
                if (!visible) {
                    if (this.preserveRemovedControl(control)) {
                        continue;
                    }

                    if (!control.disabled) {
                        control.disabled = true;
                        control.setAttribute('data-rw-disabled-by-form-toggle', 'true');
                    }
                } else if (control.getAttribute('data-rw-disabled-by-form-toggle') === 'true' && !this.removed(control)) {
                    control.disabled = false;
                    control.removeAttribute('data-rw-disabled-by-form-toggle');
                }
            }
        }

        private toggleVisible(toggle: HTMLElement) {
            const input = toggle as HTMLInputElement;
            const active = toggle.localName === 'input' && ['checkbox', 'radio'].includes(input.type)
                ? input.checked
                : ['input', 'select', 'textarea'].includes(toggle.localName)
                    ? !!input.value
                    : toggle.getAttribute('aria-expanded') !== 'false';
            return toggle.getAttribute('data-rw-form-toggle-invert') === 'true' ? !active : active;
        }

        /** Commands resolve ownership from the current DOM; no descriptors survive a mutation/Turbo scan. */
        private owned(root: Element, selector: string) {
            return Array.from(root.querySelectorAll(selector))
                .filter(element => element.closest(rootSelector) === root) as HTMLElement[];
        }

        private rows(root: HTMLElement) { return this.owned(root, rowSelector); }

        private marker(row: HTMLElement, collection: string) {
            return Array.from(row.querySelectorAll('input')).filter(element =>
                element.name === `${collection}.index` && element.closest(rowSelector) === row
                && element.closest(rootSelector) === row.closest(rootSelector));
        }

        private fail(root: HTMLElement, cause: string, fix: string) {
            this.manager.recordDiagnostic(`Collection "${this.name(root)}" ${cause}`,
                'Aborted; values retained.', fix);
            return false;
        }

        private rowIndex(row: HTMLElement, collection: string) {
            const markers = this.marker(row, collection);
            return markers[0]?.value || row.getAttribute(indexAttribute);
        }

        /** Canonical row tokens are opaque data. Metadata fallback remains for legacy one-level rows only. */
        private validateRows(root: HTMLElement, collection: string, strict = false) {
            const seen = new Set<string>();
            let valid = true;
            for (const row of this.rows(root)) {
                const markers = this.marker(row, collection);
                const marker = markers[0];
                const metadata = row.getAttribute(indexAttribute);
                if (!marker) {
                    const legacy = this.form.contains(root) && !root.parentElement?.closest(rootSelector) && !row.querySelector(rootSelector) && metadata?.trim();
                    if (!legacy || !strict) this.fail(root, `row is missing ${collection}.index.`, markerFix);
                    if (!legacy) valid = false;
                } else if (markers.length !== 1 || marker.type !== 'hidden' || marker.disabled || !marker.value.trim()) {
                    this.fail(root, `row has an invalid ${collection}.index marker.`, markerFix);
                    valid = false;
                } else if (metadata && metadata !== marker.value) {
                    this.fail(root, `row index "${metadata}" does not match ${collection}.index "${marker.value}".`, markerFix);
                    valid = false;
                }
                const key = marker?.value || metadata;
                if (key && seen.has(key)) { this.fail(root, `has duplicate index "${key}".`, markerFix); valid = false; }
                if (key) seen.add(key);
                if (row.querySelector('input[type="file"]')) this.fail(root, 'contains a file input that duplicate cannot clone.', 'Reselect files.');
            }
            return valid;
        }

        private validateRoots() {
            for (const root of Array.from(this.form.querySelectorAll(rootSelector)) as HTMLElement[]) {
                if (!this.name(root)) this.fail(root, 'blank prefix.', 'Set the prefix.');
                this.template(root);
                this.validateRows(root, this.name(root));
                for (const command of this.owned(root, '[data-rw-form-collection-add], [data-rw-form-collection-duplicate], [data-rw-form-collection-remove]')) {
                    if (command.tagName !== 'BUTTON') this.commandRoot(command);
                }
            }
        }

        private commandRoot(command: Element) {
            const root = command.closest(rootSelector) as HTMLElement | null;
            if (!root || !this.form.contains(root) || !this.name(root)) {
                this.manager.recordDiagnostic('Collection command has no root.', 'A form is required.', 'Use a form.');
                return null;
            }
            if (command.tagName !== 'BUTTON') {
                this.fail(root, `${command.tagName.toLowerCase()} uses a collection command attribute but is not a button.`, 'Use a button.');
                return null;
            }
            return root;
        }

        private canCommand(command: Element) {
            return this.commandRoot(command) && this.focusable(command as HTMLElement);
        }

        private token(root: HTMLElement) { return root.getAttribute('data-rw-form-collection-index-token')?.trim() || '__index__'; }

        private template(root: HTMLElement) {
            const token = this.token(root);
            if (!/^__[A-Za-z][A-Za-z0-9_]*__$/.test(token)) {
                this.fail(root, 'invalid template index token.', 'Use __name__ or __index__.');
                return null;
            }
            const template = this.owned(root, 'template[data-rw-form-collection-template]')[0] as HTMLTemplateElement | undefined;
            if (!template) { this.fail(root, 'is missing a template.', templateFix); return null; }
            const row = (template.content.querySelector(rowSelector) ?? template.content.firstElementChild) as HTMLElement | null;
            const prefix = `${this.name(root)}[${token}]`;
            const markers = row ? this.marker(row, this.name(root)) : [];
            const bound = row && this.controls(row).some(element => element.closest(rowSelector) === row
                && (element.name === prefix || element.name.startsWith(`${prefix}.`)));
            if (!bound || (!markers.length && root.parentElement?.closest(rowSelector))
                || (markers.length && (markers.length !== 1 || markers[0].type !== 'hidden' || markers[0].disabled || markers[0].value !== token))) {
                this.fail(root, `template binding: ${token} missing.`, templateFix);
                return null;
            }
            return template;
        }

        private nextIndex(root: HTMLElement, collection: string, reserved = new Set<string>()) {
            const values = [...this.rows(root).map(row => this.rowIndex(row, collection)).filter((key): key is string => !!key), ...reserved];
            if (values.every(value => /^\d+$/.test(value) && Number.isSafeInteger(Number(value)))) {
                const next = Math.max(-1, ...values.map(Number)) + 1;
                if (Number.isSafeInteger(next)) return String(next);
            }
            let candidate: string;
            do { candidate = `rw${Date.now().toString(36)}${(++this.indexSeed).toString(36)}`; } while (values.includes(candidate));
            return candidate;
        }

        /** Traverse inert fragments explicitly. Each fragment is a distinct ID-reference scope. */
        private walk(root: Element, visit: (element: HTMLElement, scope: Element | DocumentFragment) => void, scope: Element | DocumentFragment = root) {
            visit(root as HTMLElement, scope);
            for (const child of root.children) this.walk(child, visit, scope);
            if (root.localName === 'template') {
                const content = (root as HTMLTemplateElement).content;
                for (const child of content.children) this.walk(child, visit, content);
            }
        }

        /** A detached plan stores source/control pairs before marker insertion or tombstone pruning. */
        private clonePlan(root: HTMLElement, source: HTMLElement, oldIndex: string, index: string, duplicate: boolean) {
            const row = source.cloneNode(true) as HTMLElement;
            const pairs = new Map<HTMLElement, HTMLElement>();
            const sourceBindings = this.bindings(source);
            const originals = [...sourceBindings.keys()];
            let position = 0;
            this.walk(row, element => pairs.set(originals[position++], element));
            // Only template contexts introduce unresolved tokens; concrete keys never do.
            const tokens = new Map<Element | DocumentFragment, string[]>([[source, duplicate ? [] : [this.token(root)]]]);
            const prototypes = new Map<Element | DocumentFragment, string[]>();
            let prototypesValid = true;
            this.walk(source, (element, scope) => {
                const unresolved = tokens.get(scope) ?? [];
                if (element.hasAttribute('data-rw-form-collection')) {
                    if (!this.template(element)) prototypesValid = false;
                    if (unresolved.includes(this.token(element))) prototypesValid = this.fail(element, 'ancestor and child template tokens must differ.', templateFix);
                }
                if (element.localName === 'template') {
                    const owner = element.closest(rootSelector) as HTMLElement ?? root;
                    const content = (element as HTMLTemplateElement).content;
                    tokens.set(content, [...unresolved, this.token(owner)]);
                    const prefixes = [...prototypes.get(scope) ?? [], `${this.name(owner)}[${this.token(owner)}]`];
                    prototypes.set(content, prefixes);
                    prototypes.set((pairs.get(element) as HTMLTemplateElement).content, prefixes);
                }
            });
            if (!prototypesValid) return null;
            const mappings: { old: string; next: string; oldKey: string; key: string; clone: HTMLElement; collection: string }[] = [];
            const prepare = (owner: HTMLElement, sourceRow: HTMLElement, key: string, nextCollection: string, nextKey: string): boolean => {
                const clone = pairs.get(sourceRow)!;
                const old = `${this.name(owner)}[${key}]`;
                const next = `${nextCollection}[${nextKey}]`;
                mappings.push({ old, next, oldKey: key, key: nextKey, clone, collection: nextCollection });
                for (const childOwner of Array.from(sourceRow.querySelectorAll(rootSelector)) as HTMLElement[]) {
                    if (childOwner.closest(rowSelector) !== sourceRow) continue;
                    const childPrefix = this.rebase(this.name(childOwner), [{ old, next }]);
                    if (!this.name(childOwner).startsWith(`${old}.`)) return this.fail(childOwner, 'invalid child prefix.', templateFix);
                    if (!this.validateRows(childOwner, this.name(childOwner), true)) return false;
                    const reserved = new Set<string>();
                    for (const child of this.rows(childOwner)) {
                        if (duplicate && child.getAttribute(rowStateAttribute) === 'removed') { pairs.get(child)!.remove(); continue; }
                        const childKey = this.rowIndex(child, this.name(childOwner))!;
                        const fresh = duplicate ? this.nextIndex(childOwner, this.name(childOwner), reserved) : childKey;
                        reserved.add(fresh);
                        if (!prepare(childOwner, child, childKey, childPrefix, fresh)) return false;
                    }
                }
                return true;
            };
            if (!prepare(root, source, oldIndex, this.name(root), index)) return null;
            // Pairing before removing cloned tombstones keeps live controls aligned with their source.
            if (duplicate) {
                const deletes = new Map<HTMLElement, HTMLInputElement | null>();
                for (const [original, clone] of pairs) {
                    if (!['input', 'select', 'textarea', 'button'].includes(original.localName)) continue;
                    const owner = original.closest(rootSelector) as HTMLElement | null ?? root;
                    const ownerRow = original.closest(rowSelector) as HTMLElement | null ?? source;
                    if (!deletes.has(ownerRow)) deletes.set(ownerRow, this.deleteField(owner, ownerRow, false));
                    this.copyValue(original as FormControl, clone as FormControl, deletes.get(ownerRow) === original);
                }
            }
            mappings.sort((a, b) => b.old.length - a.old.length);
            // Longest exact owned prefix wins; concrete key characters are never regex/selector syntax.
            this.walk(source, (original, scope) => {
                const element = pairs.get(original)!;
                const applicable = mappings.filter(mapping => !prototypes.get(scope)?.some(prefix => this.matches(mapping.old, prefix)));
                for (const name of bindingAttributes.slice(0, 3)) {
                    if (element.hasAttribute(name)) element.setAttribute(name, this.rebase(element.getAttribute(name)!, applicable));
                }
            });
            for (const mapping of mappings) {
                for (const input of this.controls(mapping.clone)) {
                    if (input.closest(rowSelector) !== mapping.clone) continue;
                    if (/(^|\.)(clientindex|rowindex)$/i.test(input.name) && input.value === mapping.oldKey) input.value = mapping.key;
                }
                mapping.clone.setAttribute(indexAttribute, mapping.key);
                if (duplicate || mapping.clone !== row) this.setMarker(mapping.clone, mapping.collection, mapping.key);
            }
            this.clearValidation(row);
            if (!this.remapIds(row, mappings, root, prototypes)) return null;
            const bindings = this.bindings(row);
            const template = duplicate ? null : this.template(root);
            return { row, index, root, source, oldIndex, mappings, bindings, sourceBindings, duplicate, prefix: this.name(root), parent: root.parentNode, template };
        }

        /** Before-event listeners may edit values, but planned binding paths and their owners remain fixed. */
        private bindings(row: HTMLElement) {
            const entries = new Map<HTMLElement, (string | Node | boolean | null)[]>();
            this.walk(row, element => {
                entries.set(element, [...bindingAttributes.map(name => element.getAttribute(name)), element.parentNode,
                    ...(element instanceof HTMLInputElement && element.name.endsWith('.index') ? [element.type, element.value, element.disabled] : [])]);
            });
            return entries;
        }

        /** Compare topology and binding paths while allowing ordinary live value edits. */
        private sameBindings(row: HTMLElement, expected: ReturnType<FormInteractionsController['bindings']>) {
            const current = this.bindings(row);
            return current.size === expected.size && [...expected].every(([element, entry]) =>
                entry.every((value, position) => value === current.get(element)?.[position]));
        }

        private rebase(value: string, mappings: { old: string; next: string }[]) {
            const mapping = mappings.find(candidate => this.matches(value, candidate.old));
            return mapping ? mapping.next + value.slice(mapping.old.length) : value;
        }

        private matches(value: string, prefix: string) { return value === prefix || value.startsWith(`${prefix}.`) || value.startsWith(`${prefix}[`); }

        /** Document IDs are reserved once per operation; prototype maps remain fragment-local. */
        private remapIds(row: HTMLElement, mappings: { old: string; next: string }[], root: HTMLElement, prototypes: Map<Element | DocumentFragment, string[]>) {
            const liveIds = new Set(Array.from(document.querySelectorAll('[id]')).map(element => element.id));
            const scopes = new Map<Element | DocumentFragment, Map<string, string>>();
            const parents = new Map<Element | DocumentFragment, Element | DocumentFragment>();
            const sanitize = (path: string) => path.replace(/[.\[\]]/g, '_');
            const idMappings = mappings.map(mapping => ({ old: sanitize(mapping.old), next: sanitize(mapping.next) })).sort((a, b) => b.old.length - a.old.length);
            let valid = true;
            this.walk(row, (element, scope) => {
                if (element.localName === 'template') parents.set((element as HTMLTemplateElement).content, scope);
                const id = element.id;
                if (!id) return;
                const map = scopes.get(scope) ?? new Map<string, string>();
                scopes.set(scope, map);
                if (map.has(id)) { valid = false; return; }
                const idMapping = idMappings.find(mapping => id.startsWith(mapping.old)
                    && !prototypes.get(scope)?.some(prefix => mapping.old.startsWith(sanitize(prefix))));
                let next = idMapping ? idMapping.next + id.slice(idMapping.old.length) : `${id}_rw${(++this.indexSeed).toString(36)}`;
                while (liveIds.has(next) || [...map.values()].includes(next)) next = `${id}_rw${(++this.indexSeed).toString(36)}`;
                map.set(id, next);
                element.id = next;
                if (scope === row) liveIds.add(next);
            });
            if (!valid) { this.fail(root, 'duplicate source IDs.', 'Use unique IDs.'); return null; }
            this.walk(row, (element, scope) => {
                for (const name of ['for', 'list', 'aria-errormessage', 'aria-controls', 'aria-describedby', 'aria-labelledby', 'aria-owns']) {
                    const value = element.getAttribute(name);
                    if (value === null) continue;
                    const multiple = name === 'for' ? element.localName === 'output' : ['aria-controls', 'aria-describedby', 'aria-labelledby', 'aria-owns'].includes(name);
                    const rewrite = (id: string): string => {
                        let current: Element | DocumentFragment | undefined = scope;
                        while (current && !scopes.get(current)?.has(id)) current = parents.get(current);
                        if (current) return scopes.get(current)!.get(id)!;
                        return id;
                    };
                    element.setAttribute(name, multiple ? value.split(/\s+/).filter(Boolean).map(rewrite).join(' ') : rewrite(value));
                }
            });
            return true;
        }

        private setMarker(row: HTMLElement, collection: string, index: string) {
            let marker = this.marker(row, collection)[0];
            if (!marker) { marker = document.createElement('input'); marker.setAttribute('type', 'hidden'); marker.setAttribute('name', `${collection}.index`); row.insertAdjacentElement('afterbegin', marker); }
            marker.value = index; marker.setAttribute('value', index); marker.disabled = false;
        }

        private addRow(command: HTMLElement) {
            const root = this.commandRoot(command);
            if (!root || !this.validateRows(root, this.name(root), true)) return;
            const template = this.template(root);
            if (!template) return;
            const source = (template.content.querySelector(rowSelector) ?? template.content.firstElementChild) as HTMLElement;
            const token = this.token(root);
            const index = this.nextIndex(root, this.name(root));
            const plan = this.clonePlan(root, source, token, index, false);
            if (!plan) return;
            // Legacy before-add observes rebased attributes before clearing values/final marker assignment.
            plan.row.removeAttribute(indexAttribute);
            if (!this.rowEvent(root, command, plan.row, collectionEvent + 'before-add', 'add', index, true)) return;
            if (!this.validPlan(plan, command, false)) return;
            this.clearValues(plan.row);
            this.setMarker(plan.row, this.name(root), index);
            plan.row.setAttribute(indexAttribute, index);
            plan.bindings = this.bindings(plan.row);
            if (!this.validPlan(plan, command, true)) return;
            const before = this.owned(root, '[data-rw-form-collection-add]')[0] ?? template;
            before.insertAdjacentElement('beforebegin', plan.row);
            this.inserted(root, command, plan.row, 'add', index);
        }

        private duplicateRow(command: HTMLElement) {
            const root = this.commandRoot(command);
            const source = command.closest(rowSelector) as HTMLElement | null;
            if (!root || !source || source.closest(rootSelector) !== root) return;
            const oldIndex = this.rowIndex(source, this.name(root));
            if (!oldIndex) { this.fail(root, 'duplicate command has no source row index.', markerFix); return; }
            if (!this.validateRows(root, this.name(root), true)) return;
            const index = this.nextIndex(root, this.name(root));
            const plan = this.clonePlan(root, source, oldIndex, index, true);
            if (!plan || !this.rowEvent(root, command, plan.row, collectionEvent + 'before-duplicate', 'duplicate', index, true, oldIndex)) return;
            if (!this.validPlan(plan, command, true)) return;
            source.insertAdjacentElement('afterend', plan.row);
            this.inserted(root, command, plan.row, 'duplicate', index, oldIndex);
        }

        /** Reentrant listeners may mutate the live form; abort rather than rolling back their edits. */
        private validPlan(plan: NonNullable<ReturnType<FormInteractionsController['clonePlan']>>, command: HTMLElement, prepared: boolean) {
            const { root, source, index, duplicate } = plan;
            if (plan.row.isConnected || root.parentNode !== plan.parent || this.name(root) !== plan.prefix || !this.form.contains(root) || command.closest(rootSelector) !== root || !this.focusable(command)
                || (duplicate && (!root.contains(source) || source.closest(rootSelector) !== root || command.closest(rowSelector) !== source || this.rowIndex(source, this.name(root)) !== plan.oldIndex))) {
                return this.fail(root, ownerChanged, ownerFix);
            }
            if (!duplicate && (!plan.template || this.template(root) !== plan.template
                || this.token(root) !== plan.oldIndex
                || (plan.template.content.querySelector(rowSelector) ?? plan.template.content.firstElementChild) !== source))
                return this.fail(root, ownerChanged, ownerFix);
            if (!this.validateRows(root, this.name(root), true)) return false;
            for (const mapping of plan.mappings) {
                const marker = this.marker(mapping.clone, mapping.collection);
                const staged = mapping.clone === plan.row && !prepared;
                if (staged && !marker.length) continue;
                if (marker.length !== 1 || marker[0].disabled || marker[0].type !== 'hidden' || marker[0].value !== (staged ? mapping.oldKey : mapping.key) || (!staged && mapping.clone.getAttribute(indexAttribute) !== mapping.key))
                    return this.fail(root, 'markers changed.', markerFix);
            }
            // Snapshots include inert descendants and marker properties as well as binding paths.
            for (const [element, entry] of [...plan.bindings, ...plan.sourceBindings]) {
                const collection = element.getAttribute('data-rw-form-collection');
                if (collection !== null) {
                    if (collection !== entry[2]) return this.fail(root, ownerChanged, ownerFix);
                    if (!this.template(element) || !this.validateRows(element, collection.trim(), true)) return false;
                }
            }
            if (!this.sameBindings(plan.row, plan.bindings) || !this.sameBindings(source, plan.sourceBindings))
                return this.fail(root, ownerChanged, ownerFix);
            if (this.rows(root).some(row => this.rowIndex(row, this.name(root)) === index)) return this.fail(root, 'index conflict.', ownerFix);
            const existing = new Set(Array.from(document.querySelectorAll('[id]')).map(element => element.id));
            let valid = true;
            for (const element of [plan.row, ...Array.from(plan.row.querySelectorAll('[id]'))]) {
                if (!element.id) continue;
                if (existing.has(element.id)) valid = false;
                existing.add(element.id);
            }
            if (!valid) return this.fail(root, 'ID conflict.', 'Use unique IDs.');
            return true;
        }

        private inserted(root: HTMLElement, command: HTMLElement, row: HTMLElement, action: 'add' | 'duplicate', index: string, previous: string | null = null) {
            this.rowEvent(root, command, row, collectionEvent + action, action, index, false, previous);
            this.status(root).textContent = `${action === 'add' ? 'Added' : 'Duplicated'} ${this.label(root)}.`;
            this.focusRow(row, command);
        }

        private removeRow(command: HTMLElement) {
            const root = this.commandRoot(command);
            const row = command.closest(rowSelector) as HTMLElement | null;
            if (!root || !row || row.closest(rootSelector) !== root || !this.validateRows(root, this.name(root), true)) return;
            const mode = this.removeMode(root, command);
            const index = this.rowIndex(row, this.name(root)) ?? '';
            const parent = row.parentElement;
            const prefix = this.name(root);
            const action: CollectionAction = mode === 'mark' ? 'mark-remove' : 'physical-remove';
            if (!this.rowEvent(root, command, row, collectionEvent + 'before-remove', action, index, true)) return;
            if (row.parentElement !== parent || this.name(root) !== prefix || !this.form.contains(root) || !root.contains(row) || command.closest(rowSelector) !== row || !this.canCommand(command) || this.rowIndex(row, this.name(root)) !== index || !this.validateRows(root, this.name(root), true)) {
                this.fail(root, ownerChanged, ownerFix); return;
            }
            const focus = this.removeFocus(root, row);
            if (mode === 'mark' && !this.markRemoved(root, row)) return;
            if (mode !== 'mark') row.remove();
            this.rowEvent(root, command, row, collectionEvent + 'remove', action, index, false);
            this.status(root).textContent = `${mode === 'mark' ? 'Marked' : 'Removed'} ${this.label(root)}${mode === 'mark' ? ' for removal' : ''}.`;
            focus?.focus();
        }

        private copyValue(source: FormControl, clone: FormControl, isSelectedDeleteField: boolean) {
            // Canonical markers are assigned by their owner mapping after value copying.
            if (clone instanceof HTMLInputElement && clone.type === 'file') {
                clone.value = '';
                return;
            }

            if (clone instanceof HTMLInputElement
                && clone.type === 'hidden'
                && (clone.hasAttribute(deleteAttribute) || isSelectedDeleteField)) {
                clone.value = clone.getAttribute('value') || 'false';
                clone.disabled = false;
                return;
            }

            if (this.clearIdentity(clone)) {
                clone.value = '';
                return;
            }

            if (source.localName === 'input' && ['checkbox', 'radio'].includes(source.type)) {
                (clone as HTMLInputElement).checked = (source as HTMLInputElement).checked;
            } else clone.value = source.value;
        }

        private clearValues(row: HTMLElement) {
            for (const control of this.controls(row)) {
                if (control.type === 'hidden' || control.localName === 'button') continue;
                if (['checkbox', 'radio'].includes(control.type)) (control as HTMLInputElement).checked = false;
                else if (control.localName === 'select') (control as HTMLSelectElement).selectedIndex = -1;
                else control.value = '';
            }
        }

        private clearValidation(row: HTMLElement) {
            this.walk(row, element => {
                if (element.hasAttribute('data-rw-form-collection-status')) element.textContent = '';
                element.removeAttribute('aria-invalid');
                element.classList?.remove('input-validation-error', 'field-validation-error');
                if (element.hasAttribute('data-valmsg-for')) { element.textContent = ''; element.removeAttribute('data-valmsg-replace'); }
            });
        }

        private clearIdentity(control: FormControl) {
            if (!(control instanceof HTMLInputElement) || control.type !== 'hidden') return false;
            if (control.hasAttribute('data-rw-form-collection-copyable')) return false;
            if (control.name.endsWith('.index')) return false;
            return /(^|\.)(id|rowversion|concurrencystamp|concurrencytoken|etag|isdeleted|delete)$/i.test(control.name);
        }

        /** Delete selectors never escape the nearest row owner, including nested child rows. */
        private deleteField(root: HTMLElement, row: HTMLElement, diagnose = true) {
            const selector = root.getAttribute(deleteAttribute) || '[data-rw-form-collection-delete-field]';
            try {
                const field = Array.from(row.querySelectorAll(selector)).find(element => element.closest(rowSelector) === row
                    && element.closest(rootSelector) === root);
                if (field?.localName === 'input' && (field as HTMLInputElement).type === 'hidden') return field as HTMLInputElement;
                if (diagnose) this.fail(root, 'mark-remove has no app-owned delete field.', 'Mark an owned hidden input.');
            } catch {
                if (diagnose) this.fail(root, 'invalid delete-field selector.', 'Select an owned hidden input.');
            }
            return null;
        }

        private markRemoved(root: HTMLElement, row: HTMLElement) {
            const field = this.deleteField(root, row);
            if (!field) return false;
            field.setAttribute(deleteAttribute, 'true');
            row.hidden = true;
            row.setAttribute(rowStateAttribute, 'removed');
            field.value = field.getAttribute('data-rw-form-collection-delete-value') || 'true';
            for (const control of this.controls(row)) {
                // The row is already marked; the shared predicate retains its selected delete field and nested tombstones.
                if (this.preserveRemovedControl(control)) control.disabled = false;
                else if (!control.disabled) { control.disabled = true; control.setAttribute('data-rw-disabled-by-form-collection-remove', 'true'); }
            }
            return true;
        }

        private removed(element: Element) {
            return !!element.closest(`[${rowStateAttribute}="removed"]`);
        }

        private preserveRemovedControl(control: FormControl) {
            const row = control.closest(rowSelector);
            return this.removed(control) && (control.name.endsWith('.index') || control.hasAttribute('data-rw-form-collection-preserve')
                || (row?.getAttribute(rowStateAttribute) === 'removed' && control.hasAttribute(deleteAttribute)));
        }

        private removeMode(root: HTMLElement, command: HTMLElement) {
            const commandMode = command.getAttribute('data-rw-form-collection-remove');
            const raw = command.getAttribute('data-rw-form-collection-remove-mode')
                || (commandMode && commandMode !== 'true' ? commandMode : null)
                || root.getAttribute('data-rw-form-collection-remove-mode')
                || 'physical';
            return ['mark', 'mark-remove'].includes(raw.toLowerCase()) ? 'mark' : 'physical';
        }

        private removeFocus(root: HTMLElement, row: HTMLElement) {
            const rows = this.rows(root);
            const index = rows.indexOf(row);
            for (const candidate of [...rows.slice(index + 1), ...rows.slice(0, index).reverse()]) {
                const command = this.rowCommand(candidate);
                if (command) return command;
            }

            return this.owned(root, '[data-rw-form-collection-add]').find(add => this.focusable(add)) ?? null;
        }

        private rowCommand(row: HTMLElement) {
            return Array.from(row.querySelectorAll('[data-rw-form-collection-remove], [data-rw-form-collection-duplicate]'))
                .find(command => command.closest(rowSelector) === row && this.focusable(command as HTMLElement)) as HTMLElement | undefined;
        }

        private focusable(element: HTMLElement) {
            if ((element as HTMLButtonElement).disabled) return false;
            let current: HTMLElement | null = element;
            while (current) {
                if (current.hidden || current.getAttribute(rowStateAttribute) === 'removed' || current.hasAttribute('inert') || (current.localName === 'fieldset' && (current as HTMLFieldSetElement).disabled)) return false;
                if (typeof window.getComputedStyle === 'function') {
                    const style = window.getComputedStyle(current);
                    if (style.display === 'none' || style.visibility === 'hidden' || style.visibility === 'collapse') return false;
                }
                current = current.parentElement;
            }
            return true;
        }

        private toggleEvent(toggle: HTMLElement, target: HTMLElement, type: string, visible: boolean, cancelable: boolean) {
            return toggle.dispatchEvent(new CustomEvent(type, {
                bubbles: true,
                cancelable,
                detail: { form: this.form, control: toggle, target, visible }
            }));
        }

        private rowEvent(
            root: HTMLElement,
            command: HTMLElement,
            row: HTMLElement,
            type: string,
            action: CollectionAction,
            index: string,
            cancelable: boolean,
            previousIndex: string | null = null) {
            const event = new CustomEvent(type, {
                bubbles: true,
                cancelable,
                detail: {
                    form: this.form,
                    root,
                    control: command,
                    row,
                    index,
                    previousIndex,
                    action,
                    removeMode: action === 'mark-remove' ? 'mark' : action === 'physical-remove' ? 'physical' : null
                }
            });
            (command.isConnected ? command : root).dispatchEvent(event);
            return !event.defaultPrevented;
        }

        private status(root: HTMLElement) {
            const existing = this.owned(root, '[data-rw-form-collection-status]')[0];
            if (existing) return existing;
            const status = document.createElement('span');
            status.setAttribute('data-rw-form-collection-status', 'true');
            status.setAttribute('data-rw-form-collection-status-generated', 'true');
            status.setAttribute('aria-live', 'polite');
            status.setAttribute('role', 'status');
            root.appendChild(status);
            return status;
        }

        private focusRow(row: HTMLElement, trigger: HTMLElement) {
            const controls = this.controls(row).filter(candidate => this.focusable(candidate));
            const editable = controls.find(candidate => !['hidden', 'file', 'submit', 'button', 'reset'].includes(candidate.type) && !(candidate as HTMLInputElement).readOnly);
            const command = controls.find(candidate => candidate.localName === 'button' && ['add', 'duplicate', 'remove'].some(action => candidate.hasAttribute(`data-rw-form-collection-${action}`)));
            (editable ?? command ?? trigger).focus();
        }

        private controls(root: Element) {
            return Array.from(root.querySelectorAll('input, select, textarea, button')) as FormControl[];
        }

        private name(root: HTMLElement) {
            return (root.getAttribute('data-rw-form-collection') ?? '').trim();
        }

        private label(root: HTMLElement) {
            return root.getAttribute('data-rw-form-collection-label')?.trim()
                || this.name(root)
                || 'row';
        }

    }

    type FormControl = HTMLInputElement | HTMLSelectElement | HTMLTextAreaElement | HTMLButtonElement;

    const manager = new FormInteractionsManager();
    window.RazorWire = { ...(window.RazorWire || {}), formInteractionsManager: manager };

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', () => manager.start());
    } else {
        manager.start();
    }
})();
