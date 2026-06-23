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
                        `${this.describe(marker)} uses RazorWire form interactions outside a form.`,
                        'RazorWire leaves the marker unbound because form interactions are scoped to the nearest form.',
                        'Move the marker inside a form so submitted fields and target lookup stay scoped.');
                }
            }

            for (const form of forms) {
                this.register(form);
            }

            for (const form of Array.from(this.controllers.keys())) {
                if (!form.isConnected || !forms.has(form)) {
                    this.unregister(form);
                }
            }

            this.prune();
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
            for (const [form, controller] of this.controllers) {
                if (!form.isConnected) {
                    controller.disconnect();
                    this.controllers.delete(form);
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

        private describe(element: Element) {
            const id = element.getAttribute('id');
            return `<${element.tagName.toLowerCase()}${id ? ` id="${id}"` : ''}>`;
        }
    }

    class FormInteractionsController {
        private clickListener: EventListener = event => this.handleClick(event);
        private changeListener: EventListener = event => this.handleChange(event);
        private generatedStatus: HTMLElement | null = null;
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
            this.generatedStatus?.remove();
            this.generatedStatus = null;
        }

        refresh() {
            this.syncToggles();
            this.validateCollections();
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

            const add = target.closest('[data-rw-form-collection-add]');
            if (add && this.form.contains(add)) {
                if (!this.shouldHandleCollectionCommand(add)) return;
                event.preventDefault();
                this.addRow(add as HTMLElement);
                return;
            }

            const duplicate = target.closest('[data-rw-form-collection-duplicate]');
            if (duplicate && this.form.contains(duplicate)) {
                if (!this.shouldHandleCollectionCommand(duplicate)) return;
                event.preventDefault();
                this.duplicateRow(duplicate as HTMLElement);
                return;
            }

            const remove = target.closest('[data-rw-form-collection-remove]');
            if (remove && this.form.contains(remove)) {
                if (!this.shouldHandleCollectionCommand(remove)) return;
                event.preventDefault();
                this.removeRow(remove as HTMLElement);
            }
        }

        private syncToggles() {
            const toggles = Array.from(this.form.querySelectorAll('[data-rw-form-toggle]')) as HTMLElement[];
            for (const toggle of toggles) {
                this.syncToggle(toggle);
            }
        }

        private syncButtonToggle(toggle: HTMLElement) {
            const key = (toggle.getAttribute('data-rw-form-toggle') ?? '').trim();
            if (!key) {
                this.syncToggle(toggle);
                return;
            }

            const targets = this.findToggleTargets(key);
            if (targets.length === 0) {
                this.syncToggle(toggle);
                return;
            }

            const currentExpanded = toggle.getAttribute('aria-expanded');
            const visible = currentExpanded === null
                ? targets.every(target => (target as HTMLElement).hidden)
                : currentExpanded !== 'true';

            this.syncToggle(toggle, visible);
        }

        private syncToggle(toggle: HTMLElement, visibleOverride: boolean | null = null) {
            const key = (toggle.getAttribute('data-rw-form-toggle') ?? '').trim();
            if (!key) {
                this.manager.recordDiagnostic(
                    'data-rw-form-toggle is blank.',
                    'RazorWire cannot match this control to a target.',
                    'Set data-rw-form-toggle and data-rw-form-toggle-target to the same non-empty name.');
                return;
            }

            const targets = this.findToggleTargets(key);
            if (targets.length === 0) {
                if (this.hasCrossFormToggleTarget(key)) {
                    this.manager.recordDiagnostic(
                        `data-rw-form-toggle="${key}" points to a target outside its form.`,
                        'RazorWire leaves the target unbound because conditional form mechanics are scoped to one submitted form.',
                        `Move data-rw-form-toggle-target="${key}" inside the same form as the toggle, or duplicate the toggle-target pair per form.`);
                } else {
                    this.manager.recordDiagnostic(
                        `data-rw-form-toggle="${key}" has no matching target.`,
                        'Changing the control will not reveal, hide, enable, or disable any fields.',
                        `Add data-rw-form-toggle-target="${key}" to a target inside the same form.`);
                }

                return;
            }

            const visible = visibleOverride ?? this.resolveToggleVisible(toggle);
            const firstTarget = targets[0] as HTMLElement;
            const before = this.dispatchToggleEvent(toggle, firstTarget, 'razorwire:form-toggle:before-change', visible, true);
            if (before.defaultPrevented) return;

            for (const target of targets) {
                this.applyToggleTarget(toggle, target as HTMLElement, visible);
            }

            toggle.setAttribute('data-rw-form-toggle-state', visible ? 'shown' : 'hidden');
            this.dispatchToggleEvent(toggle, firstTarget, 'razorwire:form-toggle:change', visible, false);
        }

        private findToggleTargets(key: string) {
            const escaped = this.escapeAttributeValue(key);
            return Array.from(this.form.querySelectorAll(`[data-rw-form-toggle-target="${escaped}"]`));
        }

        private hasCrossFormToggleTarget(key: string) {
            const escaped = this.escapeAttributeValue(key);
            return Array.from(document.querySelectorAll(`[data-rw-form-toggle-target="${escaped}"]`))
                .some(target => !this.form.contains(target));
        }

        private applyToggleTarget(toggle: HTMLElement, target: HTMLElement, visible: boolean) {
            target.hidden = !visible;
            target.setAttribute('data-rw-form-toggle-target-state', visible ? 'shown' : 'hidden');
            if (target.id && !toggle.hasAttribute('aria-controls')) {
                toggle.setAttribute('aria-controls', target.id);
            }
            if (this.canExpand(toggle)) {
                toggle.setAttribute('aria-expanded', visible ? 'true' : 'false');
            }

            const shouldDisable = target.getAttribute('data-rw-form-toggle-disable-when-hidden') === 'true';
            if (!shouldDisable) return;

            for (const control of this.getControls(target)) {
                if (!visible) {
                    if (this.shouldPreserveRemovedRowSubmissionControl(control)) {
                        continue;
                    }

                    if (!control.disabled) {
                        control.disabled = true;
                        control.setAttribute('data-rw-disabled-by-form-toggle', 'true');
                    }
                } else if (control.getAttribute('data-rw-disabled-by-form-toggle') === 'true') {
                    control.disabled = false;
                    control.removeAttribute('data-rw-disabled-by-form-toggle');
                }
            }
        }

        private resolveToggleVisible(toggle: HTMLElement) {
            const input = toggle as HTMLInputElement;
            let active: boolean;
            if (toggle.localName === 'input' && (input.type === 'checkbox' || input.type === 'radio')) {
                active = input.checked;
            } else if (toggle.localName === 'select' || toggle.localName === 'textarea' || toggle.localName === 'input') {
                active = ((toggle as HTMLInputElement | HTMLSelectElement | HTMLTextAreaElement).value ?? '').length > 0;
            } else {
                active = toggle.getAttribute('aria-expanded') !== 'false';
            }

            return toggle.getAttribute('data-rw-form-toggle-invert') === 'true' ? !active : active;
        }

        private addRow(command: HTMLElement) {
            const root = this.resolveCollectionRoot(command);
            if (!root) return;

            const template = this.resolveTemplate(root);
            if (!template) return;

            const collection = this.collectionName(root);
            const index = this.allocateIndex(root, collection);
            const row = this.cloneTemplateRow(template, index, collection);
            if (!row) return;

            if (!this.dispatchCollectionEvent(root, command, row, 'razorwire:form-collection:before-add', 'add', index, true)) {
                return;
            }

            this.clearRowValues(row, false);
            this.rewriteHiddenIndexValues(row, '__index__', index);
            this.insertRow(root, template, row);
            row.setAttribute('data-rw-form-index', index);
            this.ensureIndexMarker(row, collection, index);
            this.dispatchCollectionEvent(root, command, row, 'razorwire:form-collection:add', 'add', index, false);
            this.announce(root, `Added ${this.collectionLabel(root)}.`);
            this.focusFirstControl(row);
        }

        private duplicateRow(command: HTMLElement) {
            const root = this.resolveCollectionRoot(command);
            const sourceRow = command.closest('[data-rw-form-collection-row]') as HTMLElement | null;
            if (!root || !sourceRow) return;

            const collection = this.collectionName(root);
            const sourceIndex = this.rowIndex(sourceRow, collection);
            if (!sourceIndex) {
                this.manager.recordDiagnostic(
                    `Collection "${collection}" duplicate command has no source row index.`,
                    'RazorWire cannot safely rewrite cloned field names, ids, labels, or validation references without the source sparse index.',
                    `Add an enabled <input type="hidden" name="${collection}.index" value="..."> marker or data-rw-form-index to the row before enabling duplicate.`);
                return;
            }

            const index = this.allocateIndex(root, collection);
            const row = sourceRow.cloneNode(true) as HTMLElement;
            this.rewriteAttributes(row, sourceIndex, index);
            row.setAttribute('data-rw-form-index', index);
            this.copyRowValues(sourceRow, row);
            this.rewriteHiddenIndexValues(row, sourceIndex, index);
            this.ensureIndexMarker(row, collection, index);
            this.clearValidationState(row);

            if (!this.dispatchCollectionEvent(root, command, row, 'razorwire:form-collection:before-duplicate', 'duplicate', index, true, sourceIndex)) {
                return;
            }

            sourceRow.insertAdjacentElement('afterend', row);
            this.dispatchCollectionEvent(root, command, row, 'razorwire:form-collection:duplicate', 'duplicate', index, false, sourceIndex);
            this.announce(root, `Duplicated ${this.collectionLabel(root)}.`);
            this.focusFirstControl(row);
        }

        private removeRow(command: HTMLElement) {
            const root = this.resolveCollectionRoot(command);
            const row = command.closest('[data-rw-form-collection-row]') as HTMLElement | null;
            if (!root || !row) return;

            const mode = this.resolveRemoveMode(root, command);
            const collection = this.collectionName(root);
            const index = this.rowIndex(row, collection) ?? '';
            const action: CollectionAction = mode === 'mark' ? 'mark-remove' : 'physical-remove';
            if (!this.dispatchCollectionEvent(root, command, row, 'razorwire:form-collection:before-remove', action, index, true)) {
                return;
            }

            const focusTarget = this.resolveRemoveFocusTarget(root, row);
            if (mode === 'mark') {
                if (!this.markRowForRemoval(root, row)) return;
                this.announce(root, `Marked ${this.collectionLabel(root)} for removal.`);
            } else {
                row.remove();
                this.announce(root, `Removed ${this.collectionLabel(root)}.`);
            }

            this.dispatchCollectionEvent(root, command, row, 'razorwire:form-collection:remove', action, index, false);
            focusTarget?.focus();
        }

        private validateCollections() {
            for (const root of Array.from(this.form.querySelectorAll('[data-rw-form-collection]')) as HTMLElement[]) {
                const name = this.collectionName(root);
                if (!name) {
                    this.manager.recordDiagnostic(
                        'data-rw-form-collection is blank.',
                        'RazorWire cannot allocate ASP.NET collection indexes for this root.',
                        'Set data-rw-form-collection to the model property name, such as "Actions".');
                    continue;
                }

                if (root.querySelector('[data-rw-form-collection]') !== null) {
                    this.manager.recordDiagnostic(
                        `Collection "${name}" contains another data-rw-form-collection root.`,
                        'Nested collection rewriting is intentionally unsupported in stable v1.',
                        'Keep RazorWire form collections one-dimensional and use app-owned JavaScript for nested editors.');
                }

                this.resolveTemplate(root);
                this.validateCollectionRows(root, name);
                for (const command of Array.from(root.querySelectorAll('[data-rw-form-collection-add], [data-rw-form-collection-duplicate], [data-rw-form-collection-remove]'))) {
                    if (command.tagName !== 'BUTTON') {
                        this.manager.recordDiagnostic(
                            `${command.tagName.toLowerCase()} uses a collection command attribute but is not a button.`,
                            'RazorWire leaves the command unbound to preserve keyboard and form submission semantics.',
                            'Move the command attribute to a button with type="button".');
                    }
                }
            }
        }

        private validateCollectionRows(root: HTMLElement, collection: string) {
            const markerName = `${collection}.index`;
            const seen = new Set<string>();
            for (const row of Array.from(root.querySelectorAll('[data-rw-form-collection-row]')) as HTMLElement[]) {
                const marker = row.querySelector(`input[name="${this.escapeAttributeValue(markerName)}"]`) as HTMLInputElement | null;
                if (!marker) {
                    this.manager.recordDiagnostic(
                        `Collection "${collection}" row is missing ${markerName}.`,
                        'ASP.NET Core will skip the row because sparse collection binding relies on an enabled hidden .index marker.',
                        `Add <input type="hidden" name="${markerName}" value="..."> inside each collection row.`);
                    continue;
                }

                const index = marker.value.trim();
                if (!index || marker.disabled) {
                    this.manager.recordDiagnostic(
                        `Collection "${collection}" row has an invalid ${markerName} marker.`,
                        'ASP.NET Core may skip the row because the .index marker is blank or disabled.',
                        'Keep the hidden .index marker enabled and set it to the row index token that appears in field names.');
                    continue;
                }

                if (seen.has(index)) {
                    this.manager.recordDiagnostic(
                        `Collection "${collection}" has duplicate index "${index}".`,
                        'ASP.NET Core model binding can collapse or overwrite rows that share the same sparse collection index.',
                        'Give every row a unique .index value and re-render failed validation with the posted sparse indices.');
                }
                seen.add(index);

                const rowIndex = row.getAttribute('data-rw-form-index');
                if (rowIndex && rowIndex !== index) {
                    this.manager.recordDiagnostic(
                        `Collection "${collection}" row index "${rowIndex}" does not match ${markerName} "${index}".`,
                        'RazorWire commands may allocate or duplicate from a different index than the one ASP.NET Core submits.',
                        'Keep data-rw-form-index and the hidden .index marker aligned, or omit data-rw-form-index and let the marker be canonical.');
                }

                if (row.querySelector('input[type="file"]')) {
                    this.manager.recordDiagnostic(
                        `Collection "${collection}" contains a file input that duplicate cannot clone.`,
                        'Browsers block programmatic file input cloning, so duplicated rows will not carry selected files.',
                        'Keep file uploads app-owned: ask the user to reselect files after duplication or disable duplicate commands for file rows.');
                }
            }
        }

        private resolveCollectionRoot(command: Element) {
            const root = command.closest('[data-rw-form-collection]') as HTMLElement | null;
            if (!root || !this.form.contains(root)) {
                this.manager.recordDiagnostic(
                    'Collection command has no data-rw-form-collection root.',
                    'RazorWire cannot determine which model-bound collection the command should mutate.',
                    'Wrap rows, template, and commands in an element with data-rw-form-collection.');
                return null;
            }

            if (command.tagName !== 'BUTTON') {
                this.manager.recordDiagnostic(
                    `${command.tagName.toLowerCase()} uses a collection command attribute but is not a button.`,
                    'RazorWire leaves the command unbound to preserve keyboard and form submission semantics.',
                    'Move the command attribute to a button with type="button".');
                return null;
            }

            return root;
        }

        private shouldHandleCollectionCommand(command: Element) {
            if (command.tagName === 'BUTTON') return true;

            this.resolveCollectionRoot(command);
            return false;
        }

        private resolveTemplate(root: HTMLElement) {
            const template = root.querySelector('template[data-rw-form-collection-template]') as HTMLTemplateElement | null;
            const name = this.collectionName(root);
            if (!template) {
                this.manager.recordDiagnostic(
                    `Collection "${name}" is missing a template.`,
                    'Add commands cannot create model-bound rows.',
                    'Add an app-authored <template data-rw-form-collection-template> with a row that uses the __index__ token.');
                return null;
            }

            if (!template.innerHTML.includes('__index__')) {
                this.manager.recordDiagnostic(
                    `Collection "${name}" template does not contain __index__.`,
                    'New rows would not bind to a unique ASP.NET collection index.',
                    'Use __index__ in names, ids, labels, validation references, and the hidden .index marker.');
                return null;
            }

            return template;
        }

        private cloneTemplateRow(template: HTMLTemplateElement, index: string, collection: string) {
            const fragment = template.content.cloneNode(true) as DocumentFragment;
            const row = fragment.querySelector('[data-rw-form-collection-row]') as HTMLElement | null
                ?? Array.from(fragment.children).find(child => child instanceof HTMLElement) as HTMLElement | null;
            if (!row) {
                this.manager.recordDiagnostic(
                    `Collection "${collection}" template has no row element.`,
                    'Add commands cannot insert a predictable collection row.',
                    'Add data-rw-form-collection-row to the row root inside the template.');
                return null;
            }

            this.rewriteAttributes(row, '__index__', index);
            return row;
        }

        private insertRow(root: HTMLElement, template: HTMLTemplateElement, row: HTMLElement) {
            const before = root.querySelector('[data-rw-form-collection-add]') ?? template;
            before.insertAdjacentElement('beforebegin', row);
        }

        private rewriteAttributes(row: HTMLElement, oldIndex: string, newIndex: string) {
            const elements = [row, ...Array.from(row.querySelectorAll('*'))] as HTMLElement[];
            for (const element of elements) {
                for (const attribute of Array.from(element.attributes)) {
                    if (!this.shouldRewriteIndexAttribute(attribute.name)) continue;
                    const next = this.rewriteIndexReference(attribute.value, oldIndex, newIndex);
                    if (next === attribute.value) continue;
                    element.setAttribute(attribute.name, next);
                }
            }
        }

        private shouldRewriteIndexAttribute(attributeName: string) {
            return /^(name|id|for|list|data-valmsg-for|aria-controls|aria-describedby|aria-errormessage|aria-labelledby|aria-owns)$/i
                .test(attributeName);
        }

        private rewriteIndexReference(value: string, oldIndex: string, newIndex: string) {
            if (!oldIndex) return value;
            if (oldIndex === '__index__') {
                return value.replaceAll('__index__', newIndex);
            }

            return value
                .replaceAll(`[${oldIndex}]`, `[${newIndex}]`)
                .replaceAll(`_${oldIndex}__`, `_${newIndex}__`);
        }

        private rewriteHiddenIndexValues(row: HTMLElement, oldIndex: string, newIndex: string) {
            if (!oldIndex) return;

            for (const input of Array.from(row.querySelectorAll('input[type="hidden"]')) as HTMLInputElement[]) {
                if (input.name.endsWith('.index')) continue;

                const nextValue = this.rewriteIndexCarrierValue(input.value, input.name, oldIndex, newIndex);
                if (nextValue !== input.value) {
                    input.value = nextValue;
                }

                const attributeValue = input.getAttribute('value');
                if (attributeValue === null) continue;

                const nextAttributeValue = this.rewriteIndexCarrierValue(attributeValue, input.name, oldIndex, newIndex);
                if (nextAttributeValue !== attributeValue) {
                    input.setAttribute('value', nextAttributeValue);
                }
            }
        }

        private rewriteIndexCarrierValue(value: string, name: string, oldIndex: string, newIndex: string) {
            if (value === '__index__') return newIndex;
            if (value === oldIndex && /(^|\.)(clientindex|rowindex)$/i.test(name)) return newIndex;
            return this.rewriteIndexReference(value, oldIndex, newIndex);
        }

        private ensureIndexMarker(row: HTMLElement, collection: string, index: string) {
            const markerName = `${collection}.index`;
            let marker = Array.from(row.querySelectorAll(`input[name="${this.escapeAttributeValue(markerName)}"]`))
                .find(input => input instanceof HTMLInputElement) as HTMLInputElement | undefined;
            if (!marker) {
                marker = document.createElement('input');
                marker.type = 'hidden';
                marker.name = markerName;
                marker.setAttribute('type', 'hidden');
                marker.setAttribute('name', markerName);
                row.insertAdjacentElement('afterbegin', marker);
            }

            marker.value = index;
            marker.disabled = false;
        }

        private allocateIndex(root: HTMLElement, collection: string) {
            const values = this.indexValues(root, collection);
            const numeric = values
                .map(value => Number.parseInt(value, 10))
                .filter(value => Number.isFinite(value));
            if (numeric.length === values.length && numeric.length > 0) {
                return String(Math.max(...numeric) + 1);
            }

            this.indexSeed += 1;
            let candidate = `rw${Date.now().toString(36)}${this.indexSeed.toString(36)}`;
            while (values.includes(candidate)) {
                this.indexSeed += 1;
                candidate = `rw${Date.now().toString(36)}${this.indexSeed.toString(36)}`;
            }

            return candidate;
        }

        private indexValues(root: HTMLElement, collection: string) {
            const markerName = `${collection}.index`;
            const values = Array.from(root.querySelectorAll(`input[name="${this.escapeAttributeValue(markerName)}"]`))
                .filter(input => input instanceof HTMLInputElement && !input.disabled)
                .map(input => (input as HTMLInputElement).value)
                .filter(Boolean);
            for (const row of Array.from(root.querySelectorAll('[data-rw-form-collection-row]'))) {
                const value = row.getAttribute('data-rw-form-index');
                if (value) values.push(value);
            }

            return Array.from(new Set(values));
        }

        private rowIndex(row: HTMLElement, collection: string) {
            const marker = row.querySelector(`input[name="${this.escapeAttributeValue(`${collection}.index`)}"]`) as HTMLInputElement | null;
            return marker?.value || row.getAttribute('data-rw-form-index');
        }

        private copyRowValues(sourceRow: HTMLElement, cloneRow: HTMLElement) {
            const sourceControls = this.getControls(sourceRow);
            const cloneControls = this.getControls(cloneRow);
            for (let i = 0; i < cloneControls.length; i += 1) {
                const source = sourceControls[i];
                const clone = cloneControls[i];
                if (!source || !clone) continue;
                this.copyControlValue(source, clone);
            }
        }

        private copyControlValue(source: FormControl, clone: FormControl) {
            if (clone.name.endsWith('.index')) {
                return;
            }

            if (clone instanceof HTMLInputElement && clone.type === 'file') {
                clone.value = '';
                return;
            }

            if (this.shouldClearHiddenOnDuplicate(clone)) {
                clone.value = '';
                return;
            }

            if (source instanceof HTMLInputElement && clone instanceof HTMLInputElement) {
                if (source.type === 'checkbox' || source.type === 'radio') {
                    clone.checked = source.checked;
                } else {
                    clone.value = source.value;
                }
            } else if (source instanceof HTMLSelectElement && clone instanceof HTMLSelectElement) {
                clone.value = source.value;
            } else if (source instanceof HTMLTextAreaElement && clone instanceof HTMLTextAreaElement) {
                clone.value = source.value;
            }
        }

        private clearRowValues(row: HTMLElement, duplicate: boolean) {
            for (const control of this.getControls(row)) {
                if (control instanceof HTMLInputElement) {
                    if (control.type === 'hidden') continue;
                    if (control.type === 'checkbox' || control.type === 'radio') {
                        control.checked = false;
                    } else if (control.type !== 'file') {
                        control.value = '';
                    }
                } else if (control instanceof HTMLSelectElement) {
                    control.selectedIndex = -1;
                } else if (control instanceof HTMLTextAreaElement) {
                    control.value = '';
                }
            }

            if (!duplicate) {
                this.clearValidationState(row);
            }
        }

        private clearValidationState(row: HTMLElement) {
            for (const element of [row, ...Array.from(row.querySelectorAll('*'))] as HTMLElement[]) {
                element.removeAttribute('aria-invalid');
                if (element.hasAttribute('data-valmsg-for')) {
                    element.textContent = '';
                    element.removeAttribute('data-valmsg-replace');
                }
            }
        }

        private shouldClearHiddenOnDuplicate(control: FormControl) {
            if (!(control instanceof HTMLInputElement) || control.type !== 'hidden') return false;
            if (control.hasAttribute('data-rw-form-collection-copyable')) return false;
            if (control.name.endsWith('.index')) return false;
            return /(^|\.)(id|rowversion|concurrencystamp|concurrencytoken|etag|isdeleted|delete)$/i.test(control.name);
        }

        private markRowForRemoval(root: HTMLElement, row: HTMLElement) {
            const selector = root.getAttribute('data-rw-form-collection-delete-field') || '[data-rw-form-collection-delete-field]';
            let field: Element | null = null;
            try {
                field = row.querySelector(selector);
            } catch {
                this.manager.recordDiagnostic(
                    `Collection "${this.collectionName(root)}" uses an invalid delete-field selector "${selector}".`,
                    'RazorWire cannot resolve the delete field for mark-remove mode.',
                    'Provide a valid CSS selector or use [data-rw-form-collection-delete-field] on the delete input.');
                return false;
            }

            if (!(field instanceof HTMLInputElement) || field.localName !== 'input') {
                this.manager.recordDiagnostic(
                    `Collection "${this.collectionName(root)}" mark-remove has no app-owned delete field.`,
                    'RazorWire cannot express persisted row deletion without guessing app persistence semantics.',
                    'Add a hidden delete field and mark it with data-rw-form-collection-delete-field, or use physical remove for draft-only rows.');
                return false;
            }

            const deleteField = field as HTMLInputElement;
            deleteField.setAttribute('data-rw-form-collection-delete-field', 'true');
            row.hidden = true;
            row.setAttribute('data-rw-form-collection-row-state', 'removed');
            deleteField.disabled = false;
            deleteField.value = deleteField.getAttribute('data-rw-form-collection-delete-value') || 'true';

            for (const control of this.getControls(row)) {
                if (control === deleteField || control.name.endsWith('.index') || control.hasAttribute('data-rw-form-collection-preserve')) {
                    control.disabled = false;
                } else if (!control.disabled) {
                    control.disabled = true;
                    control.setAttribute('data-rw-disabled-by-form-collection-remove', 'true');
                }
            }

            return true;
        }

        private shouldPreserveRemovedRowSubmissionControl(control: FormControl) {
            const row = control.closest('[data-rw-form-collection-row]') as HTMLElement | null;
            if (row?.getAttribute('data-rw-form-collection-row-state') !== 'removed') {
                return false;
            }

            return control.name.endsWith('.index')
                || control.hasAttribute('data-rw-form-collection-delete-field')
                || control.hasAttribute('data-rw-form-collection-preserve');
        }

        private resolveRemoveMode(root: HTMLElement, command: HTMLElement) {
            const commandMode = command.getAttribute('data-rw-form-collection-remove');
            const raw = command.getAttribute('data-rw-form-collection-remove-mode')
                || (commandMode && commandMode !== 'true' ? commandMode : null)
                || root.getAttribute('data-rw-form-collection-remove-mode')
                || 'physical';
            return raw.toLowerCase() === 'mark' || raw.toLowerCase() === 'mark-remove' ? 'mark' : 'physical';
        }

        private resolveRemoveFocusTarget(root: HTMLElement, row: HTMLElement) {
            const rows = Array.from(root.querySelectorAll('[data-rw-form-collection-row]')) as HTMLElement[];
            const index = rows.indexOf(row);
            for (const candidate of rows.slice(index + 1)) {
                const command = this.firstFocusableCollectionCommand(candidate);
                if (command) return command;
            }

            for (const candidate of rows.slice(0, index).reverse()) {
                const command = this.firstFocusableCollectionCommand(candidate);
                if (command) return command;
            }

            const add = root.querySelector('[data-rw-form-collection-add]') as HTMLElement | null;
            return add && this.canReceiveFocus(add) ? add : null;
        }

        private firstFocusableCollectionCommand(row: HTMLElement) {
            return Array.from(row.querySelectorAll('[data-rw-form-collection-remove], [data-rw-form-collection-duplicate]'))
                .find(command => command instanceof HTMLElement && this.canReceiveFocus(command)) as HTMLElement | undefined;
        }

        private canReceiveFocus(element: HTMLElement) {
            if ((element as HTMLButtonElement).disabled) return false;

            let current: HTMLElement | null = element;
            while (current) {
                if (current.hidden) return false;
                current = current.parentElement;
            }

            return true;
        }

        private dispatchToggleEvent(toggle: HTMLElement, target: HTMLElement, type: string, visible: boolean, cancelable: boolean) {
            const event = new CustomEvent(type, {
                bubbles: true,
                cancelable,
                detail: { form: this.form, control: toggle, target, visible }
            });
            toggle.dispatchEvent(event);
            return event;
        }

        private dispatchCollectionEvent(
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
            command.dispatchEvent(event);
            return !event.defaultPrevented;
        }

        private announce(root: HTMLElement, message: string) {
            const status = this.resolveStatus(root);
            status.textContent = message;
        }

        private resolveStatus(root: HTMLElement) {
            const existing = root.querySelector('[data-rw-form-collection-status]') as HTMLElement | null;
            if (existing) return existing;
            if (!this.generatedStatus) {
                this.generatedStatus = document.createElement('span');
                this.generatedStatus.setAttribute('data-rw-form-collection-status', 'true');
                this.generatedStatus.setAttribute('data-rw-form-collection-status-generated', 'true');
                this.generatedStatus.setAttribute('aria-live', 'polite');
                this.generatedStatus.setAttribute('role', 'status');
                this.form.appendChild(this.generatedStatus);
            }

            return this.generatedStatus;
        }

        private focusFirstControl(row: HTMLElement) {
            const control = this.getControls(row)
                .find(candidate => !candidate.disabled && candidate.type !== 'hidden' && candidate.type !== 'file');
            control?.focus();
        }

        private getControls(root: Element) {
            return Array.from(root.querySelectorAll('input, select, textarea, button')) as FormControl[];
        }

        private collectionName(root: HTMLElement) {
            return (root.getAttribute('data-rw-form-collection') ?? '').trim();
        }

        private collectionLabel(root: HTMLElement) {
            return root.getAttribute('data-rw-form-collection-label')?.trim()
                || this.collectionName(root)
                || 'row';
        }

        private canExpand(element: HTMLElement) {
            return element instanceof HTMLButtonElement
                || element instanceof HTMLInputElement
                || element.getAttribute('role') === 'button'
                || element.hasAttribute('aria-expanded');
        }

        private escapeAttributeValue(value: string) {
            return value.replace(/\\/g, '\\\\').replace(/"/g, '\\"');
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
