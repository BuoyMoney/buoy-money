import '../models/family_models.dart';
import 'api_client.dart';

/// Family Service
/// 
/// Handles all family-related API calls.
class FamilyService {
  /// Get current user's family
  Future<Family?> getFamily() async {
    try {
      final response = await apiClient.get<Map<String, dynamic>?>('/family');
      if (response == null) return null;
      return Family.fromJson(response);
    } catch (e) {
      // 404 is expected when user is not in a family
      if (e.toString().contains('404')) {
        return null;
      }
      rethrow;
    }
  }

  /// Create a new family (user becomes Owner)
  Future<Family> createFamily(CreateFamilyRequest request) async {
    final response = await apiClient.post<Map<String, dynamic>>(
      '/family',
      data: request.toJson(),
    );
    return Family.fromJson(response);
  }

  /// Update family name (Owner only)
  Future<Family> updateFamily(String name) async {
    final response = await apiClient.put<Map<String, dynamic>>(
      '/family',
      data: {'name': name},
    );
    return Family.fromJson(response);
  }

  /// Delete family (Owner only, cascade deletes)
  Future<void> deleteFamily() async {
    await apiClient.delete<void>('/family');
  }

  /// Leave family (non-Owner members)
  Future<void> leaveFamily() async {
    await apiClient.post<void>('/family/leave');
  }

  /// Get all family members
  Future<List<FamilyMember>> getFamilyMembers() async {
    final response = await apiClient.get<List<dynamic>>('/family/members');
    return response.map((json) => FamilyMember.fromJson(json as Map<String, dynamic>)).toList();
  }

  /// Update member role (Owner only)
  Future<FamilyMember> updateMemberRole(int userId, FamilyMemberRole role) async {
    final response = await apiClient.put<Map<String, dynamic>>(
      '/family/members/$userId/role',
      // Backend binds UpdateMemberRoleRequest.NewRole; the JSON key MUST be
      // 'newRole'. Sending 'role' left NewRole defaulting to 0 (Owner), silently
      // promoting every member on any role change.
      data: {'newRole': role.value},
    );
    return FamilyMember.fromJson(response);
  }

  /// Remove member from family (Owner only)
  Future<void> removeMember(int userId) async {
    await apiClient.delete<void>('/family/members/$userId');
  }

  /// Get pending invitations (Owner/BudgetAdmin)
  Future<List<FamilyInvitation>> getInvitations() async {
    try {
      final response = await apiClient.get<List<dynamic>>('/family/invitations');
      return response.map((json) => FamilyInvitation.fromJson(json as Map<String, dynamic>)).toList();
    } catch (e) {
      // Non-owners may not have access
      return [];
    }
  }

  /// Invite a new member (Owner/BudgetAdmin)
  Future<FamilyInvitation> inviteMember(InviteFamilyMemberRequest request) async {
    final response = await apiClient.post<Map<String, dynamic>>(
      '/family/invitations',
      data: request.toJson(),
    );
    return FamilyInvitation.fromJson(response);
  }

  /// Cancel invitation (Owner/BudgetAdmin)
  Future<void> cancelInvitation(int invitationId) async {
    await apiClient.delete<void>('/family/invitations/$invitationId');
  }

  /// Get invitation details by token (public endpoint)
  Future<FamilyInvitation> getInvitationByToken(String token) async {
    final response = await apiClient.get<Map<String, dynamic>>(
      '/family/invitations/$token',
    );
    return FamilyInvitation.fromJson(response);
  }

  /// Accept an invitation and join the family
  Future<FamilyMember> acceptInvitation(String token) async {
    final response = await apiClient.post<Map<String, dynamic>>(
      '/family/invitations/$token/accept',
    );
    return FamilyMember.fromJson(response);
  }

  /// Decline/reject an invitation (deletes it)
  /// Note: This requires the user to be authenticated
  Future<void> declineInvitation(String token) async {
    await apiClient.delete<void>('/family/invitations/$token/decline');
  }

  /// Get pending invitations addressed to the current user's email
  Future<List<FamilyInvitation>> getMyPendingInvitations() async {
    try {
      final response = await apiClient.get<List<dynamic>>('/family/my-invitations');
      return response.map((json) => FamilyInvitation.fromJson(json as Map<String, dynamic>)).toList();
    } catch (_) {
      return [];
    }
  }

  /// Helper: Check if role can manage members
  bool canManageMembers(FamilyMemberRole role) {
    return role == FamilyMemberRole.owner;
  }

  /// Helper: Check if role can invite members
  bool canInviteMembers(FamilyMemberRole role) {
    return role == FamilyMemberRole.owner || role == FamilyMemberRole.budgetAdmin;
  }
}

/// Singleton instance
final familyService = FamilyService();
