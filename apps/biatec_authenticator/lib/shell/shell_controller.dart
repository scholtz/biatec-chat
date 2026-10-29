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

  /// Whether the Approvals tab is the one showing. The pending list polls quickly only while it is.
  final ValueNotifier<bool> approvalsActive = ValueNotifier(true);

  /// False while the app is in the background: nothing polls for a screen nobody is looking at.
  final ValueNotifier<bool> foreground = ValueNotifier(true);

  /// Follows the Approvals tab's stack, so a card that is already open can be found (see [bringForward]).
  NavigatorObserver get approvalsObserver => _approvalsStack;
  final _StackObserver _approvalsStack = _StackObserver();

  /// Bumped when a tab is shown after a while away, so its first screen loads its data again in place (what it shows and what the user typed stay). The Approvals tab refreshes itself.
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
    // History is read from this device, so it is read again on every visit: a decision just made shows at once.
    if (left != null &&
        (tab == AppTab.history ||
            (tab != AppTab.approvals &&
                now.difference(left) >= reloadAfter))) {
      reloads[tab]!.value++;
    }
    _selected = tab;
    approvalsActive.value = tab == AppTab.approvals;
    notifyListeners();
  }

  /// Shows [tab]. Choosing the tab that is already showing goes back towards its first screen, but never closes an approval card.
  void select(AppTab tab) {
    if (tab == _selected) {
      _popTowardsRoot(tab);
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
  void appPaused() {
    _pausedAt ??= clock();
    if (!_disposed) foreground.value = false;
  }

  /// The app came back. A tab that has been away for a while loads afresh, and the list of requests is asked again: what it showed may be hours old.
  void appResumed() {
    final at = _pausedAt;
    _pausedAt = null;
    if (_disposed) return;
    foreground.value = true;
    if (at == null) return;
    if (clock().difference(at) < reloadAfter) return;
    if (_selected != AppTab.approvals) reloads[_selected]!.value++;
    requestsChanged.value++;
  }

  /// Shows the Approvals tab on its list, for the in-app notice. A card the user has open stays: back from it leads to the list.
  void showApprovals() {
    _popTowardsRoot(AppTab.approvals);
    if (_selected != AppTab.approvals) _show(AppTab.approvals);
  }

  /// Back to the first tab with nothing remembered, for a new session of the shell (after a sign-out and enrollment).
  ///
  /// What the shell session saw is reset; whether the app is in the foreground ([foreground], the pause time) belongs to the app, not the session, and stays, as does [canQueue], which the gate sets.
  void reset() {
    _selected = AppTab.approvals;
    approvalsActive.value = true;
    // A count from the old session is not the next one's.
    pendingCount.value = 0;
    _leftAt.clear();
    _queuedOpen = null;
    // A navigator that is taken down reports nothing about the routes it had.
    _approvalsStack.routes.clear();
  }

  /// A notification was tapped for a card that is already open: its tab is shown, and the card is uncovered when only screens that can be opened again lie on it (the recovery code screen). The card itself is never closed, so a code being typed or a signature in progress is not lost; another card on top is left as well, and back leads to this one.
  void bringForward(ApprovalServices services, String authReqId) {
    final navigator = navigators[AppTab.approvals]?.currentState;
    final stack = _approvalsStack.routes;
    final name = '${ApprovalScreen.routePrefix}$authReqId';
    final open = stack.where((r) => r.settings.name == name).lastOrNull;
    if (navigator == null || open == null) {
      // Not in this tab (it went with an earlier shell): open it.
      openApproval(services, authReqId);
      return;
    }
    focusApprovals();
    final above = stack.sublist(stack.indexOf(open) + 1);
    if (above.every((r) => reopenableRoutes.contains(r.settings.name))) {
      navigator.popUntil((r) => identical(r, open));
    }
  }

  /// The recovery code screen inside the Approvals tab (opened from the banner).
  static const recoveryCodeRoute = '/recovery-code';

  /// Screens that may be closed for the user, because nothing is lost: they can be opened again as they were. An approval card is never one.
  static const reopenableRoutes = {recoveryCodeRoute};

  static bool _isCard(Route<dynamic> r) =>
      r.settings.name?.startsWith(ApprovalScreen.routePrefix) ?? false;

  /// Back towards the first screen of [tab], stopping at an approval card: one that is open may be half decided.
  void _popTowardsRoot(AppTab tab) {
    navigators[tab]?.currentState?.popUntil((r) => r.isFirst || _isCard(r));
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
    foreground.dispose();
    requestsChanged.dispose();
    for (final n in reloads.values) {
      n.dispose();
    }
    super.dispose();
  }
}

/// The routes of one navigator, bottom first.
class _StackObserver extends NavigatorObserver {
  final List<Route<dynamic>> routes = [];

  @override
  void didPush(Route<dynamic> route, Route<dynamic>? previousRoute) =>
      routes.add(route);

  @override
  void didPop(Route<dynamic> route, Route<dynamic>? previousRoute) =>
      routes.remove(route);

  @override
  void didRemove(Route<dynamic> route, Route<dynamic>? previousRoute) =>
      routes.remove(route);

  @override
  void didReplace({Route<dynamic>? newRoute, Route<dynamic>? oldRoute}) {
    final i = oldRoute == null ? -1 : routes.indexOf(oldRoute);
    if (i < 0 || newRoute == null) return;
    routes[i] = newRoute;
  }
}
