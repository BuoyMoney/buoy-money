// Single source of truth for site-wide metadata and the marketing copy ported
// from the Flutter cinematic widgets (lib/widgets/cinematic/*). Keeping the copy
// here means the public, crawlable site and any future edits stay in one place.

export const SITE = {
  name: 'Buoy Money',
  /** Canonical host. Must match backend App:BaseUrl and WebAuthn origin. */
  url: 'https://buoy-ai.com',
  tagline: 'Budget Smarter with AI-Powered Insights',
  description:
    'Buoy is a personal finance app where your budget counts up, not down. Link your bank, watch each category earn a daily allowance, and get instant AI advice on every financial decision.',
  /** Where the Flutter app lives (served under /app/ behind nginx). */
  appLogin: '/app/login',
  appRegister: '/app/register',
  /** Google Play listing (live 2026-08-26). referrer feeds Play install attribution. */
  playStore:
    'https://play.google.com/store/apps/details?id=com.buoyai.buoy_ai&referrer=utm_source%3Dbuoy-ai.com%26utm_medium%3Dweb',
  /** Official Google Play badge artwork, hotlinked per Google's badge guidelines. */
  playBadge: 'https://play.google.com/intl/en_us/badges/static/images/badges/en_badge_web_generic.png',
  /** Public feedback board. */
  feedbackUrl: 'https://feedback.buoy-ai.com',
  ogImage: '/og-image.png',
  /**
   * X/Twitter attribution handle for `twitter:site`. EMPTY because no X account
   * exists — x.com/buoyai, /BuoyAI and /buoy_ai all 404 as of 2026-08-20, and
   * attributing every page to a handle that isn't real is worse than omitting it.
   * The tag is skipped entirely while this is blank; cards still render from
   * twitter:card/title/description/image, which need no account. Register one and
   * put the handle back here AND in SOCIAL.x.
   */
  twitter: '',
  /** Paste the verification tokens from Google Search Console / Bing Webmaster
   *  Tools here to emit the meta tags (leave blank to omit). ChatGPT search uses
   *  Bing's index, so Bing verification matters for AI-search visibility too. */
  googleSiteVerification: '',
  bingSiteVerification: '',
} as const;

/**
 * The email-capture offer shown by the trial popup.
 *
 * These numbers must match the backend, which is the thing that actually grants the
 * trial: `Subscription:TrialPeriodDays` and `Subscription:ExtendedTrialPeriodDays` in
 * `backend/src/BuoyAI.API/appsettings.*.json`. They are duplicated here rather than
 * fetched so the popup renders its headline with no request and no number flicker.
 * `GET /api/leads/offer` returns the live values if you need to check for drift.
 */
export const TRIAL_OFFER = {
  /** What everyone gets by default, in days. */
  standardDays: 14,
  /** What an email address gets, in days. */
  extendedDays: 30,
  /** Human phrasing for the headline ("a month free instead of two weeks"). Keep in sync with extendedDays. */
  extendedLabel: 'a month free',
  /** The delta, phrased for the "are you sure" copy. */
  bonusLabel: 'free month',
} as const;

/**
 * Official profiles for the BuoyAI brand entity.
 *
 * These become `sameAs` on the Organization node, which is how a knowledge graph
 * resolves the string "BuoyAI" to a real entity instead of an unlinked name. A URL
 * that 404s is WORSE than an absent one — it breaks the corroboration it was meant
 * to provide — so blanks are filtered out before emit. Fill one in only once the
 * profile is live at exactly that URL.
 */
export const SOCIAL = {
  github: 'https://github.com/BuoyMoney',
  tiktok: 'https://www.tiktok.com/@buoymoney',
  instagram: 'https://www.instagram.com/buoymoney/',
  youtube: 'https://www.youtube.com/@buoymoney',
  // No X/Twitter profile exists yet: x.com/buoyai, x.com/BuoyAI and x.com/buoy_ai all 404
  // as of 2026-08-20. Note SITE.twitter still emits `twitter:site` for that dead handle.
  x: '',
} as const;

/** Non-empty SOCIAL values, in a stable order, for schema.org `sameAs`. */
export const SOCIAL_PROFILES: string[] = Object.values(SOCIAL).filter(Boolean);

/**
 * Publisher identity. NOTE: this is the PUBLISHER, not the author — every page now
 * attributes authorship to the founder Person node (see Seo.astro). Anonymous team
 * bylines are the weakest E-E-A-T signal in YMYL finance, which is the one category
 * where identifiable authorship is weighted hardest.
 */
export const ORG_AUTHOR = {
  name: 'The Buoy Money Team',
  url: 'https://buoy-ai.com/about',
} as const;

/** Founder identity — surfaced on /our-story (not the landing page) for E-E-A-T. */
export const FOUNDER = {
  name: 'Jonathan Barnell',
  title: 'Founder & engineer',
  photo: '/founder.png',
  linkedin: 'https://www.linkedin.com/in/jonathanbarnell',
  alumniOf: 'Washburn University',
  bio: [
    'Hi, I’m Jonathan, and I’m building Buoy. By day I’m a Senior Software Engineer at a large, publicly traded security company, working on AI agent systems in C#, Kubernetes, and Python. Nights and weekends, I build Buoy solo, full-stack: the architecture, the product, all of it.',
    'My route here was unusual: I started out as a molecular biologist. During COVID-19, as a frontline microbiologist at the Kansas Department of Health & Environment, I taught myself to code and built a data pipeline that saved analysts hundreds of hours a week and helped push Kansas to top-3 in the country for sequencing turnaround. Handling sensitive data carefully isn’t new to me. It’s exactly why Buoy’s security was architected from day one, not bolted on later.',
    'That’s how I work: spot the improvement, build it, bring people along. Buoy is me doing that for personal finance, and I’d genuinely love your help shaping it. (Off the clock: husband, dad of two, happiest outdoors or tinkering with something.)',
  ],
} as const;

export const NAV = [
  { label: 'How it works', href: '/#how-it-works' },
  { label: 'Pricing', href: '/pricing' },
  { label: 'Blog', href: '/blog' },
  { label: 'Help', href: '/docs' },
] as const;

export const HERO = {
  kicker: 'PERSONAL FINANCE, REIMAGINED',
  titleTop: 'Budget Smarter with',
  titleEmphasis: 'AI-Powered Insights.',
  body: 'Stop watching your budget countdown to zero. With Buoy, your spending categories count up daily, making saving feel like winning. Plus, get instant AI advice on every financial decision.',
  primaryCta: 'Start Your Free Trial',
  secondaryCta: 'See How It Works',
} as const;

export const MANIFESTO = {
  kicker: 'THE PHILOSOPHY',
  lead: 'Most budgeting apps focus on restriction and guilt.',
  body: 'Traditional tools make you feel bad when you spend. I engineered Buoy to make you feel good when you save. By shifting the psychology from <span class="text-blue">running out of money</span> to <span class="text-green">earning more daily bandwidth</span>, your financial life stops being a chore and starts being a game you can actually win.',
} as const;

export const PROTOCOL = {
  kicker: 'THE PROTOCOL',
  title: 'How Buoy works.',
  subtitle:
    'Three steps from <span class="text-blue">I have no idea where my money goes</span> to a system that runs itself.',
  steps: [
    {
      title: 'Connect',
      desc: 'Link your bank in under a minute through Plaid, the same read-only encryption your bank already trusts. Transactions stream in automatically, so you never have to type one in.',
    },
    {
      title: 'Understand',
      desc: 'Every charge is categorized the moment it lands. Recurring payments surface as subscriptions, so the ones you’ve forgotten are easy to spot and cancel — and Buoy’s AI advisor is always a question away.',
    },
    {
      title: 'Grow',
      desc: 'Each budget category earns a small allowance every day. Skip a coffee, watch the number grow. Underspending finally feels like winning, and the number you watch grow is money you actually kept.',
    },
  ],
} as const;

export const REWARDS = {
  kicker: 'MOMENTUM',
  title: 'Saving that rewards you',
  subtitle:
    'Count-up budgeting does more than move a number up. It celebrates every good decision, so the habit sticks.',
  pillars: [
    {
      title: 'Build streaks',
      desc: 'Stay under budget and your streak grows. Reach Week Warrior at 7 days, Month Master at 30, and the Century Club at 100.',
    },
    {
      title: 'Earn badges',
      desc: '27 achievements for milestones that actually matter: your first $100 saved, a goal reached, a full month with every category under budget.',
    },
    {
      title: 'Level up',
      desc: 'Every smart decision earns points and moves you up seven levels, from Beginner all the way to Legend.',
    },
  ],
  levels: ['Beginner', 'Saver', 'Budgeter', 'Expert', 'Master', 'Champion', 'Legend'],
  challengeNote:
    'Plus a fresh set of daily challenges, like a No-Spend Day, to keep the momentum going.',
} as const;

export const PRICING = {
  kicker: 'PRICING',
  title: 'Simple plans. No surprises.',
  subtitle: 'Start with a free trial. Pay annually and get two months free.',
  plans: [
    {
      name: 'Standard',
      monthly: 10,
      annual: 100, // 12 × $10 = $120 → save $20/yr
      featured: false,
      features: [
        'Complete count-up budgeting',
        'Subscription tracking',
        'AI budgeting tips',
        'Achievements, streaks & challenges',
      ],
    },
    {
      name: 'Premium',
      monthly: 15,
      annual: 150, // 12 × $15 = $180 → save $30/yr
      featured: true,
      features: [
        'Everything in Standard',
        'AI chat advisor',
        'The latest AI models',
        'Family sharing',
      ],
    },
  ],
} as const;

export const FAQ = {
  kicker: 'QUESTIONS',
  title: 'Good to know',
  items: [
    {
      q: 'Is it safe to connect my bank?',
      a: 'Yes. Buoy links your accounts through Plaid, the same secure, read-only connection thousands of finance apps and banks already trust. The access is read-only (Buoy can never move your money), your bank credentials are never seen or stored by me, and everything is encrypted in transit and at rest.',
    },
    {
      q: 'How does “count-up” budgeting actually work?',
      a: 'Instead of a balance that ticks down to zero, each spending category earns a small allowance every day. Underspend and the number grows, so saving feels like progress you can watch, not a restriction you have to endure.',
    },
    {
      q: 'Do I need a credit card to start?',
      a: 'No. You can start your free trial without entering a card. Link a bank when you’re ready to see Buoy work with your real spending.',
    },
    {
      q: 'Can I cancel anytime?',
      a: 'Anytime, with no lock-in. Choose monthly or annual billing and change or cancel whenever you like.',
    },
    {
      q: 'What’s the difference between Standard and Premium?',
      a: 'Standard gives you the full count-up budgeting toolkit: complete budgeting, subscription tracking, AI budgeting tips, and the achievements, streaks, and challenges that make saving a game. Premium adds an interactive AI chat advisor, the latest AI models, and family sharing so your whole household can budget together.',
    },
    {
      q: 'Does Buoy reward me for saving?',
      a: 'Yes, that’s the whole idea. Staying under budget builds daily streaks, unlocks achievement badges for real milestones (your first $100 saved, a goal reached, a month under budget), and levels you up from Beginner to Legend. Saving stops being a chore and starts feeling like progress.',
    },
    {
      q: 'Which banks are supported?',
      a: 'Most major U.S. banks and credit unions connect through Plaid. If your institution is supported by Plaid, it works with Buoy.',
    },
  ],
} as const;

export const FOOTER = {
  blurb: 'Budget Smarter with AI-Powered Financial Insights.',
  security: 'Encrypted in transit & at rest',
} as const;
