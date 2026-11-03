import 'package:buoy_ai/models/auth_models.dart';
import 'package:buoy_ai/providers/auth_provider.dart';
import 'package:buoy_ai/utils/server_roles.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:provider/provider.dart';

class _FakeAuth extends AuthProvider {
  _FakeAuth(this._fake);
  User? _fake;

  @override
  User? get user => _fake;

  void signIn(User user) {
    _fake = user;
    notifyListeners();
  }
}

User _user(String tier) => User(
      id: '1',
      email: 'a@b.c',
      firstName: 'A',
      lastName: 'B',
      subscriptionTier: tier,
      createdAt: DateTime(2026),
    );

/// "Is this person the server's admin?" must say "unknown" (null) until the account has loaded, so a screen's
/// own default applies instead of briefly treating the owner as a member.
void main() {
  testWidgets('null with no AuthProvider above', (tester) async {
    bool? seen = true;
    await tester.pumpWidget(Builder(builder: (context) {
      seen = serverAdminOf(context);
      return const SizedBox();
    }));
    expect(seen, isNull);
  });

  testWidgets('null while the session is still loading, then true/false once the user arrives',
      (tester) async {
    final auth = _FakeAuth(null);
    bool? seen = true;
    await tester.pumpWidget(ChangeNotifierProvider<AuthProvider>.value(
      value: auth,
      child: Builder(builder: (context) {
        seen = serverAdminOf(context, listen: true);
        return const SizedBox();
      }),
    ));
    expect(seen, isNull, reason: 'no user yet: unknown, not "member"');

    auth.signIn(_user('admin'));
    await tester.pump();
    expect(seen, isTrue);

    auth.signIn(_user('standard'));
    await tester.pump();
    expect(seen, isFalse);
  });
}
