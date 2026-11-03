---
title: Import your data
description: Bring in transactions from a bank file, Actual Budget or YNAB, check them before anything is saved, and undo an import.
sidebar:
  order: 6
---

You can bring in history from another app, or from a file your bank gave you. Open **Settings → Privacy & Data → Import data**. Nothing is saved until you have seen what will be added and pressed the import button.

## What you can import

- **Bank files:** CSV, OFX or QFX. Most banks offer one of these on the account page ("Download transactions"). A spreadsheet saved as CSV works too.
- **Actual Budget:** the zip file from Actual's **Settings → File → Export data**.
- **YNAB:** the files from **Export Plan**. Choose the Register file and the Plan file together, or pick the zip as it is. The Register file holds your transactions. The Plan file holds your monthly budget amounts and is optional.

Files can be up to 25 MB. A bank file on its own is limited to 5 MB.

## How it works

1. **Choose a file.** Buoy reads it and shows a summary. Your file is read by your Buoy server and is not kept after you finish.
2. **Check the preview.** You see how many transactions are new, how many are already in Buoy, and any that look like duplicates. You also see the date range and the total money in and out.
3. **Press Import.** The result page tells you what was added.

## Where the transactions go

A bank file holds one account. Pick one of your existing accounts, or let Buoy create a new one from the file.

Actual and YNAB files hold several accounts. Each one gets a row. For every account you can add to one you already have, create a new one, or skip it. Buoy matches by name when it can.

An account Buoy creates for you this way is not connected to a bank, so it does not sync. For Actual and YNAB imports, Buoy works out its balance from the imported transactions, including the starting balance. For a bank file, the balance is left alone.

## Duplicates

Buoy checks every row against what you already have, so importing twice does not double anything.

- **Already here:** the file's own transaction id is already stored for that account. These are skipped.
- **Likely duplicates:** the same amount, a date within a few days, and a similar name as something you already have, such as a transaction from a connected bank or one you added by hand. These are skipped unless you turn them on. You can turn on single rows or all of them.
- **Possible duplicates:** the same amount and about the same date, but a different name. Banks often write the same purchase two ways. These are imported unless you turn them off.

Two real purchases on the same day for the same amount, such as two coffees, are both kept.

## Dates and amounts

If every date in a file could be read two ways, for example 03/04/2026, Buoy asks whether the file is day first or month first, and does not let you import until you choose. Amounts like 1.234,56 (with a decimal comma) are read correctly too. If a bank CSV puts charges as positive numbers, turn on **Flip signs**.

For a CSV, open **How this file is read** to pick which column is the date, the amount (or money in and money out), the description, the memo and the category.

## Categories

If your file has categories, you match each one to a Buoy category. Buoy suggests a match by name. You can create a new category, pick a different one, or leave it uncategorized. Transactions you already categorized in Actual or YNAB are filed as you chose and do not land in your review list. Everything else goes to your review list as usual, and your rules and AI categorization apply to it.

Transfers between two accounts in the same file are filed under Transfer, so they do not count as spending.

## Budgets

If the file has budget amounts (Actual, or YNAB with the Plan file), you can create a monthly Buoy budget from the most recent month in the file. This only happens if you do not already have an active budget. Your existing budget is never replaced. Goals, targets and count-up settings do not come across.

## Undo

After an import you can press **Undo this import** for 24 hours. It removes the transactions that import added, and any accounts it created that are now empty. Edits you made to those transactions are lost. Categories the import created stay.

## What does not come across

- Investment buy and sell activity in OFX files.
- Split transactions are imported as separate rows, each with its own category.
- Pending status, cleared flags, YNAB flags and category notes.
