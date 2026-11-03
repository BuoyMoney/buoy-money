namespace BuoyAI.Infrastructure.Email;

/// <summary>
/// Email templates for security-related emails (2FA, password reset, login alerts).
/// </summary>
public static class SecurityEmailTemplates
{
    public static (string Html, string Text) TwoFactorCode(string code, string purpose, string? ipAddress)
    {
        var purposeText = purpose.ToLowerInvariant() switch
        {
            "login" => "sign in to your account",
            "password-reset" => "reset your password",
            "account-verification" => "verify your account",
            "sensitive-action" => "complete a sensitive action",
            _ => "verify your identity"
        };

        var ipText = !string.IsNullOrEmpty(ipAddress) ? $"<p>Request from IP: {ipAddress}</p>" : "";

        var content = $@"
        {EmailTemplateBase.SectionHeading("Verification Code Required")}
        
        <p>We received a request to {purposeText}. Please use the following verification code:</p>
        
        {EmailTemplateBase.CodeBox(code)}
        
        <p><strong>This code will expire in 10 minutes.</strong></p>
        
        <p>If you didn't request this code, please ignore this email or contact our support team if you're concerned about your account security.</p>
        
        {ipText}";

        var html = EmailTemplateBase.WrapInTemplate("Buoy Money Security", "🛡️", content, "Best regards,<br>The Buoy Money Security Team");

        var text = $@"Buoy Money Verification Code

We received a request to {purposeText}. Please use the following verification code:

{code}

This code will expire in 10 minutes.

If you didn't request this code, please ignore this email.
{(!string.IsNullOrEmpty(ipAddress) ? $"\nRequest from IP: {ipAddress}" : "")}

Best regards,
The Buoy Money Security Team";

        return (html, text);
    }

    public static (string Html, string Text) PasswordReset(string resetLink)
    {
        var content = $@"
        {EmailTemplateBase.SectionHeading("Reset Your Password")}
        
        <p>We received a request to reset your Buoy Money password. Click the button below to create a new password:</p>
        
        <div style='text-align: center; margin: 30px 0;'>
            {EmailTemplateBase.PrimaryButton("Reset Password", resetLink)}
        </div>
        
        <p>Or copy and paste this link into your browser:</p>
        <p style='word-break: break-all; color: #6366F1;'>{resetLink}</p>
        
        <p><strong>This link will expire in 1 hour.</strong></p>
        
        <p>If you didn't request a password reset, you can safely ignore this email. Your password will not be changed.</p>";

        var html = EmailTemplateBase.WrapInTemplate("Buoy Money Security", "🔐", content, "Best regards,<br>The Buoy Money Security Team");

        var text = $@"Reset Your Buoy Money Password

We received a request to reset your Buoy Money password. Click the link below to create a new password:

{resetLink}

This link will expire in 1 hour.

If you didn't request a password reset, you can safely ignore this email.

Best regards,
The Buoy Money Security Team";

        return (html, text);
    }

    public static (string Html, string Text) NewDeviceLogin(string firstName, string deviceName, string location, string ipAddress, DateTime loginTime)
    {
        var content = $@"
        {EmailTemplateBase.SectionHeading("New Device Sign-In")}
        
        <p>Hi {firstName},</p>
        
        <p>We noticed a new sign-in to your Buoy Money account:</p>
        
        {EmailTemplateBase.InfoBox($@"
            <strong>Device:</strong> {deviceName}<br>
            <strong>Location:</strong> {location}<br>
            <strong>IP Address:</strong> {ipAddress}<br>
            <strong>Time:</strong> {loginTime:MMMM d, yyyy 'at' h:mm tt} UTC
        ")}
        
        <p>If this was you, no action is needed. If you don't recognize this activity, please secure your account immediately by changing your password.</p>
        
        <div style='text-align: center; margin: 30px 0;'>
            {EmailTemplateBase.PrimaryButton("Secure My Account", "https://buoy-ai.com/app/settings/security")}
        </div>";

        var html = EmailTemplateBase.WrapInTemplate("Buoy Money Security", "🔔", content, "Best regards,<br>The Buoy Money Security Team");

        var text = $@"New Device Sign-In to Buoy Money

Hi {firstName},

We noticed a new sign-in to your Buoy Money account:

Device: {deviceName}
Location: {location}
IP Address: {ipAddress}
Time: {loginTime:MMMM d, yyyy 'at' h:mm tt} UTC

If this was you, no action is needed. If you don't recognize this activity, please secure your account by visiting:
https://buoy-ai.com/app/settings/security

Best regards,
The Buoy Money Security Team";

        return (html, text);
    }

    public static (string Html, string Text) DataExported(string firstName, string format, DateTime exportedAt)
    {
        var what = format.Equals("csv", StringComparison.OrdinalIgnoreCase) ? "CSV (zip)" : "JSON";
        var content = $@"
        {EmailTemplateBase.SectionHeading("Your Data Was Downloaded")}

        <p>Hi {firstName},</p>

        <p>A full copy of your Buoy Money data was just downloaded from your account:</p>

        {EmailTemplateBase.InfoBox($@"
            <strong>Format:</strong> {what}<br>
            <strong>Time:</strong> {exportedAt:MMMM d, yyyy 'at' h:mm tt} UTC
        ")}

        <p>If this was you, no action is needed. If it wasn't, change your password and review your sign-in methods right away.</p>

        <div style='text-align: center; margin: 30px 0;'>
            {EmailTemplateBase.PrimaryButton("Secure My Account", "https://buoy-ai.com/app/settings/security")}
        </div>";

        var html = EmailTemplateBase.WrapInTemplate("Buoy Money Security", "📥", content, "Best regards,<br>The Buoy Money Security Team");

        var text = $@"Your Buoy Money data was downloaded

Hi {firstName},

A full copy of your Buoy Money data was just downloaded from your account.

Format: {what}
Time: {exportedAt:MMMM d, yyyy 'at' h:mm tt} UTC

If this was you, no action is needed. If it wasn't, change your password and review your sign-in methods:
https://buoy-ai.com/app/settings/security

Best regards,
The Buoy Money Security Team";

        return (html, text);
    }

    public static (string Html, string Text) SuspiciousLogin(string firstName, string location, string ipAddress, int riskScore, DateTime attemptTime)
    {
        var riskLevel = riskScore switch
        {
            >= 80 => "High",
            >= 50 => "Medium",
            _ => "Low"
        };

        var content = $@"
        {EmailTemplateBase.SectionHeading("⚠️ Suspicious Login Attempt Detected")}
        
        <p>Hi {firstName},</p>
        
        {EmailTemplateBase.DangerBox($@"
            <strong>We blocked a suspicious login attempt to your account.</strong>
        ")}
        
        <p>Here are the details:</p>
        
        {EmailTemplateBase.InfoBox($@"
            <strong>Location:</strong> {location}<br>
            <strong>IP Address:</strong> {ipAddress}<br>
            <strong>Risk Level:</strong> {riskLevel}<br>
            <strong>Time:</strong> {attemptTime:MMMM d, yyyy 'at' h:mm tt} UTC
        ")}
        
        <p><strong>If this was you:</strong> You may need to verify your identity to continue signing in.</p>
        
        <p><strong>If this wasn't you:</strong> We recommend changing your password immediately and enabling two-factor authentication if you haven't already.</p>
        
        <div style='text-align: center; margin: 30px 0;'>
            {EmailTemplateBase.PrimaryButton("Secure My Account", "https://buoy-ai.com/app/settings/security")}
        </div>";

        var html = EmailTemplateBase.WrapInTemplate("Buoy Money Security Alert", "🚨", content, "Best regards,<br>The Buoy Money Security Team");

        var text = $@"⚠️ Suspicious Login Attempt Detected

Hi {firstName},

We blocked a suspicious login attempt to your Buoy Money account.

Location: {location}
IP Address: {ipAddress}
Risk Level: {riskLevel}
Time: {attemptTime:MMMM d, yyyy 'at' h:mm tt} UTC

If this was you, you may need to verify your identity.
If this wasn't you, please change your password immediately.

Secure your account: https://buoy-ai.com/app/settings/security

Best regards,
The Buoy Money Security Team";

        return (html, text);
    }

    public static (string Html, string Text) MfaUpgrade(string firstName, int daysRemaining)
    {
        var content = $@"
        {EmailTemplateBase.SectionHeading("Secure Your Account with Two-Factor Authentication")}
        
        <p>Hi {firstName},</p>
        
        <p>As part of our commitment to keeping your financial data safe, we're requiring all users to enable two-factor authentication (2FA).</p>
        
        {EmailTemplateBase.WarningBox($@"
            <strong>You have {daysRemaining} days remaining</strong> to enable 2FA before it becomes mandatory.
        ")}
        
        <p>Two-factor authentication adds an extra layer of security by requiring a verification code in addition to your password when signing in.</p>
        
        <p><strong>Benefits of 2FA:</strong></p>
        <ul>
            <li>Protects against unauthorized access even if your password is compromised</li>
            <li>Required for accessing sensitive financial features</li>
            <li>Industry-standard security practice</li>
        </ul>
        
        <div style='text-align: center; margin: 30px 0;'>
            {EmailTemplateBase.PrimaryButton("Enable 2FA Now", "https://buoy-ai.com/app/settings/security")}
        </div>";

        var html = EmailTemplateBase.WrapInTemplate("Buoy Money Security", "🛡️", content, "Best regards,<br>The Buoy Money Security Team");

        var text = $@"Secure Your Account with Two-Factor Authentication

Hi {firstName},

As part of our commitment to keeping your financial data safe, we're requiring all users to enable two-factor authentication (2FA).

You have {daysRemaining} days remaining to enable 2FA before it becomes mandatory.

Enable 2FA: https://buoy-ai.com/app/settings/security

Best regards,
The Buoy Money Security Team";

        return (html, text);
    }

    public static (string Html, string Text) MfaGracePeriodEnded(string firstName)
    {
        var content = $@"
        {EmailTemplateBase.SectionHeading("Two-Factor Authentication Now Required")}
        
        <p>Hi {firstName},</p>
        
        {EmailTemplateBase.DangerBox($@"
            <strong>Your grace period for enabling two-factor authentication has ended.</strong>
        ")}
        
        <p>To continue accessing your Buoy Money account, you must now set up two-factor authentication. This security measure helps protect your sensitive financial data.</p>
        
        <p>The next time you sign in, you'll be prompted to set up 2FA. You can choose from:</p>
        <ul>
            <li><strong>Authenticator App</strong> - Use apps like Google Authenticator or Authy</li>
            <li><strong>Passkey</strong> - Use biometrics like Face ID or fingerprint</li>
        </ul>
        
        <div style='text-align: center; margin: 30px 0;'>
            {EmailTemplateBase.PrimaryButton("Sign In & Set Up 2FA", "https://buoy-ai.com/app/login")}
        </div>";

        var html = EmailTemplateBase.WrapInTemplate("Buoy Money Security", "🔒", content, "Best regards,<br>The Buoy Money Security Team");

        var text = $@"Two-Factor Authentication Now Required

Hi {firstName},

Your grace period for enabling two-factor authentication has ended.

To continue accessing your Buoy Money account, you must set up 2FA on your next sign-in.

Sign in: https://buoy-ai.com/app/login

Best regards,
The Buoy Money Security Team";

        return (html, text);
    }
}
