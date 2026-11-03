namespace BuoyAI.Infrastructure.Email;

/// <summary>
/// Base class for generating HTML email templates with consistent BuoyAI branding.
/// Provides common structure and styling that all email templates share.
/// </summary>
public static class EmailTemplateBase
{
    private const string BrandPrimaryColor = "#6366F1";
    private const string BrandSecondaryColor = "#4338CA";
    
    /// <summary>
    /// Wraps content in the standard BuoyAI email template with header and footer.
    /// </summary>
    /// <param name="title">Email title shown in header</param>
    /// <param name="headerEmoji">Emoji to display before title</param>
    /// <param name="content">Main HTML content of the email</param>
    /// <param name="footerText">Optional footer text (defaults to standard)</param>
    public static string WrapInTemplate(string title, string headerEmoji, string content, string? footerText = null)
    {
        var footer = footerText ?? "Best regards,<br>The Buoy Money Team";
        
        return $@"<!DOCTYPE html>
<html>
<head>
    <meta charset='utf-8'>
    <title>{title}</title>
</head>
<body style='font-family: Arial, sans-serif; line-height: 1.6; color: #333; max-width: 600px; margin: 0 auto; padding: 20px;'>
    <div style='background: linear-gradient(135deg, {BrandPrimaryColor} 0%, {BrandSecondaryColor} 100%); padding: 30px; text-align: center; border-radius: 10px 10px 0 0;'>
        <h1 style='color: white; margin: 0; font-size: 28px;'>{headerEmoji} {title}</h1>
    </div>
    
    <div style='background: #f8f9fa; padding: 30px; border-radius: 0 0 10px 10px; border: 1px solid #dee2e6;'>
        {content}
        
        <hr style='border: none; border-top: 1px solid #dee2e6; margin: 30px 0;'>
        
        <p style='font-size: 14px; color: #6c757d;'>
            {footer}
        </p>
    </div>
</body>
</html>";
    }

    /// <summary>
    /// Creates a styled primary button.
    /// </summary>
    public static string PrimaryButton(string text, string url)
    {
        return $@"<a href='{url}' style='display: inline-block; background: linear-gradient(135deg, {BrandPrimaryColor} 0%, {BrandSecondaryColor} 100%); color: white; padding: 12px 30px; text-decoration: none; border-radius: 6px; font-weight: bold; margin: 10px 0;'>{text}</a>";
    }

    /// <summary>
    /// Creates a styled secondary button.
    /// </summary>
    public static string SecondaryButton(string text, string url)
    {
        return $@"<a href='{url}' style='display: inline-block; background: #6c757d; color: white; padding: 12px 30px; text-decoration: none; border-radius: 6px; font-weight: bold; margin: 10px 0;'>{text}</a>";
    }

    /// <summary>
    /// Creates a highlighted code/verification box.
    /// </summary>
    public static string CodeBox(string code, int fontSize = 32)
    {
        return $@"<div style='background: white; border: 2px solid {BrandPrimaryColor}; border-radius: 8px; padding: 20px; text-align: center; margin: 20px 0;'>
            <span style='font-size: {fontSize}px; font-weight: bold; letter-spacing: 8px; color: {BrandPrimaryColor};'>{code}</span>
        </div>";
    }

    /// <summary>
    /// Creates an info/highlight box.
    /// </summary>
    public static string InfoBox(string content, string backgroundColor = "#e7f3ff", string borderColor = "#0066cc")
    {
        return $@"<div style='background: {backgroundColor}; border-left: 4px solid {borderColor}; padding: 15px; margin: 20px 0; border-radius: 4px;'>
            {content}
        </div>";
    }

    /// <summary>
    /// Creates a warning box.
    /// </summary>
    public static string WarningBox(string content)
    {
        return InfoBox(content, "#fff3cd", "#856404");
    }

    /// <summary>
    /// Creates a success box.
    /// </summary>
    public static string SuccessBox(string content)
    {
        return InfoBox(content, "#d4edda", "#155724");
    }

    /// <summary>
    /// Creates a danger/alert box.
    /// </summary>
    public static string DangerBox(string content)
    {
        return InfoBox(content, "#f8d7da", "#721c24");
    }

    /// <summary>
    /// Creates a stat/metric display.
    /// </summary>
    public static string StatBox(string label, string value, string color = "#6366F1")
    {
        return $@"<div style='text-align: center; padding: 15px;'>
            <div style='font-size: 28px; font-weight: bold; color: {color};'>{value}</div>
            <div style='font-size: 14px; color: #6c757d;'>{label}</div>
        </div>";
    }

    /// <summary>
    /// Creates a horizontal divider.
    /// </summary>
    public static string Divider()
    {
        return "<hr style='border: none; border-top: 1px solid #dee2e6; margin: 20px 0;'>";
    }

    /// <summary>
    /// Creates a section heading.
    /// </summary>
    public static string SectionHeading(string text)
    {
        return $"<h2 style='color: #495057; margin-top: 0;'>{text}</h2>";
    }

    /// <summary>
    /// Creates a paragraph with standard styling.
    /// </summary>
    public static string Paragraph(string text)
    {
        return $"<p>{text}</p>";
    }

    /// <summary>
    /// Creates a small/muted text paragraph.
    /// </summary>
    public static string SmallText(string text)
    {
        return $"<p style='font-size: 14px; color: #6c757d;'>{text}</p>";
    }
}
