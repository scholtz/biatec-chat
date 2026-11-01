import 'package:flutter/material.dart';

import '../security/settings_client.dart';
import '../security/settings_controller.dart';
import '../security/settings_frame.dart';
import '../theme.dart';
import '../ui/ui.dart';

/// Trusted clients: per-client threshold, "always ask" and lifting the
/// 24 hour pause of rule 10. Loosening any of them needs approval on this
/// device; tightening applies at once.
///
/// Each client is a card: who it is and whether its name is confirmed, whether it is paused, and the three controls. A change that loosens something is confirmed in a sheet that says the phone will ask for a fingerprint or face; one that tightens applies at once.
class ClientsScreen extends StatelessWidget {
  const ClientsScreen({super.key, required this.services, this.controller});

  final SettingsServices services;

  /// Shared with the other settings tabs (see [SettingsFrame.controller]).
  final SettingsController? controller;

  @override
  Widget build(BuildContext context) {
    return SettingsFrame(
      title: 'Trusted clients and thresholds',
      services: services,
      controller: controller,
      builder: (context, c) {
        final s = c.settings!;
        if (s.clients.isEmpty) {
          return const [
            Padding(
              key: Key('no-clients'),
              padding: EdgeInsets.all(AppSpacing.l),
              child: Column(children: [
                ExcludeSemantics(child: Icon(Icons.verified_user, size: 48)),
                SizedBox(height: AppSpacing.m),
                Text(
                    'No client has asked for an approval yet. Each one that '
                    'does will appear here.',
                    textAlign: TextAlign.center),
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

  /// Applies [change], first asking in a sheet when it loosens something.
  Future<void> _apply(BuildContext context, Map<String, Object?> change,
      {required String title,
      required String message,
      required String confirmLabel}) async {
    if (isLoosening(account, change)) {
      final yes = await confirmSheet(
        context,
        title: title,
        message: message,
        confirmLabel: confirmLabel,
        cancelLabel: 'Keep as it is',
        biometric: true,
        icon: Icons.fingerprint,
      );
      if (!yes) return;
    }
    await controller.apply(change);
  }

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final id = client.clientId;
    final enabled = !controller.busy;
    final until = client.pausedUntil;
    final own = client.perClientThresholdEur;
    final name = client.label;
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
          MergeSemantics(
            child: Row(crossAxisAlignment: CrossAxisAlignment.start, children: [
              ExcludeSemantics(
                  child: Icon(
                      client.verified
                          ? Icons.verified_user
                          : Icons.warning_amber,
                      color: client.verified
                          ? theme.colorScheme.primary
                          : theme.colorScheme.error)),
              const SizedBox(width: AppSpacing.m),
              Expanded(
                child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Text(name,
                          style: theme.textTheme.titleMedium
                              ?.copyWith(fontWeight: FontWeight.w600)),
                      Text(
                          client.verified
                              ? 'Verified client'
                              : 'Unverified client. Its name is not '
                                  'confirmed.',
                          style: theme.textTheme.bodyMedium?.copyWith(
                              color: theme.colorScheme.onSurfaceVariant)),
                    ]),
              ),
            ]),
          ),
          if (until != null) ...[
            const SizedBox(height: AppSpacing.m),
            StatusBanner(
              key: Key('paused-$id'),
              severity: StatusSeverity.warning,
              icon: Icons.pause_circle_outline,
              title: 'Paused until ${whenText(until)}',
              message: 'This client cannot ask for approvals while '
                  'paused. Lifting the pause needs approval on this '
                  'device.',
            ),
            const SizedBox(height: AppSpacing.s),
            FilledButton(
              key: Key('lift-$id'),
              onPressed: enabled
                  ? () => _apply(context, one('paused', false),
                      title: 'Lift the pause for $name?',
                      message: '$name will be able to ask for approvals '
                          'again straight away.',
                      confirmLabel: 'Lift pause')
                  : null,
              child: const Text('Lift pause'),
            ),
          ],
          const SizedBox(height: AppSpacing.s),
          SwitchListTile(
            key: Key('always-ask-$id'),
            contentPadding: EdgeInsets.zero,
            title: const Text('Always ask'),
            subtitle: const Text('Every action from this client needs '
                'your phone. Turning it off needs approval on this '
                'device.'),
            value: client.alwaysAsk,
            onChanged: enabled
                ? (v) => _apply(context, one('always_ask', v),
                    title: 'Stop always asking for $name?',
                    message: 'Small actions from $name will no longer '
                        'need your phone. Large ones still do.',
                    confirmLabel: 'Turn off')
                : null,
          ),
          const Divider(),
          MergeSemantics(
            child:
                Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
              Text('Threshold for this client',
                  key: Key('threshold-$id'), style: theme.textTheme.titleSmall),
              const SizedBox(height: AppSpacing.xs),
              Text(
                  own == null
                      ? 'Uses the account threshold '
                          '(${account.thresholdEur} EUR).'
                      : '$own EUR. Raising it needs approval on this '
                          'device.',
                  style: theme.textTheme.bodyMedium),
            ]),
          ),
          const SizedBox(height: AppSpacing.s),
          OutlinedButton(
            key: Key('threshold-change-$id'),
            onPressed: enabled
                ? () async {
                    final v = await askEur(context,
                        title: 'Threshold for $name',
                        initial: own ?? account.thresholdEur,
                        current: own ?? account.thresholdEur,
                        hint: 'Lowering applies at once. Raising needs '
                            'approval on this device.');
                    if (v == null || !context.mounted) return;
                    await controller.apply(one('per_client_threshold_eur', v));
                  }
                : null,
            child: const Text('Change threshold'),
          ),
          if (own != null) ...[
            const SizedBox(height: AppSpacing.s),
            TextButton(
              key: Key('threshold-reset-$id'),
              onPressed: enabled
                  ? () => _apply(context, one('per_client_threshold_eur', null),
                      title: 'Use the account threshold for $name?',
                      message: 'It goes back to the account threshold of '
                          '${account.thresholdEur} EUR. Actions up to '
                          'that amount will no longer need your phone.',
                      confirmLabel: 'Use account threshold')
                  : null,
              child: const Text('Use account threshold'),
            ),
          ],
        ]),
      ),
    );
  }
}
