import 'dart:io';

import '../config.dart';

/// The app version shown in Settings. Keep it equal to `version` in pubspec.yaml (a test checks), or pass `--dart-define=APP_VERSION=...` from the build.
const String appVersion =
    String.fromEnvironment('APP_VERSION', defaultValue: '0.1.0+1');

/// What kind of Biatec OIDC the build talks to, in words for the user. Stage is a host label of its own (`stage.` or `staging.`, not a name that merely contains it); a local or LAN address is a test server; anything else is live.
String endpointKind(AppConfig config) {
  if (config.isMock) return 'Mock server for testing. Nothing here is real.';
  final host = Uri.tryParse(config.oidcBaseUrl)?.host.toLowerCase() ?? '';
  final labels = host.split('.');
  if (labels
      .any((l) => l == 'stage' || l == 'staging' || l.startsWith('stage-'))) {
    return 'Stage server. For testing before release.';
  }
  if (host == 'localhost' ||
      host.endsWith('.local') ||
      InternetAddress.tryParse(host) != null) {
    return 'Test server on a local network.';
  }
  return 'Live server.';
}
