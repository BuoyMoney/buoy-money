---
title: Automatic categorization
description: How Buoy sorts transactions, reviewing what the AI wasn't sure about, and creating your own rules.
sidebar:
  order: 4
---

Most transactions get a category without you touching them. Buoy decides in this order:

1. **Your rules** always win. See below.
2. **Plaid's category** is used when Plaid is confident and you haven't overruled it for that merchant before.
3. **The AI** sorts the rest, starting from how you've categorized similar transactions. This covers the current and previous month.

While it works you'll see "AI is categorizing N new transactions…".

The AI step needs AI features switched on in **Settings → AI Assistant**. With them off, transactions Plaid wasn't sure about stay uncategorized for you to sort.

## Run it yourself

**Auto-Categorize** on the Transactions page sorts anything still uncategorized. The first time, Buoy asks you to turn on AI features.

## Review

When the AI isn't confident, the transaction waits for you. A card reads "N transactions need review"; tap **Review** to accept, change or skip each one. Skipped items come back after two days, and **Forget them** clears older ones.

## Rules

A rule files every transaction from a merchant under one category, for example "Always file Costco as Groceries".

Rules are created from a prompt that appears after you change a transaction's category. The prompt is off by default: turn on **Settings → Offer to create rules after corrections**, then change a category and accept "Always file (merchant) as (category)?".

To see your rules, open **Settings → Categorization Rules**. Each rule shows the merchant, the category it files to and how often it has been used. **Pause** a rule to let your bank and the AI decide again, or delete it. Past transactions keep their category either way. A rule shared by another family member can only be changed by them.

Categorization is included on every plan.
