<!-- appsurface:unreleased-entry section="included" -->
### Aspire MessagePack security update

The [Aspire integration](../../Aspire/ForgeTrust.AppSurface.Aspire/README.md#dependency-security) now requires MessagePack **2.5.303**, fixing [Dependabot alert #14](https://github.com/forge-trust/AppSurface/security/dependabot/14) and [GHSA-qhrr-8q5h-9q3h](https://github.com/MessagePack-CSharp/MessagePack-CSharp/security/advisories/GHSA-qhrr-8q5h-9q3h). Malformed nested containers are rejected before they can reuse trailing bytes to justify excessive allocations during object deserialization. The supported Aspire **13.4.4** baseline is unchanged; retain the MessagePack security floor when updating Aspire.
