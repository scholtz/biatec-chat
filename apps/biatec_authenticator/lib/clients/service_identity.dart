import 'package:flutter/material.dart';

import '../approvals/consent_request.dart';
import '../security/settings_client.dart';
import '../theme.dart';
import '../ui/ui.dart';
import 'label_rules.dart';
import 'trust_strings.dart';

/// `xxxx-xxxx-xxxx-xxxx` for the 16 hex characters of a fingerprint (specification section 6), anything else as it is.
String formatFingerprint(String? fingerprint) {
  final f = fingerprint ?? '';
  if (f.length != 16) return f;
  return [for (var i = 0; i < 16; i += 4) f.substring(i, i + 4)].join('-');
}

bool _ascii(String s) => s.codeUnits.every((u) => u > 0x20 && u < 0x7F);

/// How a service is shown, everywhere (the approval card, the list of linked services, its detail): ONE widget, so nothing draws a service another way.
///
/// - Its proven domain, in the ASCII (punycode) form Biatec OIDC checked, or "Unproven application" in the warning style with the fingerprint. A domain that is not plain ASCII is not
///   drawn as a proven one (a look-alike of a name is the attack).
/// - Under it, the user's own label ("You call it: ...") in another style (italic, a quoted block), so it can never be read as the domain. A label that breaks the label rules is dropped.
/// - Never a name, a logo or a badge that Biatec OIDC, the operator or the service asserted: the widget takes no such value, so no fixture can make one appear.
class ServiceIdentity extends StatelessWidget {
  const ServiceIdentity({
    super.key,
    required this.domainProven,
    required this.fingerprint,
    this.domain,
    this.label,
    this.compact = false,
  });

  /// A request's client: `{fingerprint, domain_proven, domain?}`. The user's [label] is not in a request, so the card shows none.
  factory ServiceIdentity.client(ConsentClient client,
          {Key? key, String? label, bool compact = false}) =>
      ServiceIdentity(
        key: key,
        domainProven: client.domainProven,
        domain: client.domain,
        fingerprint: client.fingerprint,
        label: label,
        compact: compact,
      );

  /// A linked service of the settings.
  factory ServiceIdentity.setting(ClientSetting client,
          {Key? key, bool compact = false}) =>
      ServiceIdentity(
        key: key,
        domainProven: client.domainProven,
        domain: client.domain,
        fingerprint: client.fingerprint,
        label: client.label,
        compact: compact,
      );

  final bool domainProven;

  /// The proven ASCII host (only drawn when [domainProven]).
  final String? domain;
  final String? fingerprint;

  /// The user's own name for the service.
  final String? label;

  /// No explanation under the warning: for a list, where the screen around it says what it means.
  final bool compact;

  /// What is drawn as the proven domain, or null when the service is drawn as unproven.
  String? get _provenDomain {
    final d = domain;
    return domainProven && d != null && d.isNotEmpty && _ascii(d) ? d : null;
  }

  @override
  Widget build(BuildContext context) {
    final s = TrustStrings.of();
    final theme = Theme.of(context);
    final shown = formatFingerprint(fingerprint);
    final host = _provenDomain;
    final own = displaySafeLabel(label);
    final Widget identity;
    if (host == null) {
      identity = compact
          ? MergeSemantics(
              child: Row(
                  key: const Key('client-unverified'),
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Icon(Icons.warning_amber_rounded,
                        color: theme.colorScheme.error,
                        semanticLabel: s.t('warning')),
                    const SizedBox(width: AppSpacing.s),
                    Expanded(
                      child: Text('${s.t('unprovenApplication')} $shown'.trim(),
                          key: const Key('service-unproven'),
                          style: theme.textTheme.titleMedium?.copyWith(
                              color: theme.colorScheme.error,
                              fontWeight: FontWeight.w700)),
                    ),
                  ]),
            )
          : StatusBanner(
              key: const Key('client-unverified'),
              severity: StatusSeverity.warning,
              icon: Icons.warning_amber_rounded,
              title: '${s.t('unprovenApplication')} $shown'.trim(),
              message: s.t('unprovenExplain'),
            );
    } else {
      identity = MergeSemantics(
        child: Row(
            key: const Key('client-verified'),
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Icon(Icons.verified,
                  color: theme.colorScheme.primary,
                  semanticLabel: s.t('provenDomain')),
              const SizedBox(width: AppSpacing.s),
              Expanded(
                child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Text(host,
                          key: const Key('service-domain'),
                          style: theme.textTheme.titleMedium),
                      Text('${s.t('provenDomain')}, ${s.t('application')} $shown'.trim(),
                          key: const Key('service-fingerprint'),
                          style: theme.textTheme.labelSmall?.copyWith(
                              color: theme.colorScheme.onSurfaceVariant)),
                    ]),
              ),
            ]),
      );
    }
    if (own == null) return identity;
    return Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
      identity,
      const SizedBox(height: AppSpacing.xs),
      // The user's own words: another style on purpose (italic, quoted, a rule at the side), never the weight or colour of the domain.
      Container(
        key: const Key('service-label'),
        padding: const EdgeInsets.only(left: AppSpacing.s),
        decoration: BoxDecoration(
            border: Border(
                left: BorderSide(
                    color: theme.colorScheme.outlineVariant, width: 3))),
        child: Text('${s.t('youCallIt')} “$own”',
            style: theme.textTheme.bodyMedium?.copyWith(
                fontStyle: FontStyle.italic,
                color: theme.colorScheme.onSurfaceVariant)),
      ),
    ]);
  }
}
