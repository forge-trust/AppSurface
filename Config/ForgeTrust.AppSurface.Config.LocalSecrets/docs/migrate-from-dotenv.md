# Migrate From .env

LocalSecrets does not parse `.env` files directly. Move each secret into the local store using the same AppSurface config
key that the app already expects.

Example `.env` value:

```text
Stripe__ApiKey=<secret>
```

Equivalent LocalSecrets command:

```bash
printf '%s' "<secret>" | appsurface secrets set Stripe:ApiKey --app MyApp --environment Development --stdin
```

Delete the `.env` value after migration and keep `.env`-style files out of source control. Use `appsurface secrets
doctor` when a platform store cannot be reached from the current shell or desktop session.
