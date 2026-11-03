---
title: "How to let Claude or ChatGPT see your money, safely"
description: "A new category of tool lets AI assistants read your real finances through something called MCP. Here's what it is, who's building it, and the one question that decides whether it's safe to use."
pubDate: 2026-07-20
updatedDate: "2026-08-08"
author: "Jonathan Barnell"
---

There's a quiet shift happening in personal finance, and most people haven't noticed it yet.

For years, if you wanted an AI assistant to help with your money, you had to become a
copy-paste machine. Screenshot your budget. Paste last month's transactions. Type out your
account balances and hope you got them right. The assistant could only reason about the
numbers you fed it by hand, which meant it never really knew your money. It knew a snapshot
you assembled at 11pm.

That's ending. A standard called MCP, the Model Context Protocol, now lets assistants like
Claude, ChatGPT, and Gemini read your real financial data directly: live balances, real
transactions, actual budgets, no copy-paste. The data always existed. What was missing was a
safe wire between it and the assistant.

Several companies are building that wire right now, and they are not all the same. This is a
plain-English guide to the category, who's in it, and the single question that matters most
before you connect anything.

## What a "financial MCP" actually is

Think of MCP as a standard plug, like USB-C for AI. A financial MCP server is a small,
permission-gated service that sits between your accounts and your assistant. You connect it
once. After that, when you ask your assistant a money question, it can pull the real answer
through that plug instead of guessing from whatever you last pasted in.

Ask "how much did I spend on groceries this month?" and it reads the actual number. Ask
"am I on track for my savings goal?" and it checks the real balance. The assistant does the
talking. The MCP does the fetching.

The important part is what happens to your bank login in all of this: with a well-built one,
it doesn't. Your credentials go to a regulated bank-connection provider (the same kind of
plumbing your existing budgeting app already uses), never to the assistant and never into a
chat window.

## The one question that decides everything: can it move your money, or only see it?

This is the whole ballgame, and it's the difference the category's marketing tends to blur.
There are two kinds of financial MCP.

The first kind can act. The assistant can initiate transfers, set up automated rules, and
move money between your accounts, usually with an approval step in front of each action. More
powerful, and genuinely useful for some people. But you are now trusting an AI agent, and the
connection behind it, with the ability to move your money.

The second kind cannot touch a dollar. The assistant can see your money and talk about it. It
can even remember things you tell it, like a savings goal or that you hate subscriptions. What
it can never do is send. If that connection were somehow abused, the worst case is that
someone learns what you spent on coffee. Nothing can leave your accounts.

Neither is wrong. But they are not the same product wearing different logos, and you should
know which one you're turning on. A budgeting assistant that can see and remember is a
fundamentally smaller risk than an agent that can transfer.

We built the second kind on purpose. Buoy Money Connect can read your money and remember what
matters to you, and it is structurally incapable of moving a cent. The "remember" part is not
a loophole in that promise. It writes notes, not transfers. Think of it as an extension of the
protections you already get inside the Buoy Money app, where moving money always sits behind
multi-factor authentication. Your agent gets to see your money the way you see it in Buoy Money.
It does not get the keys to spend it.

## Who's building this right now

The category is young, so the lineup will change, but here's an honest map as of mid-2026.
Reasonable people are building genuinely different things.

- **Era** takes the ambitious, agentic path: a connection built so an assistant can proactively
  manage and even move your money, with your approval. They're a venture-backed, SEC-registered
  adviser aiming to act on your behalf. If you want an AI that does more than advise, that's the
  bet they're making.
- **Truthifi** is read-only and pointed at investing: portfolio performance, fee analysis, and
  benchmark comparisons across brokerage accounts, for people who mostly want to understand how
  their holdings are actually doing.
- **Monarch Money** has an MCP too, but it's a community-built, self-hosted open-source project
  rather than an official product. Powerful if you're technical and happy to configure and host
  it yourself, less so if you just want to turn something on.
- **Muntze** is crypto-only: on-chain metrics and backtesting for active crypto investors, not
  traditional bank or retirement accounts.
- **Buoy Money Connect** is our own: it can see and remember, never move money, and it's built around
  everyday money, your budgets, spending, cash flow, and savings goals. When your assistant
  answers, it explains things the Buoy way, because it understands how count-up budgeting works,
  not just how to read a balance.

We're listing the others on purpose. This is a real category now, and pretending you have no
choices would insult you. The right pick depends on what you actually want the assistant to do.

## A short checklist before you connect anything

Whatever you choose, us or not, run it through this.

1. **Can it move money, or only see it?** Decide on purpose. If you only want answers, there's no
   reason to grant the power to transfer.
2. **Where does your bank login go?** It should go to a regulated aggregator, never to the AI
   assistant and never into a chat. If that's unclear, that's your answer.
3. **Official or community?** A self-hosted open-source connector can be great, but "you host and
   secure it" is a real job. Know which you're signing up for.
4. **What does it actually see?** "Your finances" is vague. Budgets and balances are one thing.
   Full transaction history and the ability to transfer is another.
5. **How do they make money?** If the answer isn't "subscriptions," ask what's being done with
   your data. Ours is subscriptions. Full stop.

## Where we land: bring your own agent, keep our view of your money

Most of this category is chasing "agent-agnostic," a raw pipe that hands any assistant your
data and steps back. We think that misses something. Raw numbers with no point of view are how
people end up anxious and stuck. Your assistant seeing that you have $128 left in dining is
useful. Your assistant understanding that in Buoy Money, that number counts up as a daily
allowance you're winning, not down toward zero, is the part that actually changes how you feel
about money.

So Buoy Money Connect is not fully agent-agnostic, and that's the point. You bring whatever
assistant you already use, Claude, ChatGPT, Gemini, newer models like Kimi K3, or any agent
that speaks MCP, and it plugs into your finances through Buoy Money's view of the world instead of
a blank one. You get your choice of AI and our framing of
your money at the same time. A view with a philosophy behind it beats no framing at all, which
makes this a rare win on both sides: your agent, our lens, your money staying exactly where you
left it.

You can ask Buoy anything about your money through the assistant you already use. It just can't
spend it.

*Buoy Money Connect is available on Premium. It works with Claude, ChatGPT, and Gemini, and any AI
agent that supports MCP, including newer ones like Kimi K3, so you're never locked to one
assistant. It never sees your bank login. If you want your AI to understand your budget instead
of guessing at it, [start a free trial](/app/register).*
