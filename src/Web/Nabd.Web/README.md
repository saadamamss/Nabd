# Nabd Web (Angular)

The P5 dashboard client. Angular 22 (zoneless, signals) + TypeScript, Angular
Material theme tokens, Transloco dictionaries (`ar`/`en`), Vitest on jsdom.

## Run (dev)

The API host is expected on `http://localhost:5109`; `proxy.conf.json` forwards
`/api` and `/hubs` there (WebSocket upgrade included), so the browser stays
same-origin.

```bash
# terminal 1 — the API (repo root)
dotnet run --project src/Hosts/Nabd.ApiHost

# terminal 2 — the app
cd src/Web/Nabd.Web
npm install        # first time
npm start          # http://localhost:4200
```

## Gates

```bash
npm run lint                  # stylelint: physical direction properties are banned
npm test -- --watch=false     # Vitest (headless, no Chrome)
npm run build                 # production bundle; dictionaries land in dist/…/i18n
```

## Conventions that are enforced, not suggested

- Every visible string lives in `public/i18n/{ar,en}.json`; templates must not
  carry Arabic literals. A missing key throws in dev/test.
- `dir` and `lang` live on `<html>`, owned by `LocaleService`; the toggle
  PATCHes the server's locale door, because the stored preference is the
  server's, not the browser's.
- Styles use logical properties only (`margin-inline-start`, `inset-inline-end`,
  `border-start-*`). Stylelint fails the build otherwise.
- The access token stays in memory; only the rotating refresh token goes to
  `sessionStorage`.
