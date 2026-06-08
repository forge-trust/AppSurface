# Third-Party Notices

ForgeTrust.AppSurface.Cli includes the following third-party payloads in addition to the repository license.

## ReportGenerator

- Package: `ReportGenerator`
- Version: `5.5.10`
- License: Apache-2.0
- Project: https://github.com/danielpalme/ReportGenerator
- Packaged payload path: `tools/net10.0/any/reportgenerator/`

ReportGenerator is redistributed inside the AppSurface CLI package so `appsurface coverage run` can merge Cobertura coverage locally without reading or mutating a consumer repository's .NET tool manifest.

No endorsement is implied by AppSurface release notes, marketing copy, package metadata, or generated coverage reports.
