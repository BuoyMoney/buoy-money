// Curated from the early-stage-positioning fleet (sourced). Edits: removed an
// unverified infra-cert claim, softened specific founding perks (founder decision),
// framed roadmap/changelog as being put in place.
export const STORY = {
  "slug": "our-story",
  "title": "My Story · Building Buoy in the open · BuoyAI",
  "metaDescription": "Buoy is early, and on purpose. Read why I'm building a count-up budgeting app in the open, how founding members shape what I build next, and why the security of your bank data was architected from day one, not left for later.",
  "heroKicker": "OUR STORY",
  "heroTitle": "I'm building Buoy in the open. The way your money is handled isn't up for debate.",
  "answerFirst": "Buoy is an early-stage personal-finance app built around count-up budgeting: your categories earn a daily allowance that grows when you underspend. It's newly live on Google Play with iOS on the way, I'm building in public, and founding members help shape the features and the roadmap. What I did NOT leave for later is security. Your bank connects through Plaid, read-only, so Buoy can never move your money and never sees your bank login.",
  "bodyMarkdown": `## Why I'm telling you it's early

Most startups wait until everything looks finished before they say a word. I'd rather do the opposite. Buoy is early. Newly launched, still taking shape. I think the honest version of that is also the more interesting one. When you join now, you're not a late arrival to a polished machine. You're early enough to change what gets built.

That's a deliberate choice, and it cuts both ways. There are two kinds of "early," and I want to be precise about which one I mean.

## Two lanes, and I never mix them up

**The product is early. The safety of your data is not.**

The *product* is genuinely open: the count-up mechanics, the badges, the categories, the AI advisor's tone, what I build next. I don't have all the answers, and I don't pretend to. I'd rather build the next features *with* the people who'll use them than guess in a room by myself. That's the part I'm inviting you into.

The way your financial data is handled is a different lane entirely. That was architected from the first line of code. It isn't a work in progress, an "I'll harden it later," or anything I'd ever put to a vote. A budgeting app is only worth using if the money side is boringly, unshakably safe. So here's exactly how it works, in plain English.

## How Buoy connects to your bank

When you link an account, you do it through **Plaid**, the same connection layer behind apps like Venmo, Robinhood, and Coinbase, used by thousands of banks and finance apps. Three things follow from that, and all three are true by design:

- **Read-only.** Buoy can see balances and transactions so it can build your budget. It cannot move money, make a payment, or change anything in your accounts. There is no pathway for it to do so. That's not a policy, it's the architecture.
- **I never see your bank login.** You type your credentials into Plaid's own screen, which hands them to your bank. They never touch Buoy. I receive an encrypted access token, not your username and password.
- **Encrypted in transit and at rest.** Your data is encrypted on the way to my servers and while I store it. You can disconnect any institution at any time, and that cuts off access immediately.

And one more, because it shapes everything else: **I make money from subscriptions, not from your data.** You are the customer, not the product. I don't sell or share your financial information. The [Privacy Policy](/privacy-policy) spells out the specifics, and my marketing will never say anything the policy doesn't back up.

## What "building in the open" actually means here

Concretely, here's what I'm putting in place, not just a vibe:

- A **public roadmap** where founding members can see what's coming and vote on what matters most: the next badges, new category types, count-up tweaks.
- A **"You asked, I shipped" changelog** that closes the loop. When something you suggested goes live, you hear about it, and I say who asked for it.
- **Founding-member** access for early users: early access to new features and a real say in the direction. It's about access and influence first; I'm shaping the rest with you.

What it does *not* mean: I will never publish vanity numbers I don't have, never imply a feature exists before it does, and never ask the internet to help me "figure out" how to protect your data. Build-in-public, for me, is about product decisions, never about safety.

## Come build it with me

If count-up budgeting sounds like the thing you wished existed, and being early sounds like a feature rather than a risk, this is the moment to get in. Tell me the budgeting problem you're actually trying to solve when you sign up. That's not a marketing question. It's the first thing that'll shape what I build.

I'm early. I'm not careless. Come help me shape what's next.

Questions, feedback, or a security question? Email [contact@buoy-ai.com](mailto:contact@buoy-ai.com).`
} as const;

export const FOUNDING_BAND = {
  "label": "Founding members",
  "headline": "Buoy is early, and that's the invitation.",
  "sub": "Join as a founding member and help shape what I build next: the badges, the categories, the count-up mechanics. Your bank connects read-only through Plaid, so Buoy can never move your money and never sees your login. Early on features. Serious on safety.",
  "ctaText": "Become a founding member"
} as const;

export const TRUST_SIGNALS = [
  "Read-only by design: Buoy connects through Plaid and can only read balances and transactions. It can never move, withdraw, or touch your money.",
  "I never see your bank login. You enter it on Plaid's own screen; Buoy only ever receives an encrypted access token.",
  "Encrypted in transit and at rest. Disconnect any bank at any time and access is cut off immediately.",
  "Powered by Plaid, the same secure connection layer behind Venmo, Robinhood, and Coinbase.",
  "You're the customer, not the product. I earn from subscriptions and never sell or share your financial data.",
  "My security wasn't left for later. It was architected from day one, and it's the one thing I'd never put up for a vote."
] as const;
