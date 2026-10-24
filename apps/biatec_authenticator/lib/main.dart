import 'package:flutter/material.dart';

import 'approvals/approval_services.dart';
import 'config.dart';
import 'enrollment/enrollment_controller.dart';
import 'push/fcm_push_transport.dart';
import 'push/push_controller.dart';
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

class AuthenticatorApp extends StatelessWidget {
  AuthenticatorApp({
    Key? key,
    required AppConfig config,
    ApprovalServices? approvals,
    SettingsServices? settings,
    PushController? push,
    ShellController? shell,
    EnrollmentController? enrollment,
    GlobalKey<NavigatorState>? navigatorKey,
    GlobalKey<ScaffoldMessengerState>? messengerKey,
  }) : this._(
          key,
          config,
          approvals ?? ApprovalServices.fromConfig(config),
          settings,
          push,
          shell ?? ShellController(),
          enrollment,
          navigatorKey,
          messengerKey,
        );

  AuthenticatorApp._(
    Key? key,
    this.config,
    this.approvals,
    SettingsServices? settings,
    this.push,
    this.shell,
    this.enrollment,
    this.navigatorKey,
    this.messengerKey,
  )   : settings = settings ?? SettingsServices.fromConfig(config, approvals),
        super(key: key);

  final AppConfig config;
  final ApprovalServices approvals;
  final SettingsServices settings;

  /// Null when push is not configured; the pending list then polls only.
  final PushController? push;
  final ShellController shell;

  /// Injected in tests; built from [approvals] and [config] otherwise.
  final EnrollmentController? enrollment;
  final GlobalKey<NavigatorState>? navigatorKey;
  final GlobalKey<ScaffoldMessengerState>? messengerKey;

  @override
  Widget build(BuildContext context) {
    final push = this.push;
    return MaterialApp(
      navigatorKey: navigatorKey,
      scaffoldMessengerKey: messengerKey,
      navigatorObservers: [if (push != null) push],
      title: 'Biatec Authenticator',
      theme: buildTheme(Brightness.light),
      darkTheme: buildTheme(Brightness.dark),
      builder: largeTypeBuilder,
      home: AppGate(
        config: config,
        approvals: approvals,
        settings: settings,
        shell: shell,
        push: push,
        enrollment: enrollment,
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
