(() => {
    const clientKey = "__razorDocsOutlineClient";
    if (window[clientKey]?.init) {
        window[clientKey].init();
        return;
    }

    const outlineSelector = "#docs-page-outline";
    const outlineLinkSelector = "a[data-doc-outline-link='true']";
    const compactMediaQuery = "(max-width: 79.999rem)";
    const outlineClickScrollDurationMs = 620;

    let lifecycleController = null;
    let activeObserver = null;
    let activeLinkAnimationFrame = 0;
    let scrollAnimationFrame = 0;
    let fallbackDisposers = [];

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

    function setActiveLink(links, link, currentLabel) {
        for (const candidate of links) {
            const isActive = candidate === link;
            candidate.classList.toggle("docs-outline-link--active", isActive);

            if (isActive) {
                candidate.setAttribute("aria-current", "location");
                if (currentLabel) {
                    currentLabel.textContent = candidate.textContent?.trim() ?? "";
                }
            } else {
                candidate.removeAttribute("aria-current");
            }
        }

        if (!link && currentLabel) {
            currentLabel.textContent = "";
        }

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

    function refreshHashActiveLink(entries, links, expectedLink, currentLabel) {
        if (getActiveEntryFromHash(entries)?.link === expectedLink) {
            setActiveLink(links, expectedLink, currentLabel);
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

    function updateActiveLinkFromScrollPosition(entries, links, root, currentLabel) {
        const activeEntry = getActiveEntryFromScrollPosition(entries, root);
        if (activeEntry) {
            setActiveLink(links, activeEntry.link, currentLabel);
        }
    }

    function scheduleActiveLinkRefresh(entries, links, root, currentLabel) {
        if (activeLinkAnimationFrame !== 0) {
            return;
        }

        if (typeof window.requestAnimationFrame !== "function") {
            updateActiveLinkFromScrollPosition(entries, links, root, currentLabel);
            return;
        }

        activeLinkAnimationFrame = window.requestAnimationFrame(() => {
            activeLinkAnimationFrame = 0;
            updateActiveLinkFromScrollPosition(entries, links, root, currentLabel);
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
        disconnectActiveObserver();
        lifecycleController?.abort();
        lifecycleController = null;
    }

    function initOutline() {
        teardown();

        const shell = document.querySelector(outlineSelector);
        if (!(shell instanceof HTMLElement)) {
            return;
        }

        const mainContent = document.getElementById("main-content");
        const primary = shell.parentElement?.querySelector(".docs-detail-primary");
        const toggle = shell.querySelector("[data-doc-outline-toggle='true']");
        const currentLabel = shell.querySelector("[data-doc-outline-current]");
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
                setActiveLink(links, link, currentLabel);

                if (compactMedia?.matches) {
                    setExpanded(shell, toggle, false);
                }

                scrollEntryIntoView(entry, mainContent);

                const hash = getLinkHash(link);
                if (hash && window.location.hash !== hash) {
                    window.history.pushState(null, "", hash);
                }

                for (const delay of [120, 360, 720]) {
                    window.setTimeout(() => refreshHashActiveLink(entries, links, link, currentLabel), delay);
                }
            });
        }

        setActiveLink(links, getInitialActiveLink(entries), currentLabel);

        addLifecycleEventListener(window, "hashchange", () => {
            setActiveLink(links, getActiveEntryFromHash(entries)?.link ?? null, currentLabel);
        });

        if (!("IntersectionObserver" in window) || !mainContent) {
            return;
        }

        addLifecycleEventListener(mainContent, "scroll", () => {
            scheduleActiveLinkRefresh(entries, links, mainContent, currentLabel);
        });
        addLifecycleEventListener(mainContent, "wheel", cancelScrollAnimation);
        addLifecycleEventListener(mainContent, "touchstart", cancelScrollAnimation);

        activeObserver = new IntersectionObserver(
            observedEntries => {
                if (observedEntries.some(entry => entry.isIntersecting)) {
                    scheduleActiveLinkRefresh(entries, links, mainContent, currentLabel);
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

    window[clientKey] = { init: initOutline };

    document.addEventListener("turbo:load", initOutline);
    document.addEventListener("turbo:frame-load", event => {
        if (event.target?.id === "doc-content") {
            initOutline();
        }
    });

    if (document.readyState === "loading") {
        document.addEventListener("DOMContentLoaded", initOutline);
    } else {
        initOutline();
    }
})();
