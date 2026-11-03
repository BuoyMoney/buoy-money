---
title: If BuoyAI LLC stops
description: What happens to your self-hosted Buoy and your Cloud subscription if BuoyAI LLC shuts down or drops a product.
draft: true
sidebar:
  order: 5
---


People who use money apps have watched companies shut down or change direction. This page says what I promise if that happens to me.

## Your self-hosted Buoy keeps running

The app runs on your machine, and its code is open source. If BuoyAI LLC disappeared tomorrow, your server would keep working with the data it has. Nothing in the self-hosted app needs to reach BuoyAI Cloud to start, sign you in, or show your budgets.

## Import stays free

Importing files and syncing banks through SimpleFIN are part of the free app, and they stay free. I will not move them behind a paid plan.

## If I end a Cloud product or shut down

- I tell every paying customer by email before a Cloud product stops.
- Cloud plans are not refunded in part, including yearly plans. A plan keeps working until the product stops.
- When bank sync stops, BuoyAI Cloud removes your bank connections from Plaid and deletes the stored bank tokens. Transactions already delivered stay on your server.
- AI stops at the same time. The app keeps working without them, and you can switch AI to your own provider or a local model in Settings.

## Your data

Everything on your server is in your own database. You can export it at any time from **Settings**, and back it up with standard Postgres tools.
