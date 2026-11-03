import 'package:flutter/material.dart';
import 'package:go_router/go_router.dart';
import 'package:provider/provider.dart';
import '../l10n/generated/app_localizations.dart';
import '../models/family_models.dart';
import '../services/analytics_service.dart';
import '../services/budget_service.dart';
import '../services/family_service.dart';
import '../providers/auth_provider.dart';
import '../widgets/app_toast.dart';

/// Accept Invitation Page
/// 
/// Handles family invitation acceptance via deep link.
/// Flow:
/// 1. Load invitation details from token
/// 2. If user is authenticated, check if email matches
/// 3. If match, accept invitation automatically or show confirmation
/// 4. If not authenticated, redirect to login/register
class AcceptInvitationPage extends StatefulWidget {
  final String token;

  const AcceptInvitationPage({
    super.key,
    required this.token,
  });

  @override
  State<AcceptInvitationPage> createState() => _AcceptInvitationPageState();
}

class _AcceptInvitationPageState extends State<AcceptInvitationPage> {
  bool _isLoading = true;
  bool _isAccepting = false;
  bool _hasExistingBudget = false;
  String? _error;
  FamilyInvitation? _invitation;

  @override
  void initState() {
    super.initState();
    _loadInvitation();
  }

  Future<void> _loadInvitation() async {
    setState(() {
      _isLoading = true;
      _error = null;
    });

    try {
      final invitation = await familyService.getInvitationByToken(widget.token);

      if (invitation.isAccepted) {
        await _handleAlreadyAccepted(invitation);
        return;
      }

      if (invitation.isExpired) {
        setState(() {
          _error = 'This invitation has expired';
          _isLoading = false;
        });
        return;
      }

      setState(() {
        _invitation = invitation;
        _isLoading = false;
      });

      // If user is authenticated, check email match and possibly auto-accept
      await _checkAuthAndProcess();
    } catch (e) {
      setState(() {
        _error = 'This invitation link is invalid or has expired';
        _isLoading = false;
      });
    }
  }

  /// Signing up from an invite link accepts the invitation and then lands here. A used
  /// invitation is a success for the person who used it, not an error.
  Future<void> _handleAlreadyAccepted(FamilyInvitation invitation) async {
    if (!mounted) return;
    final authProvider = context.read<AuthProvider>();
    final user = authProvider.user;
    final isInvitee = authProvider.status == AuthStatus.authenticated &&
        user != null &&
        user.email.toLowerCase() == invitation.email.toLowerCase();

    if (isInvitee) {
      try {
        // The server treats a repeat accept by the person who joined as a no-op and
        // returns their membership, so this confirms they really are in the family.
        await familyService.acceptInvitation(widget.token);
        if (!mounted) return;
        authProvider.clearPendingInvitations();
        showAppToast(
          context,
          'You have joined ${invitation.familyName.isNotEmpty ? invitation.familyName : 'the family'}!',
          type: AppToastType.success,
          clearsBottomNav: false,
        );
        context.go('/dashboard');
        return;
      } catch (_) {
        // Fall through to the "already used" message.
      }
    }

    if (!mounted) return;
    setState(() {
      _error = 'This invitation has already been used';
      _isLoading = false;
    });
  }

  Future<void> _checkAuthAndProcess() async {
    final authProvider = context.read<AuthProvider>();
    
    if (authProvider.status != AuthStatus.authenticated || _invitation == null) {
      return;
    }

    final user = authProvider.user;
    if (user == null) return;

    // Check if the invitation email matches the logged-in user
    if (user.email.toLowerCase() != _invitation!.email.toLowerCase()) {
      setState(() {
        _error = 'This invitation was sent to ${_invitation!.email}. '
                 'Please log in with that email address.';
      });
      return;
    }

    // Check if user has existing budgets (they'll be replaced by family budget)
    try {
      final budgets = await budgetService.getBudgets();
      if (mounted && budgets.isNotEmpty) {
        setState(() => _hasExistingBudget = true);
      }
    } catch (_) {}
    // If email matches, show the confirmation screen (don't auto-accept)
  }

  Future<void> _acceptInvitation() async {
    setState(() {
      _isAccepting = true;
      _error = null;
    });

    try {
      await familyService.acceptInvitation(widget.token);
      analytics.track(AnalyticsEvents.familyMemberJoined);

      if (mounted) {
        showAppToast(
          context,
          'You have successfully joined the family!',
          type: AppToastType.success,
          clearsBottomNav: false,
        );

        context.go('/dashboard');
      }
    } catch (e) {
      setState(() {
        _error = 'Failed to accept invitation. Please try again.';
        _isAccepting = false;
      });
    }
  }

  Future<void> _declineInvitation() async {
    try {
      await familyService.declineInvitation(widget.token);
      if (mounted) {
        context.read<AuthProvider>().clearPendingInvitations();
        context.go('/dashboard');
      }
    } catch (_) {
      if (mounted) context.go('/dashboard');
    }
  }

  void _goToLogin() {
    context.go('/login?invitation=${widget.token}');
  }

  void _goToRegister() {
    context.go('/register?invitation=${widget.token}');
  }

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final colorScheme = theme.colorScheme;

    return Scaffold(
      body: SafeArea(
        child: Center(
          child: SingleChildScrollView(
            padding: const EdgeInsets.all(24),
            // Centre INSIDE the scroll view: wrapping it in Center makes it
            // shrink-wrap to the card, stranding the web scrollbar mid-page.
            child: Center(
              child: ConstrainedBox(
                constraints: const BoxConstraints(maxWidth: 400),
                child: _buildContent(theme, colorScheme),
              ),
            ),
          ),
        ),
      ),
    );
  }

  Widget _buildContent(ThemeData theme, ColorScheme colorScheme) {
    // Loading state
    if (_isLoading) {
      return _buildLoadingState(theme);
    }

    // Error state
    if (_error != null) {
      return _buildErrorState(theme, colorScheme);
    }

    // Accepting state
    if (_isAccepting) {
      return _buildAcceptingState(theme);
    }

    // Invitation loaded - check auth
    final authProvider = context.watch<AuthProvider>();
    
    if (authProvider.status != AuthStatus.authenticated) {
      return _buildUnauthenticatedState(theme, colorScheme);
    }

    return _buildInvitationCard(theme, colorScheme);
  }

  Widget _buildLoadingState(ThemeData theme) {
    return Column(
      mainAxisAlignment: MainAxisAlignment.center,
      children: [
        // Logo placeholder
        Icon(
          Icons.family_restroom,
          size: 64,
          color: theme.colorScheme.primary,
        ),
        const SizedBox(height: 32),
        const CircularProgressIndicator(),
        const SizedBox(height: 16),
        Text(
          AppLocalizations.of(context).invLoadingInvitation,
          style: theme.textTheme.bodyLarge,
        ),
      ],
    );
  }

  Widget _buildAcceptingState(ThemeData theme) {
    return Column(
      mainAxisAlignment: MainAxisAlignment.center,
      children: [
        Icon(
          Icons.family_restroom,
          size: 64,
          color: theme.colorScheme.primary,
        ),
        const SizedBox(height: 32),
        const CircularProgressIndicator(),
        const SizedBox(height: 16),
        Text(
          AppLocalizations.of(context).invJoiningFamily,
          style: theme.textTheme.bodyLarge,
        ),
      ],
    );
  }

  Widget _buildErrorState(ThemeData theme, ColorScheme colorScheme) {
    return Column(
      mainAxisAlignment: MainAxisAlignment.center,
      children: [
        Icon(
          Icons.error_outline,
          size: 64,
          color: colorScheme.error,
        ),
        const SizedBox(height: 24),
        Text(
          AppLocalizations.of(context).invInvitationError,
          style: theme.textTheme.headlineSmall?.copyWith(
            fontWeight: FontWeight.bold,
          ),
        ),
        const SizedBox(height: 16),
        Card(
          color: colorScheme.errorContainer,
          child: Padding(
            padding: const EdgeInsets.all(16),
            child: Row(
              children: [
                Icon(Icons.info_outline, color: colorScheme.onErrorContainer),
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
        ),
        const SizedBox(height: 24),
        Text(
          AppLocalizations.of(context).invContactInviter,
          textAlign: TextAlign.center,
          style: theme.textTheme.bodyMedium?.copyWith(
            color: colorScheme.onSurfaceVariant,
          ),
        ),
        const SizedBox(height: 24),
        FilledButton.icon(
          onPressed: () => context.go('/login'),
          icon: const Icon(Icons.arrow_back),
          label: Text(AppLocalizations.of(context).invGoBack),
        ),
      ],
    );
  }

  Widget _buildUnauthenticatedState(ThemeData theme, ColorScheme colorScheme) {
    return Column(
      mainAxisAlignment: MainAxisAlignment.center,
      children: [
        Icon(
          Icons.family_restroom,
          size: 64,
          color: colorScheme.primary,
        ),
        const SizedBox(height: 24),
        Text(
          AppLocalizations.of(context).invFamilyInvitation,
          style: theme.textTheme.headlineSmall?.copyWith(
            fontWeight: FontWeight.bold,
          ),
        ),
        const SizedBox(height: 16),
        Card(
          child: Padding(
            padding: const EdgeInsets.all(20),
            child: Column(
              children: [
                Text(
                  AppLocalizations.of(context).invYouveBeenInvited,
                  style: theme.textTheme.bodyLarge,
                ),
                const SizedBox(height: 8),
                Text(
                  _invitation?.familyName ?? AppLocalizations.of(context).invAFamilyFallback,
                  style: theme.textTheme.titleLarge?.copyWith(
                    fontWeight: FontWeight.bold,
                    color: colorScheme.primary,
                  ),
                ),
                const SizedBox(height: 8),
                Text(
                  AppLocalizations.of(context).invInvitedByLine(
                      _invitation?.invitedByName ?? AppLocalizations.of(context).invFamilyOwnerFallback),
                  style: theme.textTheme.bodyMedium?.copyWith(
                    color: colorScheme.onSurfaceVariant,
                  ),
                ),
              ],
            ),
          ),
        ),
        const SizedBox(height: 24),
        Text(
          AppLocalizations.of(context).invSignInPrompt,
          textAlign: TextAlign.center,
          style: theme.textTheme.bodyMedium?.copyWith(
            color: colorScheme.onSurfaceVariant,
          ),
        ),
        const SizedBox(height: 24),
        SizedBox(
          width: double.infinity,
          child: FilledButton.icon(
            onPressed: _goToLogin,
            icon: const Icon(Icons.login),
            label: Text(AppLocalizations.of(context).signIn),
          ),
        ),
        const SizedBox(height: 12),
        SizedBox(
          width: double.infinity,
          child: OutlinedButton.icon(
            onPressed: _goToRegister,
            icon: const Icon(Icons.person_add),
            label: Text(AppLocalizations.of(context).invCreateAccount),
          ),
        ),
      ],
    );
  }

  Widget _buildInvitationCard(ThemeData theme, ColorScheme colorScheme) {
    return Column(
      mainAxisAlignment: MainAxisAlignment.center,
      children: [
        Icon(
          Icons.mail_outline,
          size: 64,
          color: colorScheme.primary,
        ),
        const SizedBox(height: 24),
        Text(
          AppLocalizations.of(context).invFamilyInvitation,
          style: theme.textTheme.headlineSmall?.copyWith(
            fontWeight: FontWeight.bold,
          ),
        ),
        const SizedBox(height: 24),
        Card(
          child: Padding(
            padding: const EdgeInsets.all(20),
            child: Column(
              children: [
                // Family icon and name
                Container(
                  padding: const EdgeInsets.all(16),
                  decoration: BoxDecoration(
                    color: colorScheme.primaryContainer,
                    shape: BoxShape.circle,
                  ),
                  child: Icon(
                    Icons.family_restroom,
                    size: 40,
                    color: colorScheme.onPrimaryContainer,
                  ),
                ),
                const SizedBox(height: 16),
                Text(
                  _invitation?.familyName ?? AppLocalizations.of(context).famTitle,
                  style: theme.textTheme.titleLarge?.copyWith(
                    fontWeight: FontWeight.bold,
                  ),
                ),
                const SizedBox(height: 8),
                Text(
                  AppLocalizations.of(context).invInvitedBy(
                      _invitation?.invitedByName ?? AppLocalizations.of(context).invFamilyOwnerFallback),
                  style: theme.textTheme.bodyMedium?.copyWith(
                    color: colorScheme.onSurfaceVariant,
                  ),
                ),
                const SizedBox(height: 16),
                const Divider(),
                const SizedBox(height: 16),
                
                // Invited role
                Row(
                  children: [
                    Icon(Icons.badge, color: colorScheme.primary, size: 20),
                    const SizedBox(width: 12),
                    Text(
                      AppLocalizations.of(context).invRoleLabel,
                      style: theme.textTheme.bodyMedium,
                    ),
                    const Spacer(),
                    Container(
                      padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 4),
                      decoration: BoxDecoration(
                        color: colorScheme.secondaryContainer,
                        borderRadius: BorderRadius.circular(16),
                      ),
                      child: Text(
                        _invitation?.invitedRole.displayName ?? AppLocalizations.of(context).invMemberFallback,
                        style: theme.textTheme.labelMedium?.copyWith(
                          color: colorScheme.onSecondaryContainer,
                          fontWeight: FontWeight.w500,
                        ),
                      ),
                    ),
                  ],
                ),
                const SizedBox(height: 12),
                
                // Role description
                Text(
                  _invitation?.invitedRole.description ?? '',
                  style: theme.textTheme.bodySmall?.copyWith(
                    color: colorScheme.onSurfaceVariant,
                  ),
                ),
              ],
            ),
          ),
        ),
        // Budget replacement warning
        if (_hasExistingBudget) ...[
          const SizedBox(height: 16),
          Card(
            color: Colors.amber.shade900.withValues(alpha: 0.3),
            child: Padding(
              padding: const EdgeInsets.all(14),
              child: Row(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Icon(Icons.warning_amber_rounded, color: Colors.amber.shade300, size: 20),
                  const SizedBox(width: 10),
                  Expanded(
                    child: Text(
                      AppLocalizations.of(context).invBudgetReplaceWarning,
                      style: theme.textTheme.bodySmall?.copyWith(
                        color: Colors.amber.shade200,
                      ),
                    ),
                  ),
                ],
              ),
            ),
          ),
        ],
        const SizedBox(height: 24),

        // Action buttons
        SizedBox(
          width: double.infinity,
          child: FilledButton.icon(
            onPressed: _isAccepting ? null : _acceptInvitation,
            icon: _isAccepting 
                ? const SizedBox(
                    width: 16,
                    height: 16,
                    child: CircularProgressIndicator(strokeWidth: 2),
                  )
                : const Icon(Icons.check),
            label: Text(_isAccepting
                ? AppLocalizations.of(context).invJoining
                : AppLocalizations.of(context).invAcceptInvitation),
          ),
        ),
        const SizedBox(height: 12),
        SizedBox(
          width: double.infinity,
          child: OutlinedButton.icon(
            onPressed: _isAccepting
                ? null
                : () => _declineInvitation(),
            icon: const Icon(Icons.close),
            label: Text(AppLocalizations.of(context).invDecline),
          ),
        ),
        const SizedBox(height: 16),
        
        // Info text
        Row(
          mainAxisAlignment: MainAxisAlignment.center,
          children: [
            Icon(
              Icons.info_outline,
              size: 16,
              color: colorScheme.outline,
            ),
            const SizedBox(width: 8),
            Flexible(
              child: Text(
                AppLocalizations.of(context).invLeaveAnytime,
                style: theme.textTheme.bodySmall?.copyWith(
                  color: colorScheme.outline,
                ),
              ),
            ),
          ],
        ),
      ],
    );
  }
}
