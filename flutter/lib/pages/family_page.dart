import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:go_router/go_router.dart';
import '../l10n/generated/app_localizations.dart';
import '../models/family_models.dart';
import '../services/analytics_service.dart';
import '../services/family_service.dart';
import '../widgets/app_toast.dart';

/// Family Page
/// 
/// Manages family creation, members, invitations, and roles.
class FamilyPage extends StatefulWidget {
  const FamilyPage({super.key});

  @override
  State<FamilyPage> createState() => _FamilyPageState();
}

class _FamilyPageState extends State<FamilyPage> {
  Family? _family;
  List<FamilyMember> _members = [];
  List<FamilyInvitation> _invitations = [];
  bool _isLoading = true;
  String? _error;
  FamilyMember? _currentUserMember;

  @override
  void initState() {
    super.initState();
    _loadFamilyData();
  }

  Future<void> _loadFamilyData() async {
    setState(() {
      _isLoading = true;
      _error = null;
    });

    try {
      final family = await familyService.getFamily();
      setState(() => _family = family);

      if (family != null) {
        final members = await familyService.getFamilyMembers();
        final invitations = await familyService.getInvitations();
        
        setState(() {
          _members = members;
          _invitations = invitations;
          _currentUserMember = members.where((m) => m.isCurrentUser).firstOrNull;
        });
      }
    } catch (e) {
      setState(() => _error = e.toString());
    } finally {
      setState(() => _isLoading = false);
    }
  }

  bool get _isOwner => _currentUserMember?.role == FamilyMemberRole.owner;
  bool get _canInvite => _currentUserMember != null && 
      familyService.canInviteMembers(_currentUserMember!.role);

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final colorScheme = theme.colorScheme;

    return Scaffold(
      appBar: AppBar(
        title: Text(AppLocalizations.of(context).famTitle),
        actions: [
          if (_family != null && _isOwner)
            PopupMenuButton<String>(
              icon: const Icon(Icons.more_vert),
              onSelected: (value) {
                switch (value) {
                  case 'edit':
                    _showEditFamilyDialog();
                    break;
                  case 'delete':
                    _showDeleteFamilyDialog();
                    break;
                }
              },
              itemBuilder: (context) => [
                PopupMenuItem(
                  value: 'edit',
                  child: ListTile(
                    leading: const Icon(Icons.edit),
                    title: Text(AppLocalizations.of(context).famEditFamilyName),
                    contentPadding: EdgeInsets.zero,
                  ),
                ),
                PopupMenuItem(
                  value: 'delete',
                  child: ListTile(
                    leading: Icon(Icons.delete, color: colorScheme.error),
                    title: Text(AppLocalizations.of(context).famDeleteFamily, style: TextStyle(color: colorScheme.error)),
                    contentPadding: EdgeInsets.zero,
                  ),
                ),
              ],
            ),
          if (_family != null && !_isOwner)
            IconButton(
              icon: const Icon(Icons.logout),
              onPressed: _showLeaveFamilyDialog,
              tooltip: 'Leave Family',
            ),
        ],
      ),
      floatingActionButton: _family != null && _canInvite
          ? FloatingActionButton.extended(
              onPressed: _showInviteSheet,
              icon: const Icon(Icons.person_add),
              label: Text(AppLocalizations.of(context).famInviteMember),
            )
          : null,
      body: _buildBody(context),
    );
  }

  Widget _buildBody(BuildContext context) {
    if (_isLoading) {
      return const Center(child: CircularProgressIndicator());
    }

    if (_error != null) {
      return Center(
        child: Column(
          mainAxisAlignment: MainAxisAlignment.center,
          children: [
            Icon(Icons.error_outline, size: 48, color: Theme.of(context).colorScheme.error),
            const SizedBox(height: 16),
            Text(_error!),
            const SizedBox(height: 16),
            FilledButton(
              onPressed: _loadFamilyData,
              child: Text(AppLocalizations.of(context).famRetry),
            ),
          ],
        ),
      );
    }

    if (_family == null) {
      return _buildCreateFamilyView(context);
    }

    return _buildFamilyView(context);
  }

  Widget _buildCreateFamilyView(BuildContext context) {
    final theme = Theme.of(context);
    final colorScheme = theme.colorScheme;

    return Center(
      child: Padding(
        padding: const EdgeInsets.all(24),
        child: Column(
          mainAxisAlignment: MainAxisAlignment.center,
          children: [
            Container(
              width: 80,
              height: 80,
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
            const SizedBox(height: 24),
            Text(
              AppLocalizations.of(context).famNoFamilyYet,
              style: theme.textTheme.headlineSmall?.copyWith(
                fontWeight: FontWeight.bold,
              ),
            ),
            const SizedBox(height: 8),
            Text(
              AppLocalizations.of(context).famCreateFamilyBlurb,
              textAlign: TextAlign.center,
              style: theme.textTheme.bodyMedium?.copyWith(
                color: colorScheme.onSurfaceVariant,
              ),
            ),
            const SizedBox(height: 32),
            FilledButton.icon(
              onPressed: _showCreateFamilySheet,
              icon: const Icon(Icons.add),
              label: Text(AppLocalizations.of(context).famCreateFamily),
            ),
          ],
        ),
      ),
    );
  }

  Widget _buildFamilyView(BuildContext context) {
    final theme = Theme.of(context);
    final colorScheme = theme.colorScheme;

    return RefreshIndicator(
      onRefresh: _loadFamilyData,
      child: ListView(
        padding: const EdgeInsets.all(16),
        children: [
          // Family header card
          Card(
            child: Padding(
              padding: const EdgeInsets.all(16),
              child: Row(
                children: [
                  Container(
                    width: 56,
                    height: 56,
                    decoration: BoxDecoration(
                      color: colorScheme.primaryContainer,
                      borderRadius: BorderRadius.circular(12),
                    ),
                    child: Icon(
                      Icons.family_restroom,
                      size: 28,
                      color: colorScheme.onPrimaryContainer,
                    ),
                  ),
                  const SizedBox(width: 16),
                  Expanded(
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.start,
                      children: [
                        Text(
                          _family!.name,
                          style: theme.textTheme.titleLarge?.copyWith(
                            fontWeight: FontWeight.bold,
                          ),
                        ),
                        Text(
                          AppLocalizations.of(context).famMemberCount(_members.length),
                          style: theme.textTheme.bodyMedium?.copyWith(
                            color: colorScheme.onSurfaceVariant,
                          ),
                        ),
                      ],
                    ),
                  ),
                  if (_currentUserMember != null)
                    _RoleBadge(role: _currentUserMember!.role),
                ],
              ),
            ),
          ),
          const SizedBox(height: 24),

          // Members section
          Text(
            AppLocalizations.of(context).famMembers,
            style: theme.textTheme.titleMedium?.copyWith(
              fontWeight: FontWeight.w600,
            ),
          ),
          const SizedBox(height: 12),
          ..._members.map((member) => _MemberCard(
            member: member,
            isOwner: _isOwner,
            onRoleChanged: _isOwner && !member.isCurrentUser
                ? (role) => _updateMemberRole(member.userId, role)
                : null,
            onRemove: _isOwner && !member.isCurrentUser
                ? () => _removeMember(member)
                : null,
          )),

          // Pending invitations
          if (_invitations.isNotEmpty) ...[
            const SizedBox(height: 24),
            Text(
              AppLocalizations.of(context).famPendingInvitations,
              style: theme.textTheme.titleMedium?.copyWith(
                fontWeight: FontWeight.w600,
              ),
            ),
            const SizedBox(height: 12),
            ..._invitations.map((invitation) => _InvitationCard(
              invitation: invitation,
              onCancel: _canInvite ? () => _cancelInvitation(invitation.id) : null,
            )),
          ],
        ],
      ),
    );
  }

  void _showCreateFamilySheet() {
    showModalBottomSheet(
      context: context,
      isScrollControlled: true,
      builder: (context) => _CreateFamilySheet(
        onCreated: (family) {
          Navigator.pop(context);
          _loadFamilyData();
          showAppToast(
            context,
            'Family "${family.name}" created!',
            type: AppToastType.success,
          );
        },
      ),
    );
  }

  void _showInviteSheet() {
    showModalBottomSheet(
      context: context,
      isScrollControlled: true,
      builder: (context) => _InviteMemberSheet(
        onInvited: () {
          Navigator.pop(context);
          _loadFamilyData();
          showAppToast(
            context,
            'Invitation sent!',
            type: AppToastType.success,
          );
        },
      ),
    );
  }

  void _showEditFamilyDialog() {
    final controller = TextEditingController(text: _family?.name);
    final navigator = Navigator.of(context);

    showDialog(
      context: context,
      builder: (dialogContext) => AlertDialog(
        title: Text(AppLocalizations.of(dialogContext).famEditFamilyName),
        content: TextField(
          controller: controller,
          decoration: const InputDecoration(
            labelText: 'Family Name',
            hintText: 'Enter family name',
          ),
          autofocus: true,
        ),
        actions: [
          TextButton(
            onPressed: () => Navigator.pop(dialogContext),
            child: Text(AppLocalizations.of(dialogContext).famCancel),
          ),
          FilledButton(
            onPressed: () async {
              if (controller.text.trim().isNotEmpty) {
                try {
                  await familyService.updateFamily(controller.text.trim());
                  navigator.pop();
                  _loadFamilyData();
                  if (mounted) {
                    showAppToast(
                      context,
                      'Family name updated',
                      type: AppToastType.success,
                    );
                  }
                } catch (e) {
                  if (mounted) {
                    showAppToast(
                      context,
                      'Error: $e',
                      type: AppToastType.error,
                    );
                  }
                }
              }
            },
            child: Text(AppLocalizations.of(dialogContext).famSave),
          ),
        ],
      ),
    );
  }

  void _showDeleteFamilyDialog() {
    final navigator = Navigator.of(context);
    final errorColor = Theme.of(context).colorScheme.error;
    
    showDialog(
      context: context,
      builder: (dialogContext) => AlertDialog(
        title: Text(AppLocalizations.of(dialogContext).famDeleteFamilyTitle),
        content: Text(
          AppLocalizations.of(dialogContext).famDeleteFamilyWarning,
        ),
        actions: [
          TextButton(
            onPressed: () => Navigator.pop(dialogContext),
            child: Text(AppLocalizations.of(dialogContext).famCancel),
          ),
          FilledButton(
            onPressed: () async {
              try {
                await familyService.deleteFamily();
                navigator.pop();
                _loadFamilyData();
                if (mounted) {
                  showAppToast(
                    context,
                    'Family deleted',
                    type: AppToastType.success,
                  );
                }
              } catch (e) {
                if (mounted) {
                  showAppToast(
                    context,
                    'Error: $e',
                    type: AppToastType.error,
                  );
                }
              }
            },
            style: FilledButton.styleFrom(
              backgroundColor: errorColor,
            ),
            child: Text(AppLocalizations.of(dialogContext).famDelete),
          ),
        ],
      ),
    );
  }

  void _showLeaveFamilyDialog() {
    final navigator = Navigator.of(context);
    final errorColor = Theme.of(context).colorScheme.error;
    
    showDialog(
      context: context,
      builder: (dialogContext) => AlertDialog(
        title: Text(AppLocalizations.of(dialogContext).famLeaveFamilyTitle),
        content: Text(
          AppLocalizations.of(dialogContext).famLeaveFamilyWarning,
        ),
        actions: [
          TextButton(
            onPressed: () => Navigator.pop(dialogContext),
            child: Text(AppLocalizations.of(dialogContext).famCancel),
          ),
          FilledButton(
            onPressed: () async {
              try {
                await familyService.leaveFamily();
                navigator.pop();
                _loadFamilyData();
                if (mounted) {
                  showAppToast(
                    context,
                    'You have left the family',
                    type: AppToastType.success,
                  );
                }
              } catch (e) {
                if (mounted) {
                  showAppToast(
                    context,
                    'Error: $e',
                    type: AppToastType.error,
                  );
                }
              }
            },
            style: FilledButton.styleFrom(
              backgroundColor: errorColor,
            ),
            child: Text(AppLocalizations.of(dialogContext).famLeave),
          ),
        ],
      ),
    );
  }

  Future<void> _updateMemberRole(int userId, FamilyMemberRole role) async {
    try {
      await familyService.updateMemberRole(userId, role);
      _loadFamilyData();
      if (mounted) {
        showAppToast(context, 'Role updated', type: AppToastType.success);
      }
    } catch (e) {
      if (mounted) {
        showAppToast(context, 'Error: $e', type: AppToastType.error);
      }
    }
  }

  Future<void> _removeMember(FamilyMember member) async {
    final confirmed = await showDialog<bool>(
      context: context,
      builder: (context) => AlertDialog(
        title: Text(AppLocalizations.of(context).famRemoveMemberTitle),
        content: Text(AppLocalizations.of(context).famRemoveMemberConfirm(member.displayName)),
        actions: [
          TextButton(
            onPressed: () => Navigator.pop(context, false),
            child: Text(AppLocalizations.of(context).famCancel),
          ),
          FilledButton(
            onPressed: () => Navigator.pop(context, true),
            style: FilledButton.styleFrom(
              backgroundColor: Theme.of(context).colorScheme.error,
            ),
            child: Text(AppLocalizations.of(context).famRemove),
          ),
        ],
      ),
    );

    if (confirmed == true) {
      try {
        await familyService.removeMember(member.userId);
        _loadFamilyData();
        if (mounted) {
          showAppToast(
            context,
            '${member.displayName} removed',
            type: AppToastType.success,
          );
        }
      } catch (e) {
        if (mounted) {
          showAppToast(context, 'Error: $e', type: AppToastType.error);
        }
      }
    }
  }

  Future<void> _cancelInvitation(int invitationId) async {
    try {
      await familyService.cancelInvitation(invitationId);
      _loadFamilyData();
      if (mounted) {
        showAppToast(
          context,
          'Invitation cancelled',
          type: AppToastType.success,
        );
      }
    } catch (e) {
      if (mounted) {
        showAppToast(context, 'Error: $e', type: AppToastType.error);
      }
    }
  }
}

// ============ Supporting Widgets ============

class _RoleBadge extends StatelessWidget {
  final FamilyMemberRole role;

  const _RoleBadge({required this.role});

  @override
  Widget build(BuildContext context) {
    final colorScheme = Theme.of(context).colorScheme;
    
    final (bgColor, textColor) = switch (role) {
      FamilyMemberRole.owner => (colorScheme.primaryContainer, colorScheme.onPrimaryContainer),
      FamilyMemberRole.budgetAdmin => (Colors.blue.shade100, Colors.blue.shade800),
      FamilyMemberRole.member => (Colors.teal.shade100, Colors.teal.shade800),
      FamilyMemberRole.readOnly => (Colors.grey.shade200, Colors.grey.shade700),
    };

    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 4),
      decoration: BoxDecoration(
        color: bgColor,
        borderRadius: BorderRadius.circular(12),
      ),
      child: Text(
        role.displayName,
        style: TextStyle(
          fontSize: 12,
          fontWeight: FontWeight.w500,
          color: textColor,
        ),
      ),
    );
  }
}

class _MemberCard extends StatelessWidget {
  final FamilyMember member;
  final bool isOwner;
  final Function(FamilyMemberRole)? onRoleChanged;
  final VoidCallback? onRemove;

  const _MemberCard({
    required this.member,
    required this.isOwner,
    this.onRoleChanged,
    this.onRemove,
  });

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final colorScheme = theme.colorScheme;

    return Card(
      margin: const EdgeInsets.only(bottom: 8),
      child: ListTile(
        leading: CircleAvatar(
          backgroundColor: colorScheme.primaryContainer,
          child: Text(
            member.displayName[0].toUpperCase(),
            style: TextStyle(
              color: colorScheme.onPrimaryContainer,
              fontWeight: FontWeight.bold,
            ),
          ),
        ),
        title: Row(
          children: [
            Text(member.displayName),
            if (member.isCurrentUser) ...[
              const SizedBox(width: 8),
              Container(
                padding: const EdgeInsets.symmetric(horizontal: 6, vertical: 2),
                decoration: BoxDecoration(
                  color: colorScheme.secondaryContainer,
                  borderRadius: BorderRadius.circular(4),
                ),
                child: Text(
                  AppLocalizations.of(context).famYouBadge,
                  style: theme.textTheme.labelSmall?.copyWith(
                    color: colorScheme.onSecondaryContainer,
                  ),
                ),
              ),
            ],
          ],
        ),
        subtitle: Text(member.email),
        trailing: Row(
          mainAxisSize: MainAxisSize.min,
          children: [
            _RoleBadge(role: member.role),
            if (onRoleChanged != null || onRemove != null)
              PopupMenuButton<String>(
                icon: const Icon(Icons.more_vert),
                onSelected: (value) {
                  if (value == 'remove') {
                    onRemove?.call();
                  } else {
                    final role = FamilyMemberRole.values.firstWhere(
                      (r) => r.name == value,
                    );
                    onRoleChanged?.call(role);
                  }
                },
                itemBuilder: (context) => [
                  if (onRoleChanged != null) ...[
                    PopupMenuItem(
                      enabled: false,
                      child: Text(AppLocalizations.of(context).famChangeRole),
                    ),
                    ...FamilyMemberRole.values
                        .where((r) => r != FamilyMemberRole.owner)
                        .map((role) => PopupMenuItem(
                          value: role.name,
                          child: Row(
                            children: [
                              if (member.role == role)
                                Icon(Icons.check, size: 18, color: colorScheme.primary)
                              else
                                const SizedBox(width: 18),
                              const SizedBox(width: 8),
                              Text(role.displayName),
                            ],
                          ),
                        )),
                    const PopupMenuDivider(),
                  ],
                  if (onRemove != null)
                    PopupMenuItem(
                      value: 'remove',
                      child: Row(
                        children: [
                          Icon(Icons.person_remove, size: 18, color: colorScheme.error),
                          const SizedBox(width: 8),
                          Text(AppLocalizations.of(context).famRemove, style: TextStyle(color: colorScheme.error)),
                        ],
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

class _InvitationCard extends StatelessWidget {
  final FamilyInvitation invitation;
  final VoidCallback? onCancel;

  const _InvitationCard({
    required this.invitation,
    this.onCancel,
  });

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final colorScheme = theme.colorScheme;

    return Card(
      margin: const EdgeInsets.only(bottom: 8),
      color: colorScheme.surfaceContainerHighest.withValues(alpha: 0.5),
      child: ListTile(
        leading: CircleAvatar(
          backgroundColor: colorScheme.tertiaryContainer,
          child: Icon(
            Icons.mail_outline,
            color: colorScheme.onTertiaryContainer,
          ),
        ),
        title: Text(invitation.email),
        subtitle: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text(AppLocalizations.of(context).famInvitedAs(invitation.invitedRole.displayName)),
            if (invitation.isExpired)
              Text(
                AppLocalizations.of(context).famExpired,
                style: TextStyle(color: colorScheme.error),
              )
            else
              Text(
                AppLocalizations.of(context).famExpiresIn(_formatTimeRemaining(invitation.expiresAt)),
                style: theme.textTheme.bodySmall?.copyWith(
                  color: colorScheme.onSurfaceVariant,
                ),
              ),
          ],
        ),
        trailing: onCancel != null
            ? IconButton(
                icon: const Icon(Icons.close),
                onPressed: onCancel,
                tooltip: 'Cancel Invitation',
              )
            : null,
      ),
    );
  }

  String _formatTimeRemaining(DateTime expiresAt) {
    final remaining = expiresAt.difference(DateTime.now());
    if (remaining.inDays > 0) {
      return '${remaining.inDays} days';
    } else if (remaining.inHours > 0) {
      return '${remaining.inHours} hours';
    } else {
      return '${remaining.inMinutes} minutes';
    }
  }
}

class _CreateFamilySheet extends StatefulWidget {
  final Function(Family) onCreated;

  const _CreateFamilySheet({required this.onCreated});

  @override
  State<_CreateFamilySheet> createState() => _CreateFamilySheetState();
}

class _CreateFamilySheetState extends State<_CreateFamilySheet> {
  final _formKey = GlobalKey<FormState>();
  final _nameController = TextEditingController();
  bool _isCreating = false;

  @override
  void dispose() {
    _nameController.dispose();
    super.dispose();
  }

  Future<void> _createFamily() async {
    if (!_formKey.currentState!.validate()) return;

    setState(() => _isCreating = true);

    try {
      final family = await familyService.createFamily(
        CreateFamilyRequest(name: _nameController.text.trim()),
      );
      analytics.track(AnalyticsEvents.familyCreated);
      widget.onCreated(family);
    } catch (e) {
      setState(() => _isCreating = false);
      if (!mounted) return;

      // Family sharing is Premium-gated: the backend returns a 403 with
      // { message, upgradeRequired: true } for non-Premium accounts. Surface a
      // clear upgrade prompt instead of dumping a raw error string.
      final data = e is DioException ? e.response?.data : null;
      final upgradeRequired =
          data is Map && data['upgradeRequired'] == true;
      if (upgradeRequired) {
        final message = (data['message'] as String?) ??
            'Family sharing is a Premium feature.';
        _showPremiumRequiredDialog(message);
        return;
      }

      final message = (data is Map ? data['message'] as String? : null) ??
          'Something went wrong creating your family. Please try again.';
      showAppToast(context, message, type: AppToastType.error);
    }
  }

  void _showPremiumRequiredDialog(String message) {
    showDialog<void>(
      context: context,
      builder: (dialogContext) => AlertDialog(
        icon: const Icon(Icons.workspace_premium_outlined),
        title: Text(AppLocalizations.of(dialogContext).famPremiumRequired),
        content: Text(
          AppLocalizations.of(dialogContext).famPremiumUpsell(message),
        ),
        actions: [
          TextButton(
            onPressed: () => Navigator.of(dialogContext).pop(),
            child: Text(AppLocalizations.of(dialogContext).famNotNow),
          ),
          FilledButton(
            onPressed: () {
              Navigator.of(dialogContext).pop();
              context.go('/billing');
            },
            child: Text(AppLocalizations.of(dialogContext).famViewPlans),
          ),
        ],
      ),
    );
  }

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);

    return Padding(
      padding: EdgeInsets.only(
        left: 24,
        right: 24,
        top: 24,
        bottom: MediaQuery.of(context).viewInsets.bottom + 24,
      ),
      child: Form(
        key: _formKey,
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text(
              AppLocalizations.of(context).famCreateFamily,
              style: theme.textTheme.headlineSmall?.copyWith(
                fontWeight: FontWeight.bold,
              ),
            ),
            const SizedBox(height: 8),
            Text(
              AppLocalizations.of(context).famOwnerBlurb,
              style: theme.textTheme.bodyMedium?.copyWith(
                color: theme.colorScheme.onSurfaceVariant,
              ),
            ),
            const SizedBox(height: 24),
            TextFormField(
              controller: _nameController,
              decoration: const InputDecoration(
                labelText: 'Family Name',
                hintText: 'e.g., The Smiths',
                prefixIcon: Icon(Icons.family_restroom),
              ),
              textCapitalization: TextCapitalization.words,
              validator: (value) {
                if (value == null || value.trim().isEmpty) {
                  return 'Please enter a family name';
                }
                return null;
              },
            ),
            const SizedBox(height: 24),
            SizedBox(
              width: double.infinity,
              child: FilledButton(
                onPressed: _isCreating ? null : _createFamily,
                child: _isCreating
                    ? const SizedBox(
                        width: 20,
                        height: 20,
                        child: CircularProgressIndicator(strokeWidth: 2),
                      )
                    : Text(AppLocalizations.of(context).famCreateFamily),
              ),
            ),
          ],
        ),
      ),
    );
  }
}

class _InviteMemberSheet extends StatefulWidget {
  final VoidCallback onInvited;

  const _InviteMemberSheet({required this.onInvited});

  @override
  State<_InviteMemberSheet> createState() => _InviteMemberSheetState();
}

class _InviteMemberSheetState extends State<_InviteMemberSheet> {
  final _formKey = GlobalKey<FormState>();
  final _emailController = TextEditingController();
  FamilyMemberRole _selectedRole = FamilyMemberRole.member;
  bool _isInviting = false;

  @override
  void dispose() {
    _emailController.dispose();
    super.dispose();
  }

  Future<void> _inviteMember() async {
    if (!_formKey.currentState!.validate()) return;

    setState(() => _isInviting = true);

    try {
      await familyService.inviteMember(
        InviteFamilyMemberRequest(
          email: _emailController.text.trim(),
          role: _selectedRole,
        ),
      );
      analytics.track(AnalyticsEvents.familyMemberInvited, {
        'role': _selectedRole.name,
      });
      widget.onInvited();
    } catch (e) {
      setState(() => _isInviting = false);
      if (mounted) {
        showAppToast(context, 'Error: $e', type: AppToastType.error);
      }
    }
  }

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final colorScheme = theme.colorScheme;

    return Padding(
      padding: EdgeInsets.only(
        left: 24,
        right: 24,
        top: 24,
        bottom: MediaQuery.of(context).viewInsets.bottom + 24,
      ),
      child: Form(
        key: _formKey,
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text(
              AppLocalizations.of(context).famInviteMember,
              style: theme.textTheme.headlineSmall?.copyWith(
                fontWeight: FontWeight.bold,
              ),
            ),
            const SizedBox(height: 8),
            Text(
              AppLocalizations.of(context).famInviteBlurb,
              style: theme.textTheme.bodyMedium?.copyWith(
                color: colorScheme.onSurfaceVariant,
              ),
            ),
            const SizedBox(height: 24),
            TextFormField(
              controller: _emailController,
              decoration: const InputDecoration(
                labelText: 'Email Address',
                hintText: 'family@example.com',
                prefixIcon: Icon(Icons.email),
              ),
              keyboardType: TextInputType.emailAddress,
              validator: (value) {
                if (value == null || value.trim().isEmpty) {
                  return 'Please enter an email address';
                }
                if (!RegExp(r'^[\w-\.]+@([\w-]+\.)+[\w-]{2,4}$').hasMatch(value)) {
                  return 'Please enter a valid email address';
                }
                return null;
              },
            ),
            const SizedBox(height: 16),
            DropdownButtonFormField<FamilyMemberRole>(
              initialValue: _selectedRole,
              decoration: const InputDecoration(
                labelText: 'Role',
                prefixIcon: Icon(Icons.badge),
              ),
              items: FamilyMemberRole.values
                  .where((r) => r != FamilyMemberRole.owner)
                  .map((role) => DropdownMenuItem(
                    value: role,
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.start,
                      mainAxisSize: MainAxisSize.min,
                      children: [
                        Text(role.displayName),
                        Text(
                          role.description,
                          style: theme.textTheme.bodySmall?.copyWith(
                            color: colorScheme.onSurfaceVariant,
                          ),
                        ),
                      ],
                    ),
                  ))
                  .toList(),
              onChanged: (value) {
                if (value != null) {
                  setState(() => _selectedRole = value);
                }
              },
            ),
            const SizedBox(height: 24),
            SizedBox(
              width: double.infinity,
              child: FilledButton(
                onPressed: _isInviting ? null : _inviteMember,
                child: _isInviting
                    ? const SizedBox(
                        width: 20,
                        height: 20,
                        child: CircularProgressIndicator(strokeWidth: 2),
                      )
                    : Text(AppLocalizations.of(context).famSendInvitation),
              ),
            ),
          ],
        ),
      ),
    );
  }
}
