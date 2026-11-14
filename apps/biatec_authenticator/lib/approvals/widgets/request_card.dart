import 'package:flutter/material.dart';

import '../../theme.dart';
import '../approval_controller.dart' show clientLabel, summarize;
import '../consent_request.dart';
import 'expiry.dart';

/// One pending request in the list: who asks, what for (the amount first), and how long is left, as words and as a ring. The whole card opens the request. A screen reader hears it as one button with everything in full.
class RequestCard extends StatelessWidget {
  const RequestCard({
    super.key,
    required this.request,
    required this.now,
    required this.onOpen,
  });

  final ConsentRequest request;
  final DateTime now;
  final VoidCallback onOpen;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final detail = request.detail;
    final left = request.expiresAt.difference(now);
    final who = clientLabel(request.client);
    final (headline, sub) = switch (detail) {
      TransactionDetail() => (
          detail.headline,
          '${detail.valueText} on ${detail.network}'
        ),
      LimitsChangeDetail() => ('Change spending limits', summarize(detail)),
    };
    final spoken = 'Request from $who: $headline, $sub, ${spokenLeft(left)}. '
        '${request.client.verified ? '' : 'Unproven application. '}'
        'Double tap to open.';
    return Card(
      clipBehavior: Clip.antiAlias,
      child: Semantics(
        container: true,
        button: true,
        label: spoken,
        onTap: onOpen,
        excludeSemantics: true,
        child: InkWell(
          key: Key('pending-${request.authReqId}'),
          onTap: onOpen,
          child: Padding(
            padding: const EdgeInsets.all(AppSpacing.m),
            child: Row(crossAxisAlignment: CrossAxisAlignment.start, children: [
              Padding(
                padding: const EdgeInsets.only(top: 2),
                child: Icon(
                    request.client.verified
                        ? Icons.verified
                        : Icons.warning_amber_rounded,
                    semanticLabel:
                        request.client.verified ? 'Proven domain' : 'Unproven'),
              ),
              const SizedBox(width: AppSpacing.m),
              Expanded(
                child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Text(who, style: theme.textTheme.labelLarge),
                      const SizedBox(height: AppSpacing.xs),
                      Text(headline,
                          style: theme.textTheme.titleLarge
                              ?.copyWith(fontWeight: FontWeight.w700)),
                      Text(sub, style: theme.textTheme.bodyMedium),
                      const SizedBox(height: AppSpacing.s),
                      Text(coarseLeft(left),
                          key: Key('left-${request.authReqId}'),
                          style: theme.textTheme.labelMedium?.copyWith(
                              color: theme.colorScheme.onSurfaceVariant)),
                    ]),
              ),
              const SizedBox(width: AppSpacing.s),
              ExpiryRing(fraction: expiryFraction(request, now)),
            ]),
          ),
        ),
      ),
    );
  }
}
