import 'dart:async';

import 'package:material_ui/material_ui.dart';

import '../approvals/cards/relying_party_card.dart' show WrittenByServiceBox;
import '../approvals/widgets/approval_actions.dart';
import '../approvals/widgets/binding_code_field.dart';
import '../approvals/widgets/expiry.dart' show clockLeft;
import '../approvals/widgets/result_view.dart';
import '../clients/service_identity.dart';
import '../theme.dart';
import '../ui/ui.dart';
import 'link_code.dart';
import 'link_controller.dart';
import 'link_services.dart';
import '../text/rp_strings.dart';

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
    final s = RpStrings.of(Localizations.maybeLocaleOf(context));
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
        !c.uncertain &&
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
          ..showSnackBar(SnackBar(
              key: const Key('touch-obscured'),
              content: Text(RpStrings.of(Localizations.maybeLocaleOf(context))
                  .t('touchObscured'))));
        return;
      }
      await c.approve(_code.text, confirmedUnproven: confirmed);
    } finally {
      _acting = false;
    }
  }

  Future<bool> _confirmUnproven() async {
    final s = RpStrings.of(Localizations.maybeLocaleOf(context));
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
    final s = RpStrings.of(Localizations.maybeLocaleOf(context));
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
          final s = RpStrings.of(Localizations.maybeLocaleOf(context));
          return ApprovalActionBar(
            busy: c.phase == LinkPhase.working,
            approveLabel: s.t('linkButton'),
            denyLabel: s.t('dontLink'),
            approveIcon: Icons.link,
            approveKey: const Key('link'),
            denyKey: const Key('dont-link'),
            onApprove: can ? () => _link(c) : null,
            onDeny: can ? () => c.deny() : null,
          );
        },
      ),
    );
  }

  Widget _body(BuildContext context) {
    final c = _controller;
    final s = RpStrings.of(Localizations.maybeLocaleOf(context));
    switch (c.phase) {
      case LinkPhase.loading:
        return const LoadingSkeleton(key: Key('link-loading'), rows: 4);
      case LinkPhase.unavailable:
        return ErrorState(
          key: const Key('link-unavailable'),
          offline: c.offline,
          title: s.t(c.offline ? 'offlineTitle' : 'errorTitle'),
          message: c.offline
              ? s.t('offline')
              : c.unavailableReason ?? s.t('goneBody'),
          retryLabel: s.t('tryAgain'),
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
      case LinkPhase.undetermined:
        return ResultView(
            icon: Icons.help_outline,
            title: s.t('undeterminedTitle'),
            message: s.t('undeterminedBody'),
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
    final s = RpStrings.of(Localizations.maybeLocaleOf(context));
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
              message: c.error!,
              // After a lost answer: ask the server instead of a dead end.
              actionLabel: c.uncertain ? s.t('checkAgain') : null,
              onAction: c.uncertain ? c.recheck : null),
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
