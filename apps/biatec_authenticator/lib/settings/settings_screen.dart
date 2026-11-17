import 'package:material_ui/material_ui.dart';

import '../channel/stay_connected.dart';
import '../clients/clients_screen.dart';
import '../clients/legacy_clients_screen.dart';
import '../clients/trust_strings.dart';
import '../clients/trust_widgets.dart';
import '../config.dart';
import '../enrollment/enrollment_strings.dart';
import '../keys/device_key_store.dart';
import '../keys/key_security_level.dart';
import '../keys/prompt_free_key.dart';
import '../privacy/phone_eraser.dart';
import '../privacy/privacy_screen.dart';
import '../lock/lock_controller.dart';
import '../security/settings_controller.dart';
import '../security/security_texts.dart';
import '../security/settings_frame.dart';
import '../shell/tab_reload.dart';
import '../theme.dart';
import '../ui/ui.dart';
import 'app_info.dart';
import 'channel_strings.dart';
import 'settings_texts.dart';
import 'theme_controller.dart';
import 'threshold_screen.dart';

/// The Settings tab: Security, Appearance and About, each a labelled group.
///
/// Appearance and About are always there. The security group needs the consent settings from Biatec OIDC, so it says when they are loading, not available or offline (with a retry) instead of taking the whole screen with it.
class SettingsScreen extends StatefulWidget {
  const SettingsScreen(
      {super.key,
      required this.services,
      required this.config,
      this.controller,
      this.stayConnected,
      this.eraser});

  final SettingsServices services;
  final AppConfig config;

  /// Shared with the other settings tabs (see [SettingsFrame.controller]); made (and disposed) here when null.
  final SettingsController? controller;

  /// The Android "Stay connected for approvals" setting; the row is shown only where it can work (Android).
  final StayConnectedController? stayConnected;

  /// Removes everything from the phone (Privacy screen); the Privacy screen still shows without it.
  final PhoneEraser? eraser;

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
    final t = SettingsTexts.of(Localizations.maybeLocaleOf(context));
    return Scaffold(
      appBar: AppBar(title: Text(t.text('title'))),
      body: ListenableBuilder(
        listenable: _controller,
        builder: (context, _) => ListView(
          children: [
            // A failed load shows its reason in the group itself, once.
            ...settingsStatus(_controller,
                showError: _controller.phase != SettingsPhase.unavailable),
            _SectionHeader(t.text('security')),
            ..._security(context, _controller),
            if (widget.stayConnected?.supported ?? false) ...[
              _SectionHeader(t.text('notifications')),
              _StayConnectedTile(controller: widget.stayConnected!),
            ],
            if (theme != null) ...[
              _SectionHeader(t.text('appearance')),
              _AppearanceChoice(controller: theme),
            ],
            _SectionHeader(t.text('about')),
            ..._about(context),
            const SizedBox(height: AppSpacing.l),
          ],
        ),
      ),
    );
  }

  List<Widget> _security(BuildContext context, SettingsController c) {
    final lock = LockScope.maybeOf(context);
    final locale = Localizations.maybeLocaleOf(context);
    final t = SettingsTexts.of(locale);
    final f = SecurityTexts.of(locale);
    if (!widget.services.enabled) {
      return [
        Padding(
          padding: const EdgeInsets.symmetric(
              horizontal: AppSpacing.m, vertical: AppSpacing.s),
          child: StatusBanner(
            key: const Key('not-available'),
            severity: StatusSeverity.info,
            title: f.text('notAvailableYet'),
            message: f.text('notAvailableYetBody'),
          ),
        ),
      ];
    }
    switch (c.phase) {
      case SettingsPhase.loading:
        return [
          Padding(
            padding: const EdgeInsets.symmetric(
                horizontal: AppSpacing.m, vertical: AppSpacing.s),
            child: StatusBanner(
              key: const Key('loading'),
              severity: StatusSeverity.info,
              title: t.text('loadingTitle'),
              message: t.text('loadingBody'),
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
              title: c.offline
                  ? t.text('offlineTitle')
                  : f.text('notAvailable'),
              message: c.error ?? f.text('notAvailableBody'),
              actionLabel: f.text('tryAgain'),
              onAction: c.load,
            ),
          ),
        ];
      case SettingsPhase.ready:
        final s = c.settings!;
        final enabled = !c.busy;
        if (widget.services.trustSettings) return _trustSecurity(context, c, lock);
        return [
          // The biometric check is no longer a setting: the app opens and approves only with a fingerprint or face.
          _biometricTile(t),
          ListTile(
            key: const Key('threshold'),
            title: Text(t.text('thresholdTitle')),
            subtitle: Text(
                t.text('thresholdSubtitle', {'amount': s.thresholdEur})),
            trailing: TextButton(
              key: const Key('threshold-change'),
              onPressed: enabled
                  ? () async {
                      final v = await askEur(context,
                          title: t.text('thresholdTitle'),
                          initial: s.thresholdEur,
                          current: s.thresholdEur,
                          hint: f.text('lowerRaiseHint'));
                      if (v != null) await c.apply({'threshold_eur': v});
                    }
                  : null,
              child: Text(t.text('change')),
            ),
          ),
          SwitchListTile(
            key: const Key('block-unverified'),
            title: Text(t.text('blockUnverified')),
            subtitle: Text(t.text('blockUnverifiedHelp')),
            value: s.blockUnprovenClients,
            onChanged: enabled
                ? (v) => applyWithConfirm(
                    context, c, s, {'block_unverified_clients': v},
                    title: t.text('blockUnverifiedOffTitle'),
                    message: t.text('blockUnverifiedOffBody'),
                    confirmLabel: t.text('turnOff'))
                : null,
          ),
          if (lock != null) _LockDelayTile(lock: lock),
          // Inside this tab's own stack, so back returns here and the bar stays.
          ListTile(
            key: const Key('trusted-clients'),
            leading: const Icon(Icons.verified_user_outlined),
            title: Text(t.text('trustedClients')),
            subtitle: Text(t.text('trustedClientsHelp')),
            trailing: const Icon(Icons.chevron_right),
            onTap: () => Navigator.of(context).push(MaterialPageRoute<void>(
              settings: const RouteSettings(name: '/settings/clients'),
              builder: (_) => LegacyClientsScreen(
                  services: widget.services, controller: _controller),
            )),
          ),
        ];
    }
  }

  /// Security with the user-held trust settings (`AppConfig.trustSettings`): the threshold and the linked services are screens of their own, inside this tab's stack so back returns here and the bar stays.
  List<Widget> _trustSecurity(
      BuildContext context, SettingsController c, LockController? lock) {
    final locale = Localizations.maybeLocaleOf(context);
    final s = TrustStrings.of(locale);
    final settings = c.settings!;
    return [
      ...inactiveBanner(c),
      _biometricTile(SettingsTexts.of(locale)),
      ListTile(
        key: const Key('threshold'),
        leading: const Icon(Icons.tune),
        title: Text(s.t('thresholdTitle')),
        subtitle: Text(s.t('thresholdTileSubtitle',
            {'amount': thresholdText(settings.thresholdEur)})),
        trailing: const Icon(Icons.chevron_right),
        onTap: () => Navigator.of(context).push(MaterialPageRoute<void>(
          settings: const RouteSettings(name: '/settings/threshold'),
          builder: (_) =>
              ThresholdScreen(services: widget.services, controller: _controller),
        )),
      ),
      if (lock != null) _LockDelayTile(lock: lock),
      ListTile(
        key: const Key('linked-services'),
        leading: const Icon(Icons.link),
        title: Text(s.t('linkedServicesTile')),
        subtitle: Text(s.t('linkedServicesSubtitle')),
        trailing: const Icon(Icons.chevron_right),
        onTap: () => Navigator.of(context).push(MaterialPageRoute<void>(
          settings: const RouteSettings(name: '/settings/clients'),
          builder: (_) =>
              ClientsScreen(services: widget.services, controller: _controller),
        )),
      ),
    ];
  }

  /// The biometric check is no longer a setting: the app opens and approves only with a fingerprint or face.
  Widget _biometricTile(SettingsTexts t) => ListTile(
        key: const Key('biometric'),
        leading: const Icon(Icons.fingerprint),
        title: Text(t.text('biometricTitle')),
        subtitle: Text(t.text('biometricBody')),
      );

  List<Widget> _about(BuildContext context) {
    final locale = Localizations.maybeLocaleOf(context);
    final t = SettingsTexts.of(locale);
    return [
      ListTile(
        key: const Key('about-version'),
        leading: const Icon(Icons.info_outline),
        title: Text(t.text('version')),
        subtitle: const Text(appVersion),
      ),
      ListTile(
        key: const Key('oidc-server'),
        leading: const Icon(Icons.dns_outlined),
        title: Text(t.text('oidcServer')),
        subtitle: Text('${widget.config.oidcBaseUrl}\n'
            '${endpointKind(widget.config, locale)}'),
        isThreeLine: true,
      ),
      _DeviceKeyTile(services: widget.services),
      ListTile(
        key: const Key('about-privacy'),
        leading: const Icon(Icons.privacy_tip_outlined),
        title: Text(t.text('privacy')),
        subtitle: Text(t.text('privacyHelp')),
        trailing: const Icon(Icons.chevron_right),
        onTap: () => Navigator.of(context).push(MaterialPageRoute<void>(
            settings: const RouteSettings(name: '/settings/privacy'),
            builder: (_) => PrivacyScreen(eraser: widget.eraser))),
      ),
      ListTile(
        key: const Key('about-licenses'),
        leading: const Icon(Icons.description_outlined),
        title: Text(t.text('licenses')),
        subtitle: Text(t.text('licensesHelp')),
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

/// "Stay connected for approvals" (Android, default off): says what on and off mean and what it costs, and offers the battery exemption once, after the first time it is turned on.
class _StayConnectedTile extends StatelessWidget {
  const _StayConnectedTile({required this.controller});

  final StayConnectedController controller;

  Future<void> _toggle(BuildContext context, bool value) async {
    final strings = ChannelStrings.of(Localizations.maybeLocaleOf(context));
    await controller.setEnabled(value);
    if (!value || !context.mounted) return;
    if (!await controller.shouldOfferBatteryExemption()) return;
    if (!context.mounted) return;
    final open = await showDialog<bool>(
      context: context,
      builder: (context) => AlertDialog(
        key: const Key('battery-offer'),
        title: Text(strings.batteryTitle),
        content: Text(strings.batteryBody),
        actions: [
          TextButton(
              onPressed: () => Navigator.of(context).pop(false),
              child: Text(strings.notNow)),
          FilledButton(
              key: const Key('battery-open'),
              onPressed: () => Navigator.of(context).pop(true),
              child: Text(strings.batteryOpen)),
        ],
      ),
    );
    if (open ?? false) await controller.openBatterySettings();
  }

  @override
  Widget build(BuildContext context) {
    final strings = ChannelStrings.of(Localizations.maybeLocaleOf(context));
    return ListenableBuilder(
      listenable: controller,
      builder: (context, _) => SwitchListTile(
        key: const Key('stay-connected'),
        secondary: const Icon(Icons.notifications_active_outlined),
        title: Text(strings.stayTitle),
        subtitle: Text(
            '${controller.enabled ? strings.stayOn : strings.stayOff}'
            '\n${strings.stayBattery}'),
        isThreeLine: true,
        value: controller.enabled,
        onChanged: controller.loaded ? (v) => _toggle(context, v) : null,
      ),
    );
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

  /// The mode, the keys of its title and of its description ([SettingsTexts]), and its icon.
  static const _choices = [
    (ThemeMode.system, 'themeSystem', 'themeSystemHelp', Icons.brightness_auto),
    (ThemeMode.light, 'themeLight', 'themeLightHelp', Icons.light_mode_outlined),
    (ThemeMode.dark, 'themeDark', 'themeDarkHelp', Icons.dark_mode_outlined),
  ];

  @override
  Widget build(BuildContext context) {
    final t = SettingsTexts.of(Localizations.maybeLocaleOf(context));
    return Column(
      key: const Key('theme-mode'),
      children: [
        for (final (mode, title, description, icon) in _choices)
          Semantics(
            inMutuallyExclusiveGroup: true,
            child: ListTile(
              key: Key('theme-${mode.name}'),
              leading: Icon(icon),
              title: Text(t.text(title)),
              subtitle: Text(t.text(description)),
              selected: controller.mode == mode,
              trailing: controller.mode == mode
                  ? Icon(Icons.check, semanticLabel: t.text('chosen'))
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
    final key = widget.services.twoKeys
        ? await widget.services.keys.loadApproval()
        : await PromptFreeKey.of(record).load(widget.services.keys);
    return key != null && key.kid == record.kid ? key : null;
  }

  @override
  Widget build(BuildContext context) {
    return FutureBuilder<DeviceKey?>(
      future: _key,
      builder: (context, snapshot) {
        final key = snapshot.data;
        final locale = Localizations.maybeLocaleOf(context);
        final t = SettingsTexts.of(locale);
        final String text;
        if (snapshot.connectionState != ConnectionState.done) {
          text = t.text('keyChecking');
        } else if (snapshot.hasError) {
          text = t.text('keyNoAnswer');
        } else if (key == null) {
          text = t.text('keyNone');
        } else if (key.securityLevel == KeySecurityLevel.windowsAccount) {
          text = EnrollStrings.of(locale).softwareKeyShort;
        } else if (key.securityLevel.isHardware) {
          text = t.text('keyHardware', {'level': key.securityLevel.label});
        } else {
          text = t.text('keySoftware', {'level': key.securityLevel.label});
        }
        return ListTile(
          key: const Key('about-key'),
          leading: const Icon(Icons.vpn_key_outlined),
          title: Text(t.text('keyTitle')),
          subtitle: Text(text),
        );
      },
    );
  }
}

/// When the app locks after it was left. A change asks for a fresh biometric first.
class _LockDelayTile extends StatelessWidget {
  const _LockDelayTile({required this.lock});

  final LockController lock;

  Future<void> _choose(BuildContext context) async {
    final t = SettingsTexts.of(Localizations.maybeLocaleOf(context));
    final chosen = await showModalBottomSheet<LockDelay>(
      context: context,
      showDragHandle: true,
      isScrollControlled: true,
      builder: (context) => SafeArea(
        child: SingleChildScrollView(
          child: Column(
            mainAxisSize: MainAxisSize.min,
            children: [
              Padding(
                padding: const EdgeInsets.fromLTRB(
                    AppSpacing.l, 0, AppSpacing.l, AppSpacing.s),
                child: Semantics(
                  header: true,
                  child: Text(t.text('lockTitle'),
                      style: Theme.of(context).textTheme.titleLarge),
                ),
              ),
              for (final d in LockDelay.values)
                ListTile(
                  key: Key('delay-${d.name}'),
                  title: Text(d.label),
                  selected: d == lock.delay,
                  trailing: d == lock.delay
                      ? Icon(Icons.check, semanticLabel: t.text('chosen'))
                      : null,
                  onTap: () => Navigator.of(context).pop(d),
                ),
            ],
          ),
        ),
      ),
    );
    if (chosen == null || chosen == lock.delay || !context.mounted) return;
    final saved = await lock.setDelay(chosen);
    if (!saved && context.mounted) {
      ScaffoldMessenger.of(context)
          .showSnackBar(SnackBar(content: Text(t.text('lockFailed'))));
    }
  }

  @override
  Widget build(BuildContext context) {
    final t = SettingsTexts.of(Localizations.maybeLocaleOf(context));
    return ListenableBuilder(
      listenable: lock,
      builder: (context, _) => ListTile(
        key: const Key('lock-delay'),
        leading: const Icon(Icons.lock_clock),
        title: Text(t.text('lockTitle')),
        subtitle: Text(t.text('lockSubtitle', {'delay': lock.delay.label})),
        trailing: const Icon(Icons.chevron_right),
        onTap: () => _choose(context),
      ),
    );
  }
}
