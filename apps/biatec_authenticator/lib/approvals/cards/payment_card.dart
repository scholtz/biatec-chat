import 'package:material_ui/material_ui.dart';

import '../../text/rp_strings.dart';
import '../../theme.dart';
import '../consent_request.dart';
import '../widgets/fact_row.dart';
import 'relying_party_card.dart';

/// `biatec_payment`: the amount and currency in the largest type, then the payee and the account (an IBAN in groups of four, or another identifier with its scheme) and the reference, in the area
/// that says the service wrote them, and the execution date when there is one. Biatec executes nothing: the service (a bank) does, after it has the user's signed approval.
class PaymentCardBody extends StatelessWidget {
  const PaymentCardBody({super.key, required this.detail});

  final PaymentDetail detail;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final s = RpStrings.of(Localizations.maybeLocaleOf(context));
    final amount = '${detail.amount} ${detail.currency}';
    return Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
      Text(s.t('paymentHeading'),
          style: theme.textTheme.labelLarge
              ?.copyWith(color: theme.colorScheme.onSurfaceVariant)),
      const SizedBox(height: AppSpacing.xs),
      Semantics(
        container: true,
        label: '${s.t('paymentHeading')}: $amount',
        child: ExcludeSemantics(
          child: Text(amount,
              key: const Key('payment-amount'),
              style: theme.textTheme.displaySmall
                  ?.copyWith(fontWeight: FontWeight.w800)),
        ),
      ),
      const SizedBox(height: AppSpacing.m),
      WrittenByServiceBox(children: [
        Fact(s.t('payee'), detail.creditorName,
            key: const Key('payment-creditor-name')),
        if (detail.iban != null)
          Fact(s.t('account'), detail.ibanGrouped!,
              mono: true, key: const Key('payment-iban'))
        else ...[
          Fact(s.t('accountNumber'), detail.identification!,
              mono: true, key: const Key('payment-identification')),
          Fact(s.t('accountScheme'), detail.scheme!,
              key: const Key('payment-scheme')),
        ],
        if (detail.remittance != null)
          Fact(s.t('remittance'), detail.remittance!,
              key: const Key('payment-remittance')),
      ]),
      if (detail.executionDate != null) ...[
        const SizedBox(height: AppSpacing.s),
        Fact(s.t('executionDate'), detail.executionDate!,
            key: const Key('payment-execution-date')),
      ],
    ]);
  }
}
