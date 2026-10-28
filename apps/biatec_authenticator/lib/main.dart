import 'package:flutter/material.dart';

import 'approvals/approval_screen.dart';
import 'approvals/approval_services.dart';
import 'approvals/history_screen.dart';
import 'approvals/pending_screen.dart';
import 'clients/clients_screen.dart';
import 'config.dart';
import 'devices/devices_screen.dart';
import 'enrollment/enrollment_screen.dart';
import 'push/fcm_push_transport.dart';
import 'push/push_controller.dart';
import 'screens/home_screen.dart';
import 'security/pinning/pinned_http.dart';
import 'security/pinning/pins.dart';
import 'security/secure_storage.dart';
import 'security/security_screen.dart';
import 'security/settings_controller.dart';
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
  final navigator = GlobalKey<NavigatorState>();
  final messenger = GlobalKey<ScaffoldMessengerState>();
  final push = PushController(
    enrollment: approvals.enrollment,
    client: approvals.client,
    onOpenRequest: (id) =>
        navigator.currentState?.push(ApprovalScreen.route(approvals, id)),
  );
  runApp(AuthenticatorApp(
      config: config,
      approvals: approvals,
      push: push,
      navigatorKey: navigator,
      messengerKey: messenger));
  // Firebase starts after the first frame: the UI never waits for it, and a
  // tapped notification that launched the app finds a mounted navigator.
  await WidgetsBinding.instance.endOfFrame;
  final transport = await FcmPushTransport.create(firebase);
  if (transport == null) return;
  // Coming back to the app retries a registration that failed earlier.
  AppLifecycleListener(onResume: push.registerIfEnrolled);
  // The OS shows nothing for a push while the app is open, so say it here.
  push.requestArrived.listen((_) => showRequestArrived(messenger, navigator));
  await push.start(transport);
}

/// In-app notice for a push received in the foreground. It names no detail:
/// the list is fetched from Biatec OIDC when opened.
void showRequestArrived(GlobalKey<ScaffoldMessengerState> messenger,
    GlobalKey<NavigatorState> navigator) {
  messenger.currentState
    ?..hideCurrentSnackBar()
    ..showSnackBar(SnackBar(
      content: const Text('New approval request'),
      action: SnackBarAction(
          label: 'Open',
          onPressed: () {
            // Back to the list if it is already open, else open it.
            final nav = navigator.currentState;
            if (nav == null) return;
            var found = false;
            nav.popUntil((r) =>
                (found = found || r.settings.name == '/pending') || r.isFirst);
            if (!found) nav.pushNamed('/pending');
          }),
    ));
}

class AuthenticatorApp extends StatelessWidget {
  AuthenticatorApp({
    Key? key,
    required AppConfig config,
    ApprovalServices? approvals,
    SettingsServices? settings,
    PushController? push,
    GlobalKey<NavigatorState>? navigatorKey,
    GlobalKey<ScaffoldMessengerState>? messengerKey,
  }) : this._(
          key,
          config,
          approvals ?? ApprovalServices.fromConfig(config),
          settings,
          push,
          navigatorKey,
          messengerKey,
        );

  AuthenticatorApp._(
    Key? key,
    this.config,
    this.approvals,
    SettingsServices? settings,
    this.push,
    this.navigatorKey,
    this.messengerKey,
  )   : settings = settings ?? SettingsServices.fromConfig(config, approvals),
        super(key: key);

  final AppConfig config;
  final ApprovalServices approvals;
  final SettingsServices settings;

  /// Null when push is not configured; the pending list then polls only.
  final PushController? push;
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
      initialRoute: '/',
      routes: {
        '/': (_) => HomeScreen(config: config),
        '/enrollment': (_) => EnrollmentScreen(config: config),
        '/pending': (_) => PendingScreen(
              services: approvals,
              pushes: push?.requestArrived,
            ),
        '/history': (_) => HistoryScreen(history: approvals.history),
        '/devices': (_) => DevicesScreen(services: settings),
        '/clients': (_) => ClientsScreen(services: settings),
        '/security': (_) => SecurityScreen(services: settings),
      },
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
