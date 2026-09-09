# consultologist-zoom-app

The **Zoom satellite** for Consultologist — a Zoom *General app* that lets a
clinician, from inside a Zoom meeting, turn the meeting's transcript into a
Consultologist consult and see the consults for that meeting. It is a
**delegated-token satellite** over the engine's existing doors
(`docs/SATELLITE_CALLERS.md` in the engine repo): it calls the engine **as the
signed-in clinician**, opens no new engine surface, and depends only on the
`transcript` input origin kind (engine #671, `provenance@v2026.09.6`).

Two legs (`docs/ZOOM_SATELLITE_SPIKE.md`):

- **Leg 1 — documents in the meeting.** The in-client panel lists the clinician's
  consults for the current meeting.
- **Leg 2 — the transcript as an input.** The app fetches the meeting's
  speaker-labeled cloud transcript from Zoom and submits it to the engine as a
  transcript. **Zoom does the transcription** (the clinic's paid feature); we run
  no speech-to-text.

## Architecture — backend-mediated

The ASP.NET Core backend holds both tokens and does all engine work
server-side; the browser panel is a thin [Zoom Apps SDK](https://developers.zoom.us/docs/zoom-apps/)
UI that talks only to this backend. Transcript bytes and tokens never reach the
browser.

- **Microsoft Entra** signs the clinician in (`Microsoft.Identity.Web`, auth-code
  flow) and yields a delegated `access_as_user` token for the engine — no
  On-Behalf-Of needed; #610 refuses app-only tokens, so it is always the
  clinician's token.
- **Zoom user-OAuth** (`cloud_recording:read`) lets the backend read the meeting's
  transcript.

The **verifiability boundary**: the app only fetches a transcript and submits
bytes. Extraction, canonicalisation, the effective-input hash and provenance are
computed by the **engine** at job start — the app never supplies them.

## Layout

```
src/Consultologist.ZoomApp.Core/   SDK-free, unit-tested core
  Engine/    EngineApiClient, EngineDtos, ConsultInputValue   (reused from the engine satellite pattern)
  Transcript/ VttToText (WEBVTT → plain text), TranscriptSubmission (the request builder)
  Meetings/  MeetingJobMap (meeting → job ids, per clinician)
src/Consultologist.ZoomApp/        ASP.NET Core host
  Program.cs   Entra + Zoom wiring; the panel-facing endpoints
  Zoom/        ZoomClient (OAuth + transcript fetch), ZoomAppContext (X-Zoom-App-Context), token store
  wwwroot/     the Zoom Apps SDK panel (index.html + app.js)
tests/Consultologist.ZoomApp.Tests/  xUnit, .Core only
```

Everything verifiable about the scaffold lives in `.Core`; the host is the Zoom /
Entra plumbing around it.

## Build, test, run locally

```bash
dotnet build
dotnet test
cp src/Consultologist.ZoomApp/appsettings.Development.json.sample \
   src/Consultologist.ZoomApp/appsettings.Development.json   # then fill it in (git-ignored)
dotnet run --project src/Consultologist.ZoomApp
```

The Home URL and the Zoom OAuth redirect must be **public HTTPS**, so front the
local app with a tunnel (`devtunnel host -p 5001` or ngrok) and use that host in
the Zoom app config and `Zoom:RedirectUri`.

## Operator runbook (deferred — not done by the scaffold)

### 1. Zoom General app (Marketplace → Develop → Build App → General app)
- **OAuth:** user-managed; scope `cloud_recording:read`; redirect URL
  `https://<host>/zoom/callback`.
- **Home URL** `https://<host>/` (in-client panel). Add the panel's **capabilities**
  (`getRunningContext`, `getMeetingContext`, `getMeetingUUID`, `getUserContext`).
- **Domain Allow List:** our web origin, the engine host
  (`east.ca.api.consultologist.ai`), and `login.microsoftonline.com`.
- The app's **client secret** is also the key for the encrypted `X-Zoom-App-Context`
  header (`ZoomAppContext`, AES-256-GCM, key = SHA-256(secret)). Verify decryption
  against a live launch here.

### 2. Entra app registration (for calling the engine as the clinician)
- A confidential web app: redirect `https://<host>/signin-oidc`, a client secret or
  certificate. This is the app's **own** Entra identity used only to sign the
  clinician in and request the engine scope.

### 3. Admit the app to the engine (engine `docs/SATELLITE_CALLERS.md` §2)
- `az ad sp create --id <entra-app-id>`; a tenant-wide `oauth2PermissionGrant` of
  `access_as_user` on the engine API; **append** the app to the engine API's
  `api.preAuthorizedApplications` (read-modify-write — never replace).
- **CORS:** none needed. This is a backend-mediated (server-to-server) caller, not a
  browser origin fetching the engine, so no `Cors__AllowedOrigins` change.

### 4. Deploy
Continuous deploy is `.github/workflows/deploy.yml` (GitHub → Azure OIDC, no
secrets) to App Service `consultologist-zoom-app`. Set the repo vars
`AZURE_CLIENT_ID` / `AZURE_TENANT_ID` / `AZURE_SUBSCRIPTION_ID` and the `deploy`
environment, and put the real `AzureAd` / `Zoom` / `Engine` settings in App Service
configuration. Prefer a federated credential + user-assigned managed identity over a
client secret for the runtime.

## Configuration

| Key | Meaning |
|---|---|
| `Engine:ApiHost` | The engine API base, e.g. `https://east.ca.api.consultologist.ai/api`. |
| `Engine:AccessAsUserScope` | `api://b3866040-…/access_as_user` — the engine scope the app requests. |
| `Engine:TranscriptInputSlot` | The declared input slot the transcript rides (stamped `transcript`). |
| `AzureAd:*` | The app's Entra registration (tenant, client id, secret, callback). |
| `Zoom:ClientId` / `ClientSecret` | The Zoom General app's OAuth credentials. |
| `Zoom:RedirectUri` | `https://<host>/zoom/callback`. |
| `Zoom:Scopes` | `cloud_recording:read`. |

Secrets live in the git-ignored `appsettings.Development.json` (local) or App
Service settings (deployed); the checked-in `appsettings.json` carries placeholders.

## License

PolyForm Strict 1.0.0 — see `LICENSE.md`.
