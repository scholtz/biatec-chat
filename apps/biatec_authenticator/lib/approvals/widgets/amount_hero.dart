import 'package:flutter/material.dart';

import '../../theme.dart';

/// What the user is approving, large: the amount and its value in the user's currency. The two texts are exactly what the authorization details say. A screen reader hears one sentence with both, in full.
class AmountHero extends StatelessWidget {
  const AmountHero({
    super.key,
    required this.amount,
    required this.fiat,
    this.amountKey = const Key('fact-amount'),
    this.fiatKey = const Key('fact-fiat'),
  });

  /// For example `5000.000000 USDC`.
  final String amount;

  /// For example `4500.00 EUR`.
  final String fiat;
  final Key amountKey;
  final Key fiatKey;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return Semantics(
      container: true,
      label: 'You are approving $amount, worth $fiat',
      child: ExcludeSemantics(
        child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
          Text('You are approving',
              style: theme.textTheme.labelLarge
                  ?.copyWith(color: theme.colorScheme.onSurfaceVariant)),
          const SizedBox(height: AppSpacing.xs),
          Text(amount,
              key: amountKey,
              style: theme.textTheme.headlineMedium
                  ?.copyWith(fontWeight: FontWeight.w700)),
          Text(fiat, key: fiatKey, style: theme.textTheme.titleMedium),
        ]),
      ),
    );
  }
}
