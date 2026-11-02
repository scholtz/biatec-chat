import '../config.dart';

/// The app version shown in Settings. Keep it equal to `version` in pubspec.yaml (a test checks), or pass `--dart-define=APP_VERSION=...` from the build.
const String appVersion =
    String.fromEnvironment('APP_VERSION', defaultValue: '0.1.0+1');

/// What kind of Biatec OIDC the build talks to, in words for the user.
String endpointKind(AppConfig config) {
  if (config.isMock) return 'Mock server for testing. Nothing here is real.';
  final host = Uri.tryParse(config.oidcBaseUrl)?.host.toLowerCase() ?? '';
  if (host.contains('stage') || host.contains('staging')) {
    return 'Stage server. For testing before release.';
  }
  return 'Live server.';
}
