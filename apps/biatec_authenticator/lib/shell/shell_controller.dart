import 'package:flutter/material.dart';

import '../approvals/approval_screen.dart';
import '../approvals/approval_services.dart';

/// The top-level destinations of the app, in bottom-navigation order.
enum AppTab {
  approvals(
    label: 'Approvals',
    icon: Icons.fact_check_outlined,
    selectedIcon: Icons.fact_check,
  ),
  history(
    label: 'History',
    icon: Icons.receipt_long_outlined,
    selectedIcon: Icons.receipt_long,
  ),
  devices(
    label: 'Devices',
    icon: Icons.phone_android_outlined,
    selectedIcon: Icons.phone_android,
  ),
  clients(
    label: 'Clients',
    icon: Icons.verified_user_outlined,
    selectedIcon: Icons.verified_user,
  ),
  settings(
    label: 'Settings',
    icon: Icons.settings_outlined,
    selectedIcon: Icons.settings,
  );

  const AppTab({
    required this.label,
    required this.icon,
    required this.selectedIcon,
  });

  final String label;
  final IconData icon;
  final IconData selectedIcon;
}

/// Which tab is showing, one navigator per tab so each keeps its own stack, and the number of pending requests for the badge. Also how the rest of the app (a tapped notification, the in-app notice) reaches the Approvals tab.
class ShellController extends ChangeNotifier {
  AppTab _selected = AppTab.approvals;
  AppTab get selected => _selected;

  final Map<AppTab, GlobalKey<NavigatorState>> navigators = {
    for (final tab in AppTab.values)
      tab: GlobalKey<NavigatorState>(debugLabel: 'tab-${tab.name}'),
  };

  /// Set by the pending list each time it loads; drives the Approvals badge.
  final ValueNotifier<int> pendingCount = ValueNotifier(0);

  /// Whether the Approvals tab is the one showing. The pending list polls only while it is.
  final ValueNotifier<bool> approvalsActive = ValueNotifier(true);

  /// A card to open once the shell exists: a notification that started the app is tapped before the enrollment check has finished.
  (ApprovalServices, String)? _queuedOpen;

  void _show(AppTab tab) {
    _selected = tab;
    approvalsActive.value = tab == AppTab.approvals;
    notifyListeners();
  }

  /// Shows [tab]. Choosing the tab that is already showing returns it to its first screen.
  void select(AppTab tab) {
    if (tab == _selected) {
      navigators[tab]?.currentState?.popUntil((r) => r.isFirst);
      return;
    }
    _show(tab);
  }

  /// Shows the Approvals tab on its list, for the in-app notice.
  void showApprovals() {
    navigators[AppTab.approvals]?.currentState?.popUntil((r) => r.isFirst);
    _show(AppTab.approvals);
  }

  /// Opens the card for [authReqId] inside the Approvals tab (a tapped notification). While the app has no shell yet (it is still checking enrollment, or the device is not enrolled) the request is kept and opened when the shell appears.
  void openApproval(ApprovalServices services, String authReqId) {
    final navigator = navigators[AppTab.approvals]?.currentState;
    if (navigator == null) {
      _queuedOpen = (services, authReqId);
      return;
    }
    _show(AppTab.approvals);
    navigator.push(ApprovalScreen.route(services, authReqId));
  }

  /// Called by the shell once it is on screen: opens a card that was asked for before.
  void openQueued() {
    final queued = _queuedOpen;
    if (queued == null) return;
    _queuedOpen = null;
    openApproval(queued.$1, queued.$2);
  }

  @override
  void dispose() {
    pendingCount.dispose();
    approvalsActive.dispose();
    super.dispose();
  }
}
