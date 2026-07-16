// Development-only host action matrix for the AppSurface Web Push safe rail.
(() => {
    "use strict";

    const status = document.getElementById("push-status");
    const actions = document.getElementById("push-actions");
    const endpoint = actions?.dataset.endpoint || "/account/push-subscriptions";
    const enable = document.getElementById("enable-push");
    const disable = document.getElementById("disable-push");
    const fakeSend = document.getElementById("fake-push-send");
    const permission = document.getElementById("push-permission-capability");
    const subscription = document.getElementById("push-subscription-capability");
    const sender = document.getElementById("push-sender-capability");
    const delivery = document.getElementById("push-delivery-capability");
    if (!status || !actions || !enable || !disable || !fakeSend) return;

    let preparedHandle = null;
    const api = () => window.AppSurface?.Pwa;
    const setBusy = busy => {
        actions.setAttribute("aria-busy", busy ? "true" : "false");
        enable.disabled = busy || !preparedHandle;
        disable.disabled = busy;
        fakeSend.disabled = busy || subscription.textContent !== "Enabled";
    };
    const announce = (state, message) => {
        status.dataset.state = state;
        status.textContent = message;
    };
    const setSubscription = enabled => {
        subscription.textContent = enabled ? "Enabled" : "Not configured";
        subscription.className = `pill ${enabled ? "configured" : "unconfigured"}`;
    };
    const setPermission = outcome => {
        if (!permission) return;
        const value = Notification.permission;
        permission.textContent = value === "granted"
            ? "Granted"
            : value === "denied"
                ? "Denied"
                : outcome === "permission-dismissed" ? "Dismissed" : "Not requested";
        permission.className = `pill ${value === "granted" ? "configured" : "unconfigured"}`;
    };

    const prepare = async (preserveMessage = false) => {
        preparedHandle = null;
        setBusy(true);
        const announcePreparation = (state, message) => {
            if (!preserveMessage) announce(state, message);
        };
        let result;
        try { result = await api().prepare({ endpoint }); }
        catch { announcePreparation("failed", "The browser client rejected the proof configuration. Check the safe ASPUSHJS code."); setBusy(false); return; }
        if (result.status === "prepared") {
            preparedHandle = result.handle;
            announcePreparation("ready", "Ready. Enable notifications must be clicked directly; no permission prompt has occurred yet.");
        } else if (result.status === "vapid-key-migration-required") {
            announcePreparation("failed", "The active VAPID key changed. Disable the old subscription, prepare again, then use a second explicit enable action.");
        } else if (result.status === "unauthorized" || result.status === "forbidden") {
            announcePreparation("failed", "Select the Push Admin DevAuth persona; Viewer is intentionally forbidden.");
        } else {
            announcePreparation("failed", `Preparation returned ${result.status}. Correct the environment and prepare again.`);
        }
        setBusy(false);
    };

    enable.addEventListener("click", () => {
        if (!preparedHandle) return;
        setBusy(true);
        // Keep this invocation directly in the click handler; the package calls PushManager.subscribe before its first await.
        let operation;
        try { operation = api().subscribe({ prepared: preparedHandle }); }
        catch {
            preparedHandle = null;
            announce("failed", "The subscribe action failed with a safe browser invariant code.");
            setBusy(false);
            void prepare(true);
            return;
        }
        preparedHandle = null;
        operation.then(result => {
            setPermission(result.status);
            if (result.status === "subscribed" || result.status === "already-subscribed") {
                setSubscription(true);
                announce("active", "Subscription custody succeeded. Browser delivery has not been tested or claimed.");
            } else {
                announce("failed", `Subscription returned ${result.status}. The browser subscription may remain for an explicit retry.`);
            }
        }).catch(() => announce("failed", "The subscribe action failed with a safe browser invariant code."))
            .finally(() => { setBusy(false); void prepare(true); });
    });

    disable.addEventListener("click", async () => {
        setBusy(true);
        try {
            const result = await api().unsubscribe({ endpoint });
            if (result.status === "unsubscribed") {
                setSubscription(false);
                announce("ready", "The server accepted the custody-removal request and the browser subscription was removed. Prepare again before enabling.");
            } else if (result.status === "already-unsubscribed") {
                setSubscription(false);
                announce("ready", "No browser subscription was found. Server custody was not contacted and may still require host cleanup.");
            } else {
                announce("failed", `Unsubscribe returned ${result.status}; follow the explicit recovery message.`);
            }
        } catch {
            announce("failed", "The unsubscribe action failed with a safe browser invariant code.");
        } finally { setBusy(false); await prepare(true); }
    });

    fakeSend.addEventListener("click", async () => {
        setBusy(true);
        try {
            const configurationResponse = await fetch(`${endpoint}/configuration`, {
                cache: "no-store",
                credentials: "same-origin"
            });
            if (!configurationResponse.ok) throw new Error("fake classification configuration rejected");
            const configuration = await configurationResponse.json();
            const headerName = configuration?.antiforgery?.headerName;
            const requestToken = configuration?.antiforgery?.requestToken;
            if (typeof headerName !== "string" || typeof requestToken !== "string") {
                throw new Error("fake classification protection unavailable");
            }
            const response = await fetch(`${endpoint}/host-action-proof`, {
                method: "POST",
                cache: "no-store",
                credentials: "same-origin",
                headers: { [headerName]: requestToken }
            });
            const proof = await response.json();
            if (!response.ok) throw new Error("fake classification rejected");
            sender.textContent = proof.senderClassification;
            sender.className = "pill configured";
            delivery.textContent = "Not proven";
            delivery.className = "pill unconfigured";
            announce("active", `${proof.message} Push delivery remains Not proven.`);
        } catch {
            announce("failed", "The example host action failed. No browser delivery was attempted.");
        } finally { setBusy(false); }
    });

    void prepare();
})();
