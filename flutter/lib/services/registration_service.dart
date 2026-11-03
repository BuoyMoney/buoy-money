import 'api_client.dart';

/// Who may sign up on a self-hosted server (`GET /api/registration/status`).
class RegistrationStatus {
  const RegistrationStatus({
    required this.firstAccount,
    required this.open,
    required this.requiresInvite,
  });

  /// No account exists yet: the person signing up becomes the server's admin.
  final bool firstAccount;

  /// An admin opened registration to everyone.
  final bool open;

  /// A stranger needs an invite code from an admin.
  final bool requiresInvite;

  factory RegistrationStatus.fromJson(Map<String, dynamic> json) => RegistrationStatus(
        firstAccount: json['firstAccount'] == true,
        open: json['open'] == true,
        requiresInvite: json['requiresInvite'] == true,
      );
}

class RegistrationService {
  /// Null when the server does not answer (hosted answers 404): the sign-up form then behaves as
  /// it always did.
  Future<RegistrationStatus?> getStatus() async {
    try {
      final response = await apiClient.get<Map<String, dynamic>>('/registration/status');
      return RegistrationStatus.fromJson(response);
    } catch (_) {
      return null;
    }
  }
}

/// Swapped by tests, like the other global services.
RegistrationService registrationService = RegistrationService();
