using System.Net;

namespace BuoyAI.Infrastructure.Email;

/// <summary>
/// The connect-your-bank reminders for trial users who signed up and never connected an
/// account. Connecting is the step everything else in the app depends on, and PostHog showed
/// most new users do try it, so these emails point at that one action and nothing else.
/// </summary>
/// <remarks>
/// Deliberately plain: no gradient header, no emoji, signed by a person. They read as a note
/// from the founder rather than a marketing blast, which is both the honest framing for a
/// one-person company and what lands in the Primary tab. Step 2 asks for a reply, so it has
/// no button at all.
/// </remarks>
public static class ActivationEmailTemplates
{
    private const string BrandBlue = "#094AAA";

    public static string ConnectReminderSubject(int step, string firstName) => step switch
    {
        1 => "The 2-minute step that makes Buoy Money work",
        // "there" is the no-name fallback; "Quick question, there" reads like a mail merge.
        _ => string.IsNullOrWhiteSpace(firstName) || firstName == "there"
            ? "Quick question"
            : $"Quick question, {firstName}",
    };

    public static (string Html, string Text) ConnectReminder(
        int step,
        string firstName,
        int trialDaysLeft,
        string accountsUrl,
        string settingsUrl)
    {
        return step == 1
            ? StepOne(firstName, trialDaysLeft, accountsUrl, settingsUrl)
            : StepTwo(firstName, accountsUrl, settingsUrl);
    }

    private static (string Html, string Text) StepOne(
        string firstName, int trialDaysLeft, string accountsUrl, string settingsUrl)
    {
        var name = WebUtility.HtmlEncode(firstName);
        var daysLine = trialDaysLeft == 1
            ? "You have 1 day left on your free trial, no card needed."
            : $"You have {trialDaysLeft} days left on your free trial, no card needed.";

        var body = $@"
        <p>Hi {name},</p>

        <p>Your Buoy Money trial is running, but you haven't connected a bank yet. That's the step
        that makes everything else work. Once your transactions are in, Buoy Money sorts them into
        categories, helps you build a budget from what you actually spend, and shows you what's
        left for the rest of the month.</p>

        <p>It takes about two minutes:</p>
        <ul style='padding-left:20px'>
            <li>You sign in to your bank through Plaid, so Buoy Money never sees your bank password.</li>
            <li>It's read-only. Buoy Money can see balances and transactions. It can't move money.</li>
            <li>You can disconnect any time from the Accounts page.</li>
        </ul>

        <p style='margin:28px 0'>
            <a href='{accountsUrl}' style='background:{BrandBlue};color:#ffffff;text-decoration:none;padding:12px 22px;border-radius:6px;font-weight:bold;display:inline-block'>Connect my bank</a>
        </p>

        <p>{daysLine}</p>

        <p>Jonathan<br>Founder, Buoy Money</p>";

        var text = $@"Hi {firstName},

Your Buoy Money trial is running, but you haven't connected a bank yet. That's the step that makes everything else work. Once your transactions are in, Buoy Money sorts them into categories, helps you build a budget from what you actually spend, and shows you what's left for the rest of the month.

It takes about two minutes:
- You sign in to your bank through Plaid, so Buoy Money never sees your bank password.
- It's read-only. Buoy Money can see balances and transactions. It can't move money.
- You can disconnect any time from the Accounts page.

Connect my bank: {accountsUrl}

{daysLine}

Jonathan
Founder, Buoy Money

---
You're getting this because you started a Buoy Money trial. Email settings: {settingsUrl}";

        return (Letter(body, settingsUrl), text);
    }

    private static (string Html, string Text) StepTwo(
        string firstName, string accountsUrl, string settingsUrl)
    {
        var name = WebUtility.HtmlEncode(firstName);

        var body = $@"
        <p>Hi {name},</p>

        <p>I'm Jonathan, and I built Buoy Money. I noticed you signed up but never connected a
        bank, and I'd really like to know why.</p>

        <p>Could you hit reply and tell me in a few words? Whatever the reason is, it helps.
        A few I'd guess at:</p>
        <ul style='padding-left:20px'>
            <li>I'm not comfortable linking my bank to a new app.</li>
            <li>My bank wasn't in the list.</li>
            <li>Something broke or got confusing.</li>
            <li>I just haven't had time.</li>
        </ul>

        <p>I read every reply myself, and what you tell me changes what I build next.</p>

        <p>If it's just time, it takes about two minutes:
        <a href='{accountsUrl}' style='color:{BrandBlue}'>connect your bank here</a>.</p>

        <p>Thanks,<br>Jonathan</p>";

        var text = $@"Hi {firstName},

I'm Jonathan, and I built Buoy Money. I noticed you signed up but never connected a bank, and I'd really like to know why.

Could you hit reply and tell me in a few words? Whatever the reason is, it helps. A few I'd guess at:
- I'm not comfortable linking my bank to a new app.
- My bank wasn't in the list.
- Something broke or got confusing.
- I just haven't had time.

I read every reply myself, and what you tell me changes what I build next.

If it's just time, it takes about two minutes: {accountsUrl}

Thanks,
Jonathan

---
You're getting this because you started a Buoy Money trial. Email settings: {settingsUrl}";

        return (Letter(body, settingsUrl), text);
    }

    /// <summary>A plain, letter-style wrapper: readable width, system font, a quiet footer.</summary>
    private static string Letter(string body, string settingsUrl) => $@"<!DOCTYPE html>
<html>
<head><meta charset='utf-8'></head>
<body style='margin:0;padding:24px 16px;background:#ffffff'>
    <div style='max-width:560px;margin:0 auto;font-family:-apple-system,Segoe UI,Roboto,Helvetica,Arial,sans-serif;font-size:16px;line-height:1.6;color:#1f2937'>
        {body}
        <p style='margin-top:36px;font-size:12px;color:#9ca3af'>You're getting this because you
        started a Buoy Money trial. <a href='{settingsUrl}' style='color:#9ca3af'>Email settings</a></p>
    </div>
</body>
</html>";
}
