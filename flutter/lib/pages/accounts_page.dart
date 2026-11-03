import '../config/bank_sources.dart';
import '../config/server_capabilities.dart';
import 'dart:async';
import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:go_router/go_router.dart';
import 'package:google_fonts/google_fonts.dart';
import 'package:plaid_flutter/plaid_flutter.dart';
import 'package:provider/provider.dart';
import 'package:sentry_flutter/sentry_flutter.dart';
import '../offline/network_error.dart' as neterr;
import 'package:url_launcher/url_launcher.dart';
import '../l10n/generated/app_localizations.dart';
import '../models/dashboard_models.dart';
import '../models/family_models.dart';
import '../providers/auth_provider.dart';
import '../services/accounts_service.dart';
import '../services/analytics_service.dart';
import '../services/family_service.dart';
import '../services/mfa_service.dart';
import '../utils/currency.dart';
import '../utils/formatters.dart';
import '../utils/pull_to_refresh.dart';
import '../utils/server_roles.dart';
import '../widgets/app_toast.dart';
import '../widgets/bank_mfa_gate_sheet.dart';
import '../widgets/step_up_mfa_dialog.dart';
import '../widgets/verify_email_dialog.dart';

// ── VaporClinic palette ─────────────────────────────────
const _accent = Color(0xFF7B61FF);
const _textGhost = Color(0xFFF0EFF4);
const _textMuted = Color(0xFF8A8A9A);
const _cardBg = Color(0x0DFFFFFF);
const _cardBorder = Color(0x14FFFFFF);
// Solid panel for popup surfaces (dropdown menus) — the translucent _cardBg is
// unreadable as a menu background over the animated layer.
const _menuBg = Color(0xFF181622);
const _income = Color(0xFF34D399);
const _expense = Color(0xFFF87171);
const _sheetBg = Color(0xFF0C0C18);

// ── Account type colors ─────────────────────────────────
const _depositoryColor = Color(0xFF60A5FA); // blue
const _investmentColor = Color(0xFFA78BFA); // purple
const _creditColor = Color(0xFFFBBF24);     // amber
const _loanColor = Color(0xFFF87171);       // red

/// ─────────────────────────────────────────────────────────
///  AccountsPage
///
///  Full-featured accounts page with:
///  - Glassmorphic summary cards (assets, credit, loans)
///  - Connection cards with categorized accounts
///  - Adaptive auto-refresh (30s business / 1m evening / 5m off-peak)
///  - Inline nickname editing
///  - Import progress indicators
///  - Plaid Link connect + update flows
///  - Disconnect confirmation
///  - RBAC awareness
/// ─────────────────────────────────────────────────────────
class AccountsPage extends StatefulWidget {
  const AccountsPage({super.key, this.openAddBank = false});

  /// Opens "Add a bank" as soon as the page is up (`/accounts?add=1`, from the owner's setup checklist).
  final bool openAddBank;

  @override
  State<AccountsPage> createState() => _AccountsPageState();
}

class _AccountsPageState extends State<AccountsPage>
    with SingleTickerProviderStateMixin {
  // ── Data ─────────────────────────────────────────────
  List<PlaidConnection> _connections = [];
  // Email-window MFA nudge (see BankMfaGateSheet): loaded alongside connections,
  // null when the status call fails so the page never blocks on it.
  MfaStatus? _mfaStatus;
  bool _mfaNudgeDismissed = false;
  final Map<int, ImportProgress> _importProgress = {};
  bool _isLoading = true;
  bool _isSyncing = false;
  bool _isConnecting = false;
  bool _addBankOpen = false;
  String? _error;

  // ── Ways to add a bank (what this server offers; see bankConnectionSources) ──
  List<BankSource> _bankSources =
      bankConnectionSources(ServerEdition.current.value, null);

  // ── Family (for per-account budget association) ──────
  List<FamilyMember> _familyMembers = [];

  // ── Animation ────────────────────────────────────────
  late AnimationController _fadeController;
  late Animation<double> _fadeAnimation;

  // ── Auto-refresh ─────────────────────────────────────
  Timer? _autoRefreshTimer;
  Timer? _importPollTimer;

  // ── Plaid Link ───────────────────────────────────────
  StreamSubscription<LinkSuccess>? _plaidSuccessSubscription;
  StreamSubscription<LinkExit>? _plaidExitSubscription;
  StreamSubscription<LinkEvent>? _plaidEventSubscription;

  /// When non-null, the next Plaid Link success is an update-mode re-auth for this
  /// connection — completion must go through updateConnection() (POST /plaid/webhook/
  /// update-connection → HandleItemUpdateAsync), NOT the fresh-connect exchange, which
  /// would hit the duplicate-institution guard on the still-active broken row and throw.
  int? _reconnectingConnectionId;

  /// The last thing the user typed into Plaid's bank search in this Link session. Prefills
  /// the "can't find your bank?" sheet when they leave Link with institution_not_found.
  String? _lastSearchQuery;

  // ── Nickname editing ─────────────────────────────────
  int? _editingAccountId;
  final _nicknameController = TextEditingController();
  bool _isSavingNickname = false;

  @override
  void initState() {
    super.initState();
    _fadeController = AnimationController(
      vsync: this,
      duration: const Duration(milliseconds: 600),
    );
    _fadeAnimation =
        CurvedAnimation(parent: _fadeController, curve: Curves.easeOut);
    _fadeController.forward();
    _setupPlaidListeners();
    _loadConnections();
    _loadFamilyMembers();
    if (widget.openAddBank) {
      // Ask the server what it offers first, so the chooser lists the right ways in.
      WidgetsBinding.instance.addPostFrameCallback((_) async {
        await _refreshBankSources();
        if (mounted) unawaited(_addBank());
      });
    } else {
      _refreshBankSources();
    }
  }

  /// False for a household member on a self-hosted server: only the owner can link it to BuoyAI Cloud, so the
  /// Cloud bank feed stays out of the way until it is linked.
  bool get _canLinkCloud =>
      !ServerEdition.current.value.isSelfHost || (serverAdminOf(context) ?? true);

  @override
  void dispose() {
    _fadeController.dispose();
    _autoRefreshTimer?.cancel();
    _importPollTimer?.cancel();
    _plaidSuccessSubscription?.cancel();
    _plaidExitSubscription?.cancel();
    _plaidEventSubscription?.cancel();
    _nicknameController.dispose();
    super.dispose();
  }

  // ═══════════════════════════════════════════════════════
  //  DATA LOADING
  // ═══════════════════════════════════════════════════════

  Future<void> _loadMfaStatus() async {
    try {
      final status = await mfaService.getStatus();
      if (mounted) setState(() => _mfaStatus = status);
    } catch (_) {
      // Non-critical: the nudge simply does not render.
    }
  }

  Future<void> _loadConnections() async {
    setState(() {
      _isLoading = true;
      _error = null;
    });
    // Explicit instrumentation for the recurring "accounts page won't load"
    // report. These events (plus the Sentry capture below) turn an invisible
    // failure into a timeline in PostHog: did the load even start, did it
    // succeed with zero connections, or did the API throw — and with what.
    analytics.track(AnalyticsEvents.accountsLoadStarted);
    _loadMfaStatus();
    try {
      final connections = await accountsService.getConnections();
      if (!mounted) return;
      setState(() {
        _connections = connections;
        _isLoading = false;
      });
      final accountCount =
          connections.fold<int>(0, (sum, c) => sum + c.accounts.length);
      analytics.track(AnalyticsEvents.accountsLoaded, {
        'connection_count': connections.length,
        'account_count': accountCount,
        // An inactive connection needs a reconnect and renders no live data —
        // a prime "nothing shows up" suspect. Surface the active/inactive split
        // so we can spot it in PostHog.
        'active_connection_count': connections.where((c) => c.isActive).length,
        'inactive_connection_count':
            connections.where((c) => !c.isActive).length,
      });
      _scheduleAutoRefresh();
      _pollImportProgress();
    } catch (e, stack) {
      if (!mounted) return;
      analytics.track(AnalyticsEvents.accountsLoadFailed, _accountsErrorProps(e));
      // The generic _error string below hides the real cause; report the
      // actual exception to Sentry so we can see status codes / stack traces.
      if (e is! DioException || !neterr.isNetworkError(e)) {
        unawaited(Sentry.captureException(e, stackTrace: stack));
      }
      setState(() {
        _error = 'Failed to load accounts';
        _isLoading = false;
      });
    }
  }

  /// Flatten an accounts-load failure into analytics-safe properties. For a
  /// Dio error we keep the HTTP status, request path and Dio error type — the
  /// three fields that tell us whether it was auth (401), a timeout, a 5xx, or
  /// the request never reaching the server at all.
  Map<String, Object> _accountsErrorProps(Object e) {
    if (e is DioException) {
      return {
        'error_kind': 'dio',
        'dio_type': e.type.name,
        'status_code': e.response?.statusCode ?? -1,
        'path': e.requestOptions.path,
        'message': e.message ?? e.error?.toString() ?? 'unknown',
      };
    }
    return {
      'error_kind': e.runtimeType.toString(),
      'message': e.toString(),
    };
  }

  /// Silent refresh — no loading indicator
  Future<void> _silentRefresh() async {
    try {
      final connections = await accountsService.getConnections();
      if (!mounted) return;
      setState(() {
        _connections = connections;
        _error = null;
      });
    } catch (_) {
      // Don't update error state for silent refreshes
    }
  }

  /// Load family members so each account can be pointed at a member's budget.
  /// Only relevant when an actual family exists (more than just yourself).
  Future<void> _loadFamilyMembers() async {
    try {
      final members = await familyService.getFamilyMembers();
      if (!mounted) return;
      setState(() => _familyMembers = members.length > 1 ? members : []);
    } on DioException catch (e) {
      // The server answers 400 when the user is not in a family. That is the normal solo case, not a failure.
      if (e.response?.statusCode != 400 && !neterr.isNetworkError(e)) {
        unawaited(Sentry.captureException(e));
      }
    } catch (_) {
      // Endpoint unavailable — no association selector is shown.
    }
  }

  /// Asks the server which ways it can connect a bank right now. Any failure keeps the edition default.
  /// Returns true only when the server actually answered, so callers can tell "confirmed" from "could not tell".
  Future<bool> _refreshBankSources() async {
    List<String>? providers;
    try {
      providers = await accountsService.getBankProviders();
    } catch (_) {
      providers = null;
    }
    if (!mounted) return false;
    // A failed request keeps the last known list: it must not reset a confirmed Bridge to the edition default.
    if (providers == null) return false;
    setState(() => _bankSources = bankConnectionSources(ServerEdition.current.value, providers));
    return true;
  }

  String _memberName(int userId) {
    final match = _familyMembers.where((m) => m.userId == userId);
    return match.isEmpty ? 'that member' : match.first.displayName;
  }

  Future<void> _setAccountAssociation(Account account, int? associatedUserId) async {
    try {
      await accountsService.setAccountAssociation(account.id, associatedUserId);
      if (!mounted) return;
      showAppToast(
        context,
        associatedUserId == null
            ? 'Now feeds the family budget'
            : 'Now feeds ${_memberName(associatedUserId)}\'s budget',
        type: AppToastType.success,
      );
      await _silentRefresh();
    } catch (_) {
      if (mounted) {
        showAppToast(context, 'Failed to update account', type: AppToastType.error);
      }
    }
  }

  // ── Adaptive auto-refresh ────────────────────────────

  int _getRefreshInterval() {
    final now = DateTime.now();
    final hour = now.hour;
    final isWeekday = now.weekday >= 1 && now.weekday <= 5;

    // Business hours: 7 AM – 7 PM weekdays → 30s
    if (isWeekday && hour >= 7 && hour < 19) return 30000;
    // Evening: 7 PM – 11 PM weekdays → 1m
    if (isWeekday && hour >= 19 && hour < 23) return 60000;
    // Weekend business: 9 AM – 6 PM → 1m
    if (!isWeekday && hour >= 9 && hour < 18) return 60000;
    // Off-peak → 5m
    return 300000;
  }

  String _getRefreshMode() {
    final interval = _getRefreshInterval();
    if (interval == 30000) return 'Active (30s)';
    if (interval == 60000) return 'Moderate (1m)';
    return 'Slow (5m)';
  }

  void _scheduleAutoRefresh() {
    _autoRefreshTimer?.cancel();
    if (_connections.isEmpty) return;

    _autoRefreshTimer = Timer(
      Duration(milliseconds: _getRefreshInterval()),
      () async {
        if (!mounted || _isSyncing || _isLoading) return;
        await _silentRefresh();
        _scheduleAutoRefresh(); // Reschedule with possibly updated interval
      },
    );
  }

  // ── Import progress polling ──────────────────────────

  void _pollImportProgress() {
    _importPollTimer?.cancel();
    // Check all connections for active imports
    final activeConnections = _connections.where((c) => c.isActive).toList();
    if (activeConnections.isEmpty) return;

    _importPollTimer = Timer.periodic(
      const Duration(seconds: 5),
      (timer) async {
        if (!mounted) {
          timer.cancel();
          return;
        }
        bool anyActive = false;
        for (final conn in activeConnections) {
          try {
            final progress = await accountsService.getImportProgress(conn.id);
            if (!mounted) return;
            setState(() => _importProgress[conn.id] = progress);
            if (progress.isActive) anyActive = true;
          } catch (_) {
            // Connection may not have import in progress
          }
        }
        if (!anyActive) {
          timer.cancel();
          _importPollTimer = null;
        }
      },
    );
  }

  // ═══════════════════════════════════════════════════════
  //  ACTIONS
  // ═══════════════════════════════════════════════════════

  Future<void> _syncAll() async {
    setState(() => _isSyncing = true);
    try {
      if (!_bankSources.contains(BankSource.plaid)) {
        // No Plaid here (self-host): SimpleFIN and the Cloud feed sync on the server's own schedule and there is
        // no manual sync route, so refreshing the list is all a pull or a tap on Sync can do.
        await _silentRefresh();
        if (mounted) {
          showAppToast(context, AppLocalizations.of(context).acctRefreshedFeeds, type: AppToastType.success);
        }
        return;
      }
      final newCount = await accountsService.syncAccounts();
      await _silentRefresh();
      if (mounted) {
        showAppToast(context, _syncResultMessage(newCount),
            type: AppToastType.success);
      }
    } catch (e) {
      if (mounted) showAppToast(context, 'Sync failed: $e', type: AppToastType.error);
    } finally {
      if (mounted) setState(() => _isSyncing = false);
    }
  }

  /// What to say after a sync, given how many transactions were actually new.
  ///
  /// This used to always report "Accounts synced", which reads as "we just fetched your
  /// latest activity from the bank". We don't do that: the sync reads what Plaid has
  /// already collected, and Plaid polls each institution on its own schedule (typically a
  /// few times a day). So something that posted at the bank twenty minutes ago is
  /// legitimately absent, and a bare success message left the only reasonable conclusion
  /// being that the app was broken.
  ///
  /// Saying nothing new arrived, and why, is both true and more useful than a green tick.
  String _syncResultMessage(int newCount) {
    if (newCount > 0) {
      return newCount == 1
          ? 'Added 1 new transaction'
          : 'Added $newCount new transactions';
    }
    return 'No new transactions yet. Banks report new activity a few times a '
        'day, so anything from the last few hours may not have reached us.';
  }

  Future<void> _syncConnection(PlaidConnection connection) async {
    try {
      // Same caveat as _syncAll: the backend has no per-connection sync route, so this
      // refreshes everything and the count is across all connections. Naming one
      // institution in the result would therefore be a second small lie.
      final newCount = await accountsService.syncConnection(connection.id);
      await _silentRefresh();
      if (mounted) {
        showAppToast(context, _syncResultMessage(newCount),
            type: AppToastType.success);
      }
    } catch (e) {
      if (mounted) showAppToast(context, 'Sync failed: $e', type: AppToastType.error);
    }
  }

  Future<void> _removeConnection(PlaidConnection connection) async {
    final confirmed = await _showDisconnectSheet(connection);
    if (confirmed != true || !mounted) return;

    try {
      await accountsService.removeConnection(connection.id, provider: connection.provider);
      analytics.track(AnalyticsEvents.bankDisconnected);
      await _loadConnections();
      if (mounted) {
        showAppToast(context, '${connection.institutionName} removed',
            type: AppToastType.success);
      }
    } catch (e) {
      if (mounted) {
        showAppToast(context, 'Failed to remove: $e', type: AppToastType.error);
      }
    }
  }

  /// Cloud bank feed: open the Cloud's sign in page, then ask the server to take the connection back once the
  /// person says they are done. The same connection is kept, so nothing is connected twice.
  Future<void> _reconnectBridge(PlaidConnection connection) async {
    try {
      final pageUrl = await accountsService.startBridgeReconnect(connection.id);
      if (pageUrl == null) {
        if (mounted) {
          showAppToast(
            context,
            'Could not start reconnection for ${connection.institutionName}',
            type: AppToastType.error,
          );
        }
        return;
      }
      final problem = await _signInOnCloudPage(pageUrl, connection.institutionName);
      if (problem == _cloudSignInCancelled) return;
      await _silentRefresh();
      if (!mounted) return;
      if (problem == null) {
        showAppToast(context, '${connection.institutionName} reconnected',
            type: AppToastType.success);
      } else {
        showAppToast(context, problem, type: AppToastType.error);
      }
    } catch (e) {
      if (mounted) {
        showAppToast(context, 'Failed to reconnect: $e', type: AppToastType.error);
      }
    }
  }

  /// Connect a bank through the Cloud bank feed: the Cloud makes the sign in page, the person signs in to
  /// their bank there, and the server adopts the bank they connected.
  Future<void> _connectCloudFeed() async {
    analytics.track(AnalyticsEvents.bankConnectionStarted);
    final l10n = AppLocalizations.of(context);
    try {
      final pageUrl = await accountsService.startBridgeConnect();
      if (pageUrl == null) {
        if (mounted) showAppToast(context, l10n.acctCloudConnectFailed, type: AppToastType.error);
        return;
      }
      final problem = await _signInOnCloudPage(pageUrl, l10n.acctCloudSignInBank);
      if (problem == _cloudSignInCancelled) return;
      await _loadConnections();
      if (!mounted) return;
      if (problem == null) {
        analytics.track(AnalyticsEvents.bankConnected, {'institution': 'cloud_feed'});
        showAppToast(context, 'Bank connected', type: AppToastType.success);
      } else {
        showAppToast(context, problem, type: AppToastType.error);
      }
    } catch (e) {
      analytics.track(AnalyticsEvents.bankConnectionFailed, _accountsErrorProps(e));
      // The server is not linked to the Cloud (any more): that is the next step to show, not an error.
      final confirmed = await _refreshBankSources();
      if (!mounted) return;
      if (confirmed && cloudFeedNeedsLink(_bankSources)) {
        await _showCloudLinkPrompt();
        return;
      }
      showAppToast(context, _friendlyError(e, l10n.acctCloudConnectFailed),
          type: AppToastType.error);
    }
  }

  /// The Cloud bank feed needs this server linked first: say so and offer the link, never a dead end.
  Future<void> _showCloudLinkPrompt() async {
    final link = await showModalBottomSheet<bool>(
      context: context,
      isScrollControlled: true,
      backgroundColor: Colors.transparent,
      builder: (sheetContext) => CloudLinkPromptSheet(
        ownerManages: !_canLinkCloud,
        onLink: () => Navigator.of(sheetContext).pop(true),
        onClose: () => Navigator.of(sheetContext).pop(false),
      ),
    );
    if (link == true && mounted) context.push('/settings/cloud');
  }

  static const _cloudSignInCancelled = '\u0000cancelled';

  /// Opens the Cloud's page, waits for the person to say they finished there, and asks the server to complete it.
  /// Returns null when it worked, a message to show when it did not, or [_cloudSignInCancelled].
  Future<String?> _signInOnCloudPage(String pageUrl, String bankName) async {
    await launchUrl(Uri.parse(pageUrl), mode: LaunchMode.externalApplication);
    if (!mounted) return _cloudSignInCancelled;
    final l10n = AppLocalizations.of(context);
    final done = await showDialog<bool>(
      context: context,
      builder: (ctx) => AlertDialog(
        title: Text(l10n.acctBridgeSignInTitle(bankName)),
        content: Text(l10n.acctBridgeSignInBody),
        actions: [
          TextButton(
              onPressed: () => Navigator.of(ctx).pop(false),
              child: Text(l10n.acctCancel)),
          TextButton(
              onPressed: () => Navigator.of(ctx).pop(true),
              child: Text(l10n.acctBridgeSignInDone)),
        ],
      ),
    );
    if (done != true) return _cloudSignInCancelled;
    return accountsService.completeBridgeReconnect(pageUrl);
  }

  Future<void> _reconnectConnection(PlaidConnection connection) async {
    if (connection.isSimpleFin) {
      context.push('/settings/simplefin');
      return;
    }
    if (connection.isBridge) {
      await _reconnectBridge(connection);
      return;
    }
    try {
      // Reconnecting a broken item requires real re-authentication through Plaid update mode —
      // the backend returns an update-mode link token rather than optimistically marking the
      // connection healthy. Open Plaid Link; the shared onSuccess listener finishes the exchange
      // and the connection reactivates only when re-auth actually succeeds.
      final linkToken = await accountsService.reconnectAccount(connection.id);
      if (linkToken == null || linkToken.isEmpty) {
        if (mounted) {
          showAppToast(
            context,
            'Could not start reconnection for ${connection.institutionName}',
            type: AppToastType.error,
          );
        }
        return;
      }
      // Mark this as an update-mode flow so the shared onSuccess listener routes completion
      // to updateConnection() instead of the fresh-connect exchange.
      _reconnectingConnectionId = connection.id;
      final config = LinkTokenConfiguration(token: linkToken);
      await PlaidLink.create(configuration: config);
      await PlaidLink.open();
    } catch (e) {
      _reconnectingConnectionId = null;
      if (mounted) {
        showAppToast(context, 'Failed to reconnect: $e', type: AppToastType.error);
      }
    }
  }

  // ── Nickname editing ─────────────────────────────────

  void _startEditingNickname(Account account) {
    setState(() {
      _editingAccountId = account.id;
      _nicknameController.text = account.nickname ?? '';
    });
  }

  void _cancelEditingNickname() {
    setState(() {
      _editingAccountId = null;
      _nicknameController.clear();
    });
  }

  Future<void> _saveNickname(Account account) async {
    setState(() => _isSavingNickname = true);
    try {
      final nickname = _nicknameController.text.trim();
      await accountsService.updateAccountNickname(
        account.id,
        nickname.isEmpty ? null : nickname,
      );
      await _silentRefresh();
      _cancelEditingNickname();
      if (mounted) {
        showAppToast(
          context,
          nickname.isNotEmpty ? 'Renamed to "$nickname"' : 'Nickname cleared',
          type: AppToastType.success,
        );
      }
    } catch (e) {
      if (mounted) {
        showAppToast(context, 'Failed to save nickname', type: AppToastType.error);
      }
    } finally {
      if (mounted) setState(() => _isSavingNickname = false);
    }
  }

  // ── Plaid Link ───────────────────────────────────────

  void _setupPlaidListeners() {
    _plaidSuccessSubscription = PlaidLink.onSuccess.listen(_handlePlaidSuccess);
    _plaidExitSubscription = PlaidLink.onExit.listen(_handlePlaidExit);
    _plaidEventSubscription = PlaidLink.onEvent.listen(_handlePlaidEvent);
  }

  /// Plaid is the only way to add a bank: the hosted service as it always was.
  bool get _plaidOnly => _bankSources.length == 1 && _bankSources.first == BankSource.plaid;

  /// "Add a bank": the chooser lists only what this server offers. When the only way is Plaid (the hosted
  /// service as it always was) the Plaid sheet opens straight away; when there is no bank feed at all the
  /// only way left is importing a file.
  Future<void> _addBank() async {
    // Work from the list loaded at start so the tap responds at once (the providers call can stall offline);
    // refresh it in the background for next time. The flag stops a double tap stacking two sheets.
    if (_addBankOpen) return;
    _addBankOpen = true;
    try {
      unawaited(_refreshBankSources());
      await _addBankFlow();
    } finally {
      _addBankOpen = false;
    }
  }

  Future<void> _addBankFlow() async {
    if (_plaidOnly) {
      _showConnectBankSheet();
      return;
    }
    // Self-hosted: the same chooser from every entry point. It always lists the Cloud bank feed (connect, or
    // link first), SimpleFIN when the server offers it, and Import a file, so it never dead-ends.
    final needsLink = cloudFeedNeedsLink(_bankSources);
    final canLink = _canLinkCloud;
    final chosen = await showModalBottomSheet<BankSource>(
      context: context,
      isScrollControlled: true,
      backgroundColor: Colors.transparent,
      builder: (sheetContext) => AddBankChooserSheet(
        sources: addBankChooserSources(_bankSources, canLink: canLink),
        cloudNeedsLink: needsLink,
        onSelected: (source) => Navigator.of(sheetContext).pop(source),
      ),
    );
    if (chosen == null || !mounted) return;
    switch (chosen) {
      case BankSource.plaid:
        _showConnectBankSheet();
      case BankSource.cloudFeed:
        // The list may be older than a link made a moment ago: ask again before sending anyone to "link first".
        var stillNeedsLink = needsLink;
        if (needsLink) {
          final confirmed = await _refreshBankSources();
          if (!mounted) return;
          if (confirmed) stillNeedsLink = cloudFeedNeedsLink(_bankSources);
        }
        if (stillNeedsLink) {
          await _showCloudLinkPrompt();
        } else {
          await _connectCloudFeed();
        }
      case BankSource.simpleFin:
        context.push('/settings/simplefin');
      case BankSource.importFile:
        context.push('/settings/import');
    }
  }

  Future<void> _openPlaidLink({bool afterMfaEnroll = false}) async {
    if (!mounted) return;
    if (!afterMfaEnroll) {
      Navigator.pop(context); // close sheet
      analytics.track(AnalyticsEvents.bankConnectionStarted);
    }
    setState(() => _isConnecting = true);

    try {
      final linkToken = await accountsService.createLinkToken();
      final configuration = LinkTokenConfiguration(token: linkToken);
      await PlaidLink.create(configuration: configuration);
      await PlaidLink.open();
    } on BankMfaRequiredException {
      // The phishing-resistant MFA gate. Do NOT count this as a Plaid failure and
      // do NOT dead-end on a toast: enroll inline, then continue into Plaid Link.
      // The single afterMfaEnroll pass guards against looping if the server still
      // says no after a successful enrollment.
      setState(() => _isConnecting = false);
      analytics.track(AnalyticsEvents.bankConnectionBlockedMfa,
          {'retry': afterMfaEnroll});
      if (!mounted || afterMfaEnroll) {
        if (mounted) {
          showAppToast(context,
              'Two-factor security is still required. Please try again from Profile.',
              type: AppToastType.error);
        }
        return;
      }
      final firstName = context.read<AuthProvider>().user?.firstName;
      final enrolled = await showBankMfaGateSheet(context, displayName: firstName);
      if (enrolled && mounted) {
        await _openPlaidLink(afterMfaEnroll: true);
      }
    } catch (e) {
      setState(() => _isConnecting = false);
      analytics.track(AnalyticsEvents.bankConnectionFailed, _accountsErrorProps(e));
      // Bank connect is the product's front door — a failure here must page us, not
      // wait for a user report (2026-07-18: Plaid Link was broken in prod for weeks
      // and the first alert was a family member's text message).
      unawaited(Sentry.captureException(e,
          withScope: (scope) => scope.setTag('flow', 'plaid_link_open')));
      if (mounted) {
        showAppToast(context, _friendlyError(e, 'Failed to open Plaid Link'),
            type: AppToastType.error);
      }
    }
  }

  String _friendlyError(Object e, String fallback) {
    if (e is DioException) {
      final data = e.response?.data;
      if (data is Map && data['message'] != null) return data['message'].toString();
    }
    if (e is Exception) {
      final msg = e.toString();
      if (msg.startsWith('Exception: ')) return msg.substring(11);
    }
    return fallback;
  }

  Future<void> _handlePlaidSuccess(LinkSuccess success, {bool emailRetried = false}) async {
    // Update-mode re-auth: reuse the existing connection row via update-connection rather
    // than the fresh-connect exchange (which would throw on the still-active broken row).
    final reconnectingId = _reconnectingConnectionId;
    if (reconnectingId != null) {
      _reconnectingConnectionId = null;
      await _handleReconnectSuccess(reconnectingId, success);
      return;
    }
    try {
      await accountsService.exchangePublicTokenWithQuickSync(
        success.publicToken,
      );
      analytics.track(AnalyticsEvents.bankConnected, {
        'institution': success.metadata.institution?.name ?? 'unknown',
      });
      await _loadConnections();
      if (mounted) {
        final name = success.metadata.institution?.name ?? 'your bank';
        showAppToast(context, 'Connected to $name! Quick sync started.',
            type: AppToastType.success);
      }
    } on DioException catch (e) {
      if (!mounted) return;
      String? error;
      if (e.response?.statusCode == 403 && e.response?.data is Map) {
        error = (e.response!.data as Map)['error'] as String?;
      }
      if (error == 'EmailVerificationRequired') {
        // Email must be verified before the bank can be connected. Verify, then retry
        // ONCE — which will then prompt for step-up MFA if that's still needed. The
        // single-retry guard avoids an endless verify loop if the gate keeps returning.
        if (emailRetried) {
          showAppToast(context, 'Email verification is still required. Please try again.',
              type: AppToastType.error);
          return;
        }
        final verified = await showVerifyEmailDialog(context);
        if (verified == true && mounted) {
          await _handlePlaidSuccess(success, emailRetried: true);
          return;
        } else if (mounted) {
          showAppToast(context, 'Verify your email to finish connecting your account.',
              type: AppToastType.error);
        }
      } else if (error == 'MfaRequired') {
        final verified = await showStepUpMfaDialog(
          context,
          operation: 'ConnectBankAccount',
        );
        if (verified != null) {
          try {
            await accountsService.exchangePublicTokenWithQuickSync(
              success.publicToken,
            );
            analytics.track(AnalyticsEvents.bankConnected, {
              'institution': success.metadata.institution?.name ?? 'unknown',
            });
            await _loadConnections();
            if (mounted) {
              final name = success.metadata.institution?.name ?? 'your bank';
              showAppToast(context, 'Connected to $name! Quick sync started.',
                  type: AppToastType.success);
            }
          } catch (retryError) {
            analytics.track(
                AnalyticsEvents.bankConnectionFailed, _accountsErrorProps(retryError));
            if (mounted) {
              showAppToast(context, _friendlyError(retryError, 'Failed to connect account'),
                  type: AppToastType.error);
            }
          }
        }
      } else {
        analytics.track(AnalyticsEvents.bankConnectionFailed, _accountsErrorProps(e));
        showAppToast(context, _friendlyError(e, 'Failed to connect account'),
            type: AppToastType.error);
      }
    } catch (e) {
      analytics.track(AnalyticsEvents.bankConnectionFailed, _accountsErrorProps(e));
      if (mounted) {
        showAppToast(context, _friendlyError(e, 'Failed to connect account'),
            type: AppToastType.error);
      }
    } finally {
      if (mounted) setState(() => _isConnecting = false);
    }
  }

  /// Complete an update-mode re-auth: reuse the existing connection row via
  /// updateConnection() → POST /plaid/webhook/update-connection (HandleItemUpdateAsync),
  /// which reactivates the row instead of tripping the fresh-connect duplicate guard.
  Future<void> _handleReconnectSuccess(int connectionId, LinkSuccess success) async {
    try {
      final ok = await accountsService.updateConnection(connectionId, success.publicToken);
      await _loadConnections();
      if (mounted) {
        final name = success.metadata.institution?.name ?? 'your bank';
        if (ok) {
          showAppToast(context, 'Reconnected to $name!', type: AppToastType.success);
        } else {
          showAppToast(context, 'Could not reconnect to $name. Please try again.',
              type: AppToastType.error);
        }
      }
    } catch (e) {
      if (mounted) {
        showAppToast(context, _friendlyError(e, 'Failed to reconnect'),
            type: AppToastType.error);
      }
    } finally {
      if (mounted) setState(() => _isConnecting = false);
    }
  }

  void _handlePlaidExit(LinkExit exit) {
    final wasReconnecting = _reconnectingConnectionId != null;
    // Cancelling clears any pending update-mode flow so a later fresh connect isn't misrouted.
    _reconnectingConnectionId = null;
    setState(() => _isConnecting = false);

    // They searched and their bank wasn't there. Nothing used to happen here, so we never
    // learned which banks people were missing (the 2 most recent connect attempts both ended
    // this way). Ask, and send it to the founder's inbox.
    final status = exit.metadata.status;
    if (!wasReconnecting &&
        mounted &&
        (status == 'institution_not_found' || status == 'institution_not_supported')) {
      unawaited(_offerBankRequest(status!));
    }
    if (exit.error != null && exit.error!.code != 'USER_EXIT' && mounted) {
      // Identifiers Plaid support needs for a ticket (link_session_id / request_id).
      debugPrint('Plaid Link exit error ${exit.error!.code} '
          '(link_session_id: ${exit.metadata.linkSessionId}, request_id: ${exit.metadata.requestId})');
      unawaited(Sentry.captureMessage(
        'Plaid Link exit: ${exit.error!.code}',
        withScope: (scope) {
          scope.setTag('flow', 'plaid_link_exit');
          scope.setContexts('plaid', {
            'link_session_id': exit.metadata.linkSessionId,
            'request_id': exit.metadata.requestId,
          });
        },
      ));
      analytics.track(AnalyticsEvents.bankConnectionFailed, {
        'error_kind': 'plaid_exit',
        'message': exit.error!.code,
        if (exit.metadata.linkSessionId != null)
          'link_session_id': exit.metadata.linkSessionId!,
        if (exit.metadata.requestId != null)
          'request_id': exit.metadata.requestId!,
      });
      showAppToast(context, 'Connection error: ${exit.error!.message}',
          type: AppToastType.error);
    }
  }

  void _handlePlaidEvent(LinkEvent event) {
    debugPrint('Plaid Link event: ${event.name}');
    // Feed Plaid's granular Link funnel (institution selected, credentials submitted,
    // handoff, per-step exits) into PostHog so conversion drop-off is measurable —
    // start/fail/success alone can't say WHERE users bail (Plaid measuring-conversion
    // guidance). Consent-gated like all analytics; no-op when the user declined.
    // link_session_id + request_id are the identifiers Plaid support and the Item
    // Debugger key on — always attach them (Plaid logging guidance).
    final institution = event.metadata.institutionName;
    final exitStatus = event.metadata.exitStatus;
    final requestId = event.metadata.requestId;
    final errorCode = event.metadata.errorCode;
    // What they typed into the bank search (SEARCH_INSTITUTION events). Without it an
    // institution_not_found exit says someone gave up, not which bank they wanted. iOS
    // reports "no query" as an empty string rather than null.
    final searchQuery = event.metadata.institutionSearchQuery?.trim() ?? '';
    if (event.name == 'OPEN') _lastSearchQuery = null;
    if (searchQuery.isNotEmpty) _lastSearchQuery = searchQuery;
    analytics.track('plaid_link_event', {
      'event': event.name,
      'link_session_id': event.metadata.linkSessionId,
      if (institution != null) 'institution': institution,
      if (exitStatus != null) 'exit_status': exitStatus,
      if (requestId != null) 'request_id': requestId,
      if (errorCode != null) 'error_code': errorCode,
      if (searchQuery.isNotEmpty) 'search_query': searchQuery,
    });
  }

  /// "Can't find your bank?" Asks which bank they wanted (prefilled from their last search)
  /// and forwards it to the support inbox, where the founder can reply to them directly.
  Future<void> _offerBankRequest(String exitStatus) async {
    final l10n = AppLocalizations.of(context);
    final bankName = await showModalBottomSheet<String>(
      context: context,
      backgroundColor: Colors.transparent,
      isScrollControlled: true,
      builder: (_) => _BankRequestSheet(initialQuery: _lastSearchQuery),
    );
    if (bankName == null || bankName.trim().isEmpty) {
      analytics.track('bank_request_skipped', {'exit_status': exitStatus});
      return;
    }
    try {
      await accountsService.requestBank(bankName.trim());
      analytics.track('bank_request_submitted', {'exit_status': exitStatus});
      if (mounted) {
        showAppToast(context, l10n.acctBankRequestThanks, type: AppToastType.success);
      }
    } catch (e) {
      if (mounted) {
        showAppToast(context, l10n.acctBankRequestFailed, type: AppToastType.error);
      }
    }
  }

  // ── Update connection (reauth) ───────────────────────

  Future<void> _updateConnection(PlaidConnection connection) async {
    if (connection.isSimpleFin) {
      // SimpleFIN is fixed with a new setup token, not Plaid Link.
      context.push('/settings/simplefin');
      return;
    }
    if (connection.isBridge) {
      await _reconnectBridge(connection);
      return;
    }
    try {
      final updateResponse = await accountsService.createUpdateToken(connection.id);
      if (!updateResponse.success || updateResponse.linkToken == null) {
        if (mounted) {
          showAppToast(context, updateResponse.error ?? 'Failed to create update token',
              type: AppToastType.error);
        }
        return;
      }
      // Update mode: completion must reuse the existing row via updateConnection(), not the
      // fresh-connect exchange (which would throw on the still-active row's duplicate guard).
      _reconnectingConnectionId = connection.id;
      final config = LinkTokenConfiguration(token: updateResponse.linkToken!);
      await PlaidLink.create(configuration: config);
      await PlaidLink.open();
      // Success/exit handled by existing Plaid listeners
    } catch (e) {
      _reconnectingConnectionId = null;
      if (mounted) {
        showAppToast(context, 'Failed to update connection: $e',
            type: AppToastType.error);
      }
    }
  }

  // ═══════════════════════════════════════════════════════
  //  COMPUTED PROPERTIES
  // ═══════════════════════════════════════════════════════

  List<Account> get _allAccounts =>
      _connections.expand((c) => c.accounts).toList();

  /// Accounts in the primary display currency — the only ones safe to sum.
  /// Foreign-currency accounts render per-account (with their own symbol) and
  /// are called out by [_foreignBalances] instead of being silently mixed in.
  List<Account> get _primaryCurrencyAccounts =>
      _allAccounts.where((a) => isPrimaryCurrency(a.currency)).toList();

  /// Net balance per non-primary ISO code (e.g. {CAD: 523.10}).
  Map<String, double> get _foreignBalances => splitByCurrency(
        _allAccounts,
        currencyOf: (a) => a.currency,
        amountOf: (a) => a.currentBalance,
      ).otherTotals;

  int get _foreignAccountCount =>
      _allAccounts.where((a) => !isPrimaryCurrency(a.currency)).length;

  /// Assets only: depository (0) + investment (1)
  double get _totalAssets => _primaryCurrencyAccounts
      .where((a) => a.type == 0 || a.type == 1)
      .fold(0.0, (s, a) => s + a.currentBalance);

  /// Credit cards (type 2)
  double get _creditBalance => _primaryCurrencyAccounts
      .where((a) => a.type == 2)
      .fold(0.0, (s, a) => s + a.currentBalance.abs());

  /// Loans (type 3/4)
  double get _loanBalance => _primaryCurrencyAccounts
      .where((a) => a.type == 3 || a.type == 4)
      .fold(0.0, (s, a) => s + a.currentBalance.abs());

  List<PlaidConnection> get _needsAttention =>
      // The API only returns active connections, so a bank that needs a new login is an
      // active connection with requiresUserAction set; inactive is kept for older servers.
      _connections.where((c) => !c.isActive || c.requiresUserAction).toList();

  // ═══════════════════════════════════════════════════════
  //  BUILD
  // ═══════════════════════════════════════════════════════

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      backgroundColor: Colors.transparent,
      body: FadeTransition(
        opacity: _fadeAnimation,
        child: _buildBody(),
      ),
    );
  }

  Widget _buildBody() {
    if (_isLoading) return _buildLoading();
    if (_error != null) return _buildError();
    if (_connections.isEmpty) return _buildEmpty();
    return _buildContent();
  }

  // ── Loading ──────────────────────────────────────────

  Widget _buildLoading() {
    return Center(
      child: Column(
        mainAxisSize: MainAxisSize.min,
        children: [
          SizedBox(
            width: 32,
            height: 32,
            child: CircularProgressIndicator(
              strokeWidth: 2,
              color: _accent.withValues(alpha: 0.7),
            ),
          ),
          const SizedBox(height: 16),
          Text(AppLocalizations.of(context).acctLoadingAccounts,
              style: GoogleFonts.sora(color: _textMuted, fontSize: 14)),
        ],
      ),
    );
  }

  // ── Error ────────────────────────────────────────────

  Widget _buildError() {
    return Center(
      child: Padding(
        padding: const EdgeInsets.all(32),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            Icon(Icons.error_outline, size: 48, color: _expense.withValues(alpha: 0.6)),
            const SizedBox(height: 16),
            Text(_error!, style: GoogleFonts.sora(color: _textMuted, fontSize: 14)),
            const SizedBox(height: 16),
            _GlassButton(
              label: 'Retry',
              icon: Icons.refresh,
              onTap: _loadConnections,
            ),
          ],
        ),
      ),
    );
  }

  // ── Empty state ──────────────────────────────────────

  Widget _buildEmpty() {
    return Center(
      child: Padding(
        padding: const EdgeInsets.all(32),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            Container(
              width: 80,
              height: 80,
              decoration: BoxDecoration(
                borderRadius: BorderRadius.circular(20),
                border: Border.all(color: _accent.withValues(alpha: 0.3)),
                color: _accent.withValues(alpha: 0.08),
              ),
              child: Icon(Icons.account_balance_outlined,
                  size: 40, color: _accent.withValues(alpha: 0.7)),
            ),
            const SizedBox(height: 24),
            Text(
                _bankSources.contains(BankSource.plaid)
                    ? AppLocalizations.of(context).acctNoBanksConnected
                    : AppLocalizations.of(context).acctNoAccountsYet,
                style: GoogleFonts.instrumentSerif(
                    fontSize: 24, color: _textGhost)),
            const SizedBox(height: 8),
            Text(
              _bankSources.contains(BankSource.plaid)
                  ? AppLocalizations.of(context).acctEmptySubtitle
                  : AppLocalizations.of(context).acctSelfHostEmptyWithFeedsSubtitle,
              textAlign: TextAlign.center,
              style: GoogleFonts.sora(
                  fontSize: 14, color: _textMuted, height: 1.5),
            ),
            const SizedBox(height: 24),
            _GlassButton(
              key: const ValueKey('accounts-empty-add-bank'),
              label: _bankSources.contains(BankSource.plaid)
                  ? 'Connect Bank'
                  : AppLocalizations.of(context).acctAddBankTitle,
              icon: Icons.add,
              accent: true,
              onTap: _addBank,
            ),
            // Self-hosted and not linked: where people look for banks, a small way to the Cloud bank feed. Only the
            // owner can link, so a household member does not see it.
            if (cloudFeedNeedsLink(_bankSources) && _canLinkCloud) ...[
              const SizedBox(height: 12),
              TextButton.icon(
                key: const ValueKey('accounts-empty-cloud-link'),
                onPressed: () => context.push('/settings/cloud'),
                icon: const Icon(Icons.cloud_outlined, size: 16, color: _accent),
                label: Text(AppLocalizations.of(context).acctCloudLinkNudge,
                    style: GoogleFonts.sora(
                        fontSize: 12, fontWeight: FontWeight.w600, color: _accent)),
              ),
            ],
          ],
        ),
      ),
    );
  }

  // ── Main content ─────────────────────────────────────

  Widget _buildContent() {
    return RefreshIndicator(
      // Pull-to-refresh triggers a full Plaid sync (not just a view reload) so the gesture
      // matches what the desktop "Sync" button does — pull down to sync your accounts.
      onRefresh: _syncAll,
      color: _accent,
      backgroundColor: _sheetBg,
      child: ListView(
        physics: const AlwaysScrollableScrollPhysics(),
        padding: const EdgeInsets.fromLTRB(16, 24, 16, 100),
        children: [
          // Header row
          _buildHeader(),
          const SizedBox(height: 16),

          // Status banners
          if (_needsAttention.isNotEmpty) ...[
            _buildStatusBanner(),
            const SizedBox(height: 16),
          ],
          if (_showMfaNudge) ...[
            _buildMfaWindowNudge(),
            const SizedBox(height: 16),
          ],

          // Summary cards
          _buildSummaryCards(),
          const SizedBox(height: 24),

          // Connections
          _buildSectionTitle('Connected Institutions'),
          const SizedBox(height: 12),
          ..._connections.map((c) => Padding(
                padding: const EdgeInsets.only(bottom: 16),
                child: _buildConnectionCard(c),
              )),
          const SizedBox(height: 16),

          // Add bank button
          _buildAddBankCard(),
        ],
      ),
    );
  }

  // ── Header ───────────────────────────────────────────

  Widget _buildHeader() {
    return Row(
      children: [
        Expanded(
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Text(AppLocalizations.of(context).acctPageTitle,
                  style: GoogleFonts.instrumentSerif(
                      fontSize: 28, color: _textGhost)),
              const SizedBox(height: 4),
              Row(
                children: [
                  Text(
                    AppLocalizations.of(context)
                        .acctAutoRefresh(_getRefreshMode()),
                    style: GoogleFonts.firaCode(
                        fontSize: 11, color: _textMuted),
                  ),
                  if (_isSyncing || _isConnecting) ...[
                    const SizedBox(width: 8),
                    SizedBox(
                      width: 12,
                      height: 12,
                      child: CircularProgressIndicator(
                        strokeWidth: 1.5,
                        color: _accent.withValues(alpha: 0.6),
                      ),
                    ),
                  ],
                ],
              ),
            ],
          ),
        ),
        // Desktop-only — on mobile the pull-to-refresh gesture triggers the same Plaid sync
        // (_syncAll), so "pull down to sync your accounts" replaces the button.
        if (showsManualRefresh(context))
          _GlassButton(
            label: 'Sync',
            icon: Icons.sync,
            onTap: _isSyncing ? null : _syncAll,
          ),
      ],
    );
  }

  // ── Status banner ────────────────────────────────────

  bool get _showMfaNudge =>
      !_mfaNudgeDismissed && (_mfaStatus?.isOnEmailWindow ?? false);

  /// Gentle upgrade prompt for users linking banks on the bounded email-code
  /// window. One line, dismissable for the session, links to Profile where the
  /// passkey/authenticator setup lives. Never blocks anything.
  Widget _buildMfaWindowNudge() {
    final days = _mfaStatus?.daysLeftInWindow;
    final when = days == null
        ? 'soon'
        : days == 0
            ? 'today'
            : days == 1
                ? 'tomorrow'
                : 'in $days days';
    return _GlassCard(
      borderColor: _accent.withValues(alpha: 0.3),
      child: Padding(
        padding: const EdgeInsets.fromLTRB(16, 14, 8, 14),
        child: Row(
          children: [
            Container(
              padding: const EdgeInsets.all(8),
              decoration: BoxDecoration(
                color: _accent.withValues(alpha: 0.15),
                borderRadius: BorderRadius.circular(10),
              ),
              child: const Icon(Icons.shield_outlined, color: _accent, size: 20),
            ),
            const SizedBox(width: 12),
            Expanded(
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Text(AppLocalizations.of(context).acctBankLinkingEnds(when),
                      style: GoogleFonts.sora(
                          fontSize: 13, fontWeight: FontWeight.w600, color: _textGhost)),
                  const SizedBox(height: 2),
                  Text(AppLocalizations.of(context).acctAddPasskeyNudge,
                      style: GoogleFonts.sora(fontSize: 12, color: _textMuted, height: 1.4)),
                ],
              ),
            ),
            TextButton(
              onPressed: () => context.push('/profile'),
              child: Text(AppLocalizations.of(context).acctAdd,
                  style: GoogleFonts.sora(
                      fontSize: 13, fontWeight: FontWeight.w600, color: _accent)),
            ),
            IconButton(
              tooltip: 'Dismiss',
              onPressed: () => setState(() => _mfaNudgeDismissed = true),
              icon: const Icon(Icons.close, size: 18, color: _textMuted),
            ),
          ],
        ),
      ),
    );
  }

  Widget _buildStatusBanner() {
    final count = _needsAttention.length;
    final single = count == 1;
    final name = single ? _needsAttention.first.institutionName : '$count connections';

    return _GlassCard(
      borderColor: _expense.withValues(alpha: 0.3),
      child: InkWell(
        onTap: single
            ? () {
                final c = _needsAttention.first;
                c.isActive ? _updateConnection(c) : _reconnectConnection(c);
              }
            : null,
        borderRadius: BorderRadius.circular(16),
        child: Padding(
          padding: const EdgeInsets.all(16),
          child: Row(
            children: [
              Container(
                padding: const EdgeInsets.all(8),
                decoration: BoxDecoration(
                  color: _expense.withValues(alpha: 0.15),
                  borderRadius: BorderRadius.circular(10),
                ),
                child: Icon(Icons.warning_amber_rounded,
                    color: _expense, size: 20),
              ),
              const SizedBox(width: 12),
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(
                      AppLocalizations.of(context)
                          .acctConnectionsNeedAttention(count),
                      style: GoogleFonts.sora(
                          fontSize: 14,
                          fontWeight: FontWeight.w600,
                          color: _textGhost),
                    ),
                    const SizedBox(height: 2),
                    Text(
                      single
                          ? (_needsAttention.first.isActive
                              ? AppLocalizations.of(context)
                                  .acctInstitutionNeedsLogin(name)
                              : AppLocalizations.of(context)
                                  .acctInstitutionDisconnected(name))
                          : AppLocalizations.of(context)
                              .acctSomeBanksRequireReconnection,
                      style: GoogleFonts.sora(fontSize: 12, color: _textMuted),
                    ),
                  ],
                ),
              ),
              Icon(Icons.chevron_right, color: _textMuted, size: 20),
            ],
          ),
        ),
      ),
    );
  }

  // ── Summary cards ────────────────────────────────────

  Widget _buildSummaryCards() {
    return LayoutBuilder(builder: (context, constraints) {
      final isWide = constraints.maxWidth > 600;
      final cards = [
        _buildSummaryCard(
          'Total Assets',
          formatCurrency(_totalAssets),
          Icons.account_balance_wallet,
          _income,
        ),
        _buildSummaryCard(
          'Credit Used',
          formatCurrency(_creditBalance),
          Icons.credit_card,
          _creditColor,
        ),
        if (_loanBalance > 0)
          _buildSummaryCard(
            'Loan Balance',
            formatCurrency(_loanBalance),
            Icons.account_balance,
            _loanColor,
          ),
        _buildSummaryCard(
          'Connections',
          '${_connections.length}',
          Icons.link,
          _accent,
          subtitle: '${_allAccounts.length} accounts',
        ),
      ];

      final grid = isWide
          ? Row(
              children: cards
                  .map((c) => Expanded(child: Padding(
                        padding: const EdgeInsets.symmetric(horizontal: 4),
                        child: c,
                      )))
                  .toList(),
            )
          : Wrap(
              spacing: 8,
              runSpacing: 8,
              children: cards
                  .map((c) => SizedBox(
                        width: (constraints.maxWidth - 8) / 2,
                        child: c,
                      ))
                  .toList(),
            );

      final foreign = _foreignBalances;
      if (foreign.isEmpty) return grid;

      // Different currencies can't be added together, so foreign-currency
      // accounts are excluded from the totals above and named here instead.
      final parts = (foreign.keys.toList()..sort())
          .map((code) => formatCurrencyCode(foreign[code]!, code))
          .join(' · ');
      return Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          grid,
          Padding(
            padding: const EdgeInsets.only(top: 8, left: 4),
            child: Row(
              children: [
                Icon(Icons.currency_exchange, size: 12, color: _textMuted),
                const SizedBox(width: 6),
                Expanded(
                  child: Text(
                    AppLocalizations.of(context)
                        .accountsForeignCurrencyNote(parts, _foreignAccountCount),
                    style: GoogleFonts.sora(fontSize: 11, color: _textMuted),
                  ),
                ),
              ],
            ),
          ),
        ],
      );
    });
  }

  Widget _buildSummaryCard(
    String label,
    String value,
    IconData icon,
    Color color, {
    String? subtitle,
  }) {
    return _GlassCard(
      child: Padding(
        padding: const EdgeInsets.all(16),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Row(
              children: [
                Icon(icon, size: 16, color: color.withValues(alpha: 0.7)),
                const SizedBox(width: 6),
                Text(label,
                    style: GoogleFonts.sora(fontSize: 12, color: _textMuted)),
              ],
            ),
            const SizedBox(height: 8),
            Text(
              value,
              style: GoogleFonts.firaCode(
                fontSize: 18,
                fontWeight: FontWeight.w700,
                color: color,
              ),
            ),
            // Always reserve the subtitle line so every summary card is the same height,
            // regardless of whether it has a subtitle. Without this, "Connections" (the only
            // card with a subtitle) renders taller than the others — a Wrap/Row doesn't
            // stretch siblings to equal height, so unequal content = unequal cards (#676).
            const SizedBox(height: 2),
            Text(subtitle ?? '',
                style: GoogleFonts.sora(fontSize: 11, color: _textMuted)),
          ],
        ),
      ),
    );
  }

  // ── Section title ────────────────────────────────────

  Widget _buildSectionTitle(String text) {
    return Text(
      text,
      style: GoogleFonts.sora(
        fontSize: 16,
        fontWeight: FontWeight.w600,
        color: _textGhost,
      ),
    );
  }

  // ── Connection card ──────────────────────────────────

  Widget _buildConnectionCard(PlaidConnection connection) {
    final progress = _importProgress[connection.id];
    final accounts = connection.accounts;
    final categories = _categorizeAccounts(accounts);

    return _GlassCard(
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          // Import progress bar (if active)
          if (progress != null && progress.isActive)
            _buildImportProgress(progress),

          // Header
          Padding(
            padding: const EdgeInsets.fromLTRB(16, 16, 8, 0),
            child: Row(
              children: [
                // Bank icon
                Container(
                  width: 44,
                  height: 44,
                  decoration: BoxDecoration(
                    borderRadius: BorderRadius.circular(12),
                    border: Border.all(color: _cardBorder),
                    color: _accent.withValues(alpha: 0.08),
                  ),
                  child: Icon(Icons.account_balance,
                      size: 22, color: _accent.withValues(alpha: 0.7)),
                ),
                const SizedBox(width: 12),
                // Name + status
                Expanded(
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Row(
                        children: [
                          Flexible(
                            child: Text(
                              connection.institutionName,
                              style: GoogleFonts.sora(
                                fontSize: 15,
                                fontWeight: FontWeight.w600,
                                color: _textGhost,
                              ),
                              overflow: TextOverflow.ellipsis,
                            ),
                          ),
                          const SizedBox(width: 8),
                          _StatusBadge(
                              isActive: connection.isActive &&
                                  !connection.requiresUserAction),
                        ],
                      ),
                      const SizedBox(height: 2),
                      Text(
                        AppLocalizations.of(context).acctAccountCountLastSync(
                            accounts.length,
                            _formatLastSync(connection.lastSyncAt)),
                        style: GoogleFonts.sora(fontSize: 12, color: _textMuted),
                      ),
                    ],
                  ),
                ),
                // Menu
                _buildConnectionMenu(connection),
              ],
            ),
          ),

          // Owner name (family context)
          if (connection.ownerName != null) ...[
            Padding(
              padding: const EdgeInsets.only(left: 72, top: 2),
              child: Text(
                AppLocalizations.of(context)
                    .acctOwnedBy(connection.ownerName!),
                style: GoogleFonts.sora(fontSize: 11, color: _textMuted),
              ),
            ),
          ],

          const SizedBox(height: 12),

          // Categorized accounts
          ...categories.entries.map((entry) => _buildAccountCategory(
                entry.key,
                entry.value,
              )),

          const SizedBox(height: 8),
        ],
      ),
    );
  }

  Widget _buildConnectionMenu(PlaidConnection connection) {
    return PopupMenuButton<String>(
      icon: Icon(Icons.more_vert, color: _textMuted, size: 20),
      color: _sheetBg,
      shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(12)),
      onSelected: (value) {
        switch (value) {
          case 'sync':
            _syncConnection(connection);
          case 'update':
            _updateConnection(connection);
          case 'reconnect':
            _reconnectConnection(connection);
          case 'remove':
            _removeConnection(connection);
        }
      },
      itemBuilder: (_) => [
        if (connection.isActive) ...[
          // SimpleFIN syncs on its own schedule; there is nothing to refresh by hand.
          if (!connection.isSimpleFin && !connection.isBridge) _menuItem('sync', Icons.sync, 'Sync Now'),
          _menuItem('update', Icons.settings, 'Update Connection'),
          _menuItem('remove', Icons.delete_outline, 'Disconnect',
              color: _expense),
        ] else ...[
          _menuItem('reconnect', Icons.refresh, 'Reconnect'),
          _menuItem('remove', Icons.delete_outline, 'Remove',
              color: _expense),
        ],
      ],
    );
  }

  PopupMenuItem<String> _menuItem(
    String value,
    IconData icon,
    String label, {
    Color? color,
  }) {
    final c = color ?? _textGhost;
    return PopupMenuItem(
      value: value,
      child: Row(
        children: [
          Icon(icon, size: 18, color: c),
          const SizedBox(width: 12),
          Text(label, style: GoogleFonts.sora(fontSize: 13, color: c)),
        ],
      ),
    );
  }

  // ── Import progress ──────────────────────────────────

  Widget _buildImportProgress(ImportProgress progress) {
    return Container(
      width: double.infinity,
      padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 10),
      decoration: BoxDecoration(
        color: _accent.withValues(alpha: 0.08),
        borderRadius: const BorderRadius.vertical(top: Radius.circular(16)),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Row(
            children: [
              SizedBox(
                width: 14,
                height: 14,
                child: CircularProgressIndicator(
                  strokeWidth: 1.5,
                  color: _accent,
                ),
              ),
              const SizedBox(width: 8),
              Text(
                progress.status == 'Pending'
                    ? AppLocalizations.of(context).acctImportPending
                    : AppLocalizations.of(context).acctImportingTransactions,
                style: GoogleFonts.sora(fontSize: 12, color: _textGhost),
              ),
              const Spacer(),
              Text(
                '${progress.progress}%',
                style: GoogleFonts.firaCode(fontSize: 12, color: _accent),
              ),
            ],
          ),
          const SizedBox(height: 6),
          ClipRRect(
            borderRadius: BorderRadius.circular(3),
            child: LinearProgressIndicator(
              value: progress.progress / 100,
              minHeight: 4,
              backgroundColor: _cardBorder,
              valueColor: AlwaysStoppedAnimation(_accent),
            ),
          ),
          if (progress.quickSyncCompletedAt != null) ...[
            const SizedBox(height: 4),
            Text(
              AppLocalizations.of(context).acctQuickSyncComplete,
              style: GoogleFonts.sora(fontSize: 10, color: _income),
            ),
          ],
        ],
      ),
    );
  }

  // ── Account categorization ───────────────────────────

  Map<String, List<Account>> _categorizeAccounts(List<Account> accounts) {
    final map = <String, List<Account>>{};
    for (final a in accounts) {
      final category = _categoryLabel(a.type);
      (map[category] ??= []).add(a);
    }
    // Sort categories in preferred order
    final order = ['Depository', 'Investment', 'Credit', 'Loans & Mortgages', 'Other'];
    final sorted = <String, List<Account>>{};
    for (final key in order) {
      if (map.containsKey(key)) sorted[key] = map[key]!;
    }
    return sorted;
  }

  String _categoryLabel(int type) {
    return switch (type) {
      0 => 'Depository',
      1 => 'Investment',
      2 => 'Credit',
      3 || 4 => 'Loans & Mortgages',
      _ => 'Other',
    };
  }

  Color _categoryColor(String category) {
    return switch (category) {
      'Depository' => _depositoryColor,
      'Investment' => _investmentColor,
      'Credit' => _creditColor,
      'Loans & Mortgages' => _loanColor,
      _ => _textMuted,
    };
  }

  Widget _buildAccountCategory(String category, List<Account> accounts) {
    final color = _categoryColor(category);

    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        // Category header
        Padding(
          padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 4),
          child: Text(
            category.toUpperCase(),
            style: GoogleFonts.sora(
              fontSize: 10,
              fontWeight: FontWeight.w600,
              color: color.withValues(alpha: 0.7),
              letterSpacing: 1.2,
            ),
          ),
        ),
        // Account tiles
        ...accounts.map((a) => _buildAccountTile(a, color)),
      ],
    );
  }

  // ── Account tile ─────────────────────────────────────

  Widget _buildAccountTile(Account account, Color categoryColor) {
    final isEditing = _editingAccountId == account.id;
    final balance = account.currentBalance;
    final isNegative = balance < 0;

    return Padding(
      padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 2),
      child: Material(
        color: Colors.transparent,
        child: InkWell(
          onTap: () {},
          borderRadius: BorderRadius.circular(10),
          hoverColor: _cardBg,
          child: Padding(
            padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 8),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Row(
                  children: [
                // Type icon
                Container(
                  width: 34,
                  height: 34,
                  decoration: BoxDecoration(
                    color: categoryColor.withValues(alpha: 0.1),
                    borderRadius: BorderRadius.circular(8),
                  ),
                  child: Icon(
                    _accountIcon(account.type, account.subtype),
                    size: 16,
                    color: categoryColor,
                  ),
                ),
                const SizedBox(width: 10),

                // Name + subtype
                Expanded(
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      if (isEditing)
                        _buildNicknameEditor(account)
                      else
                        Row(
                          children: [
                            Flexible(
                              child: Text(
                                account.displayName,
                                style: GoogleFonts.sora(
                                  fontSize: 13,
                                  fontWeight: FontWeight.w500,
                                  color: _textGhost,
                                ),
                                overflow: TextOverflow.ellipsis,
                              ),
                            ),
                            // The tap target used to be the glyph itself: a 13px icon
                            // with no padding, so roughly 13x13 of touchable area
                            // against a 48x48 guideline. Padding (not a bigger glyph)
                            // does the work, so the row doesn't grow to match — the
                            // icon only goes 13->16 so it's easier to see, and the
                            // alpha lifts because a control you can't spot is as good
                            // as one you can't hit.
                            InkWell(
                              onTap: () => _startEditingNickname(account),
                              borderRadius: BorderRadius.circular(22),
                              child: Padding(
                                padding: const EdgeInsets.all(12),
                                child: Icon(Icons.edit,
                                    size: 16,
                                    color: _textMuted.withValues(alpha: 0.8)),
                              ),
                            ),
                          ],
                        ),
                      Text(
                        '${_subtypeLabel(account.subtype)} ···${account.mask ?? ''}',
                        style: GoogleFonts.sora(fontSize: 11, color: _textMuted),
                      ),
                    ],
                  ),
                ),

                // Balance — formatted in the account's own currency, with the
                // ISO code alongside when it isn't the primary currency.
                Column(
                  crossAxisAlignment: CrossAxisAlignment.end,
                  children: [
                    Text(
                      formatCurrencyCode(balance.abs(), account.currency),
                      style: GoogleFonts.firaCode(
                        fontSize: 13,
                        fontWeight: FontWeight.w600,
                        color: isNegative ? _expense : _textGhost,
                      ),
                    ),
                    if (account.availableBalance != null)
                      Text(
                        AppLocalizations.of(context).acctAvailBalance(
                            formatCurrencyCode(
                                account.availableBalance!, account.currency)),
                        style: GoogleFonts.firaCode(fontSize: 10, color: _textMuted),
                      ),
                  ],
                ),
              ],
            ),
                if (_familyMembers.isNotEmpty)
                  _buildAssociationSelector(account),
              ],
            ),
          ),
        ),
      ),
    );
  }

  /// Per-account selector: which budget this card's spend feeds.
  /// "Family budget" (null) or a specific family member's budget.
  Widget _buildAssociationSelector(Account account) {
    // Guard against a stale association (e.g. a removed member) so the dropdown
    // always has a matching item; otherwise it falls back to "Family budget".
    final selected =
        _familyMembers.any((m) => m.userId == account.associatedUserId)
            ? account.associatedUserId
            : null;
    return Padding(
      padding: const EdgeInsets.only(left: 44, top: 4, bottom: 2),
      child: Row(
        children: [
          Icon(Icons.account_tree_outlined,
              size: 12, color: _textMuted.withValues(alpha: 0.6)),
          const SizedBox(width: 6),
          Text(AppLocalizations.of(context).acctFeeds,
              style: GoogleFonts.sora(fontSize: 11, color: _textMuted)),
          const SizedBox(width: 8),
          DropdownButtonHideUnderline(
            child: DropdownButton<int?>(
              value: selected,
              isDense: true,
              dropdownColor: _menuBg,
              borderRadius: BorderRadius.circular(8),
              icon: Icon(Icons.arrow_drop_down, size: 16, color: _textMuted),
              style: GoogleFonts.sora(fontSize: 11, color: _textGhost),
              items: [
                DropdownMenuItem<int?>(
                  value: null,
                  child: Text(AppLocalizations.of(context).acctFamilyBudget,
                      style:
                          GoogleFonts.sora(fontSize: 11, color: _textGhost)),
                ),
                ..._familyMembers.map((m) => DropdownMenuItem<int?>(
                      value: m.userId,
                      child: Text(
                          m.isCurrentUser
                              ? AppLocalizations.of(context)
                                  .acctMemberYou(m.displayName)
                              : m.displayName,
                          style: GoogleFonts.sora(
                              fontSize: 11, color: _textGhost)),
                    )),
              ],
              onChanged: (value) {
                if (value != selected) {
                  _setAccountAssociation(account, value);
                }
              },
            ),
          ),
        ],
      ),
    );
  }

  Widget _buildNicknameEditor(Account account) {
    return Row(
      children: [
        Expanded(
          child: SizedBox(
            height: 28,
            child: TextField(
              controller: _nicknameController,
              autofocus: true,
              style: GoogleFonts.sora(fontSize: 13, color: _textGhost),
              decoration: InputDecoration(
                isDense: true,
                contentPadding:
                    const EdgeInsets.symmetric(horizontal: 8, vertical: 6),
                hintText: 'Nickname…',
                hintStyle: GoogleFonts.sora(fontSize: 13, color: _textMuted),
                filled: true,
                fillColor: _cardBg,
                border: OutlineInputBorder(
                  borderRadius: BorderRadius.circular(6),
                  borderSide: BorderSide(color: _accent.withValues(alpha: 0.3)),
                ),
                enabledBorder: OutlineInputBorder(
                  borderRadius: BorderRadius.circular(6),
                  borderSide: BorderSide(color: _cardBorder),
                ),
                focusedBorder: OutlineInputBorder(
                  borderRadius: BorderRadius.circular(6),
                  borderSide: BorderSide(color: _accent.withValues(alpha: 0.5)),
                ),
              ),
              onSubmitted: (_) => _saveNickname(account),
            ),
          ),
        ),
        const SizedBox(width: 4),
        if (_isSavingNickname)
          SizedBox(
            width: 20,
            height: 20,
            child: CircularProgressIndicator(strokeWidth: 1.5, color: _accent),
          )
        else ...[
          _tinyButton(Icons.check, _income, () => _saveNickname(account)),
          _tinyButton(Icons.close, _expense, _cancelEditingNickname),
        ],
      ],
    );
  }

  Widget _tinyButton(IconData icon, Color color, VoidCallback onTap) {
    return InkWell(
      onTap: onTap,
      borderRadius: BorderRadius.circular(4),
      child: Padding(
        padding: const EdgeInsets.all(4),
        child: Icon(icon, size: 16, color: color),
      ),
    );
  }

  // ── Add bank card ────────────────────────────────────

  Widget _buildAddBankCard() {
    return _GlassCard(
      borderColor: _accent.withValues(alpha: 0.2),
      child: InkWell(
        onTap: _addBank,
        borderRadius: BorderRadius.circular(16),
        child: Padding(
          padding: const EdgeInsets.all(20),
          child: Row(
            mainAxisAlignment: MainAxisAlignment.center,
            children: [
              Container(
                padding: const EdgeInsets.all(8),
                decoration: BoxDecoration(
                  color: _accent.withValues(alpha: 0.1),
                  borderRadius: BorderRadius.circular(10),
                ),
                child: Icon(Icons.add, color: _accent, size: 20),
              ),
              const SizedBox(width: 12),
              Text(
                  _plaidOnly
                      ? AppLocalizations.of(context).acctConnectAnotherBank
                      : AppLocalizations.of(context).acctAddBankTitle,
                  style: GoogleFonts.sora(
                    fontSize: 14,
                    fontWeight: FontWeight.w600,
                    color: _accent,
                  )),
            ],
          ),
        ),
      ),
    );
  }

  // ── Helpers ──────────────────────────────────────────

  String _formatLastSync(DateTime lastSync) {
    final diff = DateTime.now().difference(lastSync);
    if (diff.inMinutes < 1) return 'just now';
    if (diff.inMinutes < 60) return '${diff.inMinutes}m ago';
    if (diff.inHours < 24) return '${diff.inHours}h ago';
    if (diff.inDays < 7) return '${diff.inDays}d ago';
    return formatDate(lastSync);
  }

  IconData _accountIcon(int type, String? subtype) {
    final s = subtype?.toLowerCase() ?? '';
    if (type == 2) return Icons.credit_card;
    if (type == 3 || type == 4) return Icons.account_balance;
    if (type == 1) return Icons.trending_up;
    if (s == 'checking') return Icons.account_balance;
    if (s == 'savings') return Icons.savings;
    return Icons.account_balance_wallet;
  }

  String _subtypeLabel(String? subtype) {
    if (subtype == null || subtype.isEmpty) return '';
    return subtype[0].toUpperCase() + subtype.substring(1);
  }

  // ── Bottom sheets ────────────────────────────────────

  void _showConnectBankSheet() {
    showModalBottomSheet(
      context: context,
      isScrollControlled: true,
      backgroundColor: Colors.transparent,
      builder: (_) => _ConnectBankSheet(
        onConnect: _openPlaidLink,
        isLoading: _isConnecting,
      ),
    );
  }

  Future<bool?> _showDisconnectSheet(PlaidConnection connection) {
    return showModalBottomSheet<bool>(
      context: context,
      backgroundColor: Colors.transparent,
      builder: (_) => _DisconnectSheet(connection: connection),
    );
  }
}

// ═════════════════════════════════════════════════════════
//  REUSABLE WIDGETS
// ═════════════════════════════════════════════════════════

/// Glassmorphic card with blur border.
class _GlassCard extends StatelessWidget {
  final Widget child;
  final Color? borderColor;

  const _GlassCard({required this.child, this.borderColor});

  @override
  Widget build(BuildContext context) {
    return Container(
      decoration: BoxDecoration(
        color: _cardBg,
        borderRadius: BorderRadius.circular(16),
        border: Border.all(color: borderColor ?? _cardBorder),
        boxShadow: [
          BoxShadow(
            color: const Color(0xFF7B61FF).withValues(alpha: 0.03),
            blurRadius: 24,
            spreadRadius: 0,
          ),
        ],
      ),
      child: child,
    );
  }
}

/// Status badge (Active / Disconnected).
class _StatusBadge extends StatelessWidget {
  final bool isActive;
  const _StatusBadge({required this.isActive});

  @override
  Widget build(BuildContext context) {
    final color = isActive ? _income : _expense;
    final label = isActive ? 'Active' : 'Disconnected';

    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 2),
      decoration: BoxDecoration(
        color: color.withValues(alpha: 0.12),
        borderRadius: BorderRadius.circular(6),
      ),
      child: Text(
        label,
        style: GoogleFonts.sora(
            fontSize: 10, fontWeight: FontWeight.w600, color: color),
      ),
    );
  }
}

/// Compact glass-style button.
class _GlassButton extends StatelessWidget {
  final String label;
  final IconData icon;
  final VoidCallback? onTap;
  final bool accent;

  const _GlassButton({
    super.key,
    required this.label,
    required this.icon,
    this.onTap,
    this.accent = false,
  });

  @override
  Widget build(BuildContext context) {
    final color = accent ? _accent : _textGhost;
    final bg = accent ? _accent.withValues(alpha: 0.15) : _cardBg;

    return Material(
      color: Colors.transparent,
      child: InkWell(
        onTap: onTap,
        borderRadius: BorderRadius.circular(10),
        child: Container(
          padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 8),
          decoration: BoxDecoration(
            color: bg,
            borderRadius: BorderRadius.circular(10),
            border: Border.all(
                color: accent
                    ? _accent.withValues(alpha: 0.3)
                    : _cardBorder),
          ),
          child: Row(
            mainAxisSize: MainAxisSize.min,
            children: [
              Icon(icon, size: 16, color: color),
              const SizedBox(width: 6),
              Text(label,
                  style: GoogleFonts.sora(
                      fontSize: 12,
                      fontWeight: FontWeight.w500,
                      color: color)),
            ],
          ),
        ),
      ),
    );
  }
}

// ═════════════════════════════════════════════════════════
//  ADD A BANK CHOOSER
// ═════════════════════════════════════════════════════════

/// Lists the ways this server can bring accounts in. Public so a widget test can build it with any list.
class AddBankChooserSheet extends StatelessWidget {
  final List<BankSource> sources;
  final ValueChanged<BankSource> onSelected;

  /// The Cloud bank feed is listed but this server is not linked yet: its row says so.
  final bool cloudNeedsLink;

  const AddBankChooserSheet({
    super.key,
    required this.sources,
    required this.onSelected,
    this.cloudNeedsLink = false,
  });

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    return Container(
      decoration: const BoxDecoration(
        color: _sheetBg,
        borderRadius: BorderRadius.vertical(top: Radius.circular(24)),
        border: Border(
          top: BorderSide(color: _cardBorder),
          left: BorderSide(color: _cardBorder),
          right: BorderSide(color: _cardBorder),
        ),
      ),
      padding: EdgeInsets.only(
        left: 24,
        right: 24,
        top: 16,
        bottom: MediaQuery.of(context).viewInsets.bottom + 24,
      ),
      child: SafeArea(
        top: false,
        child: SingleChildScrollView(
          child: Column(
            mainAxisSize: MainAxisSize.min,
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              Center(
                child: Container(
                  width: 40,
                  height: 4,
                  decoration: BoxDecoration(
                    color: _textMuted.withValues(alpha: 0.3),
                    borderRadius: BorderRadius.circular(2),
                  ),
                ),
              ),
              const SizedBox(height: 20),
              Text(l10n.acctAddBankTitle,
                  style: GoogleFonts.instrumentSerif(fontSize: 24, color: _textGhost)),
              const SizedBox(height: 6),
              Text(l10n.acctAddBankSubtitle,
                  style: GoogleFonts.sora(fontSize: 13, color: _textMuted, height: 1.5)),
              const SizedBox(height: 16),
              for (final source in sources) ...[
                _option(context, source),
                const SizedBox(height: 10),
              ],
            ],
          ),
        ),
      ),
    );
  }

  Widget _option(BuildContext context, BankSource source) {
    final l10n = AppLocalizations.of(context);
    final (IconData icon, String title, String subtitle) = switch (source) {
      BankSource.plaid => (Icons.account_balance, l10n.acctSourcePlaidTitle, l10n.acctSourcePlaidSubtitle),
      BankSource.cloudFeed => (
          Icons.cloud_outlined,
          l10n.acctSourceCloudTitle,
          cloudNeedsLink ? l10n.acctSourceCloudNeedsLink : l10n.acctSourceCloudSubtitle,
        ),
      BankSource.simpleFin => (Icons.vpn_key_outlined, l10n.acctSourceSimpleFinTitle, l10n.acctSourceSimpleFinSubtitle),
      BankSource.importFile => (Icons.upload_file_rounded, l10n.acctSourceImportTitle, l10n.acctSourceImportSubtitle),
    };
    return Material(
      color: Colors.transparent,
      child: InkWell(
        key: ValueKey('add-bank-${source.name}'),
        onTap: () => onSelected(source),
        borderRadius: BorderRadius.circular(14),
        child: Container(
          padding: const EdgeInsets.all(14),
          decoration: BoxDecoration(
            color: _cardBg,
            borderRadius: BorderRadius.circular(14),
            border: Border.all(color: _cardBorder),
          ),
          child: Row(
            children: [
              Container(
                padding: const EdgeInsets.all(10),
                decoration: BoxDecoration(
                  color: _accent.withValues(alpha: 0.12),
                  borderRadius: BorderRadius.circular(10),
                ),
                child: Icon(icon, color: _accent, size: 20),
              ),
              const SizedBox(width: 14),
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(title,
                        style: GoogleFonts.sora(
                            fontSize: 14, fontWeight: FontWeight.w600, color: _textGhost)),
                    const SizedBox(height: 2),
                    Text(subtitle,
                        style: GoogleFonts.sora(fontSize: 12, color: _textMuted, height: 1.4)),
                  ],
                ),
              ),
              const Icon(Icons.chevron_right, color: _textMuted, size: 20),
            ],
          ),
        ),
      ),
    );
  }
}

// ═════════════════════════════════════════════════════════
//  LINK WITH BUOYAI CLOUD (self-hosted, not linked yet)
// ═════════════════════════════════════════════════════════

/// Chosen "BuoyAI Cloud bank feed" on a server that is not linked yet: what to do next, in one step. Public so a
/// widget test can build it.
class CloudLinkPromptSheet extends StatelessWidget {
  final VoidCallback onLink;
  final VoidCallback onClose;

  /// A household member cannot link the server: the sheet says the owner manages it and offers no link button.
  final bool ownerManages;

  const CloudLinkPromptSheet(
      {super.key, required this.onLink, required this.onClose, this.ownerManages = false});

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    return Container(
      decoration: const BoxDecoration(
        color: _sheetBg,
        borderRadius: BorderRadius.vertical(top: Radius.circular(24)),
        border: Border(
          top: BorderSide(color: _cardBorder),
          left: BorderSide(color: _cardBorder),
          right: BorderSide(color: _cardBorder),
        ),
      ),
      padding: const EdgeInsets.fromLTRB(24, 16, 24, 24),
      child: SafeArea(
        top: false,
        child: SingleChildScrollView(
          child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            Center(
              child: Container(
                width: 40,
                height: 4,
                decoration: BoxDecoration(
                  color: _textMuted.withValues(alpha: 0.3),
                  borderRadius: BorderRadius.circular(2),
                ),
              ),
            ),
            const SizedBox(height: 20),
            Align(
              alignment: Alignment.centerLeft,
              child: Container(
                width: 56,
                height: 56,
                alignment: Alignment.center,
                decoration: BoxDecoration(
                  color: _accent.withValues(alpha: 0.12),
                  borderRadius: BorderRadius.circular(16),
                  border: Border.all(color: _accent.withValues(alpha: 0.3)),
                ),
                child: const Icon(Icons.cloud_outlined, color: _accent, size: 28),
              ),
            ),
            const SizedBox(height: 16),
            Text(ownerManages ? l10n.acctCloudNotLinkedTitle : l10n.acctCloudLinkTitle,
                style: GoogleFonts.instrumentSerif(fontSize: 24, color: _textGhost)),
            const SizedBox(height: 6),
            Text(ownerManages ? l10n.ownerManagesThis : l10n.acctCloudLinkBody,
                key: ownerManages ? const ValueKey('cloud-link-prompt-owner-manages') : null,
                style: GoogleFonts.sora(fontSize: 13, color: _textMuted, height: 1.5)),
            const SizedBox(height: 20),
            if (!ownerManages) Material(
              color: _accent.withValues(alpha: 0.85),
              borderRadius: BorderRadius.circular(12),
              child: InkWell(
                key: const ValueKey('cloud-link-prompt-link'),
                onTap: onLink,
                borderRadius: BorderRadius.circular(12),
                child: Padding(
                  padding: const EdgeInsets.symmetric(vertical: 14),
                  child: Row(
                    mainAxisAlignment: MainAxisAlignment.center,
                    children: [
                      const Icon(Icons.link_rounded, size: 18, color: Colors.white),
                      const SizedBox(width: 8),
                      Text(l10n.cloudLinkStart,
                          style: GoogleFonts.sora(
                              fontSize: 14, fontWeight: FontWeight.w600, color: Colors.white)),
                    ],
                  ),
                ),
              ),
            ),
            const SizedBox(height: 6),
            TextButton(
              key: const ValueKey('cloud-link-prompt-close'),
              onPressed: onClose,
              child: Text(ownerManages ? l10n.acctCloudOk : l10n.acctCloudLinkNotNow,
                  style: GoogleFonts.sora(fontSize: 13, color: _textMuted)),
            ),
          ],
          ),
        ),
      ),
    );
  }
}

// ═════════════════════════════════════════════════════════
//  CONNECT BANK SHEET
// ═════════════════════════════════════════════════════════

class _ConnectBankSheet extends StatelessWidget {
  final VoidCallback onConnect;
  final bool isLoading;

  const _ConnectBankSheet({
    required this.onConnect,
    required this.isLoading,
  });

  @override
  Widget build(BuildContext context) {
    return Container(
      decoration: const BoxDecoration(
        color: _sheetBg,
        borderRadius: BorderRadius.vertical(top: Radius.circular(24)),
        border: Border(
          top: BorderSide(color: _cardBorder),
          left: BorderSide(color: _cardBorder),
          right: BorderSide(color: _cardBorder),
        ),
      ),
      padding: EdgeInsets.only(
        left: 24,
        right: 24,
        top: 16,
        bottom: MediaQuery.of(context).viewInsets.bottom + 24,
      ),
      child: Column(
        mainAxisSize: MainAxisSize.min,
        children: [
          // Handle
          Container(
            width: 40,
            height: 4,
            decoration: BoxDecoration(
              color: _textMuted.withValues(alpha: 0.3),
              borderRadius: BorderRadius.circular(2),
            ),
          ),
          const SizedBox(height: 24),

          // Icon
          Container(
            width: 64,
            height: 64,
            decoration: BoxDecoration(
              borderRadius: BorderRadius.circular(18),
              border: Border.all(color: _accent.withValues(alpha: 0.3)),
              color: _accent.withValues(alpha: 0.08),
            ),
            child: Icon(Icons.account_balance, size: 30, color: _accent),
          ),
          const SizedBox(height: 16),

          Text(AppLocalizations.of(context).acctConnectYourBankTitle,
              style: GoogleFonts.instrumentSerif(
                  fontSize: 24, color: _textGhost)),
          const SizedBox(height: 8),
          Text(
            AppLocalizations.of(context).acctConnectBankSubtitle,
            textAlign: TextAlign.center,
            style: GoogleFonts.sora(
                fontSize: 13, color: _textMuted, height: 1.5),
          ),
          const SizedBox(height: 20),

          // Security badges
          Row(
            mainAxisAlignment: MainAxisAlignment.center,
            children: [
              _securityBadge(Icons.lock, '256-bit encryption'),
              const SizedBox(width: 16),
              _securityBadge(Icons.verified_user, 'Bank-level security'),
            ],
          ),
          const SizedBox(height: 24),

          // Consent affirmation — shown before the Connect CTA so the user explicitly
          // agrees before we open Plaid Link.
          Text(
            AppLocalizations.of(context).acctPlaidConsent,
            textAlign: TextAlign.center,
            style: GoogleFonts.sora(fontSize: 11, color: _textMuted, height: 1.5),
          ),
          const SizedBox(height: 8),
          Row(
            mainAxisAlignment: MainAxisAlignment.center,
            children: [
              _consentLink('Privacy Policy', 'https://buoy-ai.com/privacy-policy'),
              Text('   ·   ',
                  style: GoogleFonts.sora(fontSize: 11, color: _textMuted)),
              _consentLink("Plaid's Privacy Policy",
                  'https://plaid.com/legal/#end-user-privacy-policy'),
            ],
          ),
          const SizedBox(height: 16),

          // Connect button
          SizedBox(
            width: double.infinity,
            child: ElevatedButton(
              onPressed: isLoading ? null : onConnect,
              style: ElevatedButton.styleFrom(
                backgroundColor: _accent,
                foregroundColor: _textGhost,
                padding: const EdgeInsets.symmetric(vertical: 14),
                shape: RoundedRectangleBorder(
                  borderRadius: BorderRadius.circular(12),
                ),
              ),
              child: isLoading
                  ? SizedBox(
                      height: 20,
                      width: 20,
                      child: CircularProgressIndicator(
                          strokeWidth: 2, color: _textGhost),
                    )
                  : Text(AppLocalizations.of(context).acctConnectBank,
                      style: GoogleFonts.sora(
                          fontSize: 14, fontWeight: FontWeight.w600)),
            ),
          ),
          const SizedBox(height: 12),

          // Powered by Plaid
          Row(
            mainAxisAlignment: MainAxisAlignment.center,
            children: [
              Text(AppLocalizations.of(context).acctPoweredBy,
                  style: GoogleFonts.sora(fontSize: 11, color: _textMuted)),
              Text('Plaid',
                  style: GoogleFonts.sora(
                      fontSize: 11,
                      fontWeight: FontWeight.w700,
                      color: _textMuted)),
            ],
          ),
        ],
      ),
    );
  }

  Widget _securityBadge(IconData icon, String label) {
    return Row(
      mainAxisSize: MainAxisSize.min,
      children: [
        Icon(icon, size: 14, color: _accent.withValues(alpha: 0.6)),
        const SizedBox(width: 4),
        Text(label,
            style: GoogleFonts.sora(fontSize: 11, color: _textMuted)),
      ],
    );
  }

  Widget _consentLink(String label, String url) {
    return GestureDetector(
      onTap: () async {
        final uri = Uri.parse(url);
        try {
          await launchUrl(uri, mode: LaunchMode.externalApplication);
        } catch (_) {
          // Best-effort: a missing browser/handler shouldn't crash the sheet.
        }
      },
      child: Text(
        label,
        style: GoogleFonts.sora(
          fontSize: 11,
          color: _accent,
          decoration: TextDecoration.underline,
          decorationColor: _accent,
        ),
      ),
    );
  }
}

// ═════════════════════════════════════════════════════════
//  DISCONNECT SHEET
// ═════════════════════════════════════════════════════════

/// Asks which bank the user was looking for after Plaid couldn't find it. Pops the typed
/// name, or null when they skip.
class _BankRequestSheet extends StatefulWidget {
  final String? initialQuery;
  const _BankRequestSheet({this.initialQuery});

  @override
  State<_BankRequestSheet> createState() => _BankRequestSheetState();
}

class _BankRequestSheetState extends State<_BankRequestSheet> {
  late final TextEditingController _controller =
      TextEditingController(text: widget.initialQuery ?? '');

  @override
  void dispose() {
    _controller.dispose();
    super.dispose();
  }

  void _send() {
    final name = _controller.text.trim();
    if (name.isEmpty) return;
    Navigator.pop(context, name);
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    // A modal sheet is its own route above the app shell, so it lifts itself over the
    // keyboard here; the shell's own inset handling does not reach it.
    return Padding(
      padding: EdgeInsets.only(bottom: MediaQuery.viewInsetsOf(context).bottom),
      child: Container(
        decoration: const BoxDecoration(
          color: _sheetBg,
          borderRadius: BorderRadius.vertical(top: Radius.circular(24)),
          border: Border(
            top: BorderSide(color: _cardBorder),
            left: BorderSide(color: _cardBorder),
            right: BorderSide(color: _cardBorder),
          ),
        ),
        padding: const EdgeInsets.all(24),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            Container(
              width: 40,
              height: 4,
              decoration: BoxDecoration(
                color: _textMuted.withValues(alpha: 0.3),
                borderRadius: BorderRadius.circular(2),
              ),
            ),
            const SizedBox(height: 20),
            const Icon(Icons.account_balance_outlined, color: _accent, size: 36),
            const SizedBox(height: 12),
            Text(l10n.acctBankRequestTitle,
                style: GoogleFonts.sora(
                    fontSize: 18, fontWeight: FontWeight.w600, color: _textGhost),
                textAlign: TextAlign.center),
            const SizedBox(height: 8),
            Text(l10n.acctBankRequestBody,
                style: GoogleFonts.sora(fontSize: 13, color: _textMuted),
                textAlign: TextAlign.center),
            const SizedBox(height: 16),
            TextField(
              controller: _controller,
              autofocus: (widget.initialQuery ?? '').isEmpty,
              maxLength: 120,
              textCapitalization: TextCapitalization.words,
              textInputAction: TextInputAction.send,
              onSubmitted: (_) => _send(),
              style: GoogleFonts.sora(fontSize: 14, color: _textGhost),
              decoration: InputDecoration(
                hintText: l10n.acctBankRequestHint,
                hintStyle: GoogleFonts.sora(fontSize: 14, color: _textMuted),
                counterText: '',
                filled: true,
                fillColor: _cardBg,
                border: OutlineInputBorder(
                  borderRadius: BorderRadius.circular(10),
                  borderSide: const BorderSide(color: _cardBorder),
                ),
                enabledBorder: OutlineInputBorder(
                  borderRadius: BorderRadius.circular(10),
                  borderSide: const BorderSide(color: _cardBorder),
                ),
              ),
            ),
            const SizedBox(height: 16),
            Row(
              children: [
                Expanded(
                  child: OutlinedButton(
                    onPressed: () => Navigator.pop(context),
                    style: OutlinedButton.styleFrom(
                      foregroundColor: _textGhost,
                      side: const BorderSide(color: _cardBorder),
                      padding: const EdgeInsets.symmetric(vertical: 12),
                      shape: RoundedRectangleBorder(
                        borderRadius: BorderRadius.circular(10),
                      ),
                    ),
                    child: Text(l10n.acctBankRequestSkip,
                        style: GoogleFonts.sora(fontSize: 13)),
                  ),
                ),
                const SizedBox(width: 12),
                Expanded(
                  child: ElevatedButton(
                    onPressed: _send,
                    style: ElevatedButton.styleFrom(
                      backgroundColor: _accent,
                      foregroundColor: _textGhost,
                      padding: const EdgeInsets.symmetric(vertical: 12),
                      shape: RoundedRectangleBorder(
                        borderRadius: BorderRadius.circular(10),
                      ),
                    ),
                    child: Text(l10n.acctBankRequestSend,
                        style: GoogleFonts.sora(
                            fontSize: 13, fontWeight: FontWeight.w600)),
                  ),
                ),
              ],
            ),
          ],
        ),
      ),
    );
  }
}

class _DisconnectSheet extends StatelessWidget {
  final PlaidConnection connection;
  const _DisconnectSheet({required this.connection});

  @override
  Widget build(BuildContext context) {
    return Container(
      decoration: const BoxDecoration(
        color: _sheetBg,
        borderRadius: BorderRadius.vertical(top: Radius.circular(24)),
        border: Border(
          top: BorderSide(color: _cardBorder),
          left: BorderSide(color: _cardBorder),
          right: BorderSide(color: _cardBorder),
        ),
      ),
      padding: const EdgeInsets.all(24),
      child: Column(
        mainAxisSize: MainAxisSize.min,
        children: [
          // Handle
          Container(
            width: 40,
            height: 4,
            decoration: BoxDecoration(
              color: _textMuted.withValues(alpha: 0.3),
              borderRadius: BorderRadius.circular(2),
            ),
          ),
          const SizedBox(height: 20),

          Icon(Icons.warning_amber_rounded, color: _expense, size: 40),
          const SizedBox(height: 12),

          Text(
              AppLocalizations.of(context)
                  .acctDisconnectTitle(connection.institutionName),
              style: GoogleFonts.sora(
                  fontSize: 18, fontWeight: FontWeight.w600, color: _textGhost),
              textAlign: TextAlign.center),
          const SizedBox(height: 12),

          // Warning items
          _warningItem('Stop automatic transaction imports'),
          _warningItem('Permanently delete all transaction history',
              isDestructive: true),
          _warningItem('Deactivate all associated accounts'),
          const SizedBox(height: 8),
          Text(
            AppLocalizations.of(context).acctReconnectLaterNote,
            style: GoogleFonts.sora(fontSize: 12, color: _textMuted),
            textAlign: TextAlign.center,
          ),
          const SizedBox(height: 20),

          // Buttons
          Row(
            children: [
              Expanded(
                child: OutlinedButton(
                  onPressed: () => Navigator.pop(context, false),
                  style: OutlinedButton.styleFrom(
                    foregroundColor: _textGhost,
                    side: BorderSide(color: _cardBorder),
                    padding: const EdgeInsets.symmetric(vertical: 12),
                    shape: RoundedRectangleBorder(
                      borderRadius: BorderRadius.circular(10),
                    ),
                  ),
                  child: Text(AppLocalizations.of(context).acctCancel,
                      style: GoogleFonts.sora(fontSize: 13)),
                ),
              ),
              const SizedBox(width: 12),
              Expanded(
                child: ElevatedButton(
                  onPressed: () => Navigator.pop(context, true),
                  style: ElevatedButton.styleFrom(
                    backgroundColor: _expense,
                    foregroundColor: _textGhost,
                    padding: const EdgeInsets.symmetric(vertical: 12),
                    shape: RoundedRectangleBorder(
                      borderRadius: BorderRadius.circular(10),
                    ),
                  ),
                  child: Text(AppLocalizations.of(context).acctDisconnect,
                      style: GoogleFonts.sora(
                          fontSize: 13, fontWeight: FontWeight.w600)),
                ),
              ),
            ],
          ),
        ],
      ),
    );
  }

  Widget _warningItem(String text, {bool isDestructive = false}) {
    return Padding(
      padding: const EdgeInsets.symmetric(vertical: 3),
      child: Row(
        children: [
          Icon(Icons.circle, size: 5, color: isDestructive ? _expense : _textMuted),
          const SizedBox(width: 8),
          Expanded(
            child: Text(
              text,
              style: GoogleFonts.sora(
                fontSize: 12,
                color: isDestructive ? _expense : _textMuted,
                fontWeight: isDestructive ? FontWeight.w600 : FontWeight.normal,
              ),
            ),
          ),
        ],
      ),
    );
  }
}
