# Receipt scanning

A Walmart or Amazon charge arrives from the bank as one number. The user sends BuoyAI the receipt
(a screenshot of the order page in the store's app, a photo of a paper receipt, or a PDF); BuoyAI
reads the items, finds the bank charge it paid for and offers to split it by category. Premium only,
and it needs the user's AI consent because Gemini reads the image.

## Why not email

The first version read forwarded order emails. It was dropped before release: Amazon removed item
names, quantities and prices from its order emails on 2026-07-08, and Walmart's order emails list
only the first few items with no prices. Every email now just links to the store's order page. The
order page itself still shows everything, so a screenshot of it carries what the email no longer does.

## How a receipt travels

1. **Getting it in.** Three ways, all ending in the same sheet (`ReceiptScanSheet`):
   - **Share → BuoyAI** (Android): the manifest's `SEND`/`SEND_MULTIPLE` intent filters put BuoyAI in
     the share sheet for images and PDFs. `ReceiptShareListener` (attached by the signed-in shell)
     receives the files and opens the sheet. iOS needs a share extension target (not built yet).
   - **Scan a receipt** on the Receipts page (Settings > Receipts): take a photo or pick up to 4
     screenshots (`pickAndScanReceipt`).
   - **Scan receipt** on a store charge's transaction sheet: the same, plus the transaction id as a
     hint so that charge is matched first.
2. **Consent in the sheet.** The sheet shows the files and says Google Gemini reads them; nothing is
   sent until the user taps Send. A share is never an upload by itself.
3. **Reading.** `POST /api/receipts` (multipart `files`, optional `transactionId`; AI rate limit,
   40 a day per user) reads the files while the request waits. Types come from the bytes, not the
   label (JPEG, PNG, WebP, HEIC, PDF; up to 8 files, 8 MB each, 16 MB in all). Gemini returns the
   store, order number and date, kind, items with line totals and a category from the user's list,
   tax, shipping, discount, total and any separately listed charges (`ReceiptParser`). A screenshot
   showing only part of a long order is refused unless the items shown add up to the total. The image
   is never stored; only the parsed JSON and the offered split are (`ReceiptScans`).
4. **One share is one receipt.** A long order takes several screenshots, shared together; each order
   is shared separately. Gemini pieces the screenshots together (they may overlap, and an item on two
   screenshots is counted once), and flags files that show different orders, which are sent back.
   The server then checks the numbers: items plus tax, shipping and fees less discounts must come
   within 2% (or a dollar) of the total, otherwise part of the order is missing and the receipt is
   sent back with both figures. Splitting by only the items shown would put the missing items'
   money in the wrong categories.
5. **Same order twice.** A newer receipt for the same order number replaces an older one that wasn't
   applied. If the order was already split, or a newer receipt for it was already read, the new one
   is dropped.
6. **Matching** (`ReceiptMatcher`, pure, unit tested). With a transaction hint, that charge wins when
   its amount fits (exact, or within 15%), whatever the bank calls the merchant. Otherwise candidates
   are the user's and their family's posted expense charges from the same store (known stores by
   bank merchant patterns in `RetailerDirectory`, any other by the name on the receipt), not already
   split, dated from 3 days before the order to 10 days after, and not claimed by another receipt.
   In order: an exact amount; each listed card charge matching a different charge; two or three
   charges that add up to the total (Amazon ships in parts); the only charge within 15% (Walmart
   substitutions). Two equally good answers are no answer. Pending holds are never matched.
7. **The answer, right away.** The response carries the status: `ready` with the split, `unmatched`
   (the charge hasn't synced yet), or `not_a_receipt` with a reason. Ready splits show in the sheet
   with Apply.
8. **Waiting.** `ReceiptRematchService` (job-locked) retries unmatched receipts every 15 minutes as
   charges sync, notifies (`receipt_split_ready`, Transaction review push preference) when one
   matches, and expires them after `Receipts:WaitDays` (14).
9. **The split** (`ReceiptSplitPlanner`). Items are grouped by category and the charge is shared out by
   each group's share of the item prices, so tax, shipping, discounts and a changed final amount are
   spread the same way, rounded so the parts add up to the cent. Each part's note lists its items.
   A one-category receipt recategorizes the charge and adds the note.
10. **Apply.** Every charge is checked (still there, editable, not already split) before any is
   changed. All the splits and the scan's Applied status then commit in one database transaction, so a
   failure or a crash part way leaves nothing split and a retry starts clean. Splitting goes through
   `TransactionService.SplitTransactionAsync`, so a read-only family member can't split a shared charge.

## AI chat

The chat has a `split_transaction` tool (preview, then confirm, like the other write tools).

## Configuration

| Key | Meaning |
|---|---|
| `Receipts__WaitDays` | Days a receipt waits for its charge (default 14). |
| `Receipts__RequirePremium` | Default true. |
| `Receipts__MaxPerDay` | Receipts per user per day (default 40). |
