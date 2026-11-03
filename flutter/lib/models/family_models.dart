// Family Models
// 
// Models for family management features.

/// Family member role enum
enum FamilyMemberRole {
  owner(0, 'Owner', 'Full control over family settings, members, and billing'),
  budgetAdmin(1, 'Budget Admin', 'Can edit family budgets and goals, and link bank accounts'),
  member(2, 'Member', 'Can view everything and edit their own budgets. Cannot change family budgets or link accounts'),
  readOnly(3, 'Read Only', 'Can view budgets and financial data only');

  final int value;
  final String displayName;
  final String description;

  const FamilyMemberRole(this.value, this.displayName, this.description);

  static FamilyMemberRole fromValue(int value) {
    return FamilyMemberRole.values.firstWhere(
      (role) => role.value == value,
      orElse: () => FamilyMemberRole.member,
    );
  }
}

/// Family model
class Family {
  final int id;
  final String name;
  final DateTime createdAt;
  final int memberCount;
  final int ownerUserId;

  const Family({
    required this.id,
    required this.name,
    required this.createdAt,
    required this.memberCount,
    required this.ownerUserId,
  });

  factory Family.fromJson(Map<String, dynamic> json) {
    return Family(
      id: int.tryParse((json['id'] ?? json['Id']).toString()) ?? 0,
      name: (json['name'] ?? json['Name'] ?? '').toString(),
      createdAt: DateTime.tryParse((json['createdAt'] ?? json['CreatedAt'] ?? '').toString()) ?? DateTime.now(),
      memberCount: int.tryParse((json['memberCount'] ?? json['MemberCount']).toString()) ?? 0,
      ownerUserId: int.tryParse((json['ownerUserId'] ?? json['OwnerUserId']).toString()) ?? 0,
    );
  }
}

/// Family member model
class FamilyMember {
  final int userId;
  final String email;
  final String? firstName;
  final String? lastName;
  final FamilyMemberRole role;
  final DateTime joinedAt;
  final bool isCurrentUser;

  const FamilyMember({
    required this.userId,
    required this.email,
    this.firstName,
    this.lastName,
    required this.role,
    required this.joinedAt,
    this.isCurrentUser = false,
  });

  String get displayName {
    if (firstName != null && lastName != null) {
      return '$firstName $lastName';
    } else if (firstName != null) {
      return firstName!;
    } else if (lastName != null) {
      return lastName!;
    }
    return email;
  }

  factory FamilyMember.fromJson(Map<String, dynamic> json) {
    return FamilyMember(
      userId: int.tryParse((json['userId'] ?? json['UserId']).toString()) ?? 0,
      email: (json['email'] ?? json['Email'] ?? '').toString(),
      firstName: (json['firstName'] as String?) ?? (json['FirstName'] as String?),
      lastName: (json['lastName'] as String?) ?? (json['LastName'] as String?),
      role: FamilyMemberRole.fromValue(int.tryParse((json['role'] ?? json['Role']).toString()) ?? 2),
      joinedAt: DateTime.tryParse((json['joinedAt'] ?? json['JoinedAt'] ?? '').toString()) ?? DateTime.now(),
      isCurrentUser: (json['isCurrentUser'] ?? json['IsCurrentUser']) == true,
    );
  }
}

/// Family invitation model
class FamilyInvitation {
  final int id;
  final String email;
  final FamilyMemberRole invitedRole;
  final String token;
  final DateTime expiresAt;
  final DateTime createdAt;
  final String familyName;
  final String invitedByName;

  /// True once someone has used this invitation to join (the server returns used
  /// invitations by token so the invite page can say so instead of "invalid").
  final bool isAccepted;

  const FamilyInvitation({
    required this.id,
    required this.email,
    required this.invitedRole,
    required this.token,
    required this.expiresAt,
    required this.createdAt,
    required this.familyName,
    required this.invitedByName,
    this.isAccepted = false,
  });

  bool get isExpired => DateTime.now().isAfter(expiresAt);

  factory FamilyInvitation.fromJson(Map<String, dynamic> json) {
    return FamilyInvitation(
      id: int.tryParse((json['id'] ?? json['Id']).toString()) ?? 0,
      email: (json['email'] ?? json['Email'] ?? json['invitedEmail'] ?? json['InvitedEmail'] ?? '').toString(),
      invitedRole: FamilyMemberRole.fromValue(int.tryParse((json['invitedRole'] ?? json['InvitedRole']).toString()) ?? 2),
      token: (json['token'] ?? json['Token'] ?? json['invitationToken'] ?? json['InvitationToken'] ?? '').toString(),
      expiresAt: DateTime.tryParse((json['expiresAt'] ?? json['ExpiresAt'] ?? '').toString()) ?? DateTime.now(),
      createdAt: DateTime.tryParse((json['createdAt'] ?? json['CreatedAt'] ?? '').toString()) ?? DateTime.now(),
      familyName: (json['familyName'] ?? json['FamilyName'] ?? '').toString(),
      invitedByName: (json['invitedByName'] ?? json['InvitedByName'] ?? json['invitedByUserName'] ?? json['InvitedByUserName'] ?? '').toString(),
      isAccepted: (json['isAccepted'] ?? json['IsAccepted']) == true,
    );
  }
}

/// Create family request
class CreateFamilyRequest {
  final String name;

  const CreateFamilyRequest({required this.name});

  Map<String, dynamic> toJson() => {'name': name};
}

/// Invite family member request
class InviteFamilyMemberRequest {
  final String email;
  final FamilyMemberRole role;

  const InviteFamilyMemberRequest({
    required this.email,
    required this.role,
  });

  Map<String, dynamic> toJson() => {
    'email': email,
    'role': role.value,
  };
}

/// Update member role request
class UpdateMemberRoleRequest {
  final FamilyMemberRole role;

  const UpdateMemberRoleRequest({required this.role});

  Map<String, dynamic> toJson() => {'newRole': role.value};
}
