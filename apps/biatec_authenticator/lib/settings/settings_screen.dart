import 'package:flutter/material.dart';

import '../clients/clients_screen.dart';
import '../config.dart';
import '../keys/device_key_store.dart';
import '../security/settings_controller.dart';
import '../security/settings_frame.dart';
import '../shell/tab_reload.dart';
import '../theme.dart';
import '../ui/ui.dart';
import 'app_info.dart';
import 'theme_controller.dart';

/// The Settings tab: Security, Appearance and About, each a labelled group.
///
/// Appearance and About are always there. The security group needs the consent settings from Biatec OIDC, so it says when they are loading, not available or offline (with a retry) instead of taking the whole screen with it.
class SettingsScreen extends StatefulWidget {
  const SettingsScreen(
      {super.key,
      required this.services,
      required this.config,
      this.controller});

  final SettingsServices services;
  final AppConfig config;

  /// Shared with the other settings tabs (see [SettingsFrame.controller]); made (and disposed) here when null.
  final SettingsController? controller;

  @override
  State<SettingsScreen> createState() => _SettingsScreenState();
}

class _SettingsScreenState extends State<SettingsScreen>
    with TabReloadListener {
  late final SettingsController _controller =
      widget.controller ?? widget.services.controller();

  @override
  void initState() {
    super.initState();
    // A shared controller another tab has loaded already is used as it is.
    if (widget.services.enabled && _controller.phase == SettingsPhase.loading) {
      _controller.load();
    }
  }

  @override
  void onReload() {
    if (widget.services.enabled) _controller.load(quiet: true);
  }

  @override
  void dispose() {
    if (widget.controller == null) _controller.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final theme = ThemeScope.maybeOf(context);
    return Scaffold(
      appBar: AppBar(title: const Text('Settings')),
      body: ListenableBuilder(
        listenable: _controller,
        builder: (context, _) => ListView(
          children: [
            // A failed load shows its reason in the group itself, once.
            ...settingsStatus(_controller,
                showError: _controller.phase != SettingsPhase.unavailable),
            const _SectionHeader('Security'),
            ..._security(context, _controller),
            if (theme != null) ...[
              const _SectionHeader('Appearance'),
              _AppearanceChoice(controller: theme),
            ],
            const _SectionHeader('About'),
            ..._about(context),
            const SizedBox(height: AppSpacing.l),
          ],
        ),
      ),
    );
  }

  List<Widget> _security(BuildContext context, SettingsController c) {
    if (!widget.services.enabled) {
      return const [
        Padding(
          padding: EdgeInsets.symmetric(
              horizontal: AppSpacing.m, vertical: AppSpacing.s),
          child: StatusBanner(
            key: Key('not-available'),
            severity: StatusSeverity.info,
            title: 'Not available yet',
            message: 'This Biatec OIDC does not offer device and settings '
                'management; it only runs against the mock for now.',
          ),
        ),
      ];
    }
    switch (c.phase) {
      case SettingsPhase.loading:
        return const [
          Padding(
            padding: EdgeInsets.symmetric(
                horizontal: AppSpacing.m, vertical: AppSpacing.s),
            child: StatusBanner(
              key: Key('loading'),
              severity: StatusSeverity.info,
              title: 'Loading',
              message: 'Getting your security settings.',
            ),
          ),
        ];
      case SettingsPhase.unavailable:
        return [
          Padding(
            padding: const EdgeInsets.symmetric(
                horizontal: AppSpacing.m, vertical: AppSpacing.s),
            child: StatusBanner(
              key: const Key('unavailable'),
              severity: StatusSeverity.danger,
              icon: c.offline ? Icons.cloud_off : Icons.error_outline,
              title: c.offline ? 'You are offline' : 'Not available',
              message: c.error ?? 'Not available.',
              actionLabel: 'Try again',
              onAction: c.load,
            ),
          ),
        ];
      case SettingsPhase.ready:
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
                ? (v) => applyWithConfirm(
                    context, c, s, {'biometric_required': v},
                    title: 'Turn off the biometric check?',
                    message: 'This device will approve without asking for your '
                        'fingerprint or face first.',
                    confirmLabel: 'Turn off')
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
                          current: s.thresholdEur,
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
                ? (v) => applyWithConfirm(
                    context, c, s, {'block_unverified_clients': v},
                    title: 'Let unverified clients ask?',
                    message: 'Applications whose name is not confirmed will be '
                        'able to ask for approvals.',
                    confirmLabel: 'Turn off')
                : null,
          ),
          // Inside this tab's own stack, so back returns here and the bar stays.
          ListTile(
            key: const Key('trusted-clients'),
            leading: const Icon(Icons.verified_user_outlined),
            title: const Text('Trusted clients and thresholds'),
            subtitle: const Text('Per-client limits, "always ask" and pauses.'),
            trailing: const Icon(Icons.chevron_right),
            onTap: () => Navigator.of(context).push(MaterialPageRoute<void>(
              settings: const RouteSettings(name: '/settings/clients'),
              builder: (_) => ClientsScreen(
                  services: widget.services, controller: _controller),
            )),
          ),
        ];
    }
  }

  List<Widget> _about(BuildContext context) {
    return [
      const ListTile(
        key: Key('about-version'),
        leading: Icon(Icons.info_outline),
        title: Text('Version'),
        subtitle: Text(appVersion),
      ),
      ListTile(
        key: const Key('oidc-server'),
        leading: const Icon(Icons.dns_outlined),
        title: const Text('Biatec OIDC server'),
        subtitle: Text('${widget.config.oidcBaseUrl}\n'
            '${endpointKind(widget.config)}'),
        isThreeLine: true,
      ),
      _DeviceKeyTile(services: widget.services),
      ListTile(
        key: const Key('about-licenses'),
        leading: const Icon(Icons.description_outlined),
        title: const Text('Open-source notices'),
        subtitle: const Text('The libraries this app is built with.'),
        trailing: const Icon(Icons.chevron_right),
        onTap: () => showLicensePage(
          context: context,
          applicationName: 'Biatec Authenticator',
          applicationVersion: appVersion,
        ),
      ),
    ];
  }
}

class _SectionHeader extends StatelessWidget {
  const _SectionHeader(this.title);

  final String title;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return Padding(
      padding: const EdgeInsets.fromLTRB(
          AppSpacing.m, AppSpacing.l, AppSpacing.m, AppSpacing.xs),
      child: Semantics(
        header: true,
        child: Text(title,
            style: theme.textTheme.titleSmall
                ?.copyWith(color: theme.colorScheme.primary)),
      ),
    );
  }
}

/// System, light or dark: one row each, a check as well as the highlight on the chosen one.
class _AppearanceChoice extends StatelessWidget {
  const _AppearanceChoice({required this.controller});

  final ThemeController controller;

  static const _choices = [
    (
      ThemeMode.system,
      'System',
      'Follows your phone\'s setting.',
      Icons.brightness_auto
    ),
    (ThemeMode.light, 'Light', 'Always light.', Icons.light_mode_outlined),
    (ThemeMode.dark, 'Dark', 'Always dark.', Icons.dark_mode_outlined),
  ];

  @override
  Widget build(BuildContext context) {
    return Column(
      key: const Key('theme-mode'),
      children: [
        for (final (mode, title, description, icon) in _choices)
          Semantics(
            inMutuallyExclusiveGroup: true,
            child: ListTile(
              key: Key('theme-${mode.name}'),
              leading: Icon(icon),
              title: Text(title),
              subtitle: Text(description),
              selected: controller.mode == mode,
              trailing: controller.mode == mode
                  ? const Icon(Icons.check, semanticLabel: 'Chosen')
                  : null,
              onTap: () => controller.select(mode),
            ),
          ),
      ],
    );
  }
}

/// Where this device's key lives, read from the keystore for the enrolled device.
class _DeviceKeyTile extends StatefulWidget {
  const _DeviceKeyTile({required this.services});

  final SettingsServices services;

  @override
  State<_DeviceKeyTile> createState() => _DeviceKeyTileState();
}

class _DeviceKeyTileState extends State<_DeviceKeyTile> {
  late final Future<DeviceKey?> _key = _load();

  Future<DeviceKey?> _load() async {
    final record = await widget.services.enrollment.read();
    if (record == null) return null;
    return widget.services.keys.load(record.kid);
  }

  @override
  Widget build(BuildContext context) {
    return FutureBuilder<DeviceKey?>(
      future: _key,
      builder: (context, snapshot) {
        final key = snapshot.data;
        final String text;
        if (snapshot.connectionState != ConnectionState.done) {
          text = 'Checking.';
        } else if (snapshot.hasError) {
          text =
              'The keystore did not answer. Close and open the app to try again.';
        } else if (key == null) {
          text = 'No key found for this device.';
        } else if (key.securityLevel.isHardware) {
          text = 'Kept in ${key.securityLevel.label}. It cannot be exported.';
        } else {
          text = 'Not in secure hardware (${key.securityLevel.label}).';
        }
        return ListTile(
          key: const Key('about-key'),
          leading: const Icon(Icons.vpn_key_outlined),
          title: const Text('Device key protection'),
          subtitle: Text(text),
        );
      },
    );
  }
}
