import 'package:flutter/foundation.dart' show kReleaseMode;
import 'package:flutter/material.dart';

import 'approvals/approval_services.dart';
import 'codes/codes_services.dart';
import 'config.dart';
import 'enrollment/enrollment_controller.dart';
// Imported so the Android service's entry point is part of the program (it is run by name from StayConnectedService.kt).
// ignore: unused_import
import 'channel/background_main.dart';
import 'channel/channel_wiring.dart';
import 'channel/local_notifier.dart';
import 'channel/stay_connected.dart';
import 'push/apple_push.dart';
import 'push/device_event_controller.dart';
import 'push/device_event_source.dart';
import 'integrity/integrity_checker.dart';
import 'integrity/platform_integrity.dart';
import 'lock/lock_controller.dart';
import 'security/biometric_policy.dart';
import 'security/pinning/pinned_http.dart';
import 'security/pinning/pins.dart';
import 'security/secure_storage.dart';
import 'security/settings_controller.dart';
import 'settings/theme_controller.dart';
import 'shell/app_gate.dart';
import 'shell/shell_controller.dart';
import 'theme.dart';
import 'update/update_guard.dart';

Future<void> main() async {
  WidgetsFlutterBinding.ensureInitialized();
  // Items stored before the keychain accessibility was set are rewritten once so they stay on this device (BCHAT-009).
  await rewriteWithDeviceBoundAccessibility()
      .timeout(const Duration(seconds: 3), onTimeout: () => 0);
  final AppConfig config;
  try {
    config = AppConfig.fromEnvironment();
    // Every HTTPS connection to Biatec OIDC is checked against pinned public keys; with none for the host, the app does not start.
    PinnedHttp.configure(pinPolicyFor(config));
  } on ConfigException catch (e) {
    // Never start talking to an unsafe URL, and say why instead of showing a blank screen.
    runApp(ConfigErrorApp(message: e.message));
    return;
  }
  // The saved appearance is read before the first frame, so a saved Dark is not painted Light first. A slow keystore does not hold the app.
  final theme = ThemeController();
  await theme.load().timeout(const Duration(seconds: 1), onTimeout: () {});
  // The lowest build Biatec OIDC asked for on an earlier run: an old build stays closed when the app restarts, also offline.
  await MinimumBuildMonitor.instance
      .load()
      .timeout(const Duration(seconds: 1), onTimeout: () {});
  // The lock decides when the secure area opens; every fresh biometric (an approval, a setting) goes through it. Its delay is read before the first frame.
  final lock = LockController(
    authenticator: PlatformBiometricAuthenticator(),
    integrity: IntegrityGuard(platformIntegrityChecker()),
    // A release build fails closed on a compromised phone; a debug build only warns, so it can run on an emulator or under a debugger.
    failClosed: kReleaseMode,
    updates: UpdateGuard.forApp(),
  );
  await lock.load().timeout(const Duration(seconds: 1), onTimeout: () {});
  final approvals =
      ApprovalServices.fromConfig(config, gate: LockBiometricGate(lock));
  final messenger = GlobalKey<ScaffoldMessengerState>();
  final shell = ShellController();
  final notifier = LocalDeviceNotifier();
  // Against a Biatec OIDC without the device channel (config.deviceChannel is off) nothing connects, the service is never started and the setting is not shown.
  final StayConnectedController? stayConnected = config.deviceChannel
      ? StayConnectedController(
          platform: MethodChannelStayConnected(
              notificationPermission: () async {
            await notifier.requestPermission();
          }),
          enrollment: approvals.enrollment,
        )
      : null;
  // The device channel: a SignalR connection to Biatec OIDC while the app is in front. The Android service (if the user turned it on) keeps one while the app is closed.
  final DeviceEventSource channel = config.deviceChannel
      ? buildDeviceChannel(
          config: config,
          client: approvals.client,
          enrollment: approvals.enrollment,
          wake: changesOf(approvals.enrollment),
        )
      : IdleEventSource();
  final events = DeviceEventController(
    enrollment: approvals.enrollment,
    client: approvals.client,
    notifier: notifier,
    isForeground: () => shell.foreground.value,
    onRequestClosed: (_) => shell.requestsChanged.value++,
    onDevicesChanged: () => shell.reloads[AppTab.devices]!.value++,
    onSettingsChanged: () => shell.reloads[AppTab.settings]!.value++,
    // The optional Apple wake: nothing registers unless the build says so.
    apple: apnsEnabled ? MethodChannelApplePush() : null,
  );
  runApp(AuthenticatorApp(
      config: config,
      approvals: approvals,
      theme: theme,
      lock: lock,
      push: events,
      stayConnected: stayConnected,
      shell: shell,
      messengerKey: messenger));
  // Everything below starts after the first frame: the UI never waits for it, and a tapped notification that launched the app finds a mounted navigator.
  await WidgetsBinding.instance.endOfFrame;
  // Each step is on its own: a plugin that fails on some platform (notifications on desktop, say) must not stop the channel or the setting.
  try {
    await notifier.initialize(onTap: shell.showApprovals);
  } on Object {
    // Nothing is logged (the app never logs); the rest of the start goes on.
  }
  // The channel's events are not shown by the OS while the app is open, so say it here.
  events.requestArrived.listen((_) => showRequestArrived(messenger, shell));
  try {
    await events.start(channel);
  } on Object {
    // Nothing is logged (the app never logs); the rest of the start goes on.
  }
  try {
    await stayConnected?.load();
  } on Object {
    // Nothing is logged (the app never logs); the rest of the start goes on.
  }
}

/// In-app notice for a request that arrived over the channel while the app is open. It names no detail:
/// the list is fetched from Biatec OIDC when opened.
void showRequestArrived(
    GlobalKey<ScaffoldMessengerState> messenger, ShellController shell) {
  messenger.currentState
    ?..hideCurrentSnackBar()
    ..showSnackBar(SnackBar(
      content: const Text('New approval request'),
      action: SnackBarAction(
          label: 'Open',
          // Back to the list if a card is open, else show the list.
          onPressed: shell.showApprovals),
    ));
}

class AuthenticatorApp extends StatefulWidget {
  const AuthenticatorApp({
    super.key,
    required this.config,
    this.approvals,
    this.settings,
    this.push,
    this.stayConnected,
    this.shell,
    this.enrollment,
    this.navigatorKey,
    this.messengerKey,
    this.theme,
    this.lock,
    this.codes,
  });

  final AppConfig config;

  /// The user's system, light or dark choice; made (and saved on this phone) when not given.
  final ThemeController? theme;

  /// Opens and locks the secure area; made (with the platform's strong-biometric check) when not given.
  final LockController? lock;

  /// The vault, clipboard and clock of the Codes tab; built on the platform pieces when not given.
  final CodesServices? codes;

  /// Built from [config] when not given.
  final ApprovalServices? approvals;

  /// Built from [config] and the approval services when not given.
  final SettingsServices? settings;

  /// Null when the device channel is not wired (tests); the pending list then polls only.
  final DeviceEventController? push;

  /// The Android "Stay connected for approvals" setting; null when not wired (tests).
  final StayConnectedController? stayConnected;

  /// Created (and disposed) by the app when not given.
  final ShellController? shell;

  /// Injected in tests; built from the approval services and [config] otherwise.
  final EnrollmentController? enrollment;
  final GlobalKey<NavigatorState>? navigatorKey;
  final GlobalKey<ScaffoldMessengerState>? messengerKey;

  @override
  State<AuthenticatorApp> createState() => _AuthenticatorAppState();
}

class _AuthenticatorAppState extends State<AuthenticatorApp> {
  late final LockController _lock = widget.lock ??
      LockController(
        authenticator: PlatformBiometricAuthenticator(),
        integrity: IntegrityGuard(platformIntegrityChecker()),
        failClosed: kReleaseMode,
        updates: UpdateGuard.forApp(),
      );
  late final ApprovalServices _approvals = widget.approvals ??
      ApprovalServices.fromConfig(widget.config,
          gate: LockBiometricGate(_lock));
  late final SettingsServices _settings =
      widget.settings ?? SettingsServices.fromConfig(widget.config, _approvals);
  late final CodesServices _codes =
      widget.codes ?? CodesServices.platform(_lock);
  late final ShellController _shell = widget.shell ?? ShellController();
  late final ThemeController _theme = widget.theme ?? ThemeController();

  @override
  void initState() {
    super.initState();
    // A controller handed in was loaded by the caller (main reads it before the first frame).
    if (widget.theme == null) _theme.load();
  }

  @override
  void dispose() {
    if (widget.shell == null) _shell.dispose();
    if (widget.theme == null) _theme.dispose();
    if (widget.lock == null) _lock.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final push = widget.push;
    return ListenableBuilder(
      listenable: _theme,
      builder: (context, _) => MaterialApp(
        navigatorKey: widget.navigatorKey,
        scaffoldMessengerKey: widget.messengerKey,
        title: 'Biatec Authenticator',
        theme: buildTheme(Brightness.light),
        darkTheme: buildTheme(Brightness.dark),
        themeMode: _theme.mode,
        builder: (context, child) => ThemeScope(
            controller: _theme,
            child: LockScope(
                lock: _lock, child: largeTypeBuilder(context, child))),
        home: AppGate(
          config: widget.config,
          approvals: _approvals,
          settings: _settings,
          codes: _codes,
          shell: _shell,
          lock: _lock,
          push: push,
          stayConnected: widget.stayConnected,
          enrollment: widget.enrollment,
        ),
      ),
    );
  }
}

/// Shown instead of the app when the build's Biatec OIDC URL is not acceptable (see [AppConfig.resolve]).
class ConfigErrorApp extends StatelessWidget {
  const ConfigErrorApp({super.key, required this.message});

  final String message;

  @override
  Widget build(BuildContext context) => MaterialApp(
        title: 'Biatec Authenticator',
        theme: buildTheme(Brightness.light),
        darkTheme: buildTheme(Brightness.dark),
        home: Scaffold(
          body: SafeArea(
            child: Padding(
              padding: const EdgeInsets.all(24),
              child: Column(
                mainAxisAlignment: MainAxisAlignment.center,
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Text('This build cannot start',
                      key: const Key('config-error-title'),
                      style: Theme.of(context).textTheme.headlineSmall),
                  const SizedBox(height: 12),
                  Text(message, key: const Key('config-error-message')),
                ],
              ),
            ),
          ),
        ),
      );
}
