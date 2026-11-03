# Sign-in, email and phone notifications on a self-hosted server

Everything here needs no BuoyAI account and no hosted service, except the optional push relay.
The hosted app is unchanged: none of these rules apply when `BuoyMoney__Edition` is unset.

## Sign-in methods

| Method | Self-host default | Notes |
|---|---|---|
| Email and password | On | The baseline. |
| Authenticator app (TOTP) | On | Recovery codes are the way back in if you lose the phone and have no email set up. |
| Passkeys on the web | On when the address is HTTPS (or `localhost`) | Tied to `BUOY_HOST` and `BUOY_ORIGIN` in `.env`. Changing them later invalidates every passkey. |
| Google sign-in | Off | Turns on only when you set `GoogleAuth__ClientId` to your own Google OAuth client. Until then the endpoint answers 404 and the app hides the button. |
| Apple sign-in | Off | Not offered on self-host. |
| Single sign-on (OpenID Connect) | Off | Pocket ID, Authentik, Keycloak and others. See [Single sign-on](#single-sign-on-pocket-id-authentik-keycloak). |
| Native passkeys (phone apps) | Optional | Follow the same address rule. |

The app learns what is available from `GET /api/system/version`, which lists `features.googleSignIn`,
`features.passkeys`, `features.email`, `features.oidc` (with `oidcDisplayName`) and
`features.passwordLogin`.

## Who can register

1. The first account created on a fresh server becomes its admin. Register it yourself before
   you share the address with anyone. (Whoever reaches a brand-new server first owns it.)
2. After that, registration is closed. A stranger who tries gets "This server is not accepting new
   accounts".
3. The admin opens it again or invites people. As admin, call these (the app screen for them is
   not built yet):

```bash
# sign in as the admin and copy the "token" value from the reply
curl -X POST http://localhost:8080/api/auth/login -H "Content-Type: application/json" \
  -d '{"email":"you@example.com","password":"your password"}'
TOKEN=...   # paste it here (it works for an hour)

# make a one-time invite (valid 7 days by default); the code is shown once
curl -X POST http://localhost:8080/api/registration/invites -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" -d '{"note":"my partner","validDays":7}'
# open or close registration for everyone
curl -X PUT http://localhost:8080/api/registration/open -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" -d '{"open":true}'
# see invites, or delete an unused one
curl http://localhost:8080/api/registration/admin -H "Authorization: Bearer $TOKEN"
curl -X DELETE http://localhost:8080/api/registration/invites/3 -H "Authorization: Bearer $TOKEN"
```

The person registers with the code in the sign-up form (`inviteCode`). Codes are single use, case
and dashes do not matter, and only a hash is stored. Invited people are ordinary members.

Servers created before this release keep working: the people on `Admin__Emails` are still admins,
and registration starts closed.

## Single sign-on (Pocket ID, Authentik, Keycloak)

Buoy Money can hand sign-in to your own OpenID Connect provider, like the "OAuth login" in Immich
or Paperless. The sign-in page then shows a "Sign in with Pocket ID" button (and "Sign up with
Pocket ID" on the sign-up page). Self-host only; on the hosted service these endpoints answer 404.

### Pocket ID

[Pocket ID](https://pocket-id.org) signs people in with passkeys only, which makes it a good fit.

1. In Pocket ID, open **OIDC Clients** and add a client named `Buoy Money`.
2. Set the callback URL to `<BUOY_ORIGIN>/api/auth/oidc/callback`, for example
   `https://buoy.example.ts.net/api/auth/oidc/callback`. It must match exactly, including `https`.
3. Leave it a confidential client (PKCE can stay on; Buoy Money always sends it).
4. Copy the client ID and client secret into `selfhost/.env`:

```bash
BUOY_OIDC_ENABLED=true
BUOY_OIDC_AUTHORITY=https://id.example.ts.net   # your Pocket ID address, no trailing slash
BUOY_OIDC_CLIENT_ID=...
BUOY_OIDC_CLIENT_SECRET=...
BUOY_OIDC_DISPLAY_NAME=Pocket ID
```

5. Make sure `BUOY_ORIGIN` is set to the address people use. The callback URL and the page the
   browser comes back to are built from it.
6. `docker compose up -d` to restart. The button appears once the server answers.

Authentik, Keycloak and other providers work the same way: make a confidential OpenID Connect
client with the callback URL above and the scopes `openid email profile`, and set the authority to
the provider's issuer URL (the address in front of `/.well-known/openid-configuration`).

### Settings

| `.env` | Default | What it does |
|---|---|---|
| `BUOY_OIDC_ENABLED` | `false` | Turns single sign-on on. |
| `BUOY_OIDC_AUTHORITY` | | The provider's issuer URL. |
| `BUOY_OIDC_CLIENT_ID` / `BUOY_OIDC_CLIENT_SECRET` | | From the provider. The secret is optional for a public client. |
| `BUOY_OIDC_DISPLAY_NAME` | `Single sign-on` | The button label: "Sign in with ...". |
| `BUOY_OIDC_AUTO_REGISTER` | `false` | See "Who gets in" below. |
| `BUOY_OIDC_DISABLE_PASSWORD_LOGIN` | `false` | Hides the password form and refuses password sign-in and sign-up. |
| `BUOY_OIDC_SKIP_APP_MFA` | `false` | Counts the provider sign-in as the second step. |
| `BUOY_OIDC_TRUST_UNVERIFIED_EMAIL` | `false` | Accepts an email the provider does not mark as verified. |

They map to `Oidc__Enabled`, `Oidc__Authority`, `Oidc__ClientId`, `Oidc__ClientSecret`,
`Oidc__DisplayName`, `Oidc__AutoRegister`, `Oidc__DisablePasswordLogin`, `Oidc__SkipAppMfa` and
`Oidc__TrustUnverifiedEmail`. `Oidc__RedirectUri` overrides the callback URL if your server sits
behind an address `BUOY_ORIGIN` does not describe.

### Who gets in

- **Existing accounts** sign in when the provider's email matches and the provider says it is
  verified (`email_verified`). The first sign-in links the provider account to the Buoy Money
  account; after that it matches by the provider's user id, so changing your email at the provider
  does not lock you out.
- **New people**, with `BUOY_OIDC_AUTO_REGISTER=false` (the default): only the admin email
  (`BUOY_ADMIN_EMAIL`) or someone who typed an invite code on the sign-up page before pressing
  "Sign up with ...". This is how you create the first account with single sign-on alone.
- **New people**, with `BUOY_OIDC_AUTO_REGISTER=true`: anyone the normal registration rules let in
  (the first account, open registration, or an invite).
- An unverified email is always refused unless you set `BUOY_OIDC_TRUST_UNVERIFIED_EMAIL=true`.

### Two-step sign-in

If an account has the app's own MFA (authenticator app, email code or passkey), single sign-on
still asks for it, exactly like a password sign-in. A provider like Pocket ID already proves who you
are with a passkey, so you can set `BUOY_OIDC_SKIP_APP_MFA=true` to count that as the second step.

### Turning passwords off

`BUOY_OIDC_DISABLE_PASSWORD_LOGIN=true` hides the password form and refuses password sign-in with
"Password sign-in is turned off on this server". It only takes effect while single sign-on is
turned on and configured, so a broken provider setting never locks everyone out.

If the provider is down and you need to get in: set `BUOY_OIDC_DISABLE_PASSWORD_LOGIN=false` (or
`BUOY_OIDC_ENABLED=false`) in `.env` and run `docker compose up -d`. Passwords work again at once.
Accounts created through single sign-on have no password; with email set up, "Forgot password"
gives them one.

### Phone apps

The phone apps open the provider in the browser and come back through the app link
`com.buoyai.buoymoney://oidc`. The provider never sees that link (it only knows the callback URL
above), and the app proves with its own one-time key that the code is meant for it.

### How it works

1. The app opens `/api/auth/oidc/start`. The server remembers a random state, a nonce and a PKCE
   key for 10 minutes and sends the browser to the provider.
2. The provider sends the browser back to `/api/auth/oidc/callback`. The server trades the code at
   the provider, checks the ID token (signature from the provider's published keys, issuer,
   audience, expiry and nonce), and finds or creates the account.
3. The browser goes back to the app with a one-time code (good once, for 60 seconds). Tokens never
   appear in a URL.
4. The app trades that code at `/api/auth/oidc/exchange` for its normal session.

## Email

Set an SMTP server in `selfhost/.env` (any provider, or your own):

```
BUOY_SMTP_HOST=smtp.example.com
BUOY_SMTP_PORT=587
BUOY_SMTP_SECURITY=starttls    # starttls (587), tls (465), or none
BUOY_SMTP_USERNAME=you@example.com
BUOY_SMTP_PASSWORD=app-password
BUOY_SMTP_FROM=you@example.com
```

Mail always goes out from `BUOY_SMTP_FROM` (most servers refuse a sender they do not host), with
the display name `Smtp__FromName` (default "Buoy Money") and an optional `Smtp__ReplyTo`.

With no `BUOY_SMTP_HOST`, nothing is sent (the log says "Email not sent") and new accounts are marked
as verified, because a verification email could never arrive. Once you add a mail server, new
accounts verify by email as on hosted.

## Phone notifications

A home server cannot talk to Google or Apple push by itself. Instead it uses the free BuoyAI push
relay: your server encrypts each notification so that only your phone can read it, hands the
scrambled message to the relay, and the relay wakes your phone. The relay never sees your numbers.
The exact format is in [push_relay.md](push_relay.md).

To use it, set `BUOY_PUSH_RELAY_URL` and sign in on the phone app: the app pairs with the relay and
registers itself with your server. With no relay address, in-app notifications still work and
nothing is pushed.

The relay service itself ships separately; until then the setting stays empty.

Not built yet: ntfy and UnifiedPush for phones without Google services.
