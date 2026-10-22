(() => {
    "use strict";

    const formSelector = "form[data-v4-form]";
    const dateRowSelector = "[data-v4-date-row]";
    const unsavedFeedback = "Unsaved date changes. Save labels to check the date and its year.";

    function syncDateRow(row) {
        if (!(row instanceof HTMLElement) || !row.matches(dateRowSelector)) return;

        const shape = row.querySelector('select[data-v4-date-shape]');
        const endControls = row.querySelector('[data-date-endpoint="end"]');
        if (!shape || !(endControls instanceof HTMLFieldSetElement)) return;

        const isRange = shape.value === "Range";
        // Server-rejected values must remain editable and submitted until corrected.
        // Read rendered messages so a duplicate's cleared validation cannot inherit this exception.
        const hasEndErrors = [...endControls.querySelectorAll("[data-valmsg-for]")]
            .some(message => message.textContent.trim().length > 0);
        const hideEnd = !isRange && !hasEndErrors;
        endControls.hidden = hideEnd;
        endControls.disabled = hideEnd;
        endControls.setAttribute("aria-hidden", String(hideEnd));
    }

    function syncDateRows(scope, duplicated = false) {
        if (!(scope instanceof HTMLElement)) return;
        const rows = [...(scope.matches(dateRowSelector) ? [scope] : []), ...scope.querySelectorAll(dateRowSelector)];
        for (const row of rows) {
            syncDateRow(row);
            if (duplicated) {
                const feedback = row.querySelector("[data-date-resolution]");
                if (feedback) feedback.textContent = unsavedFeedback;
            }
        }
    }

    function onCollectionMutation(event) {
        const detail = event.detail;
        const root = detail?.root;
        const row = detail?.row;
        if (!(root instanceof HTMLElement)
            || !(row instanceof HTMLElement)
            || !root.matches("[data-rw-form-collection]")
            || !root.closest(formSelector)?.contains(row)
            || (row !== root && !root.contains(row))) {
            return;
        }

        // Parent duplicate has one parent event; initialize only its date descendants.
        // A child add/duplicate supplies that date row as detail.row.
        syncDateRows(row, event.type === "razorwire:form-collection:duplicate");
    }

    function onDateChange(event) {
        const target = event.target;
        if (!(target instanceof HTMLInputElement || target instanceof HTMLSelectElement)) return;
        const row = target.closest(dateRowSelector);
        if (!(row instanceof HTMLElement)) return;

        if (target.matches('select[data-v4-date-shape]')) syncDateRow(row);
        const feedback = row.querySelector("[data-date-resolution]");
        if (feedback) feedback.textContent = unsavedFeedback;
    }

    document.querySelectorAll(formSelector).forEach((form) => {
        syncDateRows(form);
        form.addEventListener("change", onDateChange);
        form.addEventListener("input", onDateChange);
        form.addEventListener("razorwire:form-collection:add", onCollectionMutation);
        form.addEventListener("razorwire:form-collection:duplicate", onCollectionMutation);
    });
})();
