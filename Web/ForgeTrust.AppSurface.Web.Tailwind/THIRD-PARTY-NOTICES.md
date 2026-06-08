# Third-Party Notices

ForgeTrust.AppSurface.Web.Tailwind packages include the following third-party payloads in addition to the repository license.

## Tailwind CSS Standalone CLI

- Component: Tailwind CSS standalone CLI
- Version: `4.1.18`
- License: MIT
- Project: https://github.com/tailwindlabs/tailwindcss
- Packaged payload path: `runtimes/<rid>/native/tailwindcss-*`

The runtime packages download the Tailwind CSS standalone CLI from the pinned Tailwind release URL declared in `Tailwind.Common.props`. Package validation requires `TailwindRuntimeBinaryResolutionEnabled=true`, and the runtime target validates the downloaded binary against the upstream SHA-256 sums before the native payload is packed.

## CliWrap

- Component: CliWrap
- Version: `3.10.1`
- License: MIT
- Project: https://github.com/Tyrrrz/CliWrap
- Packaged payload path: `build/tasks/CliWrap.dll`

CliWrap is redistributed in the AppSurface Tailwind package build tasks so consuming projects can invoke the Tailwind standalone CLI from MSBuild without adding their own task dependency.

No endorsement is implied by AppSurface release notes, marketing copy, package metadata, or generated CSS output.
