// Independent application-icon badging proof with authoritative in-app state.
(() => {
    "use strict";

    const input = document.getElementById("badge-count");
    const inputError = document.getElementById("badge-count-error");
    const setButton = document.getElementById("set-badge");
    const clearButton = document.getElementById("clear-badge");
    const attentionCount = document.getElementById("attention-count");
    const status = document.getElementById("badging-status");
    const brandKey = Symbol.for("ForgeTrust.AppSurface.Pwa.badging");

    const isPlainObject = value => {
        if (!value || typeof value !== "object") return false;
        try {
            const prototype = Object.getPrototypeOf(value);
            return prototype === null || prototype === Object.prototype;
        } catch {
            return false;
        }
    };

    const hasCanonicalShape = value => {
        try {
            if (!isPlainObject(value) || !Object.isFrozen(value) || Object.keys(value).length !== 0) return false;
            const ownKeys = Reflect.ownKeys(value);
            const brandDescriptor = Object.getOwnPropertyDescriptor(value, brandKey);
            const setDescriptor = Object.getOwnPropertyDescriptor(value, "set");
            const clearDescriptor = Object.getOwnPropertyDescriptor(value, "clear");
            const brand = brandDescriptor && brandDescriptor.value;
            const brandKeys = brand && Reflect.ownKeys(brand);
            const versionDescriptor = brand && Object.getOwnPropertyDescriptor(brand, "version");
            return ownKeys.length === 3
                && ownKeys.includes("set")
                && ownKeys.includes("clear")
                && ownKeys.includes(brandKey)
                && brandDescriptor.enumerable === false
                && brandDescriptor.writable === false
                && brandDescriptor.configurable === false
                && isPlainObject(brand)
                && Object.isFrozen(brand)
                && brandKeys.length === 1
                && brandKeys[0] === "version"
                && versionDescriptor.value === 1
                && versionDescriptor.enumerable === true
                && versionDescriptor.writable === false
                && versionDescriptor.configurable === false
                && setDescriptor.enumerable === false
                && setDescriptor.writable === false
                && setDescriptor.configurable === false
                && typeof setDescriptor.value === "function"
                && clearDescriptor.enumerable === false
                && clearDescriptor.writable === false
                && clearDescriptor.configurable === false
                && typeof clearDescriptor.value === "function";
        } catch {
            return false;
        }
    };

    const helper = () => {
        try {
            const appSurface = window.AppSurface;
            const pwa = appSurface && appSurface.Pwa;
            const badging = pwa && pwa.badging;
            return hasCanonicalShape(badging) ? badging : null;
        } catch {
            return null;
        }
    };

    const setControlsDisabled = disabled => {
        setButton.disabled = disabled;
        clearButton.disabled = disabled;
    };

    const setStatus = (state, message) => {
        status.dataset.state = state;
        status.textContent = message;
    };

    const renderCount = count => {
        attentionCount.textContent = count === 1 ? "1 item needs attention" : `${count} items need attention`;
    };

    const errorCode = error => {
        try {
            return error && typeof error.message === "string" ? error.message : "";
        } catch {
            return "";
        }
    };

    const parseCount = () => {
        const value = input.value;
        if (!/^\d+$/u.test(value)) return null;
        const count = Number(value);
        return Number.isSafeInteger(count) ? count : null;
    };

    const showInvalidInput = () => {
        input.setAttribute("aria-invalid", "true");
        inputError.textContent = "Enter a finite, nonnegative whole number no greater than 9,007,199,254,740,991.";
        setStatus("invalid", inputError.textContent);
        input.focus();
    };

    const clearInvalidInput = () => {
        input.removeAttribute("aria-invalid");
        inputError.textContent = "";
    };

    const run = async (operation, count) => {
        const badging = helper();
        if (!badging) {
            setControlsDisabled(true);
            setStatus(
                "helper-conflict",
                "The AppSurface badging helper is unavailable or could not claim its browser namespace. Check the page head and AppSurface diagnostics.");
            return;
        }

        setControlsDisabled(true);
        let helperConflict = false;
        setStatus(
            operation === "set" ? "setting" : "clearing",
            operation === "set"
                ? "Requesting an app badge for the synthetic attention count…"
                : "Requesting that the app badge be cleared…");

        try {
            const outcome = operation === "set" ? await badging.set(count) : await badging.clear();
            if (outcome === "unsupported") {
                setStatus(
                    "unsupported",
                    "This browser does not expose app badging. The in-app attention state remains available.");
            } else if (outcome !== "accepted") {
                helperConflict = true;
                setStatus(
                    "helper-conflict",
                    "The AppSurface badging helper is unavailable or returned an unexpected value-free outcome. Check AppSurface diagnostics.");
            } else if (operation === "set") {
                setStatus(
                    "accepted-set",
                    `Badge request accepted for ${count}. The browser or operating system may hide or change what appears on the app icon.`);
            } else {
                setStatus("accepted-clear", "Clear request accepted. AppSurface cannot read back the app icon state.");
            }
        } catch (error) {
            const code = errorCode(error);
            if (operation === "set" && code === "ASPWAJS041") {
                setStatus(
                    "rejected-set",
                    `The browser rejected the badge request (ASPWAJS041). The in-app attention state remains ${count}.`);
            } else if (operation === "clear" && code === "ASPWAJS042") {
                setStatus(
                    "rejected-clear",
                    "The browser rejected the clear request (ASPWAJS042). The in-app attention state remains 0.");
            } else {
                helperConflict = true;
                setStatus(
                    "helper-conflict",
                    "The AppSurface badging helper is unavailable or returned an unexpected value-free error. Check AppSurface diagnostics.");
            }
        } finally {
            setControlsDisabled(helperConflict || helper() === null);
        }
    };

    setButton.addEventListener("click", () => {
        const count = parseCount();
        if (count === null) {
            showInvalidInput();
            return;
        }

        clearInvalidInput();
        renderCount(count);
        if (count === 0) void run("clear", 0);
        else void run("set", count);
    });

    clearButton.addEventListener("click", () => {
        clearInvalidInput();
        input.value = "0";
        renderCount(0);
        void run("clear", 0);
    });

    const initialize = () => {
        if (!helper()) {
            setControlsDisabled(true);
            setStatus(
                "helper-conflict",
                "The AppSurface badging helper is unavailable or could not claim its browser namespace. Check the page head and AppSurface diagnostics.");
            return;
        }

        setControlsDisabled(false);
        setStatus("ready", "Ready. No badge request has been made.");
    };

    if (document.readyState === "loading") {
        document.addEventListener("DOMContentLoaded", initialize, { once: true });
    } else {
        initialize();
    }
})();
