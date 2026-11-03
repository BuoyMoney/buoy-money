---
title: Syncing and managing connections
description: How often Buoy syncs, refreshing by hand, fixing a connection that needs a new login, and disconnecting a bank.
sidebar:
  order: 2
---

## How syncing works

Buoy checks for new transactions every few minutes and also gets a signal from Plaid when your bank has new activity. You don't need to do anything.

Banks themselves only report new activity a few times a day, so a purchase can take hours to appear. That delay is on the bank's side. Pending charges show with a **Pending** badge and are replaced by the final charge when it posts.

## Refresh by hand

- On a phone, pull down on the Accounts page.
- On a wide screen, tap **Sync**.
- Or open a bank's **⋮** menu and choose **Sync Now**.

Each of these checks every linked bank. If nothing new has arrived you'll see "No new transactions yet", which means the bank hasn't reported anything since the last check.

The "Auto-refresh" label on the Accounts page only controls how often the screen reloads what Buoy already has.

## When a bank needs a new login

Banks sometimes ask you to sign in again, for example after a password change. Buoy sends a notification that opens **Update Connection**, and a banner at the top of the Accounts page names the bank; tap it to update the connection. You can also do it any time from the bank's **⋮** menu → **Update Connection**. Plaid opens, you sign in, and syncing resumes with nothing lost.

## Disconnect a bank

Open the bank's **⋮** menu → **Disconnect** and confirm. Buoy tells Plaid to revoke its access and stops syncing that bank. If you reconnect later, its history is imported again.

In a family, only the Owner or a Budget Admin can disconnect, and Buoy may ask you to confirm with two-factor.

## The Accounts page

At the top: **Total Assets**, **Credit Used**, **Loan Balance** (if you have loans) and **Connections**. Accounts in another currency are listed separately and left out of the totals. Your net worth is on the Dashboard.

Each account shows its available balance. You can give an account a nickname. In a family, the **Feeds** dropdown sets whose budget an account's spending counts toward; see [Family budgets and allocations](/docs/family/family-budgets).
