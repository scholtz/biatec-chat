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
              Text(
                  'Message from ${request.client.displayName ?? request.client.entityId}',
                  style: theme.textTheme.labelMedium),
              const SizedBox(height: AppSpacing.xs),
              Text(message, key: const Key('binding-message')),
            ],
            const Divider(height: 24),
            Fact('Requested on', request.origin, key: const Key('fact-origin')),
          ],
        ),
      ),
    );
  }

  List<Widget> _transaction(ThemeData theme, TransactionDetail d) => [
        AmountHero(
          amount: '${d.amount} ${d.assetSymbol}',
          fiat: '${d.fiatAmount} ${d.fiatCurrency}',
        ),
        const SizedBox(height: AppSpacing.m),
        // Who receives it comes before the rest: it is what is easiest to get wrong.
        if (d.receiver != null)
          Fact('To', d.receiver!, mono: true, key: const Key('fact-receiver')),
        Fact('From', d.sender, mono: true, key: const Key('fact-sender')),
        Fact('Network', d.network, key: const Key('fact-network')),
        if (d.applicationId != null)
          Fact('Application', '${d.applicationId}',
              key: const Key('fact-application')),
        Fact('Asset',
            '${d.assetSymbol} (id ${d.assetId}, ${d.assetDecimals} decimals)',
            key: const Key('fact-asset')),
        Fact('Group hash', d.groupHash,
            mono: true, key: const Key('fact-hash')),
      ];

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

/// Who asks. A verified client shows its name and domain; an unverified one
/// only a warning and its fingerprint (rule 3).
class ClientBanner extends StatelessWidget {
  const ClientBanner({super.key, required this.client});

  final ConsentClient client;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    if (!client.verified) {
      return StatusBanner(
        key: const Key('client-unverified'),
        severity: StatusSeverity.warning,
        icon: Icons.warning_amber_rounded,
        title: 'Unverified application ${client.fingerprint ?? ''}'.trim(),
        message: 'Biatec cannot confirm who this is. Approve only if you '
            'started this action yourself.',
      );
    }
    return MergeSemantics(
      child: Row(key: const Key('client-verified'), children: [
        Icon(Icons.verified,
            color: theme.colorScheme.primary, semanticLabel: 'Verified'),
        const SizedBox(width: AppSpacing.s),
        Expanded(
          child:
              Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
            Text(client.displayName ?? client.entityId,
                style: theme.textTheme.titleMedium),
            if (client.domain != null) Text(client.domain!),
            Text('Verified application',
                style: theme.textTheme.labelSmall
                    ?.copyWith(color: theme.colorScheme.onSurfaceVariant)),
          ]),
        ),
      ]),
    );
  }
}
