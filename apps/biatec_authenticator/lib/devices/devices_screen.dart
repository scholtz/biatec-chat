import 'package:flutter/material.dart';

import '../approvals/widgets/fact_row.dart';
import '../enrollment/widgets/key_protection_card.dart';
import '../keys/device_key_store.dart';
import '../security/settings_client.dart';
import '../security/settings_controller.dart';
import '../security/settings_frame.dart';
import '../theme.dart';
import '../ui/ui.dart';

/// Device management: the user's enrolled devices, and cancelling a pending
/// enrollment from any of them (rule 7).
///
/// Each device is a card: whether it is this one and whether it is active or pending, in an icon and a word; its key id in full; and, for this device (the only one whose keystore this app can ask), where its key lives. Biatec OIDC lets a device cancel a pending enrollment and nothing more, so only a pending device has a button, and the screen says that active devices stay: it shows no control that does nothing.
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
            Padding(
              key: Key('no-devices'),
              padding: EdgeInsets.all(AppSpacing.l),
              child: Column(children: [
                ExcludeSemantics(child: Icon(Icons.devices, size: 48)),
                SizedBox(height: AppSpacing.m),
                Text('No devices are enrolled.', textAlign: TextAlign.center),
              ]),
            ),
          ];
        }
        return [
          for (final d in c.devices)
            Padding(
              padding: const EdgeInsets.symmetric(
                  horizontal: AppSpacing.m, vertical: AppSpacing.s),
              child: _DeviceCard(device: d, services: services, controller: c),
            ),
          if (c.devices.any((d) => !d.pending))
            const Padding(
              padding: EdgeInsets.all(AppSpacing.m),
              child: StatusBanner(
                key: Key('devices-note'),
                severity: StatusSeverity.info,
                title: 'Devices that are in use stay',
                message: 'Only an enrollment that is still pending can be '
                    'cancelled here. A device that is in use cannot be removed '
                    'from this app.',
              ),
            ),
        ];
      },
    );
  }
}

class _DeviceCard extends StatelessWidget {
  const _DeviceCard(
      {required this.device, required this.services, required this.controller});

  final DeviceInfo device;
  final SettingsServices services;
  final SettingsController controller;

  Future<void> _confirmCancel(BuildContext context) async {
    final here = device.current;
    final yes = await confirmSheet(
      context,
      title: here
          ? 'Cancel the enrollment of this device?'
          : 'Cancel this enrollment?',
      message: here
          ? 'This is the device you are using. Cancelling its pending '
              'enrollment signs it out of this account: it can no longer '
              'approve anything, and you will have to enroll it again from the '
              'start.'
          : 'The device is removed and can no longer approve anything. You '
              'can enroll it again later.',
      warning: here ? 'You are cancelling the device in your hand.' : null,
      confirmLabel: 'Cancel enrollment',
      cancelLabel: 'Keep',
      confirmKey: const Key('confirm-cancel'),
      cancelKey: const Key('keep'),
      icon: Icons.phone_android,
    );
    if (yes) await controller.cancelDevice(device.kid);
  }

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final d = device;
    final busy = controller.busy;
    return Card(
      key: Key('device-${d.kid}'),
      child: Padding(
        padding: const EdgeInsets.all(AppSpacing.m),
        child:
            Column(crossAxisAlignment: CrossAxisAlignment.stretch, children: [
          MergeSemantics(
            child: Wrap(
                spacing: AppSpacing.s,
                runSpacing: AppSpacing.xs,
                children: [
                  if (d.current)
                    const _Badge(
                        key: Key('badge-this-device'),
                        icon: Icons.smartphone,
                        text: 'This device',
                        tone: _Tone.primary),
                  _Badge(
                      key: Key('badge-status-${d.kid}'),
                      icon:
                          d.pending ? Icons.hourglass_top : Icons.check_circle,
                      text: d.pending ? 'Pending' : 'Active',
                      tone: d.pending ? _Tone.warning : _Tone.success),
                ]),
          ),
          const SizedBox(height: AppSpacing.s),
          Fact('Device key id', d.kid, mono: true, key: Key('kid-${d.kid}')),
          if (d.pending)
            Padding(
              padding: const EdgeInsets.only(top: AppSpacing.xs),
              child: Text(
                  'Pending. It becomes active ${whenText(d.activeAt)}. '
                  'Until then it approves nothing.',
                  key: Key('pending-${d.kid}'),
                  style: theme.textTheme.bodyMedium),
            ),
          if (d.current) ...[
            const SizedBox(height: AppSpacing.s),
            _KeyLevel(
                key: Key('level-${d.kid}'), kid: d.kid, keys: services.keys),
          ] else
            Padding(
              padding: const EdgeInsets.only(top: AppSpacing.s),
              child: Text(
                  'Where its key is kept is known only on the device itself.',
                  style: theme.textTheme.bodySmall
                      ?.copyWith(color: theme.colorScheme.onSurfaceVariant)),
            ),
          if (d.pending) ...[
            const SizedBox(height: AppSpacing.m),
            OutlinedButton(
              key: Key('cancel-${d.kid}'),
              onPressed: busy ? null : () => _confirmCancel(context),
              child: const Text('Cancel enrollment'),
            ),
          ],
        ]),
      ),
    );
  }
}

/// Where this device's key lives, asked of the keystore once.
class _KeyLevel extends StatefulWidget {
  const _KeyLevel({super.key, required this.kid, required this.keys});

  final String kid;
  final DeviceKeyStore keys;

  @override
  State<_KeyLevel> createState() => _KeyLevelState();
}

class _KeyLevelState extends State<_KeyLevel> {
  late final Future<DeviceKey?> _key = widget.keys.loadApproval();

  @override
  Widget build(BuildContext context) {
    return FutureBuilder<DeviceKey?>(
      future: _key,
      builder: (context, snapshot) {
        final key = snapshot.data;
        if (key != null) return KeyProtectionCard(level: key.securityLevel);
        if (snapshot.connectionState != ConnectionState.done) {
          return const SizedBox.shrink();
        }
        // Nothing to read (the key is gone, or the keystore would not answer): said, not hidden.
        return const StatusBanner(
          severity: StatusSeverity.warning,
          title: 'Key protection unknown',
          message: 'This device\'s keystore did not say where its key is kept.',
        );
      },
    );
  }
}

enum _Tone { primary, success, warning }

/// A small label with an icon and a word.
class _Badge extends StatelessWidget {
  const _Badge(
      {super.key, required this.icon, required this.text, required this.tone});

  final IconData icon;
  final String text;
  final _Tone tone;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final colors = context.statusColors;
    final (background, foreground) = switch (tone) {
      _Tone.primary => (
          theme.colorScheme.primaryContainer,
          theme.colorScheme.onPrimaryContainer
        ),
      _Tone.success => (colors.success, colors.onSuccess),
      _Tone.warning => (colors.warning, colors.onWarning),
    };
    return DecoratedBox(
      decoration: BoxDecoration(
        color: background,
        borderRadius: BorderRadius.circular(AppRadius.l),
      ),
      child: Padding(
        padding: const EdgeInsets.symmetric(
            horizontal: AppSpacing.s + 2, vertical: AppSpacing.xs),
        child: Row(mainAxisSize: MainAxisSize.min, children: [
          ExcludeSemantics(child: Icon(icon, size: 16, color: foreground)),
          const SizedBox(width: AppSpacing.xs),
          Flexible(
            child: Text(text,
                maxLines: 2,
                overflow: TextOverflow.ellipsis,
                style: theme.textTheme.labelMedium
                    ?.copyWith(color: foreground, fontWeight: FontWeight.w700)),
          ),
        ]),
      ),
    );
  }
}
