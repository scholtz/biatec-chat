import 'package:flutter/material.dart';

import 'approvals/approval_services.dart';
import 'config.dart';
import 'enrollment/enrollment_controller.dart';
import 'push/fcm_push_transport.dart';
import 'push/push_controller.dart';
import 'security/pinning/pinned_http.dart';
import 'security/pinning/pins.dart';
import 'security/secure_storage.dart';
import 'security/settings_controller.dart';
import 'shell/app_gate.dart';
import 'shell/shell_controller.dart';
import 'theme.dart';

Future<void> main() async {
  WidgetsFlutterBinding.ensureInitialized();
  // Items stored before the keychain accessibility was set are rewritten once so they stay on this device (BCHAT-009).
  await rewriteWithDeviceBoundAccessibility().timeout(const Duration(seconds: 3), onTimeout: () => 0);
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
  final approvals = ApprovalServices.fromConfig(config);
  final firebase = FirebasePushConfig.fromEnvironment();
  if (firebase == null) {
    runApp(AuthenticatorApp(config: config, approvals: approvals));
    return;
  }
  final messenger = GlobalKey<ScaffoldMessengerState>();
  final shell = ShellController();
  final push = PushController(
    enrollment: approvals.enrollment,
    client: approvals.client,
    onOpenRequest: (id) => shell.openApproval(approvals, id),
  );
  runApp(AuthenticatorApp(
      config: config,
      approvals: approvals,
      push: push,
      shell: shell,
      messengerKey: messenger));
  // Firebase starts after the first frame: the UI never waits for it, and a
  // tapped notification that launched the app finds a mounted navigator.
  await WidgetsBinding.instance.endOfFrame;
  final transport = await FcmPushTransport.create(firebase);
  if (transport == null) return;
  // Coming back to the app retries a registration that failed earlier.
  AppLifecycleListener(onResume: push.registerIfEnrolled);
  // The OS shows nothing for a push while the app is open, so say it here.
  push.requestArrived.listen((_) => showRequestArrived(messenger, shell));
  await push.start(transport);
}

/// In-app notice for a push received in the foreground. It names no detail:
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
    this.shell,
    this.enrollment,
    this.navigatorKey,
    this.messengerKey,
  });

  final AppConfig config;

  /// Built from [config] when not given.
  final ApprovalServices? approvals;

  /// Built from [config] and the approval services when not given.
  final SettingsServices? settings;

  /// Null when push is not configured; the pending list then polls only.
  final PushController? push;

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
  late final ApprovalServices _approvals =
      widget.approvals ?? ApprovalServices.fromConfig(widget.config);
  late final SettingsServices _settings =
      widget.settings ?? SettingsServices.fromConfig(widget.config, _approvals);
  late final ShellController _shell = widget.shell ?? ShellController();

  @override
  void dispose() {
    if (widget.shell == null) _shell.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final push = widget.push;
    return MaterialApp(
      navigatorKey: widget.navigatorKey,
      scaffoldMessengerKey: widget.messengerKey,
      navigatorObservers: [if (push != null) push],
      title: 'Biatec Authenticator',
      theme: buildTheme(Brightness.light),
      darkTheme: buildTheme(Brightness.dark),
      builder: largeTypeBuilder,
      home: AppGate(
        config: widget.config,
        approvals: _approvals,
        settings: _settings,
        shell: _shell,
        push: push,
        enrollment: widget.enrollment,
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
