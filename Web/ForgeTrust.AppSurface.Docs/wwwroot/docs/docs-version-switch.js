(() => {
    "use strict";

    const runtimeKey = "__appSurfaceDocsVersionSwitchRuntime";
    const runtimeVersion = "1";
    const previousRuntime = window[runtimeKey];

    if (previousRuntime?.version === runtimeVersion) {
        previousRuntime.init();
        return;
    }

    const rootSelector = "[data-docs-version-switch]";
    const dataSelector = 'script[type="application/json"][data-docs-version-data]';
    const noticeSelector = "[data-docs-version-notice]";
    const explanationId = "docs-version-explanation";
    const anchorIdsAttribute = "data-docs-version-anchor-ids";
    const baseHrefByLink = new WeakMap();
    let lastFocusedTransition = "";

    function isRecord(value) {
        return value !== null && typeof value === "object" && !Array.isArray(value);
    }

    function isNonEmptyString(value) {
        return typeof value === "string" && value.length > 0;
    }

    function isSourceKey(value) {
        return typeof value === "string" && /^[a-f0-9]{64}$/.test(value);
    }

    function isLocale(value) {
        return value === null || typeof value === "string";
    }

    function isSafeAppRelativeUrl(value) {
        if (typeof value !== "string"
            || !value.startsWith("/")
            || value.startsWith("//")
            || /[\\\u0000-\u001f\u007f-\u009f]/.test(value)) {
            return false;
        }

        try {
            const path = value.split(/[?#]/, 1)[0];
            const decodedPath = decodeURIComponent(path);
            return !/%2f|%5c/i.test(path)
                && !/[\\\u0000-\u001f\u007f-\u009f]/.test(decodedPath)
                && !decodedPath.split("/").some(segment => segment === "." || segment === "..")
                && new URL(value, window.location.origin).origin === window.location.origin;
        } catch {
            return false;
        }
    }

    function isCanonicalRoute(value) {
        if (typeof value !== "string"
            || value.startsWith("/")
            || /[?#\\\u0000-\u001f\u007f-\u009f]/.test(value)) {
            return false;
        }

        let decoded;
        try {
            decoded = decodeURIComponent(value);
        } catch {
            return false;
        }

        return !/[\\\u0000-\u001f\u007f-\u009f]/.test(decoded)
            && !/%2f|%5c/i.test(value)
            && !decoded.split("/").some(segment => segment === "." || segment === "..");
    }

    function isIdentity(value) {
        return isRecord(value)
            && isNonEmptyString(value.instance)
            && isNonEmptyString(value.version)
            && isNonEmptyString(value.pin)
            && isLocale(value.locale)
            && isCanonicalRoute(value.canonicalRoute);
    }

    function sameIdentity(left, right) {
        return isIdentity(left)
            && isIdentity(right)
            && left.instance === right.instance
            && left.version === right.version
            && left.pin === right.pin
            && left.locale === right.locale
            && left.canonicalRoute === right.canonicalRoute;
    }

    function readPageData(root) {
        const element = root.querySelector(dataSelector);
        if (!element) {
            return null;
        }

        let data;
        try {
            data = JSON.parse(element.textContent || "");
        } catch {
            return null;
        }

        if (!isRecord(data)
            || !isSourceKey(data.sourceKey)
            || !isNonEmptyString(data.version)
            || !isLocale(data.locale)
            || !isIdentity(data.target)
            || data.version !== data.target.version
            || data.locale !== data.target.locale
            || !Array.isArray(data.incoming)) {
            return null;
        }

        if (!isSafeAppRelativeUrl(data.pageUrl)
            || new URL(data.pageUrl, window.location.origin).pathname !== window.location.pathname) {
            return null;
        }

        return data;
    }

    function readChoiceAnchorIds(link) {
        const value = link.getAttribute(anchorIdsAttribute);
        if (value === null) {
            return null;
        }

        try {
            const ids = JSON.parse(value);
            if (!Array.isArray(ids) || ids.some(id => typeof id !== "string" || id.length === 0)) {
                return null;
            }
            return new Set(ids);
        } catch {
            return null;
        }
    }

    function readCurrentFragment() {
        if (window.location.hash.length <= 1) {
            return null;
        }

        let fragment;
        try {
            fragment = decodeURIComponent(window.location.hash.slice(1));
        } catch {
            return null;
        }

        return fragment && !/[\u0000-\u001f\u007f-\u009f]/.test(fragment) ? fragment : null;
    }

    function updateChoiceLink(link, data) {
        if (!baseHrefByLink.has(link)) {
            baseHrefByLink.set(link, link.getAttribute("href") || "");
        }

        const baseHref = baseHrefByLink.get(link);
        let target;
        try {
            target = new URL(baseHref, window.location.href);
        } catch {
            return;
        }

        if (target.origin !== window.location.origin) {
            return;
        }

        const sourceKeys = target.searchParams.getAll("docsFrom");
        target.search = "";
        if (sourceKeys.length === 1 && sourceKeys[0] === data.sourceKey) {
            target.searchParams.set("docsFrom", data.sourceKey);
        }

        const fragment = readCurrentFragment();
        const anchorIds = readChoiceAnchorIds(link);
        if (!target.hash && fragment && anchorIds?.has(fragment)) {
            target.hash = fragment;
        }

        link.setAttribute("href", `${target.pathname}${target.search}${target.hash}`);
    }

    function updateChoiceLinks(root, data) {
        if (!data) {
            return;
        }

        root.querySelectorAll(`a[href][${anchorIdsAttribute}]`).forEach(link => updateChoiceLink(link, data));
    }

    function getQuerySourceKey() {
        const values = new URLSearchParams(window.location.search).getAll("docsFrom");
        return values.length === 1 && isSourceKey(values[0]) ? values[0] : null;
    }

    function getValidatedIncoming(data, sourceKey) {
        if (!sourceKey || !data) {
            return null;
        }

        const matches = data.incoming.filter(row => isRecord(row) && row.sourceKey === sourceKey);
        if (matches.length !== 1) {
            return null;
        }

        const row = matches[0];
        if (!isSourceKey(row.sourceKey)
            || !sameIdentity(row.target, data.target)
            || !isNonEmptyString(row.reason)
            || typeof row.notice !== "string"
            || !isSafeAppRelativeUrl(row.returnUrl)
            || !isNonEmptyString(row.sourceVersion)
            || !isNonEmptyString(row.sourceTitle)) {
            return null;
        }

        return row;
    }

    function getMain(root) {
        return root.closest("main") || document.querySelector("#doc-content main, main");
    }

    function removeDescriptionReference() {
        document.querySelectorAll("[data-docs-version-described]").forEach(element => {
            const ids = (element.getAttribute("aria-describedby") || "")
                .split(/\s+/)
                .filter(id => id && id !== explanationId);
            if (ids.length === 0) {
                element.removeAttribute("aria-describedby");
            } else {
                element.setAttribute("aria-describedby", ids.join(" "));
            }
            element.removeAttribute("data-docs-version-described");
        });
    }

    function clearNotice() {
        document.querySelectorAll(noticeSelector).forEach(notice => notice.remove());
        removeDescriptionReference();
        lastFocusedTransition = "";
    }

    function getOrCreateNotice(root, row, stamp) {
        if (row.notice.length === 0) {
            document.querySelectorAll(noticeSelector).forEach(notice => notice.remove());
            removeDescriptionReference();
            return null;
        }

        let notice = document.querySelector(noticeSelector);
        if (!notice) {
            notice = document.createElement("aside");
            const main = getMain(root);
            const heading = main?.querySelector("h1");
            if (main && heading) {
                heading.insertAdjacentElement("beforebegin", notice);
            } else if (main) {
                main.prepend(notice);
            } else {
                root.insertAdjacentElement("beforebegin", notice);
            }
        }

        if (notice.dataset.docsVersionSwitchContext === stamp) {
            return notice;
        }

        notice.className = "docs-version-notice";
        notice.setAttribute("data-docs-version-notice", "");
        notice.dataset.docsVersionSwitchContext = stamp;
        notice.replaceChildren();

        const description = document.createElement("p");
        description.id = explanationId;
        description.textContent = row.notice;
        notice.append(description);

        const source = document.createElement("p");
        source.append(document.createTextNode(`${row.sourceTitle} · ${row.sourceVersion} `));
        const returnLink = document.createElement("a");
        returnLink.href = row.returnUrl;
        returnLink.textContent = "Return to original guide";
        source.append(returnLink);
        notice.append(source);

        return notice;
    }

    function getFocusTarget(root) {
        const main = getMain(root);
        if (!main) {
            return null;
        }

        const fragment = readCurrentFragment();
        if (fragment) {
            const target = document.getElementById(fragment);
            if (target && main.contains(target)) {
                return target;
            }
        }

        return main.querySelector("h1") || main;
    }

    function focusWithDescription(root, notice) {
        const target = getFocusTarget(root);
        if (!target) {
            return;
        }

        if (!target.hasAttribute("tabindex")) {
            target.setAttribute("tabindex", "-1");
        }

        if (notice?.querySelector(`#${explanationId}`)) {
            const ids = (target.getAttribute("aria-describedby") || "").split(/\s+/).filter(Boolean);
            if (!ids.includes(explanationId)) {
                ids.push(explanationId);
                target.setAttribute("aria-describedby", ids.join(" "));
            }
            target.setAttribute("data-docs-version-described", "");
        }

        target.focus({ preventScroll: true });
    }

    function initializeDisclosure(root, data) {
        const button = root.querySelector("[data-docs-version-toggle]");
        const options = root.querySelector("[data-docs-version-options]");
        const fallback = root.querySelector("[data-docs-version-fallback]");
        if (!button || !options || !fallback || !data) {
            if (button) {
                button.hidden = true;
                button.setAttribute("aria-expanded", "false");
                delete button.dataset.docsVersionSwitchEnhanced;
                delete button.dataset.docsVersionSwitchSourceKey;
            }
            if (options) {
                options.hidden = true;
            }
            if (fallback) {
                fallback.hidden = false;
            }
            return;
        }

        if (button.dataset.docsVersionSwitchEnhanced === "true"
            && button.dataset.docsVersionSwitchSourceKey === data.sourceKey) {
            return;
        }

        const links = options.querySelectorAll("a[href]");
        if (links.length === 0) {
            button.hidden = true;
            options.hidden = true;
            button.setAttribute("aria-expanded", "false");
            fallback.hidden = false;
            return;
        }

        button.hidden = false;
        fallback.hidden = true;
        options.hidden = true;
        button.setAttribute("aria-expanded", "false");
        button.dataset.docsVersionSwitchEnhanced = "true";
        button.dataset.docsVersionSwitchSourceKey = data.sourceKey;
    }

    function initializeRoot(root) {
        const data = readPageData(root);
        updateChoiceLinks(root, data);
        initializeDisclosure(root, data);
        return { root, data };
    }

    function initialize() {
        const roots = Array.from(document.querySelectorAll(rootSelector));
        const initialized = roots.map(initializeRoot);
        const sourceKey = getQuerySourceKey();
        const transitions = initialized
            .map(item => ({ ...item, row: getValidatedIncoming(item.data, sourceKey) }))
            .filter(item => item.row);

        if (transitions.length !== 1) {
            clearNotice();
            return;
        }

        const { root, data, row } = transitions[0];
        const stamp = `${window.location.pathname}|${data.sourceKey}|${row.sourceKey}|${data.target.pin}|${data.target.canonicalRoute}`;
        const notice = getOrCreateNotice(root, row, stamp);
        if (stamp !== lastFocusedTransition) {
            focusWithDescription(root, notice);
            lastFocusedTransition = stamp;
        }
    }

    function onClick(event) {
        const target = event.target instanceof Element ? event.target : null;
        const choiceLink = target?.closest(`a[${anchorIdsAttribute}]`);
        const choiceRoot = choiceLink?.closest(rootSelector);
        if (choiceLink && choiceRoot) {
            const data = readPageData(choiceRoot);
            if (data) {
                updateChoiceLink(choiceLink, data);
            }
            return;
        }

        const button = target?.closest("[data-docs-version-toggle]");
        if (!button) {
            return;
        }

        const root = button.closest(rootSelector);
        const options = root?.querySelector("[data-docs-version-options]");
        if (!root || !options || button.hidden) {
            return;
        }

        const isExpanded = button.getAttribute("aria-expanded") === "true";
        button.setAttribute("aria-expanded", isExpanded ? "false" : "true");
        options.hidden = isExpanded;
    }

    function onKeyDown(event) {
        if (event.key !== "Escape" && event.key !== "Esc") {
            return;
        }

        const target = event.target instanceof Element ? event.target : null;
        const root = target?.closest(rootSelector);
        const button = root?.querySelector("[data-docs-version-toggle]");
        const options = root?.querySelector("[data-docs-version-options]");
        if (!root || !button || !options || options.hidden) {
            return;
        }

        button.setAttribute("aria-expanded", "false");
        options.hidden = true;
        button.focus();
        event.preventDefault();
    }

    function synchronizeChoiceLinks() {
        document.querySelectorAll(rootSelector).forEach(root => updateChoiceLinks(root, readPageData(root)));
    }

    let lifecycleRegistered = false;
    function init() {
        if (!lifecycleRegistered) {
            lifecycleRegistered = true;
            document.addEventListener("click", onClick);
            document.addEventListener("keydown", onKeyDown);
            document.addEventListener("DOMContentLoaded", initialize, { once: true });
            document.addEventListener("turbo:load", initialize);
            document.addEventListener("turbo:frame-load", event => {
                if (event.target?.id === "doc-content") {
                    initialize();
                }
            });
            document.addEventListener("turbo:render", initialize);
            document.addEventListener("razorwire:page-nav:active-change", initialize);
            window.addEventListener("popstate", initialize);
            window.addEventListener("hashchange", synchronizeChoiceLinks);
            window.addEventListener("pageshow", initialize);
        }

        if (document.readyState !== "loading") {
            initialize();
        }
    }

    window[runtimeKey] = { init, version: runtimeVersion };
    init();
})();
