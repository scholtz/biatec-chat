import 'package:material_ui/material_ui.dart';

import '../security/settings_client.dart';
import '../security/settings_controller.dart';
import '../security/settings_frame.dart';
import '../theme.dart';
import '../ui/ui.dart';
import 'label_rules.dart';
import '../security/security_texts.dart';
import 'service_identity.dart';
import 'trust_strings.dart';

/// The clients screen of before the user-held trust settings (T-245), shown while `AppConfig.trustSettings` is off. A service is drawn by the one identity widget like everywhere else;
/// nothing here says "verified".
///
/// Trusted clients: per-client threshold, "always ask" and lifting the
/// 24 hour pause of rule 10. Loosening any of them needs approval on this
/// device; tightening applies at once.
///
/// Each client is a card: who it is and whether its name is confirmed, whether it is paused, and the three controls. A change that loosens something is confirmed in a sheet that says the phone will ask for a fingerprint or face; one that tightens applies at once.
class LegacyClientsScreen extends StatelessWidget {
  const LegacyClientsScreen({super.key, required this.services, this.controller});

  final SettingsServices services;

  /// Shared with the other settings tabs (see [SettingsFrame.controller]).
  final SettingsController? controller;

  @override
  Widget build(BuildContext context) {
    final t = TrustStrings.of(Localizations.maybeLocaleOf(context));
    return SettingsFrame(
      title: t.t('legacyTitle'),
      services: services,
      controller: controller,
      builder: (context, c) {
        final s = c.settings!;
        if (s.clients.isEmpty) {
          return [
            Padding(
              key: const Key('no-clients'),
              padding: const EdgeInsets.all(AppSpacing.l),
              child: Column(children: [
                const ExcludeSemantics(
                    child: Icon(Icons.verified_user, size: 48)),
                const SizedBox(height: AppSpacing.m),
                Text(t.t('legacyNoClients'), textAlign: TextAlign.center),
              ]),
            ),
          ];
        }
        return [
          for (final client in s.clients)
            Padding(
              padding: const EdgeInsets.symmetric(
                  horizontal: AppSpacing.m, vertical: AppSpacing.s),
              child: _ClientCard(client: client, account: s, controller: c),
            ),
        ];
      },
    );
  }
}

class _ClientCard extends StatelessWidget {
  const _ClientCard(
      {required this.client, required this.account, required this.controller});

  final ClientSetting client;
  final ConsentSettings account;
  final SettingsController controller;

  Future<void> _apply(BuildContext context, Map<String, Object?> change,
          {required String title,
          required String message,
          required String confirmLabel}) =>
      applyWithConfirm(context, controller, account, change,
          title: title, message: message, confirmLabel: confirmLabel);

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final locale = Localizations.maybeLocaleOf(context);
    final t = TrustStrings.of(locale);
    final f = SecurityTexts.of(locale);
    final id = client.clientId;
    final enabled = !controller.busy;
    final until = client.pausedUntil;
    final own = client.perClientThresholdEur;
    final name = displaySafeLabel(client.label) ??
        (client.domain ?? t.t('legacyThisService'));
    Map<String, Object?> one(String key, Object? value) => {
          'clients': {
            id: {key: value}
          }
        };
    return Card(
      key: Key('client-$id'),
      child: Padding(
        padding: const EdgeInsets.all(AppSpacing.m),
        child:
            Column(crossAxisAlignment: CrossAxisAlignment.stretch, children: [
          ServiceIdentity.setting(client, compact: true),
          if (until != null) ...[
            const SizedBox(height: AppSpacing.m),
            StatusBanner(
              key: Key('paused-$id'),
              severity: StatusSeverity.warning,
              icon: Icons.pause_circle_outline,
              title: t.t('legacyPausedUntil', {'when': whenText(until)}),
              message: t.t('legacyPausedBody'),
            ),
            const SizedBox(height: AppSpacing.s),
            FilledButton(
              key: Key('lift-$id'),
              onPressed: enabled
                  ? () => _apply(
                      context,
                      {
                        'clients': {
                          id: {
                            'paused': false,
                            // The signed schema wants the origin of the pause with `paused: false`; a server that lists no pauses (only `paused_until`) is asked as before.
                            if (client.pauses.isNotEmpty)
                              'origin': client.pauses.first.origin
                          }
                        }
                      },
                      title: t.t('legacyLiftTitle', {'name': name}),
                      message: t.t('legacyLiftBody', {'name': name}),
                      confirmLabel: t.t('liftPause'))
                  : null,
              child: Text(t.t('liftPause')),
            ),
          ],
          const SizedBox(height: AppSpacing.s),
          SwitchListTile(
            key: Key('always-ask-$id'),
            contentPadding: EdgeInsets.zero,
            title: Text(t.t('alwaysAsk')),
            subtitle: Text(t.t('legacyAlwaysAskHelp')),
            value: client.alwaysAsk,
            onChanged: enabled
                ? (v) => _apply(context, one('always_ask', v),
                    title: t.t('legacyAlwaysAskOffTitle', {'name': name}),
                    message: t.t('legacyAlwaysAskOffBody', {'name': name}),
                    confirmLabel: t.t('turnOff'))
                : null,
          ),
          const Divider(),
          MergeSemantics(
            child:
                Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
              Text(t.t('legacyClientThreshold'),
                  key: Key('threshold-$id'), style: theme.textTheme.titleSmall),
              const SizedBox(height: AppSpacing.xs),
              Text(
                  own == null
                      ? t.t('legacyUsesAccount',
                          {'amount': account.thresholdEur})
                      : t.t('legacyOwn', {'amount': own}),
                  style: theme.textTheme.bodyMedium),
            ]),
          ),
          const SizedBox(height: AppSpacing.s),
          OutlinedButton(
            key: Key('threshold-change-$id'),
            onPressed: enabled
                ? () async {
                    final v = await askEur(context,
                        title: t.t('legacyThresholdFor', {'name': name}),
                        initial: own ?? account.thresholdEur,
                        current: own ?? account.thresholdEur,
                        hint: f.text('lowerRaiseHint'));
                    if (v == null || !context.mounted) return;
                    await controller.apply(one('per_client_threshold_eur', v));
                  }
                : null,
            child: Text(t.t('changeThreshold')),
          ),
          if (own != null) ...[
            const SizedBox(height: AppSpacing.s),
            TextButton(
              key: Key('threshold-reset-$id'),
              onPressed: enabled
                  ? () => _apply(context, one('per_client_threshold_eur', null),
                      title: t.t('legacyResetTitle', {'name': name}),
                      message: t.t('legacyResetBody',
                          {'amount': account.thresholdEur}),
                      confirmLabel: t.t('legacyUseAccount'))
                  : null,
              child: Text(t.t('legacyUseAccount')),
            ),
          ],
        ]),
      ),
    );
  }
}
