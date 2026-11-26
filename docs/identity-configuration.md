# Keycloak identity configuration contract

The pure [Keycloak identity configuration types](../Auth/ForgeTrust.AppSurface.Auth.Keycloak/KeycloakDesiredConfiguration.cs) describe non-secret realm and client identity state for a supported local AppHost run. They provide a stable desired-state snapshot for validation and reconciliation. They do not expose Keycloak administrator operations, manage application accounts, or authorize a production realm mutation. For Aspire resource wiring and the existing local proof workflow, see the [Keycloak Aspire package guide](../Auth/ForgeTrust.AppSurface.Auth.Aspire.Keycloak/README.md).

## Define one run's identity

Construct a [KeycloakDesiredConfiguration](../Auth/ForgeTrust.AppSurface.Auth.Keycloak/KeycloakDesiredConfiguration.cs) with an explicit client mode and initialized immutable arrays. Public mode preserves the existing public-client shape; confidential mode requires its client secret through a separate protected adapter binding. No secret belongs in this record or in any mapper, provider label, URI, or diagnostic.

```csharp
using System.Collections.Immutable;
using ForgeTrust.AppSurface.Auth.Keycloak;

var identity = new KeycloakDesiredConfiguration(
    Realm: "sample",
    ClientId: "sample-web",
    Authority: new Uri("https://localhost:8443/realms/sample"),
    ClientMode: KeycloakClientMode.Public,
    RedirectUris: [new Uri("http://localhost:5000/signin-oidc")],
    PostLogoutRedirectUris: [new Uri("http://localhost:5000/signout-callback-oidc")],
    WebOrigins: [new Uri("http://localhost:5000/")],
    Mappers: [],
    IdentityProviders: [],
    RemovedMapperNames: [],
    RemovedIdentityProviderAliases: []);

identity.Validate();
```

`Validate()` is synchronous and effect-free: it does not contact Keycloak, resolve parameters, or create realm files. It checks the declaration through the same finite validator used by the internal reconciliation core. Invalid declarations produce a generic `ArgumentException`; values from the declaration are not copied into its message. Validation establishes only local shape and supported bounds, not server readiness or successful login.

The record and its nested values are immutable: URI objects, strings, `ImmutableArray<T>`, and the mapper/provider records do not expose mutable state. Use a `with` copy to describe a changed run. Default, uninitialized `ImmutableArray<T>` values are invalid; use `[]` for an intentional empty mapper, provider, origin, or removal list. Redirect and post-logout lists must each contain at least one URI. URI declarations preserve their supplied `OriginalString` during projection and use ordinal string equality for duplicate checks; validation does not normalize or rewrite them.

## Client and issuer rules

`Realm` and the application `ClientId` are required identifiers containing 1–128 ASCII letters, digits, hyphens, underscores, or periods. `Authority` must be an HTTPS issuer whose path is exactly `/realms/{Realm}`. A loopback issuer is accepted for local use. A non-loopback issuer must match one of the exact `ApprovedHttpsOrigins`.

Callback URIs must be absolute and unique. HTTP is limited to loopback; HTTPS callbacks must match an approved origin when they are not loopback. Callback URIs cannot contain a wildcard, query, fragment, or user information. `WebOrigins` are exact origins rather than callback paths and follow the same loopback-or-approved-origin rule. Wildcard origins are rejected.

Each approved external origin must be an HTTPS origin on port 443, without a wildcard, path other than `/`, query, fragment, or user information. Up to 32 approved origins are accepted. The optional `TransportOrigin` describes the local worker's administrative HTTP transport, not the public issuer: it must be HTTPS loopback. Set it explicitly when the public issuer is remote and the local worker must connect to Keycloak through a separate loopback endpoint. This configuration does not permit remote administrative transport.

`KeycloakClientMode.Public` and `KeycloakClientMode.Confidential` are explicit choices; the constructor does not guess a mode. The default `AllowPublicDowngrade` value is `false`. Set it to `true` only when intentionally changing an existing confidential client to public. It does not erase dormant server-side credential data. A client mode change is handled as part of a new run, not by a browser request.

## Mappers and Google identity providers

`Mappers` declares generic claim mappings by stable name. At most 128 are accepted. A mapper name is 1–128 trimmed ASCII letters, digits, spaces, hyphens, underscores, or periods. Claim, user-attribute, and session-note identifiers are 1–128 ASCII letters, digits, hyphens, underscores, or periods. The fixed value is limited to 256 characters; do not use it to carry secrets. A mapper may choose a user attribute or a session note, but not both. Mapper names must be unique. Explicit `RemovedMapperNames` use the same name rules, are limited to 128, must be unique, and cannot also appear in `Mappers`.

`IdentityProviders` currently supports Google OpenID Connect only. Each entry has a unique alias using the same 1–128 character identifier rules as `Realm`, a non-secret Google OAuth client ID of 1–512 characters that is not all whitespace and contains no control characters, and an optional display name. A supplied display name must contain 1–128 non-control characters and cannot be blank. At most 16 providers are accepted. The implementation supplies the fixed `openid profile email` scope and `IMPORT` user-sync mode internally; these are not provider-controlled public fields. Google client secrets are bound separately by the local adapter. Provider configuration being observed does not prove Google accepted those credentials or completed a human login. It grants no Gmail, Calendar, or other Google API authorization.

Provider aliases listed in `RemovedIdentityProviderAliases` are explicit removals. Upserts and removals must be disjoint, and at most 16 removals are accepted. Omitting a provider from both lists does not authorize deleting arbitrary provider state. The same rule applies to unmanaged realm and client fields: this declaration owns only the fields it names.

The optional `LoginThemeName` is an identifier for the managed login-theme setting selected by the local adapter. It contains no theme files; the adapter owns local theme-file mounting and custody.

## Bounds, defaults, and common mistakes

The validator accepts at most 32 redirect URIs, 32 post-logout URIs, 32 web origins, 32 approved HTTPS origins, 128 mappers, and 16 Google providers. Removal lists use the corresponding mapper/provider caps. The serialized non-secret declaration is limited to 1 MiB. Values are rejected rather than truncated; repeated values and conflicting upsert/removal declarations fail validation.

Common pitfalls are passing default immutable arrays, omitting a callback URI, using a remote callback without approving its exact origin, treating an issuer URI as the local administrative transport, and putting a provider secret into a display or claim value. `Validate()` cannot prove the pinned Keycloak version's readback behavior, worker dependency closure, process-level secret custody, browser code exchange, or external-provider acceptance. Those require the separate local lifecycle and package proofs; this contract remains a supported local configuration boundary, not a production identity controller.
