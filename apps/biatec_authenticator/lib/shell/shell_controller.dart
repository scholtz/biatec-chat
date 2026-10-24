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

  /// Bumped when a tab is shown, so its first screen loads afresh instead of showing what it read on the first visit. The Approvals tab refreshes itself.
  final Map<AppTab, ValueNotifier<int>> reloads = {
    for (final tab in AppTab.values) tab: ValueNotifier(0),
  };

  /// Bumped when a card opened from outside the list (a tapped notification) closes, so the list does not show a request that was just decided.
  final ValueNotifier<int> requestsChanged = ValueNotifier(0);

  /// False once the app is known to end in enrollment rather than the shell: a tapped notification is then not kept.
  bool canQueue = true;

  /// A tab not shown for this long loads afresh when it is shown again; a quick switch keeps scroll position and typing.
  static const reloadAfter = Duration(seconds: 30);
  final Map<AppTab, DateTime> _leftAt = {};
  bool _disposed = false;

  /// A card to open once the shell exists: a notification that started the app is tapped before the enrollment check has finished. Only the latest is kept, and only for [queuedOpenTtl].
  (ApprovalServices, String, DateTime)? _queuedOpen;

  static const queuedOpenTtl = Duration(minutes: 2);
  DateTime Function() clock = DateTime.now;

  void _show(AppTab tab) {
    final now = clock();
    _leftAt[_selected] = now;
    final left = _leftAt[tab];
    if (tab != AppTab.approvals &&
        left != null &&
        now.difference(left) >= reloadAfter) {
      reloads[tab]!.value++;
    }
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

  /// Brings the Approvals tab to the front as it is (a card that is open stays open).
  void focusApprovals() {
    if (_selected != AppTab.approvals) _show(AppTab.approvals);
  }

  DateTime? _pausedAt;

  /// The app went to the background.
  void appPaused() => _pausedAt = clock();

  /// The app came back. A tab that has been away for a while loads afresh, and the list of requests is asked again: what it showed may be hours old.
  void appResumed() {
    final at = _pausedAt;
    _pausedAt = null;
    if (at == null || _disposed) return;
    if (clock().difference(at) < reloadAfter) return;
    if (_selected != AppTab.approvals) reloads[_selected]!.value++;
    requestsChanged.value++;
  }

  /// Shows the Approvals tab on its list, for the in-app notice.
  void showApprovals() {
    navigators[AppTab.approvals]?.currentState?.popUntil((r) => r.isFirst);
    _show(AppTab.approvals);
  }

  /// Back to the first tab with nothing remembered, for a new session of the shell (after a sign-out and enrollment).
  void reset() {
    _selected = AppTab.approvals;
    approvalsActive.value = true;
    _leftAt.clear();
    _queuedOpen = null;
  }

  /// Opens the card for [authReqId] inside the Approvals tab (a tapped notification). While the app has no shell yet (it is still checking enrollment, or the device is not enrolled) the request is kept and opened when the shell appears.
  void openApproval(ApprovalServices services, String authReqId) {
    final navigator = navigators[AppTab.approvals]?.currentState;
    if (navigator == null) {
      if (canQueue) _queuedOpen = (services, authReqId, clock());
      return;
    }
    _show(AppTab.approvals);
    navigator.push(ApprovalScreen.route(services, authReqId)).whenComplete(() {
      if (!_disposed) requestsChanged.value++;
    });
  }

  /// Called by the shell once it is on screen: opens a card that was asked for before.
  void openQueued() {
    if (_disposed) return;
    final queued = _queuedOpen;
    if (queued == null) return;
    // Still no shell: it stays queued with its original time, so it can expire.
    if (navigators[AppTab.approvals]?.currentState == null) return;
    _queuedOpen = null;
    if (clock().difference(queued.$3) > queuedOpenTtl) return;
    openApproval(queued.$1, queued.$2);
  }

  /// Forgets a queued card, for a launch that ends in enrollment rather than the shell.
  void dropQueuedOpen() => _queuedOpen = null;

  @override
  void dispose() {
    _disposed = true;
    pendingCount.dispose();
    approvalsActive.dispose();
    requestsChanged.dispose();
    for (final n in reloads.values) {
      n.dispose();
    }
    super.dispose();
  }
}
