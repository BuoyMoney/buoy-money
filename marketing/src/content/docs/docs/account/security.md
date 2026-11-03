---
title: Two-factor and security
description: Signing in, two-factor methods, recovery codes, trusted devices, and fingerprint or face unlock.
sidebar:
  order: 2
---

## Signing in

- **Email and password**, with a strength check on the password.
- **Continue with Google**.
- **Continue with Apple** on Apple devices.

After signing up, Buoy emails a code to verify your address. Linking a bank needs a verified email.

## Two-factor

Open **Profile → Security Settings → Add Method**. You can add:

- **Authenticator App (TOTP)**, such as Google Authenticator or 1Password.
- **Passkey (WebAuthn)**, using your phone or computer's fingerprint, face or PIN.
- **Email Code**.

A passkey or authenticator app is required before [connecting a bank](/docs/banks/connect-a-bank). An email code works for linking only for its first 30 days.

Buoy asks for your second factor when you sign in on a new device or when a sign-in looks unusual. Tick **Remember this device** to skip it on that device for 30 days. If you have more than one method, **Use different method** switches between them.

## Recovery codes

When you set up an authenticator app, Buoy shows eight one-time recovery codes once. Tap **Copy All Codes** and keep them somewhere safe. If you lose your phone, type one into the code field at sign-in.

## Trusted devices

**Profile → Trusted Devices** lists devices that skip the second step. Revoke one, or **Revoke All**.

## Fingerprint or face unlock (Android)

**Settings → Security** has two options when your phone supports it:

- **Unlock**: asks for your fingerprint or face when you open Buoy, and when you come back after more than two minutes away.
- **Quick login**: sign in with your fingerprint or face instead of your password (email-and-password accounts).

How Buoy keeps bank data safe: [Security & privacy](/security) and [Is it safe to link your bank?](/blog/is-it-safe-to-link-your-bank).
