# Buoy Money Privacy Policy

**Effective Date:** September 9, 2025  
**Last Updated:** October 14, 2026

## 1. INTRODUCTION

Buoy Money (formerly BuoyAI) is operated by BuoyAI LLC, a Kansas limited liability company ("we," "our," or "us"). We respect your privacy and are committed to protecting your personal information. This Privacy Policy explains how we collect, use, disclose, and safeguard your information when you use our personal finance management application and services, including the hosted app and BuoyAI Cloud. If you run the self-hosted edition on your own server, Section 4.7 explains what we do and do not receive.

## 2. INFORMATION WE COLLECT

### 2.1 Personal Information
- **Account Information:** Name, email address, username, and profile picture
- **Authentication Data:** Encrypted passwords, Google OAuth tokens
- **Profile Data:** Currency preferences, user settings

### 2.1a Website Visitor Information (No Account Required)
If you give us your email address on our website — for example to claim an extended free
trial or to have a calculator result sent to you — we store that address, the page or prompt
you gave it on, and any figures you asked us to save. You do not need an account for this,
and we collect nothing else about you.

- **Lawful basis:** your consent, given when you submit the form.
- **What we send:** confirmation of what you asked for, then occasional tips and product news.
- **Opting out:** every one of those emails carries a one-click unsubscribe link, which stops
  all marketing email to that address. If you later create an account, essential messages
  about your account and security are separate and are managed in Settings.
- **Retention:** we keep the address until you unsubscribe or ask us to delete it, whichever
  comes first. See Section 6.
- **We never sell it, and we never share it with advertisers.**

### 2.2 Financial Information (via Plaid)
- **Account Data:** Bank account names, types, balances, and account numbers
- **Transaction Data:** Transaction amounts, dates, merchant names, categories
- **Connection Data:** Financial institution information and connection status

### 2.3 Usage Information
- **Application Usage:** Pages visited, features used, session duration
- **AI Interactions:** Queries submitted to AI services, responses generated
- **System Logs:** Error logs, performance metrics, security events

### 2.4 Technical Information
- **Device Information:** Browser type, operating system, IP address
- **Cookies:** Session cookies for authentication and functionality
- **Security Data:** Login attempts, suspicious activity detection
- **IP Geolocation:** Approximate geographic location (city/region level) derived from IP address for security and fraud prevention purposes. The lookup runs on our own servers against a local database, so your IP address is not sent to any third party for this purpose. IP geolocation by [DB-IP](https://db-ip.com) (db-ip.com), licensed CC BY 4.0

## 3. HOW WE USE YOUR INFORMATION

### 3.1 Primary Services
- Provide personal finance management and budgeting tools
- Connect to your financial accounts through Plaid
- Categorize and analyze your financial transactions
- Generate AI-powered financial insights and recommendations
- Detect and manage recurring subscriptions

### 3.2 Account Management
- Create and maintain your user account
- Authenticate your identity and secure your account
- Detect suspicious login activity using IP-based geolocation (city/region level), performed offline on our own servers with the DB-IP.com IP to City Lite database; your IP address is not sent to a third party
- Provide risk-based security measures to protect your account
- Provide customer support and respond to inquiries
- Send important account and security notifications

### 3.3 Service Improvement
- Analyze usage patterns to improve our services
- Develop new features and functionality
- Monitor and maintain system security and performance
- Conduct research and analytics on financial trends

### 3.4 Anonymized Crowdsourced Subscription Detection

To improve our subscription detection accuracy for all users, we collect and aggregate anonymized pattern data when users confirm or dismiss detected subscriptions. This helps identify common subscription services and improve detection confidence.

**What we collect (anonymized):**
- Normalized merchant names (e.g., "netflix" not your specific transaction details)
- Typical subscription amount ranges (e.g., "$15-20/month")
- Common billing frequencies (monthly, annual, etc.)
- Aggregate confirmation and dismissal counts

**What we DO NOT collect:**
- Your name, account details, or any personally identifiable information
- Your specific transaction amounts or dates
- Any data that could identify you or your financial activity
- Individual user actions or patterns

**Your Control:**
- By default, your confirmation/dismissal actions contribute to this anonymized database
- You can opt out at any time in Settings > Privacy > Subscription Pattern Sharing
- Opting out does not affect your subscription detection functionality
- Previous anonymized data cannot be traced back to you

This feature helps improve subscription detection for the entire Buoy Money community while protecting your individual privacy.

## 4. INFORMATION SHARING AND DISCLOSURE

### 4.1 Third-Party Service Providers

#### Plaid Inc.
- **Purpose:** Secure connection to your financial accounts
- **Data Shared:** Bank account credentials, transaction data
- **Protection:** All data transmitted through encrypted connections
- **Control:** You can disconnect accounts at any time
- **Plaid's own policy:** Plaid handles your data under [Plaid's End User Privacy Policy](https://plaid.com/legal/#end-user-privacy-policy)

#### Google Services
- **OAuth Authentication:** For secure login via Google account (name and email only)
- **Push Notifications (Firebase Cloud Messaging):** A device token so we can deliver notifications you have enabled
- **AI features (Google Gemini API), only with your explicit permission:** AI chat, insights, charts, Money Wrapped bonus cards, subscription insights and automatic transaction categorization are powered by Google LLC's Gemini API. Nothing is sent to Gemini until you agree on the in-app permission screen, which names Google Gemini and lists what is sent. Section 4.5 describes exactly what is sent, how it is used, and how to withdraw.
- **Data Protection:** Subject to Google's privacy policies

#### Resend
- **Purpose:** Sends our emails (sign-in codes, account and billing messages)
- **Data Shared:** Your email address and the content of the emails we send you

#### Stripe, Inc.
- **Purpose:** Payment processing for subscriptions
- **Data Shared:** Name, email, and payment details. Card data is entered directly with Stripe (PCI-DSS Level 1); we store only tokenized references (customer/subscription IDs)
- **Data Protection:** Subject to Stripe's privacy policy

#### PostHog
- **Purpose:** First-party product analytics to understand feature usage and improve the app (not advertising)
- **Data Shared:** In-app usage/interaction events and device identifiers
- **Data Protection:** Subject to PostHog's privacy policy

#### Sentry
- **Purpose:** Crash and error monitoring to keep the app reliable
- **Data Shared:** Crash reports and diagnostic/technical data
- **Data Protection:** Subject to Sentry's privacy policy

### 4.2 We Do NOT Sell Your Data
- We never sell, rent, or trade your personal or financial information
- We do not share your data with advertisers or marketing companies
- Financial data is used solely for providing our services to you

### 4.3 Legal Requirements
We may disclose your information when required by law or to:
- Comply with legal process or government requests
- Protect our rights, property, or safety
- Prevent fraud or security threats
- Enforce our terms of service

### 4.4 Connected AI Assistants (Buoy Money Connect)

Buoy Money Connect is an **optional** feature that lets you link a third-party AI assistant (such as ChatGPT, Claude, or Gemini) to your Buoy Money account so you can ask it about your finances from within that assistant. It uses the industry-standard Model Context Protocol (MCP) with OAuth 2.0 authorization.

**Nothing is shared until you connect an assistant and approve it.** When you add Buoy Money as a connector, we show you a consent screen naming the specific assistant and the access it is requesting; data flows only after you approve, and only to the assistant you connected.

- **What is shared (read-only):** When connected, the assistant can *read* your financial information to answer your questions — including your accounts and balances, transactions and spending summaries, cash-flow figures, budgets, savings goals, subscriptions, income summaries, and your profile and app preferences.
- **What is NOT shared or permitted:** Your bank login credentials are never shared. The connection is **read-only** — a connected assistant cannot move money, make payments, initiate transfers, or change or delete your data.
- **Third-party processing:** Once you share data with an assistant, its provider (e.g., OpenAI, Anthropic, or Google) receives and processes that information under **its own privacy policy and terms**, which govern how that provider stores, uses, and retains it. We do not control that provider's handling of data you direct to it.
- **Your control:** You can disconnect the assistant at any time from that assistant's own settings (removing the Buoy Money connector), which immediately revokes its access. Access is scoped and time-limited: authorization tokens expire and must be renewed, and every connection is tied to your explicit, per-assistant consent.
- **Availability:** Buoy Money Connect is a Premium feature.

### 4.5 AI Features (Google Gemini)

**Who receives the data.** Google LLC, through the Gemini API, acting as our service provider. It is the only AI company we send your data to.

**What is sent, and where it comes from.** Only the data a feature needs for that request:
- **Transactions:** merchant, description, amount, date and category, including your bank's original description, check numbers, and the names and memos on transfers (for example Zelle or Venmo). These come from your bank through Plaid, or from what you enter.
- **Accounts:** account names and nicknames, balances, and account holder names (from Plaid).
- **Your plans:** budgets, savings goals, subscriptions, income and categorization rules (which you create in the app).
- **About you and your conversations:** your first name, your answers to the onboarding questions, what you type or attach in AI chat (including any statement files you upload), your AI chat history, and facts the assistant remembers from your conversations.
- **Receipts you send (only when you send one):** the screenshot, photo or PDF of a receipt you share with Buoy Money or scan in the app, and the names of your spending categories so each item can be placed in one. See Section 4.6.

This data is identifiable to your account within Buoy Money; it is not anonymized before it is sent.

**What is never sent.** Your bank login (Plaid holds it), full account or routing numbers, your email address, or your password.

**How it is used.**
- To answer your AI chat questions and to generate insights, charts and Money Wrapped bonus cards.
- To suggest categories for new transactions. This runs in the background after each bank sync, without you opening an AI screen.
- To learn from categorizations you confirm or correct: we use Gemini to turn the merchant, tags, amount and category into a numeric representation (an "embedding") that we store in our own database to categorize similar transactions later.
- To add insights to detected subscriptions (Premium).
- To read receipts you send and suggest how to split the matching charge by category (receipt scanning, Premium).
- To find relevant past AI conversations, including when you ask from a connected AI assistant (Section 4.4).

**Your permission.** On every platform (iOS, Android and the web) we ask before anything is sent, on a screen that names Google Gemini and lists this data. If you decline, Buoy Money keeps working without AI features, and new transactions are categorized with your rules and your bank's categories instead. You can withdraw at any time in Settings > AI Assistant; from that moment nothing more is sent. Insights and chat history already created stay in your account until you delete them or your account. If what we send changes, we ask again.

**Protection and retention.** Google processes this data under the Gemini API terms for our paid usage, which limit its use to providing the service to us and include data-protection commitments at least equal to those in this policy. Google states that under these terms it does not use API inputs or outputs to train or improve its models and does not use them for advertising. Google may keep request data for a limited time to detect abuse and meet legal obligations, as its terms describe. Within Buoy Money, AI chat history, remembered facts, insights and embeddings are stored with your account and deleted when your account is deleted (Section 6).

### 4.6 Receipt Scanning

Receipt scanning is optional. Nothing is read until you choose a receipt and tap Send.

**How it works.** You share a screenshot of an order page or a PDF with Buoy Money, or take a photo of a paper receipt in the app. The app shows you what will be sent; when you tap Send, it goes to Google Gemini to find the store, order number, date, items, prices and totals. We use those details to find the bank charge the receipt paid for and suggest a split by category. Nothing changes on your transactions until you tap Apply.

**What we keep.** The store, order number, date, items, prices, totals and the split we suggested, stored with your account. We do not store the image or PDF: it is read once and discarded. A screenshot can show more than the receipt (a notification, an address, the last digits of a card); Gemini is told to ignore anything that isn't the receipt, and we keep only the receipt details above.

**Your control.** Withdrawing AI permission (Section 4.5) stops receipts from being read. You can dismiss a suggested split, and receipt details are deleted when your account is deleted (Section 6).

### 4.7 Self-Hosted Servers and BuoyAI Cloud

The self-hosted edition of Buoy Money runs on a server you control. **Your data lives on that server. We do not receive it, cannot see it, and cannot recover it.** This policy does not cover what happens on your own server, which you run and secure yourself.

If you pair a self-hosted server with BuoyAI Cloud, our Cloud services handle the following. They run separately from the hosted app, with their own databases.

**Account and pairing records**
- Your account email and your Cloud account identifier, which is your Stripe customer ID
- A record for each server you pair: its identifier, its credential, when it was paired, confirmed or revoked, and a log of Cloud actions on your account (identifiers and results only, never transaction details). We purge log rows older than 13 months.

**Bank sync (Plaid)**
- The Plaid access token for each bank login you connect through Cloud, stored encrypted in a vault. Cloud also keeps where each bank's feed left off (a cursor), a ledger of calls, and the bank's connection status.
- Your bank login (username and password) is entered with Plaid and is never stored by us.
- Cloud passes transactions through, but does not keep them. When your server asks for new transactions, Cloud fetches them from Plaid, holds them in memory just long enough to encrypt them so that only your server can open them, and sends them. We do not store the transaction content. Once your server has them, they are yours to protect.
- If you revoke a server, or your Bank sync plan ends, we remove the bank logins held for that server from Plaid 14 days later. You can ask us to remove them sooner at privacy@buoy-ai.com.

**AI usage**
- Cloud AI counts each request against your monthly allowance and your pay-as-you-go cap. For each request it records the account, the feature used, the cost and the time, so it can enforce the allowance and bill any overage.
- To answer a request, Cloud sends what your server submits to the AI model provider that runs the model, and returns the answer. Section 4.5 describes the hosted app's AI features; the Cloud AI gateway is a separate service.

**Instant refresh**
- A ledger row for every refresh you request, whether allowed or refused, so we can enforce the monthly count and bill refreshes beyond it when that is turned on.

**Push notifications (push relay)**
- For paired phones, the relay stores the phone's push token (encrypted), a pairing token that stands in for it, and when a message went through. Your server encrypts each notification for your phone before it leaves, so the relay forwards a message it cannot read and does not store it. The relay cannot see the title, the text, the amounts or your server's address.

**Payments**
- Stripe processes payments for Cloud, as described in Section 4.1. We do not store card numbers.

**What we do not do.** We do not sell Cloud data, and we do not use it for advertising.

## 5. DATA SECURITY

### 5.1 Encryption Standards
- **Data in Transit:** All communications encrypted with TLS 1.3
- **Data at Rest:** Sensitive data encrypted with AES-256
- **Financial Tokens:** Plaid access tokens encrypted before storage
- **Passwords:** Hashed using BCrypt with salt

### 5.2 Security Measures
- **Authentication:** Multi-factor authentication support
- **Access Controls:** Role-based access and principle of least privilege
- **Container Security:** Read-only filesystems and minimal capabilities
- **Monitoring:** Continuous security monitoring and incident response
- **Biometric Unlock:** Fingerprint/face verification happens entirely on your device through the operating system; biometric data never reaches our servers

### 5.3 Infrastructure Security
- **Network Protection:** Firewall rules and secure proxy configuration
- **Regular Updates:** Automated security patches and vulnerability scanning
- **Backup Security:** Encrypted backups with secure access controls

### 5.4 Data Stored on Your Device (Mobile App)
To keep the mobile app fast and usable without an internet connection, it stores a local copy of your recent financial data (such as transactions, budgets, balances, and savings goals) and any changes you make while offline:

- **Protection:** This on-device data lives in the app's private storage and is protected by your device's built-in encryption (standard on modern iOS and Android devices). Your bank login credentials are never stored on your device.
- **Deletion:** The local copy is deleted automatically when you sign out, when your account is deleted, or when a different user signs in on the same device.
- **Your Control:** You can turn off periodic background updates at any time in **Settings → Privacy & Data → Background sync**; the app will then refresh data only while you are using it.

## 6. DATA RETENTION

### 6.1 Account Data
- **Active Accounts:** Retained while your account is active
- **Closed Accounts:** Deleted within 30 days of account closure
- **User Request:** Immediate deletion upon verified user request

### 6.2 Financial Data
- **Transaction History:** Retained for service functionality
- **Connection Data:** Maintained while accounts are connected
- **AI Insights:** Stored to improve personalization and avoid duplicates
- **AI Data:** AI chat history, remembered facts and categorization embeddings are kept with your account and deleted when it is deleted

### 6.3 Log Data
- **Security Logs:** 90 days for security monitoring
- **System Logs:** 30 days for operational purposes
- **Error Logs:** 90 days for troubleshooting and improvement

### 6.4 BuoyAI Cloud Data
- **Bank logins:** Held while your Bank sync plan is active and the server is paired. Removed from Plaid 14 days after the plan ends or the server is revoked, or sooner on request (Section 4.7)
- **Cloud action log:** 13 months
- **Transactions:** Not stored by Cloud (Section 4.7)

## 7. YOUR RIGHTS AND CHOICES

### 7.1 Account Control
- **Access:** View your data in the app; request a full copy by emailing privacy@buoy-ai.com
- **Correction:** Update incorrect or incomplete information
- **Deletion:** Request deletion of your account and data — in the app (Settings → Delete Account) or, without the app, at [buoy-ai.com/delete-account](https://buoy-ai.com/delete-account)
- **Portability:** Download your data and the shared family data you can see, without secrets, in a portable, machine-readable format (JSON or CSV) from Settings > Privacy & Data > Download my data, or request an export by emailing privacy@buoy-ai.com

### 7.2 Connection Management
- **Disconnect Accounts:** Remove bank connections at any time
- **Data Synchronization:** Control when financial data is updated
- **Selective Access:** Choose which accounts to connect

### 7.3 Privacy Settings
- **AI Features:** Allow or withdraw sharing with Google Gemini at any time in Settings > AI Assistant (see Section 4.5)
- **Connected AI Assistants:** Review and revoke any third-party AI assistant you have linked via Buoy Money Connect (see Section 4.4). Disconnect it from that assistant's settings to immediately cut off access
- **Subscription Pattern Sharing:** Opt out of contributing anonymized subscription pattern data (see Section 3.4)
- **Data Processing:** Control how your data is analyzed
- **Communications:** Manage notification preferences

## 8. COOKIES AND TRACKING

### 8.1 Essential Cookies and Storage
These are always active and cannot be turned off, because the app cannot function without them:
- **Authentication:** Required for secure login and session management
- **Security:** Necessary for fraud prevention and security
- **Functionality:** Enable core application features (e.g. saved preferences)
- **Website preferences:** Our site remembers, in your browser's local storage, that you
  dismissed or completed an offer prompt, so it does not ask you again. This exists purely to
  honour your choice, is never read for analytics or advertising, and never leaves your device.

### 8.2 Analytics Cookies and Storage (Consent-Based)
- We use **first-party product analytics (PostHog)** to understand feature usage and improve the app. This sets analytics cookies / local storage and is **not advertising**.
- **These are off by default.** We ask for your choice on first use via a consent banner, and analytics do not load — and no analytics cookies or storage are written — until you opt in.
- You can change your choice at any time under **Settings → Privacy & Data → Product analytics**. Turning it off stops analytics collection immediately.
- Crash reporting (Sentry) is used to diagnose errors and improve stability. See Section 4.1.

### 8.3 No Advertising or Cross-Site Tracking
- We do not use advertising cookies or tracking pixels
- No data shared with social media platforms for tracking
- No behavioral advertising or profiling for marketing
- We do use first-party product analytics (PostHog) and crash reporting (Sentry) to improve the app and fix bugs — never for advertising. See Section 4.1.

## 9. CHILDREN'S PRIVACY

Buoy Money is not intended for children under 18. We do not knowingly collect personal information from children under 18. If you believe we have collected information from a child under 18, please contact us immediately.

## 10. INTERNATIONAL DATA TRANSFERS

Your information may be processed in countries where we operate. We ensure appropriate safeguards are in place to protect your information when transferred internationally.

## 11. PRIVACY POLICY UPDATES

We may update this Privacy Policy periodically. We will notify you of significant changes by:
- Posting the updated policy on our website
- Sending email notifications for material changes
- Providing in-app notifications when you next log in

## 12. CONTACT INFORMATION

For privacy-related questions or concerns, contact us at:

**Email:** privacy@buoy-ai.com  
**Subject Line:** Privacy Policy Inquiry

For data deletion requests or exercising your privacy rights:
**Email:** privacy@buoy-ai.com  
**Subject Line:** Data Rights Request

## 13. COMPLIANCE

This Privacy Policy complies with:
- General Data Protection Regulation (GDPR)
- California Consumer Privacy Act (CCPA)
- Financial data protection standards
- Plaid privacy and security requirements

---

**Document Version:** 1.3  
**Effective Date:** September 9, 2025  
**Next Review:** January 16, 2027

*This Privacy Policy is part of our Terms of Service and governs your use of Buoy Money services.*
