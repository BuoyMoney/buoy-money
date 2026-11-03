import 'package:buoy_ai/models/family_models.dart';
import 'package:flutter_test/flutter_test.dart';

void main() {
  Map<String, dynamic> invitationJson({Object? isAccepted}) => {
        'id': 7,
        'familyName': 'Test Family',
        'invitedEmail': 'member@example.com',
        'invitationToken': 'abc',
        'invitedRole': 2,
        'expiresAt': '2030-01-01T00:00:00Z',
        'createdAt': '2029-12-25T00:00:00Z',
        if (isAccepted != null) 'isAccepted': isAccepted,
      };

  test('a used invitation is read as accepted', () {
    final invitation =
        FamilyInvitation.fromJson(invitationJson(isAccepted: true));
    expect(invitation.isAccepted, isTrue);
  });

  test('a pending invitation, or a server without the field, is not accepted',
      () {
    expect(
      FamilyInvitation.fromJson(invitationJson(isAccepted: false)).isAccepted,
      isFalse,
    );
    expect(FamilyInvitation.fromJson(invitationJson()).isAccepted, isFalse);
  });
}
