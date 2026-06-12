# Hybrid Hosting With Cloud Run

Hybrid hosting keeps static pages cheap while leaving RazorWire-managed live behavior on a real ASP.NET Core app. The public site can serve exported files from `https://www.example.com`, while streams, islands, and safe RazorWire forms call `https://api.example.com`.

Cloud Run is the worked live-host example in this guide because it can scale request-driven services down to zero idle instances. The RazorWire contract stays provider-neutral: Fly.io, Azure Container Apps, or another container host can serve the same live origin if it can run the ASP.NET Core app and satisfy the CORS and credential requirements below.

## Hosting Model

Use two origins with distinct jobs:

| Origin | Owns | Example |
| --- | --- | --- |
| Static origin | Exported HTML, CSS, JavaScript, images, search indexes, canonical metadata, and static route artifacts | `https://www.example.com` |
| Live origin | RazorWire streams, server-backed islands, lazy anti-forgery token refresh, and RazorWire-managed form posts | `https://api.example.com` |

Export turns RazorWire-owned live surfaces toward the live origin only when `--live-origin` is set:

```bash
razorwire export --mode hybrid \
  --live-origin https://api.example.com \
  --project ./MyApp.csproj \
  --output ./dist
```

`--hybrid-credentials auto` is the default. With a live origin, `auto` includes credentials for managed live calls so cookies and anti-forgery token refresh can work. Use `--hybrid-credentials omit` only for intentionally anonymous live endpoints that do not use cookies or anti-forgery.

## Local Proof First

Prove the split before deploying anything:

1. Run the live app locally on one origin.

   ```bash
   dotnet run --project ./MyApp.csproj --urls http://127.0.0.1:5100
   ```

   For the local proof, configure the live app's CORS policy to allow the static test origin `http://127.0.0.1:8011`. The production CORS sample below uses `https://www.example.com`; use the origin that actually serves the exported files in each environment.

2. Export hybrid output that points live calls at the running app.

   ```bash
   razorwire export --mode hybrid \
     --live-origin http://127.0.0.1:5100 \
     --url http://127.0.0.1:5100 \
     --output ./dist
   ```

3. Serve the exported folder from a different local origin.

   ```bash
   python3 -m http.server 8011 --directory ./dist
   ```

4. Open `http://127.0.0.1:8011` and verify the network panel:

   - Static page, script, style, and image requests stay on `127.0.0.1:8011`.
   - RazorWire stream, island, lazy token, and form requests go to `127.0.0.1:5100`.
   - The first form intent may call `/_rw/antiforgery/token`.
   - A stream or form failure names CORS or credentials in development diagnostics when those settings are wrong.

Do not skip this step. It catches the same origin split, cookie, token, and CORS mistakes that are hardest to debug after DNS and Cloud Run are involved.

## Cloud Run Recipe

Use Cloud Run for the live origin, then publish the exported folder to the static host you already use.

1. Deploy the ASP.NET Core app to Cloud Run.

   ```bash
   gcloud run deploy myapp-live \
     --source . \
     --region us-central1 \
     --allow-unauthenticated
   ```

   Google documents `gcloud run deploy SERVICE --source .` as the source deployment path for Cloud Run services. Source deployment uses Cloud Build and Artifact Registry behind the scenes; use an image-based deploy instead if your release pipeline already builds container images.

2. Map a custom domain such as `api.example.com` to the Cloud Run service.

3. Export the static site using the live origin.

   ```bash
   razorwire export --mode hybrid \
     --live-origin https://api.example.com \
     --project ./MyApp.csproj \
     --output ./dist
   ```

4. Publish `./dist` to the static origin such as `https://www.example.com`.

5. Verify production with the same split as the local proof:

   - Page views load from the static origin.
   - The first stream, island, or form interaction calls the Cloud Run origin.
   - Lazy anti-forgery calls use `https://api.example.com/_rw/antiforgery/token`.
   - Browser cookies that the live app needs are sent on credentialed live requests.

### Scale To Zero And Cold Starts

Cloud Run service-level minimum instances default to `0`, so request-driven services can avoid idle-instance cost. The first live request after idle time may pay a cold start while Cloud Run starts an instance. Google also notes that Cloud Run may keep idle instances briefly after requests finish, but permanent warm capacity requires the minimum instances setting and costs money.

Document that tradeoff in your own runbook:

| Need | Cloud Run setting |
| --- | --- |
| Lowest idle cost | Keep minimum instances at `0` |
| Lower first-interaction latency | Set service-level minimum instances above `0` |
| Protect databases or backing services | Set an appropriate maximum instances limit |

Official references:

- [Deploy services from source code](https://cloud.google.com/run/docs/deploying-source-code)
- [Set minimum instances](https://cloud.google.com/run/docs/configuring/min-instances)
- [About instance autoscaling](https://cloud.google.com/run/docs/about-instance-autoscaling)

## CORS And Credentials

CORS is the browser rule set that decides whether JavaScript from the static origin may call the live origin. Split-origin hybrid hosting needs explicit CORS when `https://www.example.com` calls `https://api.example.com`.

Configure the live app so RazorWire endpoints allow the static origin:

```csharp
builder.Services.AddCors(options =>
{
    options.AddPolicy("StaticSite", policy =>
    {
        policy
            .WithOrigins("https://www.example.com")
            .AllowCredentials()
            .AllowAnyHeader()
            .AllowAnyMethod();
    });
});

builder.Services.AddRazorWire(options =>
{
    options.Hybrid.LiveOrigin = "https://api.example.com";
    options.Hybrid.CorsPolicyName = "StaticSite";
});
```

Install the CORS middleware before mapped endpoints execute, then map RazorWire after the RazorWire services are registered:

```csharp
var app = builder.Build();

app.UseRouting();
app.UseCors();

app.MapRazorWire();
```

If the live app also uses authentication or authorization middleware, keep the usual ASP.NET Core order: routing, CORS, authentication, authorization, then mapped endpoints.

RazorWire can apply the configured CORS policy to its stream and token endpoints. App-owned MVC or API endpoints that forms post to remain app-owned; apply the same CORS policy to those endpoints when they receive split-origin requests.

Use credentialed requests only when needed. Most form, token, and authenticated stream flows need cookies, so the default `--hybrid-credentials auto` includes credentials when `--live-origin` is set. Anonymous live streams can opt out with `--hybrid-credentials omit`, but forms that need anti-forgery will fail export if credentials are omitted.

## Anti-Forgery

Static HTML cannot safely carry the anti-forgery token minted for the export crawler. RazorWire handles the safe hybrid case automatically:

- RazorWire-managed forms with static `__RequestVerificationToken` inputs are converted to lazy token refresh.
- The exporter removes stale static token inputs.
- The rendered form is marked `data-rw-antiforgery="lazy"`.
- The browser fetches a fresh token from the live origin on first form intent or just before submit.

You can write `rw-antiforgery="lazy"` on a form as an explicit assertion, but it is not required for safe auto-conversion. Do not use `rw-antiforgery="off"` on exported forms that still contain anti-forgery tokens unless the form is loaded from a live endpoint instead of static HTML.

`RWEXPORT006` fails unsafe cases before publish: unmanaged forms, external form actions, CDN mode anti-forgery, explicit opt-out with a static token, or split-origin credentials set to `omit` when lazy token refresh is required. The fix is to let RazorWire manage the form, use `--mode hybrid --live-origin`, keep credentialed managed calls enabled, or load the form from a live frame.

See [Security & Anti-Forgery](antiforgery.md) for the full form-token contract.

## AppSurface Docs Export

Use `appsurface docs export` when AppSurface owns the repository docs host. `--public-origin` describes the static docs origin and `--live-origin` describes the live RazorWire origin:

```bash
appsurface docs export \
  --repo . \
  --output ./dist/docs \
  --mode hybrid \
  --public-origin https://docs.example.com \
  --live-origin https://api.example.com
```

The static docs output still owns canonical metadata, route manifests, release manifests, search payloads, scripts, styles, and images. The live origin owns only RazorWire-managed runtime behavior. `appsurface docs export` intentionally does not expose `--publish-root-extras`; deployment-owned files such as `CNAME` belong in the surrounding publish root outside exact docs release archives.

## Troubleshooting

| Symptom | Likely cause | Fix |
| --- | --- | --- |
| Browser blocks the stream or token request | Missing CORS policy for the static origin | Allow the static origin on the live app and map RazorWire with the configured policy |
| Form token refresh fails in development | Live origin is unreachable, CORS rejects credentials, or `MapRazorWire()` is missing | Check the live URL, CORS policy, and RazorWire endpoint mapping |
| Export fails with `RWEXPORT006` | Static anti-forgery cannot be made safe | Let RazorWire manage the form, keep credentials enabled, or load the form from a live endpoint |
| First interaction feels slow after idle time | Cloud Run cold start | Keep min instances at `0` for lowest cost, or set service-level minimum instances for warmer latency |
| Static assets call the live origin | The asset URL was authored as a live URL or served from app-only infrastructure | Keep browser-delivered assets in the exported static output or externalize them intentionally |
