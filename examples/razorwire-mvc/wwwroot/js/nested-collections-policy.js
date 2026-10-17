// Consumer policy only. Structural cloning, binding paths and DOM references belong to RazorWire.
(() => {
    const initialize = row => {
        for (const input of row.querySelectorAll('input[name$=".Date"]')) input.setAttribute('data-date-policy-ready', 'true');
    };
    document.querySelectorAll('[data-nested-proof]').forEach(initialize);
    for (const eventName of ['add', 'duplicate']) {
        document.addEventListener(`razorwire:form-collection:${eventName}`, event => {
            if (event.detail.root.closest('[data-nested-proof]')) initialize(event.detail.row);
        });
    }
    const enforceDateLimit = event => {
        const root = event.detail.root;
        if (!root.closest('[data-nested-proof]') || !root.getAttribute('data-rw-form-collection').endsWith('.Dates')) return;
        const rows = [...root.querySelectorAll('[data-rw-form-collection-row]')].filter(row => row.closest('[data-rw-form-collection]') === root && row.dataset.rwFormCollectionRowState !== 'removed');
        if (rows.length >= 8) { event.preventDefault(); root.querySelector('[data-rw-form-collection-status]').textContent = 'Use at most eight dates per action.'; }
    };
    for (const action of ['add', 'duplicate']) document.addEventListener(`razorwire:form-collection:before-${action}`, enforceDateLimit);
})();
