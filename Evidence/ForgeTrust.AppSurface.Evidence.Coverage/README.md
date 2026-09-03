# ForgeTrust.AppSurface.Evidence.Coverage

`ForgeTrust.AppSurface.Evidence.Coverage` is a private implementation package shared by the [AppSurface CLI](../../Cli/ForgeTrust.AppSurface.Cli/README.md) and its first-party [Evidence workflow](../ForgeTrust.AppSurface.Evidence.Cli/README.md). It ships only as a transitive support package; it is not a consumer extension point, direct-install package, or separately supported integration package.

The assembly owns one in-process implementation of coverage discovery, execution, watchdog supervision, merge, gate evaluation, patch analysis, and controlled artifact writing. The CLI owns command parsing and presentation; Evidence owns policy and claim translation. This direction ensures Evidence produces coverage claims from the same execution engine as `appsurface coverage run` and `appsurface coverage gate`, without invoking a second process.

All coverage orchestration types are internal and visible only to first-party assemblies. Consumers should use the documented public [coverage commands](../../Cli/ForgeTrust.AppSurface.Cli/README.md#appsurface-coverage-run) and [EvidenceHost workflow](../../start-here/evidencehost.md).
