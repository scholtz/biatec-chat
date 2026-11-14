import 'package:flutter/material.dart';

import '../security/settings_client.dart';
import '../security/settings_controller.dart';
import '../security/settings_frame.dart';
import '../theme.dart';
import 'client_detail_screen.dart';
import 'service_identity.dart';
import 'trust_strings.dart';
import 'trust_widgets.dart';

/// Settings, Security, Linked services: every service the user linked to this authenticator, each drawn by the one identity widget (its proven domain, or "Unproven application" and the fingerprint,
/// and the user's own label), with the date it was linked. A tap opens its detail. Nothing here is a name the operator or the service asserted.
class ClientsScreen extends StatelessWidget {
  const ClientsScreen({super.key, required this.services, this.controller});

  final SettingsServices services;

  /// Shared with the other settings screens (see [SettingsFrame.controller]).
  final SettingsController? controller;

  @override
  Widget build(BuildContext context) {
    final s = TrustStrings.of();
    return SettingsFrame(
      title: s.t('linkedServicesTitle'),
      services: services,
      controller: controller,
      builder: (context, c) {
        final clients = c.settings!.clients;
        return [
          ...inactiveBanner(c),
          if (clients.isEmpty)
            Padding(
              key: const Key('no-clients'),
              padding: const EdgeInsets.all(AppSpacing.l),
              child: Column(children: [
                const ExcludeSemantics(child: Icon(Icons.link_off, size: 48)),
                const SizedBox(height: AppSpacing.m),
                Text(s.t('noLinkedServices'), textAlign: TextAlign.center),
              ]),
            )
          else
            for (final client in clients)
              Padding(
                padding: const EdgeInsets.symmetric(
                    horizontal: AppSpacing.m, vertical: AppSpacing.s),
                child: _ServiceCard(
                    client: client,
                    onOpen: () => Navigator.of(context).push(
                        ClientDetailScreen.route(services, c, client.clientId))),
              ),
        ];
      },
    );
  }
}

class _ServiceCard extends StatelessWidget {
  const _ServiceCard({required this.client, required this.onOpen});

  final ClientSetting client;
  final VoidCallback onOpen;

  @override
  Widget build(BuildContext context) {
    final s = TrustStrings.of();
    final theme = Theme.of(context);
    final linked = client.linkedAt;
    final paused = client.pausedUntil != null;
    return Card(
      key: Key('client-${client.clientId}'),
      clipBehavior: Clip.antiAlias,
      child: InkWell(
        key: Key('open-${client.clientId}'),
        onTap: onOpen,
        child: Padding(
          padding: const EdgeInsets.all(AppSpacing.m),
          child: Row(children: [
            Expanded(
              child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    ServiceIdentity.setting(client, compact: true),
                    const SizedBox(height: AppSpacing.xs),
                    Text(
                        linked == null
                            ? s.t('notLinkedDate')
                            : s.t('linkedOn', {'when': whenText(linked)}),
                        key: Key('linked-${client.clientId}'),
                        style: theme.textTheme.bodySmall?.copyWith(
                            color: theme.colorScheme.onSurfaceVariant)),
                    if (paused || client.alwaysAsk)
                      Padding(
                        padding: const EdgeInsets.only(top: AppSpacing.xs),
                        child: Text(
                            [
                              if (paused) s.t('pausedShort'),
                              if (client.alwaysAsk) s.t('alwaysAskShort'),
                            ].join(' · '),
                            style: theme.textTheme.bodySmall),
                      ),
                  ]),
            ),
            const Icon(Icons.chevron_right),
          ]),
        ),
      ),
    );
  }
}
