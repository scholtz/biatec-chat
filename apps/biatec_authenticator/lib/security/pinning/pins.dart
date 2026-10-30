import 'package:flutter/foundation.dart';

import '../../config.dart';
import 'pin_policy.dart';
import 'spki_pin.dart';

/// The public keys of Biatec OIDC's servers, per host, as `sha256/BASE64` of the SubjectPublicKeyInfo. Changing one is a release: see
/// `docs/AUTHENTICATOR-PINNING.md` for where they come from and how to rotate them.
///
/// Production (`oidc.biatec.io`) has none yet: it does not serve a certificate that is not a placeholder. A production build passes its pins with
/// `--dart-define=OIDC_PINS=sha256/...,sha256/...`, and without them the app refuses to start.
const Map<String, List<String>> defaultOidcPins = {
  // Leaf key of the stage certificate (Let's Encrypt, issued 2026-09-26). It stays the same across renewals only while the certificate is
  // requested with `privateKey.rotationPolicy: Never`; a second, pre-generated backup key has to be added here before that is changed.
  'stage.oidc.biatec.io': [
    'sha256/z8N92PhmB13Pcufe5VrnS1/Z14cMYcjKqhaQYdVJ1IM='
  ],
};

const String _definedPins = String.fromEnvironment('OIDC_PINS');

/// The pin policy for a build that talks to [config]'s Biatec OIDC.
///
/// - The mock and a plain-http developer URL need none.
/// - Any other host needs pins, from `--dart-define=OIDC_PINS=...` or [defaultOidcPins]; there is no unpinned HTTPS.
/// - A release build on iOS or Android needs at least two (the current key and a backup), so one lost or rotated key cannot lock every user out.
///
/// Throws [ConfigException], and the app then says why it cannot start.
PinPolicy pinPolicyFor(
  AppConfig config, {
  String? definedPins,
  bool? release,
  bool? mobile,
  DateTime Function()? clock,
}) {
  final uri = Uri.parse(config.oidcBaseUrl);
  if (uri.scheme != 'https') return PinPolicy.none();
  final host = uri.host.toLowerCase();
  final isRelease = release ?? !kDebugMode;
  final isMobile = mobile ??
      (defaultTargetPlatform == TargetPlatform.android ||
          defaultTargetPlatform == TargetPlatform.iOS);

  final defined = (definedPins ?? _definedPins).trim();
  final List<String> raw;
  if (defined.isNotEmpty) {
    raw = defined.split(RegExp(r'[,\s]+')).where((p) => p.isNotEmpty).toList();
  } else {
    raw = defaultOidcPins[host] ?? const [];
  }
  final pins = <String>{};
  for (final p in raw) {
    final parsed = SpkiPin.parse(p);
    if (parsed == null) {
      throw const ConfigException(
          'OIDC_PINS has a value that is not a pin (sha256/ and 32 bytes in Base64).');
    }
    pins.add(parsed);
  }
  if (pins.isEmpty) {
    throw ConfigException('There are no certificate pins for $host. Pass them '
        'with --dart-define=OIDC_PINS=sha256/...,sha256/... (see docs/AUTHENTICATOR-PINNING.md).');
  }
  if (isRelease && isMobile && pins.length < 2) {
    throw ConfigException('A release build needs at least two certificate '
        'pins for $host: the current key and a backup.');
  }
  return PinPolicy(pins: {host: pins}, clock: clock);
}
