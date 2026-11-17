import 'dart:io';

import 'dart:ui';

import '../config.dart';
import 'settings_texts.dart';

/// The app version shown in Settings. Keep it equal to `version` in pubspec.yaml (a test checks), or pass `--dart-define=APP_VERSION=...` from the build.
const String appVersion =
    String.fromEnvironment('APP_VERSION', defaultValue: '0.1.0+1');

/// What kind of Biatec OIDC the build talks to, in words for the user. Stage is a host label of its own (`stage.` or `staging.`, not a name that merely contains it); a local or LAN address is a test server; anything else is live.
///
/// In the language of [locale] (the phone's when null).
String endpointKind(AppConfig config, [Locale? locale]) {
  final t = SettingsTexts.of(locale);
  if (config.isMock) return t.text('endpointMock');
  final host = Uri.tryParse(config.oidcBaseUrl)?.host.toLowerCase() ?? '';
  final labels = host.split('.');
  if (labels
      .any((l) => l == 'stage' || l == 'staging' || l.startsWith('stage-'))) {
    return t.text('endpointStage');
  }
  if (host == 'localhost' ||
      host.endsWith('.local') ||
      InternetAddress.tryParse(host) != null) {
    return t.text('endpointLocal');
  }
  return t.text('endpointLive');
}
