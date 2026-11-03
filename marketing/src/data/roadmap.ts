// Public, product-only roadmap, curated from open GitHub issues (best guesses;
// edit freely). Discipline: product/UX only (never security), no dates, only
// feasible directions (each named item reads as an implicit promise pre-launch).
// "Live" grounds credibility (I ship); "Building"/"Exploring" invite co-creation.

export const ROADMAP = {
  columns: [
    {
      key: 'live',
      title: 'Live now',
      blurb: 'Already in the app.',
      items: [
        { title: 'Count-up budgeting', desc: 'Daily allowances that grow when you underspend.' },
        { title: 'AI advisor', desc: 'Budgeting tips, plus an AI chat advisor on Premium.' },
        { title: 'Achievements, streaks & levels', desc: 'Beginner to Legend, with under-budget streaks.' },
        { title: 'Subscription tracking', desc: 'Recurring charges surfaced automatically.' },
        { title: 'Family sharing', desc: 'Budget together on Premium.' },
        { title: 'Read-only bank linking', desc: 'Secure connections through Plaid.' },
        { title: 'Flexible budget controls', desc: 'Per-category rollover modes and count-up toggles.' },
      ],
    },
    {
      key: 'building',
      title: 'Building now',
      blurb: 'Actively in progress.',
      items: [
        { title: 'Native iOS & Android apps', desc: 'Buoy in your pocket, app-store native.' },
        { title: 'Push notifications & reminders', desc: 'Nudges for streaks, budgets, and bills.' },
      ],
    },
    {
      key: 'exploring',
      title: 'Exploring',
      blurb: 'On my mind, tell me what matters most.',
      items: [
        { title: 'Even more budget flexibility', desc: 'Category transfers and income auto-inflate.' },
        { title: 'More bank connections', desc: 'Coverage beyond Plaid (e.g. MX, Teller) for more institutions.' },
        { title: 'Richer AI', desc: 'Interactive chat visualizations and more model choices.' },
      ],
    },
  ],
} as const;
