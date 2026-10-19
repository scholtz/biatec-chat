import 'package:flutter/material.dart';

import 'consent_request.dart';
import 'receipt.dart';

/// The approval card: every fact comes from the fetched authorization
/// details and client object, nothing from elsewhere (rule 1). Long values
/// such as addresses and hashes are shown in full.
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
        padding: const EdgeInsets.all(16),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            ClientBanner(client: request.client),
            const SizedBox(height: 16),
            ...switch (detail) {
              TransactionDetail() => _transaction(theme, detail),
              LimitsChangeDetail() => _limits(theme, detail),
            },
            if (message.isNotEmpty) ...[
              const Divider(height: 24),
              Text(
                  'Message from ${request.client.displayName ?? request.client.entityId}',
                  style: theme.textTheme.labelMedium),
              const SizedBox(height: 4),
              Text(message, key: const Key('binding-message')),
            ],
            const Divider(height: 24),
            _Fact('Requested on', request.origin,
                key: const Key('fact-origin')),
          ],
        ),
      ),
    );
  }

  List<Widget> _transaction(ThemeData theme, TransactionDetail d) => [
        Text('${d.amount} ${d.assetSymbol}',
            key: const Key('fact-amount'),
            style: theme.textTheme.headlineSmall),
        Text('${d.fiatAmount} ${d.fiatCurrency}',
            key: const Key('fact-fiat'), style: theme.textTheme.titleMedium),
        const SizedBox(height: 12),
        _Fact('Network', d.network, key: const Key('fact-network')),
        _Fact('From', d.sender, key: const Key('fact-sender')),
        if (d.receiver != null)
          _Fact('To', d.receiver!, key: const Key('fact-receiver')),
        if (d.applicationId != null)
          _Fact('Application', '${d.applicationId}',
              key: const Key('fact-application')),
        _Fact('Asset',
            '${d.assetSymbol} (id ${d.assetId}, ${d.assetDecimals} decimals)',
            key: const Key('fact-asset')),
        _Fact('Group hash', d.groupHash, key: const Key('fact-hash')),
      ];

  List<Widget> _limits(ThemeData theme, LimitsChangeDetail d) => [
        Text('Change spending limits', style: theme.textTheme.headlineSmall),
        const SizedBox(height: 12),
        _Fact('Per transaction',
            '${d.current.perTransactionEur} EUR → ${d.requested.perTransactionEur} EUR',
            key: const Key('fact-per-transaction')),
        _Fact('Per day',
            '${d.current.perDayEur} EUR → ${d.requested.perDayEur} EUR',
            key: const Key('fact-per-day')),
        _Fact('Request hash', d.requestHash, key: const Key('fact-hash')),
      ];
}

/// Who asks. A verified client shows its name and domain; an unverified one
/// only a warning and its fingerprint (rule 3).
class ClientBanner extends StatelessWidget {
  const ClientBanner({super.key, required this.client});

  final ConsentClient client;

  @override
  Widget build(BuildContext context) {
    final scheme = Theme.of(context).colorScheme;
    if (!client.verified) {
      return Container(
        key: const Key('client-unverified'),
        padding: const EdgeInsets.all(12),
        color: scheme.errorContainer,
        child: Row(children: [
          Icon(Icons.warning_amber, color: scheme.onErrorContainer),
          const SizedBox(width: 8),
          Expanded(
            child: Text(
              'Unverified application ${client.fingerprint ?? ''}'.trim(),
              style: TextStyle(color: scheme.onErrorContainer),
            ),
          ),
        ]),
      );
    }
    return Row(key: const Key('client-verified'), children: [
      Icon(Icons.verified, color: scheme.primary),
      const SizedBox(width: 8),
      Expanded(
        child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
          Text(client.displayName ?? client.entityId,
              style: Theme.of(context).textTheme.titleMedium),
          if (client.domain != null) Text(client.domain!),
        ]),
      ),
    ]);
  }
}

class _Fact extends StatelessWidget {
  const _Fact(this.label, this.value, {super.key});

  final String label;
  final String value;

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.symmetric(vertical: 4),
      child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
        Text(label, style: Theme.of(context).textTheme.labelMedium),
        SelectableText(value),
      ]),
    );
  }
}
