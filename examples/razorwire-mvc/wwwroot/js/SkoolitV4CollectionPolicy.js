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
        endControls.hidden = !isRange;
        endControls.disabled = !isRange;
        endControls.setAttribute("aria-hidden", String(!isRange));
    }

    function syncDateRows(scope) {
        if (!(scope instanceof HTMLElement)) return;
        if (scope.matches(dateRowSelector)) syncDateRow(scope);
        scope.querySelectorAll(dateRowSelector).forEach(syncDateRow);
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
        syncDateRows(row);
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
