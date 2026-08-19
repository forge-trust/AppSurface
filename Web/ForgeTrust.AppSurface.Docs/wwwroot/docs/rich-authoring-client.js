(() => {
    "use strict";

    const tabsSelector = "[data-appsurfacedocs-rich-tabs='true']";
    const panelSelector = "[data-appsurfacedocs-rich-tab-panel='true']";
    const focusableSelector = "a[href], button:not([disabled]), input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex='-1'])";

    function normalizeLabel(value) {
        return (value || "").trim().toLowerCase();
    }

    function getPanels(tabs) {
        return Array.from(tabs.querySelectorAll(`:scope > ${panelSelector}`));
    }

    function validPanels(panels) {
        if (panels.length < 2 || panels.length > 4) {
            return false;
        }

        const labels = new Set();
        return panels.every((panel) => {
            const label = normalizeLabel(panel.dataset.appsurfacedocsRichTabLabel);
            if (!label || labels.has(label)) {
                return false;
            }

            labels.add(label);
            return true;
        });
    }

    function getTrustedTabsTokens() {
        return new Set(
            Array.from(document.querySelectorAll("script[data-doc-rich-authoring-client='true']"))
                .flatMap((script) => (script.dataset.appsurfacedocsRichTabsTokens || "").split(" "))
                .map((token) => token.trim())
                .filter(Boolean));
    }

    function isTrustedTabs(tabs, trustedTabsTokens) {
        return trustedTabsTokens.has(tabs.dataset.appsurfacedocsRichTabsToken || "");
    }

    function selectPanel(tabs, panels, buttons, selectedIndex, updateFragment) {
        if (selectedIndex < 0 || selectedIndex >= panels.length) {
            return;
        }

        panels.forEach((panel, index) => {
            const active = index === selectedIndex;
            panel.hidden = !active;
            panel.setAttribute("aria-hidden", String(!active));
            buttons[index].setAttribute("aria-selected", String(active));
            buttons[index].tabIndex = active ? 0 : -1;
        });

        if (updateFragment) {
            const target = panels[selectedIndex].querySelector("[id]") || panels[selectedIndex];
            if (target.id) {
                history.replaceState(null, "", `#${encodeURIComponent(target.id)}`);
            }
        }
    }

    function selectPanelForFragment(tabs, panels, buttons) {
        let hash = "";
        try {
            hash = window.location.hash.length > 1 ? decodeURIComponent(window.location.hash.slice(1)) : "";
        } catch {
            return;
        }

        if (!hash) {
            return;
        }

        const target = document.getElementById(hash);
        const panel = target?.closest(panelSelector);
        if (!panel || !tabs.contains(panel)) {
            return;
        }

        const panelIndex = panels.indexOf(panel);
        if (panelIndex < 0) {
            return;
        }

        selectPanel(tabs, panels, buttons, panelIndex, false);
        requestAnimationFrame(() => target.scrollIntoView({ block: "start" }));
    }

    function enhanceTabs(tabs, trustedTabsTokens) {
        if (tabs.dataset.appsurfacedocsRichTabsEnhanced === "true") {
            return;
        }

        const panels = getPanels(tabs);
        if (!isTrustedTabs(tabs, trustedTabsTokens) || !validPanels(panels)) {
            return;
        }

        const fragment = document.createDocumentFragment();
        const controls = document.createElement("div");
        controls.className = "docs-rich-tabs__controls";
        const tabList = document.createElement("div");
        tabList.className = "docs-rich-tabs__tablist";
        tabList.setAttribute("role", "tablist");
        const prompt = tabs.querySelector(":scope > .docs-rich-tabs__prompt");
        if (prompt?.id) {
            tabList.setAttribute("aria-labelledby", prompt.id);
        }

        const buttons = panels.map((panel, index) => {
            const label = panel.dataset.appsurfacedocsRichTabLabel;
            const button = document.createElement("button");
            const tabId = `${prompt?.id || "docs-rich-tabs"}-tab-${index + 1}`;
            const panelId = `${prompt?.id || "docs-rich-tabs"}-panel-${index + 1}`;
            button.type = "button";
            button.className = "docs-rich-tabs__tab";
            button.id = tabId;
            button.textContent = label;
            button.setAttribute("role", "tab");
            button.setAttribute("aria-controls", panelId);
            button.setAttribute("aria-selected", String(index === 0));
            button.tabIndex = index === 0 ? 0 : -1;
            button.addEventListener("click", () => selectPanel(tabs, panels, buttons, index, true));
            button.addEventListener("keydown", (event) => {
                let nextIndex = index;
                if (event.key === "ArrowRight" || event.key === "ArrowDown") {
                    nextIndex = (index + 1) % buttons.length;
                } else if (event.key === "ArrowLeft" || event.key === "ArrowUp") {
                    nextIndex = (index + buttons.length - 1) % buttons.length;
                } else if (event.key === "Home") {
                    nextIndex = 0;
                } else if (event.key === "End") {
                    nextIndex = buttons.length - 1;
                } else if (event.key === "Enter" || event.key === " ") {
                    event.preventDefault();
                    selectPanel(tabs, panels, buttons, index, true);
                    return;
                } else {
                    return;
                }

                event.preventDefault();
                buttons[nextIndex].focus();
            });
            panel.id = panelId;
            panel.setAttribute("role", "tabpanel");
            panel.setAttribute("aria-labelledby", tabId);
            if (!panel.querySelector(focusableSelector)) {
                panel.tabIndex = 0;
            }
            tabList.append(button);
            return button;
        });

        controls.append(tabList);
        fragment.append(controls);
        const baseline = tabs.querySelector(":scope > [data-appsurfacedocs-rich-tabs-baseline='true']");
        baseline?.replaceWith(fragment);
        tabs.dataset.appsurfacedocsRichTabsEnhanced = "true";
        selectPanel(tabs, panels, buttons, 0, false);
        selectPanelForFragment(tabs, panels, buttons);
    }

    function enhance(root = document) {
        const trustedTabsTokens = getTrustedTabsTokens();
        root.querySelectorAll?.(tabsSelector).forEach((tabs) => enhanceTabs(tabs, trustedTabsTokens));
    }

    function synchronizeFragments() {
        const trustedTabsTokens = getTrustedTabsTokens();
        document.querySelectorAll(tabsSelector).forEach((tabs) => {
            if (!isTrustedTabs(tabs, trustedTabsTokens)) {
                return;
            }

            const panels = getPanels(tabs);
            const buttons = Array.from(tabs.querySelectorAll("[role='tab']"));
            if (tabs.dataset.appsurfacedocsRichTabsEnhanced === "true" && panels.length === buttons.length) {
                selectPanelForFragment(tabs, panels, buttons);
            }
        });
    }

    if (document.readyState === "loading") {
        document.addEventListener("DOMContentLoaded", () => enhance());
    } else {
        enhance();
    }
    document.addEventListener("turbo:load", () => enhance());
    document.addEventListener("turbo:frame-load", (event) => {
        if (event.target?.id === "doc-content") {
            enhance(event.target);
        }
    });
    window.addEventListener("hashchange", synchronizeFragments);
    window.addEventListener("popstate", synchronizeFragments);
})();
