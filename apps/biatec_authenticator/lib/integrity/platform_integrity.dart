import 'dart:io';

import 'package:flutter/foundation.dart';
import 'package:flutter/services.dart';

import 'integrity.dart';
import 'integrity_checker.dart';

/// The build's expected release certificate, `--dart-define=RELEASE_CERT_SHA256=<sha-256 of the release signing certificate>`. A release build without it is not trusted (see `SigningCertificateDetector`).
const String expectedReleaseCertSha256 =
    String.fromEnvironment('RELEASE_CERT_SHA256');

/// The real environment: files and `/proc` through `dart:io`, the rest from the native side over `io.biatec.authenticator/integrity` (`property`, `debuggerAttached`, `classPresent`, `loadedImages`, `isSimulator`, `signingDigest`).
class PlatformIntegrityEnvironment implements IntegrityEnvironment {
  PlatformIntegrityEnvironment(
      {MethodChannel channel =
          const MethodChannel('io.biatec.authenticator/integrity'),
      IntegrityPlatform? platform})
      : _channel = channel,
        platform = platform ??
            (defaultTargetPlatform == TargetPlatform.iOS
                ? IntegrityPlatform.ios
                : IntegrityPlatform.android);

  final MethodChannel _channel;

  @override
  final IntegrityPlatform platform;

  @override
  bool fileExists(String path) {
    try {
      return FileSystemEntity.typeSync(path) != FileSystemEntityType.notFound;
    } on Object {
      return false;
    }
  }

  @override
  bool canWriteTo(String directory) {
    try {
      final probe = File('$directory/.biatec_write_probe');
      probe.writeAsStringSync('x', flush: true);
      probe.deleteSync();
      return true;
    } on Object {
      return false;
    }
  }

  @override
  String? readFile(String path) {
    try {
      return File(path).readAsStringSync();
    } on Object {
      return null;
    }
  }

  /// An error from the native side is not caught: the checker counts a detector that cannot get its answer as a hit, so a hooked or broken channel does not read as a healthy phone.
  Future<T?> _ask<T>(String method, [Map<String, Object?>? args]) =>
      _channel.invokeMethod<T>(method, args);

  @override
  Future<String?> property(String key) =>
      _ask<String>('property', {'key': key});

  @override
  Future<bool> debuggerAttached() async =>
      await _ask<bool>('debuggerAttached') ?? true;

  @override
  Future<bool> classPresent(String name) async =>
      await _ask<bool>('classPresent', {'name': name}) ?? true;

  @override
  Future<List<String>> loadedImages() async =>
      (await _ask<List<Object?>>('loadedImages'))
          ?.whereType<String>()
          .toList() ??
      const [];

  @override
  Future<bool> isSimulator() async => await _ask<bool>('isSimulator') ?? false;

  @override
  Future<String?> signingDigest() => _ask<String>('signingDigest');
}

/// The checker for this build. Only Android and iOS are checked: the app is a phone app, and the same code on a desktop (a developer's Windows preview) has no indicators to look for.
IntegrityChecker platformIntegrityChecker() {
  final supported = defaultTargetPlatform == TargetPlatform.android ||
      defaultTargetPlatform == TargetPlatform.iOS;
  if (!supported) return const _NotApplicable();
  return DeviceIntegrityChecker(
    environment: PlatformIntegrityEnvironment(),
    expectedSigningDigest: expectedReleaseCertSha256,
  );
}

class _NotApplicable implements IntegrityChecker {
  const _NotApplicable();

  @override
  Future<IntegrityResult> check() async => const IntegrityResult.healthy();
}
