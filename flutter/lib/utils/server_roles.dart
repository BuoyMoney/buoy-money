import 'package:flutter/widgets.dart';
import 'package:provider/provider.dart';

import '../models/auth_models.dart';
import '../models/billing_models.dart';
import '../providers/auth_provider.dart';

/// True for the server's administrator. On a self-hosted server that is its owner (the first account); on the
/// hosted service it is the staff allowlist. Both arrive as the Admin tier.
bool isServerAdminUser(User? user) =>
    user != null &&
    SubscriptionTierExtension.fromString(user.subscriptionTier) == SubscriptionTier.admin;

/// Whether the signed-in person is the server's administrator, or null when that is not known yet: there is no
/// [AuthProvider] above [context] (a widget test that does not care), or the session is still being restored
/// and there is no user. Callers pick the safe default for their screen, so an owner is not shown the member
/// view for the moment before their account loads.
bool? serverAdminOf(BuildContext context, {bool listen = false}) {
  try {
    final user = Provider.of<AuthProvider>(context, listen: listen).user;
    return user == null ? null : isServerAdminUser(user);
  } on ProviderNotFoundException {
    return null;
  }
}
