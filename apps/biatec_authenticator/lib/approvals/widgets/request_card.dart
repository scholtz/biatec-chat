import 'package:material_ui/material_ui.dart';

import '../../clients/trust_strings.dart';
import '../../theme.dart';
import '../../text/rp_strings.dart';
import '../approval_texts.dart';
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
    this.onDismiss,
  });

  /// For a request this app cannot show: hides it locally (nothing is sent). Null for every other request.
  final VoidCallback? onDismiss;

  final ConsentRequest request;
  final DateTime now;
  final VoidCallback onOpen;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final locale = Localizations.maybeLocaleOf(context);
    final t = ApprovalTexts.of(locale);
    final detail = request.detail;
    final left = request.expiresAt.difference(now);
    final who = clientLabel(request.client, locale);
    final (headline, sub, quoted) = switch (detail) {
      TransactionDetail() => (
          detail.headlineIn(locale),
          t.text('onNetwork', {
            'value': detail.valueTextIn(locale),
            'network': detail.network,
          }),
          null
        ),
      LimitsChangeDetail() =>
        (t.text('changeLimits'), summarize(detail, locale), null),
      // The headline and the line under it are the app's words (the amount passed the schema). What the service wrote goes only in the quoted line, in the quiet style, with who wrote it.
      PaymentDetail() => (
          '${detail.amount} ${detail.currency}',
          t.text('paymentRequest'),
          detail.creditorName
        ),
      LoginDetail() => (t.text('signIn'), t.text('signInRequest'), null),
      ActionDetail() => (
          t.text('actionRequested'),
          t.text('confirmActionRequest'),
          detail.title
        ),
      UnsupportedDetail() => (
          t.text('cannotShowTitle'),
          t.text('updateApp'),
          null
        ),
    };
    final rp = RpStrings.of(locale);
    final fromWho = quoted == null ? null : rp.t('fromService', {'who': who});
    // Each part is filled on its own, so text a service wrote is never read as a place to fill.
    final spoken = [
      t.text('cardSpoken', {
        'left': spokenLeft(left, locale),
        'sub': sub,
        'headline': headline,
        'who': who,
      }),
      if (quoted != null)
        t.text('cardSpokenQuoted', {'from': fromWho, 'quoted': quoted}),
      if (!request.client.domainProven) t.text('unprovenSpoken'),
      t.text('doubleTapOpen'),
    ].join(' ');
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
                    request.client.domainProven
                        ? Icons.verified
                        : Icons.warning_amber_rounded,
                    semanticLabel: request.client.domainProven
                        ? TrustStrings.of(locale).t('provenDomain')
                        : t.text('unproven')),
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
                      if (quoted != null)
                        Container(
                          key: Key('quoted-${request.authReqId}'),
                          margin: const EdgeInsets.only(top: AppSpacing.xs),
                          padding: const EdgeInsets.only(left: AppSpacing.s),
                          decoration: BoxDecoration(
                              border: Border(
                                  left: BorderSide(
                                      color: theme.colorScheme.outlineVariant,
                                      width: 3))),
                          child: Text('$fromWho “$quoted”',
                              style: theme.textTheme.bodySmall?.copyWith(
                                  fontStyle: FontStyle.italic,
                                  color: theme.colorScheme.onSurfaceVariant)),
                        ),
                      if (onDismiss != null)
                        TextButton(
                            key: Key('dismiss-${request.authReqId}'),
                            onPressed: onDismiss,
                            child: Text(rp.t('dismiss'))),
                      const SizedBox(height: AppSpacing.s),
                      Text(coarseLeft(left, locale),
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
