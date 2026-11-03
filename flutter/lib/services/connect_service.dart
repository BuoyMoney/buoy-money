import 'api_client.dart';

/// The consent request shown on the `/connect` OAuth page (RFC #782).
///
/// Populated from `GET /api/oauth/request/{id}`. Matches the backend
/// `ConsentRequestDto`; `fromJson` tolerates both camelCase and PascalCase keys
/// since the backend serializes both during the transition.
class ConsentRequest {
  /// Human name of the connecting client (e.g. "ChatGPT", "Claude").
  final String clientName;

  /// The client's homepage / origin, shown as trust context. Nullable —
  /// CIMD clients may omit it.
  final String? clientUri;

  /// Optional client logo URL (not required for the v1 layout).
  final String? logoUri;

  /// Raw scope identifiers requested (e.g. `["buoyai:read"]`).
  final List<String> scopes;

  /// Human-readable descriptions of what each scope grants, shown as bullets.
  final List<String> scopeDescriptions;

  /// When this request expires (10-min window). Past this the backend 404s.
  final DateTime expiresAt;

  const ConsentRequest({
    required this.clientName,
    this.clientUri,
    this.logoUri,
    required this.scopes,
    required this.scopeDescriptions,
    required this.expiresAt,
  });

  static List<String> _stringList(dynamic value) {
    if (value is List) {
      return value.map((e) => e.toString()).toList();
    }
    return const <String>[];
  }

  factory ConsentRequest.fromJson(Map<String, dynamic> json) {
    return ConsentRequest(
      clientName:
          (json['clientName'] ?? json['ClientName'] ?? '').toString(),
      clientUri: (json['clientUri'] ?? json['ClientUri'])?.toString(),
      logoUri: (json['logoUri'] ?? json['LogoUri'])?.toString(),
      scopes: _stringList(json['scopes'] ?? json['Scopes']),
      scopeDescriptions: _stringList(
          json['scopeDescriptions'] ?? json['ScopeDescriptions']),
      expiresAt: DateTime.tryParse(
            (json['expiresAt'] ?? json['ExpiresAt'] ?? '').toString(),
          ) ??
          DateTime.now(),
    );
  }
}

/// A personal access token for AI tools on a self-hosted server (Open WebUI and the like).
/// The secret itself is shown once, at creation ([CreatedConnectToken]); the list never has it.
class ConnectToken {
  final String id;
  final String name;
  final DateTime? createdAt;
  final DateTime? expiresAt;
  final DateTime? lastUsedAt;

  const ConnectToken({
    required this.id,
    required this.name,
    this.createdAt,
    this.expiresAt,
    this.lastUsedAt,
  });

  static DateTime? _date(dynamic value) =>
      value == null ? null : DateTime.tryParse(value.toString())?.toLocal();

  factory ConnectToken.fromJson(Map<String, dynamic> json) => ConnectToken(
        id: (json['id'] ?? json['Id'] ?? '').toString(),
        name: (json['name'] ?? json['Name'] ?? '').toString(),
        createdAt: _date(json['createdAt'] ?? json['CreatedAt']),
        expiresAt: _date(json['expiresAt'] ?? json['ExpiresAt']),
        lastUsedAt: _date(json['lastUsedAt'] ?? json['LastUsedAt']),
      );
}

/// The answer to creating a token: the token row plus the secret and the MCP address.
class CreatedConnectToken {
  final ConnectToken info;
  final String token;
  final String mcpUrl;

  const CreatedConnectToken({required this.info, required this.token, required this.mcpUrl});

  factory CreatedConnectToken.fromJson(Map<String, dynamic> json) => CreatedConnectToken(
        info: ConnectToken.fromJson(json),
        token: (json['token'] ?? json['Token'] ?? '').toString(),
        mcpUrl: (json['mcpUrl'] ?? json['McpUrl'] ?? '').toString(),
      );
}

/// Service for the "BuoyAI Connect" OAuth consent flow (RFC #782).
///
/// Talks to the two first-party (`[Authorize]`) OAuth request endpoints the
/// consent page needs; the browser-facing `/authorize` + `/token` legs are
/// driven by the assistant, not this app.
class ConnectService {
  /// Load the pending consent request for the `/connect` page.
  ///
  /// Throws a [DioException] with 404 when the request has expired, been
  /// consumed, or already decided — the page maps that to the "expired" state.
  Future<ConsentRequest> getConsentRequest(String id) async {
    final data =
        await apiClient.get<Map<String, dynamic>>('/oauth/request/$id');
    return ConsentRequest.fromJson(data);
  }

  /// Approve or deny the request. Returns the assistant `redirect_uri` (with the
  /// authorization `code`+`state`, or `error=access_denied`) that the browser
  /// must be sent to next. Tolerates camelCase / PascalCase result keys.
  Future<String> decide(String id, bool approve) async {
    final data = await apiClient.post<Map<String, dynamic>>(
      '/oauth/request/$id/decision',
      data: {'approve': approve},
    );
    return (data['redirectUrl'] ?? data['RedirectUrl'] ?? '').toString();
  }

  /// Access tokens on a self-hosted server. Throws a [DioException] with 404 when the server is
  /// hosted or has tools for AI assistants turned off.
  Future<List<ConnectToken>> listTokens() async {
    final data = await apiClient.get<List<dynamic>>('/connect/tokens');
    return data
        .whereType<Map>()
        .map((e) => ConnectToken.fromJson(Map<String, dynamic>.from(e)))
        .toList();
  }

  /// Makes a new token. 400 for an empty name, 409 when there are too many.
  Future<CreatedConnectToken> createToken(String name) async {
    final data = await apiClient.post<Map<String, dynamic>>(
      '/connect/tokens',
      data: {'name': name},
    );
    return CreatedConnectToken.fromJson(data);
  }

  /// Revokes a token. A 404 means it is already gone.
  Future<void> revokeToken(String id) async {
    await apiClient.delete<dynamic>('/connect/tokens/${Uri.encodeComponent(id)}');
  }
}

/// Global singleton.
///
/// Not `final` only so widget/unit tests can inject a fake (there is no DI
/// container); production code never reassigns it. Mirrors `accountsService`.
ConnectService connectService = ConnectService();
