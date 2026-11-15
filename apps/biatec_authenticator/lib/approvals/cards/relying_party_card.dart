import 'package:flutter/material.dart';

import '../../clients/service_identity.dart';
import '../../links/rp_strings.dart';
import '../../theme.dart';
import '../consent_request.dart';
import '../widgets/fact_row.dart';
import 'action_card.dart';
import 'login_card.dart';
import 'payment_card.dart';

/// The card of a payment, login or action request of a relying party (protocol 1.2), chosen by `authorization_details.type`. Every card shows who asks with the one service-identity widget
/// (the proven domain, or "Unproven application" and the fingerprint; never a name the service chose), the details in fields this app draws itself as plain text (never markup, links or images), and
/// the origin the request came from when there is one. The countdown and the binding-code field are the approval screen's, below the card, as for every request.
///
/// Everything the service wrote sits in an area that says so ([WrittenByServiceBox]) or under "Reported by the service" in a quieter style, so it cannot be read as the app's own words.
class RelyingPartyCard extends StatelessWidget {
  const RelyingPartyCard({super.key, required this.request});

  final ConsentRequest request;

  @override
  Widget build(BuildContext context) {
    final detail = request.detail;
    if (detail is! RelyingPartyDetail) return const SizedBox.shrink();
    final s = RpStrings.of();
    final body = switch (detail) {
      PaymentDetail() => PaymentCardBody(detail: detail),
      LoginDetail() => LoginCardBody(detail: detail, client: request.client),
      ActionDetail() => ActionCardBody(detail: detail),
    };
    final origin = detail.origin;
    return Card(
      key: Key('rp-card-${detail.object['type']}'),
      child: Padding(
        padding: const EdgeInsets.all(AppSpacing.m),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            ServiceIdentity.client(request.client),
            const SizedBox(height: AppSpacing.m),
            body,
            if (origin != null) ...[
              const Divider(height: 24),
              Fact(s.t('originLabel'), origin, key: const Key('fact-origin')),
            ],
          ],
        ),
      ),
    );
  }
}

/// The area that holds text the service wrote: a border and a heading ("Written by the service"), so it is never mistaken for the app's own text.
class WrittenByServiceBox extends StatelessWidget {
  const WrittenByServiceBox({super.key, required this.children});

  final List<Widget> children;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final s = RpStrings.of();
    return Container(
      key: const Key('written-by-service'),
      width: double.infinity,
      padding: const EdgeInsets.all(AppSpacing.s),
      decoration: BoxDecoration(
        border: Border.all(color: theme.colorScheme.outline, width: 1.5),
        borderRadius: BorderRadius.circular(8),
      ),
      child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
        Semantics(
          header: true,
          child: Text(s.t('writtenByService'),
              key: const Key('written-by-service-heading'),
              style: theme.textTheme.labelLarge
                  ?.copyWith(color: theme.colorScheme.onSurfaceVariant)),
        ),
        const SizedBox(height: AppSpacing.xs),
        ...children,
      ]),
    );
  }
}
