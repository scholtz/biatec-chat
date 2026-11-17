import 'package:material_ui/material_ui.dart';

import '../approvals/widgets/fact_row.dart';
import '../enrollment/enrollment_strings.dart';
import '../enrollment/pairing_code.dart';
import '../enrollment/widgets/key_protection_card.dart';
import '../keys/device_key_store.dart';
import '../keys/prompt_free_key.dart';
import '../security/settings_client.dart';
import '../security/settings_controller.dart';
import '../security/settings_frame.dart';
import '../theme.dart';
import '../ui/ui.dart';
import 'devices_texts.dart';

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
    final t = DevicesTexts.of(Localizations.maybeLocaleOf(context));
    return SettingsFrame(
      title: t.text('title'),
      services: services,
      controller: controller,
      builder: (context, c) {
        if (c.devices.isEmpty) {
          return [
            Padding(
              key: const Key('no-devices'),
              padding: const EdgeInsets.all(AppSpacing.l),
              child: Column(children: [
                const ExcludeSemantics(child: Icon(Icons.devices, size: 48)),
                const SizedBox(height: AppSpacing.m),
                Text(t.text('noDevices'), textAlign: TextAlign.center),
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
            Padding(
              padding: const EdgeInsets.all(AppSpacing.m),
              child: StatusBanner(
                key: const Key('devices-note'),
                severity: StatusSeverity.info,
                title: t.text('inUseTitle'),
                message: t.text('inUseBody'),
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
    final t = DevicesTexts.of(Localizations.maybeLocaleOf(context));
    final yes = await confirmSheet(
      context,
      title: t.text(here ? 'cancelHereTitle' : 'cancelOtherTitle'),
      message: t.text(here ? 'cancelHereBody' : 'cancelOtherBody'),
      warning: here ? t.text('cancelHereWarning') : null,
      confirmLabel: t.text('cancelEnrollment'),
      cancelLabel: t.text('keep'),
      confirmKey: const Key('confirm-cancel'),
      cancelKey: const Key('keep'),
      icon: Icons.phone_android,
    );
    if (yes) await controller.cancelDevice(device.kid);
  }

  Future<void> _confirmActivate(BuildContext context, String name) async {
    final strings = EnrollStrings.of(Localizations.maybeLocaleOf(context));
    final yes = await confirmSheet(
      context,
      title: strings.activateConfirmTitle,
      message: strings.activateConfirmMessage,
      confirmLabel: strings.activateConfirm,
      cancelLabel: strings.keep,
      confirmKey: const Key('confirm-activate'),
      cancelKey: const Key('keep-activate'),
      icon: Icons.phonelink_lock,
    );
    if (yes) await controller.activateDevice(device);
  }

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final d = device;
    final busy = controller.busy;
    final locale = Localizations.maybeLocaleOf(context);
    final strings = EnrollStrings.of(locale);
    final t = DevicesTexts.of(locale);
    // Another device's enrollment that waits for this one: its pairing code, computed here from the keys Biatec OIDC lists for it. Only when the activation flow exists (the server sends the pairing data) and this device can sign it.
    final code = d.pending && !d.current && services.twoKeys && services.activationByDevice
        ? SettingsController.pairingCodeOf(d)
        : null;
    final name = d.name ?? strings.newDeviceName;
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
                    _Badge(
                        key: const Key('badge-this-device'),
                        icon: Icons.smartphone,
                        text: t.text('thisDevice'),
                        tone: _Tone.primary),
                  _Badge(
                      key: Key('badge-status-${d.kid}'),
                      icon:
                          d.pending ? Icons.hourglass_top : Icons.check_circle,
                      text: t.text(d.pending ? 'pending' : 'active'),
                      tone: d.pending ? _Tone.warning : _Tone.success),
                ]),
          ),
          const SizedBox(height: AppSpacing.s),
          Fact(t.text('kid'), d.kid, mono: true, key: Key('kid-${d.kid}')),
          if (d.pending)
            Padding(
              padding: const EdgeInsets.only(top: AppSpacing.xs),
              child: Text(
                  t.text('pendingUntil', {'when': whenText(d.activeAt)}),
                  key: Key('pending-${d.kid}'),
                  style: theme.textTheme.bodyMedium),
            ),
          if (d.current) ...[
            const SizedBox(height: AppSpacing.s),
            _KeyLevel(
                key: Key('level-${d.kid}'),
                kid: d.kid,
                keys: services.keys,
                twoKeys: services.twoKeys),
          ] else
            Padding(
              padding: const EdgeInsets.only(top: AppSpacing.s),
              child: Text(t.text('keyElsewhere'),
                  style: theme.textTheme.bodySmall
                      ?.copyWith(color: theme.colorScheme.onSurfaceVariant)),
            ),
          if (code != null) ...[
            const SizedBox(height: AppSpacing.m),
            Semantics(
              header: true,
              child: Text(strings.activateTitle(name),
                  maxLines: 2,
                  overflow: TextOverflow.ellipsis,
                  key: Key('activate-title-${d.kid}'),
                  style: theme.textTheme.titleMedium),
            ),
            const SizedBox(height: AppSpacing.xs),
            Text(strings.activateCodeLabel, style: theme.textTheme.labelMedium),
            Semantics(
              label: code.split('').join(' '),
              child: ExcludeSemantics(
                child: Text(spacedPairingCode(code),
                    key: Key('pairing-code-${d.kid}'),
                    style: theme.textTheme.headlineMedium
                        ?.copyWith(fontFeatures: const [FontFeature.tabularFigures()])),
              ),
            ),
            const SizedBox(height: AppSpacing.xs),
            Text(strings.activateCompare, style: theme.textTheme.bodyMedium),
            const SizedBox(height: AppSpacing.m),
            FilledButton(
              key: Key('activate-${d.kid}'),
              onPressed: busy ? null : () => _confirmActivate(context, name),
              child: Text(strings.activateButton),
            ),
          ],
          if (d.pending) ...[
            const SizedBox(height: AppSpacing.m),
            OutlinedButton(
              key: Key('cancel-${d.kid}'),
              onPressed: busy ? null : () => _confirmCancel(context),
              child: Text(code != null
                  ? strings.cancelThisDevice
                  : t.text('cancelEnrollment')),
            ),
          ],
        ]),
      ),
    );
  }
}

/// Where this device's key lives, asked of the keystore once.
class _KeyLevel extends StatefulWidget {
  const _KeyLevel(
      {super.key,
      required this.kid,
      required this.keys,
      required this.twoKeys});

  final String kid;
  final bool twoKeys;
  final DeviceKeyStore keys;

  @override
  State<_KeyLevel> createState() => _KeyLevelState();
}

class _KeyLevelState extends State<_KeyLevel> {
  late final Future<DeviceKey?> _key = _load();

  Future<DeviceKey?> _load() async {
    // The key that IS the device key: the approval key in two-key mode, the prompt-free one (this build's or the old alias) otherwise. A key under another kid is not this device's.
    final key = widget.twoKeys
        ? await widget.keys.loadApproval()
        : await PromptFreeKey.forSingleKid(widget.kid).load(widget.keys);
    return key != null && key.kid == widget.kid ? key : null;
  }

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
        final t = DevicesTexts.of(Localizations.maybeLocaleOf(context));
        return StatusBanner(
          severity: StatusSeverity.warning,
          title: t.text('levelUnknownTitle'),
          message: t.text('levelUnknownBody'),
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
