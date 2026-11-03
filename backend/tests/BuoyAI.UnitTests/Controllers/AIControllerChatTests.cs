using BuoyAI.Core.DTOs.AI;
using BuoyAI.Core.Enums;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace BuoyAI.UnitTests.Controllers;

/// <summary>
/// The non-streaming chat endpoint read X-Client-Type but built a fresh AIQueryRequest
/// without it, so a phone calling POST /api/ai/chat got wide-screen replies (markdown
/// tables). The streaming endpoint passes the whole request through and was fine.
/// </summary>
public class AIControllerChatTests
{
  private const int UserId = AIControllerTestHost.UserId;
  private readonly AIControllerTestHost _host = new();

  [Fact]
  public async Task Chat_PassesClientTypeAndHistoryToTheAiService()
  {
    _host.Entitlements.Setup(e => e.HasPremiumAccessAsync(UserId)).ReturnsAsync(true);
    AIQueryRequest? sent = null;
    _host.AiService
      .Setup(s => s.ProcessQueryAsync(UserId, It.IsAny<AIQueryRequest>()))
      .Callback<int, AIQueryRequest>((_, r) => sent = r)
      .ReturnsAsync(new AIQueryResponse { Success = true, Response = "About $400 a month." });

    var result = await _host.Controller.Chat(new AIChatRequest
    {
      Message = "How much a month to finish my Japan Trip goal?",
      ConversationHistory = new List<ChatMessage>
      {
        new() { Role = "user", Content = "Hi" },
        new() { Role = "assistant", Content = "Hello! How can I help?" }
      }
    });

    result.Should().BeOfType<OkObjectResult>();
    sent.Should().NotBeNull();
    sent!.ClientType.Should().Be("mobile-android");
    sent.Query.Should().Be("How much a month to finish my Japan Trip goal?");
    sent.ConversationHistory.Should().BeEquivalentTo(new[]
    {
      new ConversationMessage { Role = "user", Content = "Hi" },
      new ConversationMessage { Role = "assistant", Content = "Hello! How can I help?" }
    });
  }

  [Fact]
  public async Task Chat_StandardTierIsRefusedWithoutCallingTheModel()
  {
    _host.Entitlements.Setup(e => e.HasPremiumAccessAsync(UserId)).ReturnsAsync(false);

    var result = await _host.Controller.Chat(new AIChatRequest { Message = "Hi" });

    result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(403);
    _host.AiService.Verify(s => s.ProcessQueryAsync(It.IsAny<int>(), It.IsAny<AIQueryRequest>()), Times.Never);
  }
}
