---
title: "How to find the subscriptions you forgot you were paying for"
description: "Free trials that quietly converted, a gym you moved away from, two cloud storage plans. A short, honest method for finding recurring charges in your own transactions, and how Buoy spots them for you."
pubDate: 2026-09-20
author: "Jonathan Barnell"
---

Almost everyone has one. A streaming service you signed up for to watch a single season.
A meditation app from a New Year's resolution. The gym near the apartment you no longer
live in. Each one is small enough to slide past you on a statement, and together they are
a line item you never agreed to.

The awkward part is that finding them takes effort, which is why they survive. So here is
the method, first by hand and then the way Buoy does it, so you can pick whichever you
will do.

## The manual audit (thirty minutes, once)

Open your last three months of statements side by side. Three is the minimum, because
some subscriptions bill quarterly and a single month will hide them.

Go down the list and mark anything that appears more than once at a similar amount. Do
not judge yet, just mark. Then, for each mark, ask two questions: when did I last use this,
and would I sign up for it today at this price?

Anything that fails both questions gets cancelled. Anything that fails one goes on a watch
list with a date to check again. The usual finds are a converted free trial, a duplicate
(two cloud storage plans, two music services), and something a family member set up on a
shared card.

Do this once and you will catch most of it. The problem is doing it again in six months.

## What a budgeting app can see that you cannot

Your bank sees every one of these charges, and the pattern is not subtle: same merchant,
similar amount, regular interval. That is a pattern a computer is good at and a tired
human is not.

Buoy reads your synced transactions and looks for exactly that. When it finds a merchant
charging you on a repeating cycle, it proposes it as a subscription. You confirm or
dismiss, and from then on it lives in a subscriptions list with the amount, the cycle, and
the next expected date.

Two things make this useful rather than just tidy. The first is the calendar: you can see
what is due in the next week or month, which is the view that stops a renewal from being
a surprise. The second is the stale flag: a subscription you confirmed that stops charging
gets marked as stale instead of sitting in the list forever, so the list stays honest
without you pruning it.

## How the detection works

For anyone who wants to know what is happening rather than trust it, this is the shape of
it.

Every synced transaction carries a merchant name, an amount, and a date. Buoy groups
transactions by merchant, clusters the ones whose amounts sit within a few dollars of the
group's typical charge, and looks at the gaps between them. A cluster that repeats at a
weekly, biweekly, monthly, quarterly, semi-annual or annual spacing becomes a candidate.
The weekly background scan wants three charges before it proposes anything; the one
exception is two charges close to a year apart, which is how an annual plan looks inside
the two-year window it scans. Pressing Detect yourself is looser and will surface a
pattern from two.

The amount tolerance matters because subscriptions drift: tax changes, a price increase, a
currency conversion. The interval tolerance matters because billing dates slide around
weekends and month ends. Get either too strict and you miss real subscriptions; get them
too loose and your coffee shop looks like one. The thresholds took a while to settle, and
a proposal you never answer quietly expires after ninety days rather than nagging.

Nothing here leaves your account. The detection runs on your own transactions inside
Buoy, and the results are yours to confirm or throw away. The app does not sell that data,
and it has no ads to sell it to.

## Cancelling is still on you

This is the honest limit. Buoy can tell you what is recurring and when it will charge you
next. It cannot cancel anything, because that means logging in to each service, and I am
not willing to hold your passwords to do it.

What it can do is make the list short, dated, and in front of you, which in practice is
what was missing. Cancelling is rarely the hard part. Finding the subscription is, and that is the part
that now takes ten minutes.

## A reasonable rhythm

Once a quarter, open the subscriptions list and ask the two questions again. Ten minutes,
four times a year. If you do nothing else with a budgeting app, that habit alone earns its
keep, and the detection means the list is already waiting when you get there.

The trial needs no card, and the bank connection is read-only.

[Start your free trial →](/app/register)
