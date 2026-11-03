using Microsoft.Extensions.Logging;
using BuoyAI.Core.DTOs.AI;
using Mscc.GenerativeAI;

namespace BuoyAI.Infrastructure.Services;

/// <summary>
/// Partial class for AI file upload functionality.
/// Handles uploading files to Gemini Files API for use in chat.
/// </summary>
public partial class AIService
{
    // Daily upload quota per user (50MB)
    private const long DailyUploadQuotaBytes = 50 * 1024 * 1024;
    
    /// <summary>
    /// Upload a file to Gemini Files API for use in chat.
    /// </summary>
    public async Task<ChatFileUploadResponse> UploadChatFileAsync(
        int userId, 
        Stream fileStream, 
        string fileName, 
        string mimeType)
    {
        try
        {
            // Validate MIME type
            if (!AllowedChatFileTypes.IsAllowed(mimeType))
            {
                _logger.LogWarning("User {UserId} attempted to upload unsupported file type: {MimeType}", 
                    userId, mimeType);
                return new ChatFileUploadResponse
                {
                    Success = false,
                    Error = $"File type '{mimeType}' is not supported. Allowed types: OFX, QFX, QIF, CSV, TXT, PDF"
                };
            }
            
            // Validate file size
            var maxSize = AllowedChatFileTypes.GetMaxSize(mimeType);
            if (fileStream.Length > maxSize)
            {
                var fileSizeMb = fileStream.Length / (1024.0 * 1024.0);
                var maxSizeMb = maxSize / (1024.0 * 1024.0);
                
                _logger.LogWarning("User {UserId} attempted to upload file exceeding size limit: {Size} > {MaxSize}", 
                    userId, fileStream.Length, maxSize);
                return new ChatFileUploadResponse
                {
                    Success = false,
                    Error = $"File size ({fileSizeMb:F2}MB) exceeds maximum allowed ({maxSizeMb:F2}MB) for this file type"
                };
            }
            
            // Daily upload quota + magic-byte content validation are enforced at the API edge
            // (AIController.UploadChatFile via IUploadQuotaService + FileSignatureValidator, #367)
            // before this service is called.

            // Get API key
            var apiKey = _geminiApiKey;
            if (string.IsNullOrEmpty(apiKey))
            {
                _logger.LogError("No Gemini API key available for file upload");
                return new ChatFileUploadResponse
                {
                    Success = false,
                    Error = "AI service is not configured. Please contact support."
                };
            }
            
            // Upload to Gemini Files API
            var googleAI = new GoogleAI(apiKey);
            
            _logger.LogInformation("Uploading file for user {UserId}: {FileName} ({MimeType}, {Size} bytes)", 
                userId, fileName, mimeType, fileStream.Length);
            
            var uploadResponse = await googleAI.UploadFile(
                fileStream, 
                displayName: fileName, 
                mimeType: mimeType);
            
            if (uploadResponse?.File == null)
            {
                _logger.LogError("Gemini file upload returned null response for user {UserId}", userId);
                return new ChatFileUploadResponse
                {
                    Success = false,
                    Error = "Failed to upload file to AI service. Please try again."
                };
            }
            
            _logger.LogInformation("File uploaded successfully for user {UserId}: {FileUri}", 
                userId, uploadResponse.File.Uri);
            
            // Gemini files expire after 48 hours
            var expiresAt = DateTime.UtcNow.AddHours(48);
            
            return new ChatFileUploadResponse
            {
                Success = true,
                FileUri = uploadResponse.File.Uri,
                MimeType = uploadResponse.File.MimeType ?? mimeType,
                DisplayName = fileName,
                SizeBytes = fileStream.Length,
                ExpiresAt = expiresAt
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error uploading file for user {UserId}: {FileName}", userId, fileName);
            return new ChatFileUploadResponse
            {
                Success = false,
                Error = "An error occurred while uploading the file. Please try again."
            };
        }
    }
}
