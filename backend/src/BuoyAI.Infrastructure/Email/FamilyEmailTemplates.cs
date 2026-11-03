namespace BuoyAI.Infrastructure.Email;

/// <summary>
/// Email templates for family sharing features.
/// </summary>
public static class FamilyEmailTemplates
{
    public static (string Html, string Text) FamilyInvitation(
        string inviterName,
        string familyName,
        string inviteeEmail,
        string inviteLink,
        string roleName)
    {
        var content = $@"
        {EmailTemplateBase.SectionHeading("You're Invited to Join a Family!")}
        
        <p>Hi there,</p>
        
        <p><strong>{inviterName}</strong> has invited you to join <strong>{familyName}</strong> on Buoy Money!</p>
        
        {EmailTemplateBase.InfoBox($@"
            <strong>Family:</strong> {familyName}<br>
            <strong>Your role:</strong> {roleName}<br>
            <strong>Invited by:</strong> {inviterName}
        ")}
        
        <p>By joining, you'll be able to:</p>
        <ul>
            <li>🏠 Share budgets and track family finances together</li>
            <li>📊 View shared savings goals</li>
            <li>💬 Collaborate on financial decisions</li>
        </ul>
        
        <div style='text-align: center; margin: 30px 0;'>
            {EmailTemplateBase.PrimaryButton("Accept Invitation", inviteLink)}
        </div>
        
        <p style='color: #6B7280; font-size: 14px;'>
            This invitation was sent to {inviteeEmail}. If you weren't expecting this invitation, you can safely ignore this email.
        </p>
        
        <p style='color: #6B7280; font-size: 14px;'>
            Or copy and paste this link: <span style='word-break: break-all;'>{inviteLink}</span>
        </p>";

        var html = EmailTemplateBase.WrapInTemplate("Buoy Money Family", "👨‍👩‍👧‍👦", content, "Best regards,<br>The Buoy Money Team");

        var text = $@"You're Invited to Join a Family!

Hi there,

{inviterName} has invited you to join {familyName} on Buoy Money!

Family: {familyName}
Your role: {roleName}
Invited by: {inviterName}

Accept invitation: {inviteLink}

This invitation was sent to {inviteeEmail}.

Best regards,
The Buoy Money Team";

        return (html, text);
    }

    public static (string Html, string Text) FamilyInvitationAccepted(
        string ownerFirstName,
        string memberName,
        string familyName,
        string memberRole)
    {
        var content = $@"
        {EmailTemplateBase.SectionHeading("New Family Member! 🎉")}
        
        <p>Hi {ownerFirstName},</p>
        
        {EmailTemplateBase.SuccessBox($@"
            <strong>{memberName}</strong> has joined <strong>{familyName}</strong>!
        ")}
        
        {EmailTemplateBase.InfoBox($@"
            <strong>Member:</strong> {memberName}<br>
            <strong>Role:</strong> {memberRole}
        ")}
        
        <p>You can manage family members and their roles in your family settings.</p>
        
        <div style='text-align: center; margin: 30px 0;'>
            {EmailTemplateBase.SecondaryButton("Manage Family", "https://buoy-ai.com/app/family")}
        </div>";

        var html = EmailTemplateBase.WrapInTemplate("Buoy Money Family", "👨‍👩‍👧‍👦", content, "Best regards,<br>The Buoy Money Team");

        var text = $@"New Family Member!

Hi {ownerFirstName},

{memberName} has joined {familyName}!

Member: {memberName}
Role: {memberRole}

Manage family: https://buoy-ai.com/app/family

Best regards,
The Buoy Money Team";

        return (html, text);
    }

    public static (string Html, string Text) FamilyMemberRemoved(
        string memberFirstName,
        string familyName,
        string removedByName)
    {
        var content = $@"
        {EmailTemplateBase.SectionHeading("Family Membership Ended")}
        
        <p>Hi {memberFirstName},</p>
        
        <p>You have been removed from <strong>{familyName}</strong> by {removedByName}.</p>
        
        {EmailTemplateBase.InfoBox($@"
            <strong>What this means:</strong><br>
            • You no longer have access to shared family budgets<br>
            • Your personal data remains intact in your own account<br>
            • Any data you contributed to the family remains with the family
        ")}
        
        <p>If you believe this was a mistake, please contact the family owner directly.</p>
        
        <div style='text-align: center; margin: 30px 0;'>
            {EmailTemplateBase.SecondaryButton("Go to Dashboard", "https://buoy-ai.com/app/dashboard")}
        </div>";

        var html = EmailTemplateBase.WrapInTemplate("Buoy Money Family", "👨‍👩‍👧‍👦", content, "Best regards,<br>The Buoy Money Team");

        var text = $@"Family Membership Ended

Hi {memberFirstName},

You have been removed from {familyName} by {removedByName}.

What this means:
- You no longer have access to shared family budgets
- Your personal data remains intact in your own account
- Any data you contributed to the family remains with the family

If you believe this was a mistake, please contact the family owner.

Best regards,
The Buoy Money Team";

        return (html, text);
    }

    public static (string Html, string Text) FamilyMemberLeft(
        string ownerFirstName,
        string memberName,
        string familyName)
    {
        var content = $@"
        {EmailTemplateBase.SectionHeading("Family Member Left")}
        
        <p>Hi {ownerFirstName},</p>
        
        <p><strong>{memberName}</strong> has left <strong>{familyName}</strong>.</p>
        
        {EmailTemplateBase.InfoBox($@"
            <strong>What this means:</strong><br>
            • {memberName} no longer has access to shared family data<br>
            • Any contributions they made remain with the family<br>
            • Budget allocations may need to be adjusted
        ")}
        
        <p>You may want to review your family settings and budget allocations.</p>
        
        <div style='text-align: center; margin: 30px 0;'>
            {EmailTemplateBase.SecondaryButton("Manage Family", "https://buoy-ai.com/app/family")}
        </div>";

        var html = EmailTemplateBase.WrapInTemplate("Buoy Money Family", "👨‍👩‍👧‍👦", content, "Best regards,<br>The Buoy Money Team");

        var text = $@"Family Member Left

Hi {ownerFirstName},

{memberName} has left {familyName}.

Their access to shared family data has been revoked.

Manage family: https://buoy-ai.com/app/family

Best regards,
The Buoy Money Team";

        return (html, text);
    }

    public static (string Html, string Text) FamilyRoleChanged(
        string memberFirstName,
        string familyName,
        string oldRole,
        string newRole,
        string changedByName)
    {
        var roleDescription = newRole.ToLowerInvariant() switch
        {
            "admin" or "budgetadmin" => "You can now manage budgets, create goals, and invite new members.",
            "member" => "You can view shared budgets and goals, but cannot make changes.",
            "readonly" => "You have view-only access to shared family finances.",
            "viewer" => "You have view-only access to shared family finances.",
            _ => "Your permissions have been updated."
        };

        var content = $@"
        {EmailTemplateBase.SectionHeading("Your Family Role Has Changed")}
        
        <p>Hi {memberFirstName},</p>
        
        <p>Your role in <strong>{familyName}</strong> has been updated by {changedByName}:</p>
        
        {EmailTemplateBase.InfoBox($@"
            <strong>Previous role:</strong> {oldRole}<br>
            <strong>New role:</strong> {newRole}
        ")}
        
        <p>{roleDescription}</p>
        
        <div style='text-align: center; margin: 30px 0;'>
            {EmailTemplateBase.SecondaryButton("View Family", "https://buoy-ai.com/app/family")}
        </div>";

        var html = EmailTemplateBase.WrapInTemplate("Buoy Money Family", "👨‍👩‍👧‍👦", content, "Best regards,<br>The Buoy Money Team");

        var text = $@"Your Family Role Has Changed

Hi {memberFirstName},

Your role in {familyName} has been updated by {changedByName}:

Previous role: {oldRole}
New role: {newRole}

{roleDescription}

View family: https://buoy-ai.com/app/family

Best regards,
The Buoy Money Team";

        return (html, text);
    }

    public static (string Html, string Text) FamilyCreated(string ownerFirstName, string familyName)
    {
        var content = $@"
        {EmailTemplateBase.SectionHeading("Welcome to Your New Family! 🎉")}
        
        <p>Hi {ownerFirstName},</p>
        
        <p>Congratulations! You've created <strong>{familyName}</strong> on Buoy Money.</p>
        
        {EmailTemplateBase.SuccessBox($@"
            <strong>Your family is ready!</strong><br>
            Start inviting members to share your financial journey.
        ")}
        
        <p><strong>What you can do now:</strong></p>
        <ul>
            <li>👥 <strong>Invite family members</strong> - Share access with your household</li>
            <li>💰 <strong>Create shared budgets</strong> - Manage household expenses together</li>
            <li>🎯 <strong>Set family goals</strong> - Save for vacations, purchases, and more</li>
            <li>👁️ <strong>Manage visibility</strong> - Control what each member can see</li>
        </ul>
        
        <div style='text-align: center; margin: 30px 0;'>
            {EmailTemplateBase.PrimaryButton("Invite Family Members", "https://buoy-ai.com/app/family/invite")}
        </div>";

        var html = EmailTemplateBase.WrapInTemplate("Buoy Money Family", "🏠", content, "Welcome to family finances!<br>The Buoy Money Team");

        var text = $@"Welcome to Your New Family!

Hi {ownerFirstName},

Congratulations! You've created {familyName} on Buoy Money.

What you can do now:
- Invite family members to share access
- Create shared budgets for household expenses
- Set family savings goals
- Control visibility settings for each member

Invite family members: https://buoy-ai.com/app/family/invite

Welcome to family finances!
The Buoy Money Team";

        return (html, text);
    }
}
