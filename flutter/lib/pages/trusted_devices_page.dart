import 'package:flutter/material.dart';
import 'package:provider/provider.dart';
import '../l10n/generated/app_localizations.dart';
import '../providers/auth_provider.dart';
import '../services/device_service.dart';
import '../theme/app_theme.dart';
import '../utils/formatters.dart';
import '../widgets/app_toast.dart';

/// Trusted Devices Page
/// 
/// View and manage trusted devices for the account
class TrustedDevicesPage extends StatefulWidget {
  const TrustedDevicesPage({super.key});

  @override
  State<TrustedDevicesPage> createState() => _TrustedDevicesPageState();
}

class _TrustedDevicesPageState extends State<TrustedDevicesPage> {
  List<TrustedDevice> _devices = [];
  bool _isLoading = true;
  String? _error;

  @override
  void initState() {
    super.initState();
    _loadDevices();
  }

  Future<void> _loadDevices() async {
    setState(() {
      _isLoading = true;
      _error = null;
    });

    try {
      final devices = await deviceService.getTrustedDevices();
      setState(() {
        _devices = devices;
        _isLoading = false;
      });
    } catch (e) {
      setState(() {
        _error = 'Failed to load devices';
        _isLoading = false;
      });
    }
  }

  Future<void> _revokeDevice(TrustedDevice device) async {
    final confirm = await showDialog<bool>(
      context: context,
      builder: (context) => AlertDialog(
        title: Text(AppLocalizations.of(context).devRevokeDeviceTitle),
        content: Text(
          device.isCurrentDevice
              ? AppLocalizations.of(context).devRevokeCurrentDeviceWarning
              : AppLocalizations.of(context)
                  .devRevokeDeviceConfirm(device.displayName),
        ),
        actions: [
          TextButton(
            onPressed: () => Navigator.pop(context, false),
            child: Text(AppLocalizations.of(context).devCancel),
          ),
          FilledButton(
            onPressed: () => Navigator.pop(context, true),
            style: FilledButton.styleFrom(
              backgroundColor: Theme.of(context).colorScheme.error,
            ),
            child: Text(AppLocalizations.of(context).devRevoke),
          ),
        ],
      ),
    );

    if (confirm == true) {
      try {
        await deviceService.revokeDevice(device.id);
        if (!mounted) return;

        // Revoking the device you're currently on ends this session — sign out so the
        // promise in the dialog ("revoking it will sign you out") actually holds.
        if (device.isCurrentDevice) {
          await context.read<AuthProvider>().logout();
          return; // auth guard redirects to /login
        }

        setState(() {
          _devices.removeWhere((d) => d.id == device.id);
        });
        if (mounted) {
          showAppToast(
            context,
            '${device.displayName} has been revoked',
            type: AppToastType.success,
          );
        }
      } catch (e) {
        if (mounted) {
          showAppToast(
            context,
            'Failed to revoke device: $e',
            type: AppToastType.error,
          );
        }
      }
    }
  }

  Future<void> _revokeAllDevices() async {
    final confirm = await showDialog<bool>(
      context: context,
      builder: (context) => AlertDialog(
        icon: const Icon(Icons.warning_amber_rounded, size: 48),
        title: Text(AppLocalizations.of(context).devSignOutEverywhereTitle),
        content: Text(
          AppLocalizations.of(context).devSignOutEverywhereBody,
        ),
        actions: [
          TextButton(
            onPressed: () => Navigator.pop(context, false),
            child: Text(AppLocalizations.of(context).devCancel),
          ),
          FilledButton(
            onPressed: () => Navigator.pop(context, true),
            style: FilledButton.styleFrom(
              backgroundColor: Theme.of(context).colorScheme.error,
            ),
            child: Text(AppLocalizations.of(context).devSignOutEverywhere),
          ),
        ],
      ),
    );

    if (confirm == true) {
      try {
        await deviceService.revokeAllDevices();
        if (!mounted) return;
        // The backend has now revoked every session; sign this device out too so the
        // redirect to login happens immediately rather than on the next 401.
        await context.read<AuthProvider>().logout();
      } catch (e) {
        if (mounted) {
          showAppToast(
            context,
            'Failed to sign out everywhere: $e',
            type: AppToastType.error,
          );
        }
      }
    }
  }

  @override
  Widget build(BuildContext context) {
    final colorScheme = Theme.of(context).colorScheme;

    return Scaffold(
      appBar: AppBar(
        title: Text(AppLocalizations.of(context).devTrustedDevicesTitle),
        actions: [
          if (_devices.length > 1)
            TextButton.icon(
              onPressed: _revokeAllDevices,
              icon: Icon(Icons.logout, color: colorScheme.error, size: 20),
              label: Text(
                AppLocalizations.of(context).devSignOutAll,
                style: TextStyle(color: colorScheme.error),
              ),
            ),
        ],
      ),
      body: _isLoading
          ? const Center(child: CircularProgressIndicator())
          : _error != null
              ? _buildErrorState()
              : _devices.isEmpty
                  ? _buildEmptyState()
                  : RefreshIndicator(
                      onRefresh: _loadDevices,
                      child: ListView(
                        padding: const EdgeInsets.all(16),
                        children: [
                          // Info card
                          Container(
                            padding: const EdgeInsets.all(16),
                            decoration: BoxDecoration(
                              color: colorScheme.primaryContainer.withValues(alpha: 0.3),
                              borderRadius: BorderRadius.circular(12),
                            ),
                            child: Row(
                              children: [
                                Icon(
                                  Icons.security,
                                  color: colorScheme.primary,
                                ),
                                const SizedBox(width: 12),
                                Expanded(
                                  child: Text(
                                    AppLocalizations.of(context).devInfoBanner,
                                    style: TextStyle(
                                      fontSize: 13,
                                      color: colorScheme.onSurface.withValues(alpha: 0.7),
                                    ),
                                  ),
                                ),
                              ],
                            ),
                          ),
                          const SizedBox(height: 16),

                          // Devices list
                          ..._devices.map((device) => Padding(
                            padding: const EdgeInsets.only(bottom: 12),
                            child: _buildDeviceCard(device),
                          )),
                        ],
                      ),
                    ),
    );
  }

  Widget _buildDeviceCard(TrustedDevice device) {
    final colorScheme = Theme.of(context).colorScheme;
    final theme = Theme.of(context);
    
    return Container(
      padding: const EdgeInsets.all(16),
      decoration: BoxDecoration(
        color: colorScheme.surface,
        borderRadius: BorderRadius.circular(16),
        border: Border.all(
          color: device.isCurrentDevice
              ? AppTheme.primaryIndigo.withValues(alpha: 0.5)
              : colorScheme.outlineVariant.withValues(alpha: 0.5),
          width: device.isCurrentDevice ? 2 : 1,
        ),
      ),
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          // Device icon
          Container(
            padding: const EdgeInsets.all(12),
            decoration: BoxDecoration(
              color: device.isCurrentDevice
                  ? AppTheme.primaryIndigo.withValues(alpha: 0.1)
                  : colorScheme.surfaceContainerHighest,
              borderRadius: BorderRadius.circular(12),
            ),
            child: Text(
              device.icon,
              style: const TextStyle(fontSize: 24),
            ),
          ),
          const SizedBox(width: 16),
          
          // Device info
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Row(
                  children: [
                    Expanded(
                      child: Text(
                        device.displayName,
                        style: theme.textTheme.titleSmall?.copyWith(
                          fontWeight: FontWeight.w600,
                        ),
                      ),
                    ),
                    if (device.isCurrentDevice)
                      Container(
                        padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 4),
                        decoration: BoxDecoration(
                          color: AppTheme.primaryIndigo.withValues(alpha: 0.1),
                          borderRadius: BorderRadius.circular(8),
                        ),
                        child: Text(
                          AppLocalizations.of(context).devThisDevice,
                          style: TextStyle(
                            fontSize: 11,
                            fontWeight: FontWeight.w600,
                            color: AppTheme.primaryIndigo,
                          ),
                        ),
                      ),
                  ],
                ),
                const SizedBox(height: 4),
                
                // Last used
                Row(
                  children: [
                    Icon(
                      Icons.schedule,
                      size: 14,
                      color: colorScheme.onSurface.withValues(alpha: 0.5),
                    ),
                    const SizedBox(width: 4),
                    Text(
                      AppLocalizations.of(context)
                          .devLastUsed(_formatRelativeTime(device.lastUsedAt)),
                      style: TextStyle(
                        fontSize: 12,
                        color: colorScheme.onSurface.withValues(alpha: 0.6),
                      ),
                    ),
                  ],
                ),
                
                // Location
                if (device.lastUsedLocation != null) ...[
                  const SizedBox(height: 2),
                  Row(
                    children: [
                      Icon(
                        Icons.location_on_outlined,
                        size: 14,
                        color: colorScheme.onSurface.withValues(alpha: 0.5),
                      ),
                      const SizedBox(width: 4),
                      Text(
                        device.lastUsedLocation!,
                        style: TextStyle(
                          fontSize: 12,
                          color: colorScheme.onSurface.withValues(alpha: 0.6),
                        ),
                      ),
                    ],
                  ),
                ],
                
                // Expiration
                const SizedBox(height: 4),
                Row(
                  children: [
                    Icon(
                      device.daysUntilExpiration <= 7
                          ? Icons.warning_amber_rounded
                          : Icons.timer_outlined,
                      size: 14,
                      color: device.daysUntilExpiration <= 7
                          ? Colors.orange
                          : colorScheme.onSurface.withValues(alpha: 0.5),
                    ),
                    const SizedBox(width: 4),
                    Text(
                      device.daysUntilExpiration > 0
                          ? AppLocalizations.of(context)
                              .devExpiresInDays(device.daysUntilExpiration)
                          : AppLocalizations.of(context).devExpired,
                      style: TextStyle(
                        fontSize: 12,
                        color: device.daysUntilExpiration <= 7
                            ? Colors.orange
                            : colorScheme.onSurface.withValues(alpha: 0.6),
                      ),
                    ),
                  ],
                ),
              ],
            ),
          ),
          
          // Revoke button
          IconButton(
            icon: Icon(
              Icons.close,
              color: colorScheme.error,
              size: 20,
            ),
            onPressed: () => _revokeDevice(device),
            tooltip: 'Revoke device',
          ),
        ],
      ),
    );
  }

  Widget _buildEmptyState() {
    final colorScheme = Theme.of(context).colorScheme;
    
    return Center(
      child: Column(
        mainAxisAlignment: MainAxisAlignment.center,
        children: [
          Icon(
            Icons.devices,
            size: 64,
            color: colorScheme.onSurface.withValues(alpha: 0.3),
          ),
          const SizedBox(height: 16),
          Text(
            AppLocalizations.of(context).devNoTrustedDevices,
            style: Theme.of(context).textTheme.titleMedium?.copyWith(
              color: colorScheme.onSurface.withValues(alpha: 0.6),
            ),
          ),
          const SizedBox(height: 8),
          Text(
            AppLocalizations.of(context).devEmptyStateHint,
            style: TextStyle(
              color: colorScheme.onSurface.withValues(alpha: 0.4),
              fontSize: 13,
            ),
          ),
        ],
      ),
    );
  }

  Widget _buildErrorState() {
    final colorScheme = Theme.of(context).colorScheme;
    
    return Center(
      child: Column(
        mainAxisAlignment: MainAxisAlignment.center,
        children: [
          Icon(
            Icons.error_outline,
            size: 48,
            color: colorScheme.error,
          ),
          const SizedBox(height: 16),
          Text(
            _error ?? AppLocalizations.of(context).devErrorOccurred,
            style: TextStyle(color: colorScheme.onSurface),
          ),
          const SizedBox(height: 16),
          FilledButton.icon(
            onPressed: _loadDevices,
            icon: const Icon(Icons.refresh),
            label: Text(AppLocalizations.of(context).devRetry),
          ),
        ],
      ),
    );
  }

  String _formatRelativeTime(DateTime dateTime) {
    final now = DateTime.now();
    final difference = now.difference(dateTime);

    if (difference.inMinutes < 1) {
      return 'Just now';
    } else if (difference.inHours < 1) {
      return '${difference.inMinutes} minutes ago';
    } else if (difference.inDays < 1) {
      return '${difference.inHours} hours ago';
    } else if (difference.inDays < 7) {
      return '${difference.inDays} days ago';
    } else {
      return formatDate(dateTime);
    }
  }
}
