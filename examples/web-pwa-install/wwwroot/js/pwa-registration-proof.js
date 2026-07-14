// Registration-only state machine for the AppSurface PWA worker proof.
(() => {
    "use strict";

    const status = document.getElementById("registration-status");
    const button = document.getElementById("register-worker");
    const workerCapability = document.getElementById("worker-capability");
    const workerMetadata = document.querySelector('meta[name="appsurface:pwa-service-worker"]');
    const scopeMetadata = document.querySelector('meta[name="appsurface:pwa-service-worker-scope"]');
    const expectedWorkerUrl = (() => {
        try {
            return workerMetadata ? new URL(workerMetadata.content, window.location.href).href : null;
        } catch {
            return null;
        }
    })();
    const expectedScopeUrl = (() => {
        try {
            return scopeMetadata ? new URL(scopeMetadata.content, window.location.href).href : null;
        } catch {
            return null;
        }
    })();

    const setState = (state, message, options = {}) => {
        status.dataset.state = state;
        status.textContent = message;
        button.disabled = options.enableButton !== true;
        if (options.buttonText) {
            button.textContent = options.buttonText;
        }

        if (options.workerActive === true) {
            workerCapability.textContent = "Active";
            workerCapability.className = "pill configured";
        } else {
            workerCapability.textContent = "Not proven";
            workerCapability.className = "pill pending";
        }
    };

    const helper = () => {
        try {
            const appSurface = window.AppSurface;
            const pwa = appSurface && appSurface.Pwa;
            return pwa && typeof pwa.register === "function" ? pwa.register : null;
        } catch {
            return null;
        }
    };

    const isExpectedWorker = worker => {
        if (!expectedWorkerUrl) {
            return false;
        }

        return worker && worker.scriptURL === expectedWorkerUrl;
    };

    const hasExpectedScope = registration =>
        Boolean(expectedScopeUrl && registration.scope === expectedScopeUrl);

    const hasWorkerCandidate = registration =>
        [registration.installing, registration.waiting, registration.active].some(Boolean);

    const isAppSurfaceRegistration = registration => {
        if (!hasExpectedScope(registration)) return false;
        return [registration.installing, registration.waiting, registration.active]
            .some(isExpectedWorker);
    };

    const watchActivation = registration => {
        const observed = new Set();
        const candidates = () => [registration.installing, registration.waiting]
            .filter(isExpectedWorker);
        if (candidates().length === 0) {
            setState(
                "activation-pending",
                "Registration completed, but no active worker is visible yet. Reload after the browser finishes activation.",
                { buttonText: "Activation pending" });
            return;
        }

        const update = () => {
            const expected = candidates();
            if (isExpectedWorker(registration.active)
                || expected.some(worker => worker.state === "activated")) {
                setState(
                    "active",
                    "Service worker active. Permission, subscription, and delivery are not configured.",
                    { buttonText: "Service worker active", workerActive: true });
                return;
            }

            const viable = expected.filter(worker => worker.state !== "redundant");
            for (const worker of viable) {
                if (!observed.has(worker)) {
                    observed.add(worker);
                    worker.addEventListener("statechange", update);
                }
            }

            if (viable.length > 0) {
                setState(
                    "activation-pending",
                    "Service worker registered. Waiting for browser activation…",
                    { buttonText: "Activation pending" });
            } else {
                setState(
                    "failed",
                    "The worker became redundant before activation. Check browser DevTools and AppSurface diagnostics.",
                    { buttonText: "Registration failed" });
            }
        };

        update();
    };

    const initialize = async () => {
        if (!("serviceWorker" in navigator)) {
            setState(
                "unsupported",
                "This browser does not expose service workers. Registration is unavailable.",
                { buttonText: "Unsupported browser" });
            return;
        }

        if (!helper()) {
            setState(
                "helper-conflict",
                "The AppSurface registration helper is unavailable or could not claim its browser namespace. Check the console and page head.",
                { buttonText: "Helper unavailable" });
            return;
        }

        try {
            const registration = await navigator.serviceWorker.getRegistration();
            if (registration && isAppSurfaceRegistration(registration) && isExpectedWorker(registration.active)) {
                setState(
                    "already-registered",
                    "Service worker already registered and active. Permission, subscription, and delivery are not configured.",
                    { buttonText: "Already registered", workerActive: true });
                return;
            }

            if (registration && isAppSurfaceRegistration(registration)) {
                setState(
                    "activation-pending",
                    "An existing service worker registration is waiting to become active.",
                    { buttonText: "Activation pending" });
                watchActivation(registration);
                return;
            }

            setState(
                "ready",
                "Ready to register the AppSurface service worker. This action will not request permission or create a subscription.",
                { enableButton: true, buttonText: "Register service worker" });
        } catch {
            setState(
                "failed",
                "Existing registration state could not be inspected. Check browser DevTools and AppSurface diagnostics.",
                { buttonText: "Inspection failed" });
        }
    };

    button.addEventListener("click", async () => {
        if (button.disabled) {
            return;
        }

        const register = helper();
        if (!register) {
            setState(
                "helper-conflict",
                "The AppSurface registration helper is unavailable or conflicted with another browser global.",
                { buttonText: "Helper unavailable" });
            return;
        }

        setState("registering", "Registering the service worker…", { buttonText: "Registering…" });

        try {
            const registration = await register();
            if (registration === null) {
                setState(
                    "unsupported",
                    "This browser does not expose service workers. Registration is unavailable.",
                    { buttonText: "Unsupported browser" });
            } else if (!hasExpectedScope(registration)
                || (hasWorkerCandidate(registration) && !isAppSurfaceRegistration(registration))) {
                setState(
                    "failed",
                    "The browser returned a service worker registration with an unexpected script or scope. Check browser DevTools and AppSurface diagnostics.",
                    { buttonText: "Registration failed" });
            } else if (isExpectedWorker(registration.active)) {
                setState(
                    "active",
                    "Service worker active. Permission, subscription, and delivery are not configured.",
                    { buttonText: "Service worker active", workerActive: true });
            } else {
                setState(
                    "activation-pending",
                    "Service worker registered. Waiting for browser activation…",
                    { buttonText: "Activation pending" });
                watchActivation(registration);
            }
        } catch {
            setState(
                "failed",
                "Service worker registration failed. Check browser DevTools and AppSurface diagnostics.",
                { buttonText: "Registration failed" });
        }
    });

    if (document.readyState === "loading") {
        document.addEventListener("DOMContentLoaded", () => void initialize(), { once: true });
    } else {
        void initialize();
    }
})();
