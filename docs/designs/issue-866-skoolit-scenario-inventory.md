# Issue866: pinned Skoolit scenario acceptance inventory

This inventory was prepared before consumer code changes for the [approved configured testing design](issue-866-pre-activation-testing-hooks.md). Source is the complete Skoolit tree at [a589f164ef3346d4ecf8bf39b8a73ec71ea1cb1e](https://github.com/forge-trust/skoolit/tree/a589f164ef3346d4ecf8bf39b8a73ec71ea1cb1e), retrieved as a commit-bound archive. These are source contracts, not passing runtime evidence. AppSurface baseline:40de2ef62ad9e697929ef4bd56f0bfda2d813d72; Hosting10.0.8; Aspire13.4.4.

## Scenario inputs and observable behavior

The [web component](https://github.com/forge-trust/skoolit/blob/a589f164ef3346d4ecf8bf39b8a73ec71ea1cb1e/src/Skoolit.AppHost/SkoolitQaWebComponent.cs) currently receives optional AppSurfaceLocalSecretProvider. Its graph selectors must be supplied by one consumer-owned immutable scenario during QA activation. Keep direct-run environment fallback separate; preserve child WithEnvironment output.

| Input | Current parsing/default | Per-call acceptance |
|---|---|---|
| SKOOLIT_QA_FORWARDING_SENDER_SCENARIO | Trimmed, absent/blank/inactive => Inactive; unavailable => Unavailable; other => InvalidOperationException | Preserve all three branches; product forwarding Enabled true except Unavailable false |
| SKOOLIT_QA_MONITORING_RECEIPT_ENABLED | Case-insensitive true only; otherwise false, including absent and1 | Inject each value without ambient reads; retain skoolit-web in both graphs |
| SKOOLIT_QA_SOURCE_ONLY_EVIDENCE | Enabled unless exactly0; absent/blank/false/1 => enabled | Preserve source-only flags and source-outcomes-v6 capability annotations |
| SKOOLIT_QA_STRIPE_SANDBOX_ENABLED | Case-insensitive true or exact1; otherwise false | QA factory bool defaults false; true keeps existing test-mode configuration resolution via optional local-secret provider |

Both receipt graphs must contain skoolit-web and these exact annotations:

| Child setting | On | Off |
|---|---|---|
| Skoolit__Inbox__MonitoringReceipt__Enabled | true | false |
| Skoolit__Inbox__MonitoringReceipt__Cohort | SeededInternal | Disabled |
| Skoolit__Inbox__MonitoringReceipt__SeededInternalHouseholdIds__0 | household_dev_billing | empty string |

## All pinned caller save/set/restore sites

The complete tree was searched for all four literal keys and their public constant names. The matched executable sites are below; source-contract tests and direct-run prose references were also inspected. The complete matching reference list is retained with the implementation progress artifacts.

| File | Reads/sets/restores selecting graph | Required migration |
|---|---|---|
| tests/Skoolit.Playwright.Tests/QaProfileTestApplication.cs |30–31 writes Stripe from bool default false; activates legacy factory; later adds QaStripeSandboxTestConfiguration to Aspire services | Inject scenario through activation services before constructor; remove process Stripe write; retain legitimate Aspire-app-only sandbox service |
| tests/Skoolit.Playwright.Tests/MonitoringReceiptBrowserGateTests.cs |24–28 saves/sets true;117–119 restores | Pass receipt-enabled scenario at line32 factory call; remove saved value/try-finally used only for env restoration |
| tests/Skoolit.Playwright.Tests/AlphaEvidenceLabelAssistanceBrowserTests.cs |40–41 saves/sets1;72–73 restores during fixture disposal | Pass explicit source-only scenario; remove ambient field/write/restore while retaining application/browser disposal |
| tests/Skoolit.Playwright.Tests/AlphaEvidenceSourceOnlyBrowserGateTests.cs | Nine save/set/restore groups:30/31/327,336/337/471,480/481/568,577/578/755,764/765/929,939/940/1517,1527/1528/1815,1826/1827/1887,1921/1922/1999 | Pass explicit per-call source-only scenario in every factory call; retain unrelated browser options and resource cleanup |
| tests/Skoolit.AppHost.Tests/SkoolitKeycloakComponentTests.cs |130–135/166–168 Stripe false;176–186/207–212 Stripe false + forwarding unavailable | Construct component with explicit immutable scenarios; remove ambient save/set/restore; preserve assertions |
| tests/Skoolit.Playwright.Tests/StripeOnboardingBrowserE2eTests.cs |35 passes stripeSandboxEnabled:true; no scenario env mutation here | Preserve explicit opt-in and fixture default false |

Other QA callers use the default fixture. They must receive deterministic default scenarios through that fixture and must not inherit the direct-run scenario environment. SkoolitQaStripeSandboxConfigurationTests checks string parsing; SkoolitQaForwardingConfigurationTests inspects component source contract, so update its references to the scenario/fallback boundary while retaining behavior assertions. No scripts set these four keys in the pinned tree. Direct-run docs name forwarding and Stripe flags and must retain those fallback instructions; monitoring-receipt docs must explain per-call QA selection alongside direct-run use.

## Shared runtime settings that remain separate

QaProfileTestApplication reads DOTNET_USE_POLLING_FILE_WATCHER and writes1 only when blank. AspireDirectRunDefaults.Apply reads/writes ASPNETCORE_URLS=http://localhost:18888, ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL=http://localhost:18889, ASPIRE_DASHBOARD_OTLP_HTTP_ENDPOINT_URL=http://localhost:18890 and ASPIRE_ALLOW_UNSECURED_TRANSPORT=true only when blank. These are process runtime defaults, not scenario inputs. Preserve their fallback semantics and existing serialization; graph overlap must not imply whole-suite parallel safety. Browser channel and optional screenshot path are unrelated test tooling inputs.

## Case handoff and future evidence

The [open case #1259](https://github.com/forge-trust/skoolit/issues/1259) retains future consumer implementation, provenance, and QA acceptance. This inventory is an older source snapshot to reconcile with the implementation checkout before consumer edits. No Skoolit owner or target branch is required for this handoff, and no consumer adoption or QA result is claimed.
