import 'dart:async';

import 'package:flutter/material.dart';

import '../approvals/cards/relying_party_card.dart' show WrittenByServiceBox;
import '../approvals/widgets/binding_code_field.dart';
import '../approvals/widgets/expiry.dart' show clockLeft;
import '../approvals/widgets/result_view.dart';
import '../clients/service_identity.dart';
import '../theme.dart';
import '../ui/ui.dart';
import 'link_code.dart';
import 'link_controller.dart';
import 'link_services.dart';
import 'rp_strings.dart';

/// Opens the link screen for [raw], the text of a scanned or pasted code or of a deep link another app opened the app with. Every way in comes through here, so they behave the same: the code is parsed
/// strictly, a code that is refused is explained on a screen of its own, and nothing is ever linked without the link screen (the user types the code the service shows and presses Link).
Future<void> openLinkCode(
  BuildContext context,
  LinkServices services,
  String raw, {
  VoidCallback? onLinked,
  bool rootNavigator = false,
}) {
  final navigator = Navigator.of(context, rootNavigator: rootNavigator);
  final LinkCode code;
  try {
    code = services.parse(raw.trim());
  } on LinkCodeException catch (e) {
    return navigator.push(LinkCodeProblemScreen.route(e.problem));
  }
  return navigator.push(LinkScreen.route(services, code, onLinked: onLinked));
}

/// What a refused code says: that it is not a link code, or that it is for another authenticator service. Nothing was sent anywhere.
class LinkCodeProblemScreen extends StatelessWidget {
  const LinkCodeProblemScreen({super.key, required this.problem});

  final LinkCodeProblem problem;

  static Route<void> route(LinkCodeProblem problem) => MaterialPageRoute<void>(
        settings: const RouteSettings(name: '/link/refused'),
        builder: (_) => LinkCodeProblemScreen(problem: problem),
      );

  @override
  Widget build(BuildContext context) {
    final s = RpStrings.of();
    return Scaffold(
      appBar: AppBar(title: Text(s.t('linkTitle'))),
      body: ResultView(
        icon: Icons.link_off,
        title: s.t('codeProblemTitle'),
        message: s.t(problem == LinkCodeProblem.differentService
            ? 'differentService'
            : 'notALinkCode'),
        actionLabel: s.t('done'),
        onAction: () => Navigator.of(context).maybePop(),
      ),
    );
  }
}

/// A service asks to be linked: who it is (the one service-identity widget), what it calls the user (as written by the service), what linking means, and the code the user types. **Link** signs the
/// statement with the approval key (one biometric prompt); **Don't link** signs a deny with the presence key. A service that has not proven its domain needs a second confirmation.
class LinkScreen extends StatefulWidget {
  const LinkScreen({super.key, required this.services, required this.code, this.onLinked});

  final LinkServices services;
  final LinkCode code;
  final VoidCallback? onLinked;

  static Route<void> route(LinkServices services, LinkCode code,
          {VoidCallback? onLinked}) =>
      MaterialPageRoute<void>(
        settings: const RouteSettings(name: '/link'),
        builder: (_) =>
            LinkScreen(services: services, code: code, onLinked: onLinked),
      );

  @override
  State<LinkScreen> createState() => _LinkScreenState();
}

class _LinkScreenState extends State<LinkScreen> {
  late final LinkController _controller;
  final _code = TextEditingController();
  Timer? _ticker;
  bool _acting = false;

  @override
  void initState() {
    super.initState();
    _controller = widget.services.controllerFor(widget.code,
        onLinked: widget.onLinked)
      ..load();
  }

  @override
  void didChangeDependencies() {
    super.didChangeDependencies();
    if (TickerMode.valuesOf(context).enabled) {
      _ticker ??= Timer.periodic(const Duration(seconds: 1), (_) {
        if (mounted) setState(() {});
      });
    } else {
      _ticker?.cancel();
      _ticker = null;
    }
  }

  @override
  void dispose() {
    _ticker?.cancel();
    _code.dispose();
    _controller.dispose();
    super.dispose();
  }

  void _close() => Navigator.of(context).maybePop();

  bool _canAct(LinkController c) {
    final r = c.request;
    return r != null &&
        c.phase == LinkPhase.ready &&
        !c.offline &&
        !r.isExpired(c.now);
  }

  Future<void> _link(LinkController c) async {
    if (_acting) return;
    _acting = true;
    try {
      final r = c.request!;
      var confirmed = false;
      // A service that has not proven who it is: a second, deliberate confirmation before anything is signed.
      if (r.domain == null) {
        confirmed = await _confirmUnproven();
        if (!confirmed || !mounted) return;
      }
      // Another app's window drawn over part of the screen at the touch (Android 10 and 11): nothing is sent.
      final obscured = await widget.services.touchGuard.partiallyObscured();
      if (!mounted || !_canAct(c)) return;
      if (obscured) {
        ScaffoldMessenger.of(context)
          ..hideCurrentSnackBar()
          ..showSnackBar(const SnackBar(
              key: Key('touch-obscured'),
              content: Text('Another app is drawing over this screen, so '
                  'nothing was sent. Close it and tap Link again.')));
        return;
      }
      await c.approve(_code.text, confirmedUnproven: confirmed);
    } finally {
      _acting = false;
    }
  }

  Future<bool> _confirmUnproven() async {
    final s = RpStrings.of();
    final yes = await showDialog<bool>(
      context: context,
      builder: (context) => AlertDialog(
        key: const Key('unproven-confirm'),
        icon: const Icon(Icons.warning_amber_rounded),
        title: Text(s.t('unprovenTitle')),
        content: Text(s.t('unprovenBody')),
        actions: [
          TextButton(
              key: const Key('unproven-cancel'),
              onPressed: () => Navigator.of(context).pop(false),
              child: Text(s.t('cancel'))),
          FilledButton(
              key: const Key('unproven-link'),
              onPressed: () => Navigator.of(context).pop(true),
              child: Text(s.t('linkAnyway'))),
        ],
      ),
    );
    return yes ?? false;
  }

  @override
  Widget build(BuildContext context) {
    final s = RpStrings.of();
    return Scaffold(
      appBar: AppBar(title: Text(s.t('linkTitle'))),
      body: ListenableBuilder(
        listenable: _controller,
        builder: (context, _) => _body(context),
      ),
      bottomNavigationBar: ListenableBuilder(
        listenable: _controller,
        builder: (context, _) {
          final c = _controller;
          if (c.phase != LinkPhase.ready && c.phase != LinkPhase.working) {
            return const SizedBox.shrink();
          }
          final can = _canAct(c);
          return _LinkActionBar(
            busy: c.phase == LinkPhase.working,
            onLink: can ? () => _link(c) : null,
            onDontLink: can ? () => c.deny(_code.text) : null,
          );
        },
      ),
    );
  }

  Widget _body(BuildContext context) {
    final c = _controller;
    final s = RpStrings.of();
    switch (c.phase) {
      case LinkPhase.loading:
        return const LoadingSkeleton(key: Key('link-loading'), rows: 4);
      case LinkPhase.unavailable:
        return ErrorState(
          key: const Key('link-unavailable'),
          offline: c.offline,
          message: c.offline
              ? 'Offline. Linking needs a connection to Biatec OIDC.'
              : c.unavailableReason ?? s.t('goneBody'),
          onRetry: c.load,
        );
      case LinkPhase.linked:
        return ResultView(
            icon: Icons.check_circle,
            title: s.t('linkedTitle'),
            message: s.t('linkedBody'),
            actionLabel: s.t('done'),
            onAction: _close);
      case LinkPhase.denied:
        return ResultView(
            icon: Icons.block,
            title: s.t('deniedTitle'),
            message: s.t('deniedBody'),
            actionLabel: s.t('done'),
            onAction: _close);
      case LinkPhase.closed:
        return ResultView(
            icon: Icons.gpp_bad,
            title: s.t('closedTitle'),
            message: s.t('closedBody'),
            actionLabel: s.t('done'),
            onAction: _close);
      case LinkPhase.gone:
        return ResultView(
            icon: Icons.timer_off,
            title: s.t('goneTitle'),
            message: s.t('goneBody'),
            actionLabel: s.t('done'),
            onAction: _close);
      case LinkPhase.ready:
      case LinkPhase.working:
        return _ready(context);
    }
  }

  Widget _ready(BuildContext context) {
    final c = _controller;
    final s = RpStrings.of();
    final theme = Theme.of(context);
    final request = c.request!;
    final left = request.expiresAtTime.difference(c.now);
    final busy = c.phase == LinkPhase.working;
    return ListView(
      padding: const EdgeInsets.all(AppSpacing.m),
      keyboardDismissBehavior: ScrollViewKeyboardDismissBehavior.onDrag,
      children: [
        if (c.error != null) ...[
          StatusBanner(
              key: const Key('link-error'),
              severity: StatusSeverity.danger,
              title: s.t('linkTitle'),
              message: c.error!),
          const SizedBox(height: AppSpacing.m),
        ],
        Card(
          child: Padding(
            padding: const EdgeInsets.all(AppSpacing.m),
            child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  ServiceIdentity.client(request.client),
                  if (request.userLabel != null) ...[
                    const SizedBox(height: AppSpacing.m),
                    // What the service calls this user: its own words, in the area that says so.
                    WrittenByServiceBox(children: [
                      Text(s.t('serviceCallsYou', {'label': request.userLabel!}),
                          key: const Key('link-user-label')),
                    ]),
                  ],
                  const SizedBox(height: AppSpacing.m),
                  Text(s.t('linkWhat'), key: const Key('link-what')),
                ]),
          ),
        ),
        const SizedBox(height: AppSpacing.m),
        Row(children: [
          const ExcludeSemantics(child: Icon(Icons.timer_outlined, size: 20)),
          const SizedBox(width: AppSpacing.s),
          Text(left <= Duration.zero ? s.t('expiredTitle') : clockLeft(left),
              key: const Key('link-expiry'),
              style: theme.textTheme.titleMedium),
        ]),
        const SizedBox(height: AppSpacing.m),
        BindingCodeField(
          controller: _code,
          enabled: !busy,
          attemptsRemaining: c.attemptsRemaining,
        ),
        const SizedBox(height: AppSpacing.l),
      ],
    );
  }
}

/// Link and Don't link in a bar that stays at the bottom of the screen.
class _LinkActionBar extends StatelessWidget {
  const _LinkActionBar(
      {required this.onLink, required this.onDontLink, required this.busy});

  final VoidCallback? onLink;
  final VoidCallback? onDontLink;
  final bool busy;

  @override
  Widget build(BuildContext context) {
    final s = RpStrings.of();
    final scheme = Theme.of(context).colorScheme;
    final stacked = MediaQuery.textScalerOf(context).scale(1) >= 1.5 ||
        MediaQuery.sizeOf(context).width < 360;
    final link = FilledButton.icon(
      key: const Key('link'),
      onPressed: onLink,
      icon: const Icon(Icons.link),
      label: Text(s.t('linkButton')),
    );
    final dont = OutlinedButton.icon(
      key: const Key('dont-link'),
      onPressed: onDontLink,
      icon: const Icon(Icons.close),
      label: Text(s.t('dontLink')),
    );
    return Material(
      elevation: 3,
      color: scheme.surfaceContainer,
      child: SafeArea(
        top: false,
        child: Padding(
          padding: const EdgeInsets.all(AppSpacing.m),
          child: Column(mainAxisSize: MainAxisSize.min, children: [
            if (busy) ...[
              Semantics(
                  label: 'Working', child: const LinearProgressIndicator()),
              const SizedBox(height: AppSpacing.s),
            ],
            if (stacked) ...[
              SizedBox(width: double.infinity, child: link),
              const SizedBox(height: AppSpacing.s),
              SizedBox(width: double.infinity, child: dont),
            ] else
              Row(children: [
                Expanded(child: dont),
                const SizedBox(width: AppSpacing.m),
                Expanded(child: link),
              ]),
          ]),
        ),
      ),
    );
  }
}
