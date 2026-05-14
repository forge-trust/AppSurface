(() => {
    const clientKey = "__razorDocsOutlineClient";
    const clientVersion = "rolling-context";
    const existingClient = window[clientKey];
    if (existingClient?.version === clientVersion && existingClient.init) {
        existingClient.init();
        return;
    }

    existingClient?.destroy?.();

    const outlineSelector = "#docs-page-outline";
    const outlineLinkSelector = "a[data-doc-outline-link='true']";
    const compactMediaQuery = "(max-width: 79.999rem)";
    const outlineClickScrollDurationMs = 620;
    const outlineContextRollDurationMs = 180;

    let lifecycleController = null;
    let activeObserver = null;
    let activeLinkAnimationFrame = 0;
    let scrollAnimationFrame = 0;
    let contextRollTimeout = 0;
    let lastActiveIndex = -1;
    let fallbackDisposers = [];
    let turboLoadHandler = null;
    let turboFrameLoadHandler = null;
    let domContentLoadedHandler = null;

    function decodeHash(hash) {
        if (!hash) {
            return null;
        }

        try {
            return decodeURIComponent(hash.replace(/^#/, ""));
        } catch {
            return null;
        }
    }

    function getLinkTargetId(link) {
        let url;
        try {
            url = new URL(link.href, window.location.origin);
        } catch {
            return null;
        }

        if (!url.hash) {
            return null;
        }

        return decodeHash(url.hash);
    }

    function setExpanded(shell, toggle, expanded) {
        shell.dataset.outlineExpanded = expanded ? "true" : "false";
        toggle?.setAttribute("aria-expanded", expanded ? "true" : "false");
    }

    function clearContextRollTimeout() {
        if (contextRollTimeout === 0) {
            return;
        }

        window.clearTimeout(contextRollTimeout);
        contextRollTimeout = 0;
    }

    function resetContextMotion(container) {
        if (!(container instanceof HTMLElement)) {
            return;
        }

        container.classList.remove("docs-outline-toggle-context--rolling");
        container.dataset.outlineMotion = "idle";
        delete container.dataset.outlineRollDirection;
    }

    function setContextRow(row, text) {
        if (!row) {
            return;
        }

        const title = row.querySelector?.("[data-doc-outline-context-title]");
        if (title) {
            title.textContent = text;
        } else {
            row.textContent = text;
        }

        row.hidden = false;
        row.dataset.outlineEmpty = text ? "false" : "true";
    }

    function setOutlineContext(context, links, activeIndex) {
        if (!context?.current) {
            return;
        }

        const currentText = activeIndex >= 0 ? links[activeIndex]?.textContent?.trim() ?? "" : "";
        const previousText = activeIndex > 0 ? links[activeIndex - 1]?.textContent?.trim() ?? "" : "";
        const nextText = activeIndex >= 0 && activeIndex < links.length - 1
            ? links[activeIndex + 1]?.textContent?.trim() ?? ""
            : "";

        setContextRow(context.previous, previousText);
        setContextRow(context.current, currentText);
        setContextRow(context.next, nextText);

        if (!context.container) {
            return;
        }

        const previousActiveIndex = lastActiveIndex >= 0
            ? lastActiveIndex
            : Number.parseInt(context.container.dataset.outlineActiveIndex ?? "", 10);

        context.container.dataset.outlineActiveIndex = activeIndex >= 0 ? String(activeIndex) : "";

        if (activeIndex < 0 || !Number.isFinite(previousActiveIndex) || activeIndex === previousActiveIndex) {
            lastActiveIndex = activeIndex;
            context.container.dataset.outlineMotion = "idle";
            return;
        }

        const reduceMotion = window.matchMedia?.("(prefers-reduced-motion: reduce)")?.matches ?? false;
        context.container.dataset.outlineRollDirection = activeIndex > previousActiveIndex ? "down" : "up";
        lastActiveIndex = activeIndex;

        if (reduceMotion) {
            context.container.dataset.outlineMotion = "reduced";
            context.container.classList.remove("docs-outline-toggle-context--rolling");
            return;
        }

        context.container.dataset.outlineMotion = "rolling";
        clearContextRollTimeout();
        context.container.classList.remove("docs-outline-toggle-context--rolling");
        void context.container.offsetWidth;
        context.container.classList.add("docs-outline-toggle-context--rolling");
        contextRollTimeout = window.setTimeout(() => {
            resetContextMotion(context.container);
            contextRollTimeout = 0;
        }, outlineContextRollDurationMs);
    }

    function setActiveLink(links, link, context) {
        const activeIndex = link ? links.indexOf(link) : -1;

        for (const candidate of links) {
            const isActive = candidate === link;
            candidate.classList.toggle("docs-outline-link--active", isActive);

            if (isActive) {
                candidate.setAttribute("aria-current", "location");
            } else {
                candidate.removeAttribute("aria-current");
            }
        }

        setOutlineContext(context, links, activeIndex);

        keepOutlineLinkVisible(link);
    }

    function keepOutlineLinkVisible(link) {
        if (!link) {
            return;
        }

        const shell = link.closest(outlineSelector);
        if (!(shell instanceof HTMLElement) || shell.scrollHeight <= shell.clientHeight) {
            return;
        }

        const shellRect = shell.getBoundingClientRect();
        const linkRect = link.getBoundingClientRect();
        const topInset = 48;
        const bottomInset = 56;
        let nextScrollTop = shell.scrollTop;

        if (linkRect.top < shellRect.top + topInset) {
            nextScrollTop += linkRect.top - shellRect.top - topInset;
        } else if (linkRect.bottom > shellRect.bottom - bottomInset) {
            nextScrollTop += linkRect.bottom - shellRect.bottom + bottomInset;
        } else {
            return;
        }

        const maxScrollTop = Math.max(0, shell.scrollHeight - shell.clientHeight);
        shell.scrollTo({
            top: Math.min(Math.max(0, nextScrollTop), maxScrollTop),
            behavior: "auto"
        });
    }

    function getActiveEntryFromHash(entries) {
        const targetId = decodeHash(window.location.hash);
        if (!targetId) {
            return null;
        }

        return entries.find(entry => getLinkTargetId(entry.link) === targetId) ?? null;
    }

    function refreshHashActiveLink(entries, links, expectedLink, context) {
        if (getActiveEntryFromHash(entries)?.link === expectedLink) {
            setActiveLink(links, expectedLink, context);
        }
    }

    function getEntryForLink(entries, link) {
        return entries.find(entry => entry.link === link) ?? null;
    }

    function getLinkHash(link) {
        try {
            return new URL(link.href, window.location.href).hash;
        } catch {
            return link.hash ?? "";
        }
    }

    function getInitialActiveLink(entries) {
        if (decodeHash(window.location.hash)) {
            return getActiveEntryFromHash(entries)?.link ?? null;
        }

        return entries[0]?.link ?? null;
    }

    function getOutlineEntries(links) {
        return links
            .map(link => {
                const targetId = getLinkTargetId(link);
                const target = targetId ? document.getElementById(targetId) : null;
                return target ? { link, target } : null;
            })
            .filter(entry => entry !== null);
    }

    function getActiveEntryFromScrollPosition(entries, root) {
        const rootTop = root.getBoundingClientRect().top;
        const activationTop = rootTop + 64;
        const hashEntry = getActiveEntryFromHash(entries);

        if (hashEntry) {
            const hashTargetTop = hashEntry.target.getBoundingClientRect().top;
            if (hashTargetTop >= rootTop - 16 && hashTargetTop <= rootTop + 160) {
                return hashEntry;
            }
        }

        let activeEntry = entries[0];

        for (const entry of entries) {
            if (entry.target.getBoundingClientRect().top > activationTop) {
                break;
            }

            activeEntry = entry;
        }

        return activeEntry;
    }

    function cancelScrollAnimation() {
        if (scrollAnimationFrame === 0) {
            return;
        }

        window.cancelAnimationFrame?.(scrollAnimationFrame);
        scrollAnimationFrame = 0;
    }

    function easeOutCubic(progress) {
        return 1 - Math.pow(1 - progress, 3);
    }

    function animateScrollTo(root, top) {
        if (typeof window.requestAnimationFrame !== "function") {
            root.scrollTo({ top, behavior: "auto" });
            return;
        }

        cancelScrollAnimation();

        const startTop = root.scrollTop;
        const distance = top - startTop;
        const duration = outlineClickScrollDurationMs;
        const startTime = performance.now();

        const step = timestamp => {
            const progress = Math.min(1, (timestamp - startTime) / duration);
            root.scrollTo({
                top: startTop + distance * easeOutCubic(progress),
                behavior: "auto"
            });

            if (progress < 1) {
                scrollAnimationFrame = window.requestAnimationFrame(step);
            } else {
                scrollAnimationFrame = 0;
            }
        };

        scrollAnimationFrame = window.requestAnimationFrame(step);
    }

    function scrollEntryIntoView(entry, root) {
        if (!entry || !root) {
            return;
        }

        const rootRect = root.getBoundingClientRect();
        const targetRect = entry.target.getBoundingClientRect();
        const desiredTop = root.scrollTop + targetRect.top - rootRect.top - 64;
        const maxScrollTop = Math.max(0, root.scrollHeight - root.clientHeight);
        const top = Math.min(Math.max(0, desiredTop), maxScrollTop);
        const reduceMotion = window.matchMedia?.("(prefers-reduced-motion: reduce)")?.matches ?? false;

        if (reduceMotion) {
            cancelScrollAnimation();
            root.scrollTo({ top, behavior: "auto" });
            return;
        }

        animateScrollTo(root, top);
    }

    function disconnectActiveObserver() {
        activeObserver?.disconnect();
        activeObserver = null;
    }

    function cancelActiveLinkRefresh() {
        if (activeLinkAnimationFrame === 0) {
            return;
        }

        window.cancelAnimationFrame?.(activeLinkAnimationFrame);
        activeLinkAnimationFrame = 0;
    }

    function updateActiveLinkFromScrollPosition(entries, links, root, context) {
        const activeEntry = getActiveEntryFromScrollPosition(entries, root);
        if (activeEntry) {
            setActiveLink(links, activeEntry.link, context);
        }
    }

    function scheduleActiveLinkRefresh(entries, links, root, context) {
        if (activeLinkAnimationFrame !== 0) {
            return;
        }

        if (typeof window.requestAnimationFrame !== "function") {
            updateActiveLinkFromScrollPosition(entries, links, root, context);
            return;
        }

        activeLinkAnimationFrame = window.requestAnimationFrame(() => {
            activeLinkAnimationFrame = 0;
            updateActiveLinkFromScrollPosition(entries, links, root, context);
        });
    }

    function createLifecycleController() {
        return typeof AbortController === "function" ? new AbortController() : null;
    }

    function addLifecycleEventListener(target, type, listener) {
        const signal = lifecycleController?.signal;

        if (typeof target?.addEventListener === "function") {
            if (signal) {
                target.addEventListener(type, listener, { signal });
                return;
            }

            target.addEventListener(type, listener);
            fallbackDisposers.push(() => target.removeEventListener?.(type, listener));
            return;
        }

        if (type === "change" && typeof target?.addListener === "function") {
            target.addListener(listener);
            fallbackDisposers.push(() => target.removeListener?.(listener));
        }
    }

    function cleanupLifecycleEventListeners() {
        const disposers = fallbackDisposers;
        fallbackDisposers = [];

        for (const dispose of disposers) {
            dispose();
        }
    }

    function syncOutlinePlacement(shell, primary, compact) {
        const layout = shell.parentElement;
        if (!layout || !primary || primary.parentElement !== layout) {
            return;
        }

        if (compact) {
            if (shell.nextElementSibling !== primary) {
                layout.insertBefore(shell, primary);
            }

            return;
        }

        if (primary.nextElementSibling !== shell) {
            primary.after(shell);
        }
    }

    function teardown() {
        cleanupLifecycleEventListeners();
        cancelActiveLinkRefresh();
        cancelScrollAnimation();
        clearContextRollTimeout();
        resetContextMotion(document.querySelector("[data-doc-outline-context]"));
        disconnectActiveObserver();
        lifecycleController?.abort();
        lifecycleController = null;
        lastActiveIndex = -1;
    }

    function removeDocumentLifecycleEventListeners() {
        if (turboLoadHandler) {
            document.removeEventListener("turbo:load", turboLoadHandler);
            turboLoadHandler = null;
        }

        if (turboFrameLoadHandler) {
            document.removeEventListener("turbo:frame-load", turboFrameLoadHandler);
            turboFrameLoadHandler = null;
        }

        if (domContentLoadedHandler) {
            document.removeEventListener("DOMContentLoaded", domContentLoadedHandler);
            domContentLoadedHandler = null;
        }
    }

    function destroyClient() {
        removeDocumentLifecycleEventListeners();
        teardown();
    }

    function resetStaleOutlineShell(shell) {
        if (shell.dataset.outlineEnhanced !== "true" || shell.dataset.outlineClientVersion === clientVersion) {
            return shell;
        }

        const freshShell = shell.cloneNode(true);
        shell.replaceWith(freshShell);
        return freshShell;
    }

    function initOutline() {
        teardown();

        let shell = document.querySelector(outlineSelector);
        if (!(shell instanceof HTMLElement)) {
            return;
        }

        shell = resetStaleOutlineShell(shell);
        const mainContent = document.getElementById("main-content");
        const primary = shell.parentElement?.querySelector(".docs-detail-primary");
        const toggle = shell.querySelector("[data-doc-outline-toggle='true']");
        const outlineContext = {
            container: shell.querySelector("[data-doc-outline-context]"),
            current: shell.querySelector("[data-doc-outline-current]"),
            previous: shell.querySelector("[data-doc-outline-previous]"),
            next: shell.querySelector("[data-doc-outline-next]")
        };
        const links = Array.from(shell.querySelectorAll(outlineLinkSelector))
            .filter(link => link instanceof HTMLAnchorElement);

        if (links.length === 0) {
            return;
        }

        const entries = getOutlineEntries(links);
        if (entries.length === 0) {
            return;
        }

        lifecycleController = createLifecycleController();
        shell.dataset.outlineEnhanced = "true";
        shell.dataset.outlineClientVersion = clientVersion;

        const compactMedia = window.matchMedia ? window.matchMedia(compactMediaQuery) : null;
        const syncViewportState = () => {
            const compact = compactMedia?.matches ?? false;
            syncOutlinePlacement(shell, primary, compact);
            setExpanded(shell, toggle, !compact);
        };

        syncViewportState();
        addLifecycleEventListener(compactMedia, "change", syncViewportState);

        addLifecycleEventListener(toggle, "click", () => {
            setExpanded(shell, toggle, shell.dataset.outlineExpanded !== "true");
        });

        for (const link of links) {
            addLifecycleEventListener(link, "click", event => {
                if (!mainContent) {
                    return;
                }

                const entry = getEntryForLink(entries, link);
                if (!entry) {
                    return;
                }

                event.preventDefault();
                setActiveLink(links, link, outlineContext);

                if (compactMedia?.matches) {
                    setExpanded(shell, toggle, false);
                }

                scrollEntryIntoView(entry, mainContent);

                const hash = getLinkHash(link);
                if (hash && window.location.hash !== hash) {
                    window.history.pushState(null, "", hash);
                }

                for (const delay of [120, 360, 720]) {
                    window.setTimeout(() => refreshHashActiveLink(entries, links, link, outlineContext), delay);
                }
            });
        }

        setActiveLink(links, getInitialActiveLink(entries), outlineContext);

        addLifecycleEventListener(window, "hashchange", () => {
            setActiveLink(links, getActiveEntryFromHash(entries)?.link ?? null, outlineContext);
        });

        if (!("IntersectionObserver" in window) || !mainContent) {
            return;
        }

        addLifecycleEventListener(mainContent, "scroll", () => {
            scheduleActiveLinkRefresh(entries, links, mainContent, outlineContext);
        });
        addLifecycleEventListener(mainContent, "wheel", cancelScrollAnimation);
        addLifecycleEventListener(mainContent, "touchstart", cancelScrollAnimation);

        activeObserver = new IntersectionObserver(
            observedEntries => {
                if (observedEntries.some(entry => entry.isIntersecting)) {
                    scheduleActiveLinkRefresh(entries, links, mainContent, outlineContext);
                }
            },
            {
                root: mainContent,
                rootMargin: "-18% 0px -68% 0px",
                threshold: [0, 1]
            });

        for (const entry of entries) {
            activeObserver.observe(entry.target);
        }
    }

    window[clientKey] = {
        destroy: destroyClient,
        init: initOutline,
        version: clientVersion
    };

    turboLoadHandler = initOutline;
    turboFrameLoadHandler = event => {
        if (event.target?.id === "doc-content") {
            initOutline();
        }
    };

    document.addEventListener("turbo:load", turboLoadHandler);
    document.addEventListener("turbo:frame-load", turboFrameLoadHandler);

    if (document.readyState === "loading") {
        domContentLoadedHandler = () => {
            domContentLoadedHandler = null;
            initOutline();
        };
        document.addEventListener("DOMContentLoaded", domContentLoadedHandler, { once: true });
    } else {
        initOutline();
    }
})();
