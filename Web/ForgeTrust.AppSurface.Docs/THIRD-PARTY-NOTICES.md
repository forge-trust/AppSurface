# Third-Party Notices

ForgeTrust.AppSurface.Docs includes the following third-party components in addition to the repository license.

## Acornima

- Package: `Acornima`
- Version: `1.6.2`
- License: BSD-3-Clause
- Project: https://github.com/adams85/acornima

Acornima is used by AppSurface Docs to parse configured JavaScript source files for public API documentation. The package is redistributed under the BSD-3-Clause license terms supplied by Acornima and its contributors.

No endorsement is implied by AppSurface or AppSurface Docs release notes, marketing copy, package metadata, or generated documentation.

## MiniSearch

- Package: `minisearch`
- Version: `7.2.0`
- License: MIT
- Project: https://lucaong.github.io/minisearch/
- Source package path: `Web/node_modules/minisearch/dist/umd/index.js`
- Generated asset path: `Web/ForgeTrust.AppSurface.Docs/wwwroot/docs/minisearch.min.js`

MiniSearch provides the real browser search runtime used by the built-in AppSurface Docs search UI. The repository pins the package in `Web/package.json` and `Web/pnpm-lock.yaml`; `pnpm --dir Web run assets:build` minifies the official distributed UMD browser bundle into the committed package asset path.

To update MiniSearch, change the pinned `minisearch` version in `Web/package.json`, run `pnpm --dir Web install`, run `pnpm --dir Web run assets:build`, verify this notice still names the correct package version, source path, and license, then run `pnpm --dir Web run assets:verify`.

Do not replace this asset with a CDN URL. AppSurface Docs search must continue to work in offline static exports and package-embedded hosts.

### MIT License Text

Copyright 2022 Luca Ongaro

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

### BSD-3-Clause License Text

Copyright (c) Adam Simon
All rights reserved.

Redistribution and use in source and binary forms, with or without modification, are permitted provided that the following conditions are met:

1. Redistributions of source code must retain the above copyright notice, this list of conditions and the following disclaimer.

2. Redistributions in binary form must reproduce the above copyright notice, this list of conditions and the following disclaimer in the documentation and/or other materials provided with the distribution.

3. Neither the name of the copyright holder nor the names of its contributors may be used to endorse or promote products derived from this software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT INCLUDING NEGLIGENCE OR OTHERWISE ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
