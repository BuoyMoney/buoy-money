import 'package:dio/dio.dart';
import 'package:flutter/foundation.dart'
    show kIsWeb, defaultTargetPlatform, TargetPlatform;
import '../models/dashboard_models.dart';
import '../config/server_capabilities.dart';
import '../offline/network_error.dart';
import 'api_client.dart';

/// Plaid Item status enum — matches backend PlaidItemStatus
enum PlaidItemStatus {
  active,       // 0
  loginRequired, // 1
  pendingExpiration, // 2
  pendingDisconnect, // 3
  disconnected, // 4
  error,        // 5
}

/// Account (Plaid Connection) model — matches backend AccountConnectionDto
class PlaidConnection {
  final int id;
  final String institutionId;
  final String institutionName;
  final String? institutionLogo;

  /// Which bank provider the connection came from: 'Plaid', 'SimpleFin' or 'Bridge'. Reconnecting differs.
  final String provider;
  final List<Account> accounts;
  final bool isActive;
  /// The bank needs a new sign-in; the connection is still active (see _needsAttention).
  final bool requiresUserAction;
  final DateTime lastSyncAt;
  final int totalTransactions;
  final int? ownerUserId;
  final String? ownerName;
  final DateTime? createdAt;

  /// Connected through SimpleFIN: fixed with a new setup token, not Plaid Link.
  bool get isSimpleFin => provider == 'SimpleFin';

  /// Connected through the BuoyAI Cloud bank feed: signed in again on the Cloud's own page, not Plaid Link.
  bool get isBridge => provider == 'Bridge';

  PlaidConnection({
    required this.id,
    required this.institutionId,
    required this.institutionName,
    this.institutionLogo,
    this.provider = 'Plaid',
    required this.accounts,
    this.isActive = true,
    this.requiresUserAction = false,
    required this.lastSyncAt,
    this.totalTransactions = 0,
    this.ownerUserId,
    this.ownerName,
    this.createdAt,
  });

  factory PlaidConnection.fromJson(Map<String, dynamic> json) {
    return PlaidConnection(
      id: (json['id'] ?? json['Id'] ?? 0) as int,
      institutionId: (json['institutionId'] ?? json['InstitutionId'] ?? '').toString(),
      institutionName: (json['institutionName'] ?? json['InstitutionName'] ?? '').toString(),
      institutionLogo: json['institutionLogo']?.toString() ?? json['InstitutionLogo']?.toString(),
      provider: (json['provider'] ?? json['Provider'] ?? 'Plaid').toString(),
      // NOTE the parentheses around the ?? — `as` binds tighter than `??`, so
      // `a ?? b as List<dynamic>?` casts only `b`. When the backend sends the
      // camelCase `accounts` key, the receiver of `.map` was then a raw
      // `dynamic`, so `.map(...).toList()` produced a `List<dynamic>` that blew
      // up assigning to this `List<Account>` field — but ONLY on AOT (mobile);
      // web's dart2js elides the cast. That was the "accounts won't load on
      // Android" crash. Cast the coalesced value as a whole so `.map` infers
      // `map<Account>`.
      accounts: ((json['accounts'] ?? json['Accounts']) as List<dynamic>?)
          ?.map((e) => Account.fromJson(e as Map<String, dynamic>))
          .toList() ??
          <Account>[],
      isActive: (json['isActive'] ?? json['IsActive'] ?? true) as bool,
      requiresUserAction: (json['requiresUserAction'] ?? json['RequiresUserAction'] ?? false) as bool,
      lastSyncAt: DateTime.tryParse(
        (json['lastSyncAt'] ?? json['LastSyncAt'] ?? json['lastSync'] ?? '').toString(),
      ) ?? DateTime.now(),
      totalTransactions: (json['totalTransactions'] ?? json['TotalTransactions'] ?? 0) as int,
      ownerUserId: json['ownerUserId'] as int? ?? json['OwnerUserId'] as int?,
      ownerName: json['ownerName']?.toString() ?? json['OwnerName']?.toString(),
      createdAt: json['createdAt'] != null
          ? DateTime.tryParse(json['createdAt'].toString())
          : null,
    );
  }
}

/// Import progress — matches backend ImportProgressDto
class ImportProgress {
  final int connectionId;
  final String status; // Pending, InProgress, Completed, Failed, QuickSyncComplete
  final int progress; // 0-100
  final String? message;
  final DateTime? startedAt;
  final DateTime? completedAt;
  final DateTime? quickSyncCompletedAt;
  final bool canDismiss;

  ImportProgress({
    required this.connectionId,
    required this.status,
    required this.progress,
    this.message,
    this.startedAt,
    this.completedAt,
    this.quickSyncCompletedAt,
    this.canDismiss = false,
  });

  bool get isActive => status == 'Pending' || status == 'InProgress';

  factory ImportProgress.fromJson(Map<String, dynamic> json) {
    return ImportProgress(
      connectionId: (json['connectionId'] ?? json['ConnectionId'] ?? 0) as int,
      status: (json['status'] ?? json['Status'] ?? 'Completed').toString(),
      progress: (json['progress'] ?? json['Progress'] ?? 0) as int,
      message: json['message']?.toString() ?? json['Message']?.toString(),
      startedAt: json['startedAt'] != null ? DateTime.tryParse(json['startedAt'].toString()) : null,
      completedAt: json['completedAt'] != null ? DateTime.tryParse(json['completedAt'].toString()) : null,
      quickSyncCompletedAt: json['quickSyncCompletedAt'] != null
          ? DateTime.tryParse(json['quickSyncCompletedAt'].toString())
          : null,
      canDismiss: (json['canDismiss'] ?? json['CanDismiss'] ?? false) as bool,
    );
  }
}

/// Account Service
/// 
/// Handles all Plaid account and connection-related API calls.
/// The server refused a Plaid operation because the user has no phishing-resistant
/// MFA (TOTP or passkey). Typed so the UI can open the inline enrollment sheet
/// instead of showing a dead-end error toast (this gate accounted for every real
/// user's failed bank connection in Aug 2026).
class BankMfaRequiredException implements Exception {
  const BankMfaRequiredException();

  @override
  String toString() =>
      'Two-factor authentication (authenticator app or passkey) is required before connecting a bank.';
}

/// True when [e] means "this server has no such route" (an older server), as opposed to a real answer such as
/// "that account does not exist". Our own 404s carry a `message`; a missing route has no body.
bool _isRouteMissing(DioException e) {
  if (e.response?.statusCode != 404) return false;
  final data = e.response?.data;
  return !(data is Map && data['message'] != null);
}

/// Runs [current] (a provider-neutral `/bank` route, served in every edition). Only if an older server does not
/// have that route does it run [legacy], the Plaid route that older servers answer.
Future<T> _currentOrLegacy<T>(Future<T> Function() current, Future<T> Function() legacy) async {
  try {
    return await current();
  } on DioException catch (e) {
    if (_isRouteMissing(e)) return legacy();
    rethrow;
  }
}

/// The user's accounts as the server sent them (a JSON list). Shared by the Accounts page, the dashboard and the
/// transaction filters, which all need the list and none of which should care which bank provider it came from.
///
/// Before this read moved to `/bank/accounts`, the offline cache held the list under `/plaid/accounts`. A phone
/// that upgraded and is offline (or sees a 5xx) before one online load has no `/bank/accounts` cache entry, so
/// when that read fails for those reasons the old entry is tried before giving up.
Future<List<dynamic>> fetchAccountsJson(ApiClient client) async {
  try {
    return await _currentOrLegacy(
      () => client.get<List<dynamic>>('/bank/accounts'),
      () => client.get<List<dynamic>>('/plaid/accounts'),
    );
  } on DioException catch (e) {
    final unreachable = isNetworkError(e) || (e.response?.statusCode ?? 0) >= 500;
    if (!unreachable) rethrow;
    try {
      return await client.get<List<dynamic>>('/plaid/accounts');
    } catch (_) {
      throw e;
    }
  }
}

class AccountsService {
  /// Get all connected accounts
  Future<List<Account>> getAccounts() async {
    final response = await fetchAccountsJson(apiClient);
    return response.map((e) => Account.fromJson(e as Map<String, dynamic>)).toList();
  }

  /// Get all bank connections, whichever provider they came from (Plaid, SimpleFIN or the Cloud bank feed).
  Future<List<PlaidConnection>> getConnections() async {
    final response = await _currentOrLegacy<List<dynamic>>(
      () => apiClient.get<List<dynamic>>('/bank/connections'),
      // An older self-hosted server has no connections route at all (its Plaid routes answer 404).
      () async {
        if (!ServerEdition.current.value.hasBankConnection) return <dynamic>[];
        return apiClient.get<List<dynamic>>('/plaid/connections');
      },
    );
    return response.map((e) => PlaidConnection.fromJson(e as Map<String, dynamic>)).toList();
  }

  /// What this server can connect a bank with right now: any of 'Plaid', 'SimpleFin', 'Bridge' (the Cloud bank
  /// feed, listed once the server is paired). An older server only has the plain provider list, and its
  /// answer cannot say whether the Cloud feed is paired, so it never offers that.
  Future<List<String>> getBankProviders() async {
    try {
      final response = await apiClient.get<List<dynamic>>('/bank/providers');
      return response.map((e) => e.toString()).toList();
    } on DioException catch (e) {
      if (!_isRouteMissing(e)) rethrow;
    }
    final response = await apiClient.get<List<dynamic>>('/aggregators');
    return response.map((e) => e.toString()).where((p) => p != 'Bridge').toList();
  }

  /// Starts a Cloud bank feed connection. Returns the Cloud page to open, or null when the server gave none; finish
  /// it with [completeBridgeReconnect] once the person says they are done (the same call ends a first connect).
  Future<String?> startBridgeConnect() async {
    final response = await apiClient.post<Map<String, dynamic>>(
      '/aggregators/Bridge/connect/start',
      data: <String, dynamic>{},
    );
    final value = (response['value'] ?? response['Value'])?.toString();
    return (value == null || value.isEmpty) ? null : value;
  }

  /// Platform hint sent to the backend so it can attach platform-specific OAuth
  /// params to the Plaid link token (e.g. android_package_name on Android).
  static String _plaidPlatform() {
    if (kIsWeb) return 'web';
    switch (defaultTargetPlatform) {
      case TargetPlatform.android:
        return 'android';
      case TargetPlatform.iOS:
        return 'ios';
      default:
        return 'other';
    }
  }

  /// Create Plaid Link token (for adding new bank connection).
  /// Throws [BankMfaRequiredException] when the server's phishing-resistant MFA
  /// gate (TOTP or passkey) blocks the request; other failures throw normally.
  Future<String> createLinkToken() async {
    try {
      final response = await apiClient.post<Map<String, dynamic>>(
        '/plaid/create-link-token',
        // Tell the backend the platform so it can attach the right OAuth params to the
        // link token (android_package_name on Android, so OAuth banks like Chase can
        // redirect back into the app).
        data: {'platform': _plaidPlatform()},
      );
      return response['linkToken'] as String;
    } on DioException catch (e) {
      final data = e.response?.data;
      if (data is Map && data['requiresMfa'] == true) {
        throw const BankMfaRequiredException();
      }
      if (e.response?.statusCode == 403 && data is Map && data['message'] != null) {
        throw Exception(data['message']);
      }
      rethrow;
    }
  }

  /// Exchange public token for access token (after Plaid Link success)
  Future<void> exchangePublicToken(String publicToken, String institutionId) async {
    await apiClient.post(
      '/plaid/exchange-public-token',
      data: {
        'publicToken': publicToken,
        'institutionId': institutionId,
      },
    );
  }

  /// Exchange public token with quick 30-day sync + background full import
  Future<void> exchangePublicTokenWithQuickSync(String publicToken) async {
    await apiClient.post(
      '/plaid/exchange-public-token-quick-sync',
      data: {'publicToken': publicToken},
    );
  }

  /// Pulls whatever the aggregator has already collected, and reports how much of it
  /// was new.
  ///
  /// Deliberately NOT a bank refresh. This hits Plaid's /transactions/get, which returns
  /// what Plaid last pulled from the institution — it does not ask the bank for anything.
  /// Forcing a fetch is /transactions/refresh, a separately billed on-demand product we
  /// do not use. So a transaction that posted at the bank minutes ago genuinely will not
  /// be here yet, however many times this is called, and the UI has to say so rather than
  /// report a bare success (see _syncAll).
  ///
  /// The count was already in the response and was being discarded by a `Future<void>`.
  Future<int> syncAccounts() async {
    final response = await apiClient.post<Map<String, dynamic>>('/plaid/sync');
    final value = response['newTransactions'];
    return value is num ? value.toInt() : 0;
  }

  /// Sync a specific connection.
  ///
  /// The backend only exposes an account-wide sync (`POST /plaid/sync`) — there is no
  /// per-connection sync route — so this triggers a full refresh across all of the
  /// caller's connections. The [connectionId] is accepted for call-site clarity only.
  Future<int> syncConnection(int connectionId) async {
    return syncAccounts();
  }

  /// Remove a connection. A Plaid connection goes through the Plaid route, which asks for the step-up
  /// check and revokes the item at Plaid; every other provider goes through the provider-neutral route,
  /// which a self-hosted server has (its Plaid route answers 404).
  Future<void> removeConnection(int connectionId, {String provider = 'Plaid'}) async {
    if (provider == 'Plaid') {
      await apiClient.delete('/plaid/connections/$connectionId');
    } else {
      await apiClient.delete<dynamic>('/aggregators/connections/$connectionId');
    }
  }

  /// Update account nickname (null to clear)
  Future<Map<String, dynamic>> updateAccountNickname(int accountId, String? nickname) async {
    final data = {'nickname': nickname};
    return _currentOrLegacy(
      () => apiClient.put<Map<String, dynamic>>('/bank/accounts/$accountId/nickname', data: data),
      () => apiClient.put<Map<String, dynamic>>('/plaid/accounts/$accountId/nickname', data: data),
    );
  }

  /// Set which family member's budget this account's spend feeds (null = the family budget).
  Future<void> setAccountAssociation(int accountId, int? associatedUserId) async {
    final data = {'associatedUserId': associatedUserId};
    await _currentOrLegacy(
      () => apiClient.put<Map<String, dynamic>>('/bank/accounts/$accountId/association', data: data),
      () => apiClient.put<Map<String, dynamic>>('/plaid/accounts/$accountId/association', data: data),
    );
  }

  /// Get import progress for a connection
  Future<ImportProgress> getImportProgress(int connectionId) async {
    final response = await _currentOrLegacy<Map<String, dynamic>>(
      () => apiClient.get<Map<String, dynamic>>('/bank/connections/$connectionId/import-progress'),
      () => apiClient.get<Map<String, dynamic>>('/plaid/connections/$connectionId/import-progress'),
    );
    return ImportProgress.fromJson(response);
  }

  /// Begin reconnecting a broken connection. Returns a Plaid update-mode link token the caller
  /// opens in Plaid Link; the connection is only reactivated once re-auth completes (backend
  /// exchanges the resulting public token). Returns null if the backend didn't supply a token.
  Future<String?> reconnectAccount(int connectionId) async {
    final response = await apiClient.post<Map<String, dynamic>>(
      // Same OAuth return config as a fresh connect (Chase re-auth on Android needs it).
      // ApiClient.post takes no queryParameters, so the platform rides in the path.
      '/plaid/connections/$connectionId/reconnect?platform=${_plaidPlatform()}',
    );
    return response['linkToken'] as String?;
  }

  /// Starts signing in again for a Cloud bank feed connection. Returns the Cloud page to open, or null when the
  /// server gave none. The same connection is kept; finish it with [completeBridgeReconnect].
  Future<String?> startBridgeReconnect(int connectionId) async {
    final response = await apiClient.post<Map<String, dynamic>>(
      '/aggregators/Bridge/connections/$connectionId/reconnect',
      data: <String, dynamic>{},
    );
    final value = (response['value'] ?? response['Value'])?.toString();
    return (value == null || value.isEmpty) ? null : value;
  }

  /// Called when the person says they finished signing in on the Cloud page. Returns null when the connection is back,
  /// else a message to show.
  Future<String?> completeBridgeReconnect(String pageUrl) async {
    final response = await apiClient.post<Map<String, dynamic>>(
      '/aggregators/Bridge/connect/complete',
      data: {'artifact': pageUrl},
    );
    final ok = (response['success'] ?? response['Success']) == true;
    final message = (response['message'] ?? response['Message'])?.toString();
    return ok ? null : (message ?? 'The sign in has not finished yet.');
  }

  /// "Can't find my bank": forwards the bank a user was looking for after Plaid Link
  /// couldn't find it. The backend emails it to the support inbox.
  Future<void> requestBank(String bankName) async {
    await apiClient.post<dynamic>(
      '/plaid/bank-request',
      data: {'bankName': bankName, 'platform': _plaidPlatform()},
    );
  }

  // ===== Update Mode Methods (for re-authentication) =====

  /// Create update token for a connection that requires re-authentication
  /// This is used when a connection has requiresReauth = true.
  /// [accountSelection] opens Link update mode with account selection enabled
  /// (the NEW_ACCOUNTS_AVAILABLE flow for adding newly available accounts).
  Future<UpdateTokenResponse> createUpdateToken(
    int connectionId, {
    bool accountSelection = false,
  }) async {
    final response = await apiClient.post<Map<String, dynamic>>(
      '/plaid/webhook/update-token',
      data: {
        'connectionId': connectionId,
        'accountSelectionEnabled': accountSelection,
        // Update-mode tokens need the same OAuth return config as fresh connects.
        'platform': _plaidPlatform(),
      },
    );
    return UpdateTokenResponse.fromJson(response);
  }

  /// Update connection after user completes Plaid Link update flow
  Future<bool> updateConnection(int connectionId, String publicToken) async {
    final response = await apiClient.post<Map<String, dynamic>>(
      '/plaid/webhook/update-connection',
      data: {
        'connectionId': connectionId,
        'publicToken': publicToken,
      },
    );
    return response['success'] as bool? ?? false;
  }

  /// Get connection status (useful for checking if re-auth is needed)
  Future<ConnectionStatus> getConnectionStatus(int connectionId) async {
    final response = await apiClient.get<Map<String, dynamic>>(
      '/plaid/webhook/connection/$connectionId/status',
    );
    return ConnectionStatus.fromJson(response);
  }
}

/// Response from create update token
class UpdateTokenResponse {
  final bool success;
  final String? linkToken;
  final String? error;

  UpdateTokenResponse({
    required this.success,
    this.linkToken,
    this.error,
  });

  factory UpdateTokenResponse.fromJson(Map<String, dynamic> json) {
    return UpdateTokenResponse(
      success: json['success'] as bool? ?? false,
      linkToken: json['linkToken'] as String?,
      error: json['error'] as String?,
    );
  }
}

/// Connection status response
class ConnectionStatus {
  final int connectionId;
  final String status;
  final bool requiresReauth;
  final String? error;
  final DateTime? lastSync;

  ConnectionStatus({
    required this.connectionId,
    required this.status,
    required this.requiresReauth,
    this.error,
    this.lastSync,
  });

  factory ConnectionStatus.fromJson(Map<String, dynamic> json) {
    return ConnectionStatus(
      connectionId: json['connectionId'] as int? ?? json['ConnectionId'] as int? ?? 0,
      status: json['status'] as String? ?? json['Status'] as String? ?? 'unknown',
      requiresReauth: json['requiresReauth'] as bool? ?? json['RequiresReauth'] as bool? ?? false,
      error: json['error'] as String? ?? json['Error'] as String?,
      lastSync: json['lastSync'] != null 
          ? DateTime.tryParse(json['lastSync'] as String)
          : json['LastSync'] != null 
              ? DateTime.tryParse(json['LastSync'] as String)
              : null,
    );
  }
}

/// Singleton instance.
///
/// Not `final` only so widget/unit tests can inject a fake (there is no DI container);
/// production code never reassigns it. See test/widgets/create_transaction_sheet_test.dart.
AccountsService accountsService = AccountsService();
