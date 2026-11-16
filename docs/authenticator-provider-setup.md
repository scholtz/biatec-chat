# Authenticator: pointing the app at a provider

This is for the engineers of a **provider**: an organisation, a bank for example, that runs its own OpenID Connect
server and wants Biatec Authenticator to enroll devices against it. It says what the server must offer, what client
to register, and which build settings point the app at that server. Stage Biatec OIDC is the worked example
throughout.

The provider is fixed when the app is built. Choosing it at run time from a profile address or a QR code is planned
(task T-249) and does not exist yet.

## 1. What the app does at the provider

1. **Sign-in.** In the step "Connect this device to your account" the user taps **Sign in**. The app opens the
   system browser (never an embedded web view) on the provider's authorization endpoint: authorization code with PKCE
   (`S256`), a fresh `state` and `nonce`, `prompt=login` and `max_age=0`. The app is a public client: it has no
   secret.
2. **Token.** The app redeems the code at the token endpoint with the PKCE verifier and gets an access token. It asks
   for no refresh token and stores nothing of the login.
3. **Enrollment.** The app generates the device key and calls `POST /devices` on the provider with
   `Authorization: Bearer <access token>`. The provider answers with the device token. The access token is dropped.
4. **Afterwards** every call carries the device token and a proof signed by the device key. The login is not used
   again.

## 2. What the server must offer

| Requirement | Detail |
| --- | --- |
| HTTPS on one origin | The base URL (`OIDC_BASE_URL`) is `https`, with no credentials, query or fragment. Every endpoint below is on that origin. |
| Discovery | `GET <base>/.well-known/openid-configuration` answers `200` with JSON. `issuer` equals the base URL exactly (a trailing slash is ignored). `authorization_endpoint` and `token_endpoint` are `https` on the same host and port. If `code_challenge_methods_supported` is present it contains `S256`. The app refuses to sign in otherwise. |
| Authorization endpoint | Authorization code flow for a public client with PKCE `S256`. Parameters the app sends: `response_type=code`, `client_id`, `redirect_uri`, `scope`, `state`, `nonce`, `code_challenge`, `code_challenge_method=S256`, `prompt=login`, `max_age=0`. The answer goes to the redirect as `code` and `state`, or `error` and `state` (RFC 6749, 4.1.2). |
| Token endpoint | `POST` form with `grant_type=authorization_code`, `code`, `redirect_uri`, `client_id`, `code_verifier`; no client authentication. Answers `200` JSON with `access_token`, `token_type` `Bearer` (if present) and an `id_token` whose `nonce` is the one sent, whose `iss` is the issuer and whose `aud` is or contains the client id. It must not answer with a redirect: the app does not follow one. |
| Device API | `POST /devices` and the other device and consent endpoints of the Biatec Consent Protocol ([spec](../packages/consent-protocol/spec/biatec-consent-protocol.md)). `POST /devices` accepts the access token of an ordinary user login. Run the conformance kit against the server: `pnpm --filter @biatec/consent-protocol conformance` ([consent-rollout.md](consent-rollout.md)). |
| A stable TLS key | The app pins the public key of the server's certificate (section 5), so the key must stay the same across certificate renewals. |

Scope: the app asks for `openid` only. When the build has `IN_APP_LOGIN=true` it asks for `openid enroll-device`,
which the server must then know and enforce together with `auth_time`
([required-changes-biatec-oidc.md](required-changes-biatec-oidc.md)).

`prompt=login` and `max_age=0` ask the server to make the user authenticate again. **Honour them.** A server that
ignores them (today's Biatec OIDC does) still works, but whoever has a session in the computer's default browser is
then signed in without typing anything, and that login enrolls the device. If the ID token carries `auth_time`, the
app refuses one older than 10 minutes; without `auth_time` the app cannot tell. The enforced version of this rule
(`enroll-device` scope and `auth_time` checked by `POST /devices`) is the in-app login of
[required-changes-biatec-oidc.md](required-changes-biatec-oidc.md), `IN_APP_LOGIN=true`.

## 3. The client to register

Register one **public** client (no secret) per platform family, with PKCE required.

### Desktop (Windows, Linux, macOS)

A desktop app cannot receive an app link, so it listens on the loopback interface for the browser's answer
(RFC 8252, section 7.3).

| Setting | Value |
| --- | --- |
| Client secret | none (public client) |
| Redirect URI | `http://localhost:42815` |
| Scopes | `openid` |

**For a new client prefer `http://127.0.0.1:42815`** and build with `OIDC_REDIRECT_URI=http://127.0.0.1:42815`
(RFC 8252, section 8.3). `localhost` is a name: a browser that remembers "always use https" for `localhost` (a
developer machine that once ran a local https server) turns the redirect into `https` and the sign-in never arrives.
The default stays `http://localhost:42815` because that is what stage has registered.

The redirect must be registered exactly as the build has it (`OIDC_REDIRECT_URI`, default `http://localhost:42815`).
Rules the app enforces on it: scheme `http`, host `localhost`, `127.0.0.1` or `[::1]`, an explicit port from 1024 up,
no credentials, no query, no fragment. A path is allowed (`http://127.0.0.1:50123/callback`). If the server matches
redirects on any port for loopback hosts, the port is still the one in the build.

Stage Biatec OIDC has this client:

```
JwtIssuer__Clients__4__ClientId="biatec-authenticator-stage-app"
JwtIssuer__Clients__4__DisplayName="Biatec Authenticator Stage"
JwtIssuer__Clients__4__ClientSecret=""
JwtIssuer__Clients__4__RedirectUris__0="http://localhost:42815"
JwtIssuer__Clients__4__AllowedScopes__0="openid"
JwtIssuer__Clients__4__AllowedScopes__1="profile"
JwtIssuer__Clients__4__AllowedScopes__2="email"
```

Biatec OIDC also needs `JwtIssuer:AllowHttpForLoopbackRedirectUris` on, or it refuses a plain `http` redirect. Do
not add redirects the app does not use to this client: every extra redirect is another place an authorization code
can be sent. (The Postman callback in the stage configuration is such an extra; keep it out of a production client.)

### Phones (Android, iOS)

A release build on a phone returns from the browser through an **app link** (an `https` address the operating system
opens only in this app). A custom scheme is refused in a release build, because any app can register one.

| Setting | Value |
| --- | --- |
| Client secret | none (public client) |
| Redirect URI | `https://<provider host>/app/authenticator/callback` |
| Scopes | `openid` (and `enroll-device` with `IN_APP_LOGIN=true`) |

This needs three things that are compiled into the app, so a phone build for a new provider is a code change, not
only build settings:

1. The host in `AppConfig.appLinkHosts` (`apps/biatec_authenticator/lib/config.dart`).
2. The host in the Android manifest (an `autoVerify` intent filter for `/app/authenticator/callback`) and in the iOS
   entitlements (`applinks:<host>`).
3. On the provider's host: `/.well-known/assetlinks.json` (Android, with the package and the release signing
   certificate) and `/.well-known/apple-app-site-association` (iOS). iOS needs 17.4 or later for the `https` callback.

Only `oidc.biatec.io` and `stage.oidc.biatec.io` are compiled in today.

## 4. Build settings

All are `--dart-define=NAME=value` on `flutter build` or `flutter run`.

| Setting | Meaning | Default |
| --- | --- | --- |
| `OIDC_BASE_URL` | The provider's base URL (the issuer). | Windows release build: `https://stage.oidc.biatec.io`. Other release builds: none, the build refuses to start. Debug: the mock. |
| `OIDC_CLIENT_ID` | The public client of section 3. | `biatec-authenticator-stage-app` on a desktop build against stage; otherwise `biatec-authenticator`. |
| `OIDC_REDIRECT_URI` | The registered redirect. | Desktop: `http://localhost:42815`. Phone release: the app link on the provider's host. Phone debug: `io.biatec.authenticator:/oauth2redirect`. |
| `BROWSER_LOGIN` | `true`: the connect step has the Sign in button. `false`: it does not. | On for a desktop build against stage and whenever `IN_APP_LOGIN` is on; otherwise off. |
| `OIDC_PINS` | Public key pins of the provider's server, comma separated (section 5). | The table in `lib/security/pinning/pins.dart`; a host with no pin stops the app at start. |
| `IN_APP_LOGIN`, `TWO_KEY_DEVICE`, `DEVICE_CHANNEL`, `TRUST_SETTINGS`, `RELYING_PARTIES`, `CONSENT_SETTINGS_API` | Server features of the consent protocol 1.1 and 1.2. Turn one on only when the server has it. | Off (on for the mock). |

Stage, Windows, nothing to pass:

```
cd apps/biatec_authenticator
flutter build windows
```

Another provider, Windows:

```
flutter build windows ^
  --dart-define=OIDC_BASE_URL=https://id.bank.example ^
  --dart-define=OIDC_CLIENT_ID=bank-authenticator-desktop ^
  --dart-define=BROWSER_LOGIN=true ^
  --dart-define=OIDC_PINS=sha256/<current key>,sha256/<backup key>
```

A setting the app cannot accept (a base URL that is not `https`, a desktop redirect that is not a loopback address, a
host with no pin) stops the app at start with a screen that says which.

## 5. Pins

The app does not trust the system's certificate authorities for the provider. It accepts the provider's server only
when the public key of its certificate is one of the build's pins. How to compute a pin, why a backup key is needed
and how to rotate: [AUTHENTICATOR-PINNING.md](AUTHENTICATOR-PINNING.md). A release build on a phone needs at least
two pins.

## 6. What protects the desktop sign-in

| Risk | What the app does |
| --- | --- |
| Another program on the network reaches the listener | The listener is bound to `127.0.0.1` and `::1` only, for the length of one sign-in (at most 5 minutes), and is closed however the sign-in ends. |
| Another page or program on the same computer sends a forged answer, or floods the listener | Only a `GET` on the redirect's path with this sign-in's `state` (256 random bits, compared in constant time) is taken. Everything else gets a fixed error page and is ignored, however often it comes. |
| Something reads the authorization code (browser history, another program holding the port) | The code is useless without the PKCE verifier, which never leaves the app. If the port is already in use on the IPv4 or the IPv6 loopback address the sign-in does not start. |
| The token answer belongs to another sign-in or another server | The ID token's `nonce`, `iss` and `aud` are checked. The discovery document must name the configured issuer and keep both endpoints on its host. |
| The code or verifier is sent somewhere else | The token request goes to the token endpoint over the pinned connection and a redirect answer is not followed. |
| The provider's error text is shown or logged | Only the error code (`[a-z_]`, at most 40 characters) is shown. The page the listener serves is fixed text with `Content-Security-Policy: default-src 'none'`. |
| The browser is started through a shell | The system's URL handler is started by its full path with the address as one argument. |
| The token is kept | The access token is held in memory for the enrollment calls and dropped. No refresh token is requested. |

Limits to know:

- **A desktop build signs in but cannot enroll yet.** The device keys live in the phone's Secure Enclave or StrongBox
  through native code that exists for Android and iOS only. On Windows, Linux and macOS there is no key store, the
  app refuses to keep a device key in software, and after the sign-in it says "This build of the app has no protected
  key store on this kind of device". A Windows key store (TPM and Windows Hello) is task T-250.
- A program that already runs as the same user on the same computer can read the app's memory and files, and no login
  flow protects against that.

## 7. Checking a setup

1. `curl https://<host>/.well-known/openid-configuration` and compare with section 2.
2. Open in a browser (one line): `https://<host>/authorize?response_type=code&client_id=<client>&redirect_uri=http%3A%2F%2Flocalhost%3A42815&scope=openid&state=<43 or more characters>&nonce=n&code_challenge=E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM&code_challenge_method=S256`.
   The login page must appear. An error page here means the client, the redirect or PKCE is not set up.
3. Build, start the app, tap **Get started**, then **Sign in**. The browser opens; after the login the page says
   "The sign-in was received". On a phone the app then shows the next step; on a desktop build it reports the missing key
   store (section 6).
4. Automated checks of the app side: `flutter test test/enrollment/loopback_auth_client_test.dart test/enrollment/desktop_login_test.dart`.

Messages the app shows and what they mean:

| Message | Cause |
| --- | --- |
| "The discovery document names another issuer" | `issuer` in the discovery document is not `OIDC_BASE_URL`. |
| "The provider refused the sign-in (invalid_scope)" and similar | The authorization endpoint answered with that error code: check the client's scopes and redirect. |
| "Port 42815 on this computer is in use" | Another program holds the port; close it, or register and build another port. |
| "Port 42815 on this computer cannot be opened for the sign-in" | The system refused the port for another reason, for example a reserved range (Hyper-V, WSL, Docker: `netsh interface ipv4 show excludedportrange protocol=tcp`). Register and build another port. |
| The browser shows a connection error on `https://localhost:42815` | The browser upgraded the redirect to https. Use the `127.0.0.1` redirect. |
| "The ID token is not for this sign-in" | The server did not copy `nonce` into the ID token. |
| "The connection to <host> could not be verified" | The server's key is not one of the build's pins. |
