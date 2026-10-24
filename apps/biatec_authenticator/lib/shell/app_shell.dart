import 'package:flutter/material.dart';
import 'package:flutter/services.dart';

import '../approvals/approval_services.dart';
import '../approvals/history_screen.dart';
import '../approvals/pending_screen.dart';
import '../clients/clients_screen.dart';
import '../config.dart';
import '../devices/devices_screen.dart';
import '../push/push_controller.dart';
import '../security/security_screen.dart';
import '../security/settings_controller.dart';
import 'shell_controller.dart';

/// Width from which the bar moves to the side as a navigation rail (tablets, desktop).
const double kRailBreakpoint = 840;

/// The signed-in app: five destinations in a bottom navigation bar (a rail on wide screens). Each destination keeps its own stack and scroll position while another is showing.
class AppShell extends StatefulWidget {
  const AppShell({
    super.key,
    required this.config,
    required this.approvals,
    required this.settings,
    required this.controller,
    this.push,
  });

  final AppConfig config;
  final ApprovalServices approvals;
  final SettingsServices settings;
  final ShellController controller;

  /// Null when push is not configured; the pending list then polls only.
  final PushController? push;

  @override
  State<AppShell> createState() => _AppShellState();
}

class _AppShellState extends State<AppShell> {
  /// Tabs are built the first time they are shown, so a tab nobody opened makes no requests.
  late final Set<AppTab> _visited = {
    AppTab.approvals,
    widget.controller.selected
  };

  @override
  void initState() {
    super.initState();
    // A count left from an earlier shell (the device was re-enrolled) is not this session's.
    widget.controller.pendingCount.value = 0;
    widget.controller.approvalsActive.value =
        widget.controller.selected == AppTab.approvals;
    widget.controller.addListener(_onSelect);
    // Once the tab navigators exist, open a card a notification asked for.
    WidgetsBinding.instance
        .addPostFrameCallback((_) => widget.controller.openQueued());
  }

  @override
  void didUpdateWidget(AppShell old) {
    super.didUpdateWidget(old);
    // The tabs are built once from these, so they are fixed for the life of the shell.
    assert(old.controller == widget.controller &&
        old.approvals == widget.approvals &&
        old.settings == widget.settings &&
        old.push == widget.push);
  }

  @override
  void dispose() {
    widget.controller.removeListener(_onSelect);
    super.dispose();
  }

  void _onSelect() => setState(() => _visited.add(widget.controller.selected));

  Widget _root(AppTab tab) => switch (tab) {
        AppTab.approvals => PendingScreen(
            services: widget.approvals,
            pushes: widget.push?.requestArrived,
            active: widget.controller.approvalsActive,
            onCount: (n) => widget.controller.pendingCount.value = n,
          ),
        AppTab.history => HistoryScreen(history: widget.approvals.history),
        AppTab.devices => DevicesScreen(services: widget.settings),
        AppTab.clients => ClientsScreen(services: widget.settings),
        AppTab.settings => SecurityScreen(services: widget.settings),
      };

  /// Back goes up inside the tab first, then to the first tab, and only then leaves the app.
  Future<void> _back() async {
    final c = widget.controller;
    final navigator = c.navigators[c.selected]?.currentState;
    if (navigator != null && await navigator.maybePop()) return;
    if (c.selected != AppTab.approvals) {
      c.select(AppTab.approvals);
      return;
    }
    await SystemNavigator.pop();
  }

  Widget _icon(AppTab tab, {required bool selected}) {
    final icon = selected ? tab.selectedIcon : tab.icon;
    if (tab != AppTab.approvals) return Icon(icon);
    return ValueListenableBuilder<int>(
      valueListenable: widget.controller.pendingCount,
      builder: (context, n, _) => Badge(
        label: Text('$n', semanticsLabel: '$n pending'),
        isLabelVisible: n > 0,
        child: Icon(icon),
      ),
    );
  }

  @override
  Widget build(BuildContext context) {
    final c = widget.controller;
    final wide = MediaQuery.sizeOf(context).width >= kRailBreakpoint;
    final index = c.selected.index;
    final body = IndexedStack(
      index: index,
      children: [
        for (final tab in AppTab.values)
          _visited.contains(tab)
              ? _TabNavigator(
                  key: ValueKey(tab),
                  navigatorKey: c.navigators[tab]!,
                  push: widget.push,
                  root: _root(tab),
                  tab: tab,
                )
              : const SizedBox.shrink(),
      ],
    );
    return PopScope(
      canPop: false,
      onPopInvokedWithResult: (didPop, _) {
        if (!didPop) _back();
      },
      child: Scaffold(
        body: wide
            ? Row(children: [
                // Keeps the first destination out from under a display cutout in landscape.
                SafeArea(
                  right: false,
                  child: NavigationRail(
                    selectedIndex: index,
                    onDestinationSelected: (i) => c.select(AppTab.values[i]),
                    destinations: [
                      for (final tab in AppTab.values)
                        NavigationRailDestination(
                          icon: _icon(tab, selected: false),
                          selectedIcon: _icon(tab, selected: true),
                          label: Text(tab.label),
                        ),
                    ],
                  ),
                ),
                const VerticalDivider(width: 1),
                Expanded(child: body),
              ])
            : body,
        bottomNavigationBar: wide
            ? null
            : NavigationBar(
                selectedIndex: index,
                onDestinationSelected: (i) => c.select(AppTab.values[i]),
                destinations: [
                  for (final tab in AppTab.values)
                    NavigationDestination(
                      icon: _icon(tab, selected: false),
                      selectedIcon: _icon(tab, selected: true),
                      label: tab.label,
                      tooltip: tab.label,
                    ),
                ],
              ),
      ),
    );
  }
}

/// One tab's own navigator. Pushes inside the tab stay inside it, below the navigation bar.
class _TabNavigator extends StatelessWidget {
  const _TabNavigator({
    super.key,
    required this.navigatorKey,
    required this.root,
    required this.tab,
    this.push,
  });

  final GlobalKey<NavigatorState> navigatorKey;
  final Widget root;
  final AppTab tab;
  final PushController? push;

  @override
  Widget build(BuildContext context) {
    return Navigator(
      key: navigatorKey,
      observers: [if (push != null) _ForwardingObserver(push!)],
      onGenerateRoute: (settings) => MaterialPageRoute<void>(
        settings: RouteSettings(name: '/${tab.name}'),
        builder: (_) => root,
      ),
    );
  }
}

/// A [NavigatorObserver] belongs to one navigator, and the push controller has to hear about every tab's navigator, so each tab gets one of these that passes the events on.
class _ForwardingObserver extends NavigatorObserver {
  _ForwardingObserver(this.target);

  final PushController target;

  @override
  void didPush(Route<dynamic> route, Route<dynamic>? previousRoute) =>
      target.didPush(route, previousRoute);

  @override
  void didPop(Route<dynamic> route, Route<dynamic>? previousRoute) =>
      target.didPop(route, previousRoute);

  @override
  void didRemove(Route<dynamic> route, Route<dynamic>? previousRoute) =>
      target.didRemove(route, previousRoute);
}
