---
title: How I use AI
description: How AI is used to build Buoy, and how AI features inside the app handle your data.
draft: true
sidebar:
  order: 6
---

## Building Buoy

Much of Buoy's code is written with AI coding agents (Anthropic's Claude) working under the maintainer's direction. The maintainer sets the goals, approves the plan, and reviews what the agents produce before it is released. Automated tests and security scans run on every change.

AI agents have no access to production data or to release keys, and they do not publish releases. The maintainer is responsible for everything released. If an AI-written change causes a bug, it is my bug and I fix it.

## AI inside the app

AI features (chat, automatic categorization, insights, receipt scanning) are off until you turn them on.

- **Self-hosted, your own provider.** Your server sends requests to the provider you chose, with your own key, or to a model running on your machine. BuoyAI LLC never sees them.
- **Self-hosted, BuoyAI Cloud AI.** Your server sends requests through BuoyAI Cloud's AI service, which passes them to the AI provider BuoyAI Cloud uses and counts the cost against your allowance. BuoyAI Cloud keeps a record of each request's feature, model and cost, not the text you sent or the answer.
- **The hosted app at buoy-ai.com.** AI features ask for your consent before they send anything, and you can turn them off in Settings.

AI answers can be wrong. Check anything important, and do not treat AI output as financial advice.
