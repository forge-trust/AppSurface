# SignedRepository test fixture policy

These fixtures are transient C# data, not production keys, repository approvals, or security claims. Each test generates an ephemeral RSA signing key with Bouncy Castle, creates a fixed-clock clear-signed `InRelease`, and creates bounded gzip package-index bytes in memory. The fixed clock makes policy and expiry assertions reproducible; generated key material is never written to the repository.

The fixture covers valid scope and historical release acceptance, signed Release hash and keyring hash checks, signer fingerprint and algorithm policy, validity and expiry, malformed or duplicate SHA256 records, duplicate package paragraphs, exact compressed package-index digest/size checks, same-size gzip corruption, package tuple selection, revocation, and cancellation. The test project explicitly compiles `SignedRepositoryTests.cs`. The `StaticFixtures/SignedRepository` directory contains no checked-in key, signature, or package bytes.
