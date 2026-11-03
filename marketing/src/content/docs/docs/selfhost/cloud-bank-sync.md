---
title: Bank sync through BuoyAI Cloud
description: How the paid Cloud bank feed connects your banks to your own server, and what BuoyAI Cloud can and cannot see.
draft: true
sidebar:
  order: 3
---

Bank sync is a paid BuoyAI Cloud product for self-hosted servers. BuoyAI Cloud holds the bank connection (through Plaid), and your server receives your transactions encrypted to a key only your server has.

## How it works

1. Your server links to your BuoyAI Cloud account. On your server, open **Settings**, **BuoyAI Cloud** and press **Link with BuoyAI Cloud**. Your server shows a short code and a QR code. On any phone or computer, go to cloud.buoymoney.com/link (or scan the QR code), sign in or make an account, check the server's name, and approve the code with your second factor. Your server notices by itself and shows **Linked**. This works on any server, even one you open over plain http. The code works for 10 minutes. Only approve a code shown on your own server. You can still pair with a one-time pairing code instead; a server paired that way waits for you to confirm it on buoy-ai.com before any bank data is sent to it.
2. To add a bank, Buoy opens a BuoyAI Cloud page that says where the data will go. You agree, then sign in to your bank on Plaid's screen. Banks that use their own sign-in page need this hosted step.
3. BuoyAI Cloud fetches new transactions from Plaid, encrypts each batch to your server's key, and hands it over. Your server stores the batch, then confirms it. Only after that does BuoyAI Cloud move on to the next batch.
4. When a bank needs you to sign in again, you get a notification and an email. Reconnect it from **Accounts** on your server.

## What BuoyAI Cloud keeps

- The Plaid token for each bank you connect, encrypted, so it can keep fetching.
- Where each bank's feed left off, a log of calls made to Plaid, and an audit log of events such as "bank connected". None of these hold transaction details.
- Each batch of transactions passes through BuoyAI Cloud's memory for a moment while it is encrypted. BuoyAI Cloud stores no transactions.

Once a batch reaches your server, your server is responsible for it.

## Limits

- Bank sync includes 3 bank logins. You can buy more, $0.75 a month each.
- Instant refresh, a separate add-on that would let you ask for fresh data on demand, is coming later and is not for sale yet.
- If you revoke a server or your plan ends, BuoyAI Cloud waits 14 days, then removes your banks from Plaid.

## Free alternative

[SimpleFIN](/docs/selfhost/simplefin) works with no BuoyAI Cloud subscription at all.
