import 'package:material_ui/material_ui.dart';

import '../clients/service_identity.dart';
import '../clients/trust_strings.dart';
import '../text/rp_strings.dart';
import '../theme.dart';
import '../ui/ui.dart';
import 'approval_texts.dart';
import 'cards/relying_party_card.dart';
import 'consent_request.dart';
import 'receipt.dart';
import 'widgets/amount_hero.dart';
import 'widgets/fact_row.dart';

/// The approval card: every fact comes from the fetched authorization
/// details and client object, nothing from elsewhere (rule 1). Long values
/// such as addresses and hashes are shown in full.
///
/// The amount is the hero, then who it goes to and who asks; the details follow. A warning is always an icon and words.
class ApprovalCard extends StatelessWidget {
  const ApprovalCard({
    super.key,
    required this.request,
    this.trust = false,
    this.remembered = const {},
    this.onRemember,
  });

  final ConsentRequest request;

  /// The user-held trust settings are on (`AppConfig.trustSettings`): the card says when approving links a new service and offers "don't ask again" per untrusted application. Off, it shows
  /// neither, and the receipt never carries `remember`.
  final bool trust;

  /// The application ids the user ticked.
  final Set<String> remembered;

  /// A box was ticked or cleared.
  final void Function(String id, bool on)? onRemember;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final locale = Localizations.maybeLocaleOf(context);
    final t = ApprovalTexts.of(locale);
    final trustTexts = TrustStrings.of(locale);
    if (!request.isShowable) return const UnsupportedRequestCard();
    final detail = request.detail;
    // A payment, login or action of a relying party (protocol 1.2) has a card of its own.
    if (detail is RelyingPartyDetail) return RelyingPartyCard(request: request);
    final message = renderedBindingMessage(request);
    return Card(
      child: Padding(
        padding: const EdgeInsets.all(AppSpacing.m),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            ClientBanner(client: request.client),
            if (trust && !request.client.linked) ...[
              const SizedBox(height: AppSpacing.s),
              StatusBanner(
                key: const Key('new-service'),
                severity: StatusSeverity.info,
                icon: Icons.add_link,
                title: trustTexts.t('newServiceTitle'),
                message: trustTexts.t('newServiceBody'),
              ),
            ],
            const SizedBox(height: AppSpacing.m),
            ...switch (detail) {
              TransactionDetail() =>
                _transaction(theme, detail, t, trustTexts, locale),
              LimitsChangeDetail() => _limits(theme, detail, t),
              // Never reached: such a request is not showable (above) or has its own card.
              RelyingPartyDetail() || UnsupportedDetail() => const <Widget>[],
            },
            if (message.isNotEmpty) ...[
              const Divider(height: 24),
              Text(t.text('messageFrom', {'domain': request.client.domain}),
                  style: theme.textTheme.labelMedium),
              const SizedBox(height: AppSpacing.xs),
              Text(message, key: const Key('binding-message')),
            ],
            const Divider(height: 24),
            Fact(t.text('originByApplication'), _origin(),
                key: const Key('fact-origin')),
          ],
        ),
      ),
    );
  }

  /// The origin the client reported; it says nothing about who the client is.
  String _origin() {
    final d = request.detail;
    if (d is TransactionDetail && d.group != null) return d.group!.origin;
    return request.origin;
  }

  List<Widget> _transaction(ThemeData theme, TransactionDetail d,
      ApprovalTexts t, TrustStrings trustTexts, Locale? locale) {
    final g = d.group!;
    return [
      // The total first, in the warning style when the value is not known.
      if (g.valueKnown)
        AmountHero(
          amount: d.headlineIn(locale),
          fiat: '${g.total}',
        )
      else ...[
        StatusBanner(
          key: const Key('value-unknown'),
          severity: StatusSeverity.warning,
          icon: Icons.warning_amber_rounded,
          title: t.text('valueUnknown'),
          message: t.text('valueUnknownBody'),
        ),
      ],
      const SizedBox(height: AppSpacing.m),
      for (var i = 0; i < g.transfers.length; i++)
        _transfer(theme, i, g.transfers[i], t),
      if (g.transfers.isEmpty)
        Fact(t.text('transfers'), t.text('none'),
            key: const Key('fact-no-transfers')),
      for (var i = 0; i < g.applications.length; i++)
        ..._applicationRow(i, g.applications, t, trustTexts),
      if (trust && request.rememberableTruncated)
        Padding(
          key: const Key('remember-truncated'),
          padding: const EdgeInsets.only(bottom: AppSpacing.s),
          child: Text(trustTexts.t('moreApplications',
              {'max': '${ConsentRequest.maxRemembered}'})),
        ),
      Fact(t.text('from'), d.sender, mono: true, key: const Key('fact-sender')),
      Fact(t.text('network'), d.network, key: const Key('fact-network')),
      Fact(t.text('groupHash'), d.groupHash,
          mono: true, key: const Key('fact-hash')),
    ];
  }

  Widget _transfer(ThemeData theme, int i, TransferEntry e, ApprovalTexts t) =>
      Padding(
        padding: const EdgeInsets.only(bottom: AppSpacing.s),
        child: Column(
            key: Key('transfer-$i'),
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Text('${e.amount} ${e.asset.symbol}',
                  key: Key('transfer-$i-amount'),
                  style: theme.textTheme.titleMedium
                      ?.copyWith(fontWeight: FontWeight.w700)),
              Text(e.fiat == null ? t.text('valueUnknown') : '${e.fiat}',
                  key: Key('transfer-$i-fiat')),
              Fact(t.text('to'), e.receiver,
                  mono: true, key: Key('transfer-$i-receiver')),
              Text(t.text('assetLine', {
                'symbol': e.asset.symbol,
                'id': e.asset.id,
                'decimals': e.asset.decimals,
              })),
            ]),
      );

  /// The application, and for an untrusted one that can be remembered the box "don't ask again" (unticked: the user decides, nothing is ticked for them).
  List<Widget> _applicationRow(int i, List<ApplicationEntry> all,
      ApprovalTexts t, TrustStrings trustTexts) {
    final a = all[i];
    final offered = trust && request.canRemember(a);
    return [
      _application(i, a, t),
      if (offered) _rememberBox(a, trustTexts),
    ];
  }

  Widget _rememberBox(ApplicationEntry a, TrustStrings trustTexts) {
    final c = request.client;
    final who = c.domainProven && c.domain != null
        ? c.domain!
        : formatFingerprint(c.fingerprint);
    return CheckboxListTile(
      key: Key('remember-${a.id}'),
      contentPadding: EdgeInsets.zero,
      controlAffinity: ListTileControlAffinity.leading,
      value: remembered.contains(a.id),
      onChanged:
          onRemember == null ? null : (v) => onRemember!(a.id, v ?? false),
      title: Text(trustTexts.t('dontAskAgain', {'id': a.id, 'who': who})),
    );
  }

  Widget _application(int i, ApplicationEntry a, ApprovalTexts t) => a.trusted
      ? Fact(t.text('callsApplication'), a.id, key: Key('application-$i'))
      : StatusBanner(
          key: Key('application-$i'),
          severity: StatusSeverity.warning,
          icon: Icons.warning_amber_rounded,
          title: t.text('callsApplicationId', {'id': a.id}),
          message: t.text('untrustedApplication'),
        );

  List<Widget> _limits(ThemeData theme, LimitsChangeDetail d, ApprovalTexts t) =>
      [
        Semantics(
          header: true,
          child: Text(t.text('changeLimits'),
              style: theme.textTheme.headlineSmall
                  ?.copyWith(fontWeight: FontWeight.w700)),
        ),
        const SizedBox(height: AppSpacing.s),
        _limitsWarning(d, t),
        const SizedBox(height: AppSpacing.s),
        Fact(t.text('perTransaction'),
            '${d.current.perTransactionEur} EUR → ${d.requested.perTransactionEur} EUR',
            key: const Key('fact-per-transaction')),
        Fact(t.text('perDay'),
            '${d.current.perDayEur} EUR → ${d.requested.perDayEur} EUR',
            key: const Key('fact-per-day')),
        Fact(t.text('requestHash'), d.requestHash,
            mono: true, key: const Key('fact-hash')),
      ];

  /// Which way the limits move. A value that cannot be read as a number counts as raising, the careful way.
  static LimitsMove limitsMove(LimitsChangeDetail d) {
    var up = false;
    var down = false;
    void compare(String now, String next) {
      final a = double.tryParse(now);
      final b = double.tryParse(next);
      if (a == null || b == null || b > a) {
        up = true;
      } else if (b < a) {
        down = true;
      }
    }

    compare(d.current.perTransactionEur, d.requested.perTransactionEur);
    compare(d.current.perDayEur, d.requested.perDayEur);
    if (up) return LimitsMove.raises;
    return down ? LimitsMove.lowers : LimitsMove.same;
  }

  Widget _limitsWarning(LimitsChangeDetail d, ApprovalTexts t) =>
      switch (limitsMove(d)) {
        LimitsMove.raises => StatusBanner(
            key: const Key('limits-raised'),
            severity: StatusSeverity.warning,
            title: t.text('limitsRaisedTitle'),
            message: t.text('limitsRaisedBody'),
          ),
        LimitsMove.lowers => StatusBanner(
            key: const Key('limits-lowered'),
            severity: StatusSeverity.info,
            title: t.text('limitsLoweredTitle'),
            message: t.text('limitsLoweredBody'),
          ),
        LimitsMove.same => StatusBanner(
            key: const Key('limits-same'),
            severity: StatusSeverity.info,
            title: t.text('limitsSameTitle'),
            message: t.text('limitsSameBody'),
          ),
      };
}

/// The way a limits change moves the limits.
enum LimitsMove { raises, lowers, same }

/// The card for a request this app cannot show (protocol 1.0 shape): nothing
/// of it is rendered and it cannot be approved.
class UnsupportedRequestCard extends StatelessWidget {
  const UnsupportedRequestCard({super.key});

  @override
  Widget build(BuildContext context) {
    final s = RpStrings.of(Localizations.maybeLocaleOf(context));
    return Card(
      child: Padding(
        padding: const EdgeInsets.all(AppSpacing.m),
        child: StatusBanner(
          key: const Key('request-unsupported'),
          severity: StatusSeverity.danger,
          icon: Icons.block,
          title: s.t('cannotShow'),
          message: s.t('cannotShowBody'),
        ),
      ),
    );
  }
}

/// Who asks: the one service-identity widget (the proven domain, or "Unproven application" and the fingerprint; never a name).
class ClientBanner extends StatelessWidget {
  const ClientBanner({super.key, required this.client});

  final ConsentClient client;

  @override
  Widget build(BuildContext context) => ServiceIdentity.client(client);
}
