# Public capture fixture preparation

This project prepares the frozen synthetic inputs for the [#888 public CaptureAsync proof](../../../docs/plans/issue-888-keycloak-identity-readiness.md#feasibility-results).
It is preparation code, never copied into or mounted by a runtime proof container. Its test-friend identity permits
source-linking the existing `SecurityRecipeFixture` and `CompleteRecipeFixture` helpers. The signing helper is
copied from the record at the end of `Auth/ForgeTrust.AppSurface.Auth.Keycloak.Tests/SignedRepositoryTests.cs`.
The ordinary public consumer has no such identity, helper code or direct BouncyCastle/Zstd references.

```sh
dotnet run --project tests/keycloak-identity-feasibility/public-capture-fixture-generator/FixtureGenerator.csproj \
  -c Release -p:UseSharedCompilation=false -- /private/tmp/issue888-new-capture-fixtures
```

The destination must be absent. Never overwrite the reviewed frozen fixtures during a runtime proof. Each
regeneration produces new ephemeral signing keys/signatures and can change archive timestamps; it requires review
and a new fixture manifest/proof run. No private signing key is serialized or retained. Only the public keyring,
signed Release, OCI/package bytes and declaration/binding/expectation JSON are emitted. All content is synthetic.

Variants `valid`, `invalid-signature` and `invalid-zstd` each have 12 material identities mapped to eight unique
content-addressed blobs under `blobs-base64/<SHA256>.b64`. Proof staging checks each decoded value against its filename and reconstructs `blobs/<SHA256>` before copying any inputs into the runtime. Source, runtime and frontend identities reuse one OCI index and archive.
The openssl Debian control and data members use Zstandard; the other two required packages use gzip. Invalid
signature changes a cleartext byte without resigning, then rebinds outer hashes. Invalid Zstandard replaces the
compressed data frame and creates matching package/index/Release signatures and outer hashes. Runtime checks
must therefore get beyond outer capture checks before rejecting these variants.

The JSON files are public canonical recipe/policy serialization. `bindings.json` is a fixture-only array of
`{id,file,sha256,maxBytes}` records whose paths are relative to the variant directory. `expectation.json` records
canonical recipe/policy hashes, mode/platform, material count, unique-blob count and synthetic signer fingerprint.
The 20-second operation limit belongs to this experiment; it does not change #888's product worker deadline.
