import 'package:flutter/material.dart';

import 'approvals/approval_screen.dart';
import 'approvals/approval_services.dart';
import 'approvals/history_screen.dart';
import 'approvals/pending_screen.dart';
import 'config.dart';
import 'enrollment/enrollment_screen.dart';
import 'push/fcm_push_transport.dart';
import 'push/push_controller.dart';
import 'push/push_registration_client.dart';
import 'routes.dart';
import 'screens/home_screen.dart';
import 'theme.dart';

Future<void> main() async {
  WidgetsFlutterBinding.ensureInitialized();
  final config = AppConfig.fromEnvironment();
  final approvals = ApprovalServices.fromConfig(config);
  final navigator = GlobalKey<NavigatorState>();
  final transport =
      await FcmPushTransport.create(FirebasePushConfig.fromEnvironment());
  final push = transport == null
      ? null
      : PushController(
          transport: transport,
          enrollment: approvals.enrollment,
          client: PushRegistrationClient(baseUrl: config.oidcBaseUrl),
          onOpenRequest: (id) => openApprovalRequest(navigator, approvals, id),
        );
  runApp(AuthenticatorApp(
      config: config,
      approvals: approvals,
      push: push,
      navigatorKey: navigator));
  await push?.start();
}

/// Opens the approval screen for a tapped notification. The screen fetches
/// the request from Biatec OIDC itself.
void openApprovalRequest(GlobalKey<NavigatorState> navigator,
    ApprovalServices approvals, String authReqId) {
  navigator.currentState?.push(MaterialPageRoute<void>(
    builder: (_) => ApprovalScreen(services: approvals, authReqId: authReqId),
  ));
}

class AuthenticatorApp extends StatelessWidget {
  AuthenticatorApp(
      {super.key,
      required this.config,
      ApprovalServices? approvals,
      this.push,
      this.navigatorKey})
      : approvals = approvals ?? ApprovalServices.fromConfig(config);

  final AppConfig config;
  final ApprovalServices approvals;

  /// Null when push is not configured; the pending list then polls only.
  final PushController? push;
  final GlobalKey<NavigatorState>? navigatorKey;

  @override
  Widget build(BuildContext context) {
    final push = this.push;
    return MaterialApp(
      navigatorKey: navigatorKey,
      title: 'Biatec Authenticator',
      theme: buildTheme(Brightness.light),
      darkTheme: buildTheme(Brightness.dark),
      builder: largeTypeBuilder,
      initialRoute: '/',
      routes: {
        '/': (_) => HomeScreen(config: config),
        for (final route in appRoutes) route.path: (_) => route.build(),
        '/enrollment': (_) => EnrollmentScreen(config: config),
        '/pending': (_) => PendingScreen(
              services: approvals,
              // With push, polling is only the fallback for a lost push.
              pollEvery: push == null
                  ? const Duration(seconds: 5)
                  : const Duration(seconds: 30),
              pushes: push?.requestArrived,
              onShown: push?.registerIfEnrolled,
            ),
        '/history': (_) => HistoryScreen(history: approvals.history),
      },
    );
  }
}
