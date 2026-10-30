import 'package:flutter/material.dart';

import 'settings_controller.dart';
import 'settings_frame.dart';

/// Security settings: biometric requirement, approval threshold and blocking
/// of unverified clients. Turning protection down needs approval on this
/// device; turning it up applies at once.
class SecurityScreen extends StatelessWidget {
  const SecurityScreen(
      {super.key, required this.services, this.server, this.controller});

  final SettingsServices services;

  /// Shared with the other settings tabs (see [SettingsFrame.controller]).
  final SettingsController? controller;

  /// The Biatec OIDC this build talks to, shown so the user and support can see which server the device is enrolled with.
  final String? server;

  @override
  Widget build(BuildContext context) {
    final server = this.server;
    return SettingsFrame(
      title: 'Security settings',
      services: services,
      controller: controller,
      footer: server == null
          ? null
          : ListTile(
              key: const Key('oidc-server'),
              dense: true,
              leading: const Icon(Icons.dns_outlined),
              title: const Text('Biatec OIDC server'),
              subtitle: Text(server),
            ),
      builder: (context, c) {
        final s = c.settings!;
        final enabled = !c.busy;
        return [
          SwitchListTile(
            key: const Key('biometric'),
            title: const Text('Require a biometric check'),
            subtitle: const Text('Asked before this device approves. '
                'Turning it off needs approval on this device.'),
            value: s.biometricRequired,
            onChanged: enabled
                ? (v) => c.apply({'biometric_required': v})
                : null,
          ),
          ListTile(
            key: const Key('threshold'),
            title: const Text('Approval threshold'),
            subtitle: Text('${s.thresholdEur} EUR. Actions above it need your '
                'phone. Raising it needs approval on this device.'),
            trailing: TextButton(
              key: const Key('threshold-change'),
              onPressed: enabled
                  ? () async {
                      final v = await askEur(context,
                          title: 'Approval threshold',
                          initial: s.thresholdEur,
                          hint: 'Lowering applies at once. Raising needs '
                              'approval on this device.');
                      if (v != null) await c.apply({'threshold_eur': v});
                    }
                  : null,
              child: const Text('Change'),
            ),
          ),
          SwitchListTile(
            key: const Key('block-unverified'),
            title: const Text('Block unverified clients'),
            subtitle: const Text('Applications whose identity is not '
                'verified cannot ask for approvals. Turning it off needs '
                'approval on this device.'),
            value: s.blockUnverifiedClients,
            onChanged: enabled
                ? (v) => c.apply({'block_unverified_clients': v})
                : null,
          ),
        ];
      },
    );
  }
}
