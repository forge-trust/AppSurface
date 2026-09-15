# Third-Party Notices

ForgeTrust.AppSurface.Web.Tailwind and its retained direct companion packages include the following third-party attribution in addition to the repository license.

## Tailwind CSS Standalone CLI

- Component: Tailwind CSS standalone CLI
- Version: `4.1.18`
- License: MIT
- Project: https://github.com/tailwindlabs/tailwindcss
- Main package release metadata: `build/tailwind.release.json`
- Direct companion payload path: `runtimes/<rid>/native/tailwindcss-*`

The main package records five package-pinned standalone-CLI digests in
`tailwind.release.json`; normal consumers acquire and verify one build-host binary in a
local cache and do not receive a native application payload. Direct companion packages
remain separately packable compatibility artifacts. Their runtime target validates a
downloaded binary before packing its intentional native payload.

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
