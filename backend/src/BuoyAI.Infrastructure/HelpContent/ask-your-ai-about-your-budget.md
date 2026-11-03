---
title: "Ask ChatGPT or Claude how your budget is going"
description: "Buoy now connects to the AI assistant you already use. Link your account once, then ask about your spending, budgets, and goals right inside ChatGPT, Claude, or Gemini."
pubDate: 2026-07-17
author: "Jonathan Barnell"
---

You already talk to an AI assistant every day. Now it can talk about your money, using
*your* real budget, in your own words, without you copying numbers into a chat box.

**Buoy Money Connect** links your Buoy account to ChatGPT, Claude, or Gemini. Once it's
connected, you can just ask:

> *"How's my Buoy Money budget going this month?"*
> *"Which categories am I closest to overspending?"*
> *"How much have I put toward my emergency fund?"*

The assistant reads your actual accounts, budgets, and savings goals to answer. Then it
explains things the Buoy way, because it understands how count-up budgeting works, not just
how to read a number.

## Read-only, by design

This is the part that matters for a finance app, so we'll be blunt about it.

The connection is **read-only**. A connected assistant can *look* at your data to answer
questions. It cannot move money, make payments, or change your budgets. Your bank login is
never shared either. Buoy connects to your accounts through Plaid, and that never touches
the assistant.

And nothing is shared until you say so. When you add the connector, Buoy shows you a
consent screen naming the exact assistant and what it's asking for. Data only flows after
you approve. To disconnect, remove the connector in that assistant's settings. Buoy does
not have its own disconnect button yet; that is on the list.

## How to connect it

Buoy Money Connect is a **Premium** feature, and setup takes about a minute.

On **Claude** (any plan): open Settings, then Connectors, then "Add custom connector,"
paste `https://buoy-ai.com/mcp`, and follow the prompt to sign in to Buoy and approve.

On **ChatGPT** (paid plans, on the web): turn on Developer mode under Settings, then Apps,
add a connector with the same URL, and approve.

On **Gemini**: Google is rolling out custom connections in the Gemini app for eligible
accounts. Add the same URL under Connected Apps when it's available to you.

That's it. Sign in once, approve the consent screen, and start asking.

## Why we built it this way

The big AI companies are racing to put finance features *inside* their own apps. We think
that's backwards. You shouldn't have to move to a new assistant to get help with your
money. Your money should meet you in the assistant you already trust.

So instead of locking your data inside Buoy, we made it something your favorite AI can
securely read, on your terms, and hand back to you with a link to learn more. Same count-up
philosophy, same encouraging voice you get in the Buoy app, now wherever you already think
out loud.

Connect it once, and next time you're chatting with your AI, just ask how you're doing.
