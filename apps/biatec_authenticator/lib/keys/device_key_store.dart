import 'package:flutter/services.dart';

import 'key_security_level.dart';

/// Public half of the device key, as the JWK the enrollment call sends.
class DeviceKey {
  const DeviceKey({
    required this.kid,
    required this.x,
    required this.y,
    required this.securityLevel,
  });

  /// Stable identifier of the key (the keystore alias). The alias is random
  /// per install, so the kid is unique per device.
  final String kid;

  /// Base64url P-256 coordinates.
  final String x;
  final String y;

  /// Where the keystore says the private key lives (BCHAT-032).
  final KeySecurityLevel securityLevel;

  Map<String, String> toJwk() => {'kty': 'EC', 'crv': 'P-256', 'x': x, 'y': y};
}

/// The hardware-backed device key (Secure Enclave on iOS, StrongBox on
/// Android).
///
/// The private key is generated inside the keystore and is never readable:
/// the interface has no way to export it, only to sign with it. Native
/// implementations must create it non-exportable (iOS
/// `kSecAttrTokenIDSecureEnclave`, Android `KeyGenParameterSpec`, StrongBox
/// when the device has it and accepts the key, otherwise the TEE). The level
/// the key got is reported in [DeviceKey.securityLevel]; enrollment refuses a
/// key that is not in hardware.
abstract class DeviceKeyStore {
  /// Creates the key pair, or returns the existing one for [alias].
  Future<DeviceKey> generate(String alias);

  /// The existing key, or null when none was generated.
  Future<DeviceKey?> load(String alias);

  /// Signs [data] (ES256, raw r||s) with the private key inside the keystore.
  Future<Uint8List> sign(String alias, Uint8List data);

  /// Removes the key, for example after a failed enrollment.
  Future<void> delete(String alias);
}

/// Talks to the native keystore code over a platform channel.
///
/// The native side lives in the platform folders generated with
/// `flutter create` (see README) and must implement the methods `generate`,
/// `load`, `sign` and `delete`, each taking an `alias` argument and returning
/// `{x, y, securityLevel}` (coordinates base64url, level as in
/// [KeySecurityLevel.wire]) for `generate` and `load`.
class PlatformDeviceKeyStore implements DeviceKeyStore {
  const PlatformDeviceKeyStore(
      [this._channel =
          const MethodChannel('io.biatec.authenticator/keystore')]);

  final MethodChannel _channel;

  DeviceKey _parse(Object? raw, String alias) {
    try {
      final map = (raw as Map).cast<String, Object?>();
      return DeviceKey(
        kid: alias,
        x: map['x'] as String,
        y: map['y'] as String,
        securityLevel: KeySecurityLevel.fromWire(map['securityLevel']),
      );
    } on Object {
      throw const FormatException('The keystore returned malformed key data.');
    }
  }

  @override
  Future<DeviceKey> generate(String alias) async =>
      _parse(await _channel.invokeMethod('generate', {'alias': alias}), alias);

  @override
  Future<DeviceKey?> load(String alias) async {
    final raw = await _channel.invokeMethod('load', {'alias': alias});
    return raw == null ? null : _parse(raw, alias);
  }

  @override
  Future<Uint8List> sign(String alias, Uint8List data) async {
    final raw = await _channel
        .invokeMethod<Uint8List>('sign', {'alias': alias, 'data': data});
    return raw!;
  }

  @override
  Future<void> delete(String alias) =>
      _channel.invokeMethod('delete', {'alias': alias});
}
