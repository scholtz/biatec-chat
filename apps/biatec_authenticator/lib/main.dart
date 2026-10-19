import 'package:flutter/material.dart';

import 'approvals/approval_services.dart';
import 'approvals/history_screen.dart';
import 'approvals/pending_screen.dart';
import 'config.dart';
import 'enrollment/enrollment_screen.dart';
import 'routes.dart';
import 'screens/home_screen.dart';
import 'theme.dart';

void main() {
  runApp(AuthenticatorApp(config: AppConfig.fromEnvironment()));
}

class AuthenticatorApp extends StatelessWidget {
  AuthenticatorApp(
      {super.key, required this.config, ApprovalServices? approvals})
      : approvals = approvals ?? ApprovalServices.fromConfig(config);

  final AppConfig config;
  final ApprovalServices approvals;

  @override
  Widget build(BuildContext context) {
    return MaterialApp(
      title: 'Biatec Authenticator',
      theme: buildTheme(Brightness.light),
      darkTheme: buildTheme(Brightness.dark),
      builder: largeTypeBuilder,
      initialRoute: '/',
      routes: {
        '/': (_) => HomeScreen(config: config),
        for (final route in appRoutes) route.path: (_) => route.build(),
        '/enrollment': (_) => EnrollmentScreen(config: config),
        '/pending': (_) => PendingScreen(services: approvals),
        '/history': (_) => HistoryScreen(history: approvals.history),
      },
    );
  }
}
