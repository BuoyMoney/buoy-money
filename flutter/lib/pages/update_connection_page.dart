import 'package:flutter/material.dart';
import 'dart:async';
import 'package:plaid_flutter/plaid_flutter.dart';
import '../l10n/generated/app_localizations.dart';
import '../services/accounts_service.dart';
import '../widgets/app_toast.dart';

/// Update Connection Page
/// 
/// Handles Plaid re-authentication when a bank connection requires update.
/// This is triggered when:
/// - Bank credentials have changed
/// - MFA requirements have changed
/// - Connection has been inactive for too long
class UpdateConnectionPage extends StatefulWidget {
  final int connectionId;
  final String? institutionName;

  /// Open Link update mode with account selection enabled — the NEW_ACCOUNTS_AVAILABLE
  /// flow, where the user adds accounts Plaid reported as newly available.
  final bool accountSelection;

  const UpdateConnectionPage({
    super.key,
    required this.connectionId,
    this.institutionName,
    this.accountSelection = false,
  });

  @override
  State<UpdateConnectionPage> createState() => _UpdateConnectionPageState();
}

class _UpdateConnectionPageState extends State<UpdateConnectionPage> {
  bool _isLoading = true;
  bool _isCreatingToken = false;
  bool _isUpdating = false;
  String? _error;
  PlaidConnection? _connection;
  LinkTokenConfiguration? _linkTokenConfig;

  // Plaid Link stream subscriptions
  StreamSubscription<LinkSuccess>? _plaidSuccessSubscription;
  StreamSubscription<LinkExit>? _plaidExitSubscription;
  StreamSubscription<LinkEvent>? _plaidEventSubscription;

  @override
  void initState() {
    super.initState();
    _loadConnection();
    _setupPlaidListeners();
  }

  @override
  void dispose() {
    _plaidSuccessSubscription?.cancel();
    _plaidExitSubscription?.cancel();
    _plaidEventSubscription?.cancel();
    super.dispose();
  }

  void _setupPlaidListeners() {
    _plaidSuccessSubscription = PlaidLink.onSuccess.listen(_onPlaidSuccess);
    _plaidExitSubscription = PlaidLink.onExit.listen(_onPlaidExit);
    _plaidEventSubscription = PlaidLink.onEvent.listen(_onPlaidEvent);
  }

  Future<void> _loadConnection() async {
    setState(() {
      _isLoading = true;
      _error = null;
    });

    try {
      final connections = await accountsService.getConnections();
      final connection = connections.where((c) => c.id == widget.connectionId).firstOrNull;

      if (connection == null) {
        setState(() {
          _error = 'Connection not found';
          _isLoading = false;
        });
        return;
      }

      setState(() {
        _connection = connection;
        _isLoading = false;
      });
    } catch (e) {
      setState(() {
        _error = 'Failed to load connection details';
        _isLoading = false;
      });
    }
  }

  Future<void> _createUpdateToken() async {
    setState(() {
      _isCreatingToken = true;
      _error = null;
    });

    try {
      final response = await accountsService.createUpdateToken(
        widget.connectionId,
        accountSelection: widget.accountSelection,
      );

      if (response.success && response.linkToken != null) {
        setState(() {
          _isCreatingToken = false;
        });
        _openPlaidLink(response.linkToken!);
      } else {
        setState(() {
          _error = response.error ?? 'Failed to create update token';
          _isCreatingToken = false;
        });
      }
    } catch (e) {
      setState(() {
        _error = 'Failed to start update process';
        _isCreatingToken = false;
      });
    }
  }

  void _openPlaidLink(String linkToken) {
    _linkTokenConfig = LinkTokenConfiguration(token: linkToken);

    PlaidLink.create(configuration: _linkTokenConfig!);
    PlaidLink.open();
  }

  Future<void> _onPlaidSuccess(LinkSuccess success) async {
    final publicToken = success.publicToken;

    setState(() {
      _isUpdating = true;
      _error = null;
    });

    try {
      final result = await accountsService.updateConnection(
        widget.connectionId,
        publicToken,
      );

      if (result && mounted) {
        showAppToast(
          context,
          'Your ${_connection?.institutionName ?? 'bank'} connection has been updated successfully.',
          type: AppToastType.success,
        );
        Navigator.of(context).pop(true); // Return success
      } else {
        setState(() {
          _error = 'Failed to update connection';
          _isUpdating = false;
        });
      }
    } catch (e) {
      setState(() {
        _error = 'Failed to update connection';
        _isUpdating = false;
      });
    }
  }

  void _onPlaidEvent(LinkEvent event) {
    debugPrint('Plaid Event: ${event.name}');
  }

  void _onPlaidExit(LinkExit exit) {
    if (exit.error != null) {
      // User didn't just cancel
      setState(() {
        _error = exit.error?.displayMessage ?? 'Update process was interrupted';
      });
    }
    // If no error, user just cancelled - no action needed
  }

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final colorScheme = theme.colorScheme;

    return Scaffold(
      appBar: AppBar(
        title: Text(AppLocalizations.of(context).connUpdateConnection),
        leading: IconButton(
          icon: const Icon(Icons.close),
          onPressed: () => Navigator.of(context).pop(false),
        ),
      ),
      body: SafeArea(
        child: _buildContent(theme, colorScheme),
      ),
    );
  }

  Widget _buildContent(ThemeData theme, ColorScheme colorScheme) {
    if (_isLoading) {
      return Center(
        child: Column(
          mainAxisAlignment: MainAxisAlignment.center,
          children: [
            const CircularProgressIndicator(),
            const SizedBox(height: 16),
            Text(AppLocalizations.of(context).connLoadingConnection),
          ],
        ),
      );
    }

    if (_isUpdating) {
      return Center(
        child: Column(
          mainAxisAlignment: MainAxisAlignment.center,
          children: [
            const CircularProgressIndicator(),
            const SizedBox(height: 16),
            Text(
              AppLocalizations.of(context).connUpdatingConnection,
              style: theme.textTheme.bodyLarge,
            ),
            const SizedBox(height: 8),
            Text(
              AppLocalizations.of(context).connPleaseWaitReconnect,
              style: theme.textTheme.bodyMedium?.copyWith(
                color: colorScheme.onSurfaceVariant,
              ),
            ),
          ],
        ),
      );
    }

    return SingleChildScrollView(
      padding: const EdgeInsets.all(24),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          // Connection info card
          _buildConnectionCard(theme, colorScheme),
          const SizedBox(height: 24),

          // Error message
          if (_error != null) ...[
            _buildErrorCard(theme, colorScheme),
            const SizedBox(height: 24),
          ],

          // Instructions
          _buildInstructions(theme, colorScheme),
          const SizedBox(height: 32),

          // Action button
          FilledButton.icon(
            onPressed: _isCreatingToken ? null : _createUpdateToken,
            icon: _isCreatingToken
                ? SizedBox(
                    width: 18,
                    height: 18,
                    child: CircularProgressIndicator(
                      strokeWidth: 2,
                      color: colorScheme.onPrimary,
                    ),
                  )
                : const Icon(Icons.link),
            label: Text(_isCreatingToken
                ? AppLocalizations.of(context).connConnecting
                : AppLocalizations.of(context).connUpdateConnection),
            style: FilledButton.styleFrom(
              padding: const EdgeInsets.symmetric(vertical: 16),
            ),
          ),
          const SizedBox(height: 16),

          // Cancel button
          OutlinedButton(
            onPressed: () => Navigator.of(context).pop(false),
            style: OutlinedButton.styleFrom(
              padding: const EdgeInsets.symmetric(vertical: 16),
            ),
            child: Text(AppLocalizations.of(context).uiCancel),
          ),
        ],
      ),
    );
  }

  Widget _buildConnectionCard(ThemeData theme, ColorScheme colorScheme) {
    final connection = _connection;
    final name = connection?.institutionName ?? widget.institutionName ?? 'Bank';

    return Card(
      child: Padding(
        padding: const EdgeInsets.all(20),
        child: Column(
          children: [
            // Institution icon
            Container(
              padding: const EdgeInsets.all(16),
              decoration: BoxDecoration(
                color: colorScheme.errorContainer,
                shape: BoxShape.circle,
              ),
              child: Icon(
                Icons.warning_amber_rounded,
                size: 40,
                color: colorScheme.onErrorContainer,
              ),
            ),
            const SizedBox(height: 16),

            // Institution name
            Text(
              name,
              style: theme.textTheme.titleLarge?.copyWith(
                fontWeight: FontWeight.bold,
              ),
            ),
            const SizedBox(height: 8),

            // Status
            Container(
              padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 6),
              decoration: BoxDecoration(
                color: colorScheme.errorContainer,
                borderRadius: BorderRadius.circular(16),
              ),
              child: Row(
                mainAxisSize: MainAxisSize.min,
                children: [
                  Icon(
                    Icons.error_outline,
                    size: 16,
                    color: colorScheme.onErrorContainer,
                  ),
                  const SizedBox(width: 4),
                  Text(
                    AppLocalizations.of(context).connRequiresUpdate,
                    style: theme.textTheme.labelMedium?.copyWith(
                      color: colorScheme.onErrorContainer,
                      fontWeight: FontWeight.w500,
                    ),
                  ),
                ],
              ),
            ),

            // Account count
            if (connection != null && connection.accounts.isNotEmpty) ...[
              const SizedBox(height: 12),
              Text(
                AppLocalizations.of(context)
                    .connAccountsAffected(connection.accounts.length),
                style: theme.textTheme.bodyMedium?.copyWith(
                  color: colorScheme.onSurfaceVariant,
                ),
              ),
            ],
          ],
        ),
      ),
    );
  }

  Widget _buildErrorCard(ThemeData theme, ColorScheme colorScheme) {
    return Card(
      color: colorScheme.errorContainer,
      child: Padding(
        padding: const EdgeInsets.all(16),
        child: Row(
          children: [
            Icon(
              Icons.error_outline,
              color: colorScheme.onErrorContainer,
            ),
            const SizedBox(width: 12),
            Expanded(
              child: Text(
                _error!,
                style: theme.textTheme.bodyMedium?.copyWith(
                  color: colorScheme.onErrorContainer,
                ),
              ),
            ),
          ],
        ),
      ),
    );
  }

  Widget _buildInstructions(ThemeData theme, ColorScheme colorScheme) {
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Text(
          AppLocalizations.of(context).connWhyNeeded,
          style: theme.textTheme.titleMedium?.copyWith(
            fontWeight: FontWeight.w600,
          ),
        ),
        const SizedBox(height: 12),
        Text(
          AppLocalizations.of(context).connReauthExplainer,
          style: theme.textTheme.bodyMedium?.copyWith(
            color: colorScheme.onSurfaceVariant,
          ),
        ),
        const SizedBox(height: 12),
        _buildReasonItem(theme, colorScheme, Icons.password,
            AppLocalizations.of(context).connReasonCredentialsChanged),
        _buildReasonItem(theme, colorScheme, Icons.security,
            AppLocalizations.of(context).connReasonMfaChanged),
        _buildReasonItem(theme, colorScheme, Icons.schedule,
            AppLocalizations.of(context).connReasonInactive),
        _buildReasonItem(theme, colorScheme, Icons.account_balance,
            AppLocalizations.of(context).connReasonBankSecurity),
        const SizedBox(height: 16),
        Card(
          color: colorScheme.primaryContainer,
          child: Padding(
            padding: const EdgeInsets.all(12),
            child: Row(
              children: [
                Icon(
                  Icons.lock,
                  color: colorScheme.onPrimaryContainer,
                  size: 20,
                ),
                const SizedBox(width: 12),
                Expanded(
                  child: Text(
                    AppLocalizations.of(context).connCredentialsSecure,
                    style: theme.textTheme.bodySmall?.copyWith(
                      color: colorScheme.onPrimaryContainer,
                    ),
                  ),
                ),
              ],
            ),
          ),
        ),
      ],
    );
  }

  Widget _buildReasonItem(ThemeData theme, ColorScheme colorScheme, IconData icon, String text) {
    return Padding(
      padding: const EdgeInsets.symmetric(vertical: 4),
      child: Row(
        children: [
          Icon(icon, size: 18, color: colorScheme.primary),
          const SizedBox(width: 12),
          Expanded(
            child: Text(
              text,
              style: theme.textTheme.bodyMedium,
            ),
          ),
        ],
      ),
    );
  }
}
