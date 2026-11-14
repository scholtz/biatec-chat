import 'package:flutter/material.dart';

import '../theme.dart';
import '../ui/ui.dart';
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
  const ApprovalCard({super.key, required this.request});

  final ConsentRequest request;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    if (!request.isShowable) return const UnsupportedRequestCard();
    final detail = request.detail;
    final message = renderedBindingMessage(request);
    return Card(
      child: Padding(
        padding: const EdgeInsets.all(AppSpacing.m),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            ClientBanner(client: request.client),
            const SizedBox(height: AppSpacing.m),
            ...switch (detail) {
              TransactionDetail() => _transaction(theme, detail),
              LimitsChangeDetail() => _limits(theme, detail),
            },
            if (message.isNotEmpty) ...[
              const Divider(height: 24),
              Text('Message from ${request.client.domain}',
                  style: theme.textTheme.labelMedium),
              const SizedBox(height: AppSpacing.xs),
              Text(message, key: const Key('binding-message')),
            ],
            const Divider(height: 24),
            Fact('Origin, as reported by the application', _origin(),
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

  List<Widget> _transaction(ThemeData theme, TransactionDetail d) {
    final g = d.group!;
    return [
      // The total first, in the warning style when the value is not known.
      if (g.valueKnown)
        AmountHero(
          amount: d.headline,
          fiat: '${g.total}',
        )
      else ...[
        const StatusBanner(
          key: Key('value-unknown'),
          severity: StatusSeverity.warning,
          icon: Icons.warning_amber_rounded,
          title: 'Value unknown',
          message: 'Biatec could not work out what this is worth, or an '
              'application in it is not trusted. Approve only if you started '
              'this action yourself.',
        ),
      ],
      const SizedBox(height: AppSpacing.m),
      for (var i = 0; i < g.transfers.length; i++)
        _transfer(theme, i, g.transfers[i]),
      if (g.transfers.isEmpty)
        const Fact('Transfers', 'None', key: Key('fact-no-transfers')),
      for (var i = 0; i < g.applications.length; i++)
        _application(i, g.applications[i]),
      Fact('From', d.sender, mono: true, key: const Key('fact-sender')),
      Fact('Network', d.network, key: const Key('fact-network')),
      Fact('Group hash', d.groupHash, mono: true, key: const Key('fact-hash')),
    ];
  }

  Widget _transfer(ThemeData theme, int i, TransferEntry t) => Padding(
        padding: const EdgeInsets.only(bottom: AppSpacing.s),
        child: Column(
            key: Key('transfer-$i'),
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Text('${t.amount} ${t.asset.symbol}',
                  key: Key('transfer-$i-amount'),
                  style: theme.textTheme.titleMedium
                      ?.copyWith(fontWeight: FontWeight.w700)),
              Text(t.fiat == null ? 'Value unknown' : '${t.fiat}',
                  key: Key('transfer-$i-fiat')),
              Fact('To', t.receiver,
                  mono: true, key: Key('transfer-$i-receiver')),
              Text('Asset ${t.asset.symbol} (id ${t.asset.id}, '
                  '${t.asset.decimals} decimals)'),
            ]),
      );

  Widget _application(int i, ApplicationEntry a) => a.trusted
      ? Fact('Calls application', a.id, key: Key('application-$i'))
      : StatusBanner(
          key: Key('application-$i'),
          severity: StatusSeverity.warning,
          icon: Icons.warning_amber_rounded,
          title: 'Calls application ${a.id}',
          message: 'You have not trusted this application for this '
              'service. It can do more than the transfers listed.',
        );

  List<Widget> _limits(ThemeData theme, LimitsChangeDetail d) => [
        Semantics(
          header: true,
          child: Text('Change spending limits',
              style: theme.textTheme.headlineSmall
                  ?.copyWith(fontWeight: FontWeight.w700)),
        ),
        const SizedBox(height: AppSpacing.s),
        _limitsWarning(d),
        const SizedBox(height: AppSpacing.s),
        Fact('Per transaction',
            '${d.current.perTransactionEur} EUR → ${d.requested.perTransactionEur} EUR',
            key: const Key('fact-per-transaction')),
        Fact('Per day',
            '${d.current.perDayEur} EUR → ${d.requested.perDayEur} EUR',
            key: const Key('fact-per-day')),
        Fact('Request hash', d.requestHash,
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

  Widget _limitsWarning(LimitsChangeDetail d) => switch (limitsMove(d)) {
        LimitsMove.raises => const StatusBanner(
            key: Key('limits-raised'),
            severity: StatusSeverity.warning,
            title: 'This raises your limits',
            message: 'More can be moved without asking you again. Approve '
                'only if you asked for this.',
          ),
        LimitsMove.lowers => const StatusBanner(
            key: Key('limits-lowered'),
            severity: StatusSeverity.info,
            title: 'This lowers your limits',
            message: 'Less can be moved without asking you.',
          ),
        LimitsMove.same => const StatusBanner(
            key: Key('limits-same'),
            severity: StatusSeverity.info,
            title: 'These limits stay the same',
            message: 'The requested limits equal the current ones.',
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
  Widget build(BuildContext context) => const Card(
        child: Padding(
          padding: EdgeInsets.all(AppSpacing.m),
          child: StatusBanner(
            key: Key('request-unsupported'),
            severity: StatusSeverity.danger,
            icon: Icons.block,
            title: 'This request cannot be shown; update the app',
            message: 'It cannot be approved here. Nothing was approved.',
          ),
        ),
      );
}

/// Who asks. Only a proven domain says who it is; otherwise "Unproven
/// application" and the fingerprint. Never a name (rule 3).
class ClientBanner extends StatelessWidget {
  const ClientBanner({super.key, required this.client});

  final ConsentClient client;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final fingerprint = client.fingerprintShown ?? '';
    if (!client.domainProven) {
      return StatusBanner(
        key: const Key('client-unverified'),
        severity: StatusSeverity.warning,
        icon: Icons.warning_amber_rounded,
        title: 'Unproven application $fingerprint'.trim(),
        message: 'Biatec cannot confirm who this is. Approve only if you '
            'started this action yourself.',
      );
    }
    return MergeSemantics(
      child: Row(key: const Key('client-verified'), children: [
        Icon(Icons.verified,
            color: theme.colorScheme.primary, semanticLabel: 'Proven domain'),
        const SizedBox(width: AppSpacing.s),
        Expanded(
          child:
              Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
            Text(client.domain!, style: theme.textTheme.titleMedium),
            Text('Proven domain, application $fingerprint'.trim(),
                style: theme.textTheme.labelSmall
                    ?.copyWith(color: theme.colorScheme.onSurfaceVariant)),
          ]),
        ),
      ]),
    );
  }
}
