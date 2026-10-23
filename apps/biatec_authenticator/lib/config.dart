import 'package:flutter/foundation.dart';

/// The Biatec OIDC URL is not usable: not https (outside local mock use), or not a plain base URL.
class ConfigException implements Exception {
  const ConfigException(this.message);
  final String message;

  @override
  String toString() => 'ConfigException: $message';
}

/// Build-time configuration.
class AppConfig {
  const AppConfig(
      {required this.oidcBaseUrl,
      required this.isMock,
      this.settingsApi = false});

  /// Default points at the mock Biatec OIDC. 10.0.2.2 is the host machine
  /// seen from the Android emulator.
  static const String mockOidcBaseUrl = 'http://10.0.2.2:5080/mock-oidc';

  static const String _definedOidcBaseUrl =
      String.fromEnvironment('OIDC_BASE_URL');

  static const bool _definedSettingsApi =
      bool.fromEnvironment('CONSENT_SETTINGS_API');

  /// Base URL of Biatec OIDC (mock unless overridden with --dart-define).
  final String oidcBaseUrl;

  /// True when [oidcBaseUrl] is the mock.
  final bool isMock;

  /// Whether the device list and consent settings API exists at
  /// [oidcBaseUrl]. Real Biatec OIDC has none yet (required changes, rows 21
  /// and 22), so this is on for the mock only until
  /// `--dart-define=CONSENT_SETTINGS_API=true` says otherwise.
  final bool settingsApi;

  factory AppConfig.fromEnvironment() =>
      AppConfig.resolve(_definedOidcBaseUrl, settingsApi: _definedSettingsApi);

  /// Hosts that mean "this machine" for a developer: plain http is acceptable to them in a build that is not a release build.
  static const _localHosts = {'localhost', '127.0.0.1', '::1', '10.0.2.2'};

  factory AppConfig.resolve(String defined,
      {bool settingsApi = false, bool? release}) {
    final url = defined.trim().replaceAll(RegExp(r'/+$'), '');
    if (url.isEmpty) {
      return const AppConfig(
          oidcBaseUrl: mockOidcBaseUrl, isMock: true, settingsApi: true);
    }
    final isMock = url == mockOidcBaseUrl;
    if (!isMock) validateUrl(url, release: release ?? kReleaseMode);
    return AppConfig(
        oidcBaseUrl: url, isMock: isMock, settingsApi: isMock || settingsApi);
  }

  /// Device tokens, binding codes and receipts travel to this URL, so it must be https. The one exception is local development
  /// (loopback, `localhost`, the Android emulator's host alias) in a build that is not a release build. Throws [ConfigException].
  @visibleForTesting
  static void validateUrl(String url, {required bool release}) {
    final uri = Uri.tryParse(url);
    if (uri == null || !uri.hasAuthority || uri.host.isEmpty) {
      throw const ConfigException('OIDC_BASE_URL is not a valid URL.');
    }
    if (uri.userInfo.isNotEmpty || uri.hasQuery || uri.hasFragment) {
      throw const ConfigException(
          'OIDC_BASE_URL must be a plain base URL (no credentials, query or fragment).');
    }
    if (uri.scheme == 'https') return;
    if (uri.scheme == 'http' && !release && _localHosts.contains(uri.host)) {
      return;
    }
    throw ConfigException(uri.scheme == 'http'
        ? 'OIDC_BASE_URL must be https (plain http is only allowed for localhost in a non-release build).'
        : 'OIDC_BASE_URL must be an https URL.');
  }
}
