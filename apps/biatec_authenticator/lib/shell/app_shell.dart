import 'dart:math' as math;

import 'package:flutter/foundation.dart' show ValueListenable;
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
import '../theme.dart';
import 'shell_controller.dart';
import 'tab_reload.dart';

/// Width from which the bar moves to the side as a navigation rail (tablets, desktop).
const double kRailBreakpoint = 840;

/// Shortest window that still shows the rail (dp), at normal text size.
const double kRailMinHeight = 480;

/// Height the rail needs for its five labelled destinations at [textScale]; it does not scroll.
double railHeightNeeded(double textScale) =>
    math.max(kRailMinHeight, 5 * (48 + 20 * textScale) + 40);

/// The signed-in app: five destinations in a bottom navigation bar (a rail on wide screens). Each destination keeps its own stack and scroll position while another is showing.
class AppShell extends StatefulWidget {
  const AppShell({
    super.key,
    required this.config,
    required this.approvals,
    required this.settings,
    required this.controller,
    this.push,
    this.banner,
  });

  final AppConfig config;
  final ApprovalServices approvals;
  final SettingsServices settings;
  final ShellController controller;

  /// Null when push is not configured; the pending list then polls only.
  final PushController? push;

  /// A notice shown above every tab (for example that the recovery code has not been saved), or null.
  final Widget? banner;

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
    // The controller may have been told to start on another tab (see ShellController.reset for the rest).
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

  /// One for the Devices, Clients and Settings tabs: they show the same data, which is then loaded once.
  SettingsController get _settings =>
      _settingsController ??= widget.settings.controller();
  SettingsController? _settingsController;

  @override
  void dispose() {
    _settingsController?.dispose();
    widget.controller.removeListener(_onSelect);
    // The next shell (after a sign-out and a new enrollment) starts on Approvals.
    widget.controller.reset();
    widget.push?.clearOpenRequests();
    super.dispose();
  }

  void _onSelect() => setState(() => _visited.add(widget.controller.selected));

  Widget _root(AppTab tab) => switch (tab) {
        AppTab.approvals => PendingScreen(
            services: widget.approvals,
            pushes: widget.push?.requestArrived,
            active: widget.controller.approvalsActive,
            foreground: widget.controller.foreground,
            refreshOn: widget.controller.requestsChanged,
            onCount: (n) => widget.controller.pendingCount.value = n,
          ),
        AppTab.history => HistoryScreen(history: widget.approvals.history),
        AppTab.devices =>
          DevicesScreen(services: widget.settings, controller: _settings),
        AppTab.clients =>
          ClientsScreen(services: widget.settings, controller: _settings),
        AppTab.settings => SecurityScreen(
            services: widget.settings,
            server: widget.config.oidcBaseUrl,
            controller: _settings),
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
    final size = MediaQuery.sizeOf(context);
    // A rail needs height for five labelled destinations; a phone on its side keeps the bar.
    final textScale = MediaQuery.textScalerOf(context).scale(1);
    final wide = size.width >= kRailBreakpoint &&
        size.height >= railHeightNeeded(textScale);
    // The soft keyboard needs the room, and the bar is no use while typing.
    final typing = MediaQuery.viewInsetsOf(context).bottom > 0;
    final index = c.selected.index;
    final tabs = IndexedStack(
      index: index,
      children: [
        for (final tab in AppTab.values)
          _visited.contains(tab)
              // Tabs that are not showing stop their animations and cannot be reached with the keyboard or switch access.
              ? TickerMode(
                  enabled: tab == c.selected,
                  child: ExcludeFocus(
                    excluding: tab != c.selected,
                    child: _TabNavigator(
                    key: ValueKey(tab),
                    navigatorKey: c.navigators[tab]!,
                    push: widget.push,
                    stack: tab == AppTab.approvals ? c.approvalsObserver : null,
                    root: _root(tab),
                    reload: c.reloads[tab]!,
                    tab: tab,
                  ),
                  ),
                )
              : const SizedBox.shrink(),
      ],
    );
    // A build pointed at the mock Biatec OIDC says so on every screen.
    final header = [
      if (widget.config.isMock) _MockNotice(config: widget.config),
      if (widget.banner != null) widget.banner!,
    ];
    final body = header.isEmpty
        ? tabs
        : Column(children: [
            SafeArea(bottom: false, child: Column(children: header)),
            Expanded(
                child: MediaQuery.removePadding(
                    context: context, removeTop: true, child: tabs)),
          ]);
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
        bottomNavigationBar: wide || typing
            ? null
            : NavigationBar(
                // Labels grow with the text, so the bar does too.
                height: math.max(72, 56 + 18 * textScale),
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
class _TabNavigator extends StatefulWidget {
  const _TabNavigator({
    super.key,
    required this.navigatorKey,
    required this.root,
    required this.tab,
    required this.reload,
    this.push,
    this.stack,
  });

  final GlobalKey<NavigatorState> navigatorKey;
  final Widget root;

  /// Changes when the tab is shown after a while away; the first screen reloads its data then (see [TabReload]).
  final ValueListenable<int> reload;
  final AppTab tab;
  final PushController? push;

  /// Follows this tab's stack, for the Approvals tab.
  final NavigatorObserver? stack;

  @override
  State<_TabNavigator> createState() => _TabNavigatorState();
}

class _TabNavigatorState extends State<_TabNavigator> {
  /// Made once: a new observer on every rebuild would be detached and attached again, and miss what happened before.
  late final List<NavigatorObserver> _observers = [
    if (widget.push != null) _ForwardingObserver(widget.push!),
    if (widget.stack != null) widget.stack!,
  ];

  @override
  Widget build(BuildContext context) {
    return TabReload(
      reload: widget.reload,
      child: Navigator(
        key: widget.navigatorKey,
        observers: _observers,
        onGenerateRoute: (settings) => MaterialPageRoute<void>(
          settings: RouteSettings(name: '/${widget.tab.name}'),
          builder: (_) => widget.root,
        ),
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

  @override
  void didReplace({Route<dynamic>? newRoute, Route<dynamic>? oldRoute}) =>
      target.didReplace(newRoute: newRoute, oldRoute: oldRoute);
}

/// A strip that marks a build talking to the mock Biatec OIDC, never to a real server.
class _MockNotice extends StatelessWidget {
  const _MockNotice({required this.config});

  final AppConfig config;

  @override
  Widget build(BuildContext context) {
    final colors = context.statusColors;
    return Semantics(
      container: true,
      child: ColoredBox(
        color: colors.warning,
        child: Padding(
          padding: const EdgeInsets.symmetric(
              horizontal: AppSpacing.m, vertical: AppSpacing.xs),
          child: Row(children: [
            Icon(Icons.science_outlined, size: 16, color: colors.onWarning),
            const SizedBox(width: AppSpacing.s),
            Expanded(
              child: Text(
                'Mock Biatec OIDC \u00b7 ${config.oidcBaseUrl}',
                key: const Key('mock-notice'),
                maxLines: 1,
                overflow: TextOverflow.ellipsis,
                style: Theme.of(context)
                    .textTheme
                    .labelSmall
                    ?.copyWith(color: colors.onWarning),
              ),
            ),
          ]),
        ),
      ),
    );
  }
}
