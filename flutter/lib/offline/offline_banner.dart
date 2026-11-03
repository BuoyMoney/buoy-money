import '../l10n/generated/app_localizations.dart';
import 'dart:async';

import 'package:flutter/material.dart';
import 'package:google_fonts/google_fonts.dart';

import 'connectivity_service.dart';
import 'offline_status.dart';

/// Connectivity status banner pinned to the top of every screen.
///
/// States:
///  - hidden    — online, fresh data, nothing pending.
///  - offline   — red banner. "Offline — N changes saved on this device" when
///    writes are queued, else "Offline — showing data from …" / "Offline".
///  - syncing   — back online with queued writes: "Syncing N pending changes…".
///
/// UX (from live device testing): the full banner shows for a few seconds,
/// then slides to the top-LEFT and shrinks to an icon-only chip (cloud-off
/// while offline, a spinner while syncing) so it stops eating screen space.
/// Tapping the chip re-expands it; tapping the expanded banner opens a short
/// explanation that offline changes are stored locally and sync automatically.
///
/// Wrapped in a [Material] because it is mounted ABOVE the Navigator (via
/// MaterialApp.router `builder:`) — without a Material ancestor every Text
/// renders with the framework's yellow double-underline fallback.
class OfflineBanner extends StatefulWidget {
  const OfflineBanner({super.key});

  /// How long the banner stays expanded before collapsing to the chip.
  static const Duration expandedDuration = Duration(seconds: 5);

  @override
  State<OfflineBanner> createState() => _OfflineBannerState();
}

class _OfflineBannerState extends State<OfflineBanner> {
  static const Color _bg = Color(0xFFC62828); // red banner + white text
  static const Color _fg = Colors.white;

  bool _collapsed = false;
  bool _showInfo = false;
  Timer? _collapseTimer;
  _BannerState _lastState = _BannerState.hidden;

  @override
  void dispose() {
    _collapseTimer?.cancel();
    super.dispose();
  }

  void _armCollapse() {
    _collapseTimer?.cancel();
    _collapseTimer = Timer(OfflineBanner.expandedDuration, () {
      if (mounted) setState(() => _collapsed = true);
    });
  }

  void _expand() {
    setState(() {
      _collapsed = false;
      _showInfo = false;
    });
    _armCollapse();
  }

  void _toggleInfo() {
    setState(() => _showInfo = !_showInfo);
    // Keep it open while the user reads; collapse timer restarts on close.
    if (!_showInfo) _armCollapse();
    if (_showInfo) _collapseTimer?.cancel();
  }

  @override
  Widget build(BuildContext context) {
    return Positioned(
      top: 0,
      left: 0,
      right: 0,
      child: SafeArea(
        bottom: false,
        child: Material(
          type: MaterialType.transparency,
          child: ListenableBuilder(
            listenable: Listenable.merge([connectivityService, offlineStatus]),
            builder: (context, _) {
              final online = connectivityService.isOnline;
              final pending = offlineStatus.pendingOutboxCount;
              final state = !online
                  ? _BannerState.offline
                  : (pending > 0 ? _BannerState.syncing : _BannerState.hidden);

              // On any state transition, re-expand and re-arm the collapse.
              if (state != _lastState) {
                _lastState = state;
                if (state != _BannerState.hidden) {
                  _collapsed = false;
                  _showInfo = false;
                  _armCollapse();
                }
              }

              if (state == _BannerState.hidden) {
                return const SizedBox.shrink(
                    key: ValueKey('offline-banner-hidden'));
              }

              final message = _message(state, pending);
              return AnimatedAlign(
                duration: const Duration(milliseconds: 300),
                curve: Curves.easeOutCubic,
                alignment:
                    _collapsed ? Alignment.topLeft : Alignment.topCenter,
                child: Padding(
                  padding: const EdgeInsets.only(top: 8, left: 8, right: 8),
                  child: _collapsed
                      ? _chip(state)
                      : _expanded(state, message),
                ),
              );
            },
          ),
        ),
      ),
    );
  }

  String _message(_BannerState state, int pending) {
    if (state == _BannerState.syncing) {
      return 'Syncing $pending pending ${pending == 1 ? 'change' : 'changes'}…';
    }
    if (pending > 0) {
      return 'Offline — $pending ${pending == 1 ? 'change' : 'changes'} saved on this device';
    }
    final cacheTime = offlineStatus.oldestServedCacheTime;
    return cacheTime != null
        ? 'Offline — showing data from ${_relative(cacheTime)}'
        : 'Offline';
  }

  /// Icon-only chip docked top-left. Tap to re-expand.
  Widget _chip(_BannerState state) {
    return GestureDetector(
      key: const ValueKey('offline-banner-chip'),
      onTap: _expand,
      child: Container(
        width: 34,
        height: 34,
        decoration: const BoxDecoration(color: _bg, shape: BoxShape.circle),
        alignment: Alignment.center,
        child: state == _BannerState.syncing
            ? const SizedBox(
                width: 15,
                height: 15,
                child: CircularProgressIndicator(strokeWidth: 2, color: _fg),
              )
            : const Icon(Icons.cloud_off_rounded, size: 17, color: _fg),
      ),
    );
  }

  Widget _expanded(_BannerState state, String message) {
    return GestureDetector(
      key: ValueKey('offline-banner-$message'),
      onTap: _toggleInfo,
      child: Container(
        constraints: const BoxConstraints(maxWidth: 520),
        padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 8),
        decoration: BoxDecoration(
          color: _bg,
          borderRadius: BorderRadius.circular(12),
        ),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Row(
              mainAxisSize: MainAxisSize.min,
              children: [
                if (state == _BannerState.syncing)
                  const SizedBox(
                    width: 13,
                    height: 13,
                    child:
                        CircularProgressIndicator(strokeWidth: 2, color: _fg),
                  )
                else
                  const Icon(Icons.cloud_off_rounded, size: 15, color: _fg),
                const SizedBox(width: 8),
                Flexible(
                  child: Text(
                    message,
                    overflow: TextOverflow.ellipsis,
                    style: GoogleFonts.sora(
                      fontSize: 12.5,
                      fontWeight: FontWeight.w600,
                      color: _fg,
                    ),
                  ),
                ),
              ],
            ),
            if (_showInfo) ...[
              const SizedBox(height: 6),
              Text(
                AppLocalizations.of(context).uiOfflineSyncNote,
                key: const ValueKey('offline-banner-info'),
                style: GoogleFonts.sora(
                  fontSize: 11.5,
                  color: _fg.withValues(alpha: 0.9),
                ),
              ),
            ],
          ],
        ),
      ),
    );
  }

  /// Compact human-friendly relative time ("just now", "5m ago", "3h ago",
  /// "yesterday", "2d ago").
  static String _relative(DateTime when) {
    final diff = DateTime.now().toUtc().difference(when.toUtc());
    if (diff.isNegative || diff.inSeconds < 45) return 'just now';
    if (diff.inMinutes < 60) return '${diff.inMinutes}m ago';
    if (diff.inHours < 24) return '${diff.inHours}h ago';
    if (diff.inDays == 1) return 'yesterday';
    return '${diff.inDays}d ago';
  }
}

enum _BannerState { hidden, offline, syncing }
