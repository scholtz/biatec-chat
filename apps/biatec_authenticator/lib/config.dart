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
      this.settingsApi = false,
      this.deviceChannel = false,
      this.twoKeys = false});

  /// Default points at the mock Biatec OIDC. 10.0.2.2 is the host machine
  /// seen from the Android emulator.
  static const String mockOidcBaseUrl = 'http://10.0.2.2:5080/mock-oidc';

  /// Default for the Windows desktop build when no `OIDC_BASE_URL` is defined.
  static const String stageOidcBaseUrl = 'https://stage.oidc.biatec.io';

  static const String _definedOidcBaseUrl =
      String.fromEnvironment('OIDC_BASE_URL');

  static const bool _definedDeviceChannel = bool.fromEnvironment('DEVICE_CHANNEL');

  static const bool _definedTwoKeys = bool.fromEnvironment('TWO_KEY_DEVICE');

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

  /// Whether Biatec OIDC has the device channel (`POST /devices/hub-ticket` and the hub, required changes row 24). Real Biatec OIDC has none yet, so
  /// this is on for the mock only until `--dart-define=DEVICE_CHANNEL=true` says otherwise. Off: no ticket is asked for, the Android service is
  /// never started and the "Stay connected" setting is not shown; the app polls.
  final bool deviceChannel;

  /// Whether Biatec OIDC has the two device keys (protocol 1.1, D9): `POST /devices` with the second key and the enrollment proofs, `POST /devices/challenge`, the
  /// presence key for device proofs. Real Biatec OIDC has none yet, so this is on for the mock only until `--dart-define=TWO_KEY_DEVICE=true` says otherwise. Off, the
  /// app enrolls ONE key (the one that needs no prompt), asks for a server challenge only when the platform attestation needs one, and an approval is gated by the app's biometric check
  /// as before T-241 (the keystore binding of the approval key is not used until the server side exists).
  final bool twoKeys;

  factory AppConfig.fromEnvironment() =>
      AppConfig.resolve(_definedOidcBaseUrl,
          settingsApi: _definedSettingsApi,
          deviceChannel: _definedDeviceChannel,
          twoKeys: _definedTwoKeys);

  /// Hosts that mean "this machine" for a developer: plain http is acceptable to them in a build that is not a release build.
  static const localHosts = {'localhost', '127.0.0.1', '::1', '10.0.2.2'};

  /// [release] is true for anything that is not a debug build (release and profile builds): those only accept https, and have no mock default.
  factory AppConfig.resolve(String defined,
      {bool settingsApi = false,
      bool deviceChannel = false,
      bool twoKeys = false,
      bool? release,
      bool? windows}) {
    final isRelease = release ?? !kDebugMode;
    final isWindows = windows ?? defaultTargetPlatform == TargetPlatform.windows;
    var url = defined.trim().replaceAll(RegExp(r'/+$'), '');
    // A plain `flutter build windows` is a release build and cannot read a defines file, so the desktop build
    // defaults to stage Biatec OIDC. Mobile release builds still need an explicit URL.
    if (url.isEmpty && isRelease && isWindows) url = stageOidcBaseUrl;
    if (url.isEmpty) {
      if (isRelease) {
        throw const ConfigException(
            'OIDC_BASE_URL is required in a release build (there is no mock default).');
      }
      return const AppConfig(
          oidcBaseUrl: mockOidcBaseUrl,
          isMock: true,
          settingsApi: true,
          deviceChannel: true,
          twoKeys: true);
    }
    // The mock is the same endpoint however the URL is spelled (scheme and host are case-insensitive).
    final isMock = Uri.tryParse(url)?.toString() == mockOidcBaseUrl;
    if (isMock && isRelease) {
      throw const ConfigException(
          'The mock Biatec OIDC is not available in a release build.');
    }
    if (!isMock) validateUrl(url, release: isRelease);
    return AppConfig(
        oidcBaseUrl: isMock ? mockOidcBaseUrl : url,
        isMock: isMock,
        settingsApi: isMock || settingsApi,
        deviceChannel: isMock || deviceChannel,
        twoKeys: isMock || twoKeys);
  }

  /// Device tokens, binding codes and receipts travel to this URL, so it must be https. The one exception is local development
  /// (loopback, `localhost`, the Android emulator's host alias) in a debug build. Throws [ConfigException].
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
    if (uri.scheme == 'http' && !release && localHosts.contains(uri.host)) {
      return;
    }
    throw ConfigException(uri.scheme == 'http'
        ? 'OIDC_BASE_URL must be https (plain http is only allowed for localhost in a debug build).'
        : 'OIDC_BASE_URL must be an https URL.');
  }
}
