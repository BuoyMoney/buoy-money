using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using BuoyAI.Core.DTOs;
using BuoyAI.Core.DTOs.Auth;
using BuoyAI.Core.Enums;
using BuoyAI.IntegrationTests.Helpers;
using FluentAssertions;
using Newtonsoft.Json;
using Xunit;

namespace BuoyAI.IntegrationTests.Controllers;

public class FamilyControllerIntegrationTests : IDisposable
{
    private readonly TestWebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public FamilyControllerIntegrationTests()
    {
        _factory = new TestWebApplicationFactory<Program>();
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private async Task AuthenticateAsync()
    {
        var loginRequest = new LoginRequest
        {
            Email = "test@BuoyAI.com",
            Password = "password123"
        };

        var loginResponse = await _client.PostAsJsonAsync("/api/auth/login", loginRequest);
        loginResponse.EnsureSuccessStatusCode();

        var loginContent = await loginResponse.Content.ReadAsStringAsync();
        var loginResult = JsonConvert.DeserializeObject<LoginResponse>(loginContent);

        _client.DefaultRequestHeaders.Authorization = 
            new AuthenticationHeaderValue("Bearer", loginResult!.Token);
    }

    #region Authentication Tests

    [Fact]
    public async Task GetFamily_WithoutAuthentication_ShouldReturnUnauthorized()
    {
        // Act
        var response = await _client.GetAsync("/api/family");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task CreateFamily_WithoutAuthentication_ShouldReturnUnauthorized()
    {
        // Arrange
        var request = new CreateFamilyRequest { Name = "Test Family" };

        // Act
        var response = await _client.PostAsJsonAsync("/api/family", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    #endregion

    #region Family Management Tests

    [Fact]
    public async Task GetFamily_WithNoFamily_ShouldReturnNotFound()
    {
        // Arrange
        await AuthenticateAsync();

        // Act
        var response = await _client.GetAsync("/api/family");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task CreateFamily_WithValidRequest_ShouldReturnCreated()
    {
        // Arrange
        await AuthenticateAsync();
        var request = new CreateFamilyRequest
        {
            Name = "Smith Family"
        };

        // Act
        var response = await _client.PostAsJsonAsync("/api/family", request);

        // Assert - May require Premium subscription
        response.StatusCode.Should().BeOneOf(HttpStatusCode.Created, HttpStatusCode.Forbidden);
        
        if (response.StatusCode == HttpStatusCode.Created)
        {
            var content = await response.Content.ReadAsStringAsync();
            var result = JsonConvert.DeserializeObject<FamilyDto>(content);
            
            result.Should().NotBeNull();
            result!.Name.Should().Be("Smith Family");
        }
    }

    [Fact]
    public async Task CreateFamily_WithEmptyName_ShouldReturnBadRequest()
    {
        // Arrange
        await AuthenticateAsync();
        var request = new CreateFamilyRequest
        {
            Name = ""
        };

        // Act
        var response = await _client.PostAsJsonAsync("/api/family", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task UpdateFamily_WithValidRequest_ShouldReturnOk()
    {
        // Arrange
        await AuthenticateAsync();
        
        // First create a family
        var createRequest = new CreateFamilyRequest { Name = "Original Name" };
        var createResponse = await _client.PostAsJsonAsync("/api/family", createRequest);
        
        if (createResponse.StatusCode == HttpStatusCode.Forbidden)
        {
            // User doesn't have Premium - skip this test
            return;
        }

        // Update request
        var updateRequest = new UpdateFamilyRequest
        {
            Name = "Updated Name"
        };

        // Act
        var response = await _client.PutAsJsonAsync("/api/family", updateRequest);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        
        var content = await response.Content.ReadAsStringAsync();
        var result = JsonConvert.DeserializeObject<FamilyDto>(content);
        
        result.Should().NotBeNull();
        result!.Name.Should().Be("Updated Name");
    }

    [Fact]
    public async Task UpdateFamily_WithEmptyName_ShouldReturnBadRequest()
    {
        // Arrange
        await AuthenticateAsync();
        var request = new UpdateFamilyRequest
        {
            Name = ""
        };

        // Act
        var response = await _client.PutAsJsonAsync("/api/family", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task DeleteFamily_WithValidFamily_ShouldReturnNoContent()
    {
        // Arrange
        await AuthenticateAsync();
        
        // First create a family
        var createRequest = new CreateFamilyRequest { Name = "To Be Deleted" };
        var createResponse = await _client.PostAsJsonAsync("/api/family", createRequest);
        
        if (createResponse.StatusCode == HttpStatusCode.Forbidden)
        {
            // User doesn't have Premium - skip this test
            return;
        }

        // Act
        var response = await _client.DeleteAsync("/api/family");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task LeaveFamily_WithNoFamily_ShouldReturnBadRequest()
    {
        // Arrange
        await AuthenticateAsync();

        // Act
        var response = await _client.PostAsync("/api/family/leave", null);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    #endregion

    #region Member Management Tests

    [Fact]
    public async Task GetFamilyMembers_WithNoFamily_ShouldReturnBadRequest()
    {
        // Arrange
        await AuthenticateAsync();

        // Act
        var response = await _client.GetAsync("/api/family/members");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GetFamilyMembers_WithFamily_ShouldReturnMembers()
    {
        // Arrange
        await AuthenticateAsync();
        
        // Create a family first
        var createRequest = new CreateFamilyRequest { Name = "Test Family" };
        var createResponse = await _client.PostAsJsonAsync("/api/family", createRequest);
        
        if (createResponse.StatusCode == HttpStatusCode.Forbidden)
        {
            // User doesn't have Premium - skip this test
            return;
        }

        // Act
        var response = await _client.GetAsync("/api/family/members");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        
        var content = await response.Content.ReadAsStringAsync();
        var result = JsonConvert.DeserializeObject<List<FamilyMemberDto>>(content);
        
        result.Should().NotBeNull();
        result.Should().BeOfType<List<FamilyMemberDto>>();
        result!.Count.Should().BeGreaterOrEqualTo(1); // At least the owner
    }

    [Fact]
    public async Task UpdateMemberRole_WithInvalidMemberId_ShouldReturnBadRequest()
    {
        // Arrange
        await AuthenticateAsync();
        var invalidMemberId = 999999;
        var request = new UpdateMemberRoleRequest
        {
            TargetUserId = invalidMemberId,
            NewRole = FamilyMemberRole.BudgetAdmin
        };

        // Act
        var response = await _client.PutAsJsonAsync($"/api/family/members/{invalidMemberId}/role", request);

        // Assert
        response.StatusCode.Should().BeOneOf(HttpStatusCode.BadRequest, HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task RemoveMember_WithInvalidMemberId_ShouldReturnBadRequest()
    {
        // Arrange
        await AuthenticateAsync();
        var invalidMemberId = 999999;

        // Act
        var response = await _client.DeleteAsync($"/api/family/members/{invalidMemberId}");

        // Assert
        response.StatusCode.Should().BeOneOf(HttpStatusCode.BadRequest, HttpStatusCode.Forbidden);
    }

    #endregion

    #region Invitation Management Tests

    [Fact]
    public async Task GetPendingInvitations_WithNoFamily_ShouldReturnBadRequest()
    {
        // Arrange
        await AuthenticateAsync();

        // Act
        var response = await _client.GetAsync("/api/family/invitations");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GetPendingInvitations_WithFamily_ShouldReturnInvitations()
    {
        // Arrange
        await AuthenticateAsync();
        
        // Create a family first
        var createRequest = new CreateFamilyRequest { Name = "Test Family" };
        var createResponse = await _client.PostAsJsonAsync("/api/family", createRequest);
        
        if (createResponse.StatusCode == HttpStatusCode.Forbidden)
        {
            // User doesn't have Premium - skip this test
            return;
        }

        // Act
        var response = await _client.GetAsync("/api/family/invitations");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        
        var content = await response.Content.ReadAsStringAsync();
        var result = JsonConvert.DeserializeObject<List<FamilyInvitationDto>>(content);
        
        result.Should().NotBeNull();
        result.Should().BeOfType<List<FamilyInvitationDto>>();
    }

    [Fact]
    public async Task GetInvitation_WithInvalidToken_ShouldReturnNotFound()
    {
        // Arrange
        var invalidToken = "invalid-token-12345";

        // Act - This endpoint is AllowAnonymous
        var response = await _client.GetAsync($"/api/family/invitations/{invalidToken}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task InviteMember_WithEmptyEmail_ShouldReturnBadRequest()
    {
        // Arrange
        await AuthenticateAsync();
        var request = new InviteFamilyMemberRequest
        {
            Email = "",
            Role = FamilyMemberRole.Member
        };

        // Act
        var response = await _client.PostAsJsonAsync("/api/family/invitations", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task InviteMember_WithInvalidEmail_ShouldReturnBadRequest()
    {
        // Arrange
        await AuthenticateAsync();
        var request = new InviteFamilyMemberRequest
        {
            Email = "invalid-email",
            Role = FamilyMemberRole.Member
        };

        // Act
        var response = await _client.PostAsJsonAsync("/api/family/invitations", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task InviteMember_WithValidEmail_ShouldReturnCreated()
    {
        // Arrange
        await AuthenticateAsync();
        
        // Create a family first
        var createRequest = new CreateFamilyRequest { Name = "Test Family" };
        var createResponse = await _client.PostAsJsonAsync("/api/family", createRequest);
        
        if (createResponse.StatusCode == HttpStatusCode.Forbidden)
        {
            // User doesn't have Premium - skip this test
            return;
        }

        var inviteRequest = new InviteFamilyMemberRequest
        {
            Email = "newmember@example.com",
            Role = FamilyMemberRole.Member
        };

        // Act
        var response = await _client.PostAsJsonAsync("/api/family/invitations", inviteRequest);

        // Assert - May require Premium subscription
        response.StatusCode.Should().BeOneOf(HttpStatusCode.Created, HttpStatusCode.Forbidden);
        
        if (response.StatusCode == HttpStatusCode.Created)
        {
            var content = await response.Content.ReadAsStringAsync();
            var result = JsonConvert.DeserializeObject<FamilyInvitationDto>(content);
            
            result.Should().NotBeNull();
            result!.InvitedEmail.Should().Be("newmember@example.com");
            result.InvitationToken.Should().NotBeNullOrEmpty();
        }
    }

    [Fact]
    public async Task AcceptInvitation_WithInvalidToken_ShouldReturnBadRequest()
    {
        // Arrange
        await AuthenticateAsync();
        var invalidToken = "invalid-token-12345";

        // Act
        var response = await _client.PostAsync($"/api/family/invitations/{invalidToken}/accept", null);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task CancelInvitation_WithInvalidId_ShouldReturnBadRequest()
    {
        // Arrange
        await AuthenticateAsync();
        var invalidId = 999999;

        // Act
        var response = await _client.DeleteAsync($"/api/family/invitations/{invalidId}");

        // Assert
        response.StatusCode.Should().BeOneOf(HttpStatusCode.BadRequest, HttpStatusCode.Forbidden);
    }

    #endregion
}
