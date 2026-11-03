import '../l10n/generated/app_localizations.dart';
import 'package:flutter/material.dart';
import '../services/email_preferences_service.dart';
import '../widgets/app_toast.dart';
import '../widgets/cinematic/glass_settings.dart';

/// Email Preferences Page
///
/// Allows users to manage their email notification preferences
class EmailPreferencesPage extends StatefulWidget {
  const EmailPreferencesPage({super.key, this.service});

  /// Test seam; the app uses the shared instance.
  final EmailPreferencesService? service;

  @override
  State<EmailPreferencesPage> createState() => _EmailPreferencesPageState();
}

class _EmailPreferencesPageState extends State<EmailPreferencesPage> {
  EmailPreferences? _preferences;
  bool _isLoading = true;
  String? _error;
  final Set<String> _savingKeys = {};

  EmailPreferencesService get _service =>
      widget.service ?? emailPreferencesService;

  @override
  void initState() {
    super.initState();
    _loadPreferences();
  }

  Future<void> _loadPreferences() async {
    setState(() {
      _isLoading = true;
      _error = null;
    });

    try {
      final prefs = await _service.getPreferences();
      setState(() {
        _preferences = prefs;
        _isLoading = false;
      });
    } catch (e) {
      setState(() {
        _error = 'Failed to load preferences';
        _isLoading = false;
      });
    }
  }

  Future<void> _updatePreference(String key, bool value) async {
    if (_savingKeys.contains(key)) return;

    setState(() {
      _savingKeys.add(key);
    });

    try {
      final updated = await _service.updateSinglePreference(key, value);
      setState(() {
        _preferences = updated;
        _savingKeys.remove(key);
      });
    } catch (e) {
      setState(() {
        _savingKeys.remove(key);
      });
      if (mounted) {
        showAppToast(
          context,
          'Failed to update preference: $e',
          type: AppToastType.error,
        );
      }
    }
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    return GlassSubPage(
      title: l10n.uiEmailPreferences,
      onRefresh: (_isLoading || _error != null) ? null : _loadPreferences,
      children: _isLoading
          ? const [GlassSpinner()]
          : _error != null
              ? [_buildErrorState()]
              : [
                  GlassNote(
                      text: l10n.uiSecurityEmailsNote,
                      icon: Icons.shield_outlined),
                  const SizedBox(height: 16),

                  // Account Activity
                  _buildSection(
                      Icons.account_balance_outlined, 'Account Activity', [
                    _PreferenceTile(
                      title: 'Connection Issues',
                      subtitle:
                          'Get notified when bank connections need attention',
                      value: _preferences?.plaidConnectionIssues ?? true,
                      isSaving: _savingKeys.contains('plaidConnectionIssues'),
                      onChanged: (v) =>
                          _updatePreference('plaidConnectionIssues', v),
                    ),
                    _PreferenceTile(
                      title: 'Reconnection Success',
                      subtitle: 'Confirm when connections are restored',
                      value: _preferences?.plaidReconnectionSuccess ?? true,
                      isSaving:
                          _savingKeys.contains('plaidReconnectionSuccess'),
                      onChanged: (v) =>
                          _updatePreference('plaidReconnectionSuccess', v),
                    ),
                    _PreferenceTile(
                      title: 'Subscription Changes',
                      subtitle: 'Billing updates and plan changes',
                      value: _preferences?.subscriptionChanges ?? true,
                      isSaving: _savingKeys.contains('subscriptionChanges'),
                      onChanged: (v) =>
                          _updatePreference('subscriptionChanges', v),
                    ),
                    _PreferenceTile(
                      title: 'Trial Warnings',
                      subtitle: 'Reminders before your trial expires',
                      value: _preferences?.trialExpirationWarnings ?? true,
                      isSaving: _savingKeys.contains('trialExpirationWarnings'),
                      onChanged: (v) =>
                          _updatePreference('trialExpirationWarnings', v),
                    ),
                  ]),

                  // Financial Insights
                  _buildSection(Icons.insights_outlined, 'Financial Insights', [
                    _PreferenceTile(
                      title: 'Budget Alerts',
                      subtitle: 'Get notified when approaching budget limits',
                      value: _preferences?.budgetAlerts ?? true,
                      isSaving: _savingKeys.contains('budgetAlerts'),
                      onChanged: (v) => _updatePreference('budgetAlerts', v),
                    ),
                    _PreferenceTile(
                      title: 'Savings Milestones',
                      subtitle: 'Celebrate when you hit savings goals',
                      value: _preferences?.savingsGoalMilestones ?? true,
                      isSaving: _savingKeys.contains('savingsGoalMilestones'),
                      onChanged: (v) =>
                          _updatePreference('savingsGoalMilestones', v),
                    ),
                    _PreferenceTile(
                      title: 'Large Transactions',
                      subtitle: 'Alert for unusually large transactions',
                      value: _preferences?.largeTransactionAlerts ?? true,
                      isSaving: _savingKeys.contains('largeTransactionAlerts'),
                      onChanged: (v) =>
                          _updatePreference('largeTransactionAlerts', v),
                    ),
                    _PreferenceTile(
                      title: 'Monthly Summary',
                      subtitle: 'Monthly financial overview email',
                      value: _preferences?.monthlySummary ?? true,
                      isSaving: _savingKeys.contains('monthlySummary'),
                      onChanged: (v) => _updatePreference('monthlySummary', v),
                    ),
                  ]),

                  // Marketing & Updates
                  _buildSection(
                      Icons.campaign_outlined, 'Marketing & Updates', [
                    _PreferenceTile(
                      title: 'Feature Announcements',
                      subtitle: 'Learn about new features',
                      value: _preferences?.featureAnnouncements ?? false,
                      isSaving: _savingKeys.contains('featureAnnouncements'),
                      onChanged: (v) =>
                          _updatePreference('featureAnnouncements', v),
                    ),
                    _PreferenceTile(
                      title: 'Tips & Best Practices',
                      subtitle: 'Financial tips and app usage guides',
                      value: _preferences?.tipsAndBestPractices ?? false,
                      isSaving: _savingKeys.contains('tipsAndBestPractices'),
                      onChanged: (v) =>
                          _updatePreference('tipsAndBestPractices', v),
                    ),
                    _PreferenceTile(
                      title: 'Product Updates',
                      subtitle: 'Important app updates and changes',
                      value: _preferences?.productUpdates ?? true,
                      isSaving: _savingKeys.contains('productUpdates'),
                      onChanged: (v) => _updatePreference('productUpdates', v),
                    ),
                    _PreferenceTile(
                      title: 'Promotional Offers',
                      subtitle: 'Special offers and discounts',
                      value: _preferences?.promotionalOffers ?? false,
                      isSaving: _savingKeys.contains('promotionalOffers'),
                      onChanged: (v) =>
                          _updatePreference('promotionalOffers', v),
                    ),
                  ]),

                  // Achievements
                  _buildSection(Icons.emoji_events_outlined,
                      'Achievements & Gamification', [
                    _PreferenceTile(
                      title: 'Badge Earned',
                      subtitle: 'Notifications when you earn badges',
                      value: _preferences?.badgeEarned ?? true,
                      isSaving: _savingKeys.contains('badgeEarned'),
                      onChanged: (v) => _updatePreference('badgeEarned', v),
                    ),
                    _PreferenceTile(
                      title: 'Milestone Reached',
                      subtitle: 'Celebrate financial milestones',
                      value: _preferences?.milestoneReached ?? true,
                      isSaving: _savingKeys.contains('milestoneReached'),
                      onChanged: (v) =>
                          _updatePreference('milestoneReached', v),
                    ),
                    _PreferenceTile(
                      title: 'Streak Notifications',
                      subtitle: 'Keep your savings streaks alive',
                      value: _preferences?.streakNotifications ?? true,
                      isSaving: _savingKeys.contains('streakNotifications'),
                      onChanged: (v) =>
                          _updatePreference('streakNotifications', v),
                    ),
                  ]),
                ],
    );
  }

  Widget _buildErrorState() => GlassLoadError(
        message: _error ?? 'An error occurred',
        retryLabel: AppLocalizations.of(context).uiRetry2,
        onRetry: _loadPreferences,
      );

  Widget _buildSection(
          IconData icon, String title, List<_PreferenceTile> tiles) =>
      GlassSection(
        icon: icon,
        title: title,
        children: [
          for (final (i, tile) in tiles.indexed) ...[
            if (i > 0) const GlassDivider(height: 9),
            tile,
          ],
        ],
      );
}

class _PreferenceTile extends StatelessWidget {
  final String title;
  final String subtitle;
  final bool value;
  final bool isSaving;
  final ValueChanged<bool> onChanged;

  const _PreferenceTile({
    required this.title,
    required this.subtitle,
    required this.value,
    required this.isSaving,
    required this.onChanged,
  });

  @override
  Widget build(BuildContext context) => GlassSwitchRow(
        title: title,
        subtitle: subtitle,
        value: value,
        busy: isSaving,
        onChanged: onChanged,
      );
}
