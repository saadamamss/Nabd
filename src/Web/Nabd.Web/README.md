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
- `dir` and `lang` live on `<html>`, owned by `LocaleService`; signed in, the
  toggle PATCHes the server's locale door, because the stored preference is
  the server's, not the browser's. A signed-out switch is mirrored to
  `localStorage` (locale only — never a token) so `/login` boots in it, and —
  if that same tab then signs in — follows the user into the app and is PATCHed
  to their account, so the server stays the single stored truth. Without an
  explicit login-page choice, the account's locale boots the app.
- Styles use logical properties only (`margin-inline-start`, `inset-inline-end`,
  `border-start-*`). Stylelint fails the build otherwise.
- Pages read the design tokens in `src/styles.scss` (`--app-canvas`,
  `--app-surface`, `--app-text`, `--app-brand`, `--app-radius-*`,
  `--app-shadow-*`); no page hard-codes a surface or text colour. The palette
  follows the OS light/dark scheme, so dark mode comes from the tokens.
- Type is self-hosted IBM Plex Sans (Latin, variable 100–700) + IBM Plex Sans
  Arabic, split per script with `unicode-range` so neither language downloads
  the other's faces. Both OFL; `public/fonts/LICENSE.txt`.
- The access token stays in memory. The rotating refresh token is an
  **HttpOnly cookie** (`nabd_refresh`, `SameSite=Strict`, `Path=/api/v1/auth`)
  set, rotated and cleared by the API, so no `sessionStorage`/`localStorage`
  entry exists for it and script cannot read it. `withCredentials` on the auth
  doors is what makes reload restore work; a reload always calls `/refresh`.
