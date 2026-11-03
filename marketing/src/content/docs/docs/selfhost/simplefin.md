---
title: Connect banks with SimpleFIN
description: Bring bank transactions into a self-hosted Buoy server with a SimpleFIN setup token.
draft: true
sidebar:
  order: 2
---

SimpleFIN is a separate service that reads your bank and hands the transactions to your server. You pay SimpleFIN directly (about $15 a year). Buoy pays nothing for it and takes no cut.

## Connect

1. In Buoy, open **Settings**, then **Bank feed (SimpleFIN)**.
2. Tap **Open SimpleFIN** and make a setup token there. SimpleFIN asks you to sign in to your banks on its own site.
3. Paste the token into Buoy and tap **Connect**.

A setup token works once. If connecting fails, make a new token.

The first sync brings in about the last 90 days. After that, Buoy checks each connection a few times a day. SimpleFIN's own data usually changes about once a day, so new transactions can take a day to show up.

## When a connection needs you

Buoy shows **Needs attention** on the connection, and sends a notification and an email. Most of the time the fix is to sign in again on SimpleFIN's site. If SimpleFIN lost the connection completely, make a new setup token and paste it.

## Disconnect

Tap **Disconnect** on the connection. Buoy stops syncing and keeps the transactions it already has. To cut off access completely, also remove the app in your SimpleFIN account.
