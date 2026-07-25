# Third-Party Notices

ForgeTrust.RazorWire includes the following third-party component in addition to the repository license.

## Hotwire Turbo

- Package: `@hotwired/turbo`
- Version: `8.0.23`
- License: MIT
- Project: https://github.com/hotwired/turbo
- Source package path: `Web/ForgeTrust.RazorWire/node_modules/@hotwired/turbo/dist/turbo.es2017-umd.js`
- Packaged asset path: `Web/ForgeTrust.RazorWire/wwwroot/razorwire/turbo.es2017-umd.js`
- SHA-256: `f9e09e3a3093874fe56d5341ca3594ac959f8b097c9b6171a5b37838da3aec81`

RazorWire copies the official distributed UMD browser runtime byte for byte. The repository pins the package in `Web/ForgeTrust.RazorWire/package.json` and `Web/pnpm-lock.yaml`; the RazorWire asset build verifies the approved SHA-256 digest before copying it to the committed package asset path.

To update Turbo, change the pinned package version, verify the upstream release and license, update the expected digest and size budget in `assets/scripts/build.mjs`, rebuild the assets, and update this notice and the custody tests in the same reviewed change.

Do not edit the copied asset by hand or pass it through the first-party esbuild pipeline.

### MIT License Text

Copyright (c) 37signals

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
