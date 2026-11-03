import 'package:flutter/material.dart';

/// A top-level navigation destination.
///
/// Lives here rather than inside the nav shell because the ORDER of
/// [kNavDestinations] is meaningful beyond drawing the menu: `NavigationDepth`
/// derives page-transition direction from it, so a page listed lower in the menu
/// animates as "deeper". Keeping one list means reordering the menu reorders the
/// transitions automatically instead of silently disagreeing with them.
class NavDestination {
  final String key;
  final String label;
  final IconData icon;
  final IconData selectedIcon;
  final String path;
  final bool isPro;
  final bool isAdmin;
  final bool debugOnly;

  const NavDestination({
    required this.key,
    required this.label,
    required this.icon,
    required this.selectedIcon,
    required this.path,
    this.isPro = false,
    // Admin-only nav gating (filter + badge). Used by the admin broadcast screen.
    this.isAdmin = false,
    this.debugOnly = false,
  });
}

/// Every top-level destination, IN MENU ORDER.
///
/// This order is the single source of truth for both the nav menu and the
/// transition direction between pages. Reorder entries here and both follow.
const kNavDestinations = [
  NavDestination(
    key: 'dashboard',
    label: 'Dashboard',
    icon: Icons.dashboard_outlined,
    selectedIcon: Icons.dashboard,
    path: '/dashboard',
  ),
  NavDestination(
    key: 'transactions',
    label: 'Transactions',
    icon: Icons.receipt_long_outlined,
    selectedIcon: Icons.receipt_long,
    path: '/transactions',
  ),
  NavDestination(
    key: 'budgets',
    label: 'Budgets',
    // Sliders, not a pie: budgets here are per-category allowances you adjust
    // (count-up lanes), not slices of a fixed whole. The pie belongs to Insights,
    // which actually shows the spending donut (tester feedback, 1.5.17).
    icon: Icons.tune_outlined,
    selectedIcon: Icons.tune,
    path: '/budgets',
  ),
  NavDestination(
    key: 'savings',
    label: 'Savings',
    icon: Icons.savings_outlined,
    selectedIcon: Icons.savings,
    path: '/savings',
  ),
  NavDestination(
    key: 'accounts',
    label: 'Accounts',
    icon: Icons.account_balance_outlined,
    selectedIcon: Icons.account_balance,
    path: '/accounts',
  ),
  NavDestination(
    key: 'subscriptions',
    label: 'Subscriptions',
    icon: Icons.subscriptions_outlined,
    selectedIcon: Icons.subscriptions,
    path: '/subscriptions',
  ),
  NavDestination(
    key: 'achievements',
    label: 'Achievements',
    icon: Icons.emoji_events_outlined,
    selectedIcon: Icons.emoji_events,
    path: '/achievements',
  ),
  NavDestination(
    key: 'insights',
    label: 'Insights',
    icon: Icons.pie_chart_outline,
    selectedIcon: Icons.pie_chart,
    path: '/insights',
  ),
  NavDestination(
    key: 'ai-chat',
    label: 'AI Chat',
    icon: Icons.auto_awesome_outlined,
    selectedIcon: Icons.auto_awesome,
    path: '/ai-chat',
    isPro: true,
  ),
  NavDestination(
    key: 'notifications',
    label: 'Notifications',
    icon: Icons.notifications_outlined,
    selectedIcon: Icons.notifications,
    path: '/notifications',
  ),
  NavDestination(
    key: 'profile',
    label: 'Profile',
    icon: Icons.person_outline,
    selectedIcon: Icons.person,
    path: '/profile',
  ),
  NavDestination(
    key: 'settings',
    label: 'Settings',
    icon: Icons.settings_outlined,
    selectedIcon: Icons.settings,
    path: '/settings',
  ),
  NavDestination(
    key: 'broadcast',
    label: 'Broadcast',
    icon: Icons.campaign_outlined,
    selectedIcon: Icons.campaign,
    path: '/admin/broadcast',
    isAdmin: true,
  ),
];
