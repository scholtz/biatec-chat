import 'package:material_ui/material_ui.dart';

import '../../clients/service_identity.dart';
import '../../text/rp_strings.dart';
import '../../theme.dart';
import '../consent_request.dart';

/// `biatec_login`: "Sign in to" the domain or the fingerprint, then what the service reports about the session that asks (address, place, browser) under "Reported by the service", in a quieter style
/// than the app's own words. The service is named by what Biatec checked (the proven domain, or the fingerprint), never by the text it wrote.
class LoginCardBody extends StatelessWidget {
  const LoginCardBody({super.key, required this.detail, required this.client});

  final LoginDetail detail;
  final ConsentClient client;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final s = RpStrings.of(Localizations.maybeLocaleOf(context));
    final who = provenDomainOf(client.domainProven, client.domain) ??
        formatFingerprint(client.fingerprint);
    final labels = {
      'ip': s.t('ctxIp'),
      'location': s.t('ctxLocation'),
      'userAgent': s.t('ctxUserAgent'),
    };
    final quiet = theme.textTheme.bodyMedium
        ?.copyWith(color: theme.colorScheme.onSurfaceVariant);
    return Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
      Semantics(
        header: true,
        child: Text(s.t('signInTo', {'who': who}),
            key: const Key('login-title'),
            style: theme.textTheme.headlineSmall
                ?.copyWith(fontWeight: FontWeight.w700)),
      ),
      const SizedBox(height: AppSpacing.m),
      Semantics(
        header: true,
        child: Text(s.t('reportedByService'),
            key: const Key('reported-by-service'),
            style: theme.textTheme.labelLarge
                ?.copyWith(color: theme.colorScheme.onSurfaceVariant)),
      ),
      const SizedBox(height: AppSpacing.xs),
      if (detail.context.isEmpty)
        Text(s.t('loginNothing'), key: const Key('login-nothing'), style: quiet)
      else
        for (final (key, value) in detail.context)
          Padding(
            padding: const EdgeInsets.only(bottom: AppSpacing.xs),
            child: MergeSemantics(
              child: Text.rich(
                TextSpan(children: [
                  TextSpan(text: '${labels[key]}: '),
                  TextSpan(text: value),
                ]),
                key: Key('login-context-$key'),
                style: quiet,
              ),
            ),
          ),
    ]);
  }
}
