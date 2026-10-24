import 'package:flutter/material.dart';

/// A money amount that a screen reader speaks in full ("12.50 ALGO"), however the visual is split or styled. The text is shown as given: nothing is rounded or reformatted, because what the user sees must equal what they sign.
class AmountText extends StatelessWidget {
  const AmountText({
    super.key,
    required this.amount,
    this.unit,
    this.style,
    this.large = false,
  });

  final String amount;

  /// Currency or asset symbol, for example `ALGO` or `EUR`.
  final String? unit;
  final TextStyle? style;

  /// Hero size, for the amount on an approval card.
  final bool large;

  /// What a screen reader says.
  String get spoken => unit == null ? amount : '$amount $unit';

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final base =
        (large ? theme.textTheme.displaySmall : theme.textTheme.titleLarge)
            ?.copyWith(fontWeight: FontWeight.w700);
    return Semantics(
      label: spoken,
      child: ExcludeSemantics(
        child: Text.rich(
          TextSpan(children: [
            TextSpan(text: amount, style: base?.merge(style)),
            if (unit != null)
              TextSpan(
                text: ' $unit',
                style: (large
                        ? theme.textTheme.titleLarge
                        : theme.textTheme.titleMedium)
                    ?.copyWith(color: theme.colorScheme.onSurfaceVariant),
              ),
          ]),
        ),
      ),
    );
  }
}
