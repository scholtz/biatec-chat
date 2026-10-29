import 'package:flutter/material.dart';

import '../security/settings_controller.dart';
import '../security/settings_frame.dart';

/// Device management: the user's enrolled devices, and cancelling a pending
/// enrollment from any of them (rule 7).
class DevicesScreen extends StatelessWidget {
  const DevicesScreen({super.key, required this.services, this.controller});

  final SettingsServices services;

  /// Shared with the other settings tabs (see [SettingsFrame.controller]).
  final SettingsController? controller;

  @override
  Widget build(BuildContext context) {
    return SettingsFrame(
      title: 'Device management',
      services: services,
      controller: controller,
      builder: (context, c) {
        if (c.devices.isEmpty) {
          return const [
            ListTile(
                key: Key('no-devices'),
                leading: Icon(Icons.devices),
                title: Text('No devices are enrolled.')),
          ];
        }
        return [
          for (final d in c.devices)
            ListTile(
              key: Key('device-${d.kid}'),
              leading: Icon(d.pending ? Icons.hourglass_top : Icons.phone_android),
              title: Text(d.current ? '${d.kid} (this device)' : d.kid),
              subtitle: Text(d.pending
                  ? 'Pending. It becomes active ${whenText(d.activeAt)}. '
                      'Until then it approves nothing.'
                  : 'Active'),
              trailing: d.pending
                  ? TextButton(
                      key: Key('cancel-${d.kid}'),
                      onPressed: c.busy ? null : () => _confirmCancel(context, c, d.kid),
                      child: const Text('Cancel enrollment'),
                    )
                  : null,
            ),
        ];
      },
    );
  }

  Future<void> _confirmCancel(
      BuildContext context, SettingsController c, String kid) async {
    final yes = await showDialog<bool>(
      context: context,
      builder: (context) => AlertDialog(
        title: const Text('Cancel this enrollment?'),
        content: const Text('The device is removed and can no longer '
            'approve anything. You can enroll it again later.'),
        actions: [
          TextButton(
              key: const Key('keep'),
              onPressed: () => Navigator.of(context).pop(false),
              child: const Text('Keep')),
          FilledButton(
              key: const Key('confirm-cancel'),
              onPressed: () => Navigator.of(context).pop(true),
              child: const Text('Cancel enrollment')),
        ],
      ),
    );
    if (yes == true) await c.cancelDevice(kid);
  }
}
