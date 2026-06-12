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

### Tailwind CSS MIT License Text

MIT License

Copyright (c) Tailwind Labs, Inc.

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.

### CliWrap MIT License Text

MIT License

Copyright (c) 2017-2026 Oleksii Holub

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
