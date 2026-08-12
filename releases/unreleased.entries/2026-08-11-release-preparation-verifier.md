<!-- appsurface:unreleased-entry section="included" -->

- Release preparation pull requests now verify the complete base-to-HEAD artifact diff before review, including generated package chooser, readiness, and managed README updates. The read-only verifier preserves the stable-release Docs archive check, reports actionable recovery guidance, and rejects incomplete generated package documentation. PostgreSQL integration tests also tolerate a short host-connection delay after Docker reports a new test container ready, reducing local startup flakiness without masking non-timeout failures.
