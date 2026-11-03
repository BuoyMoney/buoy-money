---
title: Connect a bank
description: Link a US bank through Plaid, why two-factor comes first, and what happens after you link.
sidebar:
  order: 1
---

Buoy reads your transactions through **Plaid**, the service most US finance apps use. The connection is read-only: Buoy can see balances and transactions but can't move money, and your bank password goes to Plaid, never to Buoy.

**Who can do this:** anyone on a plan or in the free trial. In a family, the Owner or a Budget Admin.

**Where:** US banks. Canadian bank sync is on the roadmap; in Canada you can add transactions by hand for now.

## Before you start: two-factor

Banks require two-factor security before an app can link an account, so Buoy asks for it first. If you haven't set it up, you'll see "One quick step first" with three options:

- **Use a passkey** (shown if your device supports passkeys)
- **Use an authenticator app**
- **Use an email code**, marked least secure

After you set one up, Plaid opens straight away. An email code lets you link banks for 30 days; after that Buoy asks you to add a passkey or authenticator app before linking another bank. Banks you've already linked keep syncing either way.

You also need a verified email. See [Two-factor and security](/docs/account/security).

## Link the bank

1. Open the **Accounts** tab.
2. Tap **Connect Bank** (or **Connect Another Bank** at the bottom if you already have one).
3. Tap **Connect Bank** in the sheet and follow Plaid: find your bank, sign in, pick the accounts to share.

You'll see "Connected to (your bank)! Quick sync started."

## What happens next

- Your **last 30 days** of transactions arrive right away.
- Up to **two years** of history import in the background. The bank card shows "Importing transactions…" until it's done.
- New transactions arrive on their own from then on. See [Syncing and managing connections](/docs/banks/manage-connections).

Linking the same bank twice is refused; use **Update Connection** on the existing one instead.
