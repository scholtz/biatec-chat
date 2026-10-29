import 'package:flutter/material.dart';

import '../security/settings_client.dart';
import '../security/settings_controller.dart';
import '../security/settings_frame.dart';

/// Trusted clients: per-client threshold, "always ask" and lifting the
/// 24 hour pause of rule 10. Loosening any of them needs approval on this
/// device; tightening applies at once.
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
        return [
          for (final client in s.clients)
            _ClientCard(client: client, account: s, controller: c),
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

  @override
  Widget build(BuildContext context) {
    final id = client.clientId;
    final enabled = !controller.busy;
    final until = client.pausedUntil;
    final own = client.perClientThresholdEur;
    return Card(
      margin: const EdgeInsets.symmetric(horizontal: 16, vertical: 8),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          ListTile(
            key: Key('client-$id'),
            leading: Icon(
                client.verified ? Icons.verified_user : Icons.warning_amber),
            title: Text(client.label),
            subtitle: Text(client.verified
                ? 'Verified client'
                : 'Unverified client. Its name is not confirmed.'),
          ),
          if (until != null)
            ListTile(
              key: Key('paused-$id'),
              leading: const Icon(Icons.pause_circle_outline),
              title: Text('Paused until ${whenText(until)}'),
              subtitle: const Text('This client cannot ask for approvals '
                  'while paused. Lifting the pause needs approval on this '
                  'device.'),
              trailing: FilledButton(
                key: Key('lift-$id'),
                onPressed: enabled
                    ? () => controller.apply({
                          'clients': {
                            id: {'paused': false}
                          }
                        })
                    : null,
                child: const Text('Lift pause'),
              ),
            ),
          SwitchListTile(
            key: Key('always-ask-$id'),
            title: const Text('Always ask'),
            subtitle: const Text('Every action from this client needs your '
                'phone. Turning it off needs approval on this device.'),
            value: client.alwaysAsk,
            onChanged: enabled
                ? (v) => controller.apply({
                      'clients': {
                        id: {'always_ask': v}
                      }
                    })
                : null,
          ),
          ListTile(
            key: Key('threshold-$id'),
            title: const Text('Threshold for this client'),
            subtitle: Text(own == null
                ? 'Uses the account threshold (${account.thresholdEur} EUR).'
                : '$own EUR. Raising it needs approval on this device.'),
          ),
          Padding(
            padding: const EdgeInsets.symmetric(horizontal: 16),
            child: OverflowBar(
              alignment: MainAxisAlignment.end,
              children: [
                if (own != null)
                  TextButton(
                    key: Key('threshold-reset-$id'),
                    onPressed: enabled
                        ? () => controller.apply({
                              'clients': {
                                id: {'per_client_threshold_eur': null}
                              }
                            })
                        : null,
                    child: const Text('Use account'),
                  ),
                TextButton(
                  key: Key('threshold-change-$id'),
                  onPressed: enabled
                      ? () async {
                          final v = await askEur(context,
                              title: 'Threshold for ${client.label}',
                              initial: own ?? account.thresholdEur,
                              hint: 'Lowering applies at once. Raising '
                                  'needs approval on this device.');
                          if (v != null) {
                            await controller.apply({
                              'clients': {
                                id: {'per_client_threshold_eur': v}
                              }
                            });
                          }
                        }
                      : null,
                  child: const Text('Change'),
                ),
              ],
            ),
          ),
        ],
      ),
    );
  }
}
